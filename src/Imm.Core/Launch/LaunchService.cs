using System.Diagnostics;
using Imm.Core.Domain;
using Serilog;

namespace Imm.Core.Launch;

/// <summary>Resolves the engine strategy, builds the launch plan and starts the process.</summary>
public interface ILaunchService
{
    /// <summary>Launches the pack. Throws <see cref="InvalidOperationException"/> with a user-facing message on failure.</summary>
    Task<Process> LaunchAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default);
}

public sealed class LaunchService : ILaunchService
{
    private readonly IReadOnlyList<ILaunchStrategy> _strategies;
    private readonly ILogger _logger;

    public LaunchService(IEnumerable<ILaunchStrategy> strategies, ILogger logger)
    {
        _strategies = strategies.ToArray();
        _logger = logger.ForContext<LaunchService>();
    }

    public async Task<Process> LaunchAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default)
    {
        var strategy = _strategies.FirstOrDefault(s => s.Handles(manifest.Engine))
            ?? throw new InvalidOperationException($"Движок запуска «{manifest.Engine}» не поддерживается.");

        var plan = await strategy.BuildPlanAsync(manifest, packRoot, cancellationToken).ConfigureAwait(false);

        _logger.Information("Launching {Engine}: {Exe} {Args} (workdir {WorkDir})",
            manifest.Engine, plan.ExecutablePath, string.Join(' ', plan.Arguments), plan.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = true,
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Процесс не был запущен (система вернула null).");
    }
}

using System.Diagnostics;
using System.IO;
using Imm.Core.Domain;
using Imm.Core.Scanning;
using Serilog;
using Imm.Core.Localization;

namespace Imm.Core.Launch;

/// <summary>Resolves the engine strategy, builds the launch plan and starts the process.</summary>
public interface ILaunchService
{
    /// <summary>Launches the pack. Throws <see cref="InvalidOperationException"/> with a user-facing message on failure.</summary>
    Task<Process> LaunchAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts ModOrganizer.exe of the pack's instance without a game executable ("open MO2");
    /// elevated for the same reason the game launch is (SE loaders inherit the MO2 token).
    /// </summary>
    Task<Process> OpenModOrganizerAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default);
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
            ?? throw new InvalidOperationException(Loc.F("Err_EngineUnsupported", manifest.Engine));

        var plan = await strategy.BuildPlanAsync(manifest, packRoot, cancellationToken).ConfigureAwait(false);

        _logger.Information("Launching {Engine}: {Exe} {Args} (workdir {WorkDir})",
            manifest.Engine, plan.ExecutablePath, string.Join(' ', plan.Arguments), plan.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = true,
        };
        if (plan.Elevate)
        {
            startInfo.Verb = "runas"; // UAC prompt; user refusal surfaces as Win32Exception 1223
        }
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(Loc.T("Err_ProcessNotStarted"));
    }

    /// <inheritdoc />
    public Task<Process> OpenModOrganizerAsync(ModpackManifest manifest, string packRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var instance = Mo2Locator.ResolveInstance(packRoot, manifest.Mo2?.Directory)
            ?? throw new InvalidOperationException(
                Loc.T("Err_Mo2ExeMissing"));

        var mo2Exe = Path.Combine(instance, Markers.ModOrganizerExe);
        _logger.Information("Opening Mod Organizer without a game: {Exe}", mo2Exe);

        var startInfo = new ProcessStartInfo
        {
            FileName = mo2Exe,
            WorkingDirectory = instance,
            UseShellExecute = true,
            Verb = "runas", // UAC prompt; refusal surfaces as Win32Exception 1223
        };

        return Task.FromResult(Process.Start(startInfo)
            ?? throw new InvalidOperationException(Loc.T("Err_ProcessNotStarted")));
    }
}

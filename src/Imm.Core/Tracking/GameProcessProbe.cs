using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace Imm.Core.Tracking;

/// <summary>
/// Real Windows process probe. Every OS call is guarded: a process may vanish between enumeration
/// and inspection, and MainModule is unavailable across privilege boundaries (IMM.md: try/catch Win32Exception).
/// </summary>
public sealed class GameProcessProbe : IGameProcessProbe
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Func<int, bool> _isAlive;

    public GameProcessProbe(ILogger logger, TimeSpan? pollInterval = null, Func<int, bool>? isAlive = null)
    {
        _logger = logger.ForContext<GameProcessProbe>();
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _isAlive = isAlive ?? IsProcessAlive;
    }

    public IReadOnlyList<GameProcessSnapshot> Snapshot(string exeName)
    {
        var processName = Path.GetFileNameWithoutExtension(exeName);
        var snapshots = new List<GameProcessSnapshot>();

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    snapshots.Add(new GameProcessSnapshot(
                        process.Id,
                        exeName,
                        process.StartTime.ToUniversalTime(),
                        TryMainModule(process)));
                }
                catch (Exception exception)
                    when (exception is InvalidOperationException or Win32Exception or NotSupportedException or ArgumentException)
                {
                    // Процесс уже завершился или доступ закрыт — для трекинга он не годится.
                }
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Waits for the process to exit. A handle-based wait needs SYNCHRONIZE, which Windows refuses for
    /// a game started elevated (MO2 run as administrator) or protected by anti-cheat — a common case
    /// for modded setups, so a denial must fall back to polling the pid instead of ending tracking.
    /// </summary>
    public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (ArgumentException)
        {
            return; // уже завершился
        }
        catch (InvalidOperationException)
        {
            // Процесс исчез между получением и ожиданием — сессия всё равно закончилась.
            return;
        }
        catch (Win32Exception exception)
        {
            _logger.Information(
                "WaitForExit denied for pid={ProcessId} (win32 {Error}); falling back to pid polling.",
                processId, exception.NativeErrorCode);
        }
        catch (NotSupportedException)
        {
            // Удалённый процесс — тоже только опрос.
        }

        await PollUntilGoneAsync(processId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polling loop for the no-handle case; ends as soon as the pid disappears.</summary>
    internal async Task PollUntilGoneAsync(int processId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isAlive(processId))
            {
                return;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enumeration only: opening a per-process handle is exactly what access control denies, while the
    /// pid list comes from one system-wide query.
    /// </summary>
    internal static bool IsProcessAlive(int processId)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == processId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Посмотреть не смогли — не обрываем сессию из-за этого.
            return true;
        }
    }

    private static string? TryMainModule(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}

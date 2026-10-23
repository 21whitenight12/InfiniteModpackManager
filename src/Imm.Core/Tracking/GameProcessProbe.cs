using System.ComponentModel;
using System.Diagnostics;

namespace Imm.Core.Tracking;

/// <summary>
/// Real Windows process probe. Every OS call is guarded: a process may vanish between enumeration
/// and inspection, and MainModule is unavailable across privilege boundaries (IMM.md: try/catch Win32Exception).
/// </summary>
public sealed class GameProcessProbe : IGameProcessProbe
{
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

    public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return; // уже завершился
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Процесс исчез между получением и ожиданием — сессия всё равно закончилась.
            }
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

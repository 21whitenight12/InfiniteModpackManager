using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Imm.App.Services;

/// <summary>Brings an already running window to the foreground (MO2 must never be started twice).</summary>
public interface IWindowActivationService
{
    /// <summary>
    /// Activates the window of the running process whose image matches <paramref name="executablePath"/>.
    /// True also when the process exists but its window cannot be found — the caller must not start a duplicate.
    /// </summary>
    bool TryActivate(string executablePath);
}

public sealed class WindowActivationService : IWindowActivationService
{
    private const int SwRestore = 9;
    private const string Mo2ProcessName = "ModOrganizer";

    public bool TryActivate(string executablePath)
    {
        var processExists = false;

        foreach (var process in Process.GetProcessesByName(Mo2ProcessName))
        {
            using (process)
            {
                if (!IsSameInstance(process, executablePath))
                {
                    continue;
                }

                processExists = true;
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    continue; // свёрнут в трей: второй экземпляр MO2 запускать нельзя
                }

                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return true;
            }
        }

        return processExists;
    }

    /// <summary>Unknown module path counts as ours: starting a second MO2 on the same instance is worse.</summary>
    private static bool IsSameInstance(Process process, string executablePath)
    {
        try
        {
            var module = process.MainModule?.FileName;
            return module is null
                || string.Equals(Path.GetFullPath(module), Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Imm.Core.Logging;

/// <summary>
/// Serilog sink bridging the app log into the UI: bounded in-memory buffer of rendered
/// lines (Information+ by default) plus a live-append event. Thread-safe; subscribers
/// marshal notifications onto their own dispatcher.
/// </summary>
public sealed class UiLogSink : ILogEventSink, IDisposable
{
    /// <summary>Hard cap per the IMM.md log-flood lesson (no unbounded in-memory buffers).</summary>
    public const int DefaultCapacity = 4000;

    private readonly object _lock = new();
    private readonly Queue<string> _lines = new();
    private readonly MessageTemplateTextFormatter _formatter = new(
        "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

    private volatile bool _disposed;

    public UiLogSink(int capacity = DefaultCapacity)
    {
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Minimum level for UI copy; Verbose/Debug stay in the file log only.</summary>
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;

    /// <summary>Raised for every appended line (from whichever thread wrote the log entry).</summary>
    public event Action<string>? LineAppended;

    /// <summary>Snapshot of the current buffer (oldest first).</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _lines.ToArray();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent.Level < MinimumLevel)
        {
            return;
        }

        var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        var line = writer.ToString().TrimEnd('\r', '\n');

        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity)
            {
                _lines.Dequeue();
            }
        }

        LineAppended?.Invoke(line);
    }

    public void Dispose() => _disposed = true;
}

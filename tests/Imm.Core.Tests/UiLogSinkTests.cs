using Imm.Core.Logging;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Imm.Core.Tests;

public sealed class UiLogSinkTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-uilog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public void Sink_KeepsBoundedBuffer_AndRendersTemplate()
    {
        var sink = new UiLogSink(capacity: 3);
        var received = new List<string>();
        sink.LineAppended += line => received.Add(line);

        var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        for (var i = 1; i <= 5; i++)
        {
            logger.Information("Line {Number}", i);
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains("[INF]", snapshot[0]);
        Assert.Contains("Line 3", snapshot[0]);
        Assert.Contains("Line 5", snapshot[2]);
        Assert.Equal(5, received.Count);
    }

    [Fact]
    public void Sink_MinimumLevel_FiltersVerbose()
    {
        var sink = new UiLogSink { MinimumLevel = LogEventLevel.Information };
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Verbose("noise");
        logger.Information("signal");

        var snapshot = sink.Snapshot();
        Assert.Single(snapshot);
        Assert.Contains("signal", snapshot[0]);
    }

    [Fact]
    public void Sink_Exception_IncludesExceptionText()
    {
        var sink = new UiLogSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Error(new InvalidOperationException("boom"), "failed");

        Assert.Contains("boom", sink.Snapshot()[0]);
    }
}

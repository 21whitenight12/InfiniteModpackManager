using System.Diagnostics;
using Imm.Core.Tracking;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>
/// Waiting for a game process: a handle-based wait is denied for elevated/protected games
/// (Win32 5), so the probe must fall back to polling the pid and still end the session.
/// </summary>
public sealed class GameProcessProbeTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task PollUntilGone_EndsAsSoonAsThePidDisappears()
    {
        var calls = 0;
        var probe = new GameProcessProbe(Logger, TimeSpan.FromMilliseconds(10),
            _ => Interlocked.Increment(ref calls) <= 2);

        await probe.PollUntilGoneAsync(4242, CancellationToken.None);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PollUntilGone_HonoursCancellation()
    {
        var probe = new GameProcessProbe(Logger, TimeSpan.FromMilliseconds(10), _ => true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.PollUntilGoneAsync(4242, cancellation.Token));
    }

    [Fact]
    public void IsProcessAlive_SeesTheCurrentProcess()
    {
        Assert.True(GameProcessProbe.IsProcessAlive(Environment.ProcessId));
    }

    [Fact]
    public void IsProcessAlive_UnknownPid_IsDead()
    {
        Assert.False(GameProcessProbe.IsProcessAlive(int.MaxValue - 1));
    }

    [Fact]
    public async Task WaitForExit_RealProcess_ReturnsAfterItExits()
    {
        var probe = new GameProcessProbe(Logger, TimeSpan.FromMilliseconds(50));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 2 127.0.0.1 > nul",
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

        await probe.WaitForExitAsync(process.Id, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task WaitForExit_AlreadyGonePid_ReturnsImmediately()
    {
        var probe = new GameProcessProbe(Logger, TimeSpan.FromMilliseconds(50));

        await probe.WaitForExitAsync(int.MaxValue - 1, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
}

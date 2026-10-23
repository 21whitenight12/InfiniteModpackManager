using System.Diagnostics;
using Imm.Core.Domain;
using Imm.Core.Tracking;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>Session tracking rules (IMM.md «Трекинг сессии»): no foreign process, MO2 wait timeout.</summary>
public sealed class SessionTrackerTests
{
    private sealed class FakeProbe : IGameProcessProbe
    {
        public List<GameProcessSnapshot> Processes { get; } = [];
        public List<int> WaitedFor { get; } = [];

        public IReadOnlyList<GameProcessSnapshot> Snapshot(string exeName) =>
            Processes.Where(process => string.Equals(process.ExeName, exeName, StringComparison.OrdinalIgnoreCase)).ToArray();

        public Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
        {
            WaitedFor.Add(processId);
            return Task.CompletedTask;
        }
    }

    private static GameProcessSnapshot SnapshotOf(DateTime startUtc, string? mainModule = null, int id = 42) =>
        new(id, "Fallout4.exe", startUtc, mainModule);

    [Fact]
    public void IsOurs_ProcessStartedAfterLaunch_Matches()
    {
        var launch = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(SessionTracker.IsOurs(SnapshotOf(launch.AddSeconds(5)), launch, expectedGameDirectory: null));
    }

    [Fact]
    public void IsOurs_ProcessStartedBeforeLaunch_Rejected()
    {
        // Чужой Fallout 4, запущенный до нашей сборки, не должен засчитываться.
        var launch = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(SessionTracker.IsOurs(SnapshotOf(launch.AddMinutes(-30)), launch, expectedGameDirectory: null));
    }

    [Fact]
    public void IsOurs_ForeignGameFolder_Rejected()
    {
        var launch = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var candidate = SnapshotOf(launch.AddSeconds(5), @"C:\Other\Fallout4.exe");

        Assert.False(SessionTracker.IsOurs(candidate, launch, @"E:\Packs\Test\Stock Game"));
        Assert.True(SessionTracker.IsOurs(candidate, launch, @"C:\Other"));
    }

    [Fact]
    public void IsOurs_MainModuleUnavailable_FallsBackToStartTime()
    {
        var launch = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(SessionTracker.IsOurs(SnapshotOf(launch.AddSeconds(5)), launch, @"E:\Packs\Test\Stock Game"));
    }

    [Fact]
    public async Task TrackAsync_Mo2_GameNeverAppears_ReturnsNull()
    {
        var probe = new FakeProbe();
        var tracker = new SessionTracker(probe, Log.Logger,
            pollInterval: TimeSpan.FromMilliseconds(10),
            gameWaitTimeout: TimeSpan.FromMilliseconds(60));
        var manifest = new ModpackManifest { Engine = LaunchEngine.Mo2Standard, GameType = GameType.Fallout4 };

        var result = await tracker.TrackAsync(manifest, DateTime.UtcNow, launchedProcess: null, expectedGameDirectory: null);

        Assert.Null(result);
        Assert.Empty(probe.WaitedFor);
    }

    [Fact]
    public async Task TrackAsync_Mo2_WaitsForMatchingGameProcess()
    {
        var probe = new FakeProbe();
        var launch = DateTime.UtcNow.AddSeconds(-1);
        probe.Processes.Add(SnapshotOf(launch.AddMilliseconds(300), id: 777));
        var tracker = new SessionTracker(probe, Log.Logger,
            pollInterval: TimeSpan.FromMilliseconds(10),
            gameWaitTimeout: TimeSpan.FromSeconds(2));
        var manifest = new ModpackManifest { Engine = LaunchEngine.Mo2RootBuilder, GameType = GameType.Fallout4 };

        var result = await tracker.TrackAsync(manifest, launch, launchedProcess: null, expectedGameDirectory: null);

        Assert.NotNull(result);
        Assert.Equal(777, Assert.Single(probe.WaitedFor));
    }

    [Fact]
    public async Task TrackAsync_UnknownGameType_SkipsTracking()
    {
        var probe = new FakeProbe();
        var tracker = new SessionTracker(probe, Log.Logger);
        var manifest = new ModpackManifest { Engine = LaunchEngine.Mo2Standard, GameType = GameType.Unknown };

        Assert.Null(await tracker.TrackAsync(manifest, DateTime.UtcNow, null, null));
    }

    [Fact]
    public async Task TrackAsync_Direct_WaitsForLaunchedProcess()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var tracker = new SessionTracker(new FakeProbe(), Log.Logger);
        var manifest = new ModpackManifest { Engine = LaunchEngine.Direct, GameType = GameType.Fallout4 };

        var result = await tracker.TrackAsync(manifest, DateTime.UtcNow.AddMinutes(-3), process, null);

        Assert.NotNull(result);
        Assert.InRange(result!.PlaytimeMinutes, 3, 4);
    }
}

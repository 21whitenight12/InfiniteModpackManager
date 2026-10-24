using System.Diagnostics;
using Imm.Core.Domain;
using Imm.Core.Scanning;
using Serilog;

namespace Imm.Core.Tracking;

/// <summary>One running game process, as seen by a probe (MainModule may be unavailable — access denied).</summary>
public sealed record GameProcessSnapshot(int ProcessId, string ExeName, DateTime StartTimeUtc, string? MainModulePath);

/// <summary>Process source for the tracker; abstracted so the anti-collision rules stay unit-testable.</summary>
public interface IGameProcessProbe
{
    /// <summary>Current processes with this exe name; empty when the game is not running.</summary>
    IReadOnlyList<GameProcessSnapshot> Snapshot(string exeName);

    /// <summary>Completes when the process exits (or immediately when it is already gone).</summary>
    Task WaitForExitAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>Playtime result of one session (IMM.md «Трекинг сессии»).</summary>
public sealed record SessionResult(int PlaytimeMinutes, DateTime LastPlayedAtUtc);

public interface ISessionTracker
{
    /// <summary>
    /// Tracks a launch until the game exits. Direct: the launched process is the game.
    /// Mo2*: polls for the game process (1–2 s, 120 s timeout) and never binds to a foreign process
    /// of the same name — it must have started after our launch and/or live in the expected folder.
    /// </summary>
    Task<SessionResult?> TrackAsync(ModpackManifest manifest, DateTime launchStartedUtc,
        Process? launchedProcess, string? expectedGameDirectory, CancellationToken cancellationToken = default);
}

public sealed class SessionTracker : ISessionTracker
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DefaultGameWaitTimeout = TimeSpan.FromSeconds(120);

    private readonly IGameProcessProbe _probe;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _gameWaitTimeout;

    public SessionTracker(IGameProcessProbe probe, ILogger logger,
        TimeSpan? pollInterval = null, TimeSpan? gameWaitTimeout = null)
    {
        _probe = probe;
        _logger = logger.ForContext<SessionTracker>();
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _gameWaitTimeout = gameWaitTimeout ?? DefaultGameWaitTimeout;
    }

    /// <inheritdoc />
    public async Task<SessionResult?> TrackAsync(ModpackManifest manifest, DateTime launchStartedUtc,
        Process? launchedProcess, string? expectedGameDirectory, CancellationToken cancellationToken = default)
    {
        if (manifest.Engine == LaunchEngine.Direct)
        {
            if (launchedProcess is null)
            {
                return null;
            }

            await launchedProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return Result(launchStartedUtc);
        }

        var exeName = ModpackScanner.GameExeNameFor(manifest.GameType);
        if (exeName.Length == 0)
        {
            _logger.Warning("Session tracking skipped: game type {GameType} is unknown.", manifest.GameType);
            return null;
        }

        var deadline = DateTime.UtcNow + _gameWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var game = FindGame(exeName, launchStartedUtc, expectedGameDirectory);
            if (game is not null)
            {
                _logger.Information("Game process found for session tracking: pid={ProcessId} {Exe}", game.ProcessId, exeName);
                await FollowSessionAsync(game, exeName, launchStartedUtc, expectedGameDirectory, cancellationToken)
                    .ConfigureAwait(false);
                return Result(launchStartedUtc);
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        _logger.Warning("Game process {Exe} did not appear within {Timeout}s; session not counted.", exeName, _gameWaitTimeout.TotalSeconds);
        return null;
    }

    /// <summary>
    /// Follows the game until it is really gone. A loader may hand the session over to a new pid
    /// (F4SE/SKSE bootstrap, MO2 relaunch), so the end of the first process is not the end of the session.
    /// </summary>
    private async Task FollowSessionAsync(GameProcessSnapshot game, string exeName, DateTime launchStartedUtc,
        string? expectedGameDirectory, CancellationToken cancellationToken)
    {
        while (true)
        {
            await _probe.WaitForExitAsync(game.ProcessId, cancellationToken).ConfigureAwait(false);

            var next = FindGame(exeName, launchStartedUtc, expectedGameDirectory);
            if (next is null || next.ProcessId == game.ProcessId)
            {
                return;
            }

            _logger.Information("Game session continues in pid={ProcessId} {Exe}", next.ProcessId, exeName);
            game = next;
        }
    }

    private GameProcessSnapshot? FindGame(string exeName, DateTime launchStartedUtc, string? expectedGameDirectory)
    {
        foreach (var candidate in _probe.Snapshot(exeName))
        {
            if (IsOurs(candidate, launchStartedUtc, expectedGameDirectory))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// IMM.md anti-collision: the process must have started after our launch AND/OR live in the
    /// expected game folder. Both facts are used when available; a foreign session never matches.
    /// </summary>
    internal static bool IsOurs(GameProcessSnapshot candidate, DateTime launchStartedUtc, string? expectedGameDirectory)
    {
        if (candidate.StartTimeUtc < launchStartedUtc.AddSeconds(-2))
        {
            return false;
        }

        if (expectedGameDirectory is null || candidate.MainModulePath is null)
        {
            return true;
        }

        var actualDirectory = Path.GetDirectoryName(candidate.MainModulePath);
        return actualDirectory is null
            || string.Equals(
                Path.TrimEndingDirectorySeparator(actualDirectory),
                Path.TrimEndingDirectorySeparator(expectedGameDirectory),
                StringComparison.OrdinalIgnoreCase);
    }

    private static SessionResult Result(DateTime launchStartedUtc)
    {
        var played = DateTime.UtcNow - launchStartedUtc;
        return new SessionResult(Math.Max(0, (int)Math.Round(played.TotalMinutes)), DateTime.UtcNow);
    }
}

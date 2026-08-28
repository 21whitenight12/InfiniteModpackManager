using Imm.Core.Domain;

namespace Imm.Core.Scanning;

/// <summary>Facts collected from a modpack folder by the heuristic scanner (IMM.md Detection Markers).</summary>
public sealed record ScanResult
{
    public required string RootPath { get; init; }

    public GameType GameType { get; init; } = GameType.Unknown;

    public LaunchEngine Engine { get; init; } = LaunchEngine.Direct;

    public PackSource Source { get; init; } = PackSource.System;

    /// <summary>Absolute path of the system game install (Steam/GOG autodetect), when Source = System.</summary>
    public string? SystemGamePath { get; init; }

    /// <summary>MO2 profile name parsed from ModOrganizer.ini (portable instances only).</summary>
    public string? Mo2ProfileName { get; init; }

    /// <summary>Executable to launch: script extender loader if present, otherwise the game exe.</summary>
    public string? LaunchExecutable { get; init; }

    /// <summary>True when the folder does not look like a supported modpack at all.</summary>
    public bool LooksLikeModpack { get; init; }

    /// <summary>Ambiguities that must be surfaced in the UI instead of a silent guess (IMM.md).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

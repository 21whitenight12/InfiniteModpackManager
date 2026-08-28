namespace Imm.Core.Domain;

/// <summary>MO2 section of the manifest. Only portable MO2 instances are supported (IMM.md domain rule).</summary>
public sealed class Mo2Section
{
    public string ProfileName { get; set; } = string.Empty;
}

/// <summary>Launch section: executable name (SE loader preferred over bare game exe) and optional arguments.</summary>
public sealed class LaunchSection
{
    public string Executable { get; set; } = string.Empty;
    public string? Arguments { get; set; }
}

/// <summary>Detection result flags. User overrides survive re-scan (overriddenByUser, IMM.md Detection Markers).</summary>
public sealed class DetectionSection
{
    public bool OverriddenByUser { get; set; }
}

/// <summary>
/// Manifest stored as modpack.json in the root of every modpack (IMM.md Data Contracts).
/// Invariants: schemaVersion is the first field, id is a GUID, relative paths inside the manifest.
/// </summary>
public sealed class ModpackManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid Id { get; set; } = Guid.NewGuid();

    public GameType GameType { get; set; } = GameType.Unknown;

    public LaunchEngine Engine { get; set; } = LaunchEngine.Direct;

    public PackSource Source { get; set; } = PackSource.System;

    /// <summary>Absolute path of the system game install; only allowed absolute path in the manifest.</summary>
    public string? SystemGamePath { get; set; }

    public Mo2Section? Mo2 { get; set; }

    public LaunchSection Launch { get; set; } = new();

    public DetectionSection Detection { get; set; } = new();
}

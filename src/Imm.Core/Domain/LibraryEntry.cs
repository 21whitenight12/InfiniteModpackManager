namespace Imm.Core.Domain;

/// <summary>One modpack entry in the library index (IMM.md Data Contracts, LibraryEntry sketch).</summary>
public sealed class LibraryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path of the modpack root folder (the library-level field, not the manifest).</summary>
    public string RootPath { get; set; } = string.Empty;

    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    public int PlaytimeMinutes { get; set; }

    public DateTime? LastPlayedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>Root document of library.json (IMM.md Data Contracts). schemaVersion must stay the first field.</summary>
public sealed class LibraryIndex
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<LibraryEntry> Entries { get; set; } = [];
}

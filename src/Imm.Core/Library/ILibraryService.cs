using Imm.Core.Domain;
using Imm.Core.Scanning;

namespace Imm.Core.Library;

/// <summary>Library index operations (IMM.md MVP #1): import, load/save, user-override-safe re-scan.</summary>
public interface ILibraryService
{
    /// <summary>Loads library.json (empty index when the file does not exist yet).</summary>
    Task<LibraryIndex> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans the folder and adds it as a new library entry + writes modpack.json into the pack root.
    /// <paramref name="displayName"/> overrides the folder-name default; <paramref name="coverImagePath"/>
    /// (PNG/JPG/WebP) is copied into the pack root as cover.&lt;ext&gt;.
    /// </summary>
    Task<LibraryEntry> ImportAsync(string rootPath, string? displayName = null, string? coverImagePath = null,
        CancellationToken cancellationToken = default);

    /// <summary>Re-scans an imported pack; user overrides (Detection.OverriddenByUser) survive untouched.</summary>
    Task<ScanResult> RescanAsync(LibraryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Renames a library entry (library.json) and returns the updated entry.</summary>
    Task RenameAsync(LibraryEntry entry, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the image into the pack root as cover.&lt;ext&gt;, replacing any previous cover file.
    /// Throws for missing files and unsupported extensions (PNG/JPG/WebP only).
    /// </summary>
    Task<string> SetCoverAsync(string packRoot, string coverImagePath, CancellationToken cancellationToken = default);

    /// <summary>Loads modpack.json from the pack root; null when the pack has no manifest yet.</summary>
    Task<ModpackManifest?> LoadManifestAsync(LibraryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Persists the manifest as modpack.json in the pack root (atomic write).</summary>
    Task SaveManifestAsync(string packRoot, ModpackManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>Persists the library index (atomic write).</summary>
    Task SaveAsync(LibraryIndex index, CancellationToken cancellationToken = default);
}

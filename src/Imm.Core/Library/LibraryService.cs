using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Serilog;

namespace Imm.Core.Library;

/// <summary>
/// Owns library.json + modpack.json lifecycle (IMM.md Data Contracts).
/// Atomic writes everywhere; re-scan never overwrites user overrides.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    public const string ManifestFileName = "modpack.json";

    private readonly ImmPaths _paths;
    private readonly IModpackScanner _scanner;
    private readonly ILogger _logger;

    public LibraryService(ImmPaths paths, IModpackScanner scanner, ILogger logger)
    {
        _paths = paths;
        _scanner = scanner;
        _logger = logger.ForContext<LibraryService>();
    }

    public async Task<LibraryIndex> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LongPath.Normalize(_paths.LibraryFilePath)))
        {
            return new LibraryIndex();
        }

        try
        {
            return await AtomicFile.ReadJsonAsync<LibraryIndex>(_paths.LibraryFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            // Broken index must not brick the app: keep data on disk, start fresh, surface via log.
            _logger.Error(exception, "library.json is unreadable; starting with an empty index. File kept at {Path}",
                _paths.LibraryFilePath);
            return new LibraryIndex();
        }
    }

    public async Task<LibraryEntry> ImportAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var root = LongPath.Normalize(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Modpack folder does not exist: {root}");
        }

        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (index.Entries.Any(e => string.Equals(LongPath.Normalize(e.RootPath), root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Эта сборка уже есть в библиотеке.");
        }

        var scan = await _scanner.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        if (!scan.LooksLikeModpack)
        {
            throw new InvalidOperationException(
                "Папка не похожа на поддерживаемую сборку: не найдены exe игры или MO2.");
        }

        var entry = new LibraryEntry
        {
            Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
            RootPath = root,
            AddedAtUtc = DateTime.UtcNow,
        };
        index.Entries.Add(entry);
        await SaveAsync(index, cancellationToken).ConfigureAwait(false);

        var manifest = ManifestFromScan(entry.Id, scan);
        await SaveManifestAsync(entry.RootPath, manifest, cancellationToken).ConfigureAwait(false);

        _logger.Information("Imported modpack {Name} from {Root} (game={Game}, engine={Engine})",
            entry.Name, root, scan.GameType, scan.Engine);
        return entry;
    }

    public async Task<ScanResult> RescanAsync(LibraryEntry entry, CancellationToken cancellationToken = default)
    {
        var scan = await _scanner.ScanAsync(entry.RootPath, cancellationToken).ConfigureAwait(false);
        var manifestPath = ManifestPath(entry.RootPath);

        ModpackManifest manifest;
        if (File.Exists(LongPath.Normalize(manifestPath)))
        {
            manifest = await AtomicFile.ReadJsonAsync<ModpackManifest>(manifestPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            manifest = ManifestFromScan(entry.Id, scan);
        }

        if (manifest.Detection.OverriddenByUser)
        {
            // User overrides win over heuristics — detection section untouched (IMM.md Detection Markers).
            _logger.Information("Re-scan completed but user overrides kept for entry {Id}.", entry.Id);
            return scan;
        }

        ApplyScan(manifest, scan);
        await SaveManifestAsync(entry.RootPath, manifest, cancellationToken).ConfigureAwait(false);
        return scan;
    }

    public async Task<ModpackManifest?> LoadManifestAsync(LibraryEntry entry, CancellationToken cancellationToken = default)
    {
        var path = ManifestPath(entry.RootPath);
        if (!File.Exists(LongPath.Normalize(path)))
        {
            return null;
        }

        return await AtomicFile.ReadJsonAsync<ModpackManifest>(path, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveManifestAsync(string packRoot, ModpackManifest manifest, CancellationToken cancellationToken = default)
    {
        var path = ManifestPath(packRoot);
        _logger.Debug("Writing manifest {Path}", path);
        return AtomicFile.WriteJsonAsync(path, manifest, cancellationToken);
    }

    public Task SaveAsync(LibraryIndex index, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(_paths.LibraryFilePath, index, cancellationToken);

    public static string ManifestPath(string packRoot) => Path.Combine(packRoot, ManifestFileName);

    internal static ModpackManifest ManifestFromScan(Guid id, ScanResult scan) => new()
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id,
        GameType = scan.GameType,
        Engine = scan.Engine,
        Source = scan.Source,
        SystemGamePath = scan.SystemGamePath,
        Mo2 = scan.Mo2ProfileName is null ? null : new Mo2Section { ProfileName = scan.Mo2ProfileName },
        Launch = new LaunchSection { Executable = scan.LaunchExecutable ?? string.Empty },
        Detection = new DetectionSection(),
    };

    internal static void ApplyScan(ModpackManifest manifest, ScanResult scan)
    {
        manifest.GameType = scan.GameType;
        manifest.Engine = scan.Engine;
        manifest.Source = scan.Source;
        manifest.SystemGamePath = scan.SystemGamePath;
        manifest.Mo2 = scan.Mo2ProfileName is null ? null : new Mo2Section { ProfileName = scan.Mo2ProfileName };
        manifest.Launch = new LaunchSection { Executable = scan.LaunchExecutable ?? string.Empty };
    }
}

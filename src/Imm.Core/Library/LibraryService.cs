using System.Text.Json;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Serilog;
using Imm.Core.Localization;

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

    private static readonly string[] CoverFileNames = ["cover.png", "cover.jpg", "cover.jpeg", "cover.webp"];

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
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Broken index must not brick the app: keep data on disk, start fresh, surface via log.
            _logger.Error(exception, "library.json is unreadable; starting with an empty index. File kept at {Path}",
                _paths.LibraryFilePath);
            return new LibraryIndex();
        }
    }

    public async Task<LibraryEntry> ImportAsync(string rootPath, string? displayName = null, string? coverImagePath = null,
        CancellationToken cancellationToken = default)
    {
        var root = LongPath.Normalize(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Modpack folder does not exist: {root}");
        }

        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (index.Entries.Any(e => string.Equals(LongPath.Normalize(e.RootPath), root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(Loc.T("Err_PackAlreadyInLibrary"));
        }

        var scan = await _scanner.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        if (!scan.LooksLikeModpack)
        {
            throw new InvalidOperationException(
                Loc.T("Err_NotAModpack"));
        }

        var entry = new LibraryEntry
        {
            Name = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
                : displayName.Trim(),
            RootPath = root,
            AddedAtUtc = DateTime.UtcNow,
        };
        index.Entries.Add(entry);
        await SaveAsync(index, cancellationToken).ConfigureAwait(false);

        if (coverImagePath is not null)
        {
            await SetCoverAsync(root, coverImagePath, cancellationToken).ConfigureAwait(false);
        }

        var manifest = ManifestFromScan(entry.Id, scan);
        await SaveManifestAsync(entry.RootPath, manifest, cancellationToken).ConfigureAwait(false);

        _logger.Information("Imported modpack {Name} from {Root} (game={Game}, engine={Engine})",
            entry.Name, root, scan.GameType, scan.Engine);
        return entry;
    }

    /// <inheritdoc />
    public async Task RenameAsync(LibraryEntry entry, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(Loc.T("Err_EmptyName"));
        }

        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var indexed = index.Entries.FirstOrDefault(e => e.Id == entry.Id)
            ?? throw new InvalidOperationException(Loc.T("Err_PackNotInLibrary"));

        indexed.Name = trimmed;
        await SaveAsync(index, cancellationToken).ConfigureAwait(false);
        entry.Name = trimmed;
        _logger.Information("Renamed modpack {Id} to {Name}", entry.Id, trimmed);
    }

    /// <inheritdoc />
    public async Task<string> SetCoverAsync(string packRoot, string coverImagePath, CancellationToken cancellationToken = default)
    {
        var source = LongPath.Normalize(Path.GetFullPath(coverImagePath));
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(Loc.T("Err_CoverNotFound"), coverImagePath);
        }

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
        {
            throw new InvalidOperationException(Loc.T("Err_CoverFormat"));
        }

        var targetName = extension is ".jpg" or ".jpeg" ? "cover.jpg" : $"cover{extension}";
        var target = LongPath.Normalize(Path.Combine(packRoot, targetName));
        var temp = LongPath.Normalize(Path.Combine(packRoot, $".cover.tmp-{Guid.NewGuid():N}{extension}"));

        try
        {
            // File.Copy does not accept long paths; a raw Stream copy keeps the pack root untouched otherwise.
            await using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var targetStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
            }

            // Новая обложка целиком записана — только теперь её можно ставить на место.
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the temp file is inert garbage.
                }
            }
        }

        // Прежние обложки убираем ПОСЛЕ успешной записи новой: падение копирования
        // не должно оставлять сборку без обложки вообще.
        foreach (var previous in CoverFileNames)
        {
            var previousPath = LongPath.Normalize(Path.Combine(packRoot, previous));
            if (string.Equals(previousPath, target, StringComparison.OrdinalIgnoreCase) || !File.Exists(previousPath))
            {
                continue;
            }

            try
            {
                File.Delete(previousPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Прежняя обложка занята другим процессом — не валим операцию: новая уже на месте.
                _logger.Warning(exception, "Could not delete previous cover {Path}", previousPath);
            }
        }

        _logger.Information("Cover set for pack at {Root}: {Target}", packRoot, target);
        return target;
    }

    public async Task<ScanResult> RescanAsync(LibraryEntry entry, CancellationToken cancellationToken = default)
    {
        var scan = await _scanner.ScanAsync(entry.RootPath, cancellationToken).ConfigureAwait(false);
        var manifestPath = ManifestPath(entry.RootPath);

        ModpackManifest manifest;
        if (File.Exists(LongPath.Normalize(manifestPath)))
        {
            manifest = await ReadManifestForRescanAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            manifest = ManifestFromScan(entry.Id, scan);
        }

        if (manifest.Detection.OverriddenByUser)
        {
            // Детект игры принадлежит пользователю (IMM.md Detection Markers) — не трогаем его.
            // Но путь запускаемого файла остаётся фактом скана: иначе персистнутый путь лоадера
            // Root Builder (схема «найти в модах при скане») у таких сборок не обновляется никогда.
            var executable = scan.LaunchExecutable;
            if (!string.IsNullOrWhiteSpace(executable)
                && !string.Equals(manifest.Launch.Executable, executable, StringComparison.OrdinalIgnoreCase))
            {
                manifest.Launch.Executable = executable;
                await SaveManifestAsync(entry.RootPath, manifest, cancellationToken).ConfigureAwait(false);
            }

            _logger.Information("Re-scan completed; user overrides kept for entry {Id}.", entry.Id);
            return scan;
        }

        var userProfile = manifest.Detection.ProfileOverriddenByUser ? manifest.Mo2?.ProfileName : null;
        ApplyScan(manifest, scan);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // Manually picked profile survives re-scan.
            manifest.Mo2 ??= new Mo2Section();
            manifest.Mo2.ProfileName = userProfile;
        }

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

        try
        {
            return await AtomicFile.ReadJsonAsync<ModpackManifest>(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Один битый modpack.json не должен ронять загрузку всей библиотеки: файл оставляем
            // на диске, сборка показывается как не сканированная, диагностика — в логе.
            _logger.Error(exception, "modpack.json is unreadable for {Root}; file kept, pack treated as unscanned",
                entry.RootPath);
            return null;
        }
    }

    public Task SaveManifestAsync(string packRoot, ModpackManifest manifest, CancellationToken cancellationToken = default)
    {
        var path = ManifestPath(packRoot);
        _logger.Debug("Writing manifest {Path}", path);
        return AtomicFile.WriteJsonAsync(path, manifest, cancellationToken);
    }

    /// <summary>
    /// Re-scan must never silently rewrite a broken manifest (user overrides live there):
    /// a corrupt file stops the operation with a user-facing message and stays untouched.
    /// </summary>
    private static async Task<ModpackManifest> ReadManifestForRescanAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await AtomicFile.ReadJsonAsync<ModpackManifest>(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                Loc.T("Err_ManifestCorrupt"),
                exception);
        }
    }

    public Task SaveAsync(LibraryIndex index, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(_paths.LibraryFilePath, index, cancellationToken);

    public async Task<LibraryEntry?> RecordSessionAsync(Guid entryId, int playtimeMinutes, DateTime lastPlayedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var stored = index.Entries.FirstOrDefault(e => e.Id == entryId);
        if (stored is null)
        {
            return null;
        }

        stored.PlaytimeMinutes += Math.Max(0, playtimeMinutes);
        stored.LastPlayedAtUtc = lastPlayedAtUtc;
        await SaveAsync(index, cancellationToken).ConfigureAwait(false);
        _logger.Information("Session recorded for {Name}: +{Minutes} min (total {Total} min)",
            stored.Name, playtimeMinutes, stored.PlaytimeMinutes);
        return stored;
    }

    public static string ManifestPath(string packRoot) => Path.Combine(packRoot, ManifestFileName);

    internal static ModpackManifest ManifestFromScan(Guid id, ScanResult scan) => new()
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id,
        GameType = scan.GameType,
        Engine = scan.Engine,
        Source = scan.Source,
        SystemGamePath = scan.SystemGamePath,
        Mo2 = scan.Mo2ProfileName is null && scan.Mo2DirectoryName is null
            ? null
            : new Mo2Section { ProfileName = scan.Mo2ProfileName ?? string.Empty, Directory = scan.Mo2DirectoryName },
        Launch = new LaunchSection { Executable = scan.LaunchExecutable ?? string.Empty },
        Detection = new DetectionSection(),
    };

    internal static void ApplyScan(ModpackManifest manifest, ScanResult scan)
    {
        manifest.GameType = scan.GameType;
        manifest.Engine = scan.Engine;
        manifest.Source = scan.Source;
        manifest.SystemGamePath = scan.SystemGamePath;
        manifest.Mo2 = scan.Mo2ProfileName is null && scan.Mo2DirectoryName is null
            ? null
            : new Mo2Section { ProfileName = scan.Mo2ProfileName ?? string.Empty, Directory = scan.Mo2DirectoryName };
        manifest.Launch = new LaunchSection { Executable = scan.LaunchExecutable ?? string.Empty };
    }
}

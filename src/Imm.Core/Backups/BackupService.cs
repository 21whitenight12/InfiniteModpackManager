using System.IO;
using System.IO.Compression;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Serilog;
using Imm.Core.Localization;

namespace Imm.Core.Backups;

/// <summary>One archive on disk: name carries the timestamp, sidecar file carries the pin.</summary>
public sealed record BackupArchive(string Path, DateTime CreatedUtc, long SizeBytes, bool IsPinned);

/// <summary>Manual backups per IMM.md «Бэкапы»: MO2 profile folder or Direct game saves, zip with a timestamp.</summary>
public interface IBackupService
{
    /// <summary>Creates an archive and returns its full path (unprefixed — safe to show in the UI).</summary>
    /// <param name="retentionOverride">
    /// App-wide limit (settings.json) used only when the pack manifest carries no own limit.
    /// </param>
    Task<string> CreateBackupAsync(LibraryEntry entry, ModpackManifest manifest,
        CancellationToken cancellationToken = default, int retentionOverride = 0);

    /// <summary>Archives of a pack, newest first.</summary>
    IReadOnlyList<BackupArchive> List(LibraryEntry entry);

    /// <summary>Pins/unpins an archive: pinned archives are never removed by retention.</summary>
    void SetPinned(string archivePath, bool pinned);

    /// <summary>Folder holding this pack's archives (Documents\&lt;packId&gt;).</summary>
    string PackDirectory(Guid packId);
}

public sealed class BackupService : IBackupService
{
    private const string ArchiveExtension = ".zip";
    private const string PinExtension = ".pinned";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    private readonly ImmPaths _paths;
    private readonly ILogger _logger;

    public BackupService(ImmPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger.ForContext<BackupService>();
    }

    /// <inheritdoc />
    public string PackDirectory(Guid packId) => Path.Combine(_paths.DocumentsDirectory, packId.ToString());

    /// <inheritdoc />
    public async Task<string> CreateBackupAsync(LibraryEntry entry, ModpackManifest manifest,
        CancellationToken cancellationToken = default, int retentionOverride = 0)
    {
        var (sourceDirectory, sourceKind) = ResolveSource(manifest, entry.RootPath)
            ?? throw new InvalidOperationException(manifest.Engine is LaunchEngine.Mo2Standard or LaunchEngine.Mo2RootBuilder
                ? Loc.T("Err_BackupNoProfile")
                : Loc.T("Err_BackupNoSaves"));

        var directory = PackDirectory(entry.Id);
        Directory.CreateDirectory(LongPath.Normalize(directory));

        var archivePath = Path.Combine(directory, DateTime.Now.ToString(TimestampFormat) + ArchiveExtension);
        for (var attempt = 2; File.Exists(LongPath.Normalize(archivePath)); attempt++)
        {
            archivePath = Path.Combine(directory,
                $"{DateTime.Now.ToString(TimestampFormat)}_{attempt}{ArchiveExtension}");
        }

        var tempPath = Path.Combine(directory, $".backup.tmp-{Guid.NewGuid():N}{ArchiveExtension}");
        try
        {
            // Упаковка на пуле потоков: профиль MO2 с сейвами бывает на гигабайты, UI ждать не должен.
            await Task.Run(() => WriteArchive(sourceDirectory, tempPath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, LongPath.Normalize(archivePath), overwrite: false);
        }
        finally
        {
            if (File.Exists(LongPath.Normalize(tempPath)))
            {
                try
                {
                    File.Delete(LongPath.Normalize(tempPath));
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the temp file is inert garbage.
                }
            }
        }

        _logger.Information("Backup created for {Pack}: {Source} -> {Archive}", entry.Name, sourceKind, archivePath);
        var manifestLimit = manifest.Backups?.MaxBackups ?? 0;
        ApplyRetention(directory, manifestLimit > 0 ? manifestLimit : Math.Max(0, retentionOverride));
        return archivePath;
    }

    /// <inheritdoc />
    public IReadOnlyList<BackupArchive> List(LibraryEntry entry) => ListArchives(PackDirectory(entry.Id));

    /// <inheritdoc />
    public void SetPinned(string archivePath, bool pinned)
    {
        var pinPath = LongPath.Normalize(archivePath + PinExtension);
        if (pinned)
        {
            if (!File.Exists(pinPath))
            {
                File.WriteAllBytes(pinPath, []);
            }

            return;
        }

        if (File.Exists(pinPath))
        {
            File.Delete(pinPath);
        }
    }

    /// <summary>MO2 packs archive the whole profile folder; Direct packs archive the game saves folder.</summary>
    private (string Directory, string Kind)? ResolveSource(ModpackManifest manifest, string packRoot)
    {
        if (manifest.Engine is LaunchEngine.Mo2Standard or LaunchEngine.Mo2RootBuilder)
        {
            var instance = Mo2Locator.ResolveInstance(packRoot, manifest.Mo2?.Directory);
            var profile = manifest.Mo2?.ProfileName;
            if (instance is null || string.IsNullOrWhiteSpace(profile))
            {
                return null;
            }

            var profileDirectory = Path.Combine(instance, Markers.ProfilesFolder, profile);
            return Directory.Exists(LongPath.Normalize(profileDirectory))
                ? (profileDirectory, Loc.F("Label_BackupProfile", profile))
                : null;
        }

        var savesDirectory = GameSavesDirectory(manifest.GameType);
        return savesDirectory is not null && Directory.Exists(LongPath.Normalize(savesDirectory))
            ? (savesDirectory, Loc.T("Label_BackupSaves"))
            : null;
    }

    /// <summary>Documents\My Games\&lt;Game&gt;\Saves — MO2 redirects saves into the profile, Direct does not.</summary>
    private string? GameSavesDirectory(GameType gameType)
    {
        var folder = gameType switch
        {
            GameType.SkyrimLE => "Skyrim",
            GameType.SkyrimSE => "Skyrim Special Edition",
            GameType.Fallout4 => "Fallout4",
            GameType.FalloutNV => "FalloutNV",
            _ => null,
        };

        return folder is null
            ? null
            : Path.Combine(_paths.DocumentsDirectory, "My Games", folder, "Saves");
    }

    private static void WriteArchive(string sourceDirectory, string tempPath, CancellationToken cancellationToken)
    {
        var root = LongPath.Normalize(Path.GetFullPath(sourceDirectory));
        using var stream = new FileStream(LongPath.Normalize(tempPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(root, file);
            archive.CreateEntryFromFile(LongPath.Normalize(file), entryName, CompressionLevel.Optimal);
        }
    }

    /// <summary>Keeps the newest MaxBackups archives; 0 = unlimited; pinned archives are never touched.</summary>
    private void ApplyRetention(string directory, int maxBackups)
    {
        if (maxBackups <= 0)
        {
            return;
        }

        foreach (var stale in ListArchives(directory).Skip(maxBackups))
        {
            if (stale.IsPinned)
            {
                continue;
            }

            try
            {
                File.Delete(LongPath.Normalize(stale.Path));
                _logger.Information("Backup retention removed {Archive}", stale.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(exception, "Backup retention could not remove {Archive}", stale.Path);
            }
        }
    }

    private static IReadOnlyList<BackupArchive> ListArchives(string directory)
    {
        var normalized = LongPath.Normalize(directory);
        if (!Directory.Exists(normalized))
        {
            return [];
        }

        return Directory.EnumerateFiles(normalized, "*" + ArchiveExtension)
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .Select(info => new BackupArchive(
                info.FullName,
                ParseTimestamp(info.Name) ?? info.CreationTimeUtc,
                info.Length,
                File.Exists(LongPath.Normalize(info.FullName + PinExtension))))
            .OrderByDescending(archive => archive.CreatedUtc)
            .ThenByDescending(archive => archive.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Archive name → local creation time; unparsable names fall back to the file system stamp.</summary>
    private static DateTime? ParseTimestamp(string fileName)
    {
        var stamp = Path.GetFileNameWithoutExtension(fileName);
        var underscore = stamp.IndexOf('_');
        if (underscore > 0)
        {
            stamp = stamp[..underscore];
        }

        return DateTime.TryParseExact(stamp, TimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}

using System.IO;
using System.IO.Compression;
using Imm.Core.Backups;
using Imm.Core.Domain;
using Imm.Core.IO;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>Manual backups (IMM.md «Бэкапы»): zip under Documents\&lt;packId&gt;, profile or saves, retention + pin.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-backup-" + Guid.NewGuid().ToString("N"));
    private readonly string _documents;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        _documents = Path.Combine(_temp, "Documents");
        Directory.CreateDirectory(_documents);
        var paths = ImmPaths.Resolve(
            exeBaseDirectory: Path.Combine(_temp, "bin"),
            localAppDataDirectory: Path.Combine(_temp, "appdata"),
            documentsDirectory: _documents);
        _service = new BackupService(paths, Log.Logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private string CreateMo2Pack()
    {
        var pack = Path.Combine(_temp, "pack");
        var profile = Path.Combine(pack, "profiles", "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(pack, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(pack, "ModOrganizer.ini"), "[General]\ngameName=Fallout 4\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+ModA\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*ModA.esp\n");
        Directory.CreateDirectory(Path.Combine(profile, "saves"));
        File.WriteAllText(Path.Combine(profile, "saves", "autosave.fos"), "save");
        return pack;
    }

    private static string[] EntryNames(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
    }

    [Fact]
    public async Task CreateBackup_Mo2Pack_ZipsWholeProfileIntoDocuments()
    {
        var pack = CreateMo2Pack();
        var entry = new LibraryEntry { Name = "Pack", RootPath = pack };
        var manifest = new ModpackManifest
        {
            Engine = LaunchEngine.Mo2Standard,
            Mo2 = new Mo2Section { ProfileName = "Default" },
        };

        var archivePath = await _service.CreateBackupAsync(entry, manifest);

        Assert.True(File.Exists(archivePath));
        Assert.StartsWith(_documents, archivePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(entry.Id.ToString(), archivePath);
        Assert.EndsWith(".zip", archivePath);

        var names = EntryNames(archivePath);
        Assert.Contains("modlist.txt", names);
        Assert.Contains("plugins.txt", names);
        Assert.Contains("saves/autosave.fos", names);
    }

    [Fact]
    public async Task CreateBackup_DirectPack_ZipsGameSaves()
    {
        var pack = Path.Combine(_temp, "direct");
        Directory.CreateDirectory(pack);
        var saves = Path.Combine(_documents, "My Games", "Fallout4", "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "Save1.fos"), "save");
        var entry = new LibraryEntry { Name = "Direct", RootPath = pack };
        var manifest = new ModpackManifest { Engine = LaunchEngine.Direct, GameType = GameType.Fallout4 };

        var archivePath = await _service.CreateBackupAsync(entry, manifest);

        Assert.Contains("Save1.fos", EntryNames(archivePath));
    }

    [Fact]
    public async Task CreateBackup_NoSource_Throws()
    {
        var pack = Path.Combine(_temp, "empty");
        Directory.CreateDirectory(pack);
        var entry = new LibraryEntry { Name = "Empty", RootPath = pack };
        var manifest = new ModpackManifest { Engine = LaunchEngine.Direct, GameType = GameType.Unknown };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateBackupAsync(entry, manifest));

        Assert.Contains("сейвов", exception.Message);
    }

    [Fact]
    public async Task CreateBackup_Retention_KeepsNewestAndPinned()
    {
        var pack = CreateMo2Pack();
        var entry = new LibraryEntry { Name = "Retention", RootPath = pack };
        var manifest = new ModpackManifest
        {
            Engine = LaunchEngine.Mo2Standard,
            Mo2 = new Mo2Section { ProfileName = "Default" },
            Backups = new BackupsSection { MaxBackups = 2 },
        };

        var directory = _service.PackDirectory(entry.Id);
        Directory.CreateDirectory(directory);
        foreach (var stamp in new[] { "20200101-000000", "20200102-000000", "20200103-000000" })
        {
            File.WriteAllBytes(Path.Combine(directory, stamp + ".zip"), [1]);
        }

        _service.SetPinned(Path.Combine(directory, "20200101-000000.zip"), pinned: true);

        await _service.CreateBackupAsync(entry, manifest);

        var names = _service.List(entry).Select(archive => Path.GetFileName(archive.Path)).ToArray();
        Assert.Equal(3, names.Length); // новый + 20200103 + запиненный 20200101
        Assert.Contains("20200103-000000.zip", names);
        Assert.Contains("20200101-000000.zip", names); // пин защитил от автоочистки
        Assert.DoesNotContain("20200102-000000.zip", names);
    }

    [Fact]
    public void SetPinned_TogglesMarkerAndListSortsNewestFirst()
    {
        var entry = new LibraryEntry { Name = "Pins", RootPath = Path.Combine(_temp, "pins") };
        var directory = _service.PackDirectory(entry.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "20200101-000000.zip"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "20200102-000000.zip"), [2]);

        _service.SetPinned(Path.Combine(directory, "20200101-000000.zip"), pinned: true);
        var archives = _service.List(entry);

        Assert.Equal("20200102-000000.zip", Path.GetFileName(archives[0].Path)); // новые сверху
        Assert.False(archives[0].IsPinned);
        Assert.True(archives[1].IsPinned);

        _service.SetPinned(archives[1].Path, pinned: false);
        Assert.All(_service.List(entry), archive => Assert.False(archive.IsPinned));
    }
}

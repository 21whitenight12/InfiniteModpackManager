using System.IO;
using Imm.Core.Backups;
using Imm.Core.Domain;
using Imm.Core.IO;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>App-wide backup limit (settings.json) applies only when the pack has no own limit.</summary>
public sealed class BackupRetentionOverrideTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-retention-" + Guid.NewGuid().ToString("N"));
    private readonly string _documents;
    private readonly BackupService _service;

    public BackupRetentionOverrideTests()
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

    private (LibraryEntry Entry, ModpackManifest Manifest) CreatePack()
    {
        var pack = Path.Combine(_temp, "pack");
        var profile = Path.Combine(pack, "profiles", "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(pack, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(pack, "ModOrganizer.ini"), "[General]\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+ModA\n");

        var entry = new LibraryEntry { Name = "Pack", RootPath = pack };
        var manifest = new ModpackManifest
        {
            Engine = LaunchEngine.Mo2Standard,
            Mo2 = new Mo2Section { ProfileName = "Default" },
        };
        return (entry, manifest);
    }

    [Fact]
    public async Task SettingsLimit_PrunesArchives_WhenManifestHasNone()
    {
        var (entry, manifest) = CreatePack();

        await _service.CreateBackupAsync(entry, manifest, retentionOverride: 1);
        await _service.CreateBackupAsync(entry, manifest, retentionOverride: 1);

        Assert.Single(_service.List(entry));
    }

    [Fact]
    public async Task ManifestLimit_WinsOverSettingsLimit()
    {
        var (entry, manifest) = CreatePack();
        manifest.Backups.MaxBackups = 5;

        await _service.CreateBackupAsync(entry, manifest, retentionOverride: 1);
        await _service.CreateBackupAsync(entry, manifest, retentionOverride: 1);

        Assert.Equal(2, _service.List(entry).Count);
    }

    [Fact]
    public async Task ZeroSettingsLimit_KeepsEverything()
    {
        var (entry, manifest) = CreatePack();

        await _service.CreateBackupAsync(entry, manifest);
        await _service.CreateBackupAsync(entry, manifest);

        Assert.Equal(2, _service.List(entry).Count);
    }
}

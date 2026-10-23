using System.IO;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Library;
using Imm.Core.Scanning;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private (LibraryService Service, ImmPaths Paths) CreateService()
    {
        var appData = Path.Combine(_temp, "appdata");
        var paths = ImmPaths.Resolve(exeBaseDirectory: Path.Combine(_temp, "bin"), localAppDataDirectory: appData);
        var scanner = new ModpackScanner(new SystemGameLocator(new NullRegistryService(), Log.Logger), Log.Logger);
        return (new LibraryService(paths, scanner, Log.Logger), paths);
    }

    private string CreateMo2Pack(string name)
    {
        var pack = Path.Combine(_temp, name);
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(pack, "ModOrganizer.ini"), string.Empty);
        Directory.CreateDirectory(Path.Combine(pack, "profiles", "Default"));
        File.WriteAllText(Path.Combine(pack, "Fallout4.exe"), string.Empty);
        return pack;
    }

    [Fact]
    public async Task Load_NoFile_EmptyIndex()
    {
        var (service, paths) = CreateService();

        var index = await service.LoadAsync();

        Assert.Empty(index.Entries);
        Assert.Equal(LibraryIndex.CurrentSchemaVersion, index.SchemaVersion);
        Assert.False(File.Exists(paths.LibraryFilePath));
    }

    [Fact]
    public async Task Import_CreatesEntryAndManifest()
    {
        var (service, paths) = CreateService();
        var pack = CreateMo2Pack("Test Pack");

        var entry = await service.ImportAsync(pack);

        Assert.Equal("Test Pack", entry.Name);
        Assert.Equal(pack, entry.RootPath, ignoreCase: true);
        Assert.True(File.Exists(paths.LibraryFilePath));
        Assert.True(File.Exists(LibraryService.ManifestPath(pack)));

        var index = await service.LoadAsync();
        var loaded = Assert.Single(index.Entries);
        Assert.Equal(entry.Id, loaded.Id);

        var manifest = await service.LoadManifestAsync(entry);
        Assert.NotNull(manifest);
        Assert.Equal(entry.Id, manifest!.Id);
        Assert.Equal(GameType.Fallout4, manifest.GameType);
        Assert.Equal(LaunchEngine.Mo2Standard, manifest.Engine);
        Assert.Equal("Default", manifest.Mo2!.ProfileName);
        Assert.Equal("Fallout4.exe", manifest.Launch.Executable);
        Assert.False(manifest.Detection.OverriddenByUser);
    }

    [Fact]
    public async Task Import_Twice_Throws()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Dup Pack");
        await service.ImportAsync(pack);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(pack));
    }

    [Fact]
    public async Task Import_NotAModpack_Throws()
    {
        var (service, _) = CreateService();
        var empty = Path.Combine(_temp, "Empty");
        Directory.CreateDirectory(empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(empty));
    }

    [Fact]
    public async Task Rescan_UserOverrideDetection_KeptUntouched()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Override Pack");
        var entry = await service.ImportAsync(pack);

        // User pins an override in modpack.json.
        var manifest = await service.LoadManifestAsync(entry);
        Assert.NotNull(manifest);
        manifest!.GameType = GameType.SkyrimSE;
        manifest.Detection.OverriddenByUser = true;
        await service.SaveManifestAsync(entry.RootPath, manifest);

        // Re-scan sees Fallout4 again, but the override must survive.
        var scan = await service.RescanAsync(entry);

        Assert.Equal(GameType.Fallout4, scan.GameType); // scanner heuristics still see Fallout
        var after = await service.LoadManifestAsync(entry);
        Assert.NotNull(after);
        Assert.Equal(GameType.SkyrimSE, after!.GameType); // override untouched
        Assert.True(after.Detection.OverriddenByUser);
    }

    [Fact]
    public async Task Rescan_NoOverride_UpdatesManifest()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Fresh Pack");
        var entry = await service.ImportAsync(pack);

        var manifest = await service.LoadManifestAsync(entry);
        Assert.NotNull(manifest);
        manifest!.GameType = GameType.Unknown; // simulate stale data
        manifest.Detection.OverriddenByUser = false;
        await service.SaveManifestAsync(entry.RootPath, manifest);

        await service.RescanAsync(entry);

        var after = await service.LoadManifestAsync(entry);
        Assert.NotNull(after);
        Assert.Equal(GameType.Fallout4, after!.GameType); // refreshed by scan
    }

    [Fact]
    public async Task Library_BrokenJson_StartsEmpty()
    {
        var (service, paths) = CreateService();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LibraryFilePath)!);
        await File.WriteAllTextAsync(paths.LibraryFilePath, "{ not valid json !!!");

        var index = await service.LoadAsync();

        Assert.Empty(index.Entries);
    }

    [Fact]
    public async Task Import_WithDisplayNameAndCover_UsesBoth()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Folder Name");
        var cover = Path.Combine(_temp, "art.png");
        await File.WriteAllBytesAsync(cover, [1, 2, 3]);

        var entry = await service.ImportAsync(pack, displayName: "  Моя сборка  ", coverImagePath: cover);

        Assert.Equal("Моя сборка", entry.Name);
        Assert.True(File.Exists(Path.Combine(pack, "cover.png")));
        var index = await service.LoadAsync();
        Assert.Equal("Моя сборка", index.Entries.Single(e => e.Id == entry.Id).Name);
    }

    [Fact]
    public async Task Import_NoDisplayName_KeepsFolderName()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Folder Name");

        var entry = await service.ImportAsync(pack);

        Assert.Equal("Folder Name", entry.Name);
    }

    [Fact]
    public async Task Rename_UpdatesIndexAndEntry()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Old Name");
        var entry = await service.ImportAsync(pack);

        await service.RenameAsync(entry, "New Name");

        Assert.Equal("New Name", entry.Name);
        var index = await service.LoadAsync();
        Assert.Equal("New Name", index.Entries.Single().Name);
    }

    [Fact]
    public async Task Rename_Empty_Throws()
    {
        var (service, _) = CreateService();
        var entry = await service.ImportAsync(CreateMo2Pack("Some"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(entry, "   "));
    }

    [Fact]
    public async Task SetCover_ReplacesPreviousCover()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Covered");
        var first = Path.Combine(_temp, "a.png");
        var second = Path.Combine(_temp, "b.jpg");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2]);

        await service.SetCoverAsync(pack, first);
        Assert.True(File.Exists(Path.Combine(pack, "cover.png")));

        var result = await service.SetCoverAsync(pack, second);

        Assert.Equal(Path.Combine(pack, "cover.jpg"), result);
        Assert.False(File.Exists(Path.Combine(pack, "cover.png"))); // old cover removed
        Assert.True(File.Exists(Path.Combine(pack, "cover.jpg")));
    }

    [Fact]
    public async Task SetCover_UnsupportedExtension_Throws()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("NoCover");
        var bmp = Path.Combine(_temp, "x.bmp");
        await File.WriteAllBytesAsync(bmp, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetCoverAsync(pack, bmp));
    }

    [Fact]
    public async Task SetCover_CopyFails_KeepsPreviousCover()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("CoverKept");
        var good = Path.Combine(_temp, "good.png");
        await File.WriteAllBytesAsync(good, [7, 7, 7]);
        await service.SetCoverAsync(pack, good);
        var previous = Path.Combine(pack, "cover.png");
        var previousBytes = await File.ReadAllBytesAsync(previous);

        // Источник занят другим процессом: копирование обязано упасть, а прежняя обложка — уцелеть.
        using (new FileStream(good, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => service.SetCoverAsync(pack, good));
        }

        Assert.True(File.Exists(previous));
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(previous));
        Assert.Empty(Directory.GetFiles(pack, ".cover.tmp-*"));
    }

    [Fact]
    public async Task LoadManifest_CorruptFile_ReturnsNullAndKeepsFile()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Broken Manifest");
        var entry = await service.ImportAsync(pack);
        var manifestPath = LibraryService.ManifestPath(pack);
        await File.WriteAllTextAsync(manifestPath, "{ not valid json !!!");

        var manifest = await service.LoadManifestAsync(entry);

        Assert.Null(manifest);
        Assert.Equal("{ not valid json !!!", await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task Rescan_CorruptManifest_ThrowsAndKeepsFile()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Broken Rescan");
        var entry = await service.ImportAsync(pack);
        var manifestPath = LibraryService.ManifestPath(pack);
        await File.WriteAllTextAsync(manifestPath, "{ not valid json !!!");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RescanAsync(entry));

        Assert.Contains("modpack.json", exception.Message);
        Assert.Equal("{ not valid json !!!", await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task Rescan_UserOverride_StillRefreshesLaunchExecutable()
    {
        var (service, _) = CreateService();
        var pack = CreateMo2Pack("Override Loader");
        var entry = await service.ImportAsync(pack);

        var manifest = await service.LoadManifestAsync(entry);
        Assert.NotNull(manifest);
        manifest!.Detection.OverriddenByUser = true;
        manifest.Launch.Executable = "stale_loader.exe";
        await service.SaveManifestAsync(entry.RootPath, manifest);

        await service.RescanAsync(entry);

        var after = await service.LoadManifestAsync(entry);
        Assert.NotNull(after);
        Assert.Equal("Fallout4.exe", after!.Launch.Executable); // путь запуска — факт скана
        Assert.True(after.Detection.OverriddenByUser);          // а детект игры — выбор пользователя
    }
}

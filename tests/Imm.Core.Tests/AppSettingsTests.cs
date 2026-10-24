using System.IO;
using Imm.Core.IO;
using Imm.Core.Settings;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>settings.json (app-wide defaults): tolerant load, atomic save, never blocks the app.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-settings-" + Guid.NewGuid().ToString("N"));

    public AppSettingsTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private AppSettingsService CreateService()
    {
        var paths = ImmPaths.Resolve(
            exeBaseDirectory: Path.Combine(_temp, "bin"),
            localAppDataDirectory: Path.Combine(_temp, "appdata"),
            documentsDirectory: Path.Combine(_temp, "documents"));
        return new AppSettingsService(paths, Log.Logger);
    }

    [Fact]
    public async Task MissingFile_ReturnsDefaults()
    {
        var settings = await CreateService().LoadAsync();

        Assert.Equal(0, settings.MaxBackups); // 0 = без лимита (IMM.md default)
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsLimit()
    {
        var service = CreateService();
        await service.SaveAsync(new AppSettings { MaxBackups = 7 });

        Assert.True(File.Exists(service.FilePath));
        var loaded = await service.LoadAsync();
        Assert.Equal(7, loaded.MaxBackups);
    }

    [Fact]
    public async Task CorruptFile_FallsBackToDefaultsAndKeepsFile()
    {
        var service = CreateService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.FilePath)!);
        await File.WriteAllTextAsync(service.FilePath, "{ this is not json");

        var loaded = await service.LoadAsync();

        Assert.Equal(0, loaded.MaxBackups);
        Assert.True(File.Exists(service.FilePath)); // файл не перезаписан молча
    }

    [Fact]
    public void SettingsLiveNextToLibraryJson()
    {
        var service = CreateService();

        Assert.Equal("settings.json", Path.GetFileName(service.FilePath));
        Assert.Equal(
            Path.GetDirectoryName(Path.Combine(_temp, "appdata", "IMM", ImmPaths.LibraryFileName)),
            Path.GetDirectoryName(service.FilePath));
    }
}

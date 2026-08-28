using Imm.Core.Domain;
using Imm.Core.IO;
using Xunit;

namespace Imm.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "imm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteRead_RoundTripsDocument()
    {
        var path = Path.Combine(_tempDirectory, "modpack.json");
        var manifest = new ModpackManifest
        {
            GameType = GameType.SkyrimSE,
            Engine = LaunchEngine.Mo2RootBuilder,
            Source = PackSource.Portable,
            Mo2 = new Mo2Section { ProfileName = "Default" }
        };

        await AtomicFile.WriteJsonAsync(path, manifest);
        var loaded = await AtomicFile.ReadJsonAsync<ModpackManifest>(path);

        Assert.Equal(manifest.Id, loaded.Id);
        Assert.Equal(GameType.SkyrimSE, loaded.GameType);
        Assert.Equal(LaunchEngine.Mo2RootBuilder, loaded.Engine);
        Assert.Equal(PackSource.Portable, loaded.Source);
        Assert.Equal("Default", loaded.Mo2!.ProfileName);
    }

    [Fact]
    public async Task Write_PutsSchemaVersionFirst()
    {
        var path = Path.Combine(_tempDirectory, "library.json");
        var index = new LibraryIndex();

        await AtomicFile.WriteJsonAsync(path, index);
        var json = await File.ReadAllTextAsync(path);

        var schemaIndex = json.IndexOf("\"SchemaVersion\"", StringComparison.Ordinal);
        var entriesIndex = json.IndexOf("\"Entries\"", StringComparison.Ordinal);
        Assert.True(schemaIndex >= 0, "SchemaVersion must be present.");
        Assert.True(entriesIndex > schemaIndex, "schemaVersion must be the first field (IMM.md Data Contracts).");
    }

    [Fact]
    public async Task Write_IsAtomic_NoTempFilesLeftBehind()
    {
        var path = Path.Combine(_tempDirectory, "library.json");
        await AtomicFile.WriteJsonAsync(path, new LibraryIndex());

        var leftovers = Directory.GetFiles(_tempDirectory, "*.tmp-*");
        Assert.Empty(leftovers);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SerializesEnumsAsStrings()
    {
        var path = Path.Combine(_tempDirectory, "modpack.json");
        await AtomicFile.WriteJsonAsync(path, new ModpackManifest { Engine = LaunchEngine.Mo2Standard });

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Engine\": \"Mo2Standard\"", json, StringComparison.Ordinal);
    }
}

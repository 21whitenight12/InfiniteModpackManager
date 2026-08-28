using Imm.Core.IO;
using Xunit;

namespace Imm.Core.Tests;

public class ImmPathsTests : IDisposable
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
    public void Resolve_WithoutPortableLibrary_UsesLocalAppData()
    {
        var appData = Path.Combine(_tempDirectory, "appdata");

        var paths = ImmPaths.Resolve(exeBaseDirectory: Path.Combine(_tempDirectory, "bin"), localAppDataDirectory: appData);

        Assert.False(paths.Portable);
        Assert.EndsWith(Path.Combine("IMM", "library.json"), paths.LibraryFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("IMM", "logs"), paths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WithPortableLibrary_NextToExe_Wins()
    {
        var exeDir = Path.Combine(_tempDirectory, "bin");
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, ImmPaths.LibraryFileName), "{}");
        var appData = Path.Combine(_tempDirectory, "appdata");

        var paths = ImmPaths.Resolve(exeBaseDirectory: exeDir, localAppDataDirectory: appData);

        Assert.True(paths.Portable);
        Assert.Equal(Path.Combine(exeDir, ImmPaths.LibraryFileName), paths.LibraryFilePath);
        Assert.Equal(Path.Combine(exeDir, "logs"), paths.LogsDirectory);
    }

    [Fact]
    public void Normalize_LongPathGetsPrefix_ShortPathUntouched()
    {
        var shortPath = @"E:\Repo\pack\SkyrimSE.exe";
        Assert.Equal(shortPath, LongPath.Normalize(shortPath));

        var longTail = string.Join('\\', Enumerable.Repeat("folder", 50));
        var longPath = @"E:\" + longTail + @"\SkyrimSE.exe";
        Assert.True(longPath.Length >= 260);
        Assert.Equal(@"\\?\" + longPath, LongPath.Normalize(longPath));

        Assert.StartsWith(@"\\?\UNC\", LongPath.Normalize(@"\\server\share\" + longTail));
        Assert.Equal(@"\\?\E:\already", LongPath.Normalize(@"\\?\E:\already"));
    }
}

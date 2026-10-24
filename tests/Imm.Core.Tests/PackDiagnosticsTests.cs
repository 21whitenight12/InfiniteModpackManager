using System.IO;
using Imm.Core.Diagnostics;
using Imm.Core.Domain;
using Imm.Core.Scanning;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>
/// Scanner screen data: fresh scan vs stored manifest, on-disk validation, warnings.
/// Diagnostics are read-only — nothing here may write to the pack.
/// </summary>
public sealed class PackDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "imm-diag-" + Guid.NewGuid().ToString("N"));

    public PackDiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PackDiagnosticsService CreateService() =>
        new(new ModpackScanner(new SystemGameLocator(new NullRegistryService(), Log.Logger), Log.Logger), Log.Logger);

    private void Write(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private void CreateDir(string relativePath) => Directory.CreateDirectory(Path.Combine(_root, relativePath));

    private LibraryEntry Entry() => new() { Name = "Pack", RootPath = _root };

    /// <summary>Portable MO2 + Root Builder pack with the loader inside Stock Game.</summary>
    private void CreateRootBuilderPack()
    {
        Write(@"ModOrganizer.exe");
        Write(@"ModOrganizer.ini");
        Write(@"plugins\RootBuilder.dll");
        CreateDir(@"profiles\Default");
        Write(@"Stock Game\SkyrimSE.exe");
        Write(@"Stock Game\skse64_loader.exe");
        CreateDir(@"mods");
    }

    private static ModpackManifest MatchingManifest() => new()
    {
        GameType = GameType.SkyrimSE,
        Engine = LaunchEngine.Mo2RootBuilder,
        Source = PackSource.Portable,
        Mo2 = new Mo2Section { ProfileName = "Default" },
        Launch = new LaunchSection { Executable = "skse64_loader.exe" },
    };

    [Fact]
    public async Task MissingFolder_ReportsProblemWithoutThrowing()
    {
        var entry = new LibraryEntry { Name = "Gone", RootPath = Path.Combine(_root, "nope") };

        var diagnostics = await CreateService().AnalyzeAsync(entry, MatchingManifest());

        Assert.False(diagnostics.RootExists);
        Assert.True(diagnostics.HasProblems);
        Assert.Contains(diagnostics.Warnings, warning => warning.Contains("не найдена", StringComparison.Ordinal));
        Assert.Empty(diagnostics.Detected);
    }

    [Fact]
    public async Task HealthyPack_MatchingManifest_HasNoProblems()
    {
        CreateRootBuilderPack();

        var diagnostics = await CreateService().AnalyzeAsync(Entry(), MatchingManifest());

        Assert.True(diagnostics.HasProblems == false, string.Join(" | ", diagnostics.Warnings));
        Assert.False(diagnostics.ManifestStale);
        Assert.Empty(diagnostics.Warnings);
        Assert.Contains(diagnostics.Detected, row => row.Label == "Движок" && row.Value == "MO2 + Root Builder");
        Assert.Contains(diagnostics.Detected, row => row.Label == "Root Builder" && row.Value == "да");
        Assert.Contains(diagnostics.Stored, row => row.Label == "Профиль MO2" && row.Value == "Default");
    }

    [Fact]
    public async Task ManifestWithoutMo2_IsFlaggedAsStale()
    {
        CreateRootBuilderPack();
        var manifest = new ModpackManifest
        {
            GameType = GameType.SkyrimSE,
            Engine = LaunchEngine.Direct,
            Source = PackSource.Portable,
        };

        var diagnostics = await CreateService().AnalyzeAsync(Entry(), manifest);

        Assert.True(diagnostics.ManifestStale);
        Assert.True(diagnostics.HasProblems);
        Assert.Contains(diagnostics.Warnings, warning => warning.Contains("ре-скан", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserOverrides_AreNotTreatedAsStale()
    {
        CreateRootBuilderPack();
        var manifest = MatchingManifest();
        manifest.GameType = GameType.Fallout4;
        manifest.Detection.OverriddenByUser = true;
        manifest.Detection.ProfileOverriddenByUser = true;
        manifest.Mo2!.ProfileName = "MyProfile";

        var diagnostics = await CreateService().AnalyzeAsync(Entry(), manifest);

        Assert.False(diagnostics.ManifestStale); // ручной выбор принадлежит пользователю (IMM.md)
        Assert.Contains(diagnostics.Stored, row => row.Label == "Профиль MO2" && row.Value == "MyProfile");
    }

    [Fact]
    public async Task StoredLoaderThatIsGone_BecomesWarningRow()
    {
        CreateRootBuilderPack();
        var manifest = MatchingManifest();
        manifest.Launch.Executable = Path.Combine(_root, "mods", "DeletedMod", "Root", "skse64_loader.exe");

        var diagnostics = await CreateService().AnalyzeAsync(Entry(), manifest);

        var row = Assert.Single(diagnostics.Stored, item => item.Label == "Запускаемый файл");
        Assert.Equal(DiagnosticSeverity.Warning, row.Severity);
        Assert.True(diagnostics.HasProblems);
    }

    [Fact]
    public async Task MissingManifest_IsReportedAsUnscanned()
    {
        CreateRootBuilderPack();

        var diagnostics = await CreateService().AnalyzeAsync(Entry(), null);

        Assert.True(diagnostics.ManifestMissing);
        Assert.True(diagnostics.HasProblems);
        Assert.Empty(diagnostics.Stored);
        Assert.Contains(diagnostics.Warnings, warning => warning.Contains("modpack.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownGame_IsAWarningInDetectedRows()
    {
        Write(@"ModOrganizer.exe");
        Write(@"ModOrganizer.ini");
        CreateDir(@"profiles\Default");

        var diagnostics = await CreateService().AnalyzeAsync(Entry(), null);

        var row = Assert.Single(diagnostics.Detected, item => item.Label == "Игра");
        Assert.Equal(DiagnosticSeverity.Warning, row.Severity);
    }
}

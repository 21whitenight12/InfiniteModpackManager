using System.IO;
using Imm.Core.Domain;
using Imm.Core.Scanning;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace Imm.Core.Tests;

/// <summary>Registry fake for tests: fed from a dictionary, no machine state touched.</summary>
internal sealed class FakeRegistryService : IRegistryService
{
    public Dictionary<(RegistryHive Hive, RegistryView View, string Key, string Value), string> Strings { get; } = new();
    public Dictionary<(RegistryHive Hive, RegistryView View, string Key), List<string>> SubKeys { get; } = new();

    public string? GetString(RegistryHive hive, RegistryView view, string subKey, string valueName) =>
        Strings.TryGetValue((hive, view, subKey, valueName), out var value) ? value : null;

    public IEnumerable<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey) =>
        SubKeys.TryGetValue((hive, view, subKey), out var keys) ? keys : [];
}

internal sealed class NullRegistryService : IRegistryService
{
    public string? GetString(RegistryHive hive, RegistryView view, string subKey, string valueName) => null;
    public IEnumerable<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey) => [];
}

public sealed class ModpackScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "imm-scan-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ModpackScannerTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ModpackScanner CreateScanner(IRegistryService? registry = null) =>
        new(new SystemGameLocator(registry ?? new NullRegistryService(), Serilog.Log.Logger), Serilog.Log.Logger);

    private void Write(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private void CreateDir(string relativePath) =>
        Directory.CreateDirectory(Path.Combine(_root, relativePath));

    [Fact]
    public async Task PortableMo2RootBuilder_FullDetection()
    {
        // Arrange: classic Stock Game + Root Builder pack (names only — no file contents written).
        Write(@"ModOrganizer.exe");
        Write(@"ModOrganizer.ini");
        Write(@"plugins\RootBuilder.dll");
        CreateDir(@"profiles\Default");
        Write(@"mods\MyMod\SKSE\Plugins\dummy.dll");
        Write(@"mods\Cleaned Root Files\SkyrimSE.exe");
        Write(@"mods\Cleaned Root Files\binkw64.dll");
        Write(@"Stock Game\SkyrimSE.exe");
        Write(@"Stock Game\skse64_loader.exe");

        // Act
        var scan = await CreateScanner().ScanAsync(_root);

        // Assert
        Assert.True(scan.LooksLikeModpack);
        Assert.Equal(GameType.SkyrimSE, scan.GameType);
        Assert.Equal(LaunchEngine.Mo2RootBuilder, scan.Engine);
        Assert.Equal(PackSource.Portable, scan.Source);
        Assert.Equal("Default", scan.Mo2ProfileName);
        Assert.Equal("skse64_loader.exe", scan.LaunchExecutable); // loader detected inside Stock Game
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public async Task SystemInstallMo2Standard_NoPortableRoot()
    {
        Write(@"ModOrganizer.exe");
        Write(@"ModOrganizer.ini");
        CreateDir(@"profiles\Main");
        Write(@"Fallout4.exe");
        Write(@"f4se_loader.exe");

        // No Steam/GOG registry entries → system path not found → warning expected (not silent guess).
        var scan = await CreateScanner().ScanAsync(_root);

        Assert.Equal(GameType.Fallout4, scan.GameType);
        Assert.Equal(LaunchEngine.Mo2Standard, scan.Engine);
        Assert.Equal(PackSource.System, scan.Source);
        Assert.Null(scan.SystemGamePath);
        Assert.Contains(scan.Warnings, w => w.Contains("Системная установка"));
        Assert.Equal("f4se_loader.exe", scan.LaunchExecutable); // f4se loader sits in pack root next to the exe
    }

    [Fact]
    public async Task GameAsRoot_NestedMo2Instance_FullDetection()
    {
        // Game-as-root exception (TrueAE-style): full game install in the root,
        // portable MO2 instance in a first-level subdirectory.
        Write(@"SkyrimSE.exe");
        Write(@"Data\Skyrim.esm");
        Write(@"MO2\ModOrganizer.exe");
        Write(@"MO2\ModOrganizer.ini");
        CreateDir(@"MO2\profiles\TRUE AE V5.32 EXTENDED");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.True(scan.LooksLikeModpack);
        Assert.Equal(GameType.SkyrimSE, scan.GameType);
        Assert.Equal(LaunchEngine.Mo2Standard, scan.Engine);
        Assert.Equal(PackSource.Portable, scan.Source); // game ships with the pack
        Assert.Null(scan.SystemGamePath);
        Assert.Equal("TRUE AE V5.32 EXTENDED", scan.Mo2ProfileName);
        Assert.Equal("MO2", scan.Mo2DirectoryName);
        Assert.Equal("SkyrimSE.exe", scan.LaunchExecutable);
    }

    [Fact]
    public async Task MultipleNestedMo2Instances_ProducesWarning_NotSilentGuess()
    {
        Write(@"SkyrimSE.exe");
        Write(@"MO2\ModOrganizer.exe");
        Write(@"MO2\ModOrganizer.ini");
        Write(@"MO2 Backup\ModOrganizer.exe");
        Write(@"MO2 Backup\ModOrganizer.ini");
        CreateDir(@"MO2\profiles\Default");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.Equal(LaunchEngine.Direct, scan.Engine); // ambiguity — no MO2 handling
        Assert.Contains(scan.Warnings, w => w.Contains("несколько вложенных"));
    }

    [Fact]
    public async Task MultipleProfiles_ProducesWarning_NotSilentGuess()
    {
        Write(@"ModOrganizer.exe");
        Write(@"ModOrganizer.ini");
        CreateDir(@"profiles\Default");
        CreateDir(@"profiles\Hardcore");
        Write(@"FalloutNV.exe");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.Null(scan.Mo2ProfileName);
        Assert.Contains(scan.Warnings, w => w.Contains("несколько профилей"));
    }

    [Fact]
    public async Task DirectLaunch_NoMo2_GameExe()
    {
        Write(@"TESV.exe");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.Equal(GameType.SkyrimLE, scan.GameType);
        Assert.Equal(LaunchEngine.Direct, scan.Engine);
        Assert.Equal("TESV.exe", scan.LaunchExecutable);
        Assert.True(scan.LooksLikeModpack);
    }

    [Fact]
    public async Task GammaLayout_AnomalyNextToMo2Folder_FullDetection()
    {
        // G.A.M.M.A real layout: <pack>\Anomaly (the game) + <pack>\GEMMA (portable MO2 instance).
        // Neither folder name is a known marker, so the facts come from MO2's own ini
        // (gameName + gamePath), and the profile name arrives as Qt UTF-8 escapes.
        var game = Path.Combine(_root, "Anomaly");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "AnomalyLauncher.exe"), string.Empty);
        CreateDir(@"Anomaly\bin");
        File.WriteAllText(Path.Combine(game, "bin", "AnomalyDX11AVX.exe"), string.Empty);

        Write(@"GEMMA\ModOrganizer.exe");
        CreateDir(@"GEMMA\profiles\3 - G.A.M.M.A - ALL DOP+Графпак+GFY");
        File.WriteAllText(Path.Combine(_root, "GEMMA", "ModOrganizer.ini"),
            "[General]\n"
            + "gameName=STALKER Anomaly\n"
            + $"gamePath=@ByteArray({game.Replace("\\", "\\\\")})\n"
            + "selected_profile=@ByteArray(3 - G.A.M.M.A - ALL DOP+\\xd0\\x93\\xd1\\x80\\xd0\\xb0\\xd1\\x84\\xd0\\xbf\\xd0\\xb0\\xd0\\xba+GFY)\n");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.True(scan.LooksLikeModpack);
        Assert.Equal(GameType.StalkerAnomaly, scan.GameType);
        Assert.Equal(LaunchEngine.Mo2Standard, scan.Engine);
        Assert.Equal(PackSource.Portable, scan.Source);   // the game ships inside the pack folder
        Assert.Equal(game, scan.SystemGamePath);          // MO2 resolves the game folder from this path
        Assert.Equal("GEMMA", scan.Mo2DirectoryName);
        Assert.Equal("3 - G.A.M.M.A - ALL DOP+Графпак+GFY", scan.Mo2ProfileName);
        Assert.Equal("AnomalyLauncher.exe", scan.LaunchExecutable);
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public async Task StalkerAnomaly_GameFolderDirect_IsPortable()
    {
        // No store sells Anomaly: the folder itself is the game, so no "system install" warning.
        Write(@"AnomalyLauncher.exe");
        Write(@"bin\AnomalyDX11AVX.exe");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.Equal(GameType.StalkerAnomaly, scan.GameType);
        Assert.Equal(LaunchEngine.Direct, scan.Engine);
        Assert.Equal(PackSource.Portable, scan.Source);
        Assert.Null(scan.SystemGamePath);
        Assert.Equal("AnomalyLauncher.exe", scan.LaunchExecutable);
        Assert.Empty(scan.Warnings);
    }

    [Theory]
    [InlineData("STALKER Anomaly")]
    [InlineData("S.T.A.L.K.E.R. Anomaly")]
    public void MapGameName_StalkerAnomaly(string gameName) =>
        Assert.Equal(GameType.StalkerAnomaly, Mo2Locator.MapGameName(gameName));

    [Fact]
    public void MapGameName_UnsupportedGame_IsNull() =>
        Assert.Null(Mo2Locator.MapGameName("Minecraft"));

    [Fact]
    public async Task EmptyFolder_NotAModpack_WithWarning()
    {
        var scan = await CreateScanner().ScanAsync(_root);

        Assert.False(scan.LooksLikeModpack);
        Assert.Contains(scan.Warnings, w => w.Contains("не похожа"));
    }

    [Fact]
    public async Task ScriptExtenderPreferred_OverBareGameExe()
    {
        Write(@"SkyrimSE.exe");
        Write(@"skse64_loader.exe");

        var scan = await CreateScanner().ScanAsync(_root);

        Assert.Equal("skse64_loader.exe", scan.LaunchExecutable);
    }

    [Fact]
    public void SteamVdfParsing_ExtractsLibraryPaths()
    {
        var vdf = Path.Combine(_root, "libraryfolders.vdf");
        File.WriteAllLines(vdf, new[]
        {
            "\"libraryfolders\"",
            "{",
            "    \"0\"",
            "    {",
            "        \"path\"        \"C:\\\\Program Files (x86)\\\\Steam\"",
            "    }",
            "    \"1\"",
            "    {",
            "        \"path\"        \"D:\\\\SteamLibrary\"",
            "    }",
            "}",
        });

        var paths = SystemGameLocator.ParseSteamLibraries(vdf).ToList();

        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\Program Files (x86)\Steam", paths);
        Assert.Contains(@"D:\SteamLibrary", paths);
    }

    [Fact]
    public void SteamLocator_FindsGameAcrossLibraries()
    {
        // Arrange: fake registry with Steam in a temp "library"
        var library = Directory.CreateDirectory(Path.Combine(_root, "SteamLibrary")).FullName;
        var gameDir = Directory.CreateDirectory(Path.Combine(library, "Skyrim Special Edition")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "SkyrimSE.exe"), string.Empty);

        var registry = new FakeRegistryService();
        registry.Strings[(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath")] =
            Path.Combine(_root, "steam-root");
        registry.SubKeys[(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\GOG.com\Games")] = [];

        var vdf = Path.Combine(_root, "steam-root", "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
        File.WriteAllText(vdf, $"\"libraryfolders\"\n{{\n  \"1\"\n  {{\n    \"path\"\t\t\"{library.Replace("\\", "\\\\")}\"\n  }}\n}}");

        var locator = new SystemGameLocator(registry, Serilog.Log.Logger);

        // Act
        var found = locator.FindGamePath(GameType.SkyrimSE);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(gameDir, found, ignoreCase: true, ignoreLineEndingDifferences: true);
        Assert.Equal(GameFolderName(GameType.SkyrimSE), SystemGameLocator.GameFolderName(GameType.SkyrimSE));
    }

    private static string GameFolderName(GameType _) => "Skyrim Special Edition";

    [Fact]
    public async Task Scan_CancelledToken_Throws()
    {
        var scanner = new ModpackScanner(
            new SystemGameLocator(new NullRegistryService(), Serilog.Log.Logger),
            Serilog.Log.Logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(_root, cts.Token));
    }
}

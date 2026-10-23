using System.IO;
using Imm.Core.Domain;
using Imm.Core.Ini;
using Imm.Core.Launch;
using Imm.Core.Scanning;
using Serilog;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>ILaunchStrategy rules (IMM.md): SE loader preferred, MO2 CLI `-p profile executable`, pre-launch checks.</summary>
public sealed class LaunchStrategyTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-launch-" + Guid.NewGuid().ToString("N"));
    private readonly DirectLaunchStrategy _direct = new();
    private readonly Mo2LaunchStrategy _mo2 = new(new ModOrganizerIniPatcher(), Log.Logger);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static ModpackManifest Manifest(LaunchEngine engine = LaunchEngine.Direct, PackSource source = PackSource.Portable, string profile = "Default")
    {
        var manifest = new ModpackManifest { Engine = engine, Source = source };
        if (profile.Length > 0)
        {
            manifest.Mo2 = new Mo2Section { ProfileName = profile };
        }

        return manifest;
    }

    private string CreatePack(string name, Action<string>? setup = null)
    {
        var pack = Path.Combine(_temp, name);
        Directory.CreateDirectory(pack);
        setup?.Invoke(pack);
        return pack;
    }

    private static void WriteGameExe(string dir) => File.WriteAllText(Path.Combine(dir, "Fallout4.exe"), string.Empty);

    [Theory]
    [InlineData(LaunchEngine.Direct, true)]
    [InlineData(LaunchEngine.Mo2Standard, false)]
    [InlineData(LaunchEngine.Mo2RootBuilder, false)]
    public void Direct_HandlesOnlyDirectEngine(LaunchEngine engine, bool expected) =>
        Assert.Equal(expected, _direct.Handles(engine));

    [Theory]
    [InlineData(LaunchEngine.Direct, false)]
    [InlineData(LaunchEngine.Mo2Standard, true)]
    [InlineData(LaunchEngine.Mo2RootBuilder, true)]
    public void Mo2_HandlesMo2Engines(LaunchEngine engine, bool expected) =>
        Assert.Equal(expected, _mo2.Handles(engine));

    [Fact]
    public async Task Direct_PortableGameRoot_UsesPortableFolder()
    {
        var pack = CreatePack("Portable", p =>
        {
            Directory.CreateDirectory(Path.Combine(p, "Stock Game"));
            WriteGameExe(Path.Combine(p, "Stock Game"));
        });

        var plan = await _direct.BuildPlanAsync(Manifest(), pack);

        Assert.Equal(Path.Combine(pack, "Stock Game", "Fallout4.exe"), plan.ExecutablePath);
        Assert.Equal(Path.Combine(pack, "Stock Game"), plan.WorkingDirectory);
        Assert.Empty(plan.Arguments);
    }

    [Fact]
    public async Task Direct_ScriptExtenderLoader_PreferredOverGameExe()
    {
        var pack = CreatePack("SE", p =>
        {
            WriteGameExe(p);
            File.WriteAllText(Path.Combine(p, "f4se_loader.exe"), string.Empty);
        });

        var plan = await _direct.BuildPlanAsync(Manifest(), pack);

        Assert.Equal("f4se_loader.exe", Path.GetFileName(plan.ExecutablePath));
    }

    [Fact]
    public async Task Direct_NoGame_Throws()
    {
        var pack = CreatePack("Empty");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _direct.BuildPlanAsync(Manifest(), pack));
        Assert.Contains("не найдена", ex.Message);
    }

    [Fact]
    public async Task Mo2_BuildsCli_WithProfileAndExecutable()
    {
        var pack = CreatePack("Mo2", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\FO4\n");
            Directory.CreateDirectory(Path.Combine(p, "Stock Game"));
            WriteGameExe(Path.Combine(p, "Stock Game"));
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, PackSource.Portable, "Vanilla+");

        var plan = await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Equal(Path.Combine(pack, "ModOrganizer.exe"), plan.ExecutablePath);
        Assert.Equal(pack, plan.WorkingDirectory);
        Assert.Equal(new[] { "-p", "Vanilla+", Path.Combine(pack, "Stock Game", "Fallout4.exe") }, plan.Arguments);
    }

    [Fact]
    public async Task Mo2_MissingOrganizer_Throws()
    {
        var pack = CreatePack("NoMo2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _mo2.BuildPlanAsync(Manifest(LaunchEngine.Mo2Standard), pack));
        Assert.Contains("ModOrganizer.exe", ex.Message);
    }

    [Fact]
    public async Task Mo2_NoProfile_Throws()
    {
        var pack = CreatePack("NoProfile", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\FO4\n");
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, profile: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _mo2.BuildPlanAsync(manifest, pack));
        Assert.Contains("Профиль", ex.Message);
    }

    [Fact]
    public async Task Mo2_RootBuilderEngine_UsesSameCli()
    {
        var pack = CreatePack("Rb", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\FO4\n");
            Directory.CreateDirectory(Path.Combine(p, "Stock Game"));
            WriteGameExe(Path.Combine(p, "Stock Game"));
        });
        var manifest = Manifest(LaunchEngine.Mo2RootBuilder, PackSource.Portable);

        var plan = await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Equal(new[] { "-p", "Default", Path.Combine(pack, "Stock Game", "Fallout4.exe") }, plan.Arguments);
    }

    [Fact]
    public async Task Mo2_StaleIni_PatchesToPortableGameRoot_WithBackup()
    {
        var pack = CreatePack("Stale", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\Gone\n");
            Directory.CreateDirectory(Path.Combine(p, "Stock Game"));
            WriteGameExe(Path.Combine(p, "Stock Game"));
        });

        await _mo2.BuildPlanAsync(Manifest(LaunchEngine.Mo2Standard), pack);

        var ini = File.ReadAllText(Path.Combine(pack, "ModOrganizer.ini"));
        Assert.Contains(Path.Combine(pack, "Stock Game"), ini);
        Assert.Single(Directory.GetFiles(pack, "ModOrganizer.ini.bak-*"));
    }

    [Fact]
    public async Task Mo2_NestedInstance_LaunchesFromSubdirectory()
    {
        // Game-as-root exception: game in the pack root, MO2 instance in MO2\.
        var pack = CreatePack("Nested", p =>
        {
            WriteGameExe(p);
            Directory.CreateDirectory(Path.Combine(p, "MO2"));
            File.WriteAllText(Path.Combine(p, "MO2", "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "MO2", "ModOrganizer.ini"), "[General]\ngamePath=@ByteArray(..\\\\)\n");
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, PackSource.Portable, "Default");
        manifest.Mo2!.Directory = "MO2";

        var plan = await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Equal(Path.Combine(pack, "MO2", "ModOrganizer.exe"), plan.ExecutablePath);
        Assert.Equal(Path.Combine(pack, "MO2"), plan.WorkingDirectory);
        Assert.Equal(new[] { "-p", "Default", Path.Combine(pack, "Fallout4.exe") }, plan.Arguments);
    }

    [Fact]
    public async Task Mo2_LoaderOutsideInstance_LaunchesByFullPath()
    {
        // FEHLauncher scheme (usvfs): MO2 resolves a bare name against its own directory,
        // so a loader living in the game root must be passed as a full path.
        var pack = CreatePack("OutsideLoader", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\FO4\n");
            Directory.CreateDirectory(Path.Combine(p, "Stock Game"));
            WriteGameExe(Path.Combine(p, "Stock Game"));
            File.WriteAllText(Path.Combine(p, "Stock Game", "f4se_loader.exe"), string.Empty);
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, PackSource.Portable, "Default");
        manifest.Launch.Executable = "f4se_loader.exe"; // scanner hint from the game root

        var plan = await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Equal(new[] { "-p", "Default", Path.Combine(pack, "Stock Game", "f4se_loader.exe") }, plan.Arguments);
    }

    [Fact]
    public async Task Mo2_RootBuilderModLayout_FindsLoaderInModRoot()
    {
        // Root Builder packs keep the loader in <mod>/Root — found even without a scanner hint.
        var pack = CreatePack("RbMods", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\FO4\n");
            Directory.CreateDirectory(Path.Combine(p, "Stock Game"));
            WriteGameExe(Path.Combine(p, "Stock Game"));
            var modRoot = Path.Combine(p, "mods", "Main Fallout Catalog", "Root");
            Directory.CreateDirectory(modRoot);
            File.WriteAllText(Path.Combine(modRoot, "f4se_loader.exe"), string.Empty);
        });
        var manifest = Manifest(LaunchEngine.Mo2RootBuilder, PackSource.Portable, "Default");

        var plan = await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Equal(
            new[] { "-p", "Default", Path.Combine(pack, "mods", "Main Fallout Catalog", "Root", "f4se_loader.exe") },
            plan.Arguments);
    }

    [Fact]
    public async Task Mo2_GamePathStale_ThrowsWithoutTouchingIni()
    {
        // gamePath belongs to MO2 2.5+ — stale value stops the launch, ini is never patched.
        var pack = CreatePack("GamePathStale", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\ngamePath=@ByteArray(C:\\\\Games\\\\Gone\\\\)\n");
            WriteGameExe(p);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _mo2.BuildPlanAsync(Manifest(LaunchEngine.Mo2Standard), pack));

        Assert.Contains("gamePath", ex.Message);
        Assert.Empty(Directory.GetFiles(pack, "ModOrganizer.ini.bak-*"));
        Assert.Contains("gamePath", File.ReadAllText(Path.Combine(pack, "ModOrganizer.ini")));
    }

    [Fact]
    public async Task Mo2_NoGameKey_IniLeftAlone()
    {
        // No base_directory / gamePath at all: MO2 applies its own defaults — ini must not be touched.
        var pack = CreatePack("NoKey", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\ngameName=Fallout 4\n");
            WriteGameExe(p);
        });

        var plan = await _mo2.BuildPlanAsync(Manifest(LaunchEngine.Mo2Standard), pack);

        Assert.Equal(Path.Combine(pack, "Fallout4.exe"), plan.Arguments[2]);
        Assert.Empty(Directory.GetFiles(pack, "ModOrganizer.ini.bak-*"));
    }

    [Fact]
    public async Task Mo2_SystemSource_GameMissing_ThrowsWithoutPatch()
    {
        var pack = CreatePack("SysMissing", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\Gone\n");
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, PackSource.System);
        manifest.SystemGamePath = @"C:\Games\AlsoGone";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _mo2.BuildPlanAsync(manifest, pack));
        Assert.Contains("не найдена", ex.Message);
        Assert.Empty(Directory.GetFiles(pack, "ModOrganizer.ini.bak-*"));
    }

    [Fact]
    public async Task Mo2_SystemSource_GamePresent_PatchesToSystemPath()
    {
        var gameDir = Path.Combine(_temp, "sysgame");
        Directory.CreateDirectory(gameDir);
        WriteGameExe(gameDir);
        var pack = CreatePack("Sys", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\nbase_directory=C:\\Games\\Gone\n");
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, PackSource.System);
        manifest.SystemGamePath = gameDir;

        await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Contains(gameDir, File.ReadAllText(Path.Combine(pack, "ModOrganizer.ini")));
    }

    [Fact]
    public async Task Mo2_StaleInstanceDirectory_FallsBackToRootInstance()
    {
        // Протухший Directory не должен прятать живой инстанс в корне сборки.
        var pack = CreatePack("StaleDir", p =>
        {
            File.WriteAllText(Path.Combine(p, "ModOrganizer.exe"), string.Empty);
            File.WriteAllText(Path.Combine(p, "ModOrganizer.ini"), "[General]\ngameName=Fallout 4\n");
            WriteGameExe(p);
        });
        var manifest = Manifest(LaunchEngine.Mo2Standard, PackSource.Portable, "Default");
        manifest.Mo2!.Directory = "Gone";

        var plan = await _mo2.BuildPlanAsync(manifest, pack);

        Assert.Equal(Path.Combine(pack, "ModOrganizer.exe"), plan.ExecutablePath);
    }
}

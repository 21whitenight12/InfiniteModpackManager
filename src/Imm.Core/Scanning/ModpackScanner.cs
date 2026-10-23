using System.IO;
using Imm.Core.Domain;
using Imm.Core.Ini;
using Imm.Core.IO;
using Serilog;

namespace Imm.Core.Scanning;

/// <summary>
/// Heuristic scanner (IMM.md Detection Markers). Reads only names/attributes of files and folders —
/// never file contents. Ambiguities become warnings surfaced in the UI, never silent guesses.
/// </summary>
public sealed class ModpackScanner : IModpackScanner
{
    private readonly SystemGameLocator _systemLocator;
    private readonly ILogger _logger;

    public ModpackScanner(SystemGameLocator systemLocator, ILogger logger)
    {
        _systemLocator = systemLocator;
        _logger = logger.ForContext<ModpackScanner>();
    }

    public Task<ScanResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanCore(rootPath, cancellationToken), cancellationToken);

    private ScanResult ScanCore(string rootPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var root = LongPath.Normalize(Path.GetFullPath(rootPath));

        if (!Directory.Exists(root))
        {
            _logger.Warning("Scan target does not exist: {Root}", root);
            return Fail(root, "Папка не существует.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rootGameType = DetectGameAt(root);
        var portableInfo = FindPortableGameRoot(root);

        // MO2 detection (portable instances only): in the pack root, or in exactly one
        // first-level subdirectory (game-as-root exception). More than one → no silent guessing.
        var nestedMo2Instances = Mo2Locator.FindSubdirectoryInstances(root);
        if (nestedMo2Instances.Count > 1)
        {
            warnings.Add(
                "Найдено несколько вложенных папок MO2 ("
                + string.Join(", ", nestedMo2Instances.Select(Path.GetFileName))
                + ") — укажите инстанс вручную.");
        }

        var mo2Directory = Mo2Locator.IsPortableInstance(root)
            ? root
            : nestedMo2Instances.Count == 1 ? nestedMo2Instances[0] : null;

        PackSource source;
        string? systemGamePath = null;
        string? gameFilesDirectory;
        GameType gameType;

        if (portableInfo is not null)
        {
            // Portable game root (Stock Game / Game Root / Game Files) wins over a stray root exe.
            source = PackSource.Portable;
            gameType = portableInfo.Value.Game;
            gameFilesDirectory = portableInfo.Value.Path;
            if (rootGameType != GameType.Unknown)
            {
                warnings.Add("Game exe found both in pack root and in a portable game folder — using the portable folder.");
            }
        }
        else if (rootGameType != GameType.Unknown)
        {
            if (mo2Directory is not null
                && !string.Equals(mo2Directory, root, StringComparison.OrdinalIgnoreCase))
            {
                // Game-as-root exception: the pack root is a full game install and the portable MO2
                // instance lives in exactly one first-level subdirectory (e.g. MO2\). The game ships
                // with the pack, so no system install lookup is needed.
                source = PackSource.Portable;
                gameType = rootGameType;
                gameFilesDirectory = root;
            }
            else
            {
                source = PackSource.System;
                gameType = rootGameType;
                gameFilesDirectory = root;
                systemGamePath = _systemLocator.FindGamePath(gameType);
                if (systemGamePath is null)
                {
                    warnings.Add("Системная установка игры не найдена — укажите путь к игре вручную.");
                }
                else
                {
                    // Script extender loaders live in the real game install, not in the pack.
                    gameFilesDirectory = systemGamePath;
                }
            }
        }
        else
        {
            source = PackSource.System;
            gameType = GameType.Unknown;
            gameFilesDirectory = null;

            // Fresh-import case: nothing in the folders, but MO2's own ini may know the game
            // (gameName + gamePath/base_directory written by MO2 itself).
            if (mo2Directory is not null)
            {
                var iniReader = new ModOrganizerIniPatcher();
                var iniPath = Path.Combine(mo2Directory, Markers.ModOrganizerIni);
                var knownGame = Mo2Locator.MapGameName(iniReader.ReadGameName(iniPath));
                if (knownGame is not null)
                {
                    gameType = knownGame.Value;
                    var managedGame = iniReader.ReadManagedGameDirectory(iniPath);
                    if (!string.IsNullOrEmpty(managedGame))
                    {
                        systemGamePath = managedGame;
                        gameFilesDirectory = managedGame;
                        if (!Directory.Exists(LongPath.Normalize(managedGame)))
                        {
                            warnings.Add(
                                $"ModOrganizer.ini указывает на несуществующую папку игры («{managedGame}») — выберите папку игры вручную.");
                        }
                    }
                    else
                    {
                        systemGamePath = _systemLocator.FindGamePath(gameType);
                        if (systemGamePath is null)
                        {
                            warnings.Add("Системная установка игры не найдена — укажите путь к игре вручную.");
                        }
                        else
                        {
                            gameFilesDirectory = systemGamePath;
                        }
                    }
                }
            }
        }

        // MO2 detection (portable instances only): ModOrganizer.exe next to ModOrganizer.ini,
        // in the pack root or in exactly one first-level subdirectory.
        var isNestedMo2 = mo2Directory is not null
            && !string.Equals(mo2Directory, root, StringComparison.OrdinalIgnoreCase);
        var mo2DirectoryName = isNestedMo2 ? Path.GetRelativePath(root, mo2Directory!) : null;

        LaunchEngine engine = LaunchEngine.Direct;
        string? mo2Profile = null;
        if (mo2Directory is not null)
        {
            engine = DetectRootBuilder(mo2Directory, warnings)
                ? LaunchEngine.Mo2RootBuilder
                : LaunchEngine.Mo2Standard;
            var iniPath = Path.Combine(mo2Directory, Markers.ModOrganizerIni);
            mo2Profile = new ModOrganizerIniPatcher().ReadSelectedProfile(iniPath);
            if (mo2Profile is not null)
            {
                // MO2's own last selection — a persisted fact, not a guess.
                _logger.Information("MO2 profile from ModOrganizer.ini selected_profile: {Profile}", mo2Profile);
            }
            else
            {
                mo2Profile = DetectMo2Profile(mo2Directory, warnings);
            }
        }

        // Launch executable: script extender loader preferred over the bare game exe (IMM.md).
        // Root Builder scheme: the pack's canonical loader lives in mods/<mod>/Root — resolve it
        // right here (fast single-pass parse) and persist the FULL path in the manifest. The game
        // folder may hold no loader at all (RB migrates root files into mods), so no preconditions
        // on the heuristic result.
        var launchExecutable = DetectLaunchExecutable(gameType, gameFilesDirectory, mo2Directory is not null, warnings);
        if (engine == LaunchEngine.Mo2RootBuilder && mo2Directory is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modsLoader = Mo2Locator.FindLoaderInMods(mo2Directory, gameType, cancellationToken);
            if (modsLoader is not null)
            {
                launchExecutable = modsLoader;
                _logger.Information("Root Builder loader resolved in mods: {Executable}", modsLoader);
            }
        }

        var looksLikeModpack = gameType != GameType.Unknown || mo2Directory is not null;
        if (!looksLikeModpack)
        {
            warnings.Add("Не найдено известных маркеров (exe игры, MO2) — папка не похожа на поддерживаемую сборку.");
        }

        _logger.Information(
            "Scan {Root}: game={GameType} engine={Engine} source={Source} profile={Profile} exe={Exe} warnings={Warnings}",
            root, gameType, engine, source, mo2Profile, launchExecutable, warnings.Count);

        return new ScanResult
        {
            RootPath = root,
            GameType = gameType,
            Engine = engine,
            Source = source,
            SystemGamePath = systemGamePath,
            Mo2ProfileName = mo2Profile,
            Mo2DirectoryName = mo2DirectoryName,
            LaunchExecutable = launchExecutable,
            LooksLikeModpack = looksLikeModpack,
            Warnings = warnings,
        };
    }

    private static ScanResult Fail(string root, string warning) => new()
    {
        RootPath = root,
        LooksLikeModpack = false,
        Warnings = [warning],
    };

    /// <summary>Game type by a well-known exe sitting directly in the directory; Unknown when none.</summary>
    private static GameType DetectGameAt(string directory)
    {
        foreach (var pair in Markers.GameExecutables)
        {
            if (File.Exists(LongPath.Normalize(Path.Combine(directory, pair.Key))))
            {
                return pair.Value;
            }
        }

        return GameType.Unknown;
    }

    /// <summary>Portable game root: Stock Game / Game Root / Game Files containing a game exe.</summary>
    private static (string Path, GameType Game)? FindPortableGameRoot(string root)
    {
        foreach (var name in Markers.PortableGameRootNames)
        {
            var candidate = Path.Combine(root, name);
            if (!Directory.Exists(LongPath.Normalize(candidate)))
            {
                continue;
            }

            var gameType = DetectGameAt(candidate);
            if (gameType != GameType.Unknown)
            {
                return (candidate, gameType);
            }
        }

        return null;
    }

    /// <summary>
    /// Root Builder ships in two forms: a C++ plugin (plugins/RootBuilder.dll) and a Python plugin
    /// (plugins/rootbuilder/). Plugin presence is the fact: root files may legitimately live in the
    /// game folder right now (Root Builder migrates them back on MO2 close and re-virtualizes on
    /// start), so scanning mods/ cannot change the answer and only burns I/O on big packs.
    /// </summary>
    private static bool DetectRootBuilder(string root, List<string> warnings)
    {
        var hasCppPlugin = File.Exists(LongPath.Normalize(
            Path.Combine(root, Markers.PluginsFolder, Markers.RootBuilderDll)));
        var hasPythonPlugin = Directory.Exists(LongPath.Normalize(
            Path.Combine(root, Markers.PluginsFolder, "rootbuilder")));
        if (!hasCppPlugin && !hasPythonPlugin)
        {
            return false;
        }

        if (!Directory.Exists(LongPath.Normalize(Path.Combine(root, Markers.ModsFolder))))
        {
            warnings.Add("Root Builder найден, но папка mods отсутствует — проверьте настройку сборки.");
        }

        return true;
    }

    internal static string GameExeNameFor(GameType gameType) => gameType switch
    {
        GameType.SkyrimLE => "TESV.exe",
        GameType.SkyrimSE => "SkyrimSE.exe",
        GameType.Fallout4 => "Fallout4.exe",
        GameType.FalloutNV => "FalloutNV.exe",
        _ => string.Empty,
    };

    /// <summary>Script extender loader for the game type; empty when unknown (any loader pass then).</summary>
    internal static string ScriptExtenderNameFor(GameType gameType) => gameType switch
    {
        GameType.SkyrimLE => "skse_loader.exe",
        GameType.SkyrimSE => "skse64_loader.exe",
        GameType.Fallout4 => "f4se_loader.exe",
        GameType.FalloutNV => "nvse_loader.exe",
        _ => string.Empty,
    };

    /// <summary>Exactly one profile folder → its name; otherwise null + warning (no silent guessing).</summary>
    private static string? DetectMo2Profile(string root, List<string> warnings)
    {
        var profilesDirectory = LongPath.Normalize(Path.Combine(root, Markers.ProfilesFolder));
        if (!Directory.Exists(profilesDirectory))
        {
            warnings.Add("Папка profiles у MO2 не найдена — выберите профиль вручную.");
            return null;
        }

        var profiles = Directory.EnumerateDirectories(profilesDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        switch (profiles.Count)
        {
            case 1:
                return profiles[0];
            case 0:
                return null; // fresh MO2 install; the user creates a profile in MO2 first
            default:
                warnings.Add($"Найдено несколько профилей ({string.Join(", ", profiles)}) — выберите нужный вручную.");
                return null;
        }
    }

    private static string? DetectLaunchExecutable(GameType gameType, string? gameFilesDirectory, bool hasMo2, List<string> warnings)
    {
        var gameExe = GameExeNameFor(gameType);
        if (gameExe.Length == 0)
        {
            if (hasMo2)
            {
                warnings.Add("Игра не распознана — укажите запускаемый exe вручную.");
            }

            return null;
        }

        if (gameFilesDirectory is not null)
        {
            foreach (var loader in Markers.ScriptExtenderLoaders)
            {
                if (File.Exists(LongPath.Normalize(Path.Combine(gameFilesDirectory, loader))))
                {
                    return loader;
                }
            }
        }

        return gameExe;
    }
}

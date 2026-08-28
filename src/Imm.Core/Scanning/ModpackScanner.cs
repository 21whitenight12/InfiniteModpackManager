using System.IO;
using Imm.Core.Domain;
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
        Task.Run(() => ScanCore(rootPath), cancellationToken);

    private ScanResult ScanCore(string rootPath)
    {
        var warnings = new List<string>();
        var root = LongPath.Normalize(Path.GetFullPath(rootPath));

        if (!Directory.Exists(root))
        {
            _logger.Warning("Scan target does not exist: {Root}", root);
            return Fail(root, "Папка не существует.");
        }

        var rootGameType = DetectGameAt(root);
        var portableInfo = FindPortableGameRoot(root);

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
        else
        {
            source = PackSource.System;
            gameType = GameType.Unknown;
            gameFilesDirectory = null;
        }

        // MO2 detection (portable instances only): ModOrganizer.exe next to ModOrganizer.ini.
        var hasMo2 = File.Exists(LongPath.Normalize(Path.Combine(root, Markers.ModOrganizerExe)))
                     && File.Exists(LongPath.Normalize(Path.Combine(root, Markers.ModOrganizerIni)));

        LaunchEngine engine = LaunchEngine.Direct;
        string? mo2Profile = null;
        if (hasMo2)
        {
            engine = DetectRootBuilder(root, gameType, warnings)
                ? LaunchEngine.Mo2RootBuilder
                : LaunchEngine.Mo2Standard;
            mo2Profile = DetectMo2Profile(root, warnings);
        }

        // Launch executable: script extender loader preferred over the bare game exe (IMM.md).
        var launchExecutable = DetectLaunchExecutable(gameType, gameFilesDirectory, hasMo2, warnings);

        var looksLikeModpack = gameType != GameType.Unknown || hasMo2;
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

    private static bool DetectRootBuilder(string root, GameType gameType, List<string> warnings)
    {
        var rootBuilderDll = Path.Combine(root, Markers.PluginsFolder, Markers.RootBuilderDll);
        if (!File.Exists(LongPath.Normalize(rootBuilderDll)))
        {
            return false;
        }

        // Root Builder signature per IMM.md: root plugin + game root files inside a mod folder.
        var modsDirectory = Path.Combine(root, Markers.ModsFolder);
        if (!Directory.Exists(LongPath.Normalize(modsDirectory)))
        {
            warnings.Add("RootBuilder.dll найден, но папка mods отсутствует — проверьте настройку сборки.");
            return true;
        }

        var gameFileNames = new List<string>();
        if (gameType != GameType.Unknown)
        {
            gameFileNames.Add(GameExeNameFor(gameType));
        }

        gameFileNames.AddRange(new[] { "binkw64.dll", "binkw32.dll" });

        foreach (var mod in Directory.EnumerateDirectories(LongPath.Normalize(modsDirectory)))
        {
            if (gameFileNames.Any(name => File.Exists(Path.Combine(mod, name))))
            {
                return true;
            }
        }

        warnings.Add("RootBuilder.dll найден, но корневые файлы игры в mods\\ не обнаружены — проверьте сборку.");
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

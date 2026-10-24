using System.IO;
using Imm.Core.Domain;
using Imm.Core.IO;

namespace Imm.Core.Scanning;

/// <summary>
/// Locates a portable MO2 instance (ModOrganizer.exe + ModOrganizer.ini). Two supported layouts:
/// classic — instance files in the pack root; game-as-root exception — instance in exactly one
/// first-level subdirectory (e.g. MO2\) next to the game install in the pack root.
/// </summary>
public static class Mo2Locator
{
    public static bool IsPortableInstance(string directory) =>
        File.Exists(LongPath.Normalize(Path.Combine(directory, Markers.ModOrganizerExe)))
        && File.Exists(LongPath.Normalize(Path.Combine(directory, Markers.ModOrganizerIni)));

    /// <summary>All first-level subdirectories holding a portable MO2 instance.</summary>
    public static IReadOnlyList<string> FindSubdirectoryInstances(string root)
    {
        var result = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(LongPath.Normalize(root)))
        {
            if (IsPortableInstance(directory))
            {
                result.Add(directory);
            }
        }

        return result;
    }

    /// <summary>
    /// Effective instance directory: the configured subdirectory (from the manifest) when valid,
    /// otherwise the pack root, otherwise a single nested instance; null when ambiguous or absent.
    /// </summary>
    public static string? ResolveInstance(string packRoot, string? configuredSubdirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredSubdirectory))
        {
            var candidate = Path.GetFullPath(Path.Combine(packRoot, configuredSubdirectory));
            if (IsPortableInstance(candidate))
            {
                return candidate;
            }

            // Протухший Directory не должен прятать живой инстанс: падаем на корень / единственный вложенный.
        }

        if (IsPortableInstance(packRoot))
        {
            return packRoot;
        }

        var instances = FindSubdirectoryInstances(packRoot);
        return instances.Count == 1 ? instances[0] : null;
    }

    /// <summary>Profile folder names under &lt;instance&gt;\profiles, ordered; empty when the folder is absent.</summary>
    public static IReadOnlyList<string> ListProfiles(string mo2Directory)
    {
        var profilesDirectory = LongPath.Normalize(Path.Combine(mo2Directory, Markers.ProfilesFolder));
        if (!Directory.Exists(profilesDirectory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(profilesDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>MO2 gameName → supported GameType; null for games IMM does not support.</summary>
    public static GameType? MapGameName(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return null;
        }

        return gameName.Trim() switch
        {
            "Skyrim" => GameType.SkyrimLE,
            "Skyrim Special Edition" => GameType.SkyrimSE,
            "Fallout 4" => GameType.Fallout4,
            "Fallout: New Vegas" or "Fallout New Vegas" => GameType.FalloutNV,
            "STALKER Anomaly" or "S.T.A.L.K.E.R. Anomaly" => GameType.StalkerAnomaly,
            _ => null,
        };
    }

    /// <summary>Game type by a well-known exe sitting directly in the directory; null when none.</summary>
    public static GameType? DetectGameByExe(string directory)
    {
        foreach (var pair in Markers.GameExecutables)
        {
            if (File.Exists(LongPath.Normalize(Path.Combine(directory, pair.Key))))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Script extender loader name, or the game exe name, found in the directory; null when neither.</summary>
    public static string? DetectLaunchExecutableName(string directory)
    {
        foreach (var loader in Markers.ScriptExtenderLoaders)
        {
            if (File.Exists(LongPath.Normalize(Path.Combine(directory, loader))))
            {
                return loader;
            }
        }

        var gameType = DetectGameByExe(directory);
        if (gameType is null)
        {
            return null;
        }

        var gameExe = ModpackScanner.GameExeNameFor(gameType.Value);
        return gameExe.Length == 0 ? null : gameExe;
    }

    /// <summary>
    /// Loader inside a mod folder: the game's own loader first, then any known loader;
    /// per pass &lt;mod&gt;/&lt;loader&gt; before the Root Builder layout &lt;mod&gt;/Root/&lt;loader&gt;.
    /// Returns a FULL path (Root Builder scheme: the pack's canonical loader).
    /// </summary>
    public static string? FindLoaderInMods(string mo2Directory, GameType gameType, CancellationToken cancellationToken = default)
    {
        var modsDirectory = Path.Combine(mo2Directory, Markers.ModsFolder);
        if (!Directory.Exists(LongPath.Normalize(modsDirectory)))
        {
            return null;
        }

        // Один проход по mods/ (реальные паки держат тысячи папок) и детерминированный порядок:
        // при нескольких кандидатах результат не должен зависеть от порядка файловой системы.
        var mods = Directory.EnumerateDirectories(LongPath.Normalize(modsDirectory))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var typedLoader = ModpackScanner.ScriptExtenderNameFor(gameType);
        var passes = string.IsNullOrEmpty(typedLoader)
            ? new List<IReadOnlyList<string>> { Markers.ScriptExtenderLoaders }
            : new List<IReadOnlyList<string>> { new[] { typedLoader }, Markers.ScriptExtenderLoaders };

        foreach (var pass in passes)
        {
            foreach (var loader in pass)
            {
                foreach (var mod in mods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var direct = Path.Combine(mod, loader);
                    if (File.Exists(LongPath.Normalize(direct)))
                    {
                        return Path.GetFullPath(direct);
                    }

                    var rootLayout = Path.Combine(mod, "Root", loader);
                    if (File.Exists(LongPath.Normalize(rootLayout)))
                    {
                        return Path.GetFullPath(rootLayout);
                    }
                }
            }
        }

        return null;
    }
}

using System.IO;
using Imm.Core.Domain;
using Imm.Core.Ini;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Serilog;
using Imm.Core.Localization;

namespace Imm.Core.Launch;

/// <summary>
/// MO2 launch: `ModOrganizer.exe -p &lt;profile&gt; &lt;executable&gt;` from the portable instance (IMM.md ILaunchStrategy).
/// Mo2RootBuilder shares this CLI — Root Builder migrates root files on MO2 start/close by itself;
/// the difference is in pre-launch checks, not arguments.
/// Two instance layouts (IMM.md Detection Markers): classic — MO2 files in the pack root;
/// game-as-root exception — the pack root is a full game install and MO2 lives in a first-level
/// subdirectory (Mo2.Directory in the manifest, e.g. "MO2").
/// </summary>
public sealed class Mo2LaunchStrategy : ILaunchStrategy
{
    private readonly ModOrganizerIniPatcher _patcher;
    private readonly ILogger _logger;

    public Mo2LaunchStrategy(ModOrganizerIniPatcher patcher, ILogger logger)
    {
        _patcher = patcher;
        _logger = logger.ForContext<Mo2LaunchStrategy>();
    }

    public bool Handles(LaunchEngine engine) => engine is LaunchEngine.Mo2Standard or LaunchEngine.Mo2RootBuilder;

    public Task<LaunchPlan> BuildPlanAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mo2Directory = ResolveMo2Directory(manifest, packRoot)
            ?? throw new InvalidOperationException(Loc.T("Err_Mo2ExeMissing"));

        var profile = manifest.Mo2?.ProfileName;
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException(Loc.T("Err_ProfileNotSelected"));
        }

        EnsureManagedGameUsable(manifest, packRoot, mo2Directory);

        var executable = ResolveExecutableName(manifest, packRoot, mo2Directory, cancellationToken)
            ?? throw new InvalidOperationException(Loc.T("Err_LaunchExeUnknown"));

        var mo2Exe = Path.Combine(mo2Directory, Markers.ModOrganizerExe);
        return Task.FromResult(new LaunchPlan(
            mo2Exe, mo2Directory, BuildArguments(mo2Directory, profile, executable), Elevate: true));
    }

    /// <summary>
    /// `-p &lt;profile&gt;` is passed only when MO2 does not already have that profile selected (MO2 uses
    /// its own selected_profile otherwise). MO2 2.5.2 crashes (0xC0000005) when -p carries a non-ASCII
    /// name — verified live on G.A.M.M.A's «3 - G.A.M.M.A - ALL DOP+Графпак+GFY» — so such a choice is
    /// written into the ini instead (backup first), exactly the way MO2 itself stores a profile switch.
    /// </summary>
    private IReadOnlyList<string> BuildArguments(string mo2Directory, string profile, string executable)
    {
        var arguments = new List<string>(3);
        var iniPath = Path.Combine(mo2Directory, Markers.ModOrganizerIni);
        var iniExists = File.Exists(LongPath.Normalize(iniPath));
        var selected = iniExists ? _patcher.ReadSelectedProfile(iniPath) : null;

        if (!string.Equals(selected, profile, StringComparison.OrdinalIgnoreCase))
        {
            if (IsAscii(profile) || !iniExists)
            {
                arguments.Add("-p");
                arguments.Add(profile);
            }
            else
            {
                var patch = _patcher.PatchSelectedProfile(iniPath, profile);
                _logger.Information(
                    "MO2 profile '{Profile}' is non-ASCII: selected through the ini instead of -p (backup {Backup})",
                    profile, patch.BackupPath);
            }
        }

        arguments.Add(executable);
        return arguments;
    }

    private static bool IsAscii(string value) => value.All(character => character < 128);

    /// <summary>Manifest-configured instance directory first; then root, then a single nested instance.</summary>
    private static string? ResolveMo2Directory(ModpackManifest manifest, string packRoot) =>
        Mo2Locator.ResolveInstance(packRoot, manifest.Mo2?.Directory);

    /// <summary>
    /// FEHLauncher scheme: MO2 resolves a bare executable name against its own directory (its cwd),
    /// so any executable living elsewhere must be launched by FULL path (usvfs "file not found" otherwise).
    /// Priority: the persisted full path from the manifest (Root Builder scheme — resolved at scan),
    /// validated live; then mod folders; then the hint expanded to a real location; then game roots.
    /// </summary>
    private string? ResolveExecutableName(ModpackManifest manifest, string packRoot, string mo2Directory,
        CancellationToken cancellationToken)
    {
        var searchDirectories = CollectSearchDirectories(manifest, packRoot, mo2Directory);

        // Persisted full path (scan-time Root Builder resolution): use while the file still exists.
        var hint = manifest.Launch.Executable;
        if (!string.IsNullOrWhiteSpace(hint) && Path.IsPathRooted(hint) && File.Exists(LongPath.Normalize(hint)))
        {
            return Path.GetFullPath(hint);
        }

        var fromMods = Mo2Locator.FindLoaderInMods(mo2Directory, manifest.GameType, cancellationToken);
        if (fromMods is not null)
        {
            _logger.Information("Launch executable found in mods folder: {Executable}", fromMods);
            return fromMods;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            var expanded = ExpandHint(hint, mo2Directory, searchDirectories);
            if (expanded is not null)
            {
                return expanded;
            }
        }

        foreach (var directory in searchDirectories)
        {
            foreach (var loader in Markers.ScriptExtenderLoaders)
            {
                if (File.Exists(LongPath.Normalize(Path.Combine(directory, loader))))
                {
                    return ToMo2Argument(directory, loader);
                }
            }
        }

        var typedExe = ModpackScanner.GameExeNameFor(manifest.GameType);
        foreach (var directory in searchDirectories)
        {
            if (typedExe.Length > 0 && File.Exists(LongPath.Normalize(Path.Combine(directory, typedExe))))
            {
                return ToMo2Argument(directory, typedExe);
            }
        }

        foreach (var directory in searchDirectories)
        {
            var anyExe = Markers.GameExecutables.Keys
                .FirstOrDefault(exe => File.Exists(LongPath.Normalize(Path.Combine(directory, exe))));
            if (anyExe is not null)
            {
                return ToMo2Argument(directory, anyExe);
            }
        }

        return null;
    }

    /// <summary>Effective game roots: ini-resolved game folder, the pack's own game root, the detected install.</summary>
    private List<string> CollectSearchDirectories(ModpackManifest manifest, string packRoot, string mo2Directory)
    {
        var searchDirectories = new List<string>();
        var iniPath = Path.Combine(mo2Directory, Markers.ModOrganizerIni);
        var candidates = new[]
        {
            File.Exists(LongPath.Normalize(iniPath)) ? _patcher.ReadManagedGameDirectory(iniPath) : null,
            GameRootLocator.FindGameRoot(packRoot),
            manifest.SystemGamePath,
        };
        foreach (var candidate in candidates)
        {
            if (candidate is not null
                && Directory.Exists(LongPath.Normalize(candidate))
                && !searchDirectories.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                searchDirectories.Add(candidate);
            }
        }

        return searchDirectories;
    }

    /// <summary>
    /// Manifest hint → launch argument: the first location that actually contains the file,
    /// always as a full path (MO2 resolves bare names against its own directory only).
    /// </summary>
    private static string? ExpandHint(string hint, string mo2Directory, List<string> searchDirectories)
    {
        if (Path.IsPathRooted(hint))
        {
            return File.Exists(LongPath.Normalize(hint)) ? Path.GetFullPath(hint) : null;
        }

        var candidates = searchDirectories.Concat([mo2Directory]);
        foreach (var directory in candidates)
        {
            var candidate = Path.Combine(directory, hint);
            if (File.Exists(LongPath.Normalize(candidate)))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>Always a full path: MO2 resolves bare names against its own directory only (usvfs).</summary>
    private static string ToMo2Argument(string directory, string fileName) =>
        Path.GetFullPath(Path.Combine(directory, fileName));

    /// <summary>
    /// Self-healing paths (IMM.md MVP #4), MO2-version aware:
    /// - legacy `base_directory` (absolute) pointing nowhere → patched to the real game folder (timestamped backup first);
    /// - MO2 2.5+ `gamePath`: a stale value stops the launch with a clear message; a value pointing at
    ///   another install while the pack carries its own game is switched to that game (backup first).
    /// No game-folder key at all → MO2 applies its own defaults; the ini is not touched.
    /// </summary>
    private void EnsureManagedGameUsable(ModpackManifest manifest, string packRoot, string mo2Directory)
    {
        var iniPath = Path.Combine(mo2Directory, Markers.ModOrganizerIni);
        if (!File.Exists(LongPath.Normalize(iniPath)))
        {
            return;
        }

        var baseDirectory = _patcher.ReadBaseDirectory(iniPath);
        if (baseDirectory is not null)
        {
            if (Directory.Exists(LongPath.Normalize(baseDirectory)) || !Path.IsPathRooted(baseDirectory))
            {
                // Working key, or a relative legacy path MO2 resolves itself (IMM.md: relative paths untouched).
                return;
            }

            var actualGame = ResolveActualGame(manifest, packRoot)
                ?? throw new InvalidOperationException(
                    Loc.T("Err_GameFolderMissing"));

            _logger.Information(
                "MO2 base_directory {Old} is missing; patching ModOrganizer.ini to {New}",
                baseDirectory, actualGame);
            _patcher.PatchBaseDirectory(iniPath, actualGame);
            return;
        }

        var gamePath = _patcher.ReadManagedGameDirectory(iniPath);
        if (gamePath is null)
        {
            // No key: MO2 2.5+ falls back to its own default (the instance directory) — leave it alone.
            return;
        }

        if (!Directory.Exists(LongPath.Normalize(gamePath)))
        {
            throw new InvalidOperationException(
                Loc.F("Err_IniGamePathMissing", gamePath));
        }

        // MO2 resolves the game from its own ini, so a gamePath left over from another install
        // (typical after the pack folder was moved) would mount the wrong Data folder — missing
        // masters, error, crash. When the pack ships its own game, MO2 is switched to it.
        var packGame = ResolveOwnGame(manifest, packRoot);
        if (packGame is not null && !SamePath(gamePath, packGame))
        {
            _logger.Information(
                "MO2 gamePath {Old} points at another install; switching to the pack's own game {New}",
                gamePath, packGame);
            _patcher.PatchGamePath(iniPath, packGame);
        }
    }

    /// <summary>The game folder that ships inside the pack (Portable source); null for system-install packs.</summary>
    private static string? ResolveOwnGame(ModpackManifest manifest, string packRoot)
    {
        if (manifest.Source != PackSource.Portable || string.IsNullOrEmpty(manifest.SystemGamePath))
        {
            return null;
        }

        return Directory.Exists(LongPath.Normalize(manifest.SystemGamePath)) && IsInside(packRoot, manifest.SystemGamePath)
            ? manifest.SystemGamePath
            : null;
    }

    /// <summary>True when the candidate sits inside the pack root.</summary>
    private static bool IsInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
    {
        static string Canon(string path) =>
            Path.GetFullPath(LongPath.Normalize(path)).TrimEnd('\\', '/').Replace('/', '\\');

        return string.Equals(Canon(left), Canon(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A known absolute game folder wins (portable packs carry it too — e.g. G.A.M.M.A keeps the game
    /// in a sibling folder of the MO2 instance); otherwise the pack's own game root.
    /// </summary>
    private static string? ResolveActualGame(ModpackManifest manifest, string packRoot)
    {
        return !string.IsNullOrEmpty(manifest.SystemGamePath)
            && Directory.Exists(LongPath.Normalize(manifest.SystemGamePath))
            ? manifest.SystemGamePath
            : GameRootLocator.FindGameRoot(packRoot);
    }
}

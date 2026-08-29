using System.IO;
using Imm.Core.Domain;
using Imm.Core.Ini;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Serilog;

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
            ?? throw new InvalidOperationException("ModOrganizer.exe не найден — поддерживаются только portable-инстансы MO2.");

        var profile = manifest.Mo2?.ProfileName;
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException("Профиль MO2 не выбран — выберите профиль и выполните ре-скан.");
        }

        EnsureManagedGameUsable(manifest, packRoot, mo2Directory);

        var executable = ResolveExecutableName(manifest, packRoot, mo2Directory)
            ?? throw new InvalidOperationException("Не удалось определить запускаемый exe игры — проверьте сборку или выполните ре-скан.");

        var mo2Exe = Path.Combine(mo2Directory, Markers.ModOrganizerExe);
        return Task.FromResult(new LaunchPlan(mo2Exe, mo2Directory, ["-p", profile, executable]));
    }

    /// <summary>Manifest-configured instance directory first; then root, then a single nested instance.</summary>
    private static string? ResolveMo2Directory(ModpackManifest manifest, string packRoot)
    {
        var configured = manifest.Mo2?.Directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.GetFullPath(Path.Combine(packRoot, configured));
            return Mo2Locator.IsPortableInstance(candidate) ? candidate : null;
        }

        // Manifests written before the Directory field existed.
        if (Mo2Locator.IsPortableInstance(packRoot))
        {
            return packRoot;
        }

        var instances = Mo2Locator.FindSubdirectoryInstances(packRoot);
        return instances.Count == 1 ? instances[0] : null;
    }

    /// <summary>
    /// Scanner-resolved executable first; otherwise re-locate the loader/game exe without touching
    /// the manifest. Searches the effective game root, the ini-resolved game folder and (for system
    /// packs) the detected install — the game type may be Unknown there, so known exe names are probed directly.
    /// </summary>
    private string? ResolveExecutableName(ModpackManifest manifest, string packRoot, string mo2Directory)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Launch.Executable))
        {
            return manifest.Launch.Executable;
        }

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

        foreach (var directory in searchDirectories)
        {
            foreach (var loader in Markers.ScriptExtenderLoaders)
            {
                if (File.Exists(LongPath.Normalize(Path.Combine(directory, loader))))
                {
                    return loader;
                }
            }
        }

        var typedExe = ModpackScanner.GameExeNameFor(manifest.GameType);
        foreach (var directory in searchDirectories)
        {
            if (typedExe.Length > 0 && File.Exists(LongPath.Normalize(Path.Combine(directory, typedExe))))
            {
                return typedExe;
            }
        }

        foreach (var directory in searchDirectories)
        {
            var anyExe = Markers.GameExecutables.Keys
                .FirstOrDefault(exe => File.Exists(LongPath.Normalize(Path.Combine(directory, exe))));
            if (anyExe is not null)
            {
                return anyExe;
            }
        }

        return null;
    }

    /// <summary>
    /// Self-healing paths (IMM.md MVP #4), MO2-version aware:
    /// - legacy `base_directory` (absolute) pointing nowhere → patched to the real game folder (timestamped backup first);
    /// - MO2 2.5+ `gamePath` belongs to MO2 — never patched; a stale value stops the launch with a clear message.
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
                    "Папка игры не найдена: base_directory в ModOrganizer.ini указывает на несуществующий путь, а игровой корень в сборке отсутствует.");

            _logger.Information(
                "MO2 base_directory {Old} is missing; patching ModOrganizer.ini to {New}",
                baseDirectory, actualGame);
            _patcher.PatchBaseDirectory(iniPath, actualGame);
            return;
        }

        var gamePath = _patcher.ReadManagedGameDirectory(iniPath);
        if (gamePath is null || Directory.Exists(LongPath.Normalize(gamePath)))
        {
            // No key (MO2 2.5+ default = instance directory) or the path resolves fine.
            return;
        }

        throw new InvalidOperationException(
            $"gamePath в ModOrganizer.ini указывает на несуществующую папку («{gamePath}») — исправьте путь к игре в MO2 вручную.");
    }

    /// <summary>System packs heal towards the detected Steam/GOG install; portable packs use their own game folder.</summary>
    private static string? ResolveActualGame(ModpackManifest manifest, string packRoot)
    {
        return manifest.Source == PackSource.System
            && !string.IsNullOrEmpty(manifest.SystemGamePath)
            && Directory.Exists(LongPath.Normalize(manifest.SystemGamePath))
            ? manifest.SystemGamePath
            : GameRootLocator.FindGameRoot(packRoot);
    }
}

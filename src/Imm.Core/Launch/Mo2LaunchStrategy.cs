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

        if (!File.Exists(LongPath.Normalize(Path.Combine(packRoot, Markers.ModOrganizerExe))))
        {
            throw new InvalidOperationException("ModOrganizer.exe не найден — поддерживаются только portable-инстансы MO2.");
        }

        var profile = manifest.Mo2?.ProfileName;
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException("Профиль MO2 не выбран — выберите профиль и выполните ре-скан.");
        }

        var executable = EnsureGameRootUsable(manifest, packRoot) is { } managedGameRoot
            ? ResolveExecutableName(manifest, packRoot, managedGameRoot)
                ?? throw new InvalidOperationException("Не удалось определить запускаемый exe игры — проверьте сборку или выполните ре-скан.")
            : throw new InvalidOperationException("ModOrganizer.ini не найден — поддерживаются только portable-инстансы MO2.");

        var mo2Exe = Path.Combine(packRoot, Markers.ModOrganizerExe);
        return Task.FromResult(new LaunchPlan(mo2Exe, packRoot, ["-p", profile, executable]));
    }

    /// <summary>
    /// Scanner-resolved executable first; otherwise re-locate the loader/game exe without touching
    /// the manifest. Searches the effective game root and (for system packs) the detected install —
    /// the game type may be Unknown there, so known exe names are probed directly.
    /// </summary>
    private static string? ResolveExecutableName(ModpackManifest manifest, string packRoot, string? managedGameRoot)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Launch.Executable))
        {
            return manifest.Launch.Executable;
        }

        var searchDirectories = new List<string>();
        foreach (var candidate in new[] { managedGameRoot, GameRootLocator.FindGameRoot(packRoot), manifest.SystemGamePath })
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
    /// Self-healing paths (IMM.md MVP #4): when ModOrganizer.ini points to a non-existent game
    /// location, patch it (timestamped backup first) to the real game folder.
    /// Returns the effective game root when the ini is already valid, null when the ini is missing.
    /// </summary>
    private string? EnsureGameRootUsable(ModpackManifest manifest, string packRoot)
    {
        var iniPath = Path.Combine(packRoot, Markers.ModOrganizerIni);
        if (!File.Exists(LongPath.Normalize(iniPath)))
        {
            return null;
        }

        var managedGame = _patcher.ReadBaseDirectory(iniPath);
        if (!string.IsNullOrEmpty(managedGame) && Directory.Exists(LongPath.Normalize(managedGame)))
        {
            return managedGame;
        }

        // System packs launch the detected Steam/GOG install; portable packs use their own game folder.
        var actualGame = manifest.Source == PackSource.System
            && !string.IsNullOrEmpty(manifest.SystemGamePath)
            && Directory.Exists(LongPath.Normalize(manifest.SystemGamePath))
            ? manifest.SystemGamePath
            : GameRootLocator.FindGameRoot(packRoot);

        if (actualGame is null)
        {
            throw new InvalidOperationException(
                "Папка игры не найдена: ModOrganizer.ini указывает на несуществующий путь, а игровой корень в сборке отсутствует.");
        }

        _logger.Information(
            "MO2 base_directory {Old} is missing; patching ModOrganizer.ini to {New}",
            managedGame ?? "<absent>", actualGame);

        _patcher.PatchBaseDirectory(iniPath, actualGame);
        return actualGame;
    }
}

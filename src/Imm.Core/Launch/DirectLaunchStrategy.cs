using System.IO;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Imm.Core.Localization;

namespace Imm.Core.Launch;

/// <summary>Direct launch: game exe or script extender loader from the pack / portable game root (IMM.md MVP #3).</summary>
public sealed class DirectLaunchStrategy : ILaunchStrategy
{
    public bool Handles(LaunchEngine engine) => engine == LaunchEngine.Direct;

    public Task<LaunchPlan> BuildPlanAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var gameRoot = GameRootLocator.FindGameRoot(packRoot)
            ?? throw new InvalidOperationException(Loc.T("Err_GameFolderGone"));

        // Script extender loader preferred over the bare game exe (IMM.md ILaunchStrategy).
        // Лоадер СВОЕЙ игры приоритетнее прочих: порядок общего списка — не приоритет.
        var typedLoader = ModpackScanner.ScriptExtenderNameFor(manifest.GameType);
        var loaders = typedLoader.Length == 0
            ? Markers.ScriptExtenderLoaders
            : new[] { typedLoader }.Concat(Markers.ScriptExtenderLoaders).ToArray();

        foreach (var loader in loaders)
        {
            if (File.Exists(LongPath.Normalize(Path.Combine(gameRoot, loader))))
            {
                return Task.FromResult(new LaunchPlan(Path.Combine(gameRoot, loader), gameRoot, []));
            }
        }

        var gameExe = Markers.GameExecutables.Keys
            .FirstOrDefault(exe => File.Exists(LongPath.Normalize(Path.Combine(gameRoot, exe))))
            ?? throw new InvalidOperationException(Loc.T("Err_LaunchExeUnknown"));

        return Task.FromResult(new LaunchPlan(Path.Combine(gameRoot, gameExe), gameRoot, []));
    }
}

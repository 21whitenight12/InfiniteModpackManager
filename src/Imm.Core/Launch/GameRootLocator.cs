using System.IO;
using Imm.Core.IO;
using Imm.Core.Scanning;

namespace Imm.Core.Launch;

/// <summary>
/// Locates the actual game folder inside a pack, mirroring the scanner precedence:
/// portable game root (Stock Game / Game Root / Game Files) wins over a game exe in the pack root.
/// </summary>
internal static class GameRootLocator
{
    /// <summary>Portable game folder, or the pack root when it directly holds a game exe; null when neither.</summary>
    public static string? FindGameRoot(string packRoot)
    {
        foreach (var name in Markers.PortableGameRootNames)
        {
            var candidate = Path.Combine(packRoot, name);
            if (Directory.Exists(LongPath.Normalize(candidate)) && ContainsGameExe(candidate))
            {
                return candidate;
            }
        }

        return ContainsGameExe(packRoot) ? packRoot : null;
    }

    public static bool ContainsGameExe(string directory)
    {
        foreach (var exe in Markers.GameExecutables.Keys)
        {
            if (File.Exists(LongPath.Normalize(Path.Combine(directory, exe))))
            {
                return true;
            }
        }

        return false;
    }
}

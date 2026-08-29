using System.IO;
using Imm.Core.IO;

namespace Imm.Core.Scanning;

/// <summary>
/// Locates a portable MO2 instance (ModOrganizer.exe + ModOrganizer.ini). Two supported layouts:
/// classic — instance files in the pack root; game-as-root exception — instance in exactly one
/// first-level subdirectory (e.g. MO2\) next to the game install in the pack root.
/// </summary>
internal static class Mo2Locator
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
}

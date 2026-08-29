using System.IO;
using Imm.Core.Domain;

namespace Imm.Core.Scanning;

/// <summary>Well-known file names used for detection (IMM.md Detection Markers).</summary>
internal static class Markers
{
    public static readonly IReadOnlyDictionary<string, GameType> GameExecutables = new Dictionary<string, GameType>(StringComparer.OrdinalIgnoreCase)
    {
        ["TESV.exe"] = GameType.SkyrimLE,
        ["SkyrimSE.exe"] = GameType.SkyrimSE,
        ["Fallout4.exe"] = GameType.Fallout4,
        ["FalloutNV.exe"] = GameType.FalloutNV,
    };

    public static readonly IReadOnlyList<string> ScriptExtenderLoaders = new[]
    {
        "skse64_loader.exe", // Skyrim SE
        "skse_loader.exe",   // Skyrim LE
        "f4se_loader.exe",   // Fallout 4
        "nvse_loader.exe",   // Fallout NV
    };

    public const string ModOrganizerExe = "ModOrganizer.exe";
    public const string ModOrganizerIni = "ModOrganizer.ini";
    public const string RootBuilderDll = "RootBuilder.dll";
    public const string ModsFolder = "mods";
    public const string PluginsFolder = "plugins";
    public const string ProfilesFolder = "profiles";
    public const string GameDataFolder = "Data";

    public static readonly IReadOnlyList<string> PortableGameRootNames = new[]
    {
        "Stock Game",
        "Game Root",
        "Game Files",
    };
}

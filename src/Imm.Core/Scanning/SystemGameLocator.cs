using System.IO;
using Imm.Core.Domain;
using Imm.Core.IO;
using Microsoft.Win32;
using Serilog;

namespace Imm.Core.Scanning;

/// <summary>
/// Finds system game installs: Steam (HKCU SteamPath + steamapps\libraryfolders.vdf) and
/// GOG (HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\*\path). Detection is name-only:
/// known game folder + executable per library — no invented store appIds (IMM.md Detection Markers).
/// </summary>
public sealed class SystemGameLocator
{
    private readonly IRegistryService _registry;
    private readonly ILogger _logger;

    public SystemGameLocator(IRegistryService registry, ILogger logger)
    {
        _registry = registry;
        _logger = logger.ForContext<SystemGameLocator>();
    }

    /// <summary>Returns the absolute game install directory, or null when not found.</summary>
    public string? FindGamePath(GameType gameType)
    {
        var executable = GameExecutableName(gameType);
        if (executable is null)
        {
            return null;
        }

        // FEHLauncher-style cascade: Bethesda registry (64/32-bit) → Steam → GOG.
        var bethesda64 = FindBethesdaGame(gameType, executable, RegistryView.Registry64);
        if (bethesda64 is not null)
        {
            _logger.Information("Bethesda registry (64) install found for {GameType}: {Path}", gameType, bethesda64);
            return bethesda64;
        }

        var bethesda32 = FindBethesdaGame(gameType, executable, RegistryView.Registry32);
        if (bethesda32 is not null)
        {
            _logger.Information("Bethesda registry (32) install found for {GameType}: {Path}", gameType, bethesda32);
            return bethesda32;
        }

        var steam = FindSteamGame(gameType, executable);
        if (steam is not null)
        {
            _logger.Information("Steam install found for {GameType}: {Path}", gameType, steam);
            return steam;
        }

        var gog = FindGogGame(executable);
        if (gog is not null)
        {
            _logger.Information("GOG install found for {GameType}: {Path}", gameType, gog);
            return gog;
        }

        _logger.Information("No system install found for {GameType}.", gameType);
        return null;
    }

    /// <summary>Bethesda launcher-era registry keys (HKLM\SOFTWARE\Bethesda Softworks\...), exe validated.</summary>
    private string? FindBethesdaGame(GameType gameType, string executable, RegistryView view)
    {
        var subKey = $@"SOFTWARE\Bethesda Softworks\{BethesdaKeyName(gameType)}";
        var path = _registry.GetString(RegistryHive.LocalMachine, view, subKey, "Installed Path");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return File.Exists(LongPath.Normalize(Path.Combine(path, executable))) ? path : null;
    }

    /// <summary>Bethesda registry key names are stable store facts, kept name-only.</summary>
    internal static string? BethesdaKeyName(GameType gameType) => gameType switch
    {
        GameType.SkyrimLE => "Skyrim",
        GameType.SkyrimSE => "Skyrim Special Edition",
        GameType.Fallout4 => "Fallout4",
        GameType.FalloutNV => "FalloutNV",
        _ => null,
    };

    private string? FindSteamGame(GameType gameType, string executable)
    {
        var steamPath = _registry.GetString(
            RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath");
        if (string.IsNullOrEmpty(steamPath))
        {
            return null;
        }

        var steamRoot = Path.GetFullPath(steamPath.Replace('/', '\\'));
        var libraries = new List<string> { steamRoot };
        libraries.AddRange(ParseSteamLibraries(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf")));

        var gameFolder = GameFolderName(gameType);
        if (gameFolder is null)
        {
            return null;
        }

        foreach (var library in libraries)
        {
            var candidate = Path.Combine(library, gameFolder, executable);
            if (File.Exists(LongPath.Normalize(candidate)))
            {
                return Path.Combine(library, gameFolder);
            }
        }

        return null;
    }

    /// <summary>Minimal parser: extracts "path" values from libraryfolders.vdf (quotes and \\ escapes only).</summary>
    internal static IEnumerable<string> ParseSteamLibraries(string vdfPath)
    {
        if (!File.Exists(vdfPath))
        {
            yield break;
        }

        foreach (var rawLine in File.ReadAllLines(vdfPath))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = line["\"path\"".Length..].TrimStart();
            if (rest.Length < 2 || rest[0] != '"')
            {
                continue;
            }

            var end = rest.IndexOf('"', 1);
            if (end < 0)
            {
                continue;
            }

            yield return rest[1..end].Replace("\\\\", "\\");
        }
    }

    private string? FindGogGame(string executable)
    {
        const string gamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";
        foreach (var subKey in _registry.GetSubKeyNames(RegistryHive.LocalMachine, RegistryView.Registry64, gamesKey))
        {
            var path = _registry.GetString(RegistryHive.LocalMachine, RegistryView.Registry64, $@"{gamesKey}\{subKey}", "path");
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (File.Exists(LongPath.Normalize(Path.Combine(path, executable))))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Steam library folder names are stable store facts, kept name-only to avoid appIds.</summary>
    internal static string? GameFolderName(GameType gameType) => gameType switch
    {
        GameType.SkyrimLE => "Skyrim",
        GameType.SkyrimSE => "Skyrim Special Edition",
        GameType.Fallout4 => "Fallout 4",
        GameType.FalloutNV => "Fallout New Vegas",
        _ => null,
    };

    internal static string? GameExecutableName(GameType gameType) => gameType switch
    {
        GameType.SkyrimLE => "TESV.exe",
        GameType.SkyrimSE => "SkyrimSE.exe",
        GameType.Fallout4 => "Fallout4.exe",
        GameType.FalloutNV => "FalloutNV.exe",
        _ => null,
    };
}

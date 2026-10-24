using System.IO;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Scanning;
using Serilog;

namespace Imm.Core.Diagnostics;

/// <summary>
/// Builds the Scanner screen data: runs the heuristic scanner (read-only, never saves) and puts the
/// fresh facts next to the stored manifest, so a stale or half-broken pack is visible before launch.
/// </summary>
public sealed class PackDiagnosticsService : IPackDiagnosticsService
{
    private readonly IModpackScanner _scanner;
    private readonly ILogger _logger;

    public PackDiagnosticsService(IModpackScanner scanner, ILogger logger)
    {
        _scanner = scanner;
        _logger = logger.ForContext<PackDiagnosticsService>();
    }

    public async Task<PackDiagnostics> AnalyzeAsync(LibraryEntry entry, ModpackManifest? manifest,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(LongPath.Normalize(entry.RootPath)))
        {
            return new PackDiagnostics
            {
                EntryId = entry.Id,
                Name = entry.Name,
                RootPath = entry.RootPath,
                Summary = "папка не найдена",
                RootExists = false,
                ManifestMissing = manifest is null,
                Warnings = ["Папка сборки не найдена на диске — проверьте путь или уберите сборку из библиотеки."],
            };
        }

        var scan = await _scanner.ScanAsync(entry.RootPath, cancellationToken).ConfigureAwait(false);
        var instance = Mo2Locator.ResolveInstance(entry.RootPath, manifest?.Mo2?.Directory);

        var warnings = new List<string>(scan.Warnings);
        if (manifest is null)
        {
            warnings.Add("modpack.json отсутствует или повреждён — сборка показывается как не сканированная.");
        }

        var stale = manifest is not null && DiffersFromScan(scan, manifest);
        if (stale)
        {
            warnings.Add("Манифест расходится с текущим сканом — выполните ре-скан, чтобы применить факты.");
        }

        _logger.Information(
            "Diagnostics {Pack}: game={Game} engine={Engine} warnings={Warnings} stale={Stale}",
            entry.Name, scan.GameType, scan.Engine, warnings.Count, stale);

        return new PackDiagnostics
        {
            EntryId = entry.Id,
            Name = entry.Name,
            RootPath = entry.RootPath,
            Summary = $"{Labels.Engine(scan.Engine, scan.Source)} · {Labels.Game(scan.GameType)}",
            RootExists = true,
            ManifestMissing = manifest is null,
            ManifestStale = stale,
            Detected = BuildDetected(scan, instance),
            Stored = manifest is null ? [] : BuildStored(manifest, entry.RootPath, instance, cancellationToken),
            Warnings = warnings,
        };
    }

    private static IReadOnlyList<DiagnosticRow> BuildDetected(ScanResult scan, string? instance)
    {
        var gamePath = scan.SystemGamePath;
        var gamePathMissing = gamePath is not null && !Directory.Exists(LongPath.Normalize(gamePath));

        return
        [
            new DiagnosticRow("Игра", Labels.Game(scan.GameType),
                scan.GameType == GameType.Unknown ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok),
            new DiagnosticRow("Движок", Labels.Engine(scan.Engine, scan.Source)),
            new DiagnosticRow("Источник", Labels.Source(scan.Source)),
            new DiagnosticRow("Папка игры",
                gamePath is null ? "внутри сборки" : gamePath,
                gamePathMissing ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok),
            new DiagnosticRow("Инстанс MO2", instance is null
                ? "не найден"
                : Labels.Instance(scan.RootPath, instance),
                instance is null && scan.Engine != LaunchEngine.Direct
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Ok),
            new DiagnosticRow("Профиль MO2", scan.Mo2ProfileName ?? "—",
                string.IsNullOrWhiteSpace(scan.Mo2ProfileName) && scan.Engine != LaunchEngine.Direct
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Ok),
            new DiagnosticRow("Root Builder", scan.Engine == LaunchEngine.Mo2RootBuilder ? "да" : "нет"),
            new DiagnosticRow("Запускаемый файл", scan.LaunchExecutable ?? "не определён",
                string.IsNullOrWhiteSpace(scan.LaunchExecutable) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok),
            new DiagnosticRow("Похоже на сборку", scan.LooksLikeModpack ? "да" : "нет",
                scan.LooksLikeModpack ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error),
        ];
    }

    private static IReadOnlyList<DiagnosticRow> BuildStored(ModpackManifest manifest, string packRoot,
        string? instance, CancellationToken cancellationToken)
    {
        var executable = manifest.Launch?.Executable ?? string.Empty;
        var executableOk = ExecutableExists(manifest, packRoot, instance, cancellationToken);

        var profile = manifest.Mo2?.ProfileName;
        var profileOnDisk = instance is not null && !string.IsNullOrWhiteSpace(profile)
            && Mo2Locator.ListProfiles(instance).Contains(profile, StringComparer.OrdinalIgnoreCase);

        var gamePath = manifest.SystemGamePath;
        var gamePathMissing = !string.IsNullOrWhiteSpace(gamePath) && !Directory.Exists(LongPath.Normalize(gamePath));

        return
        [
            new DiagnosticRow("Движок", Labels.Engine(manifest.Engine, manifest.Source)),
            new DiagnosticRow("Игра", Labels.Game(manifest.GameType),
                manifest.GameType == GameType.Unknown ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok),
            new DiagnosticRow("Источник", Labels.Source(manifest.Source)),
            new DiagnosticRow("Папка игры",
                string.IsNullOrWhiteSpace(gamePath) ? "внутри сборки" : gamePath,
                gamePathMissing ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok),
            new DiagnosticRow("Профиль MO2", string.IsNullOrWhiteSpace(profile) ? "—" : profile,
                profileOnDisk ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning),
            new DiagnosticRow("Запускаемый файл", string.IsNullOrWhiteSpace(executable) ? "не задан" : executable,
                executableOk ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning),
            new DiagnosticRow("Переопределено вручную", manifest.Detection.OverriddenByUser ? "да" : "нет",
                DiagnosticSeverity.Info),
        ];
    }

    /// <summary>
    /// A stored executable may be a full path (Root Builder loader persisted at scan time) or a bare
    /// name resolved against the game roots / MO2 instance / mods (Root Builder layout).
    /// </summary>
    private static bool ExecutableExists(ModpackManifest manifest, string packRoot, string? instance,
        CancellationToken cancellationToken)
    {
        var executable = manifest.Launch?.Executable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        if (Path.IsPathRooted(executable))
        {
            return File.Exists(LongPath.Normalize(executable));
        }

        var roots = new List<string> { packRoot };
        if (!string.IsNullOrWhiteSpace(manifest.SystemGamePath))
        {
            roots.Add(manifest.SystemGamePath);
        }

        foreach (var portableName in Markers.PortableGameRootNames)
        {
            roots.Add(Path.Combine(packRoot, portableName));
        }

        if (instance is not null)
        {
            roots.Add(instance);
        }

        if (roots.Any(root => File.Exists(LongPath.Normalize(Path.Combine(root, executable)))))
        {
            return true;
        }

        return instance is not null
            && Mo2Locator.FindLoaderInMods(instance, manifest.GameType, cancellationToken) is not null;
    }

    /// <summary>
    /// Manifest vs scan mismatch. Fields the user owns (game detection / profile override) are not
    /// counted — IMM.md guarantees those survive a re-scan, so they legitimately differ.
    /// </summary>
    private static bool DiffersFromScan(ScanResult scan, ModpackManifest manifest)
    {
        if (scan.Engine != manifest.Engine)
        {
            return true;
        }

        if (!manifest.Detection.OverriddenByUser
            && (scan.GameType != manifest.GameType || scan.Source != manifest.Source))
        {
            return true;
        }

        if (!manifest.Detection.ProfileOverriddenByUser
            && !string.Equals(scan.Mo2ProfileName ?? string.Empty, manifest.Mo2?.ProfileName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(scan.Mo2DirectoryName ?? string.Empty, manifest.Mo2?.Directory ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.Equals(
            Path.GetFileName(scan.LaunchExecutable ?? string.Empty),
            Path.GetFileName(manifest.Launch?.Executable ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>User-facing names for detected facts (Core cannot reuse the App ViewModels).</summary>
internal static class Labels
{
    public static string Game(GameType gameType) => gameType switch
    {
        GameType.SkyrimLE => "Skyrim LE",
        GameType.SkyrimSE => "Skyrim SE",
        GameType.Fallout4 => "Fallout 4",
        GameType.FalloutNV => "Fallout NV",
        _ => "не определена",
    };

    public static string Engine(LaunchEngine engine, PackSource source) => (engine, source) switch
    {
        (LaunchEngine.Mo2RootBuilder, _) => "MO2 + Root Builder",
        (LaunchEngine.Mo2Standard, _) => "MO2 Portable",
        (LaunchEngine.Direct, PackSource.Portable) => "Direct · портативная игра",
        _ => "Direct",
    };

    public static string Source(PackSource source) => source == PackSource.Portable
        ? "портативная (игра внутри сборки)"
        : "системная игра";

    /// <summary>Instance folder relative to the pack root, or "в корне сборки" for the classic layout.</summary>
    public static string Instance(string packRoot, string instance)
    {
        var relative = Path.GetRelativePath(packRoot, instance);
        return string.Equals(relative, ".", StringComparison.Ordinal) ? "в корне сборки" : relative;
    }
}

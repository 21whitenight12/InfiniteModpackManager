using Imm.Core.Domain;

namespace Imm.Core.Diagnostics;

/// <summary>How bad a single diagnostic line is: only Warning/Error mark the pack as problematic.</summary>
public enum DiagnosticSeverity
{
    Ok,
    Info,
    Warning,
    Error
}

/// <summary>One labelled fact ("Движок" → "MO2 + Root Builder") with an optional severity.</summary>
public sealed record DiagnosticRow(string Label, string? Value, DiagnosticSeverity Severity = DiagnosticSeverity.Ok);

/// <summary>
/// Read-only diagnostics of one pack: what a fresh scan sees right now, what the stored manifest
/// claims, and every ambiguity the scanner reported (IMM.md: no silent guesses).
/// </summary>
public sealed record PackDiagnostics
{
    public required Guid EntryId { get; init; }

    public required string Name { get; init; }

    public required string RootPath { get; init; }

    public string Summary { get; init; } = string.Empty;

    public bool RootExists { get; init; } = true;

    /// <summary>True when modpack.json is missing or unreadable.</summary>
    public bool ManifestMissing { get; init; }

    /// <summary>True when the stored manifest disagrees with the current scan (re-scan recommended).</summary>
    public bool ManifestStale { get; init; }

    /// <summary>What the scanner detects in the pack folder right now.</summary>
    public IReadOnlyList<DiagnosticRow> Detected { get; init; } = [];

    /// <summary>What the stored modpack.json says, with on-disk validation.</summary>
    public IReadOnlyList<DiagnosticRow> Stored { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasProblems =>
        !RootExists
        || ManifestMissing
        || ManifestStale
        || Warnings.Count > 0
        || Detected.Concat(Stored).Any(row => row.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error);
}

public interface IPackDiagnosticsService
{
    /// <summary>Scans the pack folder and compares the result with its manifest; never writes anything.</summary>
    Task<PackDiagnostics> AnalyzeAsync(LibraryEntry entry, ModpackManifest? manifest,
        CancellationToken cancellationToken = default);
}

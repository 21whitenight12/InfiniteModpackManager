using Imm.Core.Diagnostics;

namespace Imm.App.ViewModels;

/// <summary>
/// One card of the Scanner screen: the library entry (so its actions stay available) plus the
/// diagnostics snapshot of that pack.
/// </summary>
public sealed class PackDiagnosticsViewModel
{
    public PackDiagnosticsViewModel(LibraryEntryViewModel entry, PackDiagnostics diagnostics)
    {
        Entry = entry;
        Diagnostics = diagnostics;
    }

    /// <summary>Library card VM — command parameters («Ре-скан», «Указать папку игры…») point here.</summary>
    public LibraryEntryViewModel Entry { get; }

    public PackDiagnostics Diagnostics { get; }

    public string Name => Diagnostics.Name;

    public string Summary => Diagnostics.Summary;

    public string RootPath => Diagnostics.RootPath;

    public bool HasProblems => Diagnostics.HasProblems;

    public bool ManifestMissing => Diagnostics.ManifestMissing;

    public bool ManifestStale => Diagnostics.ManifestStale;

    public IReadOnlyList<DiagnosticRow> Detected => Diagnostics.Detected;

    public IReadOnlyList<DiagnosticRow> Stored => Diagnostics.Stored;

    public IReadOnlyList<string> Warnings => Diagnostics.Warnings;

    public bool HasWarnings => Diagnostics.Warnings.Count > 0;

    /// <summary>True when nothing is off — the card collapses to a single green line.</summary>
    public bool IsClean => !Diagnostics.HasProblems;

    public bool IsProblem => !IsClean;

    private string? RowValue(string label) =>
        Diagnostics.Detected.FirstOrDefault(row => row.Label == label)?.Value;

    public string GameText => RowValue("Игра") ?? "—";

    public string SourceText => RowValue("Источник") ?? "—";

    public string ProfileText => RowValue("Профиль MO2") ?? "—";

    public string LoaderText => RowValue("Запускаемый файл") ?? "—";

    /// <summary>Badge text for the card header; null when everything is clean.</summary>
    public string? ProblemBadge => Diagnostics switch
    {
        { RootExists: false } => "папка не найдена",
        { ManifestMissing: true } => "манифеста нет",
        { ManifestStale: true } => "нужен ре-скан",
        { HasProblems: true } => "есть замечания",
        _ => null,
    };
}

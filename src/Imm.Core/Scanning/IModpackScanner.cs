using Imm.Core.Domain;

namespace Imm.Core.Scanning;

/// <summary>
/// Heuristic modpack folder scanner per IMM.md Detection Markers.
/// Only file/dir NAMES and attributes are inspected — never file contents (Engineering Standards #4).
/// </summary>
public interface IModpackScanner
{
    Task<ScanResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default);
}

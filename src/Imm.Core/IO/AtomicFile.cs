using Imm.Core.Serialization;

namespace Imm.Core.IO;

/// <summary>
/// Atomic JSON document writer per IMM.md Data Contracts: temp file in the same directory,
/// then File.Move(overwrite). A crash never leaves a half-written document.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteJsonAsync<T>(string path, T document, CancellationToken cancellationToken = default)
    {
        var normalized = LongPath.Normalize(path);
        var directory = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            Path.GetDirectoryName(normalized) ?? ".",
            $".{Path.GetFileName(normalized)}.tmp-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(tempPath, ImmJson.Serialize(document), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, normalized, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; the temp file is inert garbage that the next run can ignore.
            }
        }
    }

    public static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(LongPath.Normalize(path), cancellationToken).ConfigureAwait(false);
        return ImmJson.Deserialize<T>(json);
    }
}

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
            var json = ImmJson.Serialize(document);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            // WriteThrough: данные уходят на диск до File.Move — иначе обрыв питания оставляет
            // «успешно переименованный» пустой файл.
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

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

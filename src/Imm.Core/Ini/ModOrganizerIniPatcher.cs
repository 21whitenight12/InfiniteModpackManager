using System.IO;
using System.Text;
using Imm.Core.IO;

namespace Imm.Core.Ini;

/// <summary>Outcome of a ModOrganizer.ini patch attempt.</summary>
/// <param name="Changed">False when the ini already pointed at the target (idempotent no-op).</param>
/// <param name="BackupPath">Timestamped copy of the original file; null when nothing was written.</param>
public sealed record IniPatchResult(bool Changed, string? BackupPath, string? OldValue, string NewValue);

/// <summary>
/// Reader/self-healing patcher for portable ModOrganizer.ini (IMM.md INI Patching Rules).
/// Strict custom line parser: key and value split at the first '='; Qt service structures
/// (@ByteArray, \xNN escapes, doubled backslashes) are never "normalized" — a rewritten value
/// preserves the original writing style. Absolute stale paths only; relative paths are left
/// untouched. Idempotent. Before any write: timestamped backup &lt;name&gt;.bak-&lt;timestamp&gt;
/// next to the file. A corrupt ini is never rewritten — the patcher stops with an error instead.
///
/// Game folder keys by MO2 version: legacy installs keep an absolute `base_directory` in
/// [General]; MO2 2.5+ keeps `gamePath` (often relative to the instance directory, frequently
/// @ByteArray-encoded). Only the legacy absolute key is ever patched — gamePath belongs to MO2.
/// </summary>
public sealed class ModOrganizerIniPatcher
{
    private const string BaseDirectoryKey = "base_directory";
    private const string GamePathKey = "gamePath";
    private const string GameNameKey = "gameName";
    private const string SelectedProfileKey = "selected_profile";
    private const string GeneralSection = "General";

    /// <summary>Legacy absolute base_directory value from the last [General] entry; null when the key is absent.</summary>
    public string? ReadBaseDirectory(string iniPath)
    {
        var ini = ReadIni(iniPath);
        var match = FindIniKey(ini.Lines, BaseDirectoryKey);
        if (match is null)
        {
            return null;
        }

        return UnescapeQt(StripTrailingComment(match.Value.ValueRaw));
    }

    /// <summary>
    /// MO2's own last selection (selected_profile in [General]), @ByteArray-decoded when needed.
    /// Null when the key is absent or empty.
    /// </summary>
    public string? ReadSelectedProfile(string iniPath)
    {
        var ini = ReadIni(iniPath);
        var match = FindIniKey(ini.Lines, SelectedProfileKey);
        if (match is null)
        {
            return null;
        }

        return DecodePossibleByteArray(StripTrailingComment(match.Value.ValueRaw));
    }

    /// <summary>MO2's game display name (gameName in [General]); null when absent or empty.</summary>
    public string? ReadGameName(string iniPath)
    {
        var ini = ReadIni(iniPath);
        var match = FindIniKey(ini.Lines, GameNameKey);
        if (match is null)
        {
            return null;
        }

        return DecodePossibleByteArray(StripTrailingComment(match.Value.ValueRaw));
    }

    /// <summary>Decodes a Qt @ByteArray(...) wrapper when present; plain values pass through.</summary>
    private static string DecodePossibleByteArray(string raw)
    {
        return raw.StartsWith("@ByteArray(", StringComparison.Ordinal) && raw.EndsWith(')')
            ? UnescapeQt(raw["@ByteArray(".Length..^1])
            : UnescapeQt(raw);
    }

    /// <summary>
    /// Game folder as MO2 itself resolves it: the legacy absolute base_directory when present,
    /// otherwise gamePath resolved against the instance directory (Qt @ByteArray values decoded).
    /// Null when neither key holds a usable value — MO2 then applies its own defaults
    /// (the instance directory), and the ini must not be touched.
    /// </summary>
    public string? ReadManagedGameDirectory(string iniPath)
    {
        var normalized = LongPath.Normalize(iniPath);
        var ini = ReadIni(normalized);

        var baseDirectory = FindIniKey(ini.Lines, BaseDirectoryKey);
        if (baseDirectory is not null)
        {
            return UnescapeQt(StripTrailingComment(baseDirectory.Value.ValueRaw));
        }

        var gamePath = FindIniKey(ini.Lines, GamePathKey);
        if (gamePath is null)
        {
            return null;
        }

        var raw = StripTrailingComment(gamePath.Value.ValueRaw);
        var value = raw.StartsWith("@ByteArray(", StringComparison.Ordinal) && raw.EndsWith(')')
            ? UnescapeQt(raw["@ByteArray(".Length..^1])
            : UnescapeQt(raw);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(normalized) ?? ".", value));
    }

    public IniPatchResult PatchBaseDirectory(string iniPath, string newBaseDirectory)
    {
        var normalized = LongPath.Normalize(iniPath);
        if (!File.Exists(normalized))
        {
            throw new InvalidOperationException("ModOrganizer.ini не найден — поддерживаются только portable-инстансы MO2.");
        }

        if (!Path.IsPathRooted(newBaseDirectory))
        {
            throw new InvalidOperationException("Новый путь к игре должен быть абсолютным.");
        }

        var ini = ReadIni(normalized);
        var match = FindIniKey(ini.Lines, BaseDirectoryKey)
            ?? throw new InvalidOperationException(
                "В секции [General] не найден ключ base_directory — структура ini неожиданная, файл не изменён.");

        if (match.ValueRaw.StartsWith("@ByteArray(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "base_directory хранится в бинарном формате Qt (@ByteArray) — файл не изменён, требуется ручная проверка.");
        }

        var oldValue = UnescapeQt(StripTrailingComment(match.ValueRaw));
        if (!Path.IsPathRooted(oldValue))
        {
            // IMM.md: relative paths are not touched.
            throw new InvalidOperationException(
                $"base_directory указывает на относительный путь («{oldValue}») — такие пути не исправляются автоматически.");
        }

        if (SamePath(oldValue, newBaseDirectory))
        {
            return new IniPatchResult(Changed: false, BackupPath: null, OldValue: oldValue, NewValue: newBaseDirectory);
        }

        var backupPath = CreateBackup(normalized);
        var cleanRaw = StripTrailingComment(match.ValueRaw);
        var updatedLine = match.LineText[..(match.EqualsIndex + 1)] + EncodeLike(cleanRaw, newBaseDirectory);
        ini.Lines[match.Index] = updatedLine;
        WriteIni(normalized, ini);

        return new IniPatchResult(Changed: true, BackupPath: backupPath, OldValue: oldValue, NewValue: newBaseDirectory);
    }

    // ---------- parsing ----------

    private readonly record struct IniKeyMatch(
        int Index, string LineText, int EqualsIndex, string ValueRaw);

    private sealed class ParsedIni
    {
        public required List<string> Lines { get; init; }
        public required Encoding Encoding { get; init; }
        public required bool Crlf { get; init; }
        public required bool TrailingNewline { get; init; }
    }

    private static IniKeyMatch? FindIniKey(List<string> lines, string keyName)
    {
        string section = string.Empty;
        IniKeyMatch? last = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i];
            var trimmed = text.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            var equalsIndex = text.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = text[..equalsIndex].Trim();
            // QSettings: keys before the first section header belong to a nameless root section;
            // a duplicated key's last occurrence wins.
            if ((section.Length == 0 || section.Equals(GeneralSection, StringComparison.OrdinalIgnoreCase))
                && key.Equals(keyName, StringComparison.OrdinalIgnoreCase))
            {
                last = new IniKeyMatch(i, text, equalsIndex, text[(equalsIndex + 1)..]);
            }
        }

        return last;
    }

    // ---------- Qt value encoding ----------

    /// <summary>Resolves Qt ini escaping: \\ → backslash, \xHH → char; unknown escapes keep the backslash.</summary>
    private static string UnescapeQt(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == '\\')
                {
                    sb.Append('\\');
                    i++;
                    continue;
                }

                if (next == 'x' && i + 3 < value.Length && TryParseHex(value.AsSpan(i + 2, 2), out var code))
                {
                    sb.Append((char)code);
                    i += 3;
                    continue;
                }
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes the new path in the style of the original value (IMM.md: preserve the original
    /// writing style): backslash style stays, forward-slash style converted, Qt-escaped files
    /// get doubled backslashes. A plain path never introduces new escape sequences.
    /// </summary>
    private static string EncodeLike(string originalRaw, string newPath)
    {
        var qtEscaped = originalRaw.Contains("\\x", StringComparison.Ordinal)
                        || originalRaw.Contains("\\\\", StringComparison.Ordinal);

        if (originalRaw.Contains('\\'))
        {
            return qtEscaped ? newPath.Replace("\\", "\\\\") : newPath;
        }

        return newPath.Replace("\\", "/");
    }

    /// <summary>
    /// Cuts a trailing "; comment" / "# comment" (preceded by whitespace) from a raw value.
    /// A path never continues after "; " — mid-path '#' without whitespace stays intact.
    /// </summary>
    private static string StripTrailingComment(string raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            if ((raw[i] == ';' || raw[i] == '#')
                && (i == 0 || raw[i - 1] == ' ' || raw[i - 1] == '\t'))
            {
                return raw[..i].TrimEnd();
            }
        }

        return raw;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out int value) =>
        int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static bool SamePath(string left, string right)
    {
        static string Canon(string path) =>
            LongPath.Normalize(path).TrimEnd('\\', '/').Replace('/', '\\');

        return string.Equals(Canon(left), Canon(right), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- file IO (encoding by BOM, atomic write) ----------

    private static ParsedIni ReadIni(string path)
    {
        // Единственная точка нормализации: сканер зовёт ReadBaseDirectory / ReadSelectedProfile /
        // ReadGameName с «сырым» путём, а сборки живут и за пределами MAX_PATH.
        var normalizedPath = LongPath.Normalize(path);
        var bytes = File.ReadAllBytes(normalizedPath);
        var (encoding, bomLength) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);

        if (content.Contains('\uFFFD'))
        {
            // Corrupt ini: never rewrite (IMM.md). Decoding already lost bytes — stop here.
            throw new InvalidOperationException(
                "ModOrganizer.ini повреждён (недопустимая последовательность байт) — файл не изменён. Восстановите его из .bak-копии.");
        }

        return new ParsedIni
        {
            Lines = SplitLines(content),
            Encoding = encoding,
            Crlf = content.Contains("\r\n"),
            TrailingNewline = content.EndsWith('\n') || content.EndsWith('\r'),
        };
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        // No BOM: read/write as UTF-8 without BOM (MO2's own default for fresh inis).
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
            {
                continue;
            }

            var end = i > start && content[i - 1] == '\r' ? i - 1 : i;
            lines.Add(content[start..end]);
            start = i + 1;
        }

        if (start < content.Length)
        {
            lines.Add(content[start..]);
        }

        return lines;
    }

    private static string CreateBackup(string normalizedPath)
    {
        var directory = Path.GetDirectoryName(normalizedPath) ?? ".";
        var fileName = Path.GetFileName(normalizedPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string backup;

        var attempt = 0;
        do
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt + 1}";
            backup = LongPath.Normalize(Path.Combine(directory, $"{fileName}.bak-{stamp}{suffix}"));
            attempt++;
        }
        while (File.Exists(backup) && attempt < 9);

        File.Copy(normalizedPath, backup, overwrite: false);
        return backup;
    }

    private static void WriteIni(string normalizedPath, ParsedIni ini)
    {
        var newline = ini.Crlf ? "\r\n" : "\n";
        var sb = new StringBuilder();
        for (var i = 0; i < ini.Lines.Count; i++)
        {
            sb.Append(ini.Lines[i]);
            var isLast = i == ini.Lines.Count - 1;
            if (!isLast || ini.TrailingNewline)
            {
                sb.Append(newline);
            }
        }

        var directory = Path.GetDirectoryName(normalizedPath) ?? ".";
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(normalizedPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, sb.ToString(), ini.Encoding);
            File.Move(tempPath, normalizedPath, overwrite: true);
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
                // Best-effort cleanup; the temp file is inert garbage.
            }
        }
    }
}

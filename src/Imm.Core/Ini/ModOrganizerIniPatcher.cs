using System.IO;
using System.Text;
using Imm.Core.IO;
using Imm.Core.Localization;

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
        var trimmed = raw.TrimStart();
        return trimmed.StartsWith("@ByteArray(", StringComparison.Ordinal) && trimmed.EndsWith(')')
            ? UnescapeQt(trimmed["@ByteArray(".Length..^1])
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
        var value = DecodePossibleByteArray(raw);

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
            throw new InvalidOperationException(Loc.T("Err_IniNotFound"));
        }

        if (!Path.IsPathRooted(newBaseDirectory))
        {
            throw new InvalidOperationException(Loc.T("Err_GamePathNotAbsolute"));
        }

        var ini = ReadIni(normalized);
        var match = FindIniKey(ini.Lines, BaseDirectoryKey)
            ?? throw new InvalidOperationException(
                Loc.T("Err_BaseDirectoryKeyMissing"));

        if (match.ValueRaw.TrimStart().StartsWith("@ByteArray(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                Loc.T("Err_BaseDirectoryBinary"));
        }

        var oldValue = UnescapeQt(StripTrailingComment(match.ValueRaw));
        if (!Path.IsPathRooted(oldValue))
        {
            // IMM.md: relative paths are not touched.
            throw new InvalidOperationException(
                Loc.F("Err_BaseDirectoryRelative", oldValue));
        }

        if (SamePath(oldValue, newBaseDirectory))
        {
            return new IniPatchResult(Changed: false, BackupPath: null, OldValue: oldValue, NewValue: newBaseDirectory);
        }

        var backupPath = CreateBackup(normalized);
        var cleanRaw = StripTrailingComment(match.ValueRaw);
        // Хвостовой "; комментарий" — часть пользовательской строки, а не наш мусор: возвращаем его на место.
        var commentSuffix = match.ValueRaw[cleanRaw.Length..];
        var updatedLine = match.LineText[..(match.EqualsIndex + 1)] + EncodeLike(cleanRaw, newBaseDirectory) + commentSuffix;
        ini.Lines[match.Index] = updatedLine;
        WriteIni(normalized, ini);

        return new IniPatchResult(Changed: true, BackupPath: backupPath, OldValue: oldValue, NewValue: newBaseDirectory);
    }

    /// <summary>
    /// Makes <paramref name="profileName"/> MO2's own selection (selected_profile in the ini), stored
    /// exactly as MO2 stores it: Qt @ByteArray with UTF-8 \xHH escapes. Needed because MO2 2.5.2
    /// crashes (0xC0000005) when -p receives a non-ASCII profile name — such a profile must be
    /// selected in the ini instead of on the command line. Idempotent; timestamped backup before writing.
    /// </summary>
    public IniPatchResult PatchSelectedProfile(string iniPath, string profileName)
    {
        var normalized = LongPath.Normalize(iniPath);
        if (!File.Exists(normalized))
        {
            throw new InvalidOperationException(Loc.T("Err_IniNotFound"));
        }

        var ini = ReadIni(normalized);
        var match = FindIniKey(ini.Lines, SelectedProfileKey);
        var current = match is null
            ? null
            : DecodePossibleByteArray(StripTrailingComment(match.Value.ValueRaw));

        if (string.Equals(current, profileName, StringComparison.Ordinal))
        {
            return new IniPatchResult(Changed: false, BackupPath: null, OldValue: current, NewValue: profileName);
        }

        var encoded = "@ByteArray(" + EncodeQtBytes(profileName) + ")";
        var backupPath = CreateBackup(normalized);
        if (match is null)
        {
            // A portable ini keeps this key in the nameless root section at the very top.
            ini.Lines.Insert(0, SelectedProfileKey + "=" + encoded);
        }
        else
        {
            var found = match.Value;
            var cleanRaw = StripTrailingComment(found.ValueRaw);
            var commentSuffix = found.ValueRaw[cleanRaw.Length..];
            ini.Lines[found.Index] = found.LineText[..(found.EqualsIndex + 1)] + encoded + commentSuffix;
        }

        WriteIni(normalized, ini);
        return new IniPatchResult(Changed: true, BackupPath: backupPath, OldValue: current, NewValue: profileName);
    }

    /// <summary>
    /// Makes <paramref name="gameDirectory"/> MO2's game folder (gamePath), written the way MO2 2.5
    /// stores it: Qt @ByteArray with doubled backslashes and \xHH escapes for non-ASCII bytes.
    /// Needed when the pack ships its own game but the ini still points at another install — MO2
    /// resolves the game from its own ini, so leaving it alone would mount the wrong Data folder.
    /// A missing key is left alone (MO2 then applies its own defaults). Idempotent; backup before writing.
    /// </summary>
    public IniPatchResult PatchGamePath(string iniPath, string gameDirectory)
    {
        var normalized = LongPath.Normalize(iniPath);
        if (!File.Exists(normalized))
        {
            throw new InvalidOperationException(Loc.T("Err_IniNotFound"));
        }

        if (!Path.IsPathRooted(gameDirectory))
        {
            throw new InvalidOperationException(Loc.T("Err_GamePathNotAbsolute"));
        }

        var ini = ReadIni(normalized);
        var match = FindIniKey(ini.Lines, GamePathKey);
        if (match is null)
        {
            return new IniPatchResult(Changed: false, BackupPath: null, OldValue: null, NewValue: gameDirectory);
        }

        var found = match.Value;
        var cleanRaw = StripTrailingComment(found.ValueRaw);
        var current = DecodePossibleByteArray(cleanRaw);
        if (SamePath(current, gameDirectory))
        {
            return new IniPatchResult(Changed: false, BackupPath: null, OldValue: current, NewValue: gameDirectory);
        }

        var encoded = cleanRaw.TrimStart().StartsWith("@ByteArray(", StringComparison.Ordinal)
            ? "@ByteArray(" + EncodeQtPath(gameDirectory) + ")"
            : EncodeLike(cleanRaw, gameDirectory);

        // Хвостовой "; комментарий" — часть пользовательской строки, а не наш мусор: возвращаем его на место.
        var commentSuffix = found.ValueRaw[cleanRaw.Length..];
        var backupPath = CreateBackup(normalized);
        ini.Lines[found.Index] = found.LineText[..(found.EqualsIndex + 1)] + encoded + commentSuffix;
        WriteIni(normalized, ini);

        return new IniPatchResult(Changed: true, BackupPath: backupPath, OldValue: current, NewValue: gameDirectory);
    }

    /// <summary>Path inside a Qt @ByteArray value: ASCII verbatim, backslashes doubled, other bytes as \xHH (UTF-8).</summary>
    private static string EncodeQtPath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            if (b == (byte)'\\')
            {
                sb.Append("\\\\");
            }
            else if (b is >= 0x20 and <= 0x7E)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append("\\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>Qt @ByteArray payload: printable ASCII verbatim, every other byte as \xHH of its UTF-8 form.</summary>
    private static string EncodeQtBytes(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and <= 0x7E && b != (byte)'\\')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append("\\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
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

    /// <summary>
    /// Resolves Qt ini escaping: \\ → backslash, \xHH → byte; unknown escapes keep the backslash.
    /// Qt stores non-Latin-1 text as the UTF-8 bytes of the string (\xd0\x93 = «Г»), so a run of
    /// \xHH escapes is decoded as UTF-8 — byte-by-byte would turn Cyrillic profile names into
    /// mojibake and MO2 would never find the profile passed with -p.
    /// </summary>
    private static string UnescapeQt(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var pending = new List<byte>(4);

        void FlushPending()
        {
            if (pending.Count > 0)
            {
                sb.Append(DecodeUtf8Run(pending));
                pending.Clear();
            }
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == '\\')
                {
                    FlushPending();
                    sb.Append('\\');
                    i++;
                    continue;
                }

                if (next == 'x' && i + 3 < value.Length && TryParseHex(value.AsSpan(i + 2, 2), out var code))
                {
                    pending.Add((byte)code);
                    i += 3;
                    continue;
                }
            }

            FlushPending();
            sb.Append(value[i]);
        }

        FlushPending();
        return sb.ToString();
    }

    /// <summary>UTF-8 first (Qt's own encoding); legacy byte values fall back to Latin-1 instead of throwing.</summary>
    private static string DecodeUtf8Run(List<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes.ToArray());
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        string content;
        try
        {
            content = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        catch (DecoderFallbackException)
        {
            // Не «повреждён», а «не та кодировка»: файл не трогаем, но и диагноз не врём.
            throw new InvalidOperationException(
                Loc.T("Err_IniEncoding"));
        }

        if (content.Contains('\uFFFD'))
        {
            // Corrupt ini: never rewrite (IMM.md). Decoding already lost bytes — stop here.
            throw new InvalidOperationException(
                Loc.T("Err_IniCorrupt"));
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
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
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
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
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

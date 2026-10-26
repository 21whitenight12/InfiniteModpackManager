using System.IO;
using System.Text;
using Imm.Core.Ini;
using Imm.Core.IO;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>INI patching rules (IMM.md): timestamped backup, idempotency, BOM/encoding preserved, Qt escapes untouched.</summary>
public sealed class ModOrganizerIniPatcherTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "imm-ini-" + Guid.NewGuid().ToString("N"));
    private readonly ModOrganizerIniPatcher _patcher = new();

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private string WriteIni(string content, Encoding? encoding = null)
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "ModOrganizer.ini");
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private static string ReadRaw(string path) => File.ReadAllText(path);

    [Fact]
    public void ReadBaseDirectory_PlainPath_ReturnsValue()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\Games\\Old\n");

        Assert.Equal(@"C:\Games\Old", _patcher.ReadBaseDirectory(path));
    }

    [Fact]
    public void ReadBaseDirectory_TrailingComment_Ignored()
    {
        var path = WriteIni("base_directory=C:\\Games\\Old  ; migrated 2023\n");

        Assert.Equal(@"C:\Games\Old", _patcher.ReadBaseDirectory(path));
    }

    [Fact]
    public void ReadBaseDirectory_NoKey_ReturnsNull()
    {
        var path = WriteIni("[General]\nuse_profile_paths=true\n");

        Assert.Null(_patcher.ReadBaseDirectory(path));
    }

    [Fact]
    public void Patch_UpdatesValue_CreatesTimestampedBackup_PreservesOtherLines()
    {
        var content = "[Settings]\nkeep_loose_files=true\n\n[General]\nbase_directory=C:\\Games\\Old\n\n[customExecutables]\nsize=1\n1\\title=SKSE\n";
        var path = WriteIni(content);

        var result = _patcher.PatchBaseDirectory(path, @"E:\Packs\Test\Stock Game");

        Assert.True(result.Changed);
        Assert.Equal(@"C:\Games\Old", result.OldValue);
        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(Path.Combine(_temp, "ModOrganizer.ini.bak-"), result.BackupPath);

        // The backup holds the original bytes.
        Assert.Equal(content, File.ReadAllText(result.BackupPath));

        var updated = ReadRaw(path);
        Assert.Contains(@"base_directory=E:\Packs\Test\Stock Game", updated);
        Assert.Contains("keep_loose_files=true", updated);
        Assert.Contains("size=1", updated);
        Assert.Contains("1\\title=SKSE", updated);
    }

    [Fact]
    public void Patch_IsIdempotent_SecondCallSkipsWriteAndBackup()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\Games\\Old\n");
        _patcher.PatchBaseDirectory(path, @"E:\Packs\Test\Stock Game");

        var second = _patcher.PatchBaseDirectory(path, @"E:\Packs\Test\Stock Game");

        Assert.False(second.Changed);
        Assert.Null(second.BackupPath);
        Assert.Single(Directory.GetFiles(_temp, "ModOrganizer.ini.bak-*"));
    }

    [Fact]
    public void BackupName_CollisionGetsSuffix()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\A\n");
        var first = _patcher.PatchBaseDirectory(path, @"C:\B");
        // Rewind the clock is impossible; force collision by recreating the same file and stamp.
        File.WriteAllText(path, "[General]\nbase_directory=C:\\A\n");
        var second = _patcher.PatchBaseDirectory(path, @"C:\B");

        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal(2, Directory.GetFiles(_temp, "ModOrganizer.ini.bak-*").Length);
    }

    [Fact]
    public void Patch_PreservesUtf8Bom()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\Games\\Old\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        _patcher.PatchBaseDirectory(path, @"C:\Games\New");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Contains(@"base_directory=C:\Games\New", File.ReadAllText(path));
    }

    [Fact]
    public void Patch_PreservesUtf16LeAndCyrillicContent()
    {
        var path = WriteIni("; комментарий\n[General]\nbase_directory=C:\\Games\\Old\n", Encoding.Unicode);

        _patcher.PatchBaseDirectory(path, @"C:\Games\Новая");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Contains("; комментарий", File.ReadAllText(path, Encoding.Unicode));
        Assert.Contains(@"base_directory=C:\Games\Новая", File.ReadAllText(path, Encoding.Unicode));
    }

    [Fact]
    public void Patch_PreservesNewlineStyle()
    {
        var lfPath = WriteIni("[General]\nbase_directory=C:\\A\nother=1\n");
        _patcher.PatchBaseDirectory(lfPath, @"C:\B");
        Assert.DoesNotContain("\r", ReadRaw(lfPath));

        var crlfPath = WriteIni("[General]\r\nbase_directory=C:\\A\r\nother=1\r\n");
        _patcher.PatchBaseDirectory(crlfPath, @"C:\B");
        var crlf = ReadRaw(crlfPath);
        Assert.Contains("\r\n", crlf);
        Assert.DoesNotContain("\r\n\r\n", crlf);
        Assert.DoesNotContain("\n\r", crlf);
    }

    [Fact]
    public void Patch_QtEscapedValue_WritesDoubledBackslashes()
    {
        // Qt sometimes escapes separators: C:\\Games\\Old (raw) means C:\Games\Old.
        var path = WriteIni("[General]\nbase_directory=C:\\\\Games\\\\Old\n");

        var result = _patcher.PatchBaseDirectory(path, @"E:\Packs\Stock Game");

        Assert.True(result.Changed);
        Assert.Equal(@"C:\Games\Old", result.OldValue);
        Assert.Contains(@"base_directory=E:\\Packs\\Stock Game", ReadRaw(path));
        Assert.Equal(@"E:\Packs\Stock Game", _patcher.ReadBaseDirectory(path));
    }

    [Fact]
    public void Patch_ForwardSlashStyle_ConvertsToForwardSlashes()
    {
        var path = WriteIni("[General]\nbase_directory=C:/Games/Old\n");

        _patcher.PatchBaseDirectory(path, @"E:\Packs\Stock Game");

        Assert.Contains("base_directory=E:/Packs/Stock Game", ReadRaw(path));
    }

    [Fact]
    public void Patch_RelativePath_ThrowsAndLeavesFileUntouched()
    {
        var content = "[General]\nbase_directory=.\\Game\n";
        var path = WriteIni(content);

        Assert.Throws<InvalidOperationException>(() => _patcher.PatchBaseDirectory(path, @"C:\Games\New"));

        Assert.Equal(content, ReadRaw(path));
        Assert.Empty(Directory.GetFiles(_temp, "ModOrganizer.ini.bak-*"));
    }

    [Fact]
    public void Patch_ByteArrayValue_ThrowsAndLeavesFileUntouched()
    {
        var content = "[General]\nbase_directory=@ByteArray(C:\\Games\\Old)\n";
        var path = WriteIni(content);

        Assert.Throws<InvalidOperationException>(() => _patcher.PatchBaseDirectory(path, @"C:\Games\New"));

        Assert.Equal(content, ReadRaw(path));
    }

    [Fact]
    public void Patch_MissingKey_Throws()
    {
        var path = WriteIni("[General]\nother=1\n");

        Assert.Throws<InvalidOperationException>(() => _patcher.PatchBaseDirectory(path, @"C:\Games\New"));
    }

    [Fact]
    public void Patch_CorruptUtf8_ThrowsAndLeavesFileUntouched()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "ModOrganizer.ini");
        var corrupt = new byte[] { 0x5B, 0x47, 0x5D, 0x0A, 0x62, 0x3D, 0xC3, 0x28, 0x0A }; // [G]\nb=\xC3\x28\n
        File.WriteAllBytes(path, corrupt);

        Assert.Throws<InvalidOperationException>(() => _patcher.PatchBaseDirectory(path, @"C:\Games\New"));

        Assert.Equal(corrupt, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadManagedGameDirectory_GamePathByteArrayRelative_ResolvesAgainstInstanceDir()
    {
        // Real-world MO2 2.5+ shape: gamePath=@ByteArray(..\\) — the pack root above MO2\.
        var path = WriteIni("[General]\ngamePath=@ByteArray(..\\\\)\n");

        Assert.Equal(Path.GetFullPath(Path.Combine(_temp, "..\\")), _patcher.ReadManagedGameDirectory(path));
    }

    [Fact]
    public void ReadManagedGameDirectory_GamePathAbsolute_ReturnedAsIs()
    {
        var path = WriteIni("[General]\ngamePath=C:\\Games\\Skyrim SE\n");

        Assert.Equal(@"C:\Games\Skyrim SE", _patcher.ReadManagedGameDirectory(path));
    }

    [Fact]
    public void ReadManagedGameDirectory_NoGameKeys_ReturnsNull()
    {
        var path = WriteIni("[General]\ngameName=Skyrim Special Edition\nfirst_start=false\n");

        Assert.Null(_patcher.ReadManagedGameDirectory(path));
    }

    [Fact]
    public void ReadManagedGameDirectory_LegacyBaseDirectory_WinsOverGamePath()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\Games\\Old\ngamePath=@ByteArray(..\\\\)\n");

        Assert.Equal(@"C:\Games\Old", _patcher.ReadManagedGameDirectory(path));
    }

    [Fact]
    public void Patch_DuplicateKey_LastOccurrenceWins()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\First\nbase_directory=C:\\Second\n");

        _patcher.PatchBaseDirectory(path, @"C:\Third");

        var updated = ReadRaw(path);
        Assert.Contains("base_directory=C:\\First", updated);
        Assert.Contains("base_directory=C:\\Third", updated);
        Assert.DoesNotContain("base_directory=C:\\Second", updated);
    }

    [Fact]
    public void ReadSelectedProfile_LongPath_ReadsValue()
    {
        // Сканер зовёт эти методы с «сырым» путём: нормализация обязана жить внутри патчера.
        var directory = _temp;
        while (directory.Length < 280)
        {
            directory = Path.Combine(directory, "pack-" + Guid.NewGuid().ToString("N")[..8]);
        }

        try
        {
            Directory.CreateDirectory(LongPath.Normalize(directory));
            var path = Path.Combine(directory, "ModOrganizer.ini");
            File.WriteAllText(LongPath.Normalize(path), "[General]\ngameName=Fallout 4\nselected_profile=Long\n");

            Assert.True(path.Length >= 260);
            Assert.Equal("Long", _patcher.ReadSelectedProfile(path));
            Assert.Equal("Fallout 4", _patcher.ReadGameName(path));
        }
        finally
        {
            try
            {
                Directory.Delete(LongPath.Normalize(_temp), recursive: true);
            }
            catch (IOException)
            {
                // Глубокая папка могла остаться — не роняем тест из-за уборки.
            }
        }
    }

    [Fact]
    public void Patch_KeepsTrailingComment()
    {
        var path = WriteIni("[General]\nbase_directory=C:\\Games\\Old  ; migrated 2023\n");

        _patcher.PatchBaseDirectory(path, @"C:\Games\New");

        var updated = ReadRaw(path);
        Assert.Contains(@"base_directory=C:\Games\New", updated);
        Assert.Contains("; migrated 2023", updated);
    }

    [Fact]
    public void PatchGamePath_WritesQtByteArray_WithBackup()
    {
        var path = WriteIni("[General]\ngameName=Skyrim Special Edition\n"
                            + @"gamePath=@ByteArray(D:\\Other\\Pack)" + "\n");

        var result = _patcher.PatchGamePath(path, @"E:\Packs\Dormant\Skyrim");

        Assert.True(result.Changed);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(@"D:\Other\Pack", result.OldValue);
        Assert.Contains(@"gamePath=@ByteArray(E:\\Packs\\Dormant\\Skyrim)", ReadRaw(path));
        Assert.Equal(@"E:\Packs\Dormant\Skyrim", _patcher.ReadManagedGameDirectory(path));
    }

    [Fact]
    public void PatchGamePath_SameValue_NoBackup()
    {
        var path = WriteIni("[General]\n" + @"gamePath=@ByteArray(E:\\Packs\\Skyrim)" + "\n");

        var result = _patcher.PatchGamePath(path, @"E:\Packs\Skyrim");

        Assert.False(result.Changed);
        Assert.Null(result.BackupPath);
        Assert.Empty(Directory.GetFiles(_temp, "*.bak-*"));
    }

    [Fact]
    public void PatchGamePath_CyrillicPath_WrittenAsUtf8Escapes()
    {
        // MO2 stores non-Latin-1 text as UTF-8 \xHH escapes; «С» = \xd0\xa1.
        var path = WriteIni("[General]\n" + @"gamePath=@ByteArray(D:\\Old)" + "\n");

        _patcher.PatchGamePath(path, @"E:\Сборки\Skyrim");

        Assert.Contains(@"\xd0\xa1", ReadRaw(path));
        Assert.Equal(@"E:\Сборки\Skyrim", _patcher.ReadManagedGameDirectory(path));
    }

    [Fact]
    public void PatchGamePath_MissingKey_IsAddedInRootSection()
    {
        // No gamePath at all: MO2 would fall back to the instance directory, so the key is written
        // (timestamped backup first) in the same style MO2 uses.
        var path = WriteIni("[General]\ngameName=Skyrim Special Edition\n");

        var result = _patcher.PatchGamePath(path, @"E:\Packs\Skyrim");

        Assert.True(result.Changed);
        Assert.Null(result.OldValue);
        Assert.Single(Directory.GetFiles(_temp, "*.bak-*"));
        Assert.Equal(@"E:\Packs\Skyrim", _patcher.ReadManagedGameDirectory(path));
    }
}

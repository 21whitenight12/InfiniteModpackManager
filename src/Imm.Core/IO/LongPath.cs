namespace Imm.Core.IO;

/// <summary>
/// Long path support (IMM.md Engineering Standards #3): paths &gt;= 260 chars get the \\?\ prefix.
/// UNC shares get \\?\UNC\. Already-prefixed paths pass through untouched.
/// </summary>
public static class LongPath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.StartsWith(@"\\?\"))
        {
            return path;
        }

        if (path.Length < 260)
        {
            return path;
        }

        return path.StartsWith(@"\\")
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }
}

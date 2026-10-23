using Imm.Core.Scanning;
using Microsoft.Win32;

namespace Imm.App.Services;

/// <summary>Windows registry implementation of the Core abstraction (IMM.md Core Architecture).</summary>
public sealed class WindowsRegistryService : IRegistryService
{
    public string? GetString(RegistryHive hive, RegistryView view, string subKey, string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }

    public IEnumerable<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetSubKeyNames() ?? [];
    }
}

using Imm.Core.Scanning;
using Microsoft.Win32;

namespace Imm.App.Services;

/// <summary>Windows registry implementation of the Core abstraction (IMM.md Core Architecture).</summary>
public sealed class WindowsRegistryService : IRegistryService
{
    public string? GetString(RegistryHive hive, RegistryView view, string subKey, string valueName)
    {
        using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }

    public IEnumerable<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
    {
        using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKey);
        return key?.GetSubKeyNames() ?? [];
    }
}

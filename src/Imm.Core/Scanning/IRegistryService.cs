using Microsoft.Win32;

namespace Imm.Core.Scanning;

/// <summary>
/// Registry access abstraction (IMM.md Core Architecture): interface in Core, Windows implementation in Imm.App,
/// so Steam/GOG detection logic stays fully testable with a fake.
/// </summary>
public interface IRegistryService
{
    /// <summary>Reads a string value; returns null when key or value is missing.</summary>
    string? GetString(RegistryHive hive, RegistryView view, string subKey, string valueName);

    /// <summary>Enumerates subkey names; empty sequence when the key is missing.</summary>
    IEnumerable<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey);
}

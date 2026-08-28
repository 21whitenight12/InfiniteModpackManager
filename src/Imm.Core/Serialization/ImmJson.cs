using System.Text.Json;
using System.Text.Json.Serialization;

namespace Imm.Core.Serialization;

/// <summary>
/// Shared JSON settings enforcing IMM.md data invariants:
/// enums as strings, indented local files, schemaVersion-first declaration order in contracts.
/// Dates are UTC ISO-8601 (System.Text.Json default round-trip format).
/// </summary>
public static class ImmJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Failed to deserialize {typeof(T).Name}: document is null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

using System.Text.Json;

namespace AmbientFx.Bridge;

/// <summary>
/// Single source of truth for bridge JSON conventions: camelCase property names,
/// case-insensitive reads. Everything that crosses the WebView2 bridge uses these options.
/// </summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

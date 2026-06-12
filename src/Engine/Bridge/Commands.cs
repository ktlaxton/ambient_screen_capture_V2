using System.Text.Json;

namespace AmbientFx.Bridge;

/// <summary>Web -> engine command type names. Mirrored in web/src/shared/bridge.ts (CommandMap).</summary>
public static class CommandTypes
{
    public const string SetEnabled = "setEnabled";
    public const string SetSourceMonitor = "setSourceMonitor";
    public const string SetTargetMonitors = "setTargetMonitors";
    public const string SetEffect = "setEffect";
    public const string SetEffectParams = "setEffectParams";
    public const string SetGlobal = "setGlobal";
    public const string SavePreset = "savePreset";
    public const string LoadPreset = "loadPreset";
    public const string DeletePreset = "deletePreset";
    public const string SetAutostart = "setAutostart";
    public const string SetHotkey = "setHotkey";
    public const string RequestState = "requestState";
    public const string WindowCommand = "windowCommand";
    public const string CompleteOnboarding = "completeOnboarding";
    public const string ReportError = "reportError";
}

/// <summary>Incoming envelope from any WebView2: {"type":"...","payload":{...}}.</summary>
public sealed class CommandEnvelope
{
    public string Type { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

public static class CommandParser
{
    /// <summary>Parses a raw bridge JSON string into an envelope. Returns null for malformed input (never throws).</summary>
    public static CommandEnvelope? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var env = JsonSerializer.Deserialize<CommandEnvelope>(json, BridgeJson.Options);
            return string.IsNullOrEmpty(env?.Type) ? null : env;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Deserializes the payload to a typed command DTO. Returns null on mismatch (never throws).</summary>
    public static T? PayloadAs<T>(this CommandEnvelope envelope) where T : class
    {
        try
        {
            if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return Activator.CreateInstance<T>();
            return envelope.Payload.Deserialize<T>(BridgeJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class SetEnabledCmd { public bool Enabled { get; set; } }

public sealed class SetSourceMonitorCmd { public string MonitorId { get; set; } = string.Empty; }

public sealed class SetTargetMonitorsCmd { public List<string> MonitorIds { get; set; } = new(); }

/// <summary>MonitorId null, empty, or "all" applies to all targets (sets ActiveEffectId and clears overrides).</summary>
public sealed class SetEffectCmd
{
    public string? MonitorId { get; set; }
    public string EffectId { get; set; } = string.Empty;
}

public sealed class SetEffectParamsCmd
{
    public string EffectId { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Params { get; set; } = new();
}

/// <summary>Partial update — only non-null fields are applied.</summary>
public sealed class SetGlobalCmd
{
    public float? Intensity { get; set; }
    public float? Smoothing { get; set; }
    public float? Brightness { get; set; }
    public float? AudioSensitivity { get; set; }
    public int? MaxFps { get; set; }
}

public sealed class PresetCmd { public string Name { get; set; } = string.Empty; }

public sealed class SetAutostartCmd { public bool Enabled { get; set; } }

/// <summary>Keys is a gesture string like "Ctrl+Alt+A"; empty string unbinds the action.</summary>
public sealed class SetHotkeyCmd
{
    public string Action { get; set; } = string.Empty;
    public string Keys { get; set; } = string.Empty;
}

/// <summary>Custom-chrome window controls. Action: "minimize" | "maximize" | "restore" | "close".</summary>
public sealed class WindowCommandCmd { public string Action { get; set; } = string.Empty; }

/// <summary>
/// The web layer reporting a fatal/runtime failure (e.g. an effect crashed) so the engine
/// can log it and surface a toast instead of leaving a silently dead window (NFR5/AC7).
/// </summary>
public sealed class ReportErrorCmd
{
    /// <summary>Origin hint, e.g. "effect-render" | "effect-create".</summary>
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

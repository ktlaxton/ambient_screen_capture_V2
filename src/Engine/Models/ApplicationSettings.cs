using System.Text.Json;

namespace AmbientFx.Models;

public class ApplicationSettings
{
    public bool IsEnabled { get; set; }
    public string SourceMonitorId { get; set; } = string.Empty;
    public List<string> TargetMonitorIds { get; set; } = new();

    /// <summary>Effect applied to target monitors without a per-monitor override.</summary>
    public string ActiveEffectId { get; set; } = "edge-glow";

    /// <summary>Optional per-monitor effect overrides (monitorId -> effectId).</summary>
    public Dictionary<string, string> EffectByMonitorId { get; set; } = new();

    public float AudioSensitivity { get; set; } = 0.5f;
    public float GlobalIntensity { get; set; } = 1.0f;
    public float Smoothing { get; set; } = 0.5f;
    public float Brightness { get; set; } = 0.85f;
    public int MaxFps { get; set; } = 60;
    public int ZonesPerEdge { get; set; } = 8;
    public int AudioBands { get; set; } = 12;
    public bool Autostart { get; set; }

    /// <summary>Per-effect parameter bags. Opaque to the engine; round-tripped to the web layer.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> EffectParamsById { get; set; } = new();

    /// <summary>Hotkey bindings: action name (see <see cref="HotkeyActions"/>) -> gesture string like "Ctrl+Alt+A". Empty string = unbound.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    public List<Preset> Presets { get; set; } = new();
    public string ActivePresetName { get; set; } = string.Empty;
    public bool FirstRunCompleted { get; set; }

    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Deep copy via JSON round-trip.</summary>
    public ApplicationSettings Clone() =>
        JsonSerializer.Deserialize<ApplicationSettings>(
            JsonSerializer.Serialize(this, CloneOptions), CloneOptions)!;
}

/// <summary>A named snapshot of settings. The snapshot's own Presets list is always empty (no recursion).</summary>
public class Preset
{
    public string Name { get; set; } = string.Empty;
    public ApplicationSettings Snapshot { get; set; } = new();
}

/// <summary>Well-known hotkey action names used in <see cref="ApplicationSettings.Hotkeys"/>.</summary>
public static class HotkeyActions
{
    public const string ToggleEnabled = "toggleEnabled";
    public const string OpenSettings = "openSettings";
    public const string NextPreset = "nextPreset";

    public static readonly string[] All = { ToggleEnabled, OpenSettings, NextPreset };
}

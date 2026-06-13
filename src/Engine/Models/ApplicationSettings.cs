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

    /// <summary>What closing the control window does (Story 7.3). See <see cref="CloseActions"/>.</summary>
    public string CloseAction { get; set; } = CloseActions.Ask;

    /// <summary>Velopack update feed (Story 7.4): a GitHub repo URL or a static feed URL.
    /// Blank falls back to the project's GitHub Releases feed.</summary>
    public string UpdateFeedUrl { get; set; } = string.Empty;

    /// <summary>Master toggle for ambient RGB peripherals (Epic 8 / Story 8.1).</summary>
    public bool AmbientDevicesEnabled { get; set; }

    /// <summary>Peripheral LED brightness 0..1, separate from the on-screen <see cref="Brightness"/>.</summary>
    public float PeripheralBrightness { get; set; } = 1.0f;

    /// <summary>Per-device placement/tuning overrides keyed by stable device id (Story 8.2).
    /// Devices without an entry use the Auto defaults.</summary>
    public Dictionary<string, DevicePlacement> DevicePlacements { get; set; } = new();

    /// <summary>Enabled RGB vendor providers (Story 8.3), e.g. "corsair", "razer".
    /// Corsair-only by default — other ecosystems are opt-in to avoid surprises.</summary>
    public List<string> RgbProviders { get; set; } = new() { "corsair" };

    /// <summary>Audio-reactive peripheral layer on/off (Story 8.3).</summary>
    public bool AudioReactiveDevices { get; set; }

    /// <summary>Audio-reactive depth 0..1: 0 = no effect, 1 = silence goes dark.</summary>
    public float AudioReactiveDepth { get; set; } = 0.5f;

    /// <summary>AmbientFx Premium license key (Epic 9); empty = free edition.</summary>
    public string LicenseKey { get; set; } = string.Empty;

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

/// <summary>Close-the-window behaviors (Story 7.3); bridge values are camelCase strings.</summary>
public static class CloseActions
{
    public const string Ask = "ask";
    public const string Quit = "quit";
    public const string MinimizeToTray = "minimizeToTray";

    public static readonly string[] All = { Ask, Quit, MinimizeToTray };

    public static bool IsValid(string? value) => value is Ask or Quit or MinimizeToTray;
}

/// <summary>Well-known hotkey action names used in <see cref="ApplicationSettings.Hotkeys"/>.</summary>
public static class HotkeyActions
{
    public const string ToggleEnabled = "toggleEnabled";
    public const string OpenSettings = "openSettings";
    public const string NextPreset = "nextPreset";

    public static readonly string[] All = { ToggleEnabled, OpenSettings, NextPreset };
}

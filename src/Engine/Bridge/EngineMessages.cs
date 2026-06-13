using AmbientFx.Models;

namespace AmbientFx.Bridge;

/// <summary>Engine -> web message type names. Mirrored in web/src/shared/bridge.ts (EngineMessageMap).</summary>
public static class MessageTypes
{
    public const string Frame = "frame";
    public const string Status = "status";
    public const string Config = "config";
    public const string Monitors = "monitors";
    public const string WindowConfig = "windowConfig";
    public const string WindowState = "windowState";
    /// <summary>Tells the control page to show the close-choice modal (Story 7.3).</summary>
    public const string ClosePrompt = "closePrompt";
    /// <summary>Ambient RGB peripheral state for the control UI (Story 8.1).</summary>
    public const string Devices = "devices";
}

/// <summary>Outgoing envelope: serializes to {"type":"...","payload":{...}}.</summary>
public sealed record OutboundEnvelope<T>(string Type, T Payload);

/// <summary>
/// The high-frequency per-frame data stream (colors + audio). Kept tiny on purpose —
/// raw video frames never cross the bridge.
/// </summary>
public sealed class FramePayload
{
    /// <summary>Engine timestamp, milliseconds, monotonic.</summary>
    public double T { get; set; }

    public EdgeColors Edges { get; set; } = new();

    /// <summary>Overall dominant color, sRGB 0-255 [r,g,b].</summary>
    public int[] Dominant { get; set; } = new int[3];

    public AudioData Audio { get; set; } = new();
}

/// <summary>Edge-zone colors, sRGB 0-255. Each entry is [r,g,b]; array length = zonesPerEdge.
/// Top/bottom run left-to-right; left/right run top-to-bottom.</summary>
public sealed class EdgeColors
{
    public int[][] Top { get; set; } = Array.Empty<int[]>();
    public int[][] Bottom { get; set; } = Array.Empty<int[]>();
    public int[][] Left { get; set; } = Array.Empty<int[]>();
    public int[][] Right { get; set; } = Array.Empty<int[]>();
}

public sealed class AudioData
{
    /// <summary>Overall audio intensity 0..1.</summary>
    public float Intensity { get; set; }

    /// <summary>Normalized 0..1 per log-spaced frequency band, low to high.</summary>
    public float[] Bands { get; set; } = Array.Empty<float>();
}

public sealed class StatusPayload
{
    /// <summary>"info" | "warn" | "error"</summary>
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
}

public sealed class MonitorsPayload
{
    public List<MonitorInfo> Monitors { get; set; } = new();
}

public sealed class ConfigPayload
{
    public ApplicationSettings Settings { get; set; } = new();
    public bool FirstRun { get; set; }
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Current entitlement (Epic 9); drives the web UI's premium gating.</summary>
    public LicenseStatePayload License { get; set; } = new();
}

/// <summary>Entitlement state for the UI (Epic 9). Never includes the key itself beyond settings.</summary>
public sealed class LicenseStatePayload
{
    /// <summary>"free" | "premium"</summary>
    public string Edition { get; set; } = "free";

    public bool IsPremium { get; set; }
    public string LicensedTo { get; set; } = string.Empty;

    /// <summary>ISO date when a subscription-style key expires; null/empty = perpetual.</summary>
    public string? Expires { get; set; }
}

/// <summary>
/// Ambient RGB peripheral state (Story 8.1): live connection state + the discovered device
/// list for the control UI. Pushed on every change and on requestState.
/// </summary>
public sealed class DevicesPayload
{
    /// <summary>"disabled" | "connecting" | "connected" | "icueNotFound" | "refused" | "noDevices" | "error"</summary>
    public string ConnectionState { get; set; } = "disabled";

    public List<AmbientFx.Devices.AmbientDeviceInfo> Devices { get; set; } = new();

    /// <summary>Per-vendor outcomes from the last connect (Story 8.3); empty when not connected.</summary>
    public List<AmbientFx.Devices.RgbProviderStatus> Providers { get; set; } = new();
}

/// <summary>Control-window native state, pushed on every change so the custom title bar's
/// maximize/restore glyph stays in sync with Aero snap / Win+Up / drag-region double-click.</summary>
public sealed class WindowStatePayload
{
    /// <summary>"normal" | "maximized" | "minimized"</summary>
    public string State { get; set; } = "normal";
}

/// <summary>
/// Sent once to each effect window after load (and again when its assignment changes):
/// which monitor it sits on, which effect to run, and where that monitor sits relative
/// to the capture source (for layout-aware effects, FR7).
/// </summary>
public sealed class WindowConfigPayload
{
    public string MonitorId { get; set; } = string.Empty;
    public string EffectId { get; set; } = string.Empty;
    public MonitorInfo? Monitor { get; set; }
    public MonitorInfo? Source { get; set; }

    /// <summary>Where the target monitor sits relative to the source: "left" | "right" | "above" | "below" | "none".</summary>
    public string Relation { get; set; } = "none";
}

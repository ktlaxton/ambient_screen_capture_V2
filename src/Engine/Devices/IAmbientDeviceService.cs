using AmbientFx.Bridge;
using AmbientFx.Models;

namespace AmbientFx.Devices;

/// <summary>
/// Vendor-neutral output sink that extends the ambient effect onto physical RGB peripherals
/// (Epic 8). A second consumer of the same <see cref="EdgeColors"/> stream the effect windows
/// get — no capture/processing changes. Implementations must mirror the capture/audio services'
/// defensive posture: construct safely with no hardware present, never block the caller, and
/// never throw back into the pipeline (NFR5).
/// </summary>
public interface IAmbientDeviceService : IDisposable
{
    /// <summary>Begin connecting (asynchronously) and pushing frames. Idempotent while running.</summary>
    void Start();

    /// <summary>
    /// Stop pushing and release lighting control back to the vendor software so the user's
    /// normal profiles resume (Story 8.1 AC5). Idempotent.
    /// </summary>
    void Stop();

    /// <summary>
    /// Latest frame from the pipeline (edge colors + audio). Called on the processing
    /// background thread at frame rate; must be non-blocking and thread-safe. Hardware
    /// pushes are rate-limited internally (AC4), so submitting every frame is cheap.
    /// </summary>
    void SubmitFrame(FramePayload frame);

    /// <summary>Peripheral master brightness 0..1 (separate from the on-screen brightness).</summary>
    float Brightness { get; set; }

    /// <summary>
    /// Per-device placement/tuning overrides keyed by stable device id (Story 8.2). Applies
    /// live to the next push — no reconnect. The caller hands over a private snapshot; the
    /// service never mutates it. Devices without an entry use the Auto defaults.
    /// </summary>
    void SetPlacements(IReadOnlyDictionary<string, DevicePlacement> placements);

    /// <summary>
    /// Vendor providers to use on the NEXT connect (Story 8.3) — keys like "corsair",
    /// "razer". Unlike placements this does not apply to a live session; the caller
    /// restarts the service to reconnect with the new set.
    /// </summary>
    void SetEnabledProviders(IReadOnlyCollection<string> providerKeys);

    /// <summary>Audio-reactive layer on/off (Story 8.3 AC4). Applies live.</summary>
    bool AudioReactiveEnabled { get; set; }

    /// <summary>Audio-reactive depth 0..1: 0 = no effect, 1 = silence goes dark. Applies live.</summary>
    float AudioReactiveDepth { get; set; }

    /// <summary>Current connection state + discovered devices, for the UI device list.</summary>
    AmbientDevicesSnapshot Snapshot { get; }

    /// <summary>Raised whenever <see cref="Snapshot"/> changes. May fire on any thread.</summary>
    event EventHandler? StateChanged;
}

/// <summary>
/// Connection states pushed over the bridge (mirrored in web/src/shared/bridge.ts
/// DeviceConnectionState). These are normal states, not errors — iCUE being absent must
/// degrade cleanly (AC2).
/// </summary>
public static class DeviceConnectionStates
{
    public const string Disabled = "disabled";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string IcueNotFound = "icueNotFound";
    public const string Refused = "refused";
    public const string NoDevices = "noDevices";
    public const string Error = "error";
}

/// <summary>One discovered peripheral, as shown in the control UI's read-only device list.</summary>
public sealed class AmbientDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Vendor SDK device class, e.g. "Keyboard", "Mouse", "LedStripe", "Fan".</summary>
    public string Type { get; set; } = string.Empty;

    public int LedCount { get; set; }
}

/// <summary>Immutable view of the device service's state for the UI.</summary>
public sealed class AmbientDevicesSnapshot
{
    /// <summary>A <see cref="DeviceConnectionStates"/> value.</summary>
    public string ConnectionState { get; init; } = DeviceConnectionStates.Disabled;

    public IReadOnlyList<AmbientDeviceInfo> Devices { get; init; } = Array.Empty<AmbientDeviceInfo>();

    /// <summary>Per-vendor outcomes from the last connect (Story 8.3); empty when not connected.</summary>
    public IReadOnlyList<RgbProviderStatus> Providers { get; init; } = Array.Empty<RgbProviderStatus>();
}

namespace AmbientFx.Devices;

/// <summary>
/// The thin seam between <see cref="RgbNetAmbientDeviceService"/> and the actual RGB.NET
/// surface, so the service's lifecycle, rate limiting and projection are unit-testable
/// without loading native SDK bits (AC9 — no hardware in CI).
/// One backend instance is one connection session: Connect once, Apply many, Dispose to
/// disconnect and hand lighting control back to the vendor software.
/// </summary>
public interface IRgbDeviceBackend : IDisposable
{
    /// <summary>
    /// Blocking connect + device enumeration. Expected unavailability (SDK absent, control
    /// refused, no devices) comes back as a failed <see cref="RgbBackendConnection.State"/>,
    /// not an exception.
    /// </summary>
    RgbBackendConnection Connect();

    /// <summary>
    /// Pushes per-device LED colors ([r,g,b] sRGB 0-255, indexed like the device's
    /// <see cref="RgbBackendDevice.NormalizedLeds"/>) to the hardware.
    /// </summary>
    void Apply(IReadOnlyList<RgbDeviceColors> frame);
}

/// <summary>Result of one connection attempt.</summary>
public sealed class RgbBackendConnection
{
    /// <summary>A <see cref="DeviceConnectionStates"/> value.</summary>
    public required string State { get; init; }

    public IReadOnlyList<RgbBackendDevice> Devices { get; init; } = Array.Empty<RgbBackendDevice>();

    /// <summary>Per-vendor outcome for every provider that was enabled (Story 8.3).</summary>
    public IReadOnlyList<RgbProviderStatus> Providers { get; init; } = Array.Empty<RgbProviderStatus>();
}

/// <summary>One vendor provider's outcome in a connection attempt (Story 8.3).</summary>
public sealed class RgbProviderStatus
{
    /// <summary>Stable provider key, e.g. "corsair", "razer" (mirrored in the web catalog).</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>A <see cref="RgbProviderStates"/> value.</summary>
    public required string State { get; init; }

    public int DeviceCount { get; init; }
}

/// <summary>Per-provider states (bridge values, camelCase).</summary>
public static class RgbProviderStates
{
    public const string Connected = "connected";
    /// <summary>The vendor's software/SDK isn't installed or running — a normal state (AC2).</summary>
    public const string Unavailable = "unavailable";
    public const string Refused = "refused";
    public const string Error = "error";

    /// <summary>
    /// Collapses per-provider outcomes into the overall <see cref="DeviceConnectionStates"/>
    /// value when no devices were found. Pure, so it's unit-testable without native SDKs.
    /// </summary>
    public static string SummarizeNoDevices(IReadOnlyList<RgbProviderStatus> providers)
    {
        if (providers.Count == 0 || providers.Any(p => p.State == Connected))
        {
            return DeviceConnectionStates.NoDevices;
        }
        if (providers.Any(p => p.State == Refused))
        {
            return DeviceConnectionStates.Refused;
        }
        if (providers.Any(p => p.State == Unavailable))
        {
            return DeviceConnectionStates.IcueNotFound; // "vendor software not found" in the UI
        }
        return DeviceConnectionStates.Error;
    }
}

/// <summary>One device as the backend exposes it: identity + normalized LED positions.</summary>
public sealed class RgbBackendDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Vendor SDK device class, e.g. "Keyboard", "Mouse", "LedStripe".</summary>
    public required string Type { get; init; }

    /// <summary>LED positions normalized 0..1 within the device (see <see cref="LedProjection.Normalize"/>).</summary>
    public required LedProjection.LedPoint[] NormalizedLeds { get; init; }
}

/// <summary>One device's worth of colors for <see cref="IRgbDeviceBackend.Apply"/>.</summary>
public sealed class RgbDeviceColors
{
    public required string DeviceId { get; init; }

    /// <summary>[r,g,b] 0-255 per LED, same order as the device's NormalizedLeds.</summary>
    public required int[][] Colors { get; init; }
}

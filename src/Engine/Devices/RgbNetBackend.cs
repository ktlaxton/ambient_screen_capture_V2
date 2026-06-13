using System.IO;
using Microsoft.Extensions.Logging;
using RGB.NET.Core;
using RGB.NET.Devices.Asus;
using RGB.NET.Devices.Corsair;
using RGB.NET.Devices.Logitech;
using RGB.NET.Devices.Msi;
using RGB.NET.Devices.Razer;
using RGB.NET.Devices.SteelSeries;
using RGB.NET.Devices.Wooting;

namespace AmbientFx.Devices;

/// <summary>
/// The real RGB.NET-backed session (Epic 8). One instance wraps one RGBSurface plus every
/// enabled vendor provider (Story 8.3): providers are tried independently, an absent vendor
/// (its software/SDK not installed or running) is a normal skipped state, and all discovered
/// devices flow through the same vendor-neutral pipeline — no per-vendor branching past this
/// class. Disposing disconnects every provider, handing lighting control back to the vendor
/// software (AC5). Provider singletons clear themselves on dispose, so enable→disable→enable
/// cycles create fresh sessions safely.
/// </summary>
public sealed class RgbNetBackend : IRgbDeviceBackend
{
    /// <summary>RGB.NET's Corsair default is 500 ms — too tight when iCUE is busy starting up.</summary>
    private static readonly TimeSpan CorsairConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Vendor catalog (Story 8.3 AC1/AC3): key + display name + singleton factory. Keys are
    /// the persisted settings values and are mirrored in the web UI's provider list.
    /// </summary>
    private static readonly (string Key, string Name, Func<IRGBDeviceProvider> Factory)[] ProviderRegistry =
    {
        ("corsair", "Corsair iCUE", () => CorsairDeviceProvider.Instance),
        ("logitech", "Logitech", () => LogitechDeviceProvider.Instance),
        ("razer", "Razer Chroma", () => RazerDeviceProvider.Instance),
        ("asus", "ASUS Aura", () => AsusDeviceProvider.Instance),
        ("msi", "MSI Mystic Light", () => MsiDeviceProvider.Instance),
        ("steelseries", "SteelSeries", () => SteelSeriesDeviceProvider.Instance),
        ("wooting", "Wooting", () => WootingDeviceProvider.Instance),
    };

    private readonly ILogger<RgbNetBackend> _logger;
    private readonly IReadOnlyCollection<string> _enabledProviders;
    private readonly object _sync = new();
    private readonly Dictionary<string, Led[]> _ledsByDeviceId = new();
    private readonly List<IRGBDeviceProvider> _providers = new();

    private RGBSurface? _surface;
    private bool _disposed;

    public RgbNetBackend(ILogger<RgbNetBackend> logger, IReadOnlyCollection<string> enabledProviders)
    {
        _logger = logger;
        _enabledProviders = enabledProviders;
    }

    /// <inheritdoc />
    public RgbBackendConnection Connect()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new RgbBackendConnection { State = DeviceConnectionStates.Error };
            }

            _surface = new RGBSurface();
            var statuses = new List<RgbProviderStatus>();
            var devices = new List<RgbBackendDevice>();

            foreach (var (key, name, factory) in ProviderRegistry)
            {
                if (!_enabledProviders.Contains(key))
                {
                    continue;
                }
                statuses.Add(ConnectProvider(key, name, factory, devices));
            }

            if (devices.Count == 0)
            {
                string overall = RgbProviderStates.SummarizeNoDevices(statuses);
                ReleaseCore(); // don't hold sessions open with nothing to drive
                return new RgbBackendConnection { State = overall, Providers = statuses };
            }

            _logger.LogInformation("RGB providers connected: {Providers} ({Count} device(s) total)",
                string.Join(", ", statuses.Where(s => s.State == RgbProviderStates.Connected).Select(s => s.Key)),
                devices.Count);
            return new RgbBackendConnection
            {
                State = DeviceConnectionStates.Connected,
                Devices = devices,
                Providers = statuses,
            };
        }
    }

    /// <summary>
    /// Must hold _sync. One vendor's connect + enumerate; failure never escapes (AC2 — an
    /// absent vendor is a normal state and must not affect the others).
    /// </summary>
    private RgbProviderStatus ConnectProvider(
        string key, string name, Func<IRGBDeviceProvider> factory, List<RgbBackendDevice> devices)
    {
        IRGBDeviceProvider? provider = null;
        try
        {
            if (key == "corsair")
            {
                PrepareCorsairNatives();
            }

            provider = factory();
            provider.Initialize(throwExceptions: true);
            _providers.Add(provider);

            int count = 0;
            foreach (IRGBDevice device in provider.Devices)
            {
                Led[] leds = device.ToArray();
                if (leds.Length == 0)
                {
                    continue;
                }
                _surface!.Attach(device);

                var centers = leds
                    .Select(l => new LedProjection.LedPoint(
                        l.Location.X + (l.Size.Width / 2.0),
                        l.Location.Y + (l.Size.Height / 2.0)))
                    .ToList();

                string id = StableDeviceId(device);
                _ledsByDeviceId[id] = leds;
                devices.Add(new RgbBackendDevice
                {
                    Id = id,
                    Name = device.DeviceInfo.DeviceName,
                    Type = device.DeviceInfo.DeviceType.ToString(),
                    NormalizedLeds = LedProjection.Normalize(centers),
                });
                count++;
            }

            return new RgbProviderStatus
            {
                Key = key,
                Name = name,
                State = RgbProviderStates.Connected,
                DeviceCount = count,
            };
        }
        catch (Exception ex)
        {
            string state = key == "corsair"
                ? MapCorsairFailure(provider as CorsairDeviceProvider, ex)
                : RgbProviderStates.Unavailable;
            _logger.LogInformation(ex, "RGB provider {Provider} unavailable (mapped to {State})", key, state);
            try { provider?.Dispose(); }
            catch { /* a half-initialized SDK may throw again */ }
            _providers.Remove(provider!);
            return new RgbProviderStatus { Key = key, Name = name, State = state };
        }
    }

    /// <inheritdoc />
    public void Apply(IReadOnlyList<RgbDeviceColors> frame)
    {
        lock (_sync)
        {
            if (_disposed || _surface is null)
            {
                return;
            }

            foreach (var deviceColors in frame)
            {
                if (!_ledsByDeviceId.TryGetValue(deviceColors.DeviceId, out Led[]? leds))
                {
                    continue;
                }
                int count = Math.Min(leds.Length, deviceColors.Colors.Length);
                for (int i = 0; i < count; i++)
                {
                    int[] c = deviceColors.Colors[i];
                    leds[i].Color = new RGB.NET.Core.Color((byte)c[0], (byte)c[1], (byte)c[2]);
                }
            }

            _surface.Update();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReleaseCore();
        }
    }

    /// <summary>
    /// Stable identity for per-device settings (Story 8.2 AC4): the iCUE device id survives
    /// replug/reconnect/restart, unlike RGB.NET's enumeration order. Falls back to
    /// manufacturer/type/name for devices without one; identical twins get a deterministic
    /// ordinal suffix.
    /// </summary>
    private string StableDeviceId(IRGBDevice device)
    {
        string key = device.DeviceInfo is CorsairRGBDeviceInfo corsair && !string.IsNullOrEmpty(corsair.DeviceId)
            ? $"corsair:{corsair.DeviceId}"
            : $"{device.DeviceInfo.Manufacturer}:{device.DeviceInfo.DeviceType}:{device.DeviceInfo.DeviceName}";

        string id = key;
        for (int ordinal = 2; _ledsByDeviceId.ContainsKey(id); ordinal++)
        {
            id = $"{key}#{ordinal}";
        }
        return id;
    }

    /// <summary>Must hold _sync. Tears every session down; vendors resume their own lighting.</summary>
    private void ReleaseCore()
    {
        try { _surface?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing the RGB surface"); }
        _surface = null;

        foreach (var provider in _providers)
        {
            try { provider.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing an RGB provider"); }
        }
        _providers.Clear();
        _ledsByDeviceId.Clear();
    }

    /// <summary>
    /// The Corsair provider probes relative native paths; make our shipped iCUE SDK copy
    /// findable regardless of the process working directory, and widen the handshake timeout.
    /// </summary>
    private static void PrepareCorsairNatives()
    {
        string nativeDll = Path.Combine(AppContext.BaseDirectory, "x64", "iCUESDK.x64_2019.dll");
        if (File.Exists(nativeDll) && !CorsairDeviceProvider.PossibleX64NativePaths.Contains(nativeDll))
        {
            CorsairDeviceProvider.PossibleX64NativePaths.Insert(0, nativeDll);
        }
        CorsairDeviceProvider.ConnectionTimeout = CorsairConnectTimeout;
    }

    /// <summary>
    /// Maps a Corsair connect failure via the provider's session state: refused = third-party
    /// control is off in iCUE (worth a specific hint); timeout/closed = iCUE isn't running.
    /// </summary>
    private static string MapCorsairFailure(CorsairDeviceProvider? provider, Exception exception)
    {
        try
        {
            switch (provider?.SessionState)
            {
                case CorsairSessionState.ConnectionRefused:
                    return RgbProviderStates.Refused;
                case CorsairSessionState.Timeout:
                case CorsairSessionState.Closed:
                case CorsairSessionState.Connecting:
                case CorsairSessionState.ConnectionLost:
                    return RgbProviderStates.Unavailable;
            }
        }
        catch
        {
            // SessionState itself can throw if the native SDK never loaded — fall through.
        }

        return exception.Message.Contains("native", StringComparison.OrdinalIgnoreCase)
            ? RgbProviderStates.Error
            : RgbProviderStates.Unavailable;
    }
}

#if SIMULATOR_ENABLED
using AmbientFx.Devices;

namespace AmbientFx.Simulator.Devices;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.4). An <see cref="IRgbDeviceBackend"/> that loads no native SDK and
/// instead <b>records</b> the per-device per-LED sRGB colors the <b>real</b>
/// <see cref="RgbNetAmbientDeviceService"/> produces via the real <see cref="LedProjection"/>. It is
/// injected through the service's existing backend-factory seam in place of <c>RgbNetBackend</c>; the
/// service, its push timer, and the projection are otherwise untouched (fidelity invariant — the
/// simulator never recomputes the colors, only re-reads them). Recording is thread-safe (the push
/// timer fires on the threadpool). Compiled out of Release.
/// </summary>
public sealed class VisualizationBackend : IRgbDeviceBackend
{
    private readonly object _lock = new();
    private readonly IReadOnlyList<RgbBackendDevice> _devices;
    private IReadOnlyList<RgbDeviceColors> _latest = Array.Empty<RgbDeviceColors>();

    /// <summary>Raised after each <see cref="Apply"/> so the composite window can refresh the LED dots.</summary>
    public event EventHandler? ColorsChanged;

    public VisualizationBackend() => _devices = SimDevices.Build();

    /// <summary>The simulated device set (stable for the backend's lifetime).</summary>
    public IReadOnlyList<RgbBackendDevice> Devices => _devices;

    /// <inheritdoc />
    public RgbBackendConnection Connect() => new()
    {
        State = DeviceConnectionStates.Connected,
        Devices = _devices,
        Providers = new[]
        {
            new RgbProviderStatus
            {
                Key = "corsair",
                Name = "Corsair iCUE (sim)",
                State = RgbProviderStates.Connected,
                DeviceCount = _devices.Count,
            },
        },
    };

    /// <inheritdoc />
    public void Apply(IReadOnlyList<RgbDeviceColors> frame)
    {
        lock (_lock)
        {
            _latest = frame;
        }
        try
        {
            ColorsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The renderer must never take down the push timer (NFR5).
        }
    }

    /// <summary>Thread-safe snapshot of the latest per-device per-LED colors recorded from <see cref="Apply"/>.</summary>
    public IReadOnlyList<RgbDeviceColors> LatestColors
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>Latest colors for one device id, or null if none recorded yet.</summary>
    public int[][]? ColorsFor(string deviceId)
    {
        lock (_lock)
        {
            foreach (var d in _latest)
            {
                if (string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    return d.Colors;
                }
            }
            return null;
        }
    }

    public void Dispose()
    {
        // Nothing native to release.
    }
}
#endif

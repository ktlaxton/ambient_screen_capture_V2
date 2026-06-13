using AmbientFx.Bridge;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Devices;

/// <summary>
/// Drives ambient colors onto RGB peripherals through an <see cref="IRgbDeviceBackend"/>
/// (RGB.NET in production, a fake in tests). Owns the session lifecycle, the latest-frame
/// buffer and the ~30 Hz push timer, and the projection from edge zones to LEDs.
///
/// Threading model: Start/Stop arrive on the UI thread; the connect runs on the threadpool;
/// SubmitFrame arrives on the processing background thread (lock-free latest-wins write);
/// the push timer fires on the threadpool. _gate guards all session state. Nothing in here
/// may throw to a caller (NFR5).
/// </summary>
public sealed class RgbNetAmbientDeviceService : IAmbientDeviceService
{
    /// <summary>~30 Hz — well under capture FPS; peripherals can't keep up with the screen (AC4).</summary>
    private const int UpdateIntervalMs = 33;

    private static readonly TimeSpan PushErrorLogInterval = TimeSpan.FromMinutes(1);

    private readonly Func<IReadOnlyCollection<string>, IRgbDeviceBackend> _backendFactory;
    private readonly ILogger<RgbNetAmbientDeviceService> _logger;
    private readonly object _gate = new();

    private IRgbDeviceBackend? _backend;
    private IReadOnlyList<RgbBackendDevice> _devices = Array.Empty<RgbBackendDevice>();
    private IReadOnlyList<RgbProviderStatus> _providerStatuses = Array.Empty<RgbProviderStatus>();
    private IReadOnlyCollection<string> _enabledProviders = new[] { "corsair" };
    private System.Threading.Timer? _timer;
    private string _state = DeviceConnectionStates.Disabled;
    private int _generation; // bumped on Stop so a stale in-flight connect discards itself
    private bool _running;
    private bool _disposed;
    private float _brightness = 1f;
    private bool _audioReactive;
    private float _audioDepth = 0.5f;
    private IReadOnlyDictionary<string, DevicePlacement> _placements =
        new Dictionary<string, DevicePlacement>();

    private FramePayload? _latestFrame;
    private FramePayload? _pushedFrame;
    private int _pushing;
    private DateTime _lastPushErrorUtc = DateTime.MinValue;

    public event EventHandler? StateChanged;

    public RgbNetAmbientDeviceService(
        Func<IReadOnlyCollection<string>, IRgbDeviceBackend> backendFactory,
        ILogger<RgbNetAmbientDeviceService> logger)
    {
        _backendFactory = backendFactory;
        _logger = logger;
    }

    public float Brightness
    {
        get { lock (_gate) { return _brightness; } }
        set
        {
            float clamped = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
            lock (_gate)
            {
                if (Math.Abs(_brightness - clamped) < 0.0005f)
                {
                    return;
                }
                _brightness = clamped;
                _pushedFrame = null; // re-push the current frame at the new brightness
            }
        }
    }

    /// <inheritdoc />
    public bool AudioReactiveEnabled
    {
        get { lock (_gate) { return _audioReactive; } }
        set
        {
            lock (_gate)
            {
                if (_audioReactive == value)
                {
                    return;
                }
                _audioReactive = value;
                _pushedFrame = null;
            }
        }
    }

    /// <inheritdoc />
    public float AudioReactiveDepth
    {
        get { lock (_gate) { return _audioDepth; } }
        set
        {
            float clamped = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;
            lock (_gate)
            {
                if (Math.Abs(_audioDepth - clamped) < 0.0005f)
                {
                    return;
                }
                _audioDepth = clamped;
                _pushedFrame = null;
            }
        }
    }

    /// <inheritdoc />
    public void SetPlacements(IReadOnlyDictionary<string, DevicePlacement> placements)
    {
        lock (_gate)
        {
            _placements = placements ?? new Dictionary<string, DevicePlacement>();
            _pushedFrame = null; // apply the new mapping to the current frame immediately
        }
    }

    /// <inheritdoc />
    public void SetEnabledProviders(IReadOnlyCollection<string> providerKeys)
    {
        lock (_gate)
        {
            _enabledProviders = providerKeys ?? Array.Empty<string>();
        }
    }

    public AmbientDevicesSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new AmbientDevicesSnapshot
                {
                    ConnectionState = _state,
                    Devices = _devices.Select(d => new AmbientDeviceInfo
                    {
                        Id = d.Id,
                        Name = d.Name,
                        Type = d.Type,
                        LedCount = d.NormalizedLeds.Length,
                    }).ToList(),
                    Providers = _providerStatuses,
                };
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        int generation;
        lock (_gate)
        {
            if (_disposed || _running)
            {
                return;
            }
            _running = true;
            generation = _generation;
            _state = DeviceConnectionStates.Connecting;
        }
        RaiseStateChanged();
        _ = Task.Run(() => ConnectCore(generation));
    }

    /// <summary>Threadpool. Connects, then adopts the session only if Stop hasn't intervened.</summary>
    private void ConnectCore(int generation)
    {
        IRgbDeviceBackend? backend = null;
        RgbBackendConnection connection;
        try
        {
            IReadOnlyCollection<string> providers;
            lock (_gate) { providers = _enabledProviders; }
            backend = _backendFactory(providers);
            connection = backend.Connect();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ambient device backend failed to connect");
            connection = new RgbBackendConnection { State = DeviceConnectionStates.Error };
        }

        bool stale, adopted = false;
        lock (_gate)
        {
            stale = _disposed || !_running || generation != _generation;
            if (!stale)
            {
                _providerStatuses = connection.Providers;
                if (connection.State == DeviceConnectionStates.Connected
                    && backend is not null
                    && connection.Devices.Count > 0)
                {
                    _backend = backend;
                    _devices = connection.Devices;
                    _state = DeviceConnectionStates.Connected;
                    _pushedFrame = null;
                    _timer = new System.Threading.Timer(
                        static state => ((RgbNetAmbientDeviceService)state!).PushLatestFrame(),
                        this, UpdateIntervalMs, UpdateIntervalMs);
                    adopted = true;
                }
                else
                {
                    _state = connection.State == DeviceConnectionStates.Connected
                        ? DeviceConnectionStates.NoDevices
                        : connection.State;
                    _devices = Array.Empty<RgbBackendDevice>();
                }
            }
        }

        if (!adopted)
        {
            DisposeBackendQuietly(backend); // releases any session we won't use
        }
        if (!stale)
        {
            _logger.LogInformation("Ambient devices: {State} ({Count} device(s))",
                connection.State, connection.Devices.Count);
            RaiseStateChanged();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        IRgbDeviceBackend? backend;
        bool raise;
        lock (_gate)
        {
            raise = _running || _state != DeviceConnectionStates.Disabled;
            _generation++;
            _running = false;
            _timer?.Dispose();
            _timer = null;
            backend = _backend;
            _backend = null;
            _devices = Array.Empty<RgbBackendDevice>();
            _providerStatuses = Array.Empty<RgbProviderStatus>();
            _state = DeviceConnectionStates.Disabled;
            _latestFrame = null;
            _pushedFrame = null;
        }
        DisposeBackendQuietly(backend); // disconnect: the vendor software resumes its profiles (AC5)
        if (raise)
        {
            RaiseStateChanged();
        }
    }

    /// <inheritdoc />
    public void SubmitFrame(FramePayload frame)
    {
        if (frame?.Edges is null)
        {
            return;
        }
        Volatile.Write(ref _latestFrame, frame); // latest-wins; the timer drains at its own pace
    }

    /// <summary>Timer callback (threadpool). Pushes at most one frame per tick; never throws.</summary>
    private void PushLatestFrame()
    {
        if (Interlocked.Exchange(ref _pushing, 1) == 1)
        {
            return; // a slow SDK call is still in flight — skip this tick instead of queueing
        }
        try
        {
            var frame = Volatile.Read(ref _latestFrame);
            IRgbDeviceBackend? backend;
            IReadOnlyList<RgbBackendDevice> devices;
            IReadOnlyDictionary<string, DevicePlacement> placements;
            float brightness;
            bool audioReactive;
            float audioDepth;
            lock (_gate)
            {
                if (frame is null || ReferenceEquals(frame, _pushedFrame))
                {
                    return; // nothing new since the last push
                }
                backend = _backend;
                devices = _devices;
                placements = _placements;
                brightness = _brightness;
                audioReactive = _audioReactive;
                audioDepth = _audioDepth;
                _pushedFrame = frame;
            }
            if (backend is null || devices.Count == 0)
            {
                return;
            }

            // Audio layer (Story 8.3 AC4): a brightness modulation on top of the
            // position-mapped colors — one factor per frame, no extra hardware pushes.
            float audioFactor = audioReactive
                ? AudioModulation.BrightnessFactor(frame.Audio?.Intensity ?? 0f, audioDepth)
                : 1f;

            var push = new List<RgbDeviceColors>(devices.Count);
            foreach (var device in devices)
            {
                placements.TryGetValue(device.Id, out var placement);
                // Excluded devices go dark (brightness 0) rather than freezing on the last
                // frame; per-device brightness stacks on the global peripheral brightness.
                float effective = placement is { Enabled: false }
                    ? 0f
                    : brightness * (placement?.Brightness ?? 1f) * audioFactor;
                push.Add(new RgbDeviceColors
                {
                    DeviceId = device.Id,
                    Colors = LedProjection.Project(
                        device.NormalizedLeds, frame.Edges, effective,
                        placement?.Anchor, placement?.Flip ?? false),
                });
            }
            backend.Apply(push);
        }
        catch (Exception ex)
        {
            // Never propagate out of the timer (NFR5); throttle so a wedged SDK can't spam the log.
            if (DateTime.UtcNow - _lastPushErrorUtc >= PushErrorLogInterval)
            {
                _lastPushErrorUtc = DateTime.UtcNow;
                _logger.LogWarning(ex, "Failed to push colors to ambient devices");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pushing, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        Stop();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ambient device StateChanged handler threw");
        }
    }

    private void DisposeBackendQuietly(IRgbDeviceBackend? backend)
    {
        try
        {
            backend?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error releasing the ambient device backend");
        }
    }
}

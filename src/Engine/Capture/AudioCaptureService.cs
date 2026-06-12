using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Timer = System.Threading.Timer;

namespace AmbientFx.Capture;

/// <summary>
/// Captures system audio via WASAPI loopback (FR3, spec §5.5) and reduces it to normalized
/// log-spaced frequency bands plus an overall intensity using <see cref="SpectrumAnalyzer"/>.
/// Uses a ~30 ms capture buffer (~66 Hz callback cadence), a <see cref="SilenceProvider"/>
/// keep-alive so loopback packets keep flowing during system silence, a watchdog that emits
/// zeroed bands when no data arrives for 250 ms, and automatic restart when the default render
/// device changes. With no audio device at all, <see cref="Start"/> succeeds in a degraded
/// state (zeroed bands) and recovers when a device appears.
/// </summary>
/// <remarks>
/// Threading: <see cref="AudioAnalyzed"/> is raised on background threads (the NAudio capture
/// thread or a timer thread) — handlers must be fast and must never call <see cref="Stop"/> or
/// <see cref="Dispose"/> reentrantly (teardown joins the capture thread). Start/Stop/Dispose are
/// thread-safe and idempotent. Designed as a DI singleton.
/// </remarks>
public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int FftSize = 2048;              // analysis window (power of two)
    private const int HopSize = 1024;              // new samples between analyses (50% overlap)
    private const int RingSize = 4096;             // mono ring buffer; power of two >= FftSize
    private const int CaptureBufferMs = 30;        // -> ~15 ms / ~66 Hz DataAvailable cadence
    private const int KeepAliveLatencyMs = 50;     // WasapiOut latency for the silence stream
    private const int SilenceTimeoutMs = 250;      // no data for this long -> watchdog emits zeros
    private const int WatchdogPeriodMs = 33;       // zero-band emission cadence (~30 Hz)
    private const int DeviceChangeDebounceMs = 300;
    private const int DefaultBandCount = 12;

    /// <summary>One monotonic time base shared by all emissions from this service.</summary>
    private static readonly Stopwatch SharedClock = Stopwatch.StartNew();

    private readonly ILogger<AudioCaptureService> _logger;

    /// <summary>Guards Start/Stop/Dispose and session swaps. NEVER taken on the capture thread
    /// (Stop holds it while joining that thread — taking it there would deadlock).</summary>
    private readonly object _gate = new();

    // Keep both the enumerator and the notification client referenced for the service lifetime:
    // COM holds a raw pointer to the client while registered (GC of either = crash).
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly DeviceNotificationClient _notificationClient;
    private readonly bool _notificationsRegistered;

    private CaptureSession? _session;              // written under _gate; Volatile.Read elsewhere
    private Timer? _watchdogTimer;                 // lifetime: Start..Stop
    private Timer? _restartTimer;                  // debounce; swapped via Interlocked (COM thread)
    private long _lastDataTicks;                   // Environment.TickCount64 of last non-empty packet
    private volatile int _bandCount = DefaultBandCount;
    private volatile bool _started;
    private volatile bool _disposed;
    private bool _noDeviceErrorRaised;             // raise the degraded-state Error once per outage

    /// <summary>Creates the service and registers for audio endpoint notifications.</summary>
    public AudioCaptureService(ILogger<AudioCaptureService> logger)
    {
        _logger = logger;
        _notificationClient = new DeviceNotificationClient(this);
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            _notificationsRegistered = true;
        }
        catch (Exception ex)
        {
            // No COM audio stack at all (rare). Start() will run degraded; no device-change retry.
            _logger.LogWarning(ex, "Audio device enumerator unavailable; device-change handling disabled.");
        }
    }

    /// <inheritdoc />
    /// <remarks>True between Start and Stop, including the degraded no-device state
    /// (in which zeroed bands are still emitted).</remarks>
    public bool IsCapturing => _started;

    /// <inheritdoc />
    /// <remarks>Settable while running: the analyzer is swapped atomically and takes effect on
    /// the next analysis window, keeping the current sample rate. Thread-safe.</remarks>
    public int BandCount
    {
        get => _bandCount;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Band count must be at least 1.");
            if (value == _bandCount)
                return;

            _bandCount = value;
            CaptureSession? session = Volatile.Read(ref _session);
            if (session is not null)
            {
                try
                {
                    Volatile.Write(ref session.Analyzer, new SpectrumAnalyzer(session.SampleRate, value, FftSize));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to rebuild spectrum analyzer for {BandCount} bands.", value);
                }
            }
            _logger.LogInformation("Audio band count set to {BandCount}.", value);
        }
    }

    /// <inheritdoc />
    public event EventHandler<AudioAnalysisEventArgs>? AudioAnalyzed;

    /// <inheritdoc />
    public event EventHandler<PipelineErrorEventArgs>? Error;

    /// <summary>
    /// Starts loopback capture, the silence keep-alive and the watchdog. Succeeds even with no
    /// audio device (degraded state: zeroed bands until a device appears). Idempotent.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            _started = true;
            _noDeviceErrorRaised = false;
            // Pre-stale timestamp: the watchdog emits zeros immediately until real data arrives.
            Volatile.Write(ref _lastDataTicks, Environment.TickCount64 - SilenceTimeoutMs - 1);
            StartSessionLocked();
            _watchdogTimer = new Timer(OnWatchdogTick, null, WatchdogPeriodMs, WatchdogPeriodMs);
            _logger.LogInformation("Audio capture started{Degraded}.",
                Volatile.Read(ref _session) is null ? " (degraded: no audio device)" : string.Empty);
        }
    }

    /// <summary>Stops capture, keep-alive and watchdog. Idempotent. Must not be called from an
    /// <see cref="AudioAnalyzed"/> handler (teardown joins the capture thread).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed || !_started)
                return;

            _started = false;
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            Interlocked.Exchange(ref _restartTimer, null)?.Dispose();
            StopSessionLocked();
            _logger.LogInformation("Audio capture stopped.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _started = false;
            if (_notificationsRegistered)
            {
                try
                {
                    _enumerator!.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unregistering audio device notifications threw during dispose.");
                }
            }
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            Interlocked.Exchange(ref _restartTimer, null)?.Dispose();
            StopSessionLocked();
            _enumerator?.Dispose();
        }
    }

    // ---- Session lifecycle (always under _gate). ----

    private void StartSessionLocked()
    {
        LowLatencyLoopbackCapture? capture = null;
        try
        {
            if (_enumerator is null)
                throw new InvalidOperationException("Audio device enumeration is unavailable.");

            MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            capture = new LowLatencyLoopbackCapture(device, CaptureBufferMs);

            // Read the ACTUAL shared-mode mix format — float32, but rate/channels vary per device.
            WaveFormat format = capture.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new NotSupportedException($"Unexpected loopback format '{format.Encoding}' (expected IEEE float).");

            var session = new CaptureSession(capture, format.SampleRate, format.Channels,
                new SpectrumAnalyzer(format.SampleRate, _bandCount, FftSize));
            capture.DataAvailable += (_, e) => OnDataAvailable(session, e);
            capture.RecordingStopped += (_, e) => OnRecordingStopped(session, e);
            capture.StartRecording();

            Volatile.Write(ref _session, session);
            _noDeviceErrorRaised = false;
            _logger.LogInformation("Loopback capture running: {SampleRate} Hz, {Channels} ch, device '{Device}'.",
                format.SampleRate, format.Channels, device.FriendlyName);

            StartKeepAliveLocked(session);
        }
        catch (Exception ex)
        {
            try
            {
                capture?.Dispose();
            }
            catch (Exception disposeEx)
            {
                _logger.LogDebug(disposeEx, "Disposing partially started capture threw.");
            }
            Volatile.Write(ref _session, null);

            // Degraded state (headless/RDP/no endpoint): the watchdog keeps emitting zeroed
            // bands; we retry on device notifications. Surface once, informationally (NFR5).
            if (!_noDeviceErrorRaised)
            {
                _noDeviceErrorRaised = true;
                _logger.LogInformation(ex,
                    "No usable audio render device; running degraded (silent bands) until one appears.");
                RaiseError("Audio capture is idle: no audio output device is available. " +
                           "Visuals will react to screen content only.", ex);
            }
        }
    }

    private void StartKeepAliveLocked(CaptureSession session)
    {
        try
        {
            // Fresh MMDevice instance — never share the capture's MMDevice with WasapiOut.
            MMDevice renderDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var silenceOut = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true, KeepAliveLatencyMs);
            silenceOut.Init(new SilenceProvider(silenceOut.OutputWaveFormat));
            silenceOut.Play();
            session.SilenceOut = silenceOut;
            _logger.LogDebug("Silence keep-alive playing at {Latency} ms latency.", KeepAliveLatencyMs);
        }
        catch (Exception ex)
        {
            // Non-fatal: without the keep-alive, loopback yields no packets during total system
            // silence — the watchdog covers that case by emitting zeroed bands.
            _logger.LogWarning(ex, "Silence keep-alive failed to start; relying on the watchdog during silence.");
        }
    }

    private void StopSessionLocked()
    {
        CaptureSession? session = Volatile.Read(ref _session);
        Volatile.Write(ref _session, null);
        if (session is null)
            return;

        session.Stopped = true; // in-flight capture-thread callbacks bail out
        try
        {
            session.Capture.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StopRecording threw during teardown.");
        }
        try
        {
            // Joins the capture thread. Safe here: this method is never reached from
            // DataAvailable/RecordingStopped (they take no locks and never call teardown).
            session.Capture.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture dispose threw during teardown.");
        }
        try
        {
            session.SilenceOut?.Stop();
            session.SilenceOut?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silence keep-alive dispose threw during teardown.");
        }
    }

    // ---- Capture-thread path (no locks, allocation-light). ----

    /// <summary>
    /// Runs on the dedicated NAudio capture thread. Downmixes interleaved float32 to mono into
    /// the session ring buffer and analyzes the most recent <see cref="FftSize"/> samples every
    /// <see cref="HopSize"/> new samples. Never takes <c>_gate</c> and never tears down the
    /// capture (Dispose from here would self-deadlock).
    /// </summary>
    private void OnDataAvailable(CaptureSession session, WaveInEventArgs e)
    {
        try
        {
            if (_disposed || session.Stopped)
                return;
            if (e.BytesRecorded == 0)
                return; // normal silence heartbeat in sleep-polling mode — not an error

            Volatile.Write(ref _lastDataTicks, Environment.TickCount64);

            // Buffer is reused and oversized — only BytesRecorded bytes are valid.
            ReadOnlySpan<float> interleaved = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            int channels = session.Channels;
            int frames = interleaved.Length / channels;
            float[] ring = session.Ring;

            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int baseIndex = f * channels;
                for (int c = 0; c < channels; c++)
                    sum += interleaved[baseIndex + c];

                ring[session.WritePos] = sum / channels;
                session.WritePos = (session.WritePos + 1) & (RingSize - 1);
                session.TotalWritten++;

                if (session.TotalWritten >= session.NextAnalysisAt)
                {
                    AnalyzeLatestWindow(session);
                    session.NextAnalysisAt = session.TotalWritten + HopSize;
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the NAudio capture loop (it would kill the session).
            _logger.LogError(ex, "Audio analysis failed in DataAvailable.");
        }
    }

    private void AnalyzeLatestWindow(CaptureSession session)
    {
        // Copy the most recent FftSize samples out of the ring in chronological order.
        float[] ring = session.Ring;
        float[] window = session.Window;
        int start = (session.WritePos - FftSize) & (RingSize - 1);
        int firstRun = Math.Min(FftSize, RingSize - start);
        Array.Copy(ring, start, window, 0, firstRun);
        if (firstRun < FftSize)
            Array.Copy(ring, 0, window, firstRun, FftSize - firstRun);

        SpectrumAnalyzer analyzer = Volatile.Read(ref session.Analyzer); // BandCount swaps land here
        (float[] bands, float intensity) = analyzer.Analyze(window);
        RaiseAudioAnalyzed(bands, intensity);
    }

    /// <summary>
    /// May run on the capture thread (no SynchronizationContext at construction time).
    /// Must not take <c>_gate</c> or tear down the capture; restart is scheduled instead.
    /// </summary>
    private void OnRecordingStopped(CaptureSession session, StoppedEventArgs e)
    {
        if (_disposed || session.Stopped || !_started)
            return; // deliberate teardown

        _logger.LogWarning(e.Exception, "Loopback capture stopped unexpectedly; scheduling restart.");
        RaiseError("Audio capture stopped unexpectedly; restarting.", e.Exception);
        ScheduleRestart();
    }

    // ---- Watchdog (threadpool timer, ~30 Hz). ----

    /// <summary>If no real audio data arrived for &gt;250 ms (keep-alive failed, device wedged,
    /// or no device at all), emits zeroed bands so consumers decay to silence; stops emitting
    /// automatically as soon as data resumes.</summary>
    private void OnWatchdogTick(object? state)
    {
        try
        {
            if (_disposed || !_started)
                return;
            if (Environment.TickCount64 - Volatile.Read(ref _lastDataTicks) <= SilenceTimeoutMs)
                return;

            RaiseAudioAnalyzed(new float[_bandCount], 0f);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio watchdog tick failed.");
        }
    }

    // ---- Device-change handling (COM callback thread -> debounced threadpool restart). ----

    private void OnDefaultRenderDeviceChanged()
    {
        if (_disposed || !_started)
            return;
        _logger.LogInformation("Default render device changed; restarting audio capture in {Debounce} ms.",
            DeviceChangeDebounceMs);
        ScheduleRestart();
    }

    /// <summary>Retry path for the degraded no-device state when a device appears/activates.</summary>
    private void OnDevicesPossiblyAvailable()
    {
        if (_disposed || !_started)
            return;
        if (Volatile.Read(ref _session) is not null)
            return; // healthy — only the default-device-changed path restarts a live session
        ScheduleRestart();
    }

    /// <summary>
    /// Debounces restarts by 300 ms onto a threadpool timer. Safe to call from COM callback or
    /// capture threads — the actual restart never runs on the calling thread.
    /// </summary>
    private void ScheduleRestart()
    {
        var timer = new Timer(OnRestartTimer, null, DeviceChangeDebounceMs, Timeout.Infinite);
        Interlocked.Exchange(ref _restartTimer, timer)?.Dispose();
        if (_disposed || !_started)
            Interlocked.Exchange(ref _restartTimer, null)?.Dispose(); // lost race with Dispose/Stop:
            // an armed timer installed after Stop's exchange would otherwise survive into (and
            // needlessly restart) the next session started within the 300 ms debounce window.
    }

    private void OnRestartTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || !_started)
                return;

            _logger.LogInformation("Restarting loopback capture and silence keep-alive.");
            StopSessionLocked();
            // Watchdog covers the gap until the new session delivers data (or stays degraded).
            Volatile.Write(ref _lastDataTicks, Environment.TickCount64 - SilenceTimeoutMs - 1);
            StartSessionLocked();
        }
    }

    private void RaiseAudioAnalyzed(float[] bands, float intensity)
    {
        try
        {
            AudioAnalyzed?.Invoke(this, new AudioAnalysisEventArgs
            {
                Bands = bands,
                Intensity = intensity,
                TimestampMs = SharedClock.Elapsed.TotalMilliseconds,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioAnalyzed subscriber threw.");
        }
    }

    private void RaiseError(string message, Exception? exception)
    {
        try
        {
            Error?.Invoke(this, new PipelineErrorEventArgs
            {
                Source = "audio",
                Message = message,
                Exception = exception,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscriber threw.");
        }
    }

    /// <summary>
    /// State for one capture session (one device, one NAudio capture instance). Ring buffer,
    /// window scratch and counters are touched only on that session's capture thread.
    /// </summary>
    private sealed class CaptureSession
    {
        public CaptureSession(LowLatencyLoopbackCapture capture, int sampleRate, int channels, SpectrumAnalyzer analyzer)
        {
            Capture = capture;
            SampleRate = sampleRate;
            Channels = channels;
            Analyzer = analyzer;
        }

        public LowLatencyLoopbackCapture Capture { get; }
        public WasapiOut? SilenceOut { get; set; }
        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>Swapped atomically (Volatile) on BandCount changes; read once per window.</summary>
        public SpectrumAnalyzer Analyzer;

        /// <summary>Set (under the service gate) before teardown so late callbacks bail out.</summary>
        public volatile bool Stopped;

        // Mono ring buffer — capture thread only.
        public readonly float[] Ring = new float[RingSize];
        public readonly float[] Window = new float[FftSize];
        public int WritePos;
        public long TotalWritten;
        public long NextAnalysisAt = FftSize; // first analysis once FftSize samples are buffered
    }

    /// <summary>
    /// Endpoint notification sink. Callbacks arrive on a COM worker thread, and
    /// OnDefaultDeviceChanged fires up to once per Role per switch — filtered to
    /// (Render, Multimedia) here and debounced upstream. The owning service keeps this instance
    /// and the enumerator referenced while registered (COM holds a raw pointer).
    /// </summary>
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioCaptureService _owner;

        public DeviceNotificationClient(AudioCaptureService owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
                _owner.OnDefaultRenderDeviceChanged();
        }

        public void OnDeviceAdded(string pwstrDeviceId) => _owner.OnDevicesPossiblyAvailable();

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active)
                _owner.OnDevicesPossiblyAvailable();
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}

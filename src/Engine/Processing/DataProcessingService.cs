using System.Diagnostics;
using AmbientFx.Bridge;
using AmbientFx.Capture;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Processing;

/// <summary>
/// Combines the latest downscaled screen frame and raw audio analysis into the bridge
/// <see cref="FramePayload"/>: edge-zone extraction + dominant color, temporal smoothing,
/// audio gain/envelope shaping, idle detection (NFR1) and MaxFps coalescing.
///
/// Threading model: capture/audio events only snapshot the newest data under a lock (never
/// blocking the producers); a single background tick loop (Stopwatch-paced, retimed live when
/// MaxFps changes or idle mode toggles) does all processing and raises <see cref="FrameReady"/>.
/// Designed as a DI singleton with idempotent Start/Stop. Processing failures are logged and the
/// tick is skipped — the host never crashes (NFR5).
/// </summary>
public sealed class DataProcessingService : IDataProcessingService
{
    /// <summary>Mean absolute byte difference below which consecutive frames count as unchanged.</summary>
    internal const double IdlePixelDiffThreshold = 1.5;

    /// <summary>Shaped audio intensity below which audio counts as silent for idle detection.</summary>
    internal const float IdleIntensityThreshold = 0.02f;

    /// <summary>Consecutive quiet ticks required before entering idle mode.</summary>
    internal const int IdleTickThreshold = 30;

    /// <summary>Tick rate while idle.</summary>
    private const double IdleFps = 5.0;

    private const int MinFps = 1;
    private const int MaxFpsCeiling = 240;

    private readonly IScreenCaptureService _screenCapture;
    private readonly IAudioCaptureService _audioCapture;
    private readonly ILogger<DataProcessingService> _logger;

    /// <summary>Guards Start/Stop/Dispose lifecycle state.</summary>
    private readonly object _lifecycleLock = new();

    /// <summary>Guards the latest-input snapshots written by the capture/audio event handlers.</summary>
    private readonly object _stateLock = new();

    /// <summary>Guards the smoother/shaper/options used by the tick and mutated by UpdateOptions.</summary>
    private readonly object _processLock = new();

    private readonly TemporalSmoother _smoother = new();
    private readonly AudioShaper _shaper = new();

    // ---- Latest inputs (under _stateLock). Producers copy in; the tick copies out. ----
    private byte[] _latestPixels = Array.Empty<byte>();
    private int _latestWidth;
    private int _latestHeight;
    private bool _hasFrame;
    private float[] _latestBands = Array.Empty<float>(); // replaced wholesale per audio event (copy-on-write)
    private float _latestIntensity;

    // ---- Options (under _processLock; _maxFps also read lock-free by the loop). ----
    private int _zonesPerEdge = 8;
    private int _maxFps = 60;

    // ---- Tick-thread-owned working state (only touched by the loop, or in Start while stopped). ----
    private byte[] _curPixels = Array.Empty<byte>();
    private byte[] _prevPixels = Array.Empty<byte>();
    private int _curLen;
    private int _prevLen;
    private int _curWidth;
    private int _curHeight;
    private bool _hasPrev;
    private int _idleTickCount;
    private bool _isIdle;
    private long _tickErrorCount;

    // ---- Loop control ----
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Stopwatch _clock = new();
    private int _tickThreadId = -1; // managed thread id while a tick executes; -1 otherwise
    private bool _disposed;

    public DataProcessingService(
        IScreenCaptureService screenCapture,
        IAudioCaptureService audioCapture,
        ILogger<DataProcessingService> logger)
    {
        _screenCapture = screenCapture ?? throw new ArgumentNullException(nameof(screenCapture));
        _audioCapture = audioCapture ?? throw new ArgumentNullException(nameof(audioCapture));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler<FrameReadyEventArgs>? FrameReady;

    /// <summary>
    /// Subscribes to the capture services and starts the tick loop. Idempotent: calling while
    /// running is a no-op. Per-run state (smoothing history, idle counters, clock) is reset.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts is not null)
            {
                _logger.LogDebug("DataProcessingService.Start ignored: already running.");
                return;
            }

            lock (_stateLock)
            {
                _hasFrame = false;
                _latestWidth = 0;
                _latestHeight = 0;
                _latestBands = Array.Empty<float>();
                _latestIntensity = 0f;
            }

            lock (_processLock)
            {
                _smoother.Reset();
                _shaper.Reset();
            }

            // Tick-thread state: safe to touch here because the loop is not running.
            _curLen = 0;
            _prevLen = 0;
            _curWidth = 0;
            _curHeight = 0;
            _hasPrev = false;
            _idleTickCount = 0;
            _isIdle = false;
            _tickErrorCount = 0;

            _clock = Stopwatch.StartNew();
            _screenCapture.FrameCaptured += OnFrameCaptured;
            _audioCapture.AudioAnalyzed += OnAudioAnalyzed;

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);

            _logger.LogInformation(
                "Data processing started (zonesPerEdge={Zones}, maxFps={MaxFps}).",
                _zonesPerEdge, Volatile.Read(ref _maxFps));
        }
    }

    /// <summary>
    /// Unsubscribes from the capture services and stops the tick loop. Idempotent. Safe to call
    /// from a FrameReady handler (it will not wait on its own loop in that case).
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lifecycleLock)
        {
            if (_cts is null) return;
            _screenCapture.FrameCaptured -= OnFrameCaptured;
            _audioCapture.AudioAnalyzed -= OnAudioAnalyzed;
            cts = _cts;
            _cts = null;
            loop = _loopTask;
            _loopTask = null;
        }

        cts.Cancel();

        // Wait briefly for a clean exit — unless we ARE the tick thread (Stop called from a
        // FrameReady handler), where waiting would stall on ourselves.
        bool onTickThread = Environment.CurrentManagedThreadId == Volatile.Read(ref _tickThreadId);
        if (!onTickThread && loop is not null)
        {
            try
            {
                loop.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException)
            {
                // Loop faults are already logged inside RunLoopAsync.
            }
        }

        cts.Dispose();
        _logger.LogInformation("Data processing stopped.");
    }

    /// <summary>
    /// Live-updates processing parameters from any thread: a zone-count change resets the smoother,
    /// MaxFps retimes the loop on its next tick, sensitivity reconfigures the audio shaper, and
    /// smoothing adjusts the EMA alpha. Values are clamped to sane ranges.
    /// </summary>
    public void UpdateOptions(ProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_processLock)
        {
            int zones = Math.Max(1, options.ZonesPerEdge);
            if (zones != _zonesPerEdge)
            {
                _zonesPerEdge = zones;
                _smoother.Reset(); // per-zone state is meaningless across a zone-count change
            }

            _smoother.SetSmoothing(options.Smoothing);
            _shaper.Configure(options.AudioSensitivity);
            Volatile.Write(ref _maxFps, Math.Clamp(options.MaxFps, MinFps, MaxFpsCeiling));

            _logger.LogDebug(
                "Processing options updated: zonesPerEdge={Zones}, smoothing={Smoothing}, audioSensitivity={Sensitivity}, maxFps={MaxFps}.",
                zones, options.Smoothing, options.AudioSensitivity, Volatile.Read(ref _maxFps));
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    /// <summary>
    /// Mean absolute per-byte difference between two buffers (idle detection metric).
    /// Buffers of different lengths count as maximally different (255); two empty buffers as 0.
    /// </summary>
    internal static double MeanAbsoluteDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return 255.0;
        if (a.IsEmpty) return 0.0;

        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);

        return sum / (double)a.Length;
    }

    // ---- Producers: store the newest data and return immediately (capture/audio threads). ----

    private void OnFrameCaptured(object? sender, ScreenFrameEventArgs e)
    {
        try
        {
            byte[]? pixels = e.PixelsBgra;
            long required = (long)e.Width * e.Height * 4;
            if (pixels is null || e.Width <= 0 || e.Height <= 0 || pixels.Length < required)
            {
                _logger.LogWarning(
                    "Ignoring malformed captured frame: {Width}x{Height}, buffer {Length} bytes.",
                    e.Width, e.Height, pixels?.Length ?? 0);
                return;
            }

            int len = (int)required;
            lock (_stateLock)
            {
                // Copy: the producer may reuse its buffer after the event returns.
                if (_latestPixels.Length < len) _latestPixels = new byte[len];
                Buffer.BlockCopy(pixels, 0, _latestPixels, 0, len);
                _latestWidth = e.Width;
                _latestHeight = e.Height;
                _hasFrame = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store captured frame.");
        }
    }

    private void OnAudioAnalyzed(object? sender, AudioAnalysisEventArgs e)
    {
        try
        {
            float[] src = e.Bands ?? Array.Empty<float>();
            // Copy-on-write: a fresh array per event lets the tick take the reference without copying.
            var copy = new float[src.Length];
            Array.Copy(src, copy, src.Length);

            lock (_stateLock)
            {
                _latestBands = copy;
                _latestIntensity = e.Intensity;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store audio analysis.");
        }
    }

    // ---- Tick loop (single background thread at a time). ----

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("Processing tick loop started.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double tickStartMs = _clock.Elapsed.TotalMilliseconds;

                Volatile.Write(ref _tickThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    FrameReadyEventArgs? args = null;
                    try
                    {
                        args = BuildFrame();
                    }
                    catch (Exception ex)
                    {
                        long count = ++_tickErrorCount;
                        if (count == 1 || count % 300 == 0) // rate-limit: first failure, then every ~5s at 60fps
                            _logger.LogError(ex, "Processing tick failed ({Count} failures so far); frame skipped.", count);
                    }

                    if (args is not null && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            FrameReady?.Invoke(this, args);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "A FrameReady subscriber threw; continuing.");
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _tickThreadId, -1);
                }

                // Re-read pacing every iteration so MaxFps changes and idle transitions retime
                // the loop immediately without recreating any timer.
                double periodMs = _isIdle
                    ? 1000.0 / IdleFps
                    : 1000.0 / Volatile.Read(ref _maxFps);
                double waitMs = periodMs - (_clock.Elapsed.TotalMilliseconds - tickStartMs);

                if (waitMs >= 1.0)
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                else
                    await Task.Yield(); // overloaded tick: don't spin the thread pool dry
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Should be unreachable (ticks are individually guarded) — but never crash the host (NFR5).
            _logger.LogError(ex, "Processing tick loop terminated unexpectedly.");
        }
        finally
        {
            _logger.LogDebug("Processing tick loop exited.");
        }
    }

    /// <summary>Runs one tick: snapshot inputs, extract + smooth + shape, update idle state, build the payload.</summary>
    private FrameReadyEventArgs BuildFrame()
    {
        bool hasFrame;
        float[] rawBands;
        float rawIntensity;

        lock (_stateLock)
        {
            hasFrame = _hasFrame;
            if (hasFrame)
            {
                int len = _latestWidth * _latestHeight * 4;

                // Keep the previous tick's pixels for the idle diff, then copy the newest in.
                (_prevPixels, _curPixels) = (_curPixels, _prevPixels);
                _prevLen = _curLen;
                _hasPrev = _curLen > 0;

                if (_curPixels.Length < len) _curPixels = new byte[len];
                Buffer.BlockCopy(_latestPixels, 0, _curPixels, 0, len);
                _curLen = len;
                _curWidth = _latestWidth;
                _curHeight = _latestHeight;
            }

            rawBands = _latestBands; // safe: handler replaces the array, never mutates it
            rawIntensity = _latestIntensity;
        }

        FramePayload payload;
        lock (_processLock)
        {
            int zones = _zonesPerEdge;

            EdgeColors rawEdges;
            int[] rawDominant;
            if (hasFrame && _curLen > 0)
            {
                var span = new ReadOnlySpan<byte>(_curPixels, 0, _curLen);
                rawEdges = EdgeZoneExtractor.ExtractEdges(span, _curWidth, _curHeight, zones);
                rawDominant = EdgeZoneExtractor.ExtractDominant(span, _curWidth, _curHeight);
            }
            else
            {
                // No screen frame yet: emit black zones of the correct length so audio-only
                // effects still animate before capture delivers (or if it never does).
                rawEdges = CreateBlackEdges(zones);
                rawDominant = new int[3];
            }

            EdgeColors edges = _smoother.Smooth(rawEdges);
            int[] dominant = _smoother.SmoothDominant(rawDominant);
            (float[] shapedBands, float shapedIntensity) = _shaper.Shape(rawBands, rawIntensity);

            UpdateIdleState(shapedIntensity);

            payload = new FramePayload
            {
                T = _clock.Elapsed.TotalMilliseconds,
                Edges = edges,
                Dominant = dominant,
                Audio = new AudioData { Intensity = shapedIntensity, Bands = shapedBands },
            };
        }

        return new FrameReadyEventArgs { Frame = payload };
    }

    /// <summary>
    /// Idle detection (NFR1): after <see cref="IdleTickThreshold"/> consecutive ticks with an
    /// unchanged screen (mean abs byte diff &lt; 1.5) AND near-silent shaped audio (&lt; 0.02),
    /// the loop drops to 5 fps. Any pixel or audio change exits idle on the tick that observes it.
    /// </summary>
    private void UpdateIdleState(float shapedIntensity)
    {
        double diff;
        if (_curLen > 0 && _hasPrev)
        {
            diff = MeanAbsoluteDiff(
                new ReadOnlySpan<byte>(_prevPixels, 0, _prevLen),
                new ReadOnlySpan<byte>(_curPixels, 0, _curLen));
        }
        else if (_curLen > 0)
        {
            diff = 255.0; // first frame ever = content appeared = a change
        }
        else
        {
            diff = 0.0; // no screen frames at all: only audio can hold us out of idle
        }

        bool quiet = diff < IdlePixelDiffThreshold && shapedIntensity < IdleIntensityThreshold;
        if (quiet)
        {
            if (_idleTickCount < int.MaxValue) _idleTickCount++;
            if (!_isIdle && _idleTickCount >= IdleTickThreshold)
            {
                _isIdle = true;
                _logger.LogDebug("Entering idle mode (unchanged screen + silent audio); ticking at {IdleFps} fps.", IdleFps);
            }
        }
        else
        {
            if (_isIdle)
                _logger.LogDebug("Exiting idle mode (diff={Diff:F2}, intensity={Intensity:F3}).", diff, shapedIntensity);
            _idleTickCount = 0;
            _isIdle = false;
        }
    }

    private static EdgeColors CreateBlackEdges(int zones)
    {
        return new EdgeColors
        {
            Top = BlackZones(zones),
            Bottom = BlackZones(zones),
            Left = BlackZones(zones),
            Right = BlackZones(zones),
        };

        static int[][] BlackZones(int count)
        {
            var result = new int[count][];
            for (int i = 0; i < count; i++)
                result[i] = new int[3];
            return result;
        }
    }
}

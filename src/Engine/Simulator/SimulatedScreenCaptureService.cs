#if SIMULATOR_ENABLED
using System.Diagnostics;
using AmbientFx.Capture;
using AmbientFx.Models;
using AmbientFx.Simulator.Content;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator). A drop-in <see cref="IScreenCaptureService"/> that emits
/// synthetic test-pattern BGRA for the started monitor at that monitor's source resolution, instead
/// of capturing a real display via WGC. It honors the established frame contract exactly: tightly
/// packed 32-bit BGRA (B@0/G@1/R@2/A@3), top-down rows, no stride padding, and a buffer that is
/// <b>reused</b> per frame (subscribers consume synchronously). Emission is rate-limited to a
/// per-monitor (or default) <c>maxFps</c>. Start/Stop/Dispose are idempotent and never throw back into
/// the pipeline (NFR5); internal faults surface via <see cref="Error"/>. Compiled out of Release via
/// <c>SIMULATOR_ENABLED</c>.
/// </summary>
public sealed class SimulatedScreenCaptureService : IScreenCaptureService
{
    private const string ErrorSource = "capture";

    /// <summary>Shared monotonic clock for <see cref="ScreenFrameEventArgs.TimestampMs"/>.</summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly ILogger<SimulatedScreenCaptureService> _logger;

    /// <summary>Guards pipeline state and serializes frame generation against Start/Stop/Dispose.</summary>
    private readonly object _gate = new();

    private readonly Dictionary<string, (string Pattern, int MaxFps)> _perMonitor =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-monitor content strategy (Story 10.3); absent = the synthetic pattern from 10.1.</summary>
    private readonly Dictionary<string, ISimContentSource> _contentSources =
        new(StringComparer.OrdinalIgnoreCase);

    // ── pipeline state (under _gate) ───────────────────────────────────────────────────────────────
    private Timer? _timer;
    private byte[] _buffer = Array.Empty<byte>(); // reused per frame — no per-frame allocation once warm
    private string? _monitorId;
    private string _pattern = SyntheticPatterns.Gradient;
    private ISimContentSource? _activeSource;
    private int _maxFps = 60;
    private int _width;
    private int _height;
    private long _frameIndex;
    private bool _isCapturing;
    private bool _disposed;

    /// <summary>0/1 reentrancy guard: a slow subscriber means the next tick is skipped, never queued.</summary>
    private int _frameInFlight;

    /// <summary>Pattern used when the started monitor has no per-monitor configuration.</summary>
    public string DefaultPattern { get; set; } = SyntheticPatterns.Gradient;

    /// <summary>Frame-rate ceiling used when the started monitor has no per-monitor configuration.</summary>
    public int DefaultMaxFps { get; set; } = 60;

    /// <summary>
    /// Optional live-resolution resolver, keyed by monitor id. When set, each tick re-reads the active
    /// monitor's current size and reallocates the buffer if it changed — so the simulated source follows
    /// a live <c>SetResolution</c> mutation mid-stream without a Start() recall, mirroring the real
    /// <see cref="Capture.ScreenCaptureService"/>'s ContentSize self-heal (it never depends on Start
    /// being re-called). Null (the default, and for standalone unit tests) keeps the fixed size from Start.
    /// </summary>
    public Func<string, (int Width, int Height)?>? MonitorResolver { get; set; }

    public SimulatedScreenCaptureService(ILogger<SimulatedScreenCaptureService> logger) => _logger = logger;

    /// <inheritdoc />
    public event EventHandler<ScreenFrameEventArgs>? FrameCaptured;

    /// <inheritdoc />
    public event EventHandler<PipelineErrorEventArgs>? Error;

    /// <inheritdoc />
    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _isCapturing;
            }
        }
    }

    /// <summary>
    /// Associates a synthetic pattern and frame-rate ceiling with a monitor id. Looked up at
    /// <see cref="Start"/> time; falls back to <see cref="DefaultPattern"/>/<see cref="DefaultMaxFps"/>.
    /// </summary>
    public void ConfigureMonitor(string monitorId, string pattern, int maxFps)
    {
        if (string.IsNullOrEmpty(monitorId))
        {
            return;
        }
        lock (_gate)
        {
            _perMonitor[monitorId] = (SyntheticPatterns.Normalize(pattern), Math.Clamp(maxFps <= 0 ? 60 : maxFps, 1, 240));
        }
    }

    /// <summary>
    /// Assigns a content strategy (media/mirror/etc.) to a monitor (Story 10.3). Absent = the synthetic
    /// pattern from <see cref="ConfigureMonitor"/>. If the monitor is the one currently capturing, the
    /// swap is live (no Stop/Start needed). Passing null clears the assignment back to synthetic. The
    /// service takes ownership and disposes assigned sources on Stop/Dispose.
    /// </summary>
    public void SetContentSource(string monitorId, ISimContentSource? source)
    {
        if (string.IsNullOrEmpty(monitorId))
        {
            return;
        }
        ISimContentSource? previous = null;
        lock (_gate)
        {
            _contentSources.TryGetValue(monitorId, out previous);
            if (source is null)
            {
                _contentSources.Remove(monitorId);
            }
            else
            {
                _contentSources[monitorId] = source;
            }
            if (_isCapturing && string.Equals(_monitorId, monitorId, StringComparison.OrdinalIgnoreCase))
            {
                _activeSource = source; // live swap
            }
        }
        if (previous is not null && !ReferenceEquals(previous, source))
        {
            try { previous.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing a replaced content source failed."); }
        }
    }

    /// <inheritdoc />
    public void Start(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        PipelineErrorEventArgs? error = null;

        lock (_gate)
        {
            if (_disposed)
            {
                _logger.LogWarning("Start({MonitorId}) ignored: the simulated capture service is disposed.", monitor.Id);
                return;
            }

            if (_isCapturing && string.Equals(_monitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Already simulating monitor {MonitorId}; Start is a no-op.", monitor.Id);
                return;
            }

            StopLocked(); // clean switch (no-op when idle)

            try
            {
                _width = Math.Max(1, monitor.Width);
                _height = Math.Max(1, monitor.Height);
                int requiredBytes = checked(_width * _height * 4);
                if (_buffer.Length != requiredBytes)
                {
                    _buffer = new byte[requiredBytes];
                }

                if (_perMonitor.TryGetValue(monitor.Id, out var cfg))
                {
                    _pattern = cfg.Pattern;
                    _maxFps = cfg.MaxFps;
                }
                else
                {
                    _pattern = SyntheticPatterns.Normalize(DefaultPattern);
                    _maxFps = Math.Clamp(DefaultMaxFps <= 0 ? 60 : DefaultMaxFps, 1, 240);
                }

                _activeSource = _contentSources.GetValueOrDefault(monitor.Id);
                _monitorId = monitor.Id;
                _frameIndex = 0;
                _isCapturing = true;

                int intervalMs = Math.Max(1, 1000 / _maxFps);
                _timer = new Timer(OnTick, null, 0, intervalMs);

                _logger.LogInformation(
                    "Simulated capture started on monitor {MonitorId} ({Width}x{Height}); pattern={Pattern}, maxFps={MaxFps}.",
                    monitor.Id, _width, _height, _pattern, _maxFps);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start simulated capture on monitor {MonitorId}.", monitor.Id);
                StopLocked();
                error = new PipelineErrorEventArgs
                {
                    Source = ErrorSource,
                    Message = $"Failed to start simulated capture: {ex.Message}",
                    Exception = ex,
                };
            }
        }

        RaiseError(error); // outside the lock so subscribers may call Start/Stop freely
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            bool wasCapturing = _isCapturing;
            StopLocked();
            if (wasCapturing)
            {
                _logger.LogInformation("Simulated capture stopped.");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<ISimContentSource> sources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopLocked();
            sources = _contentSources.Values.ToList();
            _contentSources.Clear();
            _activeSource = null;
        }
        // Dispose outside the lock — a mirror source stops its real WGC capture.
        foreach (var source in sources)
        {
            try { source.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing a content source failed."); }
        }
    }

    private void StopLocked()
    {
        try
        {
            _timer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure disposing the simulated capture timer during teardown.");
        }
        _timer = null;
        _monitorId = null;
        _isCapturing = false;
    }

    /// <summary>Timer callback (ThreadPool thread): fills the reused buffer and raises a frame.</summary>
    private void OnTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _frameInFlight, 1, 0) != 0)
        {
            return; // previous frame still in flight — drop this one
        }

        byte[]? pixels = null;
        int width = 0;
        int height = 0;
        double timestampMs = 0;
        PipelineErrorEventArgs? error = null;

        try
        {
            lock (_gate)
            {
                if (_disposed || !_isCapturing)
                {
                    return; // stale tick from a torn-down timer
                }

                FollowLiveResolutionLocked();
                if (_activeSource is not null)
                {
                    error = _activeSource.Fill(_buffer, _width, _height, _frameIndex); // media/mirror (NFR5: never throws)
                }
                else
                {
                    SyntheticPatterns.Fill(_pattern, _buffer, _width, _height, _frameIndex);
                }
                _frameIndex++;

                pixels = _buffer;
                width = _width;
                height = _height;
                timestampMs = Clock.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex)
        {
            // NFR5: a bad frame must never take down the host — log and surface via Error.
            _logger.LogError(ex, "Unexpected error generating a simulated frame; the frame was skipped.");
            error = new PipelineErrorEventArgs
            {
                Source = ErrorSource,
                Message = $"Simulated frame generation failed: {ex.Message}",
                Exception = ex,
            };
        }
        finally
        {
            try
            {
                if (pixels is not null)
                {
                    // Raised outside the lock so subscribers may call Start/Stop. The in-flight guard is
                    // still held, so _buffer cannot be overwritten by the next tick while handlers run.
                    FrameCaptured?.Invoke(this, new ScreenFrameEventArgs
                    {
                        PixelsBgra = pixels,
                        Width = width,
                        Height = height,
                        TimestampMs = timestampMs,
                    });
                }

                RaiseError(error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A FrameCaptured/Error subscriber threw; ignoring (NFR5).");
            }
            finally
            {
                Volatile.Write(ref _frameInFlight, 0);
            }
        }
    }

    /// <summary>
    /// Reallocates the reused buffer when the active monitor's live resolution has changed since the
    /// last frame (mirrors the real service's ContentSize-driven resize). Runs under <see cref="_gate"/>.
    /// </summary>
    private void FollowLiveResolutionLocked()
    {
        if (_monitorId is null || MonitorResolver is not { } resolver)
        {
            return;
        }

        if (resolver(_monitorId) is not { } dims)
        {
            return; // monitor gone from the live topology — keep emitting the last known size
        }

        int width = Math.Max(1, dims.Width);
        int height = Math.Max(1, dims.Height);
        if (width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        int requiredBytes = checked(width * height * 4);
        if (_buffer.Length != requiredBytes)
        {
            _buffer = new byte[requiredBytes];
        }
        _logger.LogInformation(
            "Simulated capture followed a live source-resolution change to {Width}x{Height} on {MonitorId}.",
            _width, _height, _monitorId);
    }

    private void RaiseError(PipelineErrorEventArgs? error)
    {
        if (error is null)
        {
            return;
        }

        try
        {
            Error?.Invoke(this, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An Error event subscriber threw; ignoring (NFR5).");
        }
    }
}
#endif

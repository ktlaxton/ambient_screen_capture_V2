#if SIMULATOR_ENABLED
using System.Diagnostics;
using AmbientFx.Capture;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace AmbientFx.Simulator.Capture;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.4). A drop-in <see cref="IAudioCaptureService"/> that emits synthetic
/// bands/intensity on the real ~60 Hz cadence WITHOUT touching WASAPI, so the unmodified real audio path
/// (<c>DataProcessingService.OnAudioAnalyzed</c> → shaping → <c>FramePayload.Audio</c> → audio-reactive
/// effects and <see cref="AmbientFx.Devices.AudioModulation"/> peripheral brightness) all run hardware-
/// and sound-free. The signal is selectable: a 124 bpm "track" (the browser simulator's <c>makeSimFrame</c>
/// audio pattern ported to C#) or a sine sweep. Never throws (NFR5). Compiled out of Release.
/// </summary>
public sealed class SimulatedAudioCaptureService : IAudioCaptureService
{
    public enum Signal
    {
        Track124Bpm,
        SineSweep,
    }

    private const int IntervalMs = 16; // ~60 Hz, matching the browser simulator's 1000/60 cadence

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly ILogger<SimulatedAudioCaptureService> _logger;
    private readonly object _gate = new();

    private Timer? _timer;
    private int _bandCount = 12;
    private bool _capturing;
    private bool _disposed;
    private int _inFlight;

    /// <summary>Which synthetic signal to emit. Settable live.</summary>
    public Signal Mode { get; set; } = Signal.Track124Bpm;

    public SimulatedAudioCaptureService(ILogger<SimulatedAudioCaptureService> logger) => _logger = logger;

    /// <inheritdoc />
    public event EventHandler<AudioAnalysisEventArgs>? AudioAnalyzed;

    /// <inheritdoc />
    public event EventHandler<PipelineErrorEventArgs>? Error;

    /// <inheritdoc />
    public bool IsCapturing
    {
        get { lock (_gate) { return _capturing; } }
    }

    /// <inheritdoc />
    public int BandCount
    {
        get { lock (_gate) { return _bandCount; } }
        set { lock (_gate) { _bandCount = Math.Clamp(value <= 0 ? 12 : value, 1, 512); } }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _capturing)
            {
                return;
            }
            _capturing = true;
            _timer = new Timer(OnTick, null, 0, IntervalMs);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _capturing = false;
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
            _timer?.Dispose();
            _timer = null;
            _capturing = false;
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return;
        }

        AudioAnalysisEventArgs? args = null;
        try
        {
            int bands;
            Signal mode;
            lock (_gate)
            {
                if (_disposed || !_capturing)
                {
                    return;
                }
                bands = _bandCount;
                mode = Mode;
            }

            double ts = Clock.Elapsed.TotalMilliseconds;
            var (band, intensity) = mode == Signal.SineSweep ? SineSweep(ts, bands) : Track124Bpm(ts, bands);
            args = new AudioAnalysisEventArgs { Bands = band, Intensity = intensity, TimestampMs = ts };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulated audio generation failed; skipping (NFR5).");
            try
            {
                Error?.Invoke(this, new PipelineErrorEventArgs { Source = "audio", Message = $"Simulated audio failed: {ex.Message}", Exception = ex });
            }
            catch
            {
                // An Error subscriber must never take down the timer (NFR5).
            }
        }
        finally
        {
            try
            {
                if (args is not null)
                {
                    AudioAnalyzed?.Invoke(this, args);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An AudioAnalyzed subscriber threw; ignoring (NFR5).");
            }
            Volatile.Write(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// The 124 bpm pattern from <c>web/src/shared/simulator.ts</c> (<c>makeSimFrame</c>), audio portion
    /// only, ported to C#: a kick on the beat, per-band bass-boost + melody + sparkle, intensity from the
    /// band mean plus the kick. Pure → deterministic for a fixed timestamp (used by the 10.5 render hook).
    /// </summary>
    public static (float[] Bands, float Intensity) Track124Bpm(double elapsedMs, int audioBands)
    {
        double ts = elapsedMs / 1000.0;
        double beatPhase = (ts * (124.0 / 60.0)) % 1.0;
        double kick = Math.Pow(Math.Max(0, 1 - beatPhase * 4), 1.5);

        var bands = new float[audioBands];
        double sum = 0;
        for (int i = 0; i < audioBands; i++)
        {
            double fracHigh = audioBands <= 1 ? 0 : (double)i / (audioBands - 1);
            double bassBoost = i <= audioBands / 4.0 ? kick * (1 - fracHigh) : 0;
            double melody = (0.3 + 0.25 * Math.Sin(ts * (1.3 + i * 0.43) + i * 1.7)) * (1 - fracHigh * 0.55);
            double sparkle = fracHigh > 0.6 ? 0.12 * Math.Max(0, Math.Sin(ts * 9 + i * 3)) : 0;
            double v = Math.Clamp(bassBoost + melody + sparkle, 0, 1);
            bands[i] = (float)v;
            sum += v;
        }
        float intensity = (float)Math.Min(1, sum / Math.Max(1, audioBands) + kick * 0.35);
        return (bands, intensity);
    }

    /// <summary>A band-position sine sweep (a moving peak across the spectrum), clamped 0..1.</summary>
    public static (float[] Bands, float Intensity) SineSweep(double elapsedMs, int audioBands)
    {
        double ts = elapsedMs / 1000.0;
        double sweep = (ts * 0.2) % 1.0; // ~5 s to traverse the spectrum

        var bands = new float[audioBands];
        double sum = 0;
        for (int i = 0; i < audioBands; i++)
        {
            double frac = audioBands <= 1 ? 0 : (double)i / (audioBands - 1);
            double v = Math.Clamp(0.5 + 0.5 * Math.Cos((frac - sweep) * Math.PI * 4), 0, 1) * 0.9;
            bands[i] = (float)v;
            sum += v;
        }
        float intensity = (float)Math.Min(1, sum / Math.Max(1, audioBands));
        return (bands, intensity);
    }
}
#endif

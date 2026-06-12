namespace AmbientFx.Capture;

/// <summary>
/// Captures system audio via WASAPI loopback and reduces it to normalized log-spaced
/// frequency band magnitudes plus an overall intensity. Emits raw (unsmoothed,
/// sensitivity-agnostic) values — DataProcessingService applies sensitivity and smoothing.
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>Number of log-spaced bands to produce. May be changed while running; takes effect on the next analysis window.</summary>
    int BandCount { get; set; }

    void Start();
    void Stop();

    /// <summary>Raised per analysis window (~30-60x/sec) on a background thread.</summary>
    event EventHandler<AudioAnalysisEventArgs>? AudioAnalyzed;

    event EventHandler<PipelineErrorEventArgs>? Error;
}

public sealed class AudioAnalysisEventArgs : EventArgs
{
    /// <summary>Normalized 0..1 magnitude per band, low to high frequency. Length == BandCount.</summary>
    public required float[] Bands { get; init; }

    /// <summary>Overall 0..1 intensity.</summary>
    public required float Intensity { get; init; }

    /// <summary>Monotonic timestamp in milliseconds.</summary>
    public required double TimestampMs { get; init; }
}

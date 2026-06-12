using AmbientFx.Bridge;

namespace AmbientFx.Processing;

/// <summary>
/// Combines the latest screen frame and audio analysis into the bridge FramePayload:
/// extracts edge-zone colors and a dominant color from the downscaled pixels, applies
/// temporal smoothing and audio sensitivity, detects idle (unchanged) frames, and emits
/// at a rate coalesced to MaxFps.
/// </summary>
public interface IDataProcessingService : IDisposable
{
    void Start();
    void Stop();

    /// <summary>Live-update processing parameters (zones, smoothing, sensitivity, fps cap).</summary>
    void UpdateOptions(ProcessingOptions options);

    /// <summary>Raised at up to MaxFps with a ready-to-send FramePayload (background thread).</summary>
    event EventHandler<FrameReadyEventArgs>? FrameReady;
}

public sealed class ProcessingOptions
{
    public int ZonesPerEdge { get; set; } = 8;

    /// <summary>0 = no temporal smoothing, 1 = heaviest smoothing.</summary>
    public float Smoothing { get; set; } = 0.5f;

    /// <summary>0..1 scale applied to audio bands/intensity.</summary>
    public float AudioSensitivity { get; set; } = 0.5f;

    public int MaxFps { get; set; } = 60;
}

public sealed class FrameReadyEventArgs : EventArgs
{
    public required FramePayload Frame { get; init; }
}

using AmbientFx.Models;

namespace AmbientFx.Capture;

/// <summary>
/// Captures the source monitor with Windows.Graphics.Capture (Direct3D11), GPU-downscales
/// each frame to a tiny image, and emits the small BGRA pixel buffer for analysis.
/// All D3D/WinRT interop stays inside the implementation.
/// </summary>
public interface IScreenCaptureService : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>Starts capturing the given monitor. If already capturing another monitor, switches to it.</summary>
    void Start(MonitorInfo monitor);

    void Stop();

    /// <summary>Raised per captured frame (on a background thread) with the downscaled pixel buffer.</summary>
    event EventHandler<ScreenFrameEventArgs>? FrameCaptured;

    /// <summary>Raised when capture fails or stops unexpectedly. The host shows a toast and degrades gracefully (NFR5).</summary>
    event EventHandler<PipelineErrorEventArgs>? Error;
}

/// <summary>A downscaled frame: tightly packed 32-bit BGRA, top-down rows, no padding.</summary>
public sealed class ScreenFrameEventArgs : EventArgs
{
    public required byte[] PixelsBgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Monotonic timestamp in milliseconds.</summary>
    public required double TimestampMs { get; init; }
}

public sealed class PipelineErrorEventArgs : EventArgs
{
    /// <summary>"capture" | "audio" | "processing" | "hosting"</summary>
    public required string Source { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}

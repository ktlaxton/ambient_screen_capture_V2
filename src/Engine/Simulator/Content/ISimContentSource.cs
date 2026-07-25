#if SIMULATOR_ENABLED
using AmbientFx.Capture;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator, Story 10.3). A pluggable strategy that fills the simulated
/// capture service's reused BGRA buffer for the started source monitor. The service owns the cadence
/// (<c>maxFps</c>), the reused output buffer, and raising <c>FrameCaptured</c>/<c>Error</c>; a strategy
/// only fills bytes. <see cref="Fill"/> must never throw — on a fault it fills a safe (blank) frame and
/// returns a one-shot <see cref="PipelineErrorEventArgs"/> the service surfaces (NFR5). Compiled out of Release.
/// </summary>
public interface ISimContentSource : IDisposable
{
    /// <summary>
    /// Fills <paramref name="bgra"/> (tightly packed, top-down, length ≥ <c>width*height*4</c>) for the
    /// source monitor. Returns null on success, or a capture error to surface <b>once</b> (the strategy
    /// tracks whether it has already reported). Never throws.
    /// </summary>
    PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex);
}

/// <summary>Shared NFR5 helpers for content sources: one-shot error reporting and a safe blank frame.</summary>
public abstract class SimContentSourceBase : ISimContentSource
{
    /// <summary>
    /// Source tag for recoverable content faults. Deliberately NOT "capture": the real coordinator
    /// (<c>EngineCoordinator.HandlePipelineErrorOnUi</c>) treats a "capture" error as <b>fatal</b> and
    /// disables the whole pipeline, which would tear the simulator down on a missing media file. A
    /// non-"capture"/-"audio" source lands in the coordinator's non-fatal default branch (a toast), so
    /// the safe blank frame keeps the pipeline running — exactly what AC6 requires.
    /// </summary>
    public const string ContentErrorSource = "processing";

    private bool _errorReported;

    /// <inheritdoc />
    public abstract PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex);

    /// <summary>Returns a (non-fatal) content error the first time it is called; null on every subsequent call.</summary>
    protected PipelineErrorEventArgs? ErrorOnce(string message, Exception? exception = null)
    {
        if (_errorReported)
        {
            return null;
        }
        _errorReported = true;
        return new PipelineErrorEventArgs { Source = ContentErrorSource, Message = message, Exception = exception };
    }

    /// <summary>Fills the buffer with opaque black — the safe fallback frame.</summary>
    protected static void FillBlank(byte[] bgra, int width, int height)
    {
        int count = Math.Min(bgra.Length, checked(width * height * 4));
        for (int i = 0; i < count; i += 4)
        {
            bgra[i] = 0;
            bgra[i + 1] = 0;
            bgra[i + 2] = 0;
            bgra[i + 3] = 255;
        }
    }

    public virtual void Dispose()
    {
    }
}
#endif

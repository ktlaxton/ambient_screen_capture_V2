#if SIMULATOR_ENABLED
using AmbientFx.Capture;

namespace AmbientFx.Simulator.Content;

/// <summary>Dev/QA only (Epic 10, Story 10.3). Emits an opaque black frame — the "blank" content kind.</summary>
public sealed class BlankContentSource : SimContentSourceBase
{
    public override PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex)
    {
        FillBlank(bgra, width, height);
        return null;
    }
}
#endif

#if SIMULATOR_ENABLED
using AmbientFx.Capture;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.3). The default content source: the synthetic test pattern from
/// Story 10.1. Keeping it behind <see cref="ISimContentSource"/> makes media/mirror additive rather
/// than a rewrite. Compiled out of Release.
/// </summary>
public sealed class SyntheticContentSource : SimContentSourceBase
{
    private readonly string _pattern;

    public SyntheticContentSource(string pattern) => _pattern = SyntheticPatterns.Normalize(pattern);

    public override PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex)
    {
        try
        {
            SyntheticPatterns.Fill(_pattern, bgra, width, height, frameIndex);
            return null;
        }
        catch (Exception ex)
        {
            FillBlank(bgra, width, height);
            return ErrorOnce($"Synthetic pattern fill failed: {ex.Message}", ex);
        }
    }
}
#endif

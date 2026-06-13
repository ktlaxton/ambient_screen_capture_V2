namespace AmbientFx.Devices;

/// <summary>
/// Pure audio-reactive math for ambient peripherals (Story 8.3 AC4): the audio layer is a
/// brightness modulation COMPOSED OVER the position-mapped colors, never a replacement.
/// Stateless and vendor-free, same testing discipline as <see cref="LedProjection"/>.
/// </summary>
public static class AudioModulation
{
    /// <summary>
    /// Brightness factor for the current frame: at depth 0 audio has no effect (1.0);
    /// at depth 1 the peripherals fully track the audio intensity (silence = dark).
    /// Linear blend between: factor = (1 - depth) + depth * intensity.
    /// Non-finite inputs are repaired (depth → 0 = no effect, intensity → 0).
    /// </summary>
    public static float BrightnessFactor(float intensity, float depth)
    {
        float d = float.IsFinite(depth) ? Math.Clamp(depth, 0f, 1f) : 0f;
        float i = float.IsFinite(intensity) ? Math.Clamp(intensity, 0f, 1f) : 0f;
        return 1f - d + (d * i);
    }
}

namespace AmbientFx.Processing;

/// <summary>
/// Applies the user's audio sensitivity (gain) to raw 0..1 FFT bands + intensity, then an
/// attack/release envelope per band (fast rise, slow fall) so visuals punch on transients and
/// decay smoothly. A band-count change resets the envelope state (bands and intensity) to zero.
/// NOT thread-safe — DataProcessingService serializes all access on its tick.
/// </summary>
public sealed class AudioShaper
{
    private const float AttackAlpha = 0.55f;
    private const float ReleaseAlpha = 0.10f;

    /// <summary>gain = 0.25 + 1.75 * sensitivity; defaults to the value for sensitivity = 0.5.</summary>
    private float _gain = ComputeGain(0.5f);

    private float[] _bandState = Array.Empty<float>();
    private float _intensityState;

    /// <summary>
    /// Sets the audio sensitivity: 0 = quietest (gain 0.25), 1 = hottest (gain 2.0).
    /// Out-of-range inputs are clamped to [0,1]. Envelope state is preserved.
    /// </summary>
    public void Configure(float sensitivity01) => _gain = ComputeGain(sensitivity01);

    /// <summary>
    /// Scales the raw bands/intensity by the configured gain (clamped to 0..1), then applies the
    /// per-band attack/release EMA (rising alpha 0.55, falling alpha 0.10); the intensity gets the
    /// same envelope. Returns fresh values in 0..1; the output band array length equals the input's.
    /// Non-finite inputs (NaN/Infinity) are treated as 0.
    /// </summary>
    public (float[] Bands, float Intensity) Shape(float[] rawBands, float rawIntensity)
    {
        ArgumentNullException.ThrowIfNull(rawBands);

        if (_bandState.Length != rawBands.Length)
        {
            // Band-count change: restart the envelope from silence.
            _bandState = new float[rawBands.Length];
            _intensityState = 0f;
        }

        float gain = _gain;
        var outBands = new float[rawBands.Length];
        for (int i = 0; i < rawBands.Length; i++)
        {
            float scaled = ScaleAndClamp(rawBands[i], gain);
            _bandState[i] += EnvelopeAlpha(scaled, _bandState[i]) * (scaled - _bandState[i]);
            outBands[i] = _bandState[i];
        }

        float scaledIntensity = ScaleAndClamp(rawIntensity, gain);
        _intensityState += EnvelopeAlpha(scaledIntensity, _intensityState) * (scaledIntensity - _intensityState);

        return (outBands, _intensityState);
    }

    /// <summary>Resets the envelope state (bands + intensity) to silence; the configured gain is kept.</summary>
    public void Reset()
    {
        _bandState = Array.Empty<float>();
        _intensityState = 0f;
    }

    private static float ComputeGain(float sensitivity01) =>
        0.25f + 1.75f * Math.Clamp(sensitivity01, 0f, 1f);

    private static float ScaleAndClamp(float raw, float gain)
    {
        if (!float.IsFinite(raw)) return 0f;
        return Math.Clamp(raw * gain, 0f, 1f);
    }

    private static float EnvelopeAlpha(float target, float state) =>
        target > state ? AttackAlpha : ReleaseAlpha;
}

using NAudio.Dsp;

namespace AmbientFx.Capture;

/// <summary>
/// Pure, deterministic FFT spectrum analyzer (FR3, spec §5.5). Reduces a mono sample window to
/// normalized log-spaced frequency band magnitudes plus an overall intensity:
/// Hann window → forward FFT (NAudio applies 1/N scaling on the FORWARD transform — the dB
/// calibration below accounts for it) → magnitude spectrum → log-spaced band edges from 20 Hz to
/// min(16000, sampleRate/2 · 0.95) → per-band mean magnitude → dB = 20·log10(mag + 1e-12) →
/// -65 dB..-5 dB mapped to 0..1 (clamped).
/// </summary>
/// <remarks>
/// Threading: instances reuse internal scratch buffers, so <see cref="Analyze"/> must not be called
/// concurrently on the same instance. Construction is cheap; swap whole instances to reconfigure.
/// No NAudio device types are referenced — only the pure <c>NAudio.Dsp</c> math.
/// </remarks>
public sealed class SpectrumAnalyzer
{
    private const float MinFrequencyHz = 20f;
    private const float MaxFrequencyHz = 16000f;
    private const float MinDb = -65f;
    private const float MaxDb = -5f;

    private readonly int _fftOrder;            // m, where FftSize == 2^m
    private readonly float[] _hann;            // precomputed Hann window coefficients
    private readonly Complex[] _fftBuffer;     // scratch: in-place FFT buffer (NAudio.Dsp.Complex: float X/Y)
    private readonly float[] _magnitudes;      // scratch: bins 0 .. FftSize/2 - 1
    private readonly int[] _bandFirstBin;      // inclusive first FFT bin per band (bin 0 = DC, excluded)
    private readonly int[] _bandEndBin;        // exclusive end FFT bin per band
    private readonly float[] _bandWeights;     // intensity weighting, favors low-mid bands
    private readonly float _bandWeightSum;

    /// <summary>
    /// Creates an analyzer for the given sample rate, band count and FFT size.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz of the mono samples passed to <see cref="Analyze"/>.</param>
    /// <param name="bandCount">Number of log-spaced output bands (≥ 1).</param>
    /// <param name="fftSize">FFT window length; must be a power of two (≥ 16).</param>
    public SpectrumAnalyzer(int sampleRate, int bandCount, int fftSize = 2048)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        if (bandCount < 1)
            throw new ArgumentOutOfRangeException(nameof(bandCount), bandCount, "Band count must be at least 1.");
        if (fftSize < 16 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize), fftSize, "FFT size must be a power of two (>= 16).");

        float maxFrequency = MathF.Min(MaxFrequencyHz, sampleRate * 0.5f * 0.95f);
        if (maxFrequency <= MinFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate is too low for the 20 Hz analysis floor.");

        BandCount = bandCount;
        FftSize = fftSize;
        _fftOrder = System.Numerics.BitOperations.Log2((uint)fftSize);

        _hann = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            _hann[i] = (float)FastFourierTransform.HannWindow(i, fftSize);

        _fftBuffer = new Complex[fftSize];
        _magnitudes = new float[fftSize / 2];

        // Log-spaced band edges. freq(k) = k * sampleRate / fftSize; usable bins 1 .. fftSize/2 - 1
        // (bin 0 = DC, excluded). Clamping guarantees every band spans at least one bin.
        _bandFirstBin = new int[bandCount];
        _bandEndBin = new int[bandCount];
        float hzPerBin = (float)sampleRate / fftSize;
        float ratio = maxFrequency / MinFrequencyHz;
        for (int b = 0; b < bandCount; b++)
        {
            float loHz = MinFrequencyHz * MathF.Pow(ratio, (float)b / bandCount);
            float hiHz = MinFrequencyHz * MathF.Pow(ratio, (float)(b + 1) / bandCount);
            _bandFirstBin[b] = Math.Clamp((int)MathF.Floor(loHz / hzPerBin), 1, fftSize / 2 - 1);
            _bandEndBin[b] = Math.Clamp((int)MathF.Ceiling(hiHz / hzPerBin), _bandFirstBin[b] + 1, fftSize / 2);
        }

        // Intensity weights: full weight through the low-mid range, smoothly tapering to 0.4 for
        // the highest bands, so bass/low-mid energy dominates the overall intensity.
        _bandWeights = new float[bandCount];
        float weightSum = 0f;
        for (int b = 0; b < bandCount; b++)
        {
            float position = (b + 0.5f) / bandCount; // 0 = lowest band, 1 = highest
            _bandWeights[b] = 1f - 0.6f * SmoothStep(0.4f, 1f, position);
            weightSum += _bandWeights[b];
        }
        _bandWeightSum = weightSum;
    }

    /// <summary>Number of log-spaced output bands.</summary>
    public int BandCount { get; }

    /// <summary>FFT window length; <see cref="Analyze"/> requires exactly this many samples.</summary>
    public int FftSize { get; }

    /// <summary>
    /// Analyzes one window of mono samples and returns normalized 0..1 band magnitudes
    /// (low to high frequency, length == <see cref="BandCount"/>) plus an overall 0..1 intensity
    /// (energy-weighted average favoring low-mid bands). A fresh band array is returned per call.
    /// </summary>
    /// <param name="monoSamples">Exactly <see cref="FftSize"/> mono samples (nominal range -1..1).</param>
    public (float[] Bands, float Intensity) Analyze(ReadOnlySpan<float> monoSamples)
    {
        if (monoSamples.Length != FftSize)
            throw new ArgumentException($"Expected exactly {FftSize} samples, got {monoSamples.Length}.", nameof(monoSamples));

        for (int i = 0; i < monoSamples.Length; i++)
        {
            _fftBuffer[i].X = monoSamples[i] * _hann[i];
            _fftBuffer[i].Y = 0f;
        }

        // In-place forward FFT. NAudio scales by 1/N on the FORWARD transform (non-standard);
        // the -65..-5 dB normalization window is calibrated for that — do not "fix" it twice.
        FastFourierTransform.FFT(true, _fftOrder, _fftBuffer);

        int half = FftSize / 2;
        for (int k = 0; k < half; k++)
            _magnitudes[k] = MathF.Sqrt(_fftBuffer[k].X * _fftBuffer[k].X + _fftBuffer[k].Y * _fftBuffer[k].Y);

        var bands = new float[BandCount];
        for (int b = 0; b < BandCount; b++)
        {
            float sum = 0f;
            int first = _bandFirstBin[b];
            int end = _bandEndBin[b];
            for (int k = first; k < end; k++)
                sum += _magnitudes[k];
            float mean = sum / (end - first);

            float db = 20f * MathF.Log10(mean + 1e-12f);
            bands[b] = Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0f, 1f);
        }

        float weightedEnergy = 0f;
        for (int b = 0; b < BandCount; b++)
            weightedEnergy += _bandWeights[b] * bands[b] * bands[b];
        float intensity = Math.Clamp(MathF.Sqrt(weightedEnergy / _bandWeightSum), 0f, 1f);

        return (bands, intensity);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

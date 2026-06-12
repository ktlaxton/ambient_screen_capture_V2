using AmbientFx.Capture;
using Xunit;

namespace AmbientFx.Engine.Tests.Signal;

/// <summary>
/// FR3 / AC2: the analyzer must perform REAL spectral analysis — a sine's energy lands in the
/// band whose log-spaced [low, high) range contains its frequency, bass and treble are
/// distinguishable, silence is zero, and every output is normalized to 0..1.
/// </summary>
public class SpectrumAnalyzerTests
{
    private const int SampleRate = 48000;
    private const int BandCount = 12;
    private const int FftSize = 2048;

    // ---------- helpers ----------

    private static float[] Sine(double frequencyHz, double amplitude = 1.0, int count = FftSize, int sampleRate = SampleRate)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate));
        }
        return samples;
    }

    /// <summary>
    /// Replicates the analyzer's documented band-edge contract (log-spaced 20 Hz ..
    /// min(16000, sampleRate/2 * 0.95)) to find the band whose [low, high) range contains
    /// <paramref name="frequencyHz"/>.
    /// </summary>
    private static int ExpectedBandFor(double frequencyHz, int bandCount = BandCount, int sampleRate = SampleRate)
    {
        double minHz = 20.0;
        double maxHz = Math.Min(16000.0, sampleRate * 0.5 * 0.95);
        double ratio = maxHz / minHz;
        for (int b = 0; b < bandCount; b++)
        {
            double lo = minHz * Math.Pow(ratio, (double)b / bandCount);
            double hi = minHz * Math.Pow(ratio, (double)(b + 1) / bandCount);
            if (frequencyHz >= lo && frequencyHz < hi)
            {
                return b;
            }
        }
        throw new InvalidOperationException($"{frequencyHz} Hz is outside the analysis range.");
    }

    private static int ArgMax(float[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }
        return best;
    }

    private static void AssertAllInUnitRange(float[] bands, float intensity)
    {
        foreach (float band in bands)
        {
            Assert.False(float.IsNaN(band) || float.IsInfinity(band), "Band must be finite.");
            Assert.InRange(band, 0f, 1f);
        }
        Assert.False(float.IsNaN(intensity) || float.IsInfinity(intensity), "Intensity must be finite.");
        Assert.InRange(intensity, 0f, 1f);
    }

    // ---------- tonal accuracy (FR3) ----------

    [Fact]
    public void Analyze_FullScale440HzSine_PeaksInTheBandContaining440Hz()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        var (bands, intensity) = analyzer.Analyze(Sine(440.0));

        int expectedBand = ExpectedBandFor(440.0);
        Assert.Equal(expectedBand, ArgMax(bands));
        Assert.True(bands[expectedBand] > 0.5f,
            $"Band {expectedBand} (contains 440 Hz) read {bands[expectedBand]:F3}, expected > 0.5.");
        Assert.True(intensity > 0f);
        AssertAllInUnitRange(bands, intensity);
    }

    [Fact]
    public void Analyze_FullScale440HzSine_BandsTwoOctavesAwayReadLow()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        var (bands, _) = analyzer.Analyze(Sine(440.0));

        int twoOctavesDown = ExpectedBandFor(110.0);  // 440 / 4
        int twoOctavesUp = ExpectedBandFor(1760.0);   // 440 * 4
        int peak = ExpectedBandFor(440.0);
        Assert.NotEqual(peak, twoOctavesDown);
        Assert.NotEqual(peak, twoOctavesUp);
        Assert.True(bands[twoOctavesDown] < 0.15f,
            $"Band {twoOctavesDown} (two octaves below) read {bands[twoOctavesDown]:F3}, expected < 0.15.");
        Assert.True(bands[twoOctavesUp] < 0.15f,
            $"Band {twoOctavesUp} (two octaves above) read {bands[twoOctavesUp]:F3}, expected < 0.15.");
    }

    [Fact]
    public void Analyze_BassVersusTreble_ArgmaxBandsAreClearlySeparated()
    {
        // AC2: 100 Hz (bass) vs 8 kHz (treble) must excite clearly different bands.
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        var (bassBands, _) = analyzer.Analyze(Sine(100.0));
        var (trebleBands, _) = analyzer.Analyze(Sine(8000.0));

        int bassPeak = ArgMax(bassBands);
        int treblePeak = ArgMax(trebleBands);

        Assert.True(bassPeak < BandCount / 3,
            $"100 Hz argmax band {bassPeak} should be in the lower third (< {BandCount / 3}).");
        Assert.True(treblePeak >= 2 * BandCount / 3,
            $"8 kHz argmax band {treblePeak} should be in the upper third (>= {2 * BandCount / 3}).");
        Assert.True(bassPeak < treblePeak);
    }

    // ---------- degenerate signals ----------

    [Fact]
    public void Analyze_Silence_AllBandsZeroAndIntensityZero()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        var (bands, intensity) = analyzer.Analyze(new float[FftSize]);

        Assert.All(bands, b => Assert.Equal(0f, b));
        Assert.Equal(0f, intensity);
    }

    [Fact]
    public void Analyze_FullScaleWhiteNoise_ProducesBroadbandEnergy()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);
        var rng = new Random(12345);
        var noise = new float[FftSize];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var (bands, intensity) = analyzer.Analyze(noise);

        float mean = bands.Average();
        Assert.True(mean > 0.2f, $"Mean band value for full-scale white noise was {mean:F3}, expected > 0.2.");
        Assert.True(intensity > 0f);
        AssertAllInUnitRange(bands, intensity);
    }

    [Fact]
    public void Analyze_DcOffset_ProducesNoNaNOrInfinity()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);
        var dc = new float[FftSize];
        Array.Fill(dc, 0.5f);

        var (bands, intensity) = analyzer.Analyze(dc);

        AssertAllInUnitRange(bands, intensity);
    }

    // ---------- normalization invariant ----------

    [Theory]
    [InlineData(0.0)]    // silence
    [InlineData(0.001)]  // near-silent sine
    [InlineData(1.0)]    // full scale
    [InlineData(10.0)]   // beyond nominal range — must still clamp to 1
    public void Analyze_AnyAmplitude_AllOutputsWithinUnitRange(double amplitude)
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        var (bands, intensity) = analyzer.Analyze(Sine(440.0, amplitude));

        AssertAllInUnitRange(bands, intensity);
    }

    // ---------- shape & configuration ----------

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void Analyze_BandsLengthMatchesConfiguredBandCount(int bandCount)
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, bandCount, FftSize);

        var (bands, intensity) = analyzer.Analyze(Sine(440.0));

        Assert.Equal(bandCount, bands.Length);
        Assert.Equal(bandCount, analyzer.BandCount);
        AssertAllInUnitRange(bands, intensity);
    }

    [Fact]
    public void Analyze_ReturnsAFreshBandArrayPerCall()
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        var (first, _) = analyzer.Analyze(Sine(440.0));
        var (second, _) = analyzer.Analyze(Sine(440.0));

        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData(FftSize - 1)]
    [InlineData(FftSize + 1)]
    [InlineData(0)]
    public void Analyze_WrongSampleCount_Throws(int count)
    {
        var analyzer = new SpectrumAnalyzer(SampleRate, BandCount, FftSize);

        Assert.Throws<ArgumentException>(() => analyzer.Analyze(new float[count]));
    }

    [Theory]
    [InlineData(0, 12, 2048)]      // non-positive sample rate
    [InlineData(-48000, 12, 2048)]
    [InlineData(48000, 0, 2048)]   // band count < 1
    [InlineData(48000, -1, 2048)]
    [InlineData(48000, 12, 1000)]  // not a power of two
    [InlineData(48000, 12, 8)]     // power of two but < 16
    [InlineData(30, 12, 2048)]     // sample rate too low for the 20 Hz floor
    public void Constructor_InvalidArguments_Throws(int sampleRate, int bandCount, int fftSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(sampleRate, bandCount, fftSize));
    }
}

using AmbientFx.Processing;
using Xunit;

namespace AmbientFx.Engine.Tests.Processing;

public class AudioShaperTests
{
    private const double Tolerance = 1e-4;

    // ---------- Gain mapping (sensitivity -> gain) ----------

    [Fact]
    public void Shape_SensitivityZero_AppliesQuarterGain()
    {
        // gain = 0.25; raw 1.0 -> scaled 0.25; first step rises with attack 0.55 -> 0.1375.
        var shaper = new AudioShaper();
        shaper.Configure(0f);

        var (bands, intensity) = shaper.Shape(new[] { 1f }, 1f);

        Assert.Equal(0.55 * 0.25, bands[0], Tolerance);
        Assert.Equal(0.55 * 0.25, intensity, Tolerance);
    }

    [Fact]
    public void Shape_SensitivityOne_AppliesDoubleGain()
    {
        // gain = 2.0; raw 0.25 -> scaled 0.5; first step -> 0.55 * 0.5 = 0.275.
        var shaper = new AudioShaper();
        shaper.Configure(1f);

        var (bands, intensity) = shaper.Shape(new[] { 0.25f }, 0.25f);

        Assert.Equal(0.275, bands[0], Tolerance);
        Assert.Equal(0.275, intensity, Tolerance);
    }

    [Fact]
    public void Shape_OutOfRangeSensitivity_IsClampedTo01()
    {
        var hot = new AudioShaper();
        hot.Configure(5f); // clamps to 1 -> gain 2
        var (hotBands, _) = hot.Shape(new[] { 0.25f }, 0f);
        Assert.Equal(0.275, hotBands[0], Tolerance);

        var cold = new AudioShaper();
        cold.Configure(-3f); // clamps to 0 -> gain 0.25
        var (coldBands, _) = cold.Shape(new[] { 1f }, 0f);
        Assert.Equal(0.1375, coldBands[0], Tolerance);
    }

    // ---------- Clamp to 0..1 after gain ----------

    [Fact]
    public void Shape_GainedValueAboveOne_IsClampedBeforeEnvelope()
    {
        // gain 2.0 on raw 0.9 -> 1.8 clamps to 1.0; first attack step -> 0.55.
        var shaper = new AudioShaper();
        shaper.Configure(1f);

        var (bands, intensity) = shaper.Shape(new[] { 0.9f }, 0.9f);

        Assert.Equal(0.55, bands[0], Tolerance);
        Assert.Equal(0.55, intensity, Tolerance);
        Assert.True(bands[0] <= 1f && bands[0] >= 0f);
    }

    [Fact]
    public void Shape_NegativeRawInput_ClampsToZero()
    {
        var shaper = new AudioShaper();
        shaper.Configure(1f);

        var (bands, intensity) = shaper.Shape(new[] { -0.5f }, -1f);

        Assert.Equal(0f, bands[0]);
        Assert.Equal(0f, intensity);
    }

    // ---------- Attack/release asymmetry ----------

    [Fact]
    public void Shape_AttackThenRelease_UsesAsymmetricAlphas()
    {
        // gain 2: raw 0.5 -> scaled 1.0.
        // Rising step (attack 0.55): 0 + 0.55*(1 - 0)   = 0.55.
        // Falling step (release 0.10): 0.55 + 0.10*(0 - 0.55) = 0.495.
        var shaper = new AudioShaper();
        shaper.Configure(1f);

        var (rise, riseIntensity) = shaper.Shape(new[] { 0.5f }, 0.5f);
        var (fall, fallIntensity) = shaper.Shape(new[] { 0f }, 0f);

        Assert.Equal(0.55, rise[0], Tolerance);
        Assert.Equal(0.495, fall[0], Tolerance);
        Assert.Equal(0.55, riseIntensity, Tolerance);
        Assert.Equal(0.495, fallIntensity, Tolerance);

        // The rise (0.55) is much larger than the fall (0.055): fast attack, slow release.
        double riseDelta = rise[0];
        double fallDelta = rise[0] - fall[0];
        Assert.True(riseDelta > fallDelta * 5);
    }

    // ---------- Band-count change resets ----------

    [Fact]
    public void Shape_BandCountChange_RestartsEnvelopeFromSilence()
    {
        var shaper = new AudioShaper();
        shaper.Configure(1f); // gain 2: raw 0.5 -> scaled 1.0

        // Build up state over two steps with 2 bands (0.55, then 0.7975).
        shaper.Shape(new[] { 0.5f, 0.5f }, 0.5f);
        shaper.Shape(new[] { 0.5f, 0.5f }, 0.5f);

        // Switch to 3 bands: state must restart from 0, so output is a first attack step again.
        var (bands, intensity) = shaper.Shape(new[] { 0.5f, 0.5f, 0.5f }, 0.5f);

        Assert.Equal(3, bands.Length);
        foreach (float band in bands)
            Assert.Equal(0.55, band, Tolerance);
        Assert.Equal(0.55, intensity, Tolerance); // intensity envelope resets too
    }

    // ---------- Non-finite input ----------

    [Fact]
    public void Shape_NaNAndInfinityInputs_ProduceFiniteOutput()
    {
        var shaper = new AudioShaper();
        shaper.Configure(0.5f);

        var (bands, intensity) = shaper.Shape(
            new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity },
            float.NaN);

        Assert.All(bands, b => Assert.True(float.IsFinite(b)));
        Assert.True(float.IsFinite(intensity));
        Assert.All(bands, b => Assert.Equal(0f, b));
        Assert.Equal(0f, intensity);
    }

    [Fact]
    public void Shape_NaNDoesNotPoisonSubsequentFrames()
    {
        var shaper = new AudioShaper();
        shaper.Configure(1f);

        shaper.Shape(new[] { float.NaN }, float.PositiveInfinity);
        var (bands, intensity) = shaper.Shape(new[] { 0.5f }, 0.5f);

        Assert.Equal(0.55, bands[0], Tolerance);
        Assert.Equal(0.55, intensity, Tolerance);
    }

    // ---------- Misc contracts ----------

    [Fact]
    public void Shape_OutputLengthEqualsInputLength_AndEmptyIsAllowed()
    {
        var shaper = new AudioShaper();

        var (bands4, _) = shaper.Shape(new float[4], 0f);
        Assert.Equal(4, bands4.Length);

        var (bands0, intensity) = shaper.Shape(Array.Empty<float>(), 0f);
        Assert.Empty(bands0);
        Assert.True(float.IsFinite(intensity));
    }

    [Fact]
    public void Shape_NullBands_Throws()
    {
        var shaper = new AudioShaper();
        Assert.Throws<ArgumentNullException>(() => shaper.Shape(null!, 0f));
    }

    [Fact]
    public void Reset_RestartsEnvelope_ButKeepsConfiguredGain()
    {
        var shaper = new AudioShaper();
        shaper.Configure(1f);

        shaper.Shape(new[] { 0.5f }, 0.5f);
        shaper.Shape(new[] { 0.5f }, 0.5f);
        shaper.Reset();

        // First step after reset: envelope restarted (0.55), gain still 2 (scaled hit 1.0).
        var (bands, intensity) = shaper.Shape(new[] { 0.5f }, 0.5f);
        Assert.Equal(0.55, bands[0], Tolerance);
        Assert.Equal(0.55, intensity, Tolerance);
    }
}

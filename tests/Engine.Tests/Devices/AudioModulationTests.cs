using AmbientFx.Devices;
using Xunit;

namespace AmbientFx.Engine.Tests.Devices;

/// <summary>Table tests for the pure audio-reactive math (Story 8.3 AC4/AC7).</summary>
public sealed class AudioModulationTests
{
    [Theory]
    // depth 0 → audio has no effect, whatever the intensity.
    [InlineData(0f, 0f, 1f)]
    [InlineData(1f, 0f, 1f)]
    // depth 1 → factor IS the intensity (silence = dark, full beat = full).
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(0.5f, 1f, 0.5f)]
    // mid depth blends linearly: 1 - d + d*i.
    [InlineData(0f, 0.5f, 0.5f)]
    [InlineData(0.5f, 0.5f, 0.75f)]
    [InlineData(1f, 0.5f, 1f)]
    public void Factor_blends_between_full_and_intensity(float intensity, float depth, float expected)
    {
        Assert.Equal(expected, AudioModulation.BrightnessFactor(intensity, depth), precision: 5);
    }

    [Theory]
    [InlineData(5f, 0.5f)]    // intensity above 1 clamps to 1 → factor 1
    [InlineData(-3f, 0.5f)]   // negative intensity clamps to 0 → factor 0.5
    public void Out_of_range_intensity_is_clamped(float intensity, float depth)
    {
        float factor = AudioModulation.BrightnessFactor(intensity, depth);
        Assert.InRange(factor, 1f - depth, 1f);
    }

    [Fact]
    public void Non_finite_inputs_are_repaired_not_propagated()
    {
        // NaN depth → no effect; NaN intensity at full depth → silence (0), never NaN.
        Assert.Equal(1f, AudioModulation.BrightnessFactor(0.5f, float.NaN));
        Assert.Equal(0f, AudioModulation.BrightnessFactor(float.NaN, 1f));
        Assert.True(float.IsFinite(AudioModulation.BrightnessFactor(float.PositiveInfinity, float.NegativeInfinity)));
    }
}

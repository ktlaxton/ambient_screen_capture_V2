using AmbientFx.Bridge;
using AmbientFx.Processing;
using Xunit;

namespace AmbientFx.Engine.Tests.Processing;

public class TemporalSmootherTests
{
    private static EdgeColors UniformEdges(int zones, int r, int g, int b)
    {
        static int[][] Zones(int count, int r, int g, int b)
        {
            var result = new int[count][];
            for (int i = 0; i < count; i++)
                result[i] = new[] { r, g, b };
            return result;
        }

        return new EdgeColors
        {
            Top = Zones(zones, r, g, b),
            Bottom = Zones(zones, r, g, b),
            Left = Zones(zones, r, g, b),
            Right = Zones(zones, r, g, b),
        };
    }

    private static void AssertAllZones(EdgeColors edges, int zones, int[] expected)
    {
        foreach (int[][] edge in new[] { edges.Top, edges.Bottom, edges.Left, edges.Right })
        {
            Assert.Equal(zones, edge.Length);
            foreach (int[] zone in edge)
                Assert.Equal(expected, zone);
        }
    }

    // ---------- Smooth: edges ----------

    [Fact]
    public void Smooth_FirstCall_AdoptsTargetExactly()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.9f); // heavy smoothing must NOT dilute the very first frame

        var result = smoother.Smooth(UniformEdges(3, 123, 45, 67));

        AssertAllZones(result, 3, new[] { 123, 45, 67 });
    }

    [Fact]
    public void Smooth_ReturnsFreshArrays_NeverAliasesInput()
    {
        var smoother = new TemporalSmoother();
        var target = UniformEdges(2, 10, 20, 30);

        var result = smoother.Smooth(target);

        Assert.NotSame(target.Top, result.Top);
        Assert.NotSame(target.Top[0], result.Top[0]);
    }

    [Fact]
    public void Smooth_SmoothingHalf_EmaMatchesHandComputedValues()
    {
        // smoothing 0.5 => alpha = 1 - 0.92*0.5 = 0.54.
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.5f);

        smoother.Smooth(UniformEdges(2, 0, 0, 0)); // adopt 0

        // Step 1 toward 100: 0 + 0.54*(100-0) = 54.
        var step1 = smoother.Smooth(UniformEdges(2, 100, 100, 100));
        AssertAllZones(step1, 2, new[] { 54, 54, 54 });

        // Step 2 toward 100: 54 + 0.54*46 = 78.84 -> 79.
        var step2 = smoother.Smooth(UniformEdges(2, 100, 100, 100));
        AssertAllZones(step2, 2, new[] { 79, 79, 79 });
    }

    [Fact]
    public void Smooth_SmoothingZero_IsPassthrough()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0f); // alpha = 1

        smoother.Smooth(UniformEdges(2, 10, 20, 30));
        var result = smoother.Smooth(UniformEdges(2, 200, 10, 30));

        AssertAllZones(result, 2, new[] { 200, 10, 30 });
    }

    [Fact]
    public void Smooth_SmoothingOne_UsesMinAlpha()
    {
        // smoothing 1 => alpha clamped to 0.08: 0 -> 100 gives round(8) = 8.
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(1f);

        smoother.Smooth(UniformEdges(1, 0, 0, 0));
        var result = smoother.Smooth(UniformEdges(1, 100, 100, 100));

        AssertAllZones(result, 1, new[] { 8, 8, 8 });
    }

    [Fact]
    public void Smooth_ZoneCountChange_AdoptsNewTargetImmediately()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.5f);

        smoother.Smooth(UniformEdges(2, 0, 0, 0));
        var result = smoother.Smooth(UniformEdges(3, 250, 250, 250)); // count changed: no blending

        AssertAllZones(result, 3, new[] { 250, 250, 250 });
    }

    [Fact]
    public void Smooth_ZoneCountChangeOnOneEdge_ResetsOnlyThatEdge()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.5f);

        smoother.Smooth(UniformEdges(2, 0, 0, 0)); // all edges adopt 0 with 2 zones

        var target = UniformEdges(2, 100, 100, 100);
        target.Top = new[] { new[] { 100, 100, 100 }, new[] { 100, 100, 100 }, new[] { 100, 100, 100 } };
        var result = smoother.Smooth(target);

        // Top changed 2 -> 3 zones: adopted directly.
        Assert.Equal(3, result.Top.Length);
        foreach (int[] zone in result.Top)
            Assert.Equal(new[] { 100, 100, 100 }, zone);

        // Left kept its zone count: still smoothing (0 + 0.54*100 = 54).
        foreach (int[] zone in result.Left)
            Assert.Equal(new[] { 54, 54, 54 }, zone);
    }

    [Fact]
    public void Smooth_AfterReset_AdoptsTargetExactly()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.5f);

        smoother.Smooth(UniformEdges(2, 0, 0, 0));
        smoother.Reset();
        var result = smoother.Smooth(UniformEdges(2, 100, 100, 100));

        AssertAllZones(result, 2, new[] { 100, 100, 100 });
    }

    [Fact]
    public void Smooth_NullTarget_Throws()
    {
        var smoother = new TemporalSmoother();
        Assert.Throws<ArgumentNullException>(() => smoother.Smooth(null!));
    }

    // ---------- SmoothDominant ----------

    [Fact]
    public void SmoothDominant_FirstCall_AdoptsTargetExactly()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.9f);

        int[] result = smoother.SmoothDominant(new[] { 11, 22, 33 });

        Assert.Equal(new[] { 11, 22, 33 }, result);
    }

    [Fact]
    public void SmoothDominant_SmoothingHalf_EmaMatchesHandComputedValue()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.5f); // alpha 0.54

        smoother.SmoothDominant(new[] { 0, 0, 0 });
        int[] step1 = smoother.SmoothDominant(new[] { 100, 100, 100 });

        Assert.Equal(new[] { 54, 54, 54 }, step1);
    }

    [Fact]
    public void SmoothDominant_AfterReset_AdoptsTargetExactly()
    {
        var smoother = new TemporalSmoother();
        smoother.SetSmoothing(0.5f);

        smoother.SmoothDominant(new[] { 0, 0, 0 });
        smoother.Reset();
        int[] result = smoother.SmoothDominant(new[] { 77, 88, 99 });

        Assert.Equal(new[] { 77, 88, 99 }, result);
    }

    [Fact]
    public void SmoothDominant_ReturnsFreshArray()
    {
        var smoother = new TemporalSmoother();
        var target = new[] { 5, 6, 7 };

        int[] result = smoother.SmoothDominant(target);

        Assert.NotSame(target, result);
        Assert.Equal(target, result);
    }

    [Fact]
    public void SmoothDominant_NullTarget_Throws()
    {
        var smoother = new TemporalSmoother();
        Assert.Throws<ArgumentNullException>(() => smoother.SmoothDominant(null!));
    }
}

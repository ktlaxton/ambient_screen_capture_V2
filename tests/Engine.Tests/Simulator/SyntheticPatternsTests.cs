#if SIMULATOR_ENABLED
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Exact-byte coverage of the pure synthetic pattern generators (Story 10.1 AC4/AC10): tightly
/// packed, top-down BGRA (B@0/G@1/R@2/A@3, stride = width*4), deterministic per frame index.
/// </summary>
public sealed class SyntheticPatternsTests
{
    private static byte[] Buffer(int w, int h) => new byte[w * h * 4];

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int w, int x, int y)
    {
        int p = (y * w + x) * 4;
        return (bgra[p], bgra[p + 1], bgra[p + 2], bgra[p + 3]);
    }

    [Fact]
    public void Gradient_IsTopDownBgra_WithDeterministicBytesForFixedFrameIndex()
    {
        const int w = 4, h = 3;
        var buf = Buffer(w, h);

        SyntheticPatterns.FillGradient(buf, w, h, frameIndex: 0);

        // (0,0): r=0,g=0,b=0,a=255
        Assert.Equal((byte)0, buf[0]);   // B
        Assert.Equal((byte)0, buf[1]);   // G
        Assert.Equal((byte)0, buf[2]);   // R
        Assert.Equal((byte)255, buf[3]); // A

        // (2,1): r = 2*256/4 = 128, g = 1*256/3 = 85, b = 2+1 = 3
        var (b, g, r, a) = Pixel(buf, w, x: 2, y: 1);
        Assert.Equal((byte)3, b);
        Assert.Equal((byte)85, g);
        Assert.Equal((byte)128, r);
        Assert.Equal((byte)255, a);
    }

    [Fact]
    public void Gradient_MovesBetweenFrames()
    {
        const int w = 8, h = 8;
        var f0 = Buffer(w, h);
        var f1 = Buffer(w, h);

        SyntheticPatterns.FillGradient(f0, w, h, frameIndex: 0);
        SyntheticPatterns.FillGradient(f1, w, h, frameIndex: 1);

        Assert.False(f0.AsSpan().SequenceEqual(f1), "the animated gradient must change frame-to-frame");
    }

    [Fact]
    public void Gradient_IsDeterministic_SameFrameIndexSameBytes()
    {
        const int w = 16, h = 9;
        var a = Buffer(w, h);
        var b = Buffer(w, h);

        SyntheticPatterns.FillGradient(a, w, h, frameIndex: 42);
        SyntheticPatterns.FillGradient(b, w, h, frameIndex: 42);

        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void Bars_EmitFullSaturationColorsInBgraOrder()
    {
        const int w = 16, h = 2; // 8 bars, 2px each
        var buf = Buffer(w, h);

        SyntheticPatterns.FillBars(buf, w, h);

        // Bar 0 = white.
        Assert.Equal((255, 255, 255, 255), Pixel(buf, w, 0, 0));
        // Bar 5 = red -> B=0, G=0, R=255 (proves channel order, not RGBA).
        Assert.Equal((0, 0, 255, 255), Pixel(buf, w, 10, 1));
        // Bar 7 = black.
        Assert.Equal((0, 0, 0, 255), Pixel(buf, w, 15, 0));
    }

    [Fact]
    public void TestCard_DrawsBorderCrosshairOnGreyField()
    {
        const int w = 5, h = 5;
        var buf = Buffer(w, h);

        SyntheticPatterns.FillTestCard(buf, w, h, frameIndex: 1); // sweep at x=1

        // Centre crosshair (x==2) is white.
        Assert.Equal((255, 255, 255, 255), Pixel(buf, w, 2, 2));
        // Interior, non-line, non-sweep pixel is the grey field.
        Assert.Equal((96, 96, 96, 255), Pixel(buf, w, 3, 1));
        // Corner is the white border.
        Assert.Equal((255, 255, 255, 255), Pixel(buf, w, 0, 0));
    }

    [Theory]
    [InlineData("bars", SyntheticPatterns.Bars)]
    [InlineData("BARS", SyntheticPatterns.Bars)]
    [InlineData("testcard", SyntheticPatterns.TestCard)]
    [InlineData("test-card", SyntheticPatterns.TestCard)]
    [InlineData("gradient", SyntheticPatterns.Gradient)]
    [InlineData("", SyntheticPatterns.Gradient)]
    [InlineData("nonsense", SyntheticPatterns.Gradient)]
    [InlineData(null, SyntheticPatterns.Gradient)]
    public void Normalize_DefaultsUnknownToGradient(string? input, string expected)
    {
        Assert.Equal(expected, SyntheticPatterns.Normalize(input));
    }

    [Fact]
    public void Fill_AlphaIsAlwaysOpaque_AcrossEveryPattern()
    {
        const int w = 7, h = 5;
        foreach (var pattern in new[] { SyntheticPatterns.Gradient, SyntheticPatterns.Bars, SyntheticPatterns.TestCard })
        {
            var buf = Buffer(w, h);
            SyntheticPatterns.Fill(pattern, buf, w, h, frameIndex: 3);
            for (int i = 3; i < buf.Length; i += 4)
            {
                Assert.Equal((byte)255, buf[i]);
            }
        }
    }
}
#endif

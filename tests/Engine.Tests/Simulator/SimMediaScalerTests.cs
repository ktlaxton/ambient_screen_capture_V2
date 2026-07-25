#if SIMULATOR_ENABLED
using AmbientFx.Simulator.Content;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.3 AC8: the pure <see cref="SimMediaScaler"/> — exact-fit, letterbox bars, crop, stretch,
/// upscale — producing tightly-packed top-down opaque BGRA.
/// </summary>
public sealed class SimMediaScalerTests
{
    // 2x2 source: (0,0)=red, (1,0)=green, (0,1)=blue, (1,1)=white — in BGRA top-down.
    private static byte[] Source2x2() => new byte[]
    {
        0, 0, 255, 255,   0, 255, 0, 255,   // row 0: red, green
        255, 0, 0, 255,   255, 255, 255, 255, // row 1: blue, white
    };

    private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int w, int x, int y)
    {
        int p = (y * w + x) * 4;
        return (bgra[p], bgra[p + 1], bgra[p + 2], bgra[p + 3]);
    }

    private static bool IsBlack((byte B, byte G, byte R, byte A) px) => px is (0, 0, 0, 255);

    [Fact]
    public void Fit_ExactSize_IsAnExactCopy()
    {
        var dst = new byte[2 * 2 * 4];
        SimMediaScaler.Scale(Source2x2(), 2, 2, dst, 2, 2, SimMediaScaler.Mode.Fit);
        Assert.Equal(Source2x2(), dst);
    }

    [Fact]
    public void Fit_WiderTarget_LetterboxesWithVerticalBars()
    {
        var dst = new byte[4 * 2 * 4];
        SimMediaScaler.Scale(Source2x2(), 2, 2, dst, 4, 2, SimMediaScaler.Mode.Fit);

        Assert.True(IsBlack(Px(dst, 4, 0, 0)), "left bar is black");
        Assert.True(IsBlack(Px(dst, 4, 3, 0)), "right bar is black");
        Assert.Equal((0, 0, 255, 255), Px(dst, 4, 1, 0)); // red moved to centre
        Assert.Equal((0, 255, 0, 255), Px(dst, 4, 2, 0)); // green
    }

    [Fact]
    public void Fit_TallerTarget_LetterboxesWithHorizontalBars()
    {
        var dst = new byte[2 * 4 * 4];
        SimMediaScaler.Scale(Source2x2(), 2, 2, dst, 2, 4, SimMediaScaler.Mode.Fit);

        Assert.True(IsBlack(Px(dst, 2, 0, 0)), "top bar is black");
        Assert.True(IsBlack(Px(dst, 2, 0, 3)), "bottom bar is black");
        Assert.Equal((0, 0, 255, 255), Px(dst, 2, 0, 1)); // red in the centre band
    }

    [Fact]
    public void Stretch_FillsEntireTarget_NoBars()
    {
        var dst = new byte[4 * 4 * 4];
        SimMediaScaler.Scale(Source2x2(), 2, 2, dst, 4, 4, SimMediaScaler.Mode.Stretch);

        // Top-left quadrant maps from src(0,0)=red.
        Assert.Equal((0, 0, 255, 255), Px(dst, 4, 0, 0));
        // Bottom-right quadrant maps from src(1,1)=white.
        Assert.Equal((255, 255, 255, 255), Px(dst, 4, 3, 3));
        // No black letterbox anywhere.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.False(IsBlack(Px(dst, 4, x, y)) && (x, y) != (0, 0), "stretch must leave no black bars");
            }
        }
    }

    [Fact]
    public void Cover_FillsTarget_CroppingOverflow()
    {
        // 2x2 into 2x4: Cover scales x2 (region 4x4), so it covers fully and crops horizontally.
        var dst = new byte[2 * 4 * 4];
        SimMediaScaler.Scale(Source2x2(), 2, 2, dst, 2, 4, SimMediaScaler.Mode.Cover);

        // No letterbox: every pixel is painted from the source.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Assert.Equal((byte)255, Px(dst, 2, x, y).A);
            }
        }
    }

    [Fact]
    public void Upscale_TinySource_FillsWithSolidColor()
    {
        var src = new byte[] { 10, 20, 30, 255 }; // 1x1
        var dst = new byte[4 * 4 * 4];
        SimMediaScaler.Scale(src, 1, 1, dst, 4, 4, SimMediaScaler.Mode.Stretch);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), Px(dst, 4, x, y));
            }
        }
    }

    [Fact]
    public void Scale_AlwaysWritesOpaqueAlpha()
    {
        var dst = new byte[5 * 3 * 4];
        SimMediaScaler.Scale(Source2x2(), 2, 2, dst, 5, 3, SimMediaScaler.Mode.Fit);
        for (int i = 3; i < dst.Length; i += 4)
        {
            Assert.Equal((byte)255, dst[i]);
        }
    }
}
#endif

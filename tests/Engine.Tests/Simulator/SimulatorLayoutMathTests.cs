#if SIMULATOR_ENABLED
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.6: the pure pan/zoom/Fit geometry behind the composite canvas. Verifies that (a) Fit
/// (zoom 1 / pan 0) reproduces the auto-fit mapping, (b) a pan offset translates every placed rect by
/// the same delta with sizes unchanged, and (c) wheel zoom keeps the virtual-desktop point under the
/// cursor anchored. Runs off any WPF/GPU path.
/// </summary>
public sealed class SimulatorLayoutMathTests
{
    private static readonly IReadOnlyList<SimulatorLayoutMath.Rect> TwoWide = new[]
    {
        new SimulatorLayoutMath.Rect(0, 0, 2000, 1000),
        new SimulatorLayoutMath.Rect(2000, 0, 2000, 1000),
    };

    private static void Close(double expected, double actual, double tol = 1e-6) =>
        Assert.True(System.Math.Abs(expected - actual) < tol, $"expected {expected}, got {actual}");

    [Fact]
    public void Fit_ZoomOnePanZero_ReproducesAutoFit()
    {
        // span 4000x1000; avail 1000x1000; pad 20 -> fit = min(1000/4000, 1000/1000) = 0.25.
        Assert.True(SimulatorLayoutMath.TryCompute(TwoWide, 1000, 1000, 20, userZoom: 1, panX: 0, panY: 0, out var layout));
        Close(0.25, layout.Scale);
        Close(20, layout.OffsetX);             // 20 + (1000 - 4000*0.25)/2 = 20
        Close(395, layout.OffsetY);            // 20 + (1000 - 1000*0.25)/2 = 395

        var a = layout.Place(0, 0, 2000, 1000);
        Close(20, a.Left);
        Close(395, a.Top);
        Close(500, a.Width);
        Close(250, a.Height);

        var b = layout.Place(2000, 0, 2000, 1000);
        Close(520, b.Left);                    // 20 + 2000*0.25
        Close(395, b.Top);
    }

    [Fact]
    public void Pan_TranslatesEveryRectEqually_SizesUnchanged()
    {
        Assert.True(SimulatorLayoutMath.TryCompute(TwoWide, 1000, 1000, 20, 1, 0, 0, out var basis));
        Assert.True(SimulatorLayoutMath.TryCompute(TwoWide, 1000, 1000, 20, 1, panX: 50, panY: -30, out var panned));

        foreach (var r in TwoWide)
        {
            var bp = basis.Place(r.X, r.Y, r.Width, r.Height);
            var pp = panned.Place(r.X, r.Y, r.Width, r.Height);
            Close(bp.Left + 50, pp.Left);
            Close(bp.Top - 30, pp.Top);
            Close(bp.Width, pp.Width);   // pan never changes size
            Close(bp.Height, pp.Height);
        }
    }

    [Fact]
    public void Zoom_AboutCursor_KeepsThePointUnderTheCursorFixed()
    {
        const double availW = 1000, availH = 1000, pad = 20;
        const double cursorX = 300, cursorY = 400;

        Assert.True(SimulatorLayoutMath.TryCompute(TwoWide, availW, availH, pad, 1, 0, 0, out var old));
        // The virtual-desktop point currently under the cursor.
        double worldX = old.MinX + (cursorX - old.OffsetX) / old.Scale;
        double worldY = old.MinY + (cursorY - old.OffsetY) / old.Scale;

        double newZoom = 2.0;
        var (panX, panY) = SimulatorLayoutMath.PanForZoom(
            TwoWide, availW, availH, pad, oldZoom: 1, oldPanX: 0, oldPanY: 0, newZoom, cursorX, cursorY);

        Assert.True(SimulatorLayoutMath.TryCompute(TwoWide, availW, availH, pad, newZoom, panX, panY, out var after));
        Close(old.Scale * 2.0, after.Scale);   // zoom scaled the mapping...

        // ...and the same world point still maps to the cursor.
        double mappedX = after.OffsetX + (worldX - after.MinX) * after.Scale;
        double mappedY = after.OffsetY + (worldY - after.MinY) * after.Scale;
        Close(cursorX, mappedX, 1e-4);
        Close(cursorY, mappedY, 1e-4);
    }

    [Fact]
    public void TryCompute_NoRects_ReturnsFalse()
    {
        Assert.False(SimulatorLayoutMath.TryCompute(
            System.Array.Empty<SimulatorLayoutMath.Rect>(), 1000, 1000, 20, 1, 0, 0, out _));
    }

    [Fact]
    public void Snap_RightEdgeToNeighborLeft_ClicksIntoAdjacency()
    {
        // A 1920-wide monitor dropped 12px short of a neighbor whose left edge is at 1920.
        var others = new[] { new SimulatorLayoutMath.Rect(1920, 0, 1920, 1080) };
        var (x, y) = SimulatorLayoutMath.Snap(x: -12, y: 6, w: 1920, h: 1080, others, snapDistance: 30);
        Assert.Equal(0, x);    // right edge (1908) snaps to neighbor left (1920) -> x = 0
        Assert.Equal(0, y);    // top (6) aligns to neighbor top (0)
    }

    [Fact]
    public void Snap_OutOfRange_LeavesPositionUnchanged()
    {
        var others = new[] { new SimulatorLayoutMath.Rect(1920, 0, 1920, 1080) };
        var (x, y) = SimulatorLayoutMath.Snap(x: -200, y: 400, w: 1920, h: 1080, others, snapDistance: 30);
        Assert.Equal(-200, x); // nothing within 30px
        Assert.Equal(400, y);
    }

    [Fact]
    public void Snap_NoNeighbors_IsIdentity()
    {
        var (x, y) = SimulatorLayoutMath.Snap(123, -45, 1920, 1080, System.Array.Empty<SimulatorLayoutMath.Rect>(), 30);
        Assert.Equal(123, x);
        Assert.Equal(-45, y);
    }
}
#endif

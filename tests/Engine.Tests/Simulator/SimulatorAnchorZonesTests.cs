#if SIMULATOR_ENABLED
using AmbientFx.Devices;
using AmbientFx.Models;
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (peripheral drag-and-snap): the drop-zone geometry covers all seven REAL anchors,
/// hit-testing resolves each one (inside, near within snap distance, and outside → auto), and the
/// surround perimeter positions follow the exact <see cref="LedProjection"/> surround angle
/// convention so every dot sits where its color is sampled from.
/// </summary>
public sealed class SimulatorAnchorZonesTests
{
    // Source rect (100,100) 400×300, band thickness 30, gap 10 → outer band offset 50.
    private static readonly IReadOnlyList<SimulatorAnchorZones.Zone> Z =
        SimulatorAnchorZones.Zones(100, 100, 400, 300, thickness: 30, gap: 10);

    private static string Hit(double x, double y, double snap = 40) =>
        SimulatorAnchorZones.HitTest(Z, x, y, snap);

    [Fact]
    public void Zones_CoverAllSevenAnchors()
    {
        var anchors = Z.Select(z => z.Anchor).Distinct().ToHashSet();
        // Six spatial anchors are drawn (auto included as a slot); every zone tag is a valid anchor.
        Assert.Superset(new HashSet<string>
        {
            DeviceAnchors.Left, DeviceAnchors.Right, DeviceAnchors.Above, DeviceAnchors.Below,
            DeviceAnchors.Behind, DeviceAnchors.Surround, DeviceAnchors.Auto,
        }, anchors);
        Assert.All(Z, z => Assert.True(DeviceAnchors.IsValid(z.Anchor)));
        Assert.Equal(4, Z.Count(z => z.Anchor == DeviceAnchors.Surround)); // the frame band
    }

    [Theory]
    [InlineData(70, 200, DeviceAnchors.Left)]      // inside the left strip (60..90)
    [InlineData(525, 250, DeviceAnchors.Right)]    // inside the right strip (510..540)
    [InlineData(300, 75, DeviceAnchors.Above)]     // inside the above strip (60..90 y)
    [InlineData(300, 425, DeviceAnchors.Below)]    // inside the below strip (410..440 y)
    [InlineData(300, 250, DeviceAnchors.Behind)]   // center of the monitor
    [InlineData(300, 30, DeviceAnchors.Surround)]  // outer top band (y 20..50)
    [InlineData(560, 250, DeviceAnchors.Surround)] // outer right band (x 550..580)
    [InlineData(520, 420, DeviceAnchors.Auto)]     // the corner auto slot (510..540 × 410..440)
    public void HitTest_ResolvesEachZone(double x, double y, string expected) =>
        Assert.Equal(expected, Hit(x, y));

    [Fact]
    public void HitTest_NearAZone_SnapsWithinDistance()
    {
        Assert.Equal(DeviceAnchors.Left, Hit(95, 250));  // 5 px right of the left strip
        Assert.Equal(DeviceAnchors.Auto, Hit(95, 250, snap: 2)); // same point, tighter snap → auto
    }

    [Fact]
    public void HitTest_FarOutside_ResolvesToAuto() =>
        Assert.Equal(DeviceAnchors.Auto, Hit(2000, 2000));

    // ---- surround perimeter positions (rect (0,0) 100×100) ----

    private static (double X, double Y) One(double lx, double ly, bool flip = false) =>
        Assert.Single(SimulatorAnchorZones.PerimeterPositions(
            0, 0, 100, 100, new[] { new LedProjection.LedPoint(lx, ly) }, flip));

    /// <summary>Corner LEDs sit exactly on a segment boundary, where the atan2 round-off can land
    /// them an epsilon into either neighboring segment — the walk is continuous there, so the
    /// position is the same corner either way, but only within a tolerance.</summary>
    private static void Close((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.True(System.Math.Abs(expected.X - actual.X) < 1e-6
                 && System.Math.Abs(expected.Y - actual.Y) < 1e-6,
            $"expected ({expected.X}, {expected.Y}), got ({actual.X}, {actual.Y})");
    }

    [Fact]
    public void PerimeterPositions_FollowTheProjectionConvention()
    {
        Close((50, 0), One(0.5, 0.5));    // centered LED reads the top midpoint
        Close((50, 0), One(0.5, 0.0));    // "up" → top midpoint
        Close((0, 0), One(0.0, 0.0));     // top-left corner (t = 0)
        Close((100, 0), One(1.0, 0.0));   // top-right corner (t = .25)
        Close((100, 50), One(1.0, 0.5));  // right midpoint
        Close((100, 100), One(1.0, 1.0)); // bottom-right corner (t = .5)
        Close((50, 100), One(0.5, 1.0));  // bottom midpoint
        Close((0, 100), One(0.0, 1.0));   // bottom-left corner (t = .75)
        Close((0, 50), One(0.0, 0.5));    // left midpoint
    }

    [Fact]
    public void PerimeterPositions_FlipReversesTheWinding() =>
        Close((0, 0), One(1.0, 0.0, flip: true)); // top-right LED lands top-left when flipped

    [Fact]
    public void PerimeterPositions_AllDotsSitOnThePerimeter()
    {
        var grid = new List<LedProjection.LedPoint>();
        for (int col = 0; col < 18; col++)
        {
            for (int row = 0; row < 6; row++)
            {
                grid.Add(new LedProjection.LedPoint(col / 17.0, row / 5.0));
            }
        }

        var positions = SimulatorAnchorZones.PerimeterPositions(0, 0, 100, 100, grid, flip: false);

        Assert.Equal(grid.Count, positions.Length);
        Assert.All(positions, p =>
        {
            bool onEdge = System.Math.Abs(p.X) < 1e-9 || System.Math.Abs(p.X - 100) < 1e-9
                       || System.Math.Abs(p.Y) < 1e-9 || System.Math.Abs(p.Y - 100) < 1e-9;
            Assert.True(onEdge, $"({p.X}, {p.Y}) is not on the perimeter");
            Assert.InRange(p.X, 0, 100);
            Assert.InRange(p.Y, 0, 100);
        });
    }
}
#endif

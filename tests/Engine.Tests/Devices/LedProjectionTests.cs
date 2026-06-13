using AmbientFx.Bridge;
using AmbientFx.Devices;
using Xunit;
using LedPoint = AmbientFx.Devices.LedProjection.LedPoint;

namespace AmbientFx.Engine.Tests.Devices;

/// <summary>
/// Table tests for the pure LED→edge-zone projection (Story 8.1 AC3/AC9): normalization of
/// vendor coordinate spaces (mm vs logical units), nearest-edge selection with deterministic
/// tie-breaking, zone indexing along each edge, brightness scaling, and degenerate inputs.
/// </summary>
public sealed class LedProjectionTests
{
    /// <summary>
    /// 4 zones per edge with unique colors: Top[i]=[100+i,0,0], Bottom[i]=[0,100+i,0],
    /// Left[i]=[0,0,100+i], Right[i]=[100+i,100+i,0].
    /// </summary>
    private static EdgeColors MakeEdges(int zones = 4) => new()
    {
        Top = Enumerable.Range(0, zones).Select(i => new[] { 100 + i, 0, 0 }).ToArray(),
        Bottom = Enumerable.Range(0, zones).Select(i => new[] { 0, 100 + i, 0 }).ToArray(),
        Left = Enumerable.Range(0, zones).Select(i => new[] { 0, 0, 100 + i }).ToArray(),
        Right = Enumerable.Range(0, zones).Select(i => new[] { 100 + i, 100 + i, 0 }).ToArray(),
    };

    private static int[] ProjectOne(LedPoint led, EdgeColors edges, float brightness = 1f) =>
        LedProjection.Project(new[] { led }, edges, brightness)[0];

    // ------------------------------------------------------------ Normalize --

    [Fact]
    public void Normalize_maps_the_bounding_box_to_the_unit_square()
    {
        // Keyboard-style mm coordinates with an offset origin.
        var normalized = LedProjection.Normalize(new[]
        {
            new LedPoint(10, 5),
            new LedPoint(110, 5),
            new LedPoint(10, 55),
            new LedPoint(110, 55),
            new LedPoint(60, 30),
        });

        Assert.Equal(new LedPoint(0, 0), normalized[0]);
        Assert.Equal(new LedPoint(1, 0), normalized[1]);
        Assert.Equal(new LedPoint(0, 1), normalized[2]);
        Assert.Equal(new LedPoint(1, 1), normalized[3]);
        Assert.Equal(new LedPoint(0.5, 0.5), normalized[4]);
    }

    [Fact]
    public void Normalize_centers_a_single_led()
    {
        var normalized = LedProjection.Normalize(new[] { new LedPoint(42, 7) });
        Assert.Equal(new LedPoint(0.5, 0.5), Assert.Single(normalized));
    }

    [Fact]
    public void Normalize_centers_a_degenerate_axis()
    {
        // A horizontal light strip: every LED at the same Y (logical units).
        var normalized = LedProjection.Normalize(new[]
        {
            new LedPoint(0, 3),
            new LedPoint(5, 3),
            new LedPoint(10, 3),
        });

        Assert.Equal(new LedPoint(0, 0.5), normalized[0]);
        Assert.Equal(new LedPoint(0.5, 0.5), normalized[1]);
        Assert.Equal(new LedPoint(1, 0.5), normalized[2]);
    }

    [Fact]
    public void Normalize_returns_empty_for_empty_input()
    {
        Assert.Empty(LedProjection.Normalize(Array.Empty<LedPoint>()));
    }

    [Fact]
    public void Normalize_units_do_not_matter()
    {
        // The same physical shape in mm and in logical units must normalize identically (AC3).
        var mm = LedProjection.Normalize(new[] { new LedPoint(0, 0), new LedPoint(450, 150) });
        var logical = LedProjection.Normalize(new[] { new LedPoint(0, 0), new LedPoint(3, 1) });
        Assert.Equal(mm, logical);
    }

    // -------------------------------------------------------------- Project --

    [Theory]
    // Left-side LED → left edge, indexed by height (top→bottom).
    [InlineData(0.05, 0.10, 0, 0, 100)] // y=0.10 → zone 0
    [InlineData(0.05, 0.60, 0, 0, 102)] // y=0.60 → zone 2
    // Right-side LED → right edge, indexed by height.
    [InlineData(0.95, 0.30, 101, 101, 0)] // y=0.30 → zone 1
    // Top LED → top edge, indexed left→right.
    [InlineData(0.60, 0.05, 102, 0, 0)] // x=0.60 → zone 2
    // Bottom LED → bottom edge, indexed left→right.
    [InlineData(0.90, 0.95, 0, 103, 0)] // x=0.90 → zone 3
    public void Project_picks_the_nearest_edge_and_matching_zone(
        double x, double y, int r, int g, int b)
    {
        Assert.Equal(new[] { r, g, b }, ProjectOne(new LedPoint(x, y), MakeEdges()));
    }

    [Fact]
    public void Project_dead_center_tie_breaks_to_the_top_edge()
    {
        // All four edges are equidistant — the declared order (top first) must win, always.
        Assert.Equal(new[] { 102, 0, 0 }, ProjectOne(new LedPoint(0.5, 0.5), MakeEdges()));
    }

    [Fact]
    public void Project_indexes_the_last_zone_at_the_far_end_of_an_edge()
    {
        // t=1.0 would compute index 4 of 4 — must clamp to the last zone, not throw.
        Assert.Equal(new[] { 103, 0, 0 }, ProjectOne(new LedPoint(1.0, 0.0), MakeEdges()));
    }

    [Fact]
    public void Project_scales_by_brightness()
    {
        // Zone 2 of the top edge is [102,0,0]; half brightness rounds to 51.
        Assert.Equal(new[] { 51, 0, 0 }, ProjectOne(new LedPoint(0.5, 0.0), MakeEdges(), 0.5f));
        Assert.Equal(new[] { 0, 0, 0 }, ProjectOne(new LedPoint(0.5, 0.0), MakeEdges(), 0f));
    }

    [Fact]
    public void Project_repairs_out_of_range_brightness()
    {
        var full = ProjectOne(new LedPoint(0.5, 0.0), MakeEdges(), 1f);
        Assert.Equal(full, ProjectOne(new LedPoint(0.5, 0.0), MakeEdges(), 5f));
        Assert.Equal(full, ProjectOne(new LedPoint(0.5, 0.0), MakeEdges(), float.NaN));
    }

    [Fact]
    public void Project_clamps_out_of_range_led_positions()
    {
        // Garbage positions clamp into the unit square instead of indexing out of bounds.
        Assert.Equal(new[] { 100, 0, 0 }, ProjectOne(new LedPoint(-3, -7), MakeEdges()));
    }

    [Fact]
    public void Project_empty_edges_yield_black()
    {
        Assert.Equal(new[] { 0, 0, 0 }, ProjectOne(new LedPoint(0.5, 0.05), new EdgeColors()));
    }

    [Fact]
    public void Project_returns_empty_for_no_leds()
    {
        Assert.Empty(LedProjection.Project(Array.Empty<LedPoint>(), MakeEdges(), 1f));
    }

    [Fact]
    public void Project_clamps_color_components_to_byte_range()
    {
        var edges = new EdgeColors { Top = new[] { new[] { 999, -5, 255 } } };
        Assert.Equal(new[] { 255, 0, 255 }, ProjectOne(new LedPoint(0.5, 0.0), edges));
    }

    // ----------------------------------------------- placement (Story 8.2) --

    private static int[] ProjectAnchored(LedPoint led, string anchor, bool flip = false) =>
        LedProjection.Project(new[] { led }, MakeEdges(), 1f, anchor, flip)[0];

    [Theory]
    // A device left of the screen is fed by the LEFT edge, indexed by LED height.
    [InlineData("left", 0.9, 0.10, 0, 0, 100)]  // y=0.10 → left zone 0, even though x is far right
    [InlineData("left", 0.9, 0.60, 0, 0, 102)]
    [InlineData("right", 0.1, 0.30, 101, 101, 0)] // y=0.30 → right zone 1
    // Above/below are fed by the top/bottom edges, indexed by LED x.
    [InlineData("above", 0.60, 0.95, 102, 0, 0)] // x=0.60 → top zone 2, even though the LED is low
    [InlineData("below", 0.90, 0.05, 0, 103, 0)] // x=0.90 → bottom zone 3
    public void Anchor_feeds_the_device_from_that_single_edge(
        string anchor, double x, double y, int r, int g, int b)
    {
        Assert.Equal(new[] { r, g, b }, ProjectAnchored(new LedPoint(x, y), anchor));
    }

    [Theory]
    [InlineData("left", 0.5, 0.10, 0, 0, 103)]  // y=0.10 flipped → t=0.90 → zone 3
    [InlineData("above", 0.10, 0.5, 103, 0, 0)] // x=0.10 flipped → t=0.90 → zone 3
    public void Flip_reverses_the_zone_order_along_the_edge(
        string anchor, double x, double y, int r, int g, int b)
    {
        Assert.Equal(new[] { r, g, b }, ProjectAnchored(new LedPoint(x, y), anchor, flip: true));
    }

    [Theory]
    // Surround walks the perimeter by angle around the device center.
    [InlineData(0.5, 0.05, 102, 0, 0)]   // straight up → top edge middle (zone 2)
    [InlineData(0.95, 0.5, 102, 102, 0)] // straight right → right edge middle
    [InlineData(0.5, 0.95, 0, 102, 0)]   // straight down → bottom edge middle
    [InlineData(0.05, 0.5, 0, 0, 102)]   // straight left → left edge middle
    [InlineData(0.95, 0.05, 100, 100, 0)] // top-right diagonal → start of the right edge (zone 0)
    public void Surround_maps_led_angle_around_the_perimeter(double x, double y, int r, int g, int b)
    {
        Assert.Equal(new[] { r, g, b }, ProjectAnchored(new LedPoint(x, y), "surround"));
    }

    [Fact]
    public void Surround_flip_reverses_the_winding_direction()
    {
        // A right-pointing LED winds counterclockwise to the left edge when flipped.
        Assert.Equal(new[] { 0, 0, 102 }, ProjectAnchored(new LedPoint(0.95, 0.5), "surround", flip: true));
    }

    [Fact]
    public void Surround_centered_led_reads_the_top_edge_midpoint()
    {
        Assert.Equal(new[] { 102, 0, 0 }, ProjectAnchored(new LedPoint(0.5, 0.5), "surround"));
    }

    [Fact]
    public void Behind_and_auto_use_the_nearest_edge_heuristic()
    {
        var led = new LedPoint(0.05, 0.6);
        Assert.Equal(ProjectOne(led, MakeEdges()), ProjectAnchored(led, "behind"));
        Assert.Equal(ProjectOne(led, MakeEdges()), ProjectAnchored(led, "auto"));
        // Flip has no single direction to reverse for nearest-edge mapping — it is ignored.
        Assert.Equal(ProjectAnchored(led, "behind"), ProjectAnchored(led, "behind", flip: true));
    }

    [Fact]
    public void Unknown_anchor_falls_back_to_auto()
    {
        var led = new LedPoint(0.05, 0.6);
        Assert.Equal(ProjectOne(led, MakeEdges()), ProjectAnchored(led, "sideways"));
    }
}

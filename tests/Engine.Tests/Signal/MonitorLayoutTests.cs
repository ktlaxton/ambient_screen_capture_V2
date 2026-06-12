using AmbientFx.Models;
using AmbientFx.Services;
using Xunit;

namespace AmbientFx.Engine.Tests.Signal;

/// <summary>
/// FR7: physical-layout mapping so the correct source edge "spills" onto the adjacent target
/// monitor. Pure helper — no OS calls. Topology mirrors a real dev machine: 1920x1080 source
/// at the virtual-desktop origin.
/// </summary>
public class MonitorLayoutTests
{
    private static MonitorInfo Monitor(string id, int x, int y, int width = 1920, int height = 1080) => new()
    {
        Id = id,
        Name = id,
        X = x,
        Y = y,
        Width = width,
        Height = height,
    };

    private static readonly MonitorInfo Source = Monitor(@"\\.\DISPLAY1", 0, 0);

    // ---------- cardinal directions ----------

    [Fact]
    public void ComputeRelation_TargetDirectlyRight_ReturnsRight()
    {
        var target = Monitor(@"\\.\DISPLAY2", 1920, 0);

        Assert.Equal("right", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_TargetDirectlyLeft_ReturnsLeft()
    {
        var target = Monitor(@"\\.\DISPLAY2", -1920, 0);

        Assert.Equal("left", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_TargetAbove_ReturnsAbove()
    {
        // Screen Y grows downward: a target at y = -1080 is physically above the source.
        var target = Monitor(@"\\.\DISPLAY2", 0, -1080);

        Assert.Equal("above", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_TargetBelow_ReturnsBelow()
    {
        var target = Monitor(@"\\.\DISPLAY2", 0, 1080);

        Assert.Equal("below", MonitorLayout.ComputeRelation(Source, target));
    }

    // ---------- "none" cases ----------

    [Fact]
    public void ComputeRelation_SameId_ReturnsNone()
    {
        var target = Monitor(@"\\.\DISPLAY1", 1920, 0); // would be "right" if the ids differed

        Assert.Equal("none", MonitorLayout.ComputeRelation(Source, target));
    }

    [Theory]
    [InlineData(0, 1080)]    // zero width
    [InlineData(1920, 0)]    // zero height
    [InlineData(-10, 1080)]  // negative width
    public void ComputeRelation_ZeroAreaTarget_ReturnsNone(int width, int height)
    {
        var target = Monitor(@"\\.\DISPLAY2", 1920, 0, width, height);

        Assert.Equal("none", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_ZeroAreaSource_ReturnsNone()
    {
        var degenerateSource = Monitor(@"\\.\DISPLAY1", 0, 0, 0, 0);
        var target = Monitor(@"\\.\DISPLAY2", 1920, 0);

        Assert.Equal("none", MonitorLayout.ComputeRelation(degenerateSource, target));
    }

    [Fact]
    public void ComputeRelation_NearIdenticalBoundsDifferentId_FallsInDeadZone()
    {
        // Centers offset (10, 10) px -> ndx ~0.0052, ndy ~0.0093: both below the 0.05 dead zone.
        var target = Monitor(@"\\.\DISPLAY2", 10, 10);

        Assert.Equal("none", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_NullArguments_ReturnNone()
    {
        Assert.Equal("none", MonitorLayout.ComputeRelation(null!, Monitor(@"\\.\DISPLAY2", 1920, 0)));
        Assert.Equal("none", MonitorLayout.ComputeRelation(Source, null!));
        Assert.Equal("none", MonitorLayout.ComputeRelation(null!, null!));
    }

    // ---------- diagonal: dominant axis per the normalized formula ----------

    [Fact]
    public void ComputeRelation_DiagonalTarget_ResolvesToDominantNormalizedAxis()
    {
        // Target at (2000, 900), same 1920x1080 size.
        // Centers: source (960, 540), target (2960, 1440) -> dx = 2000, dy = 900.
        // ndx = 2000 / 1920 = 1.0417; ndy = 900 / 1080 = 0.8333. |ndx| > |ndy| -> "right".
        var target = Monitor(@"\\.\DISPLAY2", 2000, 900);

        Assert.Equal("right", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_ExactDiagonalTie_HorizontalAxisWins()
    {
        // Offset (1920, 1080): ndx = 1.0, ndy = 1.0 — the >= comparison favors horizontal.
        var target = Monitor(@"\\.\DISPLAY2", 1920, 1080);

        Assert.Equal("right", MonitorLayout.ComputeRelation(Source, target));
    }

    [Fact]
    public void ComputeRelation_MixedResolutionNeighbor_NormalizesByAverageExtent()
    {
        // 2560x1440 monitor flush right of the 1920x1080 source.
        // dx = 2240, avg width = 2240 -> ndx = 1.0; dy = 180, avg height = 1260 -> ndy ~0.143.
        var target = Monitor(@"\\.\DISPLAY2", 1920, 0, 2560, 1440);

        Assert.Equal("right", MonitorLayout.ComputeRelation(Source, target));
    }
}

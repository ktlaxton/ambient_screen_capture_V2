#if SIMULATOR_ENABLED
using AmbientFx.Models;
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign ("my real setup, mirrored"): the clone builder maps real monitors onto virtual twins —
/// synthetic SIM-DISPLAY ids (never the real ids), copied geometry (negative coords included), each
/// twin mirroring its real counterpart's stable id, and the source resolving to the primary's twin.
/// </summary>
public sealed class SimulatorRealSetupCloneTests
{
    private static MonitorInfo Real(string id, string name, int x, int y, int w, int h, bool primary = false) =>
        new() { Id = id, Name = name, X = x, Y = y, Width = w, Height = h, IsPrimary = primary, HMonitor = (IntPtr)7 };

    [Fact]
    public void Build_EmptyOrNull_FallsBack()
    {
        Assert.Single(SimulatorRealSetupClone.Build(null).Monitors);
        Assert.Single(SimulatorRealSetupClone.Build(Array.Empty<MonitorInfo>()).Monitors);
    }

    [Fact]
    public void Build_MapsGeometryIdsAndMirrors()
    {
        var real = new[]
        {
            Real(@"\\?\DISPLAY#DELA1E2#5&1a2b3c&0&UID4352#{guid}", "DELL U2723QE", 0, 0, 2560, 1440, primary: true),
            Real(@"\\?\DISPLAY#GSM5B08#4&2f3e4d&0&UID261#{guid}", "LG 27GL850", 2560, -180, 1920, 1080),
        };

        var scenario = SimulatorRealSetupClone.Build(real);

        Assert.Equal(SimulatorRealSetupClone.ScenarioName, scenario.Name);
        Assert.Equal(2, scenario.Monitors.Count);

        var twin1 = scenario.Monitors[0];
        Assert.Equal(@"\\.\SIM-DISPLAY1", twin1.Id);
        Assert.Equal("DELL U2723QE", twin1.Name);
        Assert.Equal((0, 0, 2560, 1440), (twin1.X, twin1.Y, twin1.Width, twin1.Height));
        Assert.True(twin1.IsPrimary);
        Assert.NotNull(twin1.Content);
        Assert.Equal(SimContent.Mirror, twin1.Content!.Kind);
        Assert.Equal(real[0].Id, twin1.Content.PhysicalMonitorId);

        var twin2 = scenario.Monitors[1];
        Assert.Equal(@"\\.\SIM-DISPLAY2", twin2.Id);
        Assert.Equal((2560, -180), (twin2.X, twin2.Y)); // negative coords preserved
        Assert.False(twin2.IsPrimary);
        Assert.Equal(real[1].Id, twin2.Content!.PhysicalMonitorId);

        Assert.Equal(twin1.Id, scenario.SourceMonitorId); // source = twin of the real primary
    }

    [Fact]
    public void Build_NoPrimary_PromotesFirst_AndSourcesIt()
    {
        var scenario = SimulatorRealSetupClone.Build(new[]
        {
            Real("real-a", "A", 0, 0, 1920, 1080),
            Real("real-b", "B", 1920, 0, 1920, 1080),
        });

        Assert.True(scenario.Monitors[0].IsPrimary);
        Assert.Equal(scenario.Monitors[0].Id, scenario.SourceMonitorId);
    }

    [Fact]
    public void Build_MultiplePrimaries_KeepsOnlyTheFirst()
    {
        var scenario = SimulatorRealSetupClone.Build(new[]
        {
            Real("real-a", "A", 0, 0, 1920, 1080, primary: true),
            Real("real-b", "B", 1920, 0, 1920, 1080, primary: true),
        });

        var primary = Assert.Single(scenario.Monitors, m => m.IsPrimary);
        Assert.Equal(@"\\.\SIM-DISPLAY1", primary.Id);
    }

    [Fact]
    public void Build_RealMonitorWithoutStableId_StaysSynthetic()
    {
        var scenario = SimulatorRealSetupClone.Build(new[] { Real("", "Ghost", 0, 0, 1920, 1080, primary: true) });

        Assert.Null(Assert.Single(scenario.Monitors).Content);
    }

    [Fact]
    public void Build_Result_SurvivesValidateUnchanged()
    {
        var scenario = SimulatorRealSetupClone.Build(new[]
        {
            Real("real-a", "A", 0, 0, 2560, 1440, primary: true),
            Real("real-b", "B", 2560, 0, 1920, 1080),
        });

        int monitorCount = scenario.Monitors.Count;
        scenario.Validate(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(monitorCount, scenario.Monitors.Count);
        Assert.Single(scenario.Monitors, m => m.IsPrimary);
    }
}
#endif

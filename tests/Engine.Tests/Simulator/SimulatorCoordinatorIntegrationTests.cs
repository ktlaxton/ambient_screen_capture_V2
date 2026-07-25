#if SIMULATOR_ENABLED
using AmbientFx.Hosting;
using AmbientFx.Models;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AmbientFx.Engine.Tests.Coordinator;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.1 AC10(c): wire the real <see cref="SimulatedMonitorDetectionService"/> into the real,
/// unmodified <see cref="AmbientFx.Services.EngineCoordinator"/> and assert the WindowConfigPayload /
/// Relation set across several fabricated topologies, plus a FireMonitorsChanged re-sync. No effect
/// windows are rendered (that is Story 10.2); the engine runs headless and we assert the specs it
/// produces — the same data 10.2 will draw.
/// </summary>
public sealed class SimulatorCoordinatorIntegrationTests
{
    private const string D1 = @"\\.\SIM-DISPLAY1";
    private const string D2 = @"\\.\SIM-DISPLAY2";
    private const string D3 = @"\\.\SIM-DISPLAY3";

    private static SimulatorMonitor Mon(string id, int x, int y, int w, int h, bool primary = false) =>
        new() { Id = id, Name = id, X = x, Y = y, Width = w, Height = h, IsPrimary = primary };

    private static SimulatorScenario Scenario(string name, params SimulatorMonitor[] monitors) =>
        new() { Name = name, Monitors = monitors.ToList() };

    private static SimulatedMonitorDetectionService Detection(SimulatorScenario scenario) =>
        new(NullLogger<SimulatedMonitorDetectionService>.Instance, scenario);

    private static async Task<IReadOnlyList<EffectWindowSpec>> SyncFor(
        SimulatorScenario scenario, string sourceId, params string[] targetIds)
    {
        var h = new CoordinatorHarness(Detection(scenario));
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = sourceId;
        h.InitialSettings.TargetMonitorIds = targetIds.ToList();
        await h.StartAsync();
        return h.Syncs.Last();
    }

    private static EffectWindowSpec SpecFor(IReadOnlyList<EffectWindowSpec> specs, string monitorId) =>
        Assert.Single(specs, s => s.Monitor.Id == monitorId);

    [Fact]
    public async Task ThreeWide_SourceInMiddle_ResolvesLeftAndRight()
    {
        var scenario = Scenario("3-wide",
            Mon(D1, 0, 0, 1920, 1080),
            Mon(D2, 1920, 0, 1920, 1080, primary: true),
            Mon(D3, 3840, 0, 1920, 1080));

        var specs = await SyncFor(scenario, sourceId: D2, D1, D3);

        Assert.Equal(2, specs.Count);
        Assert.Equal("left", SpecFor(specs, D1).Config.Relation);
        Assert.Equal("right", SpecFor(specs, D3).Config.Relation);
        Assert.All(specs, s => Assert.Equal(D2, s.Config.Source?.Id));
    }

    [Fact]
    public async Task LShape_ResolvesRightAndBelow()
    {
        var scenario = Scenario("L-shape",
            Mon(D1, 0, 0, 1920, 1080, primary: true),
            Mon(D2, 1920, 0, 1920, 1080),
            Mon(D3, 0, 1080, 1920, 1080));

        var specs = await SyncFor(scenario, sourceId: D1, D2, D3);

        Assert.Equal("right", SpecFor(specs, D2).Config.Relation);
        Assert.Equal("below", SpecFor(specs, D3).Config.Relation);
    }

    [Fact]
    public async Task VerticalStack_ResolvesAboveAndBelow()
    {
        var scenario = Scenario("vertical-stack",
            Mon(D1, 0, 0, 1920, 1080, primary: true),
            Mon(D2, 0, 1080, 1920, 1080),
            Mon(D3, 0, -1080, 1920, 1080));

        var specs = await SyncFor(scenario, sourceId: D1, D2, D3);

        Assert.Equal("below", SpecFor(specs, D2).Config.Relation);
        Assert.Equal("above", SpecFor(specs, D3).Config.Relation);
    }

    [Fact]
    public async Task MixedResolution_FlushRightNeighbor_ResolvesRight_AndCarriesRects()
    {
        // 2560x1440 source with a 1920x1080 neighbor flush-right at (2560,180) — the SIM_MONITORS pair.
        var scenario = Scenario("mixed-dpi",
            Mon(D1, 0, 0, 2560, 1440, primary: true),
            Mon(D2, 2560, 180, 1920, 1080));

        var specs = await SyncFor(scenario, sourceId: D1, D2);

        var d2 = SpecFor(specs, D2);
        Assert.Equal("right", d2.Config.Relation);
        Assert.Equal(D2, d2.Config.Monitor?.Id);
        Assert.Equal((1920, 1080), (d2.Config.Monitor!.Width, d2.Config.Monitor.Height));
        Assert.Equal((2560, 1440), (d2.Config.Source!.Width, d2.Config.Source.Height));
    }

    [Fact]
    public async Task FireMonitorsChanged_HotPlug_DrivesTheRealReSyncPath()
    {
        UiApp.EnsureRunning(); // OnMonitorsChanged marshals through Application.Current.Dispatcher

        var scenario = Scenario("hot-plug",
            Mon(D1, 0, 0, 1920, 1080, primary: true),
            Mon(D2, 1920, 0, 1920, 1080));
        var detection = Detection(scenario);

        var h = new CoordinatorHarness(detection);
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2, D3 }; // D3 not present yet
        await h.StartAsync();

        // Initial sync: only D2 exists, so D3 is skipped.
        Assert.Single(h.Syncs.Last());
        Assert.Equal("right", SpecFor(h.Syncs.Last(), D2).Config.Relation);

        // Hot-plug D3 below the source and fire the on-demand change.
        detection.AddMonitor(new MonitorInfo { Id = D3, Name = D3, X = 0, Y = 1080, Width = 1920, Height = 1080 });
        detection.FireMonitorsChanged();

        CoordinatorHarness.WaitUntil(() => h.Syncs.Last().Count == 2,
            because: "the real coordinator re-sync must pick up the hot-plugged monitor");
        Assert.Equal("below", SpecFor(h.Syncs.Last(), D3).Config.Relation);
    }
}
#endif

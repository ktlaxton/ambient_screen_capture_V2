#if SIMULATOR_ENABLED
using AmbientFx.Models;
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (preset save): <see cref="SimulatorSceneCapture.Capture"/> overlays the LIVE engine
/// state (source / global + per-monitor effects / fps, from the coordinator-mutated
/// <see cref="ApplicationSettings"/>) and the live placements onto the backing scenario — live wins,
/// defaults are pruned, and nothing is aliased.
/// </summary>
public sealed class SimulatorSceneCaptureTests
{
    private static SimulatorScenario Backing() => new()
    {
        Name = "backing",
        SourceMonitorId = "a",
        Monitors = new List<SimulatorMonitor>
        {
            new() { Id = "a", Width = 1920, Height = 1080, IsPrimary = true, Pattern = SyntheticPatterns.Bars, Effect = "stale-effect" },
            new() { Id = "b", X = 1920, Width = 1920, Height = 1080 },
        },
    };

    private static List<MonitorInfo> LiveMonitors() => new()
    {
        new() { Id = "a", Name = "A", X = 0, Y = 0, Width = 1920, Height = 1080, IsPrimary = true },
        new() { Id = "b", Name = "B", X = 1920, Y = 0, Width = 1920, Height = 1080 },
    };

    [Fact]
    public void Capture_NullLive_KeepsBackingState_CapturesPlacements()
    {
        var placements = new Dictionary<string, DevicePlacement>
        {
            ["kb"] = new() { Anchor = DeviceAnchors.Left },
            ["mouse"] = new(), // default — must be pruned
        };

        var result = SimulatorSceneCapture.Capture(Backing(), LiveMonitors(), live: null, placements);

        Assert.Equal("a", result.SourceMonitorId);
        Assert.Null(result.ActiveEffectId);
        Assert.Null(result.GlobalMaxFps);
        Assert.Equal("stale-effect", result.Monitors[0].Effect); // no live state to overrule it

        var captured = Assert.Single(result.DevicePlacements!);
        Assert.Equal("kb", captured.Key);

        // Deep clone: mutating the live dict must not touch the captured scene.
        placements["kb"].Anchor = DeviceAnchors.Surround;
        Assert.Equal(DeviceAnchors.Left, result.DevicePlacements!["kb"].Anchor);
    }

    [Fact]
    public void Capture_LiveState_OverlaysSourceEffectsAndFps()
    {
        var live = new ApplicationSettings
        {
            SourceMonitorId = "b",
            ActiveEffectId = "plasma",
            MaxFps = 120,
            EffectByMonitorId = { ["b"] = "rain" },
        };

        var result = SimulatorSceneCapture.Capture(Backing(), LiveMonitors(), live, placements: null);

        Assert.Equal("b", result.SourceMonitorId);
        Assert.Equal("plasma", result.ActiveEffectId);
        Assert.Equal(120, result.GlobalMaxFps);
        Assert.Equal("rain", Assert.Single(result.Monitors, m => m.Id == "b").Effect);
        // Live override map is the truth: 'a' has no live override, so its stale backing value drops.
        Assert.Null(Assert.Single(result.Monitors, m => m.Id == "a").Effect);
        Assert.Null(result.DevicePlacements);
    }

    [Fact]
    public void Capture_LiveSourceUnknown_KeepsBackingResolution()
    {
        var live = new ApplicationSettings { SourceMonitorId = "not-a-monitor" };

        var result = SimulatorSceneCapture.Capture(Backing(), LiveMonitors(), live, placements: null);

        Assert.Equal("a", result.SourceMonitorId);
    }

    [Fact]
    public void Capture_PreservesPerMonitorContentViaWithTopology()
    {
        var backing = Backing();
        backing.Monitors[1].Content = new SimContent { Kind = SimContent.Mirror, PhysicalMonitorId = "real-x" };

        var result = SimulatorSceneCapture.Capture(backing, LiveMonitors(), live: null, placements: null);

        Assert.Equal("real-x", Assert.Single(result.Monitors, m => m.Id == "b").Content!.PhysicalMonitorId);
        Assert.Equal(SyntheticPatterns.Bars, Assert.Single(result.Monitors, m => m.Id == "a").Pattern);
    }
}
#endif

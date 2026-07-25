#if SIMULATOR_ENABLED
using AmbientFx.Models;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (schema v2 — full-scene presets): the new fields (per-monitor effect, global effect,
/// global fps, device placements) round-trip through JSON, survive <see
/// cref="SimulatorScenario.WithTopology"/>, are sanitized by Validate, and v1 files still load
/// unchanged with the new fields null (backward compatibility, no version gate).
/// </summary>
public sealed class SimulatorScenarioV2Tests
{
    private static SimulatorScenario Parse(string json) => SimulatorScenario.Parse(json, NullLogger.Instance);

    private static SimulatorScenario FullScene() => new()
    {
        Name = "scene",
        SourceMonitorId = "a",
        ActiveEffectId = "plasma",
        GlobalMaxFps = 120,
        DevicePlacements = new Dictionary<string, DevicePlacement>
        {
            ["0:K95 (sim)"] = new() { Anchor = DeviceAnchors.Left, Flip = true, Brightness = 0.5f },
        },
        Monitors = new List<SimulatorMonitor>
        {
            new() { Id = "a", Width = 1920, Height = 1080, IsPrimary = true, Effect = "fire" },
            new() { Id = "b", X = 1920, Width = 1920, Height = 1080 },
        },
    };

    [Fact]
    public void ToJson_Parse_RoundTripsAllV2Fields()
    {
        var parsed = Parse(FullScene().ToJson());

        Assert.Equal("plasma", parsed.ActiveEffectId);
        Assert.Equal(120, parsed.GlobalMaxFps);
        Assert.Equal("fire", Assert.Single(parsed.Monitors, m => m.Id == "a").Effect);
        Assert.Null(Assert.Single(parsed.Monitors, m => m.Id == "b").Effect);

        Assert.NotNull(parsed.DevicePlacements);
        var placement = parsed.DevicePlacements!["0:K95 (sim)"];
        Assert.Equal(DeviceAnchors.Left, placement.Anchor);
        Assert.True(placement.Flip);
        Assert.Equal(0.5f, placement.Brightness);
    }

    [Fact]
    public void V1File_LoadsWithV2FieldsNull()
    {
        var scenario = Parse("""
        {
          "version": 1,
          "name": "legacy",
          "sourceMonitorId": "a",
          "monitors": [
            { "id": "a", "width": 1920, "height": 1080, "isPrimary": true, "pattern": "bars" }
          ]
        }
        """);

        Assert.Null(scenario.ActiveEffectId);
        Assert.Null(scenario.GlobalMaxFps);
        Assert.Null(scenario.DevicePlacements);
        Assert.Null(Assert.Single(scenario.Monitors).Effect);
        Assert.Equal("bars", Assert.Single(scenario.Monitors).Pattern); // v1 content untouched
    }

    [Fact]
    public void ToJson_OmitsNullV2Fields()
    {
        string json = SimulatorScenario.Fallback().ToJson();

        Assert.DoesNotContain("activeEffectId", json);
        Assert.DoesNotContain("globalMaxFps", json);
        Assert.DoesNotContain("devicePlacements", json);
        Assert.DoesNotContain("\"effect\"", json);
    }

    [Fact]
    public void WithTopology_PreservesV2Fields_AndDeepClonesPlacements()
    {
        var scene = FullScene();
        var live = new List<MonitorInfo>
        {
            new() { Id = "a", Name = "A", X = 100, Y = 0, Width = 2560, Height = 1440, IsPrimary = true },
            new() { Id = "c", Name = "added", X = 2660, Y = 0, Width = 1920, Height = 1080 },
        };

        var result = scene.WithTopology(live);

        Assert.Equal("plasma", result.ActiveEffectId);
        Assert.Equal(120, result.GlobalMaxFps);
        Assert.Equal("fire", Assert.Single(result.Monitors, m => m.Id == "a").Effect); // preserved by id
        Assert.Null(Assert.Single(result.Monitors, m => m.Id == "c").Effect);          // new monitor: default

        // Placements are cloned, not aliased.
        scene.DevicePlacements!["0:K95 (sim)"].Anchor = DeviceAnchors.Surround;
        Assert.Equal(DeviceAnchors.Left, result.DevicePlacements!["0:K95 (sim)"].Anchor);
    }

    [Fact]
    public void Validate_DropsInvalidPlacements_AndClampsBrightness()
    {
        var scenario = FullScene();
        scenario.DevicePlacements = new Dictionary<string, DevicePlacement>
        {
            ["bad-anchor"] = new() { Anchor = "diagonal" },
            [""] = new() { Anchor = DeviceAnchors.Left },
            ["too-bright"] = new() { Anchor = DeviceAnchors.Right, Brightness = 5f },
            ["negative"] = new() { Anchor = DeviceAnchors.Above, Brightness = -1f },
            ["nan"] = new() { Anchor = DeviceAnchors.Below, Brightness = float.NaN },
        };

        scenario.Validate(NullLogger.Instance);

        Assert.False(scenario.DevicePlacements.ContainsKey("bad-anchor"));
        Assert.False(scenario.DevicePlacements.ContainsKey(""));
        Assert.Equal(1f, scenario.DevicePlacements["too-bright"].Brightness);
        Assert.Equal(0f, scenario.DevicePlacements["negative"].Brightness);
        Assert.Equal(1f, scenario.DevicePlacements["nan"].Brightness);
    }
}
#endif

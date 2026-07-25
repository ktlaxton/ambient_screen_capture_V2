#if SIMULATOR_ENABLED
using System.IO;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.1 AC9: the scenario loader is defensive — malformed/missing input degrades to a clear log
/// plus a sane default topology and never throws; validation coerces exactly one primary, positive
/// dimensions, and unique ids. These branches are exercised here (the happy path is covered by the
/// detection-service / integration tests).
/// </summary>
public sealed class SimulatorScenarioTests
{
    private static SimulatorScenario Parse(string json) => SimulatorScenario.Parse(json, NullLogger.Instance);

    [Fact]
    public void Parse_InvalidJson_ReturnsFallback_NeverThrows()
    {
        var exception = Record.Exception(() =>
        {
            var scenario = Parse("this is not json {{{");
            Assert.NotNull(scenario);
            var monitor = Assert.Single(scenario.Monitors);
            Assert.True(monitor.IsPrimary);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Parse_EmptyMonitors_SubstitutesFallbackTopology()
    {
        var scenario = Parse("""{ "version": 1, "name": "empty", "monitors": [] }""");

        var monitor = Assert.Single(scenario.Monitors);
        Assert.True(monitor.IsPrimary);
        Assert.True(monitor.Width > 0 && monitor.Height > 0);
    }

    [Fact]
    public void Validate_MultiplePrimary_KeepsExactlyTheFirst()
    {
        var scenario = Parse("""
        {
          "version": 1,
          "monitors": [
            { "id": "a", "width": 1920, "height": 1080, "isPrimary": true },
            { "id": "b", "width": 1920, "height": 1080, "isPrimary": true },
            { "id": "c", "width": 1920, "height": 1080, "isPrimary": true }
          ]
        }
        """);

        Assert.Equal(3, scenario.Monitors.Count);
        var primary = Assert.Single(scenario.Monitors, m => m.IsPrimary);
        Assert.Equal("a", primary.Id);
    }

    [Fact]
    public void Validate_NoPrimary_PromotesTheFirstMonitor()
    {
        var scenario = Parse("""
        {
          "monitors": [
            { "id": "a", "width": 1920, "height": 1080 },
            { "id": "b", "width": 1920, "height": 1080 }
          ]
        }
        """);

        var primary = Assert.Single(scenario.Monitors, m => m.IsPrimary);
        Assert.Equal("a", primary.Id);
    }

    [Fact]
    public void Validate_DuplicateIds_AreMadeUnique()
    {
        var scenario = Parse("""
        {
          "monitors": [
            { "id": "dup", "width": 1920, "height": 1080, "isPrimary": true },
            { "id": "dup", "width": 1280, "height": 720 }
          ]
        }
        """);

        Assert.Equal(2, scenario.Monitors.Count);
        Assert.Equal(2, scenario.Monitors.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Validate_NonPositiveDimensions_AreCoercedTo1080p()
    {
        var scenario = Parse("""
        {
          "monitors": [
            { "id": "a", "width": 0, "height": -5, "isPrimary": true }
          ]
        }
        """);

        var m = Assert.Single(scenario.Monitors);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
    }

    [Fact]
    public void Validate_UnknownPattern_FallsBackToGradient()
    {
        var scenario = Parse("""
        {
          "monitors": [
            { "id": "a", "width": 1920, "height": 1080, "isPrimary": true, "pattern": "nonsense" }
          ]
        }
        """);

        Assert.Equal(SyntheticPatterns.Gradient, Assert.Single(scenario.Monitors).Pattern);
    }

    [Fact]
    public void LoadFromFile_MissingFile_ReturnsFallback_NeverThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ambientfx-no-such-scenario-" + Guid.NewGuid().ToString("N") + ".json");

        SimulatorScenario scenario = null!;
        var exception = Record.Exception(() => scenario = SimulatorScenario.LoadFromFile(missing, NullLogger.Instance));

        Assert.Null(exception);
        Assert.NotNull(scenario);
        Assert.Single(scenario.Monitors, m => m.IsPrimary);
    }

    [Fact]
    public void Fallback_IsASingle1080pPrimary()
    {
        var fallback = SimulatorScenario.Fallback();

        var m = Assert.Single(fallback.Monitors);
        Assert.True(m.IsPrimary);
        Assert.Equal((1920, 1080), (m.Width, m.Height));
        Assert.Equal(m.Id, fallback.ResolveSourceId());
    }

    [Fact]
    public void ResolveSourceId_DefaultsToPrimary_WhenSourceIdUnknown()
    {
        var scenario = Parse("""
        {
          "sourceMonitorId": "does-not-exist",
          "monitors": [
            { "id": "a", "width": 1920, "height": 1080 },
            { "id": "b", "width": 1920, "height": 1080, "isPrimary": true }
          ]
        }
        """);

        Assert.Equal("b", scenario.ResolveSourceId());
    }
}
#endif

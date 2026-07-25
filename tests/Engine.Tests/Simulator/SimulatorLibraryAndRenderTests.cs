#if SIMULATOR_ENABLED
using System.IO;
using AmbientFx.Models;
using AmbientFx.Services;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.5 AC8: every curated scenario loads + validates; save→load round-trips every field; and the
/// headless render hook produces a deterministic artifact.
/// </summary>
public sealed class SimulatorLibraryAndRenderTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Fact]
    public void Library_HasTheCuratedScenarios()
    {
        Assert.Equal(8, SimulatorScenarioLibrary.Names.Count);
        Assert.Contains("SIM_MONITORS", SimulatorScenarioLibrary.Names);
        Assert.Contains("six-grid", SimulatorScenarioLibrary.Names);
    }

    [Fact]
    public void EveryCuratedScenario_LoadsAndValidates()
    {
        foreach (var name in SimulatorScenarioLibrary.Names)
        {
            var scenario = SimulatorScenarioLibrary.Load(name, Log);

            Assert.NotEmpty(scenario.Monitors);
            Assert.Equal(1, scenario.Monitors.Count(m => m.IsPrimary)); // exactly one primary
            Assert.Equal(scenario.Monitors.Count,
                scenario.Monitors.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()); // unique ids
            Assert.All(scenario.Monitors, m =>
            {
                Assert.True(m.Width > 0 && m.Height > 0, $"{name}/{m.Id} positive size");
                Assert.True(m.Scale > 0, $"{name}/{m.Id} positive scale");
            });
            Assert.Contains(scenario.Monitors, m => m.Id == scenario.ResolveSourceId()); // source resolves
        }
    }

    [Fact]
    public void SixGrid_HasSixMonitors_AndPortraitFlankedHasAPortrait()
    {
        Assert.Equal(6, SimulatorScenarioLibrary.Load("six-grid", Log).Monitors.Count);
        var portrait = SimulatorScenarioLibrary.Load("portrait-flanked", Log);
        Assert.Contains(portrait.Monitors, m => m.Height > m.Width); // a portrait monitor exists
    }

    [Fact]
    public void CuratedRelations_AgreeWithMonitorLayout()
    {
        // portrait-flanked: the portrait sits to the right of the 1440p source.
        var s = SimulatorScenarioLibrary.Load("portrait-flanked", Log);
        var monitors = s.ToMonitorInfos();
        var source = monitors.First(m => m.Id == s.ResolveSourceId());
        var portrait = monitors.First(m => m.Height > m.Width);
        Assert.Equal("right", MonitorLayout.ComputeRelation(source, portrait));

        // vertical-stack: one monitor above and one below the source.
        var vs = SimulatorScenarioLibrary.Load("vertical-stack", Log).ToMonitorInfos();
        var vsSource = vs.First(m => m.IsPrimary);
        var relations = vs.Where(m => m.Id != vsSource.Id)
            .Select(m => MonitorLayout.ComputeRelation(vsSource, m)).ToList();
        Assert.Contains("above", relations);
        Assert.Contains("below", relations);
    }

    [Fact]
    public void SaveLoad_RoundTripsEveryField()
    {
        var original = SimulatorScenarioLibrary.Load("L-shape", Log);
        string path = Path.Combine(Path.GetTempPath(), $"afx-scenario-{Guid.NewGuid():N}.json");
        try
        {
            original.Save(path);
            var loaded = SimulatorScenario.LoadFromFile(path, Log);

            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.ResolveSourceId(), loaded.ResolveSourceId());
            Assert.Equal(original.Monitors.Count, loaded.Monitors.Count);
            for (int i = 0; i < original.Monitors.Count; i++)
            {
                var a = original.Monitors[i];
                var b = loaded.Monitors[i];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal((a.X, a.Y, a.Width, a.Height), (b.X, b.Y, b.Width, b.Height));
                Assert.Equal(a.IsPrimary, b.IsPrimary);
                Assert.Equal(a.Pattern, b.Pattern);
                Assert.Equal(a.MaxFps, b.MaxFps);
                Assert.Equal(a.Scale, b.Scale);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EditorSave_WithTopology_PreservesPerMonitorFields()
    {
        var scenario = SimulatorScenarioLibrary.Load("mixed-dpi", Log);
        var primary = scenario.Monitors.First(m => m.IsPrimary);
        Assert.Equal(1.5, primary.Scale); // sanity: the fixture carries a non-default scale

        // Simulate the editor's live topology (geometry only, as MonitorInfo) with an edit.
        var live = scenario.ToMonitorInfos();
        live[0].Width = 5120;

        var saved = scenario.WithTopology(live);

        var savedPrimary = saved.Monitors.First(m => m.Id == primary.Id);
        Assert.Equal(5120, savedPrimary.Width);          // the geometry edit is applied
        Assert.Equal(1.5, savedPrimary.Scale);           // scale preserved (a naive MonitorInfo save loses it)
        Assert.Equal(primary.Pattern, savedPrimary.Pattern);
        Assert.Contains(saved.Monitors, m => m.Pattern == "bars"); // the other monitor's pattern survives
    }

    [Fact]
    public void RenderHook_ComputeRelations_UsesTheRealEngineLayout()
    {
        // 3-wide: source is the centre (DISPLAY2); the engine's MonitorLayout decides left/right.
        var relations = SimulatorRenderHook.ComputeRelations(SimulatorScenarioLibrary.Load("3-wide", Log));

        Assert.Equal("source", relations[@"\\.\SIM-DISPLAY2"]);
        Assert.Equal("left", relations[@"\\.\SIM-DISPLAY1"]);
        Assert.Equal("right", relations[@"\\.\SIM-DISPLAY3"]);
    }

    [Fact]
    public void RenderHook_ComposeBgra_IsDeterministic_ForFixedFrameIndex()
    {
        var scenario = SimulatorScenarioLibrary.Load("3-wide", Log);

        var a = SimulatorRenderHook.ComposeBgra(scenario, frameIndex: 0, out int wa, out int ha);
        var b = SimulatorRenderHook.ComposeBgra(scenario, frameIndex: 0, out int wb, out int hb);

        Assert.Equal((wa, ha), (wb, hb));
        Assert.Equal(a, b); // byte-identical across runs — the snapshot-regression seam

        // A different frame index yields different pixels (the content is animated).
        var c = SimulatorRenderHook.ComposeBgra(scenario, frameIndex: 7, out _, out _);
        Assert.False(a.AsSpan().SequenceEqual(c));
    }

    [Fact]
    public void RenderHook_RenderComposite_WritesANonEmptyPng()
    {
        var scenario = SimulatorScenarioLibrary.Load("SIM_MONITORS", Log);
        string path = Path.Combine(Path.GetTempPath(), $"afx-render-{Guid.NewGuid():N}.png");
        try
        {
            string result = SimulatorRenderHook.RenderComposite(scenario, frameIndex: 0, path);
            Assert.Equal(path, result);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RenderHook_CliArgs_RendersNamedScenario()
    {
        string outDir = Path.Combine(Path.GetTempPath(), $"afx-cli-{Guid.NewGuid():N}");
        try
        {
            var path = SimulatorRenderHook.TryRunFromArgs(
                new[] { "--simulator", "--simulator-render", "L-shape", "--out", outDir }, Log);
            Assert.NotNull(path);
            Assert.True(File.Exists(path!));

            Assert.Null(SimulatorRenderHook.TryRunFromArgs(new[] { "--simulator" }, Log)); // no render arg
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
#endif

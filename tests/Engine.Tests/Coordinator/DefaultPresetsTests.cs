using AmbientFx.Bridge;
using AmbientFx.Models;
using Xunit;

namespace AmbientFx.Engine.Tests.Coordinator;

/// <summary>
/// Story 7.2 AC6: the curated preset library ships on first run and never clobbers the
/// user's machine-specific monitor selection when loaded.
/// </summary>
public sealed class DefaultPresetsTests
{
    /// <summary>Pulls a fresh config snapshot via requestState (StartAsync alone doesn't push one).</summary>
    private static ConfigPayload CurrentConfig(CoordinatorHarness h)
    {
        h.Send(CommandTypes.RequestState);
        return (ConfigPayload)h.ControlPosts.Last(p => p.Type == MessageTypes.Config).Payload;
    }

    [Fact]
    public void Build_ReturnsCuratedPresets_WithHygiene()
    {
        var presets = DefaultPresets.Build(new ApplicationSettings());

        Assert.True(presets.Count >= 5, "ship a meaningful starter library");
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var preset in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.NotNull(preset.Snapshot);
            Assert.Empty(preset.Snapshot.Presets); // snapshots never nest presets
            Assert.Equal(string.Empty, preset.Snapshot.ActivePresetName);
            // Machine-agnostic: no monitor binding, not enabled.
            Assert.Equal(string.Empty, preset.Snapshot.SourceMonitorId);
            Assert.Empty(preset.Snapshot.TargetMonitorIds);
            Assert.False(preset.Snapshot.IsEnabled);
            // Each preset styles a real effect with at least one param.
            Assert.False(string.IsNullOrWhiteSpace(preset.Snapshot.ActiveEffectId));
            Assert.True(preset.Snapshot.EffectParamsById.ContainsKey(preset.Snapshot.ActiveEffectId));
        }
    }

    [Fact]
    public async Task FirstRun_SeedsDefaultPresets_AndPersistsThem()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = false;

        await h.StartAsync();

        Assert.True(CurrentConfig(h).Settings.Presets.Count >= 5);
        CoordinatorHarness.WaitUntil(() => h.Saved.Length > 0, because: "seeded presets must be persisted");
        Assert.True(h.Saved.Last().Presets.Count >= 5);
    }

    [Fact]
    public async Task NonFirstRun_DoesNotSeed()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;

        await h.StartAsync();

        Assert.Empty(CurrentConfig(h).Settings.Presets);
    }

    [Fact]
    public async Task FirstRun_WithExistingPresets_DoesNotReseed()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = false;
        h.InitialSettings.Presets.Add(new Preset { Name = "Mine", Snapshot = new ApplicationSettings() });

        await h.StartAsync();

        var preset = Assert.Single(CurrentConfig(h).Settings.Presets);
        Assert.Equal("Mine", preset.Name);
    }

    [Fact]
    public async Task LoadPreset_WithEmptyMonitorSelection_KeepsCurrentMonitorsAndEnabledState()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = false; // seeds the curated presets
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.TargetMonitorIds = new List<string> { CoordinatorHarness.Display2 };

        await h.StartAsync();
        h.Send("loadPreset", new { name = "Movie Night" });

        var s = h.LastConfig.Settings;
        Assert.Equal("Movie Night", s.ActivePresetName);
        Assert.Equal("edge-glow", s.ActiveEffectId);
        // The look applied, the machine-specific selection survived.
        Assert.Equal(CoordinatorHarness.Display1, s.SourceMonitorId);
        Assert.Equal(new List<string> { CoordinatorHarness.Display2 }, s.TargetMonitorIds);
        Assert.True(s.IsEnabled);
    }

    [Fact]
    public async Task LoadPreset_WithExplicitMonitorSelection_StillAppliesItsOwn()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.Presets.Add(new Preset
        {
            Name = "Pinned",
            Snapshot = new ApplicationSettings
            {
                SourceMonitorId = CoordinatorHarness.Display3,
                TargetMonitorIds = new List<string> { CoordinatorHarness.Display1 },
                ActiveEffectId = "plasma",
            },
        });

        await h.StartAsync();
        h.Send("loadPreset", new { name = "Pinned" });

        var s = h.LastConfig.Settings;
        Assert.Equal(CoordinatorHarness.Display3, s.SourceMonitorId);
        Assert.Equal(new List<string> { CoordinatorHarness.Display1 }, s.TargetMonitorIds);
    }
}

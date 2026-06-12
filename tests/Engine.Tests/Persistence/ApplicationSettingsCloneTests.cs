using System.Text.Json;
using AmbientFx.Models;
using Xunit;

namespace AmbientFx.Engine.Tests.Persistence;

/// <summary>ApplicationSettings.Clone must be a deep copy (used for preset snapshots).</summary>
public sealed class ApplicationSettingsCloneTests
{
    private static ApplicationSettings BuildSettings() => new()
    {
        IsEnabled = true,
        SourceMonitorId = @"\\.\DISPLAY1",
        TargetMonitorIds = new List<string> { @"\\.\DISPLAY2" },
        ActiveEffectId = "plasma",
        EffectByMonitorId = new Dictionary<string, string> { [@"\\.\DISPLAY2"] = "audio-bars" },
        AudioSensitivity = 0.7f,
        GlobalIntensity = 0.6f,
        Smoothing = 0.3f,
        Brightness = 0.4f,
        MaxFps = 120,
        ZonesPerEdge = 16,
        AudioBands = 24,
        Autostart = true,
        EffectParamsById = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["plasma"] = new() { ["speed"] = JsonSerializer.SerializeToElement(0.75) },
        },
        Hotkeys = new Dictionary<string, string> { [HotkeyActions.ToggleEnabled] = "Ctrl+Alt+A" },
        Presets = new List<Preset>
        {
            new() { Name = "Gaming", Snapshot = new ApplicationSettings { MaxFps = 30 } },
        },
        ActivePresetName = "Gaming",
        FirstRunCompleted = true,
    };

    [Fact]
    public void Clone_CopiesEveryValue()
    {
        var original = BuildSettings();
        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.True(clone.IsEnabled);
        Assert.Equal(@"\\.\DISPLAY1", clone.SourceMonitorId);
        Assert.Equal(original.TargetMonitorIds, clone.TargetMonitorIds);
        Assert.Equal("plasma", clone.ActiveEffectId);
        Assert.Equal("audio-bars", clone.EffectByMonitorId[@"\\.\DISPLAY2"]);
        Assert.Equal(0.7f, clone.AudioSensitivity);
        Assert.Equal(0.6f, clone.GlobalIntensity);
        Assert.Equal(0.3f, clone.Smoothing);
        Assert.Equal(0.4f, clone.Brightness);
        Assert.Equal(120, clone.MaxFps);
        Assert.Equal(16, clone.ZonesPerEdge);
        Assert.Equal(24, clone.AudioBands);
        Assert.True(clone.Autostart);
        Assert.Equal(0.75, clone.EffectParamsById["plasma"]["speed"].GetDouble());
        Assert.Equal("Ctrl+Alt+A", clone.Hotkeys[HotkeyActions.ToggleEnabled]);
        var preset = Assert.Single(clone.Presets);
        Assert.Equal("Gaming", preset.Name);
        Assert.Equal(30, preset.Snapshot.MaxFps);
        Assert.Equal("Gaming", clone.ActivePresetName);
        Assert.True(clone.FirstRunCompleted);
    }

    [Fact]
    public void Clone_IsDeep_MutatingCloneDoesNotAffectOriginal()
    {
        var original = BuildSettings();
        var clone = original.Clone();

        clone.TargetMonitorIds.Add(@"\\.\DISPLAY3");
        clone.EffectByMonitorId["extra"] = "edge-glow";
        clone.EffectParamsById["plasma"]["speed"] = JsonSerializer.SerializeToElement(0.1);
        clone.EffectParamsById["new-effect"] = new Dictionary<string, JsonElement>();
        clone.Hotkeys[HotkeyActions.ToggleEnabled] = "Ctrl+Z";
        clone.Presets.Add(new Preset { Name = "Movie" });
        clone.Presets[0].Name = "Renamed";
        clone.Presets[0].Snapshot.MaxFps = 999;
        clone.SourceMonitorId = "changed";

        Assert.Single(original.TargetMonitorIds);
        Assert.Equal(@"\\.\DISPLAY2", original.TargetMonitorIds[0]);
        Assert.Single(original.EffectByMonitorId);
        Assert.Equal(0.75, original.EffectParamsById["plasma"]["speed"].GetDouble());
        Assert.Single(original.EffectParamsById);
        Assert.Equal("Ctrl+Alt+A", original.Hotkeys[HotkeyActions.ToggleEnabled]);
        Assert.Single(original.Presets);
        Assert.Equal("Gaming", original.Presets[0].Name);
        Assert.Equal(30, original.Presets[0].Snapshot.MaxFps);
        Assert.Equal(@"\\.\DISPLAY1", original.SourceMonitorId);
    }

    [Fact]
    public void Clone_IsDeep_MutatingOriginalDoesNotAffectClone()
    {
        var original = BuildSettings();
        var clone = original.Clone();

        original.TargetMonitorIds.Clear();
        original.Presets.Clear();

        Assert.Single(clone.TargetMonitorIds);
        Assert.Single(clone.Presets);
    }
}

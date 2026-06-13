using System.IO;
using AmbientFx.Services;
using Xunit;

namespace AmbientFx.Engine.Tests.Persistence;

/// <summary>
/// Regression tests for the launch-bricking review finding: hand-edited or corrupt numeric
/// values in settings.json must never escape <see cref="SettingsService"/> — AudioBands &lt; 1
/// used to flow into SpectrumAnalyzer's ctor guard and throw during StartAsync, killing the
/// app on every launch until the file was deleted.
/// </summary>
public sealed class SettingsNormalizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AmbientFxTests", Guid.NewGuid().ToString("N"));

    private SettingsService NewService() => new(_dir);

    private async Task<AmbientFx.Models.ApplicationSettings> LoadFromJson(string json)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), json);
        return await NewService().LoadAsync();
    }

    [Fact]
    public async Task AudioBands_below_one_is_repaired_to_a_safe_value()
    {
        var s = await LoadFromJson("""{"audioBands":0,"zonesPerEdge":8}""");
        Assert.InRange(s.AudioBands, 1, 64);
    }

    [Fact]
    public async Task Absurd_numeric_ranges_are_clamped()
    {
        var s = await LoadFromJson(
            """{"audioBands":-3,"zonesPerEdge":500,"maxFps":-5,"audioSensitivity":42.0,"brightness":-1.0}""");
        Assert.InRange(s.AudioBands, 1, 64);
        Assert.InRange(s.ZonesPerEdge, 1, 64);
        Assert.InRange(s.MaxFps, 1, 240);
        Assert.InRange(s.AudioSensitivity, 0f, 1f);
        Assert.InRange(s.Brightness, 0f, 1f);
    }

    [Fact]
    public async Task NaN_floats_fall_back_to_defaults_instead_of_poisoning_smoothers()
    {
        var s = await LoadFromJson("""{"smoothing":"NaN","globalIntensity":"Infinity"}""");
        Assert.True(float.IsFinite(s.Smoothing));
        Assert.True(float.IsFinite(s.GlobalIntensity));
        Assert.InRange(s.Smoothing, 0f, 1f);
        Assert.InRange(s.GlobalIntensity, 0f, 1f);
    }

    [Fact]
    public async Task Null_and_nameless_presets_are_dropped_and_snapshots_are_normalized()
    {
        var s = await LoadFromJson(
            """
            {"presets":[
              null,
              {"name":"","snapshot":{"audioBands":12}},
              {"name":"NoSnapshot","snapshot":null},
              {"name":"Bad","snapshot":{"audioBands":0,"smoothing":"NaN"}}
            ]}
            """);
        var preset = Assert.Single(s.Presets);
        Assert.Equal("Bad", preset.Name);
        Assert.InRange(preset.Snapshot.AudioBands, 1, 64);
        Assert.True(float.IsFinite(preset.Snapshot.Smoothing));
        Assert.Empty(preset.Snapshot.Presets);
    }

    [Fact]
    public async Task PeripheralBrightness_is_clamped_and_nan_repaired()
    {
        var s = await LoadFromJson("""{"peripheralBrightness":7.5}""");
        Assert.Equal(1f, s.PeripheralBrightness);

        s = await LoadFromJson("""{"peripheralBrightness":"NaN"}""");
        Assert.Equal(1f, s.PeripheralBrightness);

        s = await LoadFromJson("""{"peripheralBrightness":-2.0}""");
        Assert.Equal(0f, s.PeripheralBrightness);
    }

    [Fact]
    public async Task Missing_ambient_device_keys_default_cleanly()
    {
        // Forward/back compat (Story 8.1 AC7): a pre-Epic-8 settings.json has neither key.
        var s = await LoadFromJson("""{"isEnabled":true}""");
        Assert.False(s.AmbientDevicesEnabled);
        Assert.Equal(1f, s.PeripheralBrightness);
        Assert.Empty(s.DevicePlacements);
        Assert.Equal(new[] { "corsair" }, s.RgbProviders); // pre-8.3 file → Corsair default
        Assert.False(s.AudioReactiveDevices);
        Assert.Equal(0.5f, s.AudioReactiveDepth);
    }

    [Fact]
    public async Task Rgb_provider_list_is_cleaned_but_an_explicit_empty_list_is_respected()
    {
        var s = await LoadFromJson("""{"rgbProviders":["corsair","corsair"," ","razer",null]}""");
        Assert.Equal(new[] { "corsair", "razer" }, s.RgbProviders);

        s = await LoadFromJson("""{"rgbProviders":[]}""");
        Assert.Empty(s.RgbProviders); // the user disabled every vendor on purpose

        s = await LoadFromJson("""{"rgbProviders":null,"audioReactiveDepth":42.0}""");
        Assert.Equal(new[] { "corsair" }, s.RgbProviders); // explicit null repaired to default
        Assert.Equal(1f, s.AudioReactiveDepth); // clamped
    }

    [Fact]
    public async Task Device_placements_are_repaired_not_rejected()
    {
        var s = await LoadFromJson(
            """
            {"devicePlacements":{
              "corsair:ok":{"anchor":"left","flip":true,"brightness":0.5,"enabled":false},
              "corsair:badAnchor":{"anchor":"sideways","brightness":42.0},
              "corsair:nullEntry":null,
              " ":{"anchor":"left"}
            }}
            """);
        Assert.Equal(2, s.DevicePlacements.Count); // null entry and blank key dropped

        var ok = s.DevicePlacements["corsair:ok"];
        Assert.Equal("left", ok.Anchor);
        Assert.True(ok.Flip);
        Assert.Equal(0.5f, ok.Brightness);
        Assert.False(ok.Enabled);

        var repaired = s.DevicePlacements["corsair:badAnchor"];
        Assert.Equal("auto", repaired.Anchor); // invalid anchor falls back to Auto
        Assert.Equal(1f, repaired.Brightness); // clamped
    }

    [Fact]
    public async Task Null_entries_in_target_monitor_ids_are_dropped()
    {
        var s = await LoadFromJson("""{"targetMonitorIds":[null,"","\\\\?\\DISPLAY#X"]}""");
        var id = Assert.Single(s.TargetMonitorIds);
        Assert.Equal(@"\\?\DISPLAY#X", id);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

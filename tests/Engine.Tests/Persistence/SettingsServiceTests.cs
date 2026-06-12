using System.IO;
using System.Text.Json;
using AmbientFx.Bridge;
using AmbientFx.Models;
using AmbientFx.Services;
using Xunit;

namespace AmbientFx.Engine.Tests.Persistence;

/// <summary>
/// FR9 / AC5: settings (including source/target monitor selection — the MVP bug) must
/// round-trip through %AppData% JSON persistence. Tests run against a unique temp dir
/// via the internal SettingsService(string) constructor.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AmbientFxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SettingsService CreateService() => new(_dir);

    private string SettingsPath => Path.Combine(_dir, "settings.json");
    private string BackupPath => Path.Combine(_dir, "settings.backup.json");

    /// <summary>Every field non-default so the round-trip test proves each one persists.</summary>
    private static ApplicationSettings BuildFullSettings()
    {
        var snapshot = new ApplicationSettings
        {
            IsEnabled = true,
            SourceMonitorId = @"\\.\DISPLAY1",
            TargetMonitorIds = new List<string> { @"\\.\DISPLAY2" },
            ActiveEffectId = "audio-bars",
            GlobalIntensity = 0.25f,
            MaxFps = 30,
        };

        return new ApplicationSettings
        {
            IsEnabled = true,
            SourceMonitorId = @"\\.\DISPLAY1",
            TargetMonitorIds = new List<string> { @"\\.\DISPLAY2", @"\\.\DISPLAY3" },
            ActiveEffectId = "plasma",
            EffectByMonitorId = new Dictionary<string, string> { [@"\\.\DISPLAY3"] = "particle-field" },
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
                ["plasma"] = new()
                {
                    ["speed"] = JsonSerializer.SerializeToElement(0.75),
                    ["palette"] = JsonSerializer.SerializeToElement("rainbow"),
                    ["mirrored"] = JsonSerializer.SerializeToElement(true),
                },
            },
            Hotkeys = new Dictionary<string, string>
            {
                [HotkeyActions.ToggleEnabled] = "Ctrl+Alt+A",
                [HotkeyActions.OpenSettings] = "Ctrl+Alt+S",
                [HotkeyActions.NextPreset] = string.Empty,
            },
            Presets = new List<Preset> { new() { Name = "Gaming", Snapshot = snapshot } },
            ActivePresetName = "Gaming",
            FirstRunCompleted = true,
        };
    }

    // ---------------------------------------------------------------- AC5 round-trip

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryField()
    {
        var service = CreateService();
        await service.SaveAsync(BuildFullSettings());

        // Fresh instance: nothing cached, must come from disk.
        var loaded = await CreateService().LoadAsync();

        Assert.True(loaded.IsEnabled);
        // THE MVP BUG (AC5): monitor selection must actually persist.
        Assert.Equal(@"\\.\DISPLAY1", loaded.SourceMonitorId);
        Assert.Equal(new List<string> { @"\\.\DISPLAY2", @"\\.\DISPLAY3" }, loaded.TargetMonitorIds);

        Assert.Equal("plasma", loaded.ActiveEffectId);
        Assert.Equal("particle-field", loaded.EffectByMonitorId[@"\\.\DISPLAY3"]);
        Assert.Equal(0.7f, loaded.AudioSensitivity);
        Assert.Equal(0.6f, loaded.GlobalIntensity);
        Assert.Equal(0.3f, loaded.Smoothing);
        Assert.Equal(0.4f, loaded.Brightness);
        Assert.Equal(120, loaded.MaxFps);
        Assert.Equal(16, loaded.ZonesPerEdge);
        Assert.Equal(24, loaded.AudioBands);
        Assert.True(loaded.Autostart);

        // Opaque effect-param bag round-trips number/string/bool JsonElements.
        var plasma = loaded.EffectParamsById["plasma"];
        Assert.Equal(JsonValueKind.Number, plasma["speed"].ValueKind);
        Assert.Equal(0.75, plasma["speed"].GetDouble());
        Assert.Equal(JsonValueKind.String, plasma["palette"].ValueKind);
        Assert.Equal("rainbow", plasma["palette"].GetString());
        Assert.Equal(JsonValueKind.True, plasma["mirrored"].ValueKind);
        Assert.True(plasma["mirrored"].GetBoolean());

        Assert.Equal("Ctrl+Alt+A", loaded.Hotkeys[HotkeyActions.ToggleEnabled]);
        Assert.Equal("Ctrl+Alt+S", loaded.Hotkeys[HotkeyActions.OpenSettings]);
        Assert.Equal(string.Empty, loaded.Hotkeys[HotkeyActions.NextPreset]);

        var preset = Assert.Single(loaded.Presets);
        Assert.Equal("Gaming", preset.Name);
        Assert.True(preset.Snapshot.IsEnabled);
        Assert.Equal(@"\\.\DISPLAY1", preset.Snapshot.SourceMonitorId);
        Assert.Equal(new List<string> { @"\\.\DISPLAY2" }, preset.Snapshot.TargetMonitorIds);
        Assert.Equal("audio-bars", preset.Snapshot.ActiveEffectId);
        Assert.Equal(0.25f, preset.Snapshot.GlobalIntensity);
        Assert.Equal(30, preset.Snapshot.MaxFps);

        Assert.Equal("Gaming", loaded.ActivePresetName);
        Assert.True(loaded.FirstRunCompleted);
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public async Task Load_NoFile_ReturnsDefaults()
    {
        var loaded = await CreateService().LoadAsync();

        Assert.False(loaded.IsEnabled);
        Assert.Equal(string.Empty, loaded.SourceMonitorId);
        Assert.Empty(loaded.TargetMonitorIds);
        Assert.Equal("edge-glow", loaded.ActiveEffectId);
        Assert.Equal(60, loaded.MaxFps);
        Assert.Equal(8, loaded.ZonesPerEdge);
        Assert.Equal(12, loaded.AudioBands);
        Assert.False(loaded.FirstRunCompleted);
    }

    [Fact]
    public void GetDefaults_MatchesSpecDefaults()
    {
        var d = CreateService().GetDefaults();

        Assert.False(d.IsEnabled);
        Assert.Equal(string.Empty, d.SourceMonitorId);
        Assert.Empty(d.TargetMonitorIds);
        Assert.Equal("edge-glow", d.ActiveEffectId);
        Assert.Equal(0.5f, d.AudioSensitivity);
        Assert.Equal(1.0f, d.GlobalIntensity);
        Assert.Equal(0.5f, d.Smoothing);
        Assert.Equal(0.85f, d.Brightness);
        Assert.Equal(60, d.MaxFps);
        Assert.Equal(8, d.ZonesPerEdge);
        Assert.Equal(12, d.AudioBands);
        Assert.False(d.Autostart);
        Assert.Empty(d.EffectParamsById);
        Assert.Empty(d.Presets);
        Assert.Equal(string.Empty, d.ActivePresetName);
        Assert.False(d.FirstRunCompleted);

        // All known hotkey actions present (stable keys for the web UI) but unbound.
        foreach (var action in HotkeyActions.All)
        {
            Assert.Equal(string.Empty, d.Hotkeys[action]);
        }
    }

    // ---------------------------------------------------------------- corruption fallback

    [Fact]
    public async Task Load_CorruptMainFile_RecoversFromBackup()
    {
        File.WriteAllText(BackupPath, BridgeJson.Serialize(BuildFullSettings()));
        File.WriteAllBytes(SettingsPath, new byte[] { 0x00, 0xFF, 0x7B, 0x01, 0x02 }); // garbage

        var loaded = await CreateService().LoadAsync();

        Assert.Equal(@"\\.\DISPLAY1", loaded.SourceMonitorId);
        Assert.Equal(new List<string> { @"\\.\DISPLAY2", @"\\.\DISPLAY3" }, loaded.TargetMonitorIds);
        Assert.Equal("plasma", loaded.ActiveEffectId);
    }

    [Fact]
    public async Task Load_CorruptMainAndBackup_ReturnsDefaults_WithoutThrowing()
    {
        File.WriteAllText(SettingsPath, "{{{ not json !!!");
        File.WriteAllBytes(BackupPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var loaded = await CreateService().LoadAsync();

        Assert.Equal("edge-glow", loaded.ActiveEffectId);
        Assert.Equal(60, loaded.MaxFps);
        Assert.False(loaded.IsEnabled);
        Assert.Empty(loaded.TargetMonitorIds);
    }

    [Fact]
    public async Task Load_UnknownExtraJsonProperties_AreTolerated()
    {
        File.WriteAllText(SettingsPath, """
            {
              "sourceMonitorId": "\\\\.\\DISPLAY9",
              "maxFps": 144,
              "futureFeature": { "nested": [1, 2, 3], "deep": { "x": true } },
              "someUnknownString": "hello"
            }
            """);

        var loaded = await CreateService().LoadAsync();

        Assert.Equal(@"\\.\DISPLAY9", loaded.SourceMonitorId);
        Assert.Equal(144, loaded.MaxFps);
        Assert.Equal("edge-glow", loaded.ActiveEffectId); // untouched default
    }

    [Fact]
    public async Task Load_ExplicitJsonNulls_AreNormalizedToEmpty()
    {
        File.WriteAllText(SettingsPath, """
            {
              "sourceMonitorId": null,
              "targetMonitorIds": null,
              "activeEffectId": null,
              "effectByMonitorId": null,
              "effectParamsById": null,
              "hotkeys": null,
              "presets": null,
              "activePresetName": null
            }
            """);

        var loaded = await CreateService().LoadAsync();

        Assert.NotNull(loaded.SourceMonitorId);
        Assert.NotNull(loaded.TargetMonitorIds);
        Assert.Equal("edge-glow", loaded.ActiveEffectId);
        Assert.NotNull(loaded.EffectByMonitorId);
        Assert.NotNull(loaded.EffectParamsById);
        Assert.NotNull(loaded.Hotkeys);
        Assert.NotNull(loaded.Presets);
        Assert.Equal(string.Empty, loaded.ActivePresetName);
    }

    // ---------------------------------------------------------------- save mechanics

    [Fact]
    public async Task Save_PreservesPreviousFileAsBackup()
    {
        var service = CreateService();

        var first = service.GetDefaults();
        first.SourceMonitorId = @"\\.\DISPLAY1";
        await service.SaveAsync(first);

        var second = service.GetDefaults();
        second.SourceMonitorId = @"\\.\DISPLAY2";
        await service.SaveAsync(second);

        Assert.True(File.Exists(BackupPath));
        var backup = BridgeJson.Deserialize<ApplicationSettings>(File.ReadAllText(BackupPath));
        Assert.Equal(@"\\.\DISPLAY1", backup!.SourceMonitorId);

        var main = BridgeJson.Deserialize<ApplicationSettings>(File.ReadAllText(SettingsPath));
        Assert.Equal(@"\\.\DISPLAY2", main!.SourceMonitorId);
    }

    [Fact]
    public async Task Save_WritesValidCamelCaseJson()
    {
        await CreateService().SaveAsync(BuildFullSettings());

        string raw = File.ReadAllText(SettingsPath);

        // camelCase on disk — the same shape the web layer sees over the bridge.
        Assert.Contains("\"sourceMonitorId\"", raw);
        Assert.Contains("\"targetMonitorIds\"", raw);
        Assert.DoesNotContain("\"SourceMonitorId\"", raw);

        using var doc = JsonDocument.Parse(raw); // must be valid JSON
        Assert.Equal(
            @"\\.\DISPLAY1",
            doc.RootElement.GetProperty("sourceMonitorId").GetString());
    }

    [Fact]
    public async Task Save_NullSettings_IsIgnored_NoThrow_NoFile()
    {
        await CreateService().SaveAsync(null!);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task ConcurrentSaves_LeaveAValidFile()
    {
        var service = CreateService();
        var tasks = Enumerable.Range(0, 8).Select(i =>
        {
            var s = service.GetDefaults();
            s.MaxFps = 100 + i;
            return service.SaveAsync(s);
        });
        await Task.WhenAll(tasks);

        var loaded = await CreateService().LoadAsync();
        Assert.InRange(loaded.MaxFps, 100, 107); // one writer won; the file is intact
    }
}

using AmbientFx.Bridge;
using AmbientFx.Capture;
using AmbientFx.Models;
using AmbientFx.Services;
using Moq;
using Xunit;

namespace AmbientFx.Engine.Tests.Coordinator;

public sealed class EngineCoordinatorTests
{
    private const string D1 = CoordinatorHarness.Display1;
    private const string D2 = CoordinatorHarness.Display2;
    private const string D3 = CoordinatorHarness.Display3;

    // ------------------------------------------------------------------
    // Startup
    // ------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_Minimized_InitializesEverything_ButDoesNotShowControlWindow()
    {
        var h = new CoordinatorHarness();

        await h.StartAsync(minimized: true);

        h.Settings.Verify(s => s.LoadAsync(), Times.Once);
        h.WindowManager.Verify(w => w.InitializeAsync(), Times.Once);
        h.Tray.Verify(t => t.Initialize(), Times.Once);
        h.Tray.Verify(t => t.Update(It.IsAny<bool>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>()),
            Times.AtLeastOnce);
        h.Hotkeys.Verify(k => k.Apply(It.IsAny<IReadOnlyDictionary<string, string>>()), Times.Once);
        h.MonitorDetection.Verify(m => m.StartMonitoring(), Times.Once);
        h.WindowManager.Verify(w => w.ShowControlWindowAsync(), Times.Never);
    }

    [Fact]
    public async Task StartAsync_NotMinimized_ShowsControlWindow()
    {
        var h = new CoordinatorHarness();

        await h.StartAsync(minimized: false);

        h.WindowManager.Verify(w => w.ShowControlWindowAsync(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_Disabled_DoesNotStartCapture_AndSyncsZeroEffectWindows()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = false;
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2, D3 };

        await h.StartAsync();

        h.Capture.Verify(c => c.Start(It.IsAny<MonitorInfo>()), Times.Never);
        h.Audio.Verify(a => a.Start(), Times.Never);
        h.Processing.Verify(p => p.Start(), Times.Never);
        Assert.NotEmpty(h.Syncs);
        Assert.Empty(h.Syncs.Last());
    }

    [Fact]
    public async Task StartAsync_EnabledWithValidSource_StartsPipeline_AndBuildsSpecsWithRelations()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2, D3 };

        await h.StartAsync();

        h.Capture.Verify(c => c.Start(It.Is<MonitorInfo>(m => m.Id == D1)), Times.Once);
        h.Audio.Verify(a => a.Start(), Times.Once);
        h.Processing.Verify(p => p.Start(), Times.Once);

        var specs = h.Syncs.Last();
        Assert.Equal(2, specs.Count);

        var left = Assert.Single(specs, s => s.Monitor.Id == D2);
        Assert.Equal("left", left.Config.Relation);
        Assert.Equal(D2, left.Config.MonitorId);
        Assert.Equal(D1, left.Config.Source?.Id);

        var right = Assert.Single(specs, s => s.Monitor.Id == D3);
        Assert.Equal("right", right.Config.Relation);
    }

    [Fact]
    public async Task StartAsync_PerMonitorOverride_ResolvesIntoSpecEffectIds()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2, D3 };
        h.InitialSettings.ActiveEffectId = "edge-glow";
        h.InitialSettings.EffectByMonitorId[D3] = "plasma";

        await h.StartAsync();

        var specs = h.Syncs.Last();
        Assert.Equal("edge-glow", Assert.Single(specs, s => s.Monitor.Id == D2).Config.EffectId);
        Assert.Equal("plasma", Assert.Single(specs, s => s.Monitor.Id == D3).Config.EffectId);
    }

    [Fact]
    public async Task StartAsync_TargetsNeverIncludeTheSourceMonitor()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D1, D2 };

        await h.StartAsync();

        var specs = h.Syncs.Last();
        var spec = Assert.Single(specs);
        Assert.Equal(D2, spec.Monitor.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\.\DISPLAY9")]
    public async Task StartAsync_EnabledWithUnusableSource_ForcesDisabled_Toasts_AndNeverStartsCapture(string sourceId)
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = sourceId;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2 };

        await h.StartAsync();

        Assert.False(h.InitialSettings.IsEnabled);
        h.Capture.Verify(c => c.Start(It.IsAny<MonitorInfo>()), Times.Never);
        Assert.Empty(h.Syncs.Last());
        Assert.False(h.LastConfig.Settings.IsEnabled);

        var status = h.ControlPosts.Where(p => p.Type == MessageTypes.Status)
            .Select(p => (StatusPayload)p.Payload);
        Assert.Contains(status, s => s.Level is "warn" or "error");
    }

    // ------------------------------------------------------------------
    // setEnabled round-trip (FR13 / AC4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetEnabled_RoundTrip_StartsAndStopsPipeline_SyncsWindows_PushesConfig_AndEventuallySaves()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2 };
        await h.StartAsync();

        h.Send(CommandTypes.SetEnabled, new { enabled = true });

        h.Capture.Verify(c => c.Start(It.Is<MonitorInfo>(m => m.Id == D1)), Times.Once);
        h.Audio.Verify(a => a.Start(), Times.Once);
        h.Processing.Verify(p => p.Start(), Times.Once);
        Assert.Single(h.Syncs.Last());
        Assert.True(h.LastConfig.Settings.IsEnabled);

        var stopsBefore = h.CaptureStopCount;
        h.Send(CommandTypes.SetEnabled, new { enabled = false });

        Assert.True(h.CaptureStopCount > stopsBefore, "capture.Stop should be called on disable");
        Assert.Empty(h.Syncs.Last()); // AC4: target monitors released
        Assert.False(h.LastConfig.Settings.IsEnabled);

        // Saves are debounced by 600 ms — poll for the persisted disabled state (AC5).
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => !s.IsEnabled),
            because: "debounced SaveAsync after setEnabled");
    }

    // ------------------------------------------------------------------
    // setGlobal
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetGlobal_ClampsValues_AndForwardsProcessingOptions()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SetGlobal, new
        {
            intensity = 2.0f,
            smoothing = 0.25f,
            brightness = -1.0f,
            audioSensitivity = 0.9f,
            maxFps = 45,
        });

        var settings = h.LastConfig.Settings;
        Assert.Equal(1.0f, settings.GlobalIntensity); // clamped to 1.0
        Assert.Equal(0.25f, settings.Smoothing);
        Assert.Equal(0.0f, settings.Brightness);      // clamped to 0
        Assert.Equal(0.9f, settings.AudioSensitivity);
        Assert.Equal(30, settings.MaxFps);            // 45 snaps to nearest allowed, ties -> lower

        var options = h.LastProcessingOptions;
        Assert.NotNull(options);
        Assert.Equal(0.25f, options!.Smoothing);
        Assert.Equal(0.9f, options.AudioSensitivity);
        Assert.Equal(30, options.MaxFps);
    }

    [Theory]
    [InlineData(45, 30)]   // tie between 30 and 60 -> lower wins
    [InlineData(46, 60)]
    [InlineData(90, 60)]   // tie between 60 and 120 -> lower wins
    [InlineData(200, 120)]
    [InlineData(1, 30)]
    [InlineData(60, 60)]
    public async Task SetGlobal_SnapsMaxFpsToNearestAllowedValue(int requested, int expected)
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SetGlobal, new { maxFps = requested });

        Assert.Equal(expected, h.LastConfig.Settings.MaxFps);
        Assert.Equal(expected, h.LastProcessingOptions!.MaxFps);
    }

    // ------------------------------------------------------------------
    // setEffect
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetEffect_Global_SetsActiveEffect_ClearsOverrides_AndRepostsWindowConfigs()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2, D3 };
        h.InitialSettings.ActiveEffectId = "edge-glow";
        h.InitialSettings.EffectByMonitorId[D2] = "aurora";
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.SetEffect, new { effectId = "plasma" });

        var settings = h.LastConfig.Settings;
        Assert.Equal("plasma", settings.ActiveEffectId);
        Assert.Empty(settings.EffectByMonitorId);

        foreach (var id in new[] { D2, D3 })
        {
            var post = Assert.Single(h.EffectPosts,
                p => p.MonitorId == id && p.Type == MessageTypes.WindowConfig);
            Assert.Equal("plasma", ((WindowConfigPayload)post.Payload).EffectId);
        }
    }

    [Fact]
    public async Task SetEffect_PerMonitor_SetsOverride_AndKeepsActiveEffect()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2, D3 };
        h.InitialSettings.ActiveEffectId = "edge-glow";
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.SetEffect, new { monitorId = D2, effectId = "aurora" });

        var settings = h.LastConfig.Settings;
        Assert.Equal("edge-glow", settings.ActiveEffectId); // unchanged
        Assert.Equal("aurora", settings.EffectByMonitorId[D2]);

        var post = Assert.Single(h.EffectPosts,
            p => p.MonitorId == D2 && p.Type == MessageTypes.WindowConfig);
        var payload = (WindowConfigPayload)post.Payload;
        Assert.Equal("aurora", payload.EffectId);
        Assert.Equal("left", payload.Relation);
    }

    // ------------------------------------------------------------------
    // Presets
    // ------------------------------------------------------------------

    [Fact]
    public async Task SavePreset_StoresSnapshotWithoutNestedPresets_AndSetsActiveName()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.Presets.Add(new Preset { Name = "Old", Snapshot = new ApplicationSettings() });
        h.InitialSettings.ActiveEffectId = "plasma";
        await h.StartAsync();

        h.Send(CommandTypes.SavePreset, new { name = "Gaming" });

        Assert.Equal(2, h.InitialSettings.Presets.Count);
        var preset = h.InitialSettings.Presets.Single(p => p.Name == "Gaming");
        Assert.Empty(preset.Snapshot.Presets);                      // no recursion
        Assert.Equal(string.Empty, preset.Snapshot.ActivePresetName);
        Assert.Equal("plasma", preset.Snapshot.ActiveEffectId);     // snapshot captures current state
        Assert.Equal("Gaming", h.InitialSettings.ActivePresetName);

        Assert.Contains(h.Saved, s => s.ActivePresetName == "Gaming"); // persisted immediately
        h.Tray.Verify(t => t.Update(It.IsAny<bool>(),
            It.Is<IReadOnlyList<string>>(names => names.Contains("Gaming")), "Gaming"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SavePreset_BlankName_IsIgnored()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SavePreset, new { name = "   " });

        Assert.Empty(h.InitialSettings.Presets);
        Assert.Equal(string.Empty, h.InitialSettings.ActivePresetName);
    }

    [Fact]
    public async Task LoadPreset_RestoresSnapshot_ButPreservesPresetListAndFirstRunCompleted()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.InitialSettings.ActiveEffectId = "edge-glow";
        h.InitialSettings.GlobalIntensity = 1.0f;
        h.InitialSettings.Presets.Add(new Preset
        {
            Name = "Night",
            Snapshot = new ApplicationSettings
            {
                ActiveEffectId = "plasma",
                GlobalIntensity = 0.25f,
                SourceMonitorId = D1,
                TargetMonitorIds = new List<string> { D2 },
                IsEnabled = false,
                FirstRunCompleted = false, // must NOT leak into live settings
            },
        });
        await h.StartAsync();

        h.Send(CommandTypes.LoadPreset, new { name = "Night" });

        var settings = h.LastConfig.Settings;
        Assert.Equal("plasma", settings.ActiveEffectId);
        Assert.Equal(0.25f, settings.GlobalIntensity);
        Assert.Equal("Night", settings.ActivePresetName);
        Assert.True(settings.FirstRunCompleted);               // preserved
        var kept = Assert.Single(settings.Presets);            // preset list preserved
        Assert.Equal("Night", kept.Name);

        var toasts = h.ControlPosts.Where(p => p.Type == MessageTypes.Status)
            .Select(p => (StatusPayload)p.Payload);
        Assert.Contains(toasts, t => t.Level == "info" && t.Message.Contains("Night"));
    }

    [Fact]
    public async Task LoadPreset_UnknownName_ToastsWarning_AndChangesNothing()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.ActiveEffectId = "edge-glow";
        await h.StartAsync();

        h.Send(CommandTypes.LoadPreset, new { name = "Nope" });

        Assert.Equal("edge-glow", h.InitialSettings.ActiveEffectId);
        var toasts = h.ControlPosts.Where(p => p.Type == MessageTypes.Status)
            .Select(p => (StatusPayload)p.Payload);
        Assert.Contains(toasts, t => t.Level == "warn" && t.Message.Contains("Nope"));
    }

    [Fact]
    public async Task DeletePreset_RemovesIt_AndClearsActiveNameWhenItMatched()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.Presets.Add(new Preset { Name = "Gaming", Snapshot = new ApplicationSettings() });
        h.InitialSettings.ActivePresetName = "Gaming";
        await h.StartAsync();

        h.Send(CommandTypes.DeletePreset, new { name = "Gaming" });

        Assert.Empty(h.InitialSettings.Presets);
        Assert.Equal(string.Empty, h.InitialSettings.ActivePresetName);
        Assert.Equal(string.Empty, h.LastConfig.Settings.ActivePresetName);
    }

    // ------------------------------------------------------------------
    // requestState
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestState_FromControl_PostsConfigAndMonitorsToControl()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.RequestState, sourceWindow: "control");

        var config = Assert.Single(h.ControlPosts, p => p.Type == MessageTypes.Config);
        Assert.IsType<ConfigPayload>(config.Payload);

        var monitors = Assert.Single(h.ControlPosts, p => p.Type == MessageTypes.Monitors);
        Assert.Equal(3, ((MonitorsPayload)monitors.Payload).Monitors.Count);
    }

    [Fact]
    public async Task RequestState_FromEffectWindow_PostsConfigAndCorrectWindowConfig()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2 };
        h.InitialSettings.ActiveEffectId = "edge-glow";
        h.InitialSettings.EffectByMonitorId[D2] = "aurora";
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.RequestState, sourceWindow: D2);

        Assert.Single(h.EffectPosts, p => p.MonitorId == D2 && p.Type == MessageTypes.Config);

        var wc = Assert.Single(h.EffectPosts, p => p.MonitorId == D2 && p.Type == MessageTypes.WindowConfig);
        var payload = (WindowConfigPayload)wc.Payload;
        Assert.Equal(D2, payload.MonitorId);
        Assert.Equal("aurora", payload.EffectId);   // per-monitor override wins
        Assert.Equal("left", payload.Relation);     // DISPLAY2 sits left of DISPLAY1
        Assert.Equal(D1, payload.Source?.Id);
    }

    // ------------------------------------------------------------------
    // windowCommand / completeOnboarding
    // ------------------------------------------------------------------

    [Fact]
    public async Task WindowCommand_Minimize_ForwardsToWindowManager()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.WindowCommand, new { action = "minimize" });

        h.WindowManager.Verify(w => w.HandleControlWindowCommand("minimize"), Times.Once);
    }

    [Fact]
    public async Task CompleteOnboarding_PushesConfigWithFirstRunFalse_AndSaves()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = false;
        await h.StartAsync();

        h.Send(CommandTypes.CompleteOnboarding);

        Assert.False(h.LastConfig.FirstRun);
        Assert.True(h.LastConfig.Settings.FirstRunCompleted);
        Assert.Contains(h.Saved, s => s.FirstRunCompleted);
    }

    // ------------------------------------------------------------------
    // Pipeline errors (AC7) — needs a live dispatcher for the marshal hop
    // ------------------------------------------------------------------

    [Fact]
    public async Task CaptureError_WhileEnabled_ForcesDisabled_StopsCapture_AndToastsError()
    {
        UiApp.EnsureRunning(); // OnPipelineError marshals through Application.Current.Dispatcher

        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2 };
        await h.StartAsync();
        Assert.True(h.InitialSettings.IsEnabled);

        h.Capture.Raise(c => c.Error += null,
            new PipelineErrorEventArgs { Source = "capture", Message = "device lost" });

        CoordinatorHarness.WaitUntil(() => !h.InitialSettings.IsEnabled,
            because: "capture error must force IsEnabled false (AC7)");
        CoordinatorHarness.WaitUntil(() => h.CaptureStopCount > 0,
            because: "capture must be stopped after a capture error");
        CoordinatorHarness.WaitUntil(
            () => h.ControlPosts.Any(p => p.Type == MessageTypes.Status
                                          && ((StatusPayload)p.Payload).Level == "error"),
            because: "an error-level toast must be posted (AC7)");
        CoordinatorHarness.WaitUntil(
            () => h.AllPosts.Any(p => p.Type == MessageTypes.Config
                                      && !((ConfigPayload)p.Payload).Settings.IsEnabled),
            because: "disabled state must be pushed to the UI");
        Assert.Empty(h.Syncs.Last()); // effect windows released
    }

    // ------------------------------------------------------------------
    // Tray + hotkeys
    // ------------------------------------------------------------------

    [Fact]
    public async Task TrayToggle_TogglesEnabledStateBothWays()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2 };
        await h.StartAsync();

        h.Tray.Raise(t => t.ToggleEnabledRequested += null, EventArgs.Empty);
        h.Capture.Verify(c => c.Start(It.Is<MonitorInfo>(m => m.Id == D1)), Times.Once);
        Assert.True(h.LastConfig.Settings.IsEnabled);

        var stopsBefore = h.CaptureStopCount;
        h.Tray.Raise(t => t.ToggleEnabledRequested += null, EventArgs.Empty);
        Assert.True(h.CaptureStopCount > stopsBefore);
        Assert.False(h.LastConfig.Settings.IsEnabled);
    }

    [Fact]
    public async Task Hotkey_ToggleEnabled_TogglesState()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.SourceMonitorId = D1;
        h.InitialSettings.TargetMonitorIds = new List<string> { D2 };
        await h.StartAsync();

        h.Hotkeys.Raise(k => k.HotkeyPressed += null, h.Hotkeys.Object, HotkeyActions.ToggleEnabled);

        Assert.True(h.LastConfig.Settings.IsEnabled);
        h.Capture.Verify(c => c.Start(It.IsAny<MonitorInfo>()), Times.Once);
    }

    [Fact]
    public async Task Hotkey_NextPreset_CyclesInOrderAndWraps()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.Presets.Add(new Preset { Name = "A", Snapshot = new ApplicationSettings() });
        h.InitialSettings.Presets.Add(new Preset { Name = "B", Snapshot = new ApplicationSettings() });
        h.InitialSettings.ActivePresetName = "A";
        await h.StartAsync();

        h.Hotkeys.Raise(k => k.HotkeyPressed += null, h.Hotkeys.Object, HotkeyActions.NextPreset);
        Assert.Equal("B", h.LastConfig.Settings.ActivePresetName);

        h.Hotkeys.Raise(k => k.HotkeyPressed += null, h.Hotkeys.Object, HotkeyActions.NextPreset);
        Assert.Equal("A", h.LastConfig.Settings.ActivePresetName); // wrapped B -> A
    }

    [Fact]
    public async Task Hotkey_NextPreset_WithNoPresets_IsANoOp()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        var postsBefore = h.AllPosts.Length;

        h.Hotkeys.Raise(k => k.HotkeyPressed += null, h.Hotkeys.Object, HotkeyActions.NextPreset);

        Assert.Equal(postsBefore, h.AllPosts.Length); // nothing pushed, nothing crashed
        var toasts = h.ControlPosts.Where(p => p.Type == MessageTypes.Status)
            .Select(p => (StatusPayload)p.Payload);
        Assert.DoesNotContain(toasts, t => t.Message.Contains("Preset"));
    }
}

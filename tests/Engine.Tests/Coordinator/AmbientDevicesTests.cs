using AmbientFx.Bridge;
using AmbientFx.Devices;
using AmbientFx.Models;
using Moq;
using Xunit;

namespace AmbientFx.Engine.Tests.Coordinator;

/// <summary>
/// Coordinator wiring for ambient RGB peripherals (Story 8.1 AC5–AC8): the setDevices
/// command, start/stop coupling to the pipeline, frame fan-out into the device service,
/// and the devices payload pushes to the control window.
/// </summary>
public sealed class AmbientDevicesTests
{
    [Fact]
    public async Task SetDevices_enabled_starts_the_service_when_the_pipeline_runs()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();

        h.Send(CommandTypes.SetDevices, new { enabled = true });

        h.AmbientDevices.Verify(d => d.Start(), Times.Once);
        Assert.True(h.LastConfig.Settings.AmbientDevicesEnabled);
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.AmbientDevicesEnabled),
            because: "the toggle is a discrete action and must save immediately");
    }

    [Fact]
    public async Task SetDevices_enabled_does_not_start_while_the_master_switch_is_off()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = false;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        await h.StartAsync();

        h.Send(CommandTypes.SetDevices, new { enabled = true });

        h.AmbientDevices.Verify(d => d.Start(), Times.Never);
        h.AmbientDevices.Verify(d => d.Stop(), Times.AtLeastOnce);
        Assert.True(h.LastConfig.Settings.AmbientDevicesEnabled); // remembered for next enable
    }

    [Fact]
    public async Task SetDevices_disabled_stops_the_service_and_releases_control()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.AmbientDevicesEnabled = true;
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();

        h.Send(CommandTypes.SetDevices, new { enabled = false });

        h.AmbientDevices.Verify(d => d.Stop(), Times.Once);
        h.AmbientDevices.Verify(d => d.Start(), Times.Never);
    }

    [Fact]
    public async Task Devices_start_with_the_pipeline_when_the_setting_is_already_on()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.AmbientDevicesEnabled = true;
        await h.StartAsync();

        h.AmbientDevices.Verify(d => d.Start(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Disabling_the_master_switch_stops_the_devices_too()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.AmbientDevicesEnabled = true;
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();

        h.Send(CommandTypes.SetEnabled, new { enabled = false });

        h.AmbientDevices.Verify(d => d.Stop(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SetDevices_brightness_is_clamped_applied_live_and_debounce_saved()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SetDevices, new { brightness = 7.5f });

        h.AmbientDevices.VerifySet(d => d.Brightness = 1f, Times.AtLeastOnce);
        Assert.Equal(1f, h.LastConfig.Settings.PeripheralBrightness);
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.PeripheralBrightness == 1f),
            because: "brightness saves on the debounce timer");
    }

    [Fact]
    public async Task FrameReady_feeds_the_device_service_with_the_frame()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        var frame = new FramePayload
        {
            Edges = new EdgeColors { Top = new[] { new[] { 1, 2, 3 } } },
        };

        h.Processing.Raise(p => p.FrameReady += null,
            new AmbientFx.Processing.FrameReadyEventArgs { Frame = frame });

        h.AmbientDevices.Verify(d => d.SubmitFrame(frame), Times.Once);
    }

    [Fact]
    public async Task RequestState_from_control_includes_the_devices_payload()
    {
        var h = new CoordinatorHarness();
        h.AmbientDevices.SetupGet(d => d.Snapshot).Returns(new AmbientDevicesSnapshot
        {
            ConnectionState = DeviceConnectionStates.Connected,
            Devices = new[]
            {
                new AmbientDeviceInfo { Id = "0:Kbd", Name = "Kbd", Type = "Keyboard", LedCount = 108 },
            },
        });
        await h.StartAsync();

        h.Send(CommandTypes.RequestState);

        var (_, payload) = h.ControlPosts.Last(p => p.Type == MessageTypes.Devices);
        var devices = Assert.IsType<DevicesPayload>(payload);
        Assert.Equal(DeviceConnectionStates.Connected, devices.ConnectionState);
        Assert.Equal("Kbd", Assert.Single(devices.Devices).Name);
    }

    [Fact]
    public async Task Service_state_changes_push_a_devices_payload_to_the_control_window()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        int before = h.ControlPosts.Count(p => p.Type == MessageTypes.Devices);

        h.AmbientDevices.Raise(d => d.StateChanged += null, EventArgs.Empty);

        Assert.Equal(before + 1, h.ControlPosts.Count(p => p.Type == MessageTypes.Devices));
    }

    [Fact]
    public async Task SetDevices_with_a_malformed_payload_is_ignored()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();
        h.ClearRecordings();

        h.Send(CommandTypes.SetDevices, new { enabled = "banana" });

        h.AmbientDevices.Verify(d => d.Start(), Times.Never);
        Assert.Empty(h.Saved);
    }

    // -------------------------------------- providers + audio reactive (8.3) --

    [Fact]
    public async Task SetRgbProviders_persists_and_forces_a_fresh_session()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.AmbientDevicesEnabled = true;
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();

        h.Send(CommandTypes.SetRgbProviders, new { providers = new[] { "corsair", "razer", "razer", " " } });

        Assert.Equal(new[] { "corsair", "razer" }, h.LastConfig.Settings.RgbProviders); // dedup + blank dropped
        h.AmbientDevices.Verify(d => d.SetEnabledProviders(
            It.Is<IReadOnlyCollection<string>>(p => p.SequenceEqual(new[] { "corsair", "razer" }))), Times.Once);
        // Reconnect: Stop tears down the old session, then Start opens one with the new set.
        h.AmbientDevices.Verify(d => d.Stop(), Times.AtLeastOnce);
        h.AmbientDevices.Verify(d => d.Start(), Times.Once);
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.RgbProviders.Contains("razer")));
    }

    [Fact]
    public async Task SetDevices_audio_fields_persist_and_apply_live()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SetDevices, new { audioReactive = true, audioDepth = 7.5f });

        Assert.True(h.LastConfig.Settings.AudioReactiveDevices);
        Assert.Equal(1f, h.LastConfig.Settings.AudioReactiveDepth); // clamped
        h.AmbientDevices.VerifySet(d => d.AudioReactiveEnabled = true, Times.AtLeastOnce);
        h.AmbientDevices.VerifySet(d => d.AudioReactiveDepth = 1f, Times.AtLeastOnce);
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.AudioReactiveDevices),
            because: "the audio toggle is discrete and must save immediately");
    }

    // ------------------------------------------- per-device placement (8.2) --

    [Fact]
    public async Task SetDevicePlacement_merges_clamps_applies_live_and_saves()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();

        h.Send(CommandTypes.SetDevicePlacement,
            new { deviceId = "corsair:K95", anchor = "left", brightness = 7.5f });

        var placement = h.LastConfig.Settings.DevicePlacements["corsair:K95"];
        Assert.Equal(DeviceAnchors.Left, placement.Anchor);
        Assert.Equal(1f, placement.Brightness); // clamped
        h.AmbientDevices.Verify(d => d.SetPlacements(
            It.Is<IReadOnlyDictionary<string, DevicePlacement>>(p =>
                p["corsair:K95"].Anchor == DeviceAnchors.Left)), Times.Once);
        CoordinatorHarness.WaitUntil(
            () => h.Saved.Any(s => s.DevicePlacements.ContainsKey("corsair:K95")),
            because: "anchor selection is a discrete action and must save immediately");
    }

    [Fact]
    public async Task SetDevicePlacement_back_to_defaults_prunes_the_entry()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.DevicePlacements["corsair:K95"] = new DevicePlacement
        {
            Anchor = DeviceAnchors.Left,
            Flip = true,
        };
        await h.StartAsync();

        h.Send(CommandTypes.SetDevicePlacement,
            new { deviceId = "corsair:K95", anchor = "auto", flip = false });

        Assert.Empty(h.LastConfig.Settings.DevicePlacements);
    }

    [Fact]
    public async Task SetDevicePlacement_rejects_an_invalid_anchor_but_keeps_valid_fields()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SetDevicePlacement,
            new { deviceId = "corsair:K95", anchor = "sideways", enabled = false });

        var placement = h.LastConfig.Settings.DevicePlacements["corsair:K95"];
        Assert.Equal(DeviceAnchors.Auto, placement.Anchor); // invalid anchor ignored
        Assert.False(placement.Enabled);
    }

    [Fact]
    public async Task SetDevicePlacement_without_a_device_id_is_ignored()
    {
        var h = new CoordinatorHarness();
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.SetDevicePlacement, new { anchor = "left" });

        Assert.Empty(h.Saved);
        Assert.DoesNotContain(h.AllPosts, p => p.Type == MessageTypes.Config);
    }

    [Fact]
    public async Task Machine_agnostic_presets_do_not_wipe_device_placements()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true; // skip default-preset seeding
        h.InitialSettings.DevicePlacements["corsair:strip"] = new DevicePlacement
        {
            Anchor = DeviceAnchors.Left,
        };
        h.InitialSettings.Presets.Add(new Preset
        {
            Name = "Agnostic",
            Snapshot = new ApplicationSettings(), // no SourceMonitorId, no placements
        });
        await h.StartAsync();

        h.Send(CommandTypes.LoadPreset, new { name = "Agnostic" });

        Assert.Equal(DeviceAnchors.Left,
            h.LastConfig.Settings.DevicePlacements["corsair:strip"].Anchor);
    }

    [Fact]
    public async Task Presets_with_their_own_placements_restore_them()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        var snapshot = new ApplicationSettings
        {
            SourceMonitorId = CoordinatorHarness.Display1,
            DevicePlacements = { ["corsair:strip"] = new DevicePlacement { Anchor = DeviceAnchors.Right } },
        };
        h.InitialSettings.DevicePlacements["corsair:strip"] = new DevicePlacement
        {
            Anchor = DeviceAnchors.Left,
        };
        h.InitialSettings.Presets.Add(new Preset { Name = "Desk", Snapshot = snapshot });
        await h.StartAsync();

        h.Send(CommandTypes.LoadPreset, new { name = "Desk" });

        Assert.Equal(DeviceAnchors.Right,
            h.LastConfig.Settings.DevicePlacements["corsair:strip"].Anchor);
    }
}

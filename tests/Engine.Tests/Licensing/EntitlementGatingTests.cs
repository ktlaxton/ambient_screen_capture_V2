using AmbientFx.Bridge;
using AmbientFx.Hosting;
using AmbientFx.Licensing;
using AmbientFx.Models;
using Moq;
using Xunit;
using CoordinatorHarness = AmbientFx.Engine.Tests.Coordinator.CoordinatorHarness;

namespace AmbientFx.Engine.Tests.Licensing;

/// <summary>
/// Free-vs-premium enforcement in the coordinator (Story 9.2): target-monitor cap, premium
/// effect rejection + fallback, per-monitor override gating, the RGB peripherals gate, and
/// the setLicenseKey activate/deactivate flow.
/// </summary>
public sealed class EntitlementGatingTests
{
    private static CoordinatorHarness FreeHarness(bool enabled = true)
    {
        var h = new CoordinatorHarness();
        h.UseFreeEdition();
        h.InitialSettings.IsEnabled = enabled;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.FirstRunCompleted = true;
        return h;
    }

    [Fact]
    public async Task Free_edition_glows_only_the_first_target_monitor()
    {
        var h = FreeHarness();
        h.InitialSettings.TargetMonitorIds = new List<string>
        {
            CoordinatorHarness.Display2, CoordinatorHarness.Display3,
        };
        await h.StartAsync();

        var lastSync = h.Syncs.Last();
        var spec = Assert.Single(lastSync); // second target stays listed but dormant
        Assert.Equal(CoordinatorHarness.Display2, spec.Monitor.Id);
    }

    [Fact]
    public async Task Premium_glows_every_target_monitor()
    {
        var h = FreeHarness();
        h.GrantPremium();
        h.InitialSettings.TargetMonitorIds = new List<string>
        {
            CoordinatorHarness.Display2, CoordinatorHarness.Display3,
        };
        await h.StartAsync();

        Assert.Equal(2, h.Syncs.Last().Count);
    }

    [Fact]
    public async Task Selecting_extra_targets_when_free_warns_but_keeps_the_selection()
    {
        var h = FreeHarness();
        await h.StartAsync();

        h.Send(CommandTypes.SetTargetMonitors, new
        {
            monitorIds = new[] { CoordinatorHarness.Display2, CoordinatorHarness.Display3 },
        });

        // Selection persists (upgrade lights it up later); the user gets an upsell toast.
        Assert.Equal(2, h.LastConfig.Settings.TargetMonitorIds.Count);
        Assert.Contains(h.ControlPosts, p => p.Type == MessageTypes.Status
            && ((StatusPayload)p.Payload).Message.Contains("Premium"));
    }

    [Fact]
    public async Task A_premium_effect_is_rejected_when_free_and_accepted_when_premium()
    {
        var h = FreeHarness();
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.SetEffect, new { effectId = "kaleidoscope" });

        Assert.Equal("edge-glow", h.LastConfig.Settings.ActiveEffectId); // unchanged default
        Assert.Contains(h.ControlPosts, p => p.Type == MessageTypes.Status
            && ((StatusPayload)p.Payload).Message.Contains("Premium"));

        h.GrantPremium();
        h.Send(CommandTypes.SetEffect, new { effectId = "kaleidoscope" });
        Assert.Equal("kaleidoscope", h.LastConfig.Settings.ActiveEffectId);
    }

    [Fact]
    public async Task Settings_referencing_premium_features_degrade_instead_of_breaking()
    {
        // An expired/removed license must not corrupt settings — effects fall back, overrides
        // are ignored, but everything stays stored for when premium returns.
        var h = FreeHarness();
        h.InitialSettings.ActiveEffectId = "nebula"; // premium
        h.InitialSettings.TargetMonitorIds = new List<string> { CoordinatorHarness.Display2 };
        h.InitialSettings.EffectByMonitorId[CoordinatorHarness.Display2] = "fire"; // premium override
        await h.StartAsync();
        h.Send(CommandTypes.RequestState);

        var spec = Assert.Single(h.Syncs.Last());
        Assert.Equal("edge-glow", spec.Config.EffectId); // fallback, not a crash or a blank
        Assert.Equal("nebula", h.LastControlConfig.Settings.ActiveEffectId); // settings untouched
    }

    [Fact]
    public async Task Free_edition_ignores_per_monitor_overrides_premium_honors_them()
    {
        var h = FreeHarness();
        h.InitialSettings.ActiveEffectId = "plasma";
        h.InitialSettings.TargetMonitorIds = new List<string> { CoordinatorHarness.Display2 };
        h.InitialSettings.EffectByMonitorId[CoordinatorHarness.Display2] = "aurora"; // free effect, premium feature
        await h.StartAsync();
        Assert.Equal("plasma", h.Syncs.Last()[0].Config.EffectId);

        h.GrantPremium();
        h.Send(CommandTypes.SetSourceMonitor, new { monitorId = CoordinatorHarness.Display1 });
        Assert.Equal("aurora", h.Syncs.Last()[0].Config.EffectId);
    }

    [Fact]
    public async Task Rgb_peripherals_cannot_be_enabled_on_the_free_edition()
    {
        var h = FreeHarness();
        await h.StartAsync();
        h.AmbientDevices.Invocations.Clear();

        h.Send(CommandTypes.SetDevices, new { enabled = true });

        Assert.False(h.LastConfig.Settings.AmbientDevicesEnabled);
        h.AmbientDevices.Verify(d => d.Start(), Times.Never);
        Assert.Contains(h.ControlPosts, p => p.Type == MessageTypes.Status
            && ((StatusPayload)p.Payload).Message.Contains("Premium"));
    }

    [Fact]
    public async Task Rgb_peripherals_stay_dormant_when_enabled_in_settings_but_unlicensed()
    {
        // e.g. a premium user whose subscription-style key expired: the setting survives,
        // the hardware is released.
        var h = FreeHarness();
        h.InitialSettings.AmbientDevicesEnabled = true;
        await h.StartAsync();
        h.Send(CommandTypes.RequestState);

        h.AmbientDevices.Verify(d => d.Start(), Times.Never);
        h.AmbientDevices.Verify(d => d.Stop(), Times.AtLeastOnce);
        Assert.True(h.LastControlConfig.Settings.AmbientDevicesEnabled); // preserved for re-upgrade
    }

    [Fact]
    public async Task SetLicenseKey_with_a_valid_key_activates_persists_and_reapplies_state()
    {
        var h = FreeHarness();
        h.InitialSettings.TargetMonitorIds = new List<string>
        {
            CoordinatorHarness.Display2, CoordinatorHarness.Display3,
        };
        await h.StartAsync();
        Assert.Single(h.Syncs.Last()); // free: one window

        var premium = h.GrantPremium("Kirk"); // the mocked Apply now accepts the key
        h.Send(CommandTypes.SetLicenseKey, new { key = "AFX1.fake.fake" });

        Assert.Equal("AFX1.fake.fake", h.LastConfig.Settings.LicenseKey);
        Assert.True(h.LastConfig.License.IsPremium);
        Assert.Equal("Kirk", h.LastConfig.License.LicensedTo);
        Assert.Equal(2, h.Syncs.Last().Count); // second monitor lights up immediately
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.LicenseKey == "AFX1.fake.fake"));
        Assert.Equal(premium.Edition, h.LastConfig.License.Edition);
    }

    [Fact]
    public async Task SetLicenseKey_with_an_invalid_key_changes_nothing_and_warns()
    {
        var h = FreeHarness();
        h.License.Setup(l => l.Apply("AFX1.bad.key"))
            .Returns(LicenseInfo.Invalid("The license key's signature is not valid."));
        await h.StartAsync();
        h.ClearRecordings();

        h.Send(CommandTypes.SetLicenseKey, new { key = "AFX1.bad.key" });

        Assert.Equal(string.Empty, h.LastConfig.Settings.LicenseKey);
        Assert.False(h.LastConfig.License.IsPremium);
        Assert.Contains(h.ControlPosts, p => p.Type == MessageTypes.Status
            && ((StatusPayload)p.Payload).Level == "warn");
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task SetLicenseKey_with_an_empty_key_returns_to_free()
    {
        var h = new CoordinatorHarness(); // premium by default
        h.InitialSettings.LicenseKey = "AFX1.some.key";
        h.InitialSettings.FirstRunCompleted = true;
        await h.StartAsync();

        h.UseFreeEdition(); // the mocked Apply("") now reports free
        h.Send(CommandTypes.SetLicenseKey, new { key = "" });

        Assert.Equal(string.Empty, h.LastConfig.Settings.LicenseKey);
        Assert.False(h.LastConfig.License.IsPremium);
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.LicenseKey == string.Empty));
    }

    [Fact]
    public async Task Startup_applies_the_persisted_license_key()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.LicenseKey = "AFX1.saved.key";
        h.InitialSettings.FirstRunCompleted = true;
        await h.StartAsync();
        h.Send(CommandTypes.RequestState);

        h.License.Verify(l => l.Apply("AFX1.saved.key"), Times.Once);
        Assert.True(h.LastControlConfig.License.IsPremium);
    }
}

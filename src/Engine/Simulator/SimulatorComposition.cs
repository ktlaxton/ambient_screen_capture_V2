#if SIMULATOR_ENABLED
using AmbientFx.Capture;
using AmbientFx.Devices;
using AmbientFx.Hosting;
using AmbientFx.Licensing;
using AmbientFx.Models;
using AmbientFx.Services;
using AmbientFx.Simulator.Capture;
using AmbientFx.Simulator.Content;
using AmbientFx.Simulator.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator). Overrides exactly the two DI seams that originate topology
/// and pixels — <see cref="IMonitorDetectionService"/> and <see cref="IScreenCaptureService"/> — with
/// their simulated implementations. Re-registering a service type makes the last registration win, so
/// the real registrations from <c>App.ConfigureServices</c> are shadowed (never instantiated) when the
/// simulator gate is on. Everything else — the coordinator, processing, bridge, effect runtime —
/// composes exactly as in production. Compiled out of Release via <c>SIMULATOR_ENABLED</c>.
/// </summary>
public static class SimulatorComposition
{
    public static void Apply(IServiceCollection services)
    {
        // The scenario seeds the topology and the per-monitor pattern/fps once. The detection service
        // owns the LIVE topology thereafter (its mutation API mutates a private list), so the capture
        // service follows live source-resolution changes by pulling the active monitor's current size
        // from the detection service each frame (wired below) — not from this scenario instance, which
        // is only the initial seed. Pattern/fps are seeded once; a monitor hot-plugged later via the
        // mutation API uses the default pattern/fps until ConfigureMonitor'd (e.g. by the 10.5 editor).
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SimulatorScenario>>();
            // Story 10.6: the run-simulator launcher can preselect a scenario via this env var (a curated
            // library name or a .json path) — automation keeps deterministic startup this way.
            var requested = Environment.GetEnvironmentVariable("AMBIENTFX_SIMULATOR_SCENARIO");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                return SimulatorScenarioLibrary.Load(requested, logger);
            }
            // UX redesign default: land on "my real setup, mirrored" — the real monitors recreated as
            // virtual twins, each live-mirroring its real counterpart. The mirror guard (wired with the
            // window below) pauses the twin of whichever display ends up hosting the composite window.
            var real = EnumerateRealMonitors(sp);
            return real.Count > 0 ? SimulatorRealSetupClone.Build(real) : SimulatorScenario.LoadDefault(logger);
        });

        // One shared device-placements map: the peripheral editor mutates it AND the sim settings expose
        // it as ApplicationSettings.DevicePlacements, so the coordinator's ApplyAmbientDeviceState re-push
        // preserves interactive anchor edits instead of clobbering them with an empty map (Story 10.4 fix).
        services.AddSingleton(_ => new Dictionary<string, DevicePlacement>());

        services.AddSingleton<IMonitorDetectionService>(sp => new SimulatedMonitorDetectionService(
            sp.GetRequiredService<ILogger<SimulatedMonitorDetectionService>>(),
            sp.GetRequiredService<SimulatorScenario>()));

        services.AddSingleton<IScreenCaptureService>(sp =>
        {
            var scenario = sp.GetRequiredService<SimulatorScenario>();
            var capture = new SimulatedScreenCaptureService(
                sp.GetRequiredService<ILogger<SimulatedScreenCaptureService>>());
            foreach (var m in scenario.Monitors)
            {
                capture.ConfigureMonitor(m.Id, m.Pattern, m.MaxFps);
            }
            // Let the simulated capture follow a live SetResolution on the source monitor mid-stream,
            // pulling the current size from the detection service (the live-topology owner).
            if (sp.GetRequiredService<IMonitorDetectionService>() is SimulatedMonitorDetectionService detection)
            {
                capture.MonitorResolver = detection.TryGetResolution;
            }
            // Story 10.3: build per-monitor content sources (media / mirror / blank) from the scenario.
            BuildContentSources(sp, scenario, capture);
            return capture;
        });

        // ── Story 10.2: composite window + surface-factory seam + engine-drives-scenario settings ──

        // UX redesign: the one orchestrator for whole-scene actions (load preset / clone real setup /
        // per-monitor content + effect + source). It funnels everything through the existing seams;
        // the command hook resolves the window manager LAZILY (at click time) to avoid a DI cycle.
        services.AddSingleton(sp =>
        {
            var detection = (SimulatedMonitorDetectionService)sp.GetRequiredService<IMonitorDetectionService>();
            var capture = (SimulatedScreenCaptureService)sp.GetRequiredService<IScreenCaptureService>();
            var logger = sp.GetRequiredService<ILogger<SimulatorSceneController>>();
            return new SimulatorSceneController(
                detection,
                capture,
                content => BuildOneContentSource(sp, content, sp.GetRequiredService<ILogger<SimulatorScenario>>()),
                cmd => (sp.GetRequiredService<IWebViewWindowManager>() as WebViewWindowManager)?.RaiseSimulatorCommand(cmd, "control"),
                sp.GetRequiredService<Dictionary<string, DevicePlacement>>(),
                p => sp.GetRequiredService<IAmbientDeviceService>().SetPlacements(p),
                sp.GetRequiredService<SimulatorScenario>(),
                logger);
        });

        // The single composite window. Created + shown on the UI thread when first resolved (the engine
        // coordinator is built on the UI thread). Draws a backdrop for every monitor and re-draws them
        // when the simulated topology changes; effect viewports are added on top by the surface factory.
        services.AddSingleton(sp =>
        {
            var window = new SimulatorWindow(sp.GetRequiredService<ILogger<SimulatorWindow>>());
            var scenario = sp.GetRequiredService<SimulatorScenario>();
            var controller = sp.GetRequiredService<SimulatorSceneController>();
            // The controller's Current follows scene swaps (presets / clone / blank) — the DI scenario
            // is only the launch seed.
            window.PatternResolver = id => controller.Current.Monitors
                .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Pattern
                ?? SyntheticPatterns.Gradient;
            if (sp.GetRequiredService<IMonitorDetectionService>() is SimulatedMonitorDetectionService detection)
            {
                window.SetTopologyProvider(detection.GetMonitors);
                // Live re-layout: the on-demand MonitorsChanged (10.1) refreshes the backdrops; the
                // coordinator's own subscription drives the effect-surface re-sync. Both ride the
                // unmodified MonitorsChanged path.
                detection.MonitorsChanged += (_, _) =>
                    window.Dispatcher.BeginInvoke(new Action(window.RefreshTopology));
                // UX redesign: the canvas-first scene UI — floating monitor card + scene events
                // (replaces the retired docked editor panel).
                window.ConfigureScene(
                    controller,
                    detection,
                    SimulatorEffectsCatalog.Load(sp.GetRequiredService<ILogger<SimulatorScenario>>()),
                    () => EnumerateRealMonitors(sp),
                    () => sp.GetRequiredService<SimulatorSettingsService>().Current);

                // Story 10.6: drag-to-arrange. The window moves/adds monitors through the live topology and
                // re-syncs the engine once per gesture (mutations don't fire MonitorsChanged themselves).
                window.MoveMonitorRequested = (id, x, y) => detection.SetPosition(id, x, y);
                window.AddMonitorRequested = (w, h) => detection.AddMonitorAtDefault(w, h);
                window.TopologyCommitted = () => detection.FireMonitorsChanged();

                // UX redesign: the scene-level toolbar (Presets / sized Add / mode toggle / display
                // change / FPS / Fit).
                window.ConfigureChrome(new SimulatorToolbar(
                    controller,
                    () => EnumerateRealMonitors(sp),
                    () => sp.GetRequiredService<SimulatorSettingsService>().Current,
                    window.ToggleArrangeMode,
                    window.AddMonitorSized,
                    () => detection.FireMonitorsChanged(),
                    window.FitToWindow,
                    sp.GetRequiredService<ILogger<SimulatorToolbar>>()));
            }

            // UX redesign: the feedback-loop guard — pauses any mirror of the display hosting this
            // window (hall of mirrors) and restores it when the window moves away. Must exist before
            // Show(): the first evaluation rides SourceInitialized.
            var guard = new SimulatorMirrorGuard(
                window,
                controller,
                () => EnumerateRealMonitors(sp),
                sp.GetRequiredService<ILogger<SimulatorMirrorGuard>>());
            controller.MirrorGuard = guard;
            guard.PausedChanged += paused =>
                window.Dispatcher.BeginInvoke(new Action(() => window.SetMirrorStatus(paused)));

            // Open the window on a display OTHER than the one the source monitor mirrors, so the
            // guard never has to pause the mirror that drives the effect right at launch.
            string? sourcePhysicalId = null;
            var sourceMonitor = controller.Current.Monitors.FirstOrDefault(m =>
                string.Equals(m.Id, controller.Current.ResolveSourceId(), StringComparison.OrdinalIgnoreCase));
            if (sourceMonitor?.Content is { } sourceContent
                && string.Equals(sourceContent.Kind, SimContent.Mirror, StringComparison.OrdinalIgnoreCase))
            {
                sourcePhysicalId = sourceContent.PhysicalMonitorId;
            }
            window.PreferDisplayAwayFrom(sourcePhysicalId, () => EnumerateRealMonitors(sp));

            // Story 10.4: live RGB peripheral visualization around the source viewport.
            var backend = sp.GetRequiredService<VisualizationBackend>();
            var deviceService = sp.GetRequiredService<IAmbientDeviceService>();
            var placements = sp.GetRequiredService<Dictionary<string, DevicePlacement>>(); // shared with sim settings
            window.ConfigurePeripherals(
                backend,
                placements,
                p => deviceService.SetPlacements(p),
                scenario.ResolveSourceId());

            window.Show();
            return window;
        });

        // Override the window manager so effect surfaces are composite-window viewports, not OS windows.
        // The bridge, coordinator, WindowConfigPayload, and EffectWindowSpec are unchanged — only the
        // injected surface factory differs from production.
        services.AddSingleton<IWebViewWindowManager>(sp => new WebViewWindowManager(
            sp.GetRequiredService<ILogger<WebViewWindowManager>>(),
            monitor => sp.GetRequiredService<SimulatorWindow>().CreateSurface(monitor)));

        // Drive the engine against the scenario (source + targets + enabled) without persisting, so
        // --simulator immediately renders effects in the composite window. Registered as the concrete
        // type too: the UI reads the LIVE engine state via SimulatorSettingsService.Current when
        // saving a full-scene preset (the coordinator mutates that instance in place).
        services.AddSingleton(sp => new SimulatorSettingsService(
            sp.GetRequiredService<SimulatorScenario>(),
            sp.GetRequiredService<Dictionary<string, DevicePlacement>>(),
            sp.GetRequiredService<ILogger<SimulatorSettingsService>>()));
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SimulatorSettingsService>());

        // ── Story 10.4: RGB peripheral readback + synthetic audio + sim-Premium entitlement ──

        // The recording backend the renderer reads (a single instance reused across connect sessions).
        services.AddSingleton<VisualizationBackend>();

        // Override the device service's backend factory with the VisualizationBackend — the service,
        // its push timer, and LedProjection are otherwise unchanged (fidelity invariant).
        services.AddSingleton<IAmbientDeviceService>(sp =>
        {
            var backend = sp.GetRequiredService<VisualizationBackend>();
            return new RgbNetAmbientDeviceService(
                _ => backend,
                sp.GetRequiredService<ILogger<RgbNetAmbientDeviceService>>());
        });

        // Synthetic audio in place of WASAPI loopback — drives the unmodified real audio path.
        services.AddSingleton<IAudioCaptureService>(sp => new SimulatedAudioCaptureService(
            sp.GetRequiredService<ILogger<SimulatedAudioCaptureService>>()));

        // Sim-Premium so the gated RGB path actually starts (the real gate code is untouched).
        services.AddSingleton<ILicenseService, SimulatorLicenseService>();
    }

    /// <summary>
    /// Story 10.6: enumerates the <b>real</b> physical monitors (for the editor's "mirror a monitor"
    /// dropdown) via a throwaway real <see cref="MonitorDetectionService"/>. Never throws.
    /// </summary>
    private static IReadOnlyList<MonitorInfo> EnumerateRealMonitors(IServiceProvider sp)
    {
        try
        {
            using var detection = new MonitorDetectionService(sp.GetRequiredService<ILogger<MonitorDetectionService>>());
            return detection.GetMonitors();
        }
        catch
        {
            return Array.Empty<MonitorInfo>();
        }
    }

    /// <summary>
    /// Story 10.3: assigns media / mirror / blank content sources to the simulated capture per the
    /// scenario. Synthetic monitors keep the default pattern path. Mirror resolves the <b>real</b>
    /// physical monitor (live HMONITOR) from a real <see cref="MonitorDetectionService"/> and wraps a
    /// real <see cref="ScreenCaptureService"/> — the one place real WGC runs. Failures are logged and
    /// skipped (the monitor falls back to synthetic); nothing throws.
    /// </summary>
    private static void BuildContentSources(IServiceProvider sp, SimulatorScenario scenario, SimulatedScreenCaptureService capture)
    {
        var logger = sp.GetRequiredService<ILogger<SimulatorScenario>>();
        foreach (var m in scenario.Monitors)
        {
            var content = m.Content;
            if (content is null || string.Equals(content.Kind, SimContent.Synthetic, StringComparison.OrdinalIgnoreCase))
            {
                continue; // synthetic uses the configured pattern
            }
            try
            {
                var source = BuildOneContentSource(sp, content, logger);
                if (source is not null)
                {
                    capture.SetContentSource(m.Id, source);
                }
                else
                {
                    logger.LogWarning("Simulator: monitor '{Id}' content '{Kind}' could not be built; using synthetic.", m.Id, content.Kind);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Simulator: building content for '{Id}' failed; using synthetic.", m.Id);
            }
        }
    }

    /// <summary>
    /// Builds one content source from a <see cref="SimContent"/> descriptor (shared by startup wiring and
    /// the 10.5 editor's content selector). Mirror resolves the <b>real</b> physical monitor (live
    /// HMONITOR) from a real <see cref="MonitorDetectionService"/> and wraps a real
    /// <see cref="ScreenCaptureService"/> — the one place real WGC runs. Returns null for synthetic or an
    /// unresolvable assignment (the monitor falls back to the pattern path).
    /// </summary>
    internal static ISimContentSource? BuildOneContentSource(IServiceProvider sp, SimContent content, ILogger logger)
    {
        switch (content.Kind?.ToLowerInvariant())
        {
            case SimContent.Blank:
                return new BlankContentSource();

            case SimContent.Media:
                if (string.IsNullOrWhiteSpace(content.MediaPath))
                {
                    logger.LogWarning("Simulator: media content has no mediaPath; using synthetic.");
                    return null;
                }
                // Story 10.6: a video file routes to the in-box MediaPlayer decoder; an image (or a folder
                // of images) stays on the WIC path. A directory is always an image sequence.
                if (!System.IO.Directory.Exists(content.MediaPath) && SimMediaKinds.IsVideo(content.MediaPath))
                {
                    return new VideoContentSource(content.MediaPath!, sp.GetRequiredService<ILogger<VideoContentSource>>());
                }
                return new MediaContentSource(content.MediaPath!, sp.GetRequiredService<ILogger<MediaContentSource>>());

            case SimContent.Mirror:
                using (var realDetection = new MonitorDetectionService(sp.GetRequiredService<ILogger<MonitorDetectionService>>()))
                {
                    var physical = realDetection.GetMonitors()
                        .FirstOrDefault(pm => string.Equals(pm.Id, content.PhysicalMonitorId, StringComparison.OrdinalIgnoreCase));
                    if (physical is null)
                    {
                        logger.LogWarning("Simulator: physical monitor '{Phys}' is not connected; using synthetic.", content.PhysicalMonitorId);
                        return null;
                    }
                    // MirrorContentSource captures the live HMONITOR + starts the real capture immediately,
                    // so disposing realDetection after construction is safe.
                    return new MirrorContentSource(
                        new ScreenCaptureService(sp.GetRequiredService<ILogger<ScreenCaptureService>>()),
                        physical,
                        sp.GetRequiredService<ILogger<MirrorContentSource>>());
                }

            default:
                return null; // synthetic / unknown -> pattern path
        }
    }
}
#endif

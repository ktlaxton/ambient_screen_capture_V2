#if SIMULATOR_ENABLED
using System.Linq;
using System.Text.Json;
using AmbientFx.Bridge;
using AmbientFx.Models;
using AmbientFx.Simulator.Content;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The single orchestrator for "apply / record a whole scene":
/// owns the mutable current scenario (the desired state the canvas UI edits), and funnels every
/// scene-level action — load a preset, clone the real setup, set a monitor's content/effect, switch
/// the source — through the existing seams: the simulated detection/capture services, the shared
/// placements dictionary, and real coordinator commands via the injected
/// <c>RaiseSimulatorCommand</c> path. No pipeline logic is reimplemented (fidelity invariant).
/// Compiled out of Release.
/// </summary>
/// <remarks>
/// All members are UI-thread (the same thread the coordinator handles commands on). Content installs
/// consult the <see cref="MirrorGuard"/> so a mirror of the display hosting the simulator window is
/// physically installed as synthetic (feedback-loop guard) while the DESIRED state — what presets
/// save and what the guard restores when the window moves — stays in <see cref="Current"/>.
/// </remarks>
public sealed class SimulatorSceneController
{
    private readonly SimulatedMonitorDetectionService _detection;
    private readonly SimulatedScreenCaptureService _capture;
    private readonly Func<SimContent, ISimContentSource?> _buildContent;
    private readonly Action<CommandEnvelope> _inject;
    private readonly Dictionary<string, DevicePlacement> _placements; // the shared singleton
    private readonly Action<IReadOnlyDictionary<string, DevicePlacement>> _applyPlacements;
    private readonly ILogger _logger;

    public SimulatorSceneController(
        SimulatedMonitorDetectionService detection,
        SimulatedScreenCaptureService capture,
        Func<SimContent, ISimContentSource?> buildContent,
        Action<CommandEnvelope> inject,
        Dictionary<string, DevicePlacement> placements,
        Action<IReadOnlyDictionary<string, DevicePlacement>> applyPlacements,
        SimulatorScenario initial,
        ILogger logger)
    {
        _detection = detection;
        _capture = capture;
        _buildContent = buildContent;
        _inject = inject;
        _placements = placements;
        _applyPlacements = applyPlacements;
        Current = initial;
        _logger = logger;
    }

    /// <summary>The desired scene: topology seed + per-monitor pattern/content/effect + source +
    /// placements. The canvas UI mutates it through the methods below; preset save reads it.</summary>
    public SimulatorScenario Current { get; private set; }

    /// <summary>Feedback-loop guard; assigned right after construction (it needs the window).</summary>
    public SimulatorMirrorGuard? MirrorGuard { get; set; }

    /// <summary>Raised after a whole-scene swap (load preset / clone real setup / blank slate).</summary>
    public event Action? SceneReplaced;

    /// <summary>Raised whenever the engine source monitor id changes (scene apply or Set as source),
    /// so the window can re-home the peripheral layer.</summary>
    public event Action<string>? SourceChanged;

    // ── whole-scene actions ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Swaps the entire scene: topology, per-monitor pattern/fps/content, placements, source,
    /// targets (every non-source monitor, the simulator's standing rule), and effects — global
    /// effect FIRST (the coordinator clears per-monitor overrides on a global setEffect), then the
    /// per-monitor overrides. UI thread.
    /// </summary>
    public void ApplyScene(SimulatorScenario scene)
    {
        var oldIds = Current.Monitors.Select(m => m.Id).ToList();
        var newIds = new HashSet<string>(scene.Monitors.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        _detection.ReplaceTopology(scene.ToMonitorInfos());
        foreach (var m in scene.Monitors)
        {
            _capture.ConfigureMonitor(m.Id, m.Pattern, m.MaxFps);
        }

        Current = scene;              // desired state first — the installs below read it
        MirrorGuard?.UpdatePausedSet(); // recompute the pause overlay for the new mirror set

        foreach (string oldId in oldIds.Where(id => !newIds.Contains(id)))
        {
            _capture.SetContentSource(oldId, null); // dispose sources of monitors that left the scene
        }
        foreach (var m in scene.Monitors)
        {
            InstallDesiredContent(m.Id);
        }

        _placements.Clear();
        if (scene.DevicePlacements is not null)
        {
            foreach (var (deviceId, placement) in scene.DevicePlacements)
            {
                _placements[deviceId] = placement.Clone();
            }
        }
        _applyPlacements(ClonePlacements());

        _detection.FireMonitorsChanged(); // the one re-sync: real coordinator hot-plug path

        string sourceId = scene.ResolveSourceId();
        PushSourceAndTargets(sourceId);
        if (!string.IsNullOrWhiteSpace(scene.ActiveEffectId))
        {
            Inject(CommandTypes.SetEffect, new { effectId = scene.ActiveEffectId }); // global first
        }
        foreach (var m in scene.Monitors.Where(m => !string.IsNullOrWhiteSpace(m.Effect)))
        {
            Inject(CommandTypes.SetEffect, new { monitorId = m.Id, effectId = m.Effect });
        }
        if (scene.GlobalMaxFps is { } fps)
        {
            Inject(CommandTypes.SetGlobal, new { maxFps = fps });
        }

        SceneReplaced?.Invoke();
        SourceChanged?.Invoke(sourceId);
        _logger.LogInformation("Simulator: scene '{Name}' applied ({Count} monitors, source {Source}).",
            scene.Name, scene.Monitors.Count, sourceId);
    }

    /// <summary>Clones the real desk: every real monitor becomes a mirrored virtual twin.</summary>
    public void ApplyRealSetup(IReadOnlyList<MonitorInfo> realMonitors) =>
        ApplyScene(SimulatorRealSetupClone.Build(realMonitors));

    /// <summary>A fresh single-monitor canvas to build on.</summary>
    public void ApplyBlank()
    {
        var scene = SimulatorScenario.Fallback();
        scene.Name = "Blank slate";
        ApplyScene(scene);
    }

    /// <summary>Full-scene snapshot for the preset store: live topology + live engine state
    /// (<paramref name="live"/> = <see cref="SimulatorSettingsService.Current"/>) + live placements.</summary>
    public SimulatorScenario CaptureScene(ApplicationSettings? live) =>
        SimulatorSceneCapture.Capture(Current, _detection.GetMonitors(), live, _placements);

    // ── per-monitor actions (the card's seams) ─────────────────────────────────────────────────────

    /// <summary>Records + installs a monitor's content (null = back to its synthetic pattern).
    /// Mirrors route through the feedback-loop guard.</summary>
    public void SetMonitorContent(string monitorId, SimContent? content)
    {
        if (FindMonitor(monitorId) is not { } monitor)
        {
            return;
        }
        monitor.Content = content;

        var changed = MirrorGuard?.UpdatePausedSet() ?? Array.Empty<string>();
        InstallDesiredContent(monitorId);
        foreach (string id in changed.Where(c => !string.Equals(c, monitorId, StringComparison.OrdinalIgnoreCase)))
        {
            InstallDesiredContent(id);
        }
        _logger.LogInformation("Simulator: content for '{Id}' -> {Kind}.", monitorId, content?.Kind ?? "synthetic");
    }

    /// <summary>Sets a monitor's synthetic pattern (takes effect when its content is synthetic).</summary>
    public void SetMonitorPattern(string monitorId, string pattern)
    {
        if (FindMonitor(monitorId) is not { } monitor)
        {
            return;
        }
        monitor.Pattern = pattern;
        _capture.ConfigureMonitor(monitorId, pattern, monitor.MaxFps);
    }

    /// <summary>Sets/clears a monitor's per-monitor effect override (null/empty = the global effect),
    /// via the real setEffect command path.</summary>
    public void SetMonitorEffect(string monitorId, string? effectId)
    {
        if (FindMonitor(monitorId) is not { } monitor)
        {
            return;
        }
        monitor.Effect = string.IsNullOrWhiteSpace(effectId) ? null : effectId;
        // An empty effectId clears the per-monitor override back to global (coordinator semantics).
        Inject(CommandTypes.SetEffect, new { monitorId, effectId = monitor.Effect ?? string.Empty });
    }

    /// <summary>Makes a monitor the engine source; every other monitor becomes a target (the
    /// simulator's standing all-non-source-are-targets rule).</summary>
    public void SetSource(string monitorId)
    {
        if (FindMonitor(monitorId) is null)
        {
            return;
        }
        Current.SourceMonitorId = monitorId;
        PushSourceAndTargets(monitorId);
        SourceChanged?.Invoke(monitorId);
    }

    /// <summary>Sets the global FPS ceiling via the real setGlobal command.</summary>
    public void SetGlobalFps(int fps)
    {
        if (fps <= 0)
        {
            return;
        }
        Current.GlobalMaxFps = fps;
        Inject(CommandTypes.SetGlobal, new { maxFps = fps });
    }

    // ── content install (also called by the mirror guard on pause/resume) ─────────────────────────

    /// <summary>
    /// (Re)installs a monitor's DESIRED content into the capture service: synthetic/none clears the
    /// assignment; a mirror currently paused by the guard installs as synthetic (the desired state in
    /// <see cref="Current"/> is untouched, so it restores when the window moves away). UI thread.
    /// </summary>
    public void InstallDesiredContent(string monitorId)
    {
        var content = FindMonitor(monitorId)?.Content;
        if (content is null || string.Equals(content.Kind, SimContent.Synthetic, StringComparison.OrdinalIgnoreCase))
        {
            _capture.SetContentSource(monitorId, null);
            return;
        }

        if (string.Equals(content.Kind, SimContent.Mirror, StringComparison.OrdinalIgnoreCase)
            && MirrorGuard is { } guard
            && guard.PausedMonitorIds.Contains(monitorId))
        {
            _capture.SetContentSource(monitorId, null); // paused: mirroring the hosting display would feed back
            _logger.LogInformation("Simulator: mirror for '{Id}' paused (feedback-loop guard).", monitorId);
            return;
        }

        try
        {
            var source = _buildContent(content);
            _capture.SetContentSource(monitorId, source);
            if (source is null)
            {
                _logger.LogWarning("Simulator: content '{Kind}' for '{Id}' could not be built; using synthetic.",
                    content.Kind, monitorId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulator: installing content for '{Id}' failed; using synthetic.", monitorId);
            _capture.SetContentSource(monitorId, null);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private SimulatorMonitor? FindMonitor(string monitorId) => Current.Monitors
        .FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));

    private void PushSourceAndTargets(string sourceId)
    {
        Inject(CommandTypes.SetSourceMonitor, new { monitorId = sourceId });
        Inject(CommandTypes.SetTargetMonitors, new
        {
            monitorIds = Current.Monitors
                .Select(m => m.Id)
                .Where(id => !string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        });
    }

    private Dictionary<string, DevicePlacement> ClonePlacements()
    {
        var clones = new Dictionary<string, DevicePlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (deviceId, placement) in _placements)
        {
            clones[deviceId] = placement.Clone();
        }
        return clones;
    }

    private void Inject(string type, object payload)
    {
        try
        {
            _inject(new CommandEnvelope
            {
                Type = type,
                Payload = JsonSerializer.SerializeToElement(payload, BridgeJson.Options),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulator: injecting command {Type} failed.", type);
        }
    }
}
#endif

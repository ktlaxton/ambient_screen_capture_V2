#if SIMULATOR_ENABLED
using System.Linq;
using AmbientFx.Models;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The pure "save the whole scene" merge behind the preset store:
/// starts from the backing scenario re-based onto the live topology (<see
/// cref="SimulatorScenario.WithTopology"/>, which preserves pattern/fps/scale/content per monitor),
/// then overlays the LIVE engine state — source monitor, global + per-monitor effects, global FPS —
/// read from the coordinator's in-place-mutated <see cref="ApplicationSettings"/> (see
/// <see cref="SimulatorSettingsService.Current"/>), plus the live peripheral placements. The result
/// round-trips through <see cref="SimulatorScenario.ToJson"/> as a full-scene preset. Compiled out
/// of Release.
/// </summary>
public static class SimulatorSceneCapture
{
    /// <summary>
    /// Captures the current scene. <paramref name="live"/> may be null (engine not started yet) —
    /// then only topology + placements are captured. Placement entries are deep-cloned and
    /// default-valued entries are pruned (matching the coordinator's own pruning rule).
    /// </summary>
    public static SimulatorScenario Capture(
        SimulatorScenario backing,
        IReadOnlyList<MonitorInfo> liveMonitors,
        ApplicationSettings? live,
        IReadOnlyDictionary<string, DevicePlacement>? placements)
    {
        var result = backing.WithTopology(liveMonitors);

        if (live is not null)
        {
            if (!string.IsNullOrWhiteSpace(live.SourceMonitorId)
                && result.Monitors.Any(m => string.Equals(m.Id, live.SourceMonitorId, StringComparison.OrdinalIgnoreCase)))
            {
                result.SourceMonitorId = live.SourceMonitorId;
            }

            if (!string.IsNullOrWhiteSpace(live.ActiveEffectId))
            {
                result.ActiveEffectId = live.ActiveEffectId;
            }

            result.GlobalMaxFps = live.MaxFps;

            // Live per-monitor overrides are the truth for effects: a monitor absent from the live
            // override map gets Effect = null (global), even if the backing scenario said otherwise.
            foreach (var monitor in result.Monitors)
            {
                monitor.Effect =
                    live.EffectByMonitorId.TryGetValue(monitor.Id, out string? effect)
                    && !string.IsNullOrWhiteSpace(effect)
                        ? effect
                        : null;
            }
        }

        Dictionary<string, DevicePlacement>? captured = null;
        if (placements is not null)
        {
            foreach (var (deviceId, placement) in placements)
            {
                if (string.IsNullOrWhiteSpace(deviceId) || placement is null || placement.IsDefault)
                {
                    continue;
                }
                captured ??= new Dictionary<string, DevicePlacement>(StringComparer.OrdinalIgnoreCase);
                captured[deviceId] = placement.Clone();
            }
        }
        result.DevicePlacements = captured; // live placements are the truth, not the backing scenario's

        return result;
    }
}
#endif

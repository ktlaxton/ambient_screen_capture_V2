#if SIMULATOR_ENABLED
using AmbientFx.Models;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). Builds the "my real setup, mirrored" scenario: every REAL
/// monitor is recreated as a virtual twin (same name/geometry/primary), and each twin's content is a
/// live <c>mirror</c> of its real counterpart, so launching the simulator lands on a working replica
/// of the machine's actual desk driven by what is really on screen. The engine source is the twin of
/// the real primary. Pure — enumeration happens at the caller (the composition's throwaway real
/// <c>MonitorDetectionService</c>). Compiled out of Release.
/// </summary>
/// <remarks>
/// Id mapping matters: the twins get synthetic <c>\\.\SIM-DISPLAY{n}</c> ids (the simulated topology
/// must never collide with real ids), while each twin's <see cref="SimContent.PhysicalMonitorId"/>
/// carries the REAL stable id (<c>monitorDevicePath</c>) that <c>MirrorContentSource</c> resolves via
/// real WGC. Mirroring the display that hosts the simulator window is a feedback loop — that is
/// handled downstream by the mirror guard, not here (this builder states desired content only).
/// </remarks>
public static class SimulatorRealSetupClone
{
    public const string ScenarioName = "My real setup";

    /// <summary>Builds the mirrored-clone scenario. Empty/absent enumeration falls back to the
    /// built-in single-monitor topology (never throws, never returns null).</summary>
    public static SimulatorScenario Build(IReadOnlyList<MonitorInfo>? realMonitors)
    {
        if (realMonitors is null || realMonitors.Count == 0)
        {
            return SimulatorScenario.Fallback();
        }

        var scenario = new SimulatorScenario { Version = 2, Name = ScenarioName };
        for (int i = 0; i < realMonitors.Count; i++)
        {
            var real = realMonitors[i];
            scenario.Monitors.Add(new SimulatorMonitor
            {
                Id = $@"\\.\SIM-DISPLAY{i + 1}",
                Name = string.IsNullOrWhiteSpace(real.Name) ? $"Display {i + 1}" : real.Name,
                X = real.X,
                Y = real.Y,
                Width = real.Width,
                Height = real.Height,
                IsPrimary = real.IsPrimary,
                Content = string.IsNullOrWhiteSpace(real.Id)
                    ? null // no stable id to mirror — leave the twin synthetic
                    : new SimContent { Kind = SimContent.Mirror, PhysicalMonitorId = real.Id },
            });
        }

        // Exactly one primary (defensive — a real enumeration should already guarantee this).
        int primaryIndex = -1;
        for (int i = 0; i < scenario.Monitors.Count; i++)
        {
            if (!scenario.Monitors[i].IsPrimary)
            {
                continue;
            }
            if (primaryIndex < 0)
            {
                primaryIndex = i;
            }
            else
            {
                scenario.Monitors[i].IsPrimary = false;
            }
        }
        if (primaryIndex < 0)
        {
            primaryIndex = 0;
            scenario.Monitors[0].IsPrimary = true;
        }

        scenario.SourceMonitorId = scenario.Monitors[primaryIndex].Id;
        return scenario;
    }
}
#endif

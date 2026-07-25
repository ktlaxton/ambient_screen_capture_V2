#if SIMULATOR_ENABLED
using AmbientFx.Models;
using AmbientFx.Services;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator, Story 10.2). An <see cref="ISettingsService"/> that drives
/// the engine against the simulator scenario instead of the user's real <c>settings.json</c>: it loads
/// a fresh settings object with the scenario's source + target monitors and the engine enabled, so
/// <c>--simulator</c> immediately renders effects in the composite window. It <b>never persists</b>, so
/// the user's real configuration is left completely untouched. Compiled out of Release.
/// </summary>
public sealed class SimulatorSettingsService : ISettingsService
{
    private readonly SimulatorScenario _scenario;
    private readonly Dictionary<string, DevicePlacement> _placements;
    private readonly ILogger<SimulatorSettingsService> _logger;

    public SimulatorSettingsService(
        SimulatorScenario scenario,
        Dictionary<string, DevicePlacement> placements,
        ILogger<SimulatorSettingsService> logger)
    {
        _scenario = scenario;
        _placements = placements;
        _logger = logger;
    }

    /// <summary>
    /// The live engine state. <see cref="EngineCoordinator.StartAsync"/> keeps the exact instance
    /// <see cref="LoadAsync"/> hands out and mutates it in place for every command (setEffect /
    /// setSourceMonitor / setGlobal / …), all on the UI thread — so the scene-preset save can read
    /// the current effect/source/fps from here without shadow-tracking commands. Null until the
    /// coordinator has started.
    /// </summary>
    public ApplicationSettings? Current { get; private set; }

    public Task<ApplicationSettings> LoadAsync()
    {
        string sourceId = _scenario.ResolveSourceId();
        var settings = new ApplicationSettings
        {
            IsEnabled = true,
            FirstRunCompleted = true, // skip onboarding in the simulator
            SourceMonitorId = sourceId,
            TargetMonitorIds = _scenario.Monitors
                .Select(m => m.Id)
                .Where(id => !string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            // Story 10.4: exercise the (sim-Premium) RGB + audio-reactive paths out of the box.
            AmbientDevicesEnabled = true,
            AudioReactiveDevices = true,
            // Share the SAME placements dictionary the peripheral editor mutates, so the coordinator's
            // ApplyAmbientDeviceState re-push preserves interactive anchor changes instead of clobbering
            // them with an empty map (Story 10.4 fix).
            DevicePlacements = _placements,
        };
        _logger.LogInformation(
            "Simulator settings loaded: source={Source}, {TargetCount} target(s), engine enabled.",
            sourceId, settings.TargetMonitorIds.Count);
        Current = settings;
        return Task.FromResult(settings);
    }

    /// <summary>No-op: simulator state must never overwrite the user's real settings file.</summary>
    public Task SaveAsync(ApplicationSettings settings) => Task.CompletedTask;

    public ApplicationSettings GetDefaults() => new();
}
#endif

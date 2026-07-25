#if SIMULATOR_ENABLED
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.5). The curated, named scenario library shipped as embedded fixtures:
/// the layouts the product must support, ready to load in one action. Compiled out of Release.
/// </summary>
public static class SimulatorScenarioLibrary
{
    /// <summary>Display name → embedded resource file. SIM_MONITORS reproduces the browser simulator.</summary>
    private static readonly IReadOnlyList<(string Name, string Resource)> Entries = new[]
    {
        ("SIM_MONITORS", "sim-monitors.json"),
        ("3-wide", "three-wide.json"),
        ("L-shape", "l-shape.json"),
        ("vertical-stack", "vertical-stack.json"),
        ("portrait-flanked", "portrait-flanked.json"),
        ("mixed-dpi", "mixed-dpi.json"),
        ("gapped", "gapped.json"),
        ("six-grid", "six-grid.json"),
    };

    /// <summary>The curated display names, in menu order.</summary>
    public static IReadOnlyList<string> Names => Entries.Select(e => e.Name).ToList();

    /// <summary>The embedded resource file names (for the fixture-load tests).</summary>
    public static IReadOnlyList<string> ResourceFiles => Entries.Select(e => e.Resource).ToList();

    /// <summary>
    /// Loads a curated scenario by display name, or — if the argument is not a known name — treats it as
    /// a JSON file path. Always returns a validated scenario (never throws; falls back on failure).
    /// </summary>
    public static SimulatorScenario Load(string nameOrPath, ILogger logger)
    {
        var match = Entries.FirstOrDefault(e => string.Equals(e.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));
        if (match.Resource is not null)
        {
            return SimulatorScenario.LoadEmbedded(match.Resource, logger) ?? SimulatorScenario.Fallback();
        }
        return SimulatorScenario.LoadFromFile(nameOrPath, logger);
    }

    /// <summary>Loads every curated scenario (for the library picker and tests).</summary>
    public static IReadOnlyList<SimulatorScenario> LoadAll(ILogger logger) =>
        Entries.Select(e => SimulatorScenario.LoadEmbedded(e.Resource, logger) ?? SimulatorScenario.Fallback()).ToList();
}
#endif

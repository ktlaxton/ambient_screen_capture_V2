#if SIMULATOR_ENABLED
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (feedback-loop guard): the pure pause decision — only mirrors of the display hosting
/// the simulator window pause; an unknown host pauses nothing; multiple twins of the same physical
/// display all pause; results are deterministic (sorted).
/// </summary>
public sealed class SimulatorMirrorGuardDecisionTests
{
    private const string Host = @"\\?\DISPLAY#AAA#1&2&3#{guid}";
    private const string Other = @"\\?\DISPLAY#BBB#4&5&6#{guid}";

    [Fact]
    public void PausesOnlyMirrorsOfTheHostDisplay()
    {
        var mirrors = new Dictionary<string, string>
        {
            [@"\\.\SIM-DISPLAY1"] = Host,
            [@"\\.\SIM-DISPLAY2"] = Other,
        };

        var paused = SimulatorMirrorGuard.Decision.PausedIds(Host, mirrors);

        Assert.Equal(new[] { @"\\.\SIM-DISPLAY1" }, paused);
    }

    [Fact]
    public void UnknownHost_PausesNothing()
    {
        var mirrors = new Dictionary<string, string> { [@"\\.\SIM-DISPLAY1"] = Host };

        Assert.Empty(SimulatorMirrorGuard.Decision.PausedIds(null, mirrors));
        Assert.Empty(SimulatorMirrorGuard.Decision.PausedIds(string.Empty, mirrors));
    }

    [Fact]
    public void NoMirrors_PausesNothing()
    {
        Assert.Empty(SimulatorMirrorGuard.Decision.PausedIds(Host, null));
        Assert.Empty(SimulatorMirrorGuard.Decision.PausedIds(Host, new Dictionary<string, string>()));
    }

    [Fact]
    public void MultipleTwinsOfTheHost_AllPause_Sorted()
    {
        var mirrors = new Dictionary<string, string>
        {
            [@"\\.\SIM-DISPLAY3"] = Host,
            [@"\\.\SIM-DISPLAY1"] = Host.ToUpperInvariant(), // id comparison is case-insensitive
            [@"\\.\SIM-DISPLAY2"] = Other,
        };

        var paused = SimulatorMirrorGuard.Decision.PausedIds(Host, mirrors);

        Assert.Equal(new[] { @"\\.\SIM-DISPLAY1", @"\\.\SIM-DISPLAY3" }, paused);
    }
}
#endif

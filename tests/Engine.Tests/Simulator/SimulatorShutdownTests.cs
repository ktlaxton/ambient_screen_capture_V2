#if SIMULATOR_ENABLED
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.6: linked-window shutdown is one-shot. Closing either simulator window funnels through
/// <see cref="SimulatorShutdown.Request"/>; the first call fires the shutdown, and the second window
/// closing as a consequence must be a no-op (no double-shutdown). Uses the test seam so no WPF
/// <c>Application</c> is required.
/// </summary>
public sealed class SimulatorShutdownTests
{
    [Fact]
    public void Request_FiresShutdownOnce_ThenIsANoOp()
    {
        SimulatorShutdown.ResetForTests();
        try
        {
            int fired = 0;
            SimulatorShutdown.ShutdownActionForTests = () => fired++;

            Assert.True(SimulatorShutdown.Request());   // first close wins
            Assert.False(SimulatorShutdown.Request());  // second window closing -> no-op
            Assert.False(SimulatorShutdown.Request());

            Assert.Equal(1, fired);
        }
        finally
        {
            SimulatorShutdown.ResetForTests();
        }
    }
}
#endif

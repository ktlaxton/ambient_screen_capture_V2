#if SIMULATOR_ENABLED
using System.Threading;
using Application = System.Windows.Application;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.6). Linked-window shutdown: in simulator mode the composite
/// <see cref="SimulatorWindow"/> and the normal <c>ControlWindow</c> are one session — closing
/// <b>either</b> ends the whole thing. Both close paths funnel through <see cref="Request"/>, which is
/// idempotent: the first caller triggers <see cref="Application.Shutdown()"/> (routed to the real
/// teardown in <c>App.OnExit</c>); the second window closing as a consequence is a no-op, so there is no
/// double-shutdown. Production lifetime is untouched — this only runs behind the simulator gate.
/// Compiled out of Release.
/// </summary>
public static class SimulatorShutdown
{
    private static int _requested;

    /// <summary>Test seam: overrides the default <see cref="Application"/> shutdown so the idempotency
    /// guard can be exercised without a WPF <see cref="Application"/> instance.</summary>
    internal static Action? ShutdownActionForTests;

    /// <summary>
    /// Requests a one-time application shutdown. Returns <c>true</c> only for the first call (which fires
    /// the shutdown); subsequent calls return <c>false</c> and do nothing. Safe to call from any close
    /// handler on the UI thread.
    /// </summary>
    public static bool Request()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return false;
        }

        (ShutdownActionForTests ?? DefaultShutdown).Invoke();
        return true;
    }

    private static void DefaultShutdown()
    {
        var app = Application.Current;
        // Marshal to the UI thread; Shutdown() must run on the dispatcher and is safe to queue from it.
        app?.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
    }

    /// <summary>Test-only: clears the one-shot guard between cases.</summary>
    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _requested, 0);
        ShutdownActionForTests = null;
    }
}
#endif

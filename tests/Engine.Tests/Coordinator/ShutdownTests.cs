using AmbientFx.Bridge;
using AmbientFx.Hosting;
using AmbientFx.Models;
using Moq;
using Xunit;

namespace AmbientFx.Engine.Tests.Coordinator;

/// <summary>
/// Story 7.3: closing from the taskbar routes through CloseAction, Quit funnels into one
/// shutdown path, and ShutdownAsync is idempotent and tears everything down exactly once.
/// </summary>
public sealed class ShutdownTests
{
    private static void RaiseCloseRequested(CoordinatorHarness h) =>
        h.WindowManager.Raise(w => w.ControlWindowCloseRequested += null, EventArgs.Empty);

    // ------------------------------------------------------------- AC3/AC7 teardown

    [Fact]
    public async Task ShutdownAsync_TearsDownPipelineWindowsAndTray()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.InitialSettings.IsEnabled = true;
        h.InitialSettings.SourceMonitorId = CoordinatorHarness.Display1;
        h.InitialSettings.TargetMonitorIds = new List<string> { CoordinatorHarness.Display2 };
        await h.StartAsync();
        h.ClearRecordings();

        await h.Coordinator.ShutdownAsync();

        // Pipeline stopped.
        h.Capture.Verify(c => c.Stop(), Times.AtLeastOnce);
        h.Audio.Verify(a => a.Stop(), Times.AtLeastOnce);
        h.Processing.Verify(p => p.Stop(), Times.AtLeastOnce);
        // Monitor watching stopped; effect windows synced to empty; everything disposed.
        h.MonitorDetection.Verify(m => m.StopMonitoring(), Times.Once);
        Assert.Contains(h.Syncs, s => s.Count == 0);
        h.WindowManager.Verify(w => w.Dispose(), Times.Once);
        h.Tray.Verify(t => t.Dispose(), Times.Once);
        // Final settings flush happened.
        Assert.NotEmpty(h.Saved);
    }

    [Fact]
    public async Task ShutdownAsync_IsIdempotent_SecondCallIsANoOp()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        await h.StartAsync();

        await h.Coordinator.ShutdownAsync();
        await h.Coordinator.ShutdownAsync(); // double Quit (tray + window race, AC7)

        h.WindowManager.Verify(w => w.Dispose(), Times.Once);
        h.Tray.Verify(t => t.Dispose(), Times.Once);
        h.MonitorDetection.Verify(m => m.StopMonitoring(), Times.Once);
    }

    [Fact]
    public async Task ShutdownAsync_ConcurrentCalls_TearDownOnce()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        await h.StartAsync();

        await Task.WhenAll(h.Coordinator.ShutdownAsync(), h.Coordinator.ShutdownAsync());

        h.WindowManager.Verify(w => w.Dispose(), Times.Once);
        h.Tray.Verify(t => t.Dispose(), Times.Once);
    }

    // ------------------------------------------------------------- AC1 close routing

    [Fact]
    public async Task CloseRequested_WithAsk_ShowsPromptAndKeepsWindow()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true; // CloseAction defaults to "ask"
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        RaiseCloseRequested(h);

        Assert.Contains(h.ControlPosts, p => p.Type == MessageTypes.ClosePrompt);
        h.WindowManager.Verify(w => w.HideControlWindow(), Times.Never);
        Assert.False(quitRequested);
    }

    [Fact]
    public async Task CloseRequested_WithMinimizeToTray_HidesWithoutPromptOrQuit()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.InitialSettings.CloseAction = CloseActions.MinimizeToTray;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        RaiseCloseRequested(h);

        h.WindowManager.Verify(w => w.HideControlWindow(), Times.Once);
        Assert.DoesNotContain(h.ControlPosts, p => p.Type == MessageTypes.ClosePrompt);
        Assert.False(quitRequested);
    }

    [Fact]
    public async Task CloseRequested_WithQuit_RequestsShutdown()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.InitialSettings.CloseAction = CloseActions.Quit;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        RaiseCloseRequested(h);

        Assert.True(quitRequested);
        h.WindowManager.Verify(w => w.HideControlWindow(), Times.Never);
    }

    // ------------------------------------------------------------- AC2 quit commands

    [Fact]
    public async Task QuitAppCommand_RequestsShutdown()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        h.Send(CommandTypes.QuitApp);

        Assert.True(quitRequested);
    }

    [Fact]
    public async Task TrayExit_RequestsShutdown_SamePath()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        h.Tray.Raise(t => t.ExitRequested += null, h.Tray.Object, EventArgs.Empty);

        Assert.True(quitRequested);
    }

    // ------------------------------------------------------------- prompt resolution

    [Fact]
    public async Task ResolveClosePrompt_QuitWithRemember_PersistsAndShutsDown()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        h.Send(CommandTypes.ResolveClosePrompt, new { action = "quit", remember = true });

        Assert.True(quitRequested);
        CoordinatorHarness.WaitUntil(() => h.Saved.Any(s => s.CloseAction == CloseActions.Quit),
            because: "the remembered choice must persist");
    }

    [Fact]
    public async Task ResolveClosePrompt_TrayWithoutRemember_HidesAndKeepsAsk()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        h.Send(CommandTypes.ResolveClosePrompt, new { action = "minimizeToTray", remember = false });

        h.WindowManager.Verify(w => w.HideControlWindow(), Times.Once);
        Assert.False(quitRequested);
        Assert.Equal(CloseActions.Ask, h.InitialSettings.CloseAction);
    }

    [Fact]
    public async Task ResolveClosePrompt_BogusAction_IsIgnored()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        var quitRequested = false;
        h.Coordinator.ShutdownRequester = () => quitRequested = true;
        await h.StartAsync();

        h.Send(CommandTypes.ResolveClosePrompt, new { action = "explode", remember = true });

        Assert.False(quitRequested);
        h.WindowManager.Verify(w => w.HideControlWindow(), Times.Never);
        Assert.Equal(CloseActions.Ask, h.InitialSettings.CloseAction);
    }

    // ------------------------------------------------------------- setCloseAction

    [Fact]
    public async Task SetCloseAction_PersistsValidValues_RejectsUnknown()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        await h.StartAsync();

        h.Send(CommandTypes.SetCloseAction, new { action = "minimizeToTray" });
        Assert.Equal(CloseActions.MinimizeToTray, h.InitialSettings.CloseAction);

        h.Send(CommandTypes.SetCloseAction, new { action = "nonsense" });
        Assert.Equal(CloseActions.MinimizeToTray, h.InitialSettings.CloseAction); // unchanged
    }
}

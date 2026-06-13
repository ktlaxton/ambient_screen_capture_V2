using System.Net.Http;
using AmbientFx.Bridge;
using Moq;
using Xunit;

namespace AmbientFx.Engine.Tests.Coordinator;

/// <summary>
/// Story 7.4 AC6: launch-time + manual update checks route through IUpdateService and
/// surface results as toasts, without ever blocking or crashing startup.
/// </summary>
public sealed class UpdateCheckTests
{
    private static (string Level, string Message)[] Toasts(CoordinatorHarness h) =>
        h.ControlPosts.Where(p => p.Type == MessageTypes.Status)
            .Select(p => ((StatusPayload)p.Payload))
            .Select(s => (s.Level, s.Message))
            .ToArray();

    [Fact]
    public async Task Startup_WhenSupported_ChecksAndToastsOnlyWhenAnUpdateWasStaged()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.Updates.SetupGet(u => u.IsSupported).Returns(true);
        h.Updates.Setup(u => u.CheckAndStageAsync(It.IsAny<string>())).ReturnsAsync("2.1.0");

        await h.StartAsync();

        CoordinatorHarness.WaitUntil(
            () => Toasts(h).Any(t => t.Message.Contains("2.1.0")),
            because: "a staged update must be announced");
        h.Updates.Verify(u => u.CheckAndStageAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Startup_WhenSupported_UpToDate_StaysSilent()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.Updates.SetupGet(u => u.IsSupported).Returns(true);
        h.Updates.Setup(u => u.CheckAndStageAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        await h.StartAsync();

        CoordinatorHarness.WaitUntil(
            () => h.Updates.Invocations.Any(i => i.Method.Name == nameof(AmbientFx.Services.IUpdateService.CheckAndStageAsync)),
            because: "the launch check should still run");
        Assert.DoesNotContain(Toasts(h), t => t.Message.Contains("latest version"));
    }

    [Fact]
    public async Task Startup_WhenUnsupported_NeverChecks()
    {
        var h = new CoordinatorHarness(); // Updates.IsSupported defaults to false
        h.InitialSettings.FirstRunCompleted = true;

        await h.StartAsync();

        h.Updates.Verify(u => u.CheckAndStageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ManualCheck_UpToDate_ToastsConfirmation()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.Updates.SetupGet(u => u.IsSupported).Returns(false); // skip the launch check
        await h.StartAsync();
        h.Updates.SetupGet(u => u.IsSupported).Returns(true);
        h.Updates.Setup(u => u.CheckAndStageAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        h.Send(CommandTypes.CheckForUpdates);

        CoordinatorHarness.WaitUntil(
            () => Toasts(h).Any(t => t.Message.Contains("latest version")),
            because: "a manual check must confirm even when current");
    }

    [Fact]
    public async Task ManualCheck_FeedFailure_ToastsWarning_NeverThrows()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.Updates.SetupGet(u => u.IsSupported).Returns(false);
        await h.StartAsync();
        h.Updates.SetupGet(u => u.IsSupported).Returns(true);
        h.Updates.Setup(u => u.CheckAndStageAsync(It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("feed unreachable"));

        h.Send(CommandTypes.CheckForUpdates);

        CoordinatorHarness.WaitUntil(
            () => Toasts(h).Any(t => t.Level == "warn" && t.Message.Contains("Could not check")),
            because: "feed failures must surface as a warning toast");
    }

    [Fact]
    public async Task ManualCheck_OnUnpackagedBuild_ExplainsItself()
    {
        var h = new CoordinatorHarness(); // IsSupported = false
        h.InitialSettings.FirstRunCompleted = true;
        await h.StartAsync();

        h.Send(CommandTypes.CheckForUpdates);

        CoordinatorHarness.WaitUntil(
            () => Toasts(h).Any(t => t.Message.Contains("installed version")),
            because: "dev/portable runs must explain why updates are unavailable");
    }

    [Fact]
    public async Task ManualCheck_PassesTheConfiguredFeedUrl()
    {
        var h = new CoordinatorHarness();
        h.InitialSettings.FirstRunCompleted = true;
        h.InitialSettings.UpdateFeedUrl = "https://example.com/my-feed";
        h.Updates.SetupGet(u => u.IsSupported).Returns(false);
        await h.StartAsync();
        h.Updates.SetupGet(u => u.IsSupported).Returns(true);
        h.Updates.Setup(u => u.CheckAndStageAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        h.Send(CommandTypes.CheckForUpdates);

        CoordinatorHarness.WaitUntil(
            () => h.Updates.Invocations.Any(i =>
                i.Method.Name == nameof(AmbientFx.Services.IUpdateService.CheckAndStageAsync) &&
                Equals(i.Arguments[0], "https://example.com/my-feed")),
            because: "the configured feed URL must reach the update service");
    }
}

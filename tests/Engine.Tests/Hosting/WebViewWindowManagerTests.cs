using AmbientFx.Bridge;
using AmbientFx.Hosting;
using AmbientFx.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace AmbientFx.Engine.Tests.Hosting;

/// <summary>
/// Story 10.2 AC8: the <see cref="IEffectSurfaceHost"/> factory seam. With a fake surface factory and
/// the environment guard relaxed, the unmodified <see cref="WebViewWindowManager"/> sync logic is
/// exercised without any real WebView2/GPU — proving surfaces are created/repositioned/posted/closed
/// per monitor, and that the production <see cref="EffectWindow"/> still implements the contract.
/// </summary>
public sealed class WebViewWindowManagerTests
{
    private static MonitorInfo Mon(string id, int x = 0) =>
        new() { Id = id, Name = id, X = x, Y = 0, Width = 1920, Height = 1080 };

    private static EffectWindowSpec Spec(string id, string relation = "right", int x = 1920) => new()
    {
        Monitor = Mon(id, x),
        Config = new WindowConfigPayload { MonitorId = id, EffectId = "edge-glow", Monitor = Mon(id, x), Relation = relation },
    };

    private static WebViewWindowManager NewManager(FakeSurfaceFactory factory) =>
        new(NullLogger<WebViewWindowManager>.Instance, factory.Create) { AllowSurfaceCreationWithoutEnvironment = true };

    [Fact]
    public void EffectWindow_ImplementsIEffectSurfaceHost_ProductionContractUnchanged()
    {
        Assert.True(typeof(IEffectSurfaceHost).IsAssignableFrom(typeof(EffectWindow)),
            "the production EffectWindow must implement the surface host contract");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task Sync_CreatesOneSurfacePerSpec_ShowsAndInitializesEach(int count)
    {
        var factory = new FakeSurfaceFactory();
        var mgr = NewManager(factory);

        var specs = Enumerable.Range(0, count)
            .Select(i => Spec($"\\\\.\\SIM-DISPLAY{i}", x: i * 1920))
            .ToList();
        await mgr.SyncEffectWindowsAsync(specs);

        Assert.Equal(count, factory.Created.Count);
        Assert.All(factory.Created, s =>
        {
            Assert.True(s.Initialized, "each surface must be initialized");
            Assert.True(s.Shown, "each surface must be shown");
            Assert.Contains(s.Posts, json => json.Contains("windowConfig")); // handed its config envelope
        });
    }

    [Fact]
    public async Task Sync_PostsTheRealWindowConfigEnvelope_PerMonitor()
    {
        var factory = new FakeSurfaceFactory();
        var mgr = NewManager(factory);

        await mgr.SyncEffectWindowsAsync(new[] { Spec("\\\\.\\SIM-DISPLAY1", "left", 0) });

        var surface = Assert.Single(factory.Created);
        var config = Assert.Single(surface.Posts, p => p.Contains("windowConfig"));
        Assert.Contains("SIM-DISPLAY1", config); // the genuine monitor id from the payload (JSON-escaped)
        Assert.Contains("left", config);         // the genuine relation
    }

    [Fact]
    public async Task ReSync_ExistingSurface_IsRepositioned_AndReceivesFreshConfig()
    {
        var factory = new FakeSurfaceFactory();
        var mgr = NewManager(factory);

        await mgr.SyncEffectWindowsAsync(new[] { Spec("m", "right", 1920) });
        var surface = Assert.Single(factory.Created);
        int postsAfterCreate = surface.Posts.Count;

        // Same monitor id, moved/re-relationed — the manager keeps the surface and refreshes it.
        await mgr.SyncEffectWindowsAsync(new[] { Spec("m", "left", -1920) });

        Assert.Single(factory.Created); // no new surface created
        Assert.True(surface.RepositionCount >= 1, "an existing surface must be RepositionTo'd on re-sync");
        Assert.True(surface.Posts.Count > postsAfterCreate, "an existing surface must get the refreshed config");
        Assert.Contains(surface.Posts, p => p.Contains("left"));
    }

    [Fact]
    public async Task Sync_RemovedMonitors_AreClosed()
    {
        var factory = new FakeSurfaceFactory();
        var mgr = NewManager(factory);

        await mgr.SyncEffectWindowsAsync(new[] { Spec("a", "left", 0), Spec("b", "right", 3840) });
        Assert.Equal(2, factory.Created.Count);

        await mgr.SyncEffectWindowsAsync(new[] { Spec("a", "left", 0) }); // b removed

        var closed = Assert.Single(factory.Created, s => s.Closed);
        Assert.Equal("b", closed.MonitorId);
        Assert.False(factory.Created.Single(s => s.MonitorId == "a").Closed);
    }

    [Fact]
    public async Task Sync_EmptyList_ClosesAllSurfaces()
    {
        var factory = new FakeSurfaceFactory();
        var mgr = NewManager(factory);

        await mgr.SyncEffectWindowsAsync(new[] { Spec("a", "left", 0), Spec("b", "right", 3840) });
        await mgr.SyncEffectWindowsAsync(Array.Empty<EffectWindowSpec>());

        Assert.All(factory.Created, s => Assert.True(s.Closed));
    }

    // ── fakes ──────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeSurfaceFactory
    {
        public readonly List<FakeEffectSurface> Created = new();

        public IEffectSurfaceHost Create(MonitorInfo monitor)
        {
            var surface = new FakeEffectSurface(monitor);
            Created.Add(surface);
            return surface;
        }
    }

    private sealed class FakeEffectSurface : IEffectSurfaceHost
    {
        public string MonitorId { get; }
        public bool Initialized { get; private set; }
        public bool Shown { get; private set; }
        public bool Closed { get; private set; }
        public int RepositionCount { get; private set; }
        public MonitorInfo? LastReposition { get; private set; }
        public List<string> Posts { get; } = new();

        public event EventHandler<string>? BridgeMessageReceived;
        public event EventHandler? PageReady;

        public FakeEffectSurface(MonitorInfo monitor) => MonitorId = monitor.Id;

        public Task InitializeWebViewAsync(CoreWebView2Environment environment)
        {
            Initialized = true;
            PageReady?.Invoke(this, EventArgs.Empty); // mimic navigation completing → triggers config push
            return Task.CompletedTask;
        }

        public void RepositionTo(MonitorInfo monitor)
        {
            RepositionCount++;
            LastReposition = monitor;
        }

        public bool TryPostWebMessage(string json)
        {
            Posts.Add(json);
            return true;
        }

        public void Show() => Shown = true;

        public void Close() => Closed = true;

        // Kept to satisfy the interface; unused by these tests.
        public void RaiseBridgeMessage(string json) => BridgeMessageReceived?.Invoke(this, json);
    }
}

using System.Text.Json;
using AmbientFx.Bridge;
using AmbientFx.Capture;
using AmbientFx.Hosting;
using AmbientFx.Models;
using AmbientFx.Processing;
using AmbientFx.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AmbientFx.Engine.Tests.Coordinator;

/// <summary>
/// Shared fixture for EngineCoordinator tests: mocks every collaborator, records all
/// engine-to-web posts and effect-window syncs, and delivers bridge commands by raising
/// the window manager's CommandReceived event. Monitor topology: DISPLAY1 primary at
/// (0,0), DISPLAY2 to its left, DISPLAY3 to its right (all 1920x1080).
/// </summary>
internal sealed class CoordinatorHarness
{
    public const string Display1 = @"\\.\DISPLAY1";
    public const string Display2 = @"\\.\DISPLAY2";
    public const string Display3 = @"\\.\DISPLAY3";

    private readonly object _sync = new();

    public Mock<ISettingsService> Settings { get; } = new();
    public Mock<IMonitorDetectionService> MonitorDetection { get; } = new();
    public Mock<ISystemTrayService> Tray { get; } = new();
    public Mock<IAutostartService> Autostart { get; } = new();
    public Mock<IHotkeyService> Hotkeys { get; } = new();
    public Mock<IScreenCaptureService> Capture { get; } = new();
    public Mock<IAudioCaptureService> Audio { get; } = new();
    public Mock<IDataProcessingService> Processing { get; } = new();
    public Mock<IWebViewWindowManager> WindowManager { get; } = new();

    /// <summary>The instance LoadAsync hands to the coordinator; mutate before StartAsync.</summary>
    public ApplicationSettings InitialSettings { get; } = new();

    public EngineCoordinator Coordinator { get; }

    private readonly List<(string Type, object Payload)> _allPosts = new();
    private readonly List<(string Type, object Payload)> _controlPosts = new();
    private readonly List<(string MonitorId, string Type, object Payload)> _effectPosts = new();
    private readonly List<IReadOnlyList<EffectWindowSpec>> _syncs = new();
    private readonly List<ApplicationSettings> _saved = new();
    private ProcessingOptions? _lastProcessingOptions;

    public CoordinatorHarness()
    {
        Settings.Setup(s => s.LoadAsync()).ReturnsAsync(InitialSettings);
        Settings.Setup(s => s.GetDefaults()).Returns(() => new ApplicationSettings());
        Settings.Setup(s => s.SaveAsync(It.IsAny<ApplicationSettings>()))
            .Callback<ApplicationSettings>(s => { lock (_sync) { _saved.Add(s); } })
            .Returns(Task.CompletedTask);

        MonitorDetection.Setup(m => m.GetMonitors()).Returns(() => new List<MonitorInfo>
        {
            new() { Id = Display1, Name = "Primary", X = 0, Y = 0, Width = 1920, Height = 1080, IsPrimary = true },
            new() { Id = Display2, Name = "Left", X = -1920, Y = 0, Width = 1920, Height = 1080 },
            new() { Id = Display3, Name = "Right", X = 1920, Y = 0, Width = 1920, Height = 1080 },
        });

        // Moq would otherwise return null and crash the coordinator's failed-hotkey check.
        Hotkeys.Setup(h => h.Apply(It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Returns(Array.Empty<string>());

        WindowManager.Setup(w => w.InitializeAsync()).Returns(Task.CompletedTask);
        WindowManager.Setup(w => w.ShowControlWindowAsync()).Returns(Task.CompletedTask);
        WindowManager.Setup(w => w.SyncEffectWindowsAsync(It.IsAny<IReadOnlyList<EffectWindowSpec>>()))
            .Callback<IReadOnlyList<EffectWindowSpec>>(s => { lock (_sync) { _syncs.Add(s); } })
            .Returns(Task.CompletedTask);
        WindowManager.Setup(w => w.PostToAll(It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, object>((t, p) => { lock (_sync) { _allPosts.Add((t, p)); } });
        WindowManager.Setup(w => w.PostToControl(It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, object>((t, p) => { lock (_sync) { _controlPosts.Add((t, p)); } });
        WindowManager.Setup(w => w.PostToEffectWindow(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, string, object>((id, t, p) => { lock (_sync) { _effectPosts.Add((id, t, p)); } });

        Processing.Setup(p => p.UpdateOptions(It.IsAny<ProcessingOptions>()))
            .Callback<ProcessingOptions>(o => { lock (_sync) { _lastProcessingOptions = o; } });

        Coordinator = new EngineCoordinator(
            Settings.Object,
            MonitorDetection.Object,
            Tray.Object,
            Autostart.Object,
            Hotkeys.Object,
            Capture.Object,
            Audio.Object,
            Processing.Object,
            WindowManager.Object,
            NullLogger<EngineCoordinator>.Instance);
    }

    public Task StartAsync(bool minimized = true) => Coordinator.StartAsync(minimized);

    /// <summary>Builds a camelCase CommandEnvelope and raises CommandReceived on the window manager.</summary>
    public void Send(string type, object? payload = null, string sourceWindow = "control")
    {
        var envelope = new CommandEnvelope
        {
            Type = type,
            Payload = payload is null
                ? default
                : JsonSerializer.SerializeToElement(payload, BridgeJson.Options),
        };
        WindowManager.Raise(w => w.CommandReceived += null,
            new BridgeCommandEventArgs { Command = envelope, SourceWindow = sourceWindow });
    }

    // Thread-safe snapshots of everything the coordinator pushed.
    public (string Type, object Payload)[] AllPosts { get { lock (_sync) { return _allPosts.ToArray(); } } }
    public (string Type, object Payload)[] ControlPosts { get { lock (_sync) { return _controlPosts.ToArray(); } } }
    public (string MonitorId, string Type, object Payload)[] EffectPosts { get { lock (_sync) { return _effectPosts.ToArray(); } } }
    public IReadOnlyList<EffectWindowSpec>[] Syncs { get { lock (_sync) { return _syncs.ToArray(); } } }
    public ApplicationSettings[] Saved { get { lock (_sync) { return _saved.ToArray(); } } }
    public ProcessingOptions? LastProcessingOptions { get { lock (_sync) { return _lastProcessingOptions; } } }

    /// <summary>The settings snapshot inside the most recent PostToAll("config", ...) push.</summary>
    public ConfigPayload LastConfig =>
        (ConfigPayload)AllPosts.Last(p => p.Type == MessageTypes.Config).Payload;

    public int CaptureStopCount =>
        Capture.Invocations.Count(i => i.Method.Name == nameof(IScreenCaptureService.Stop));

    public void ClearRecordings()
    {
        lock (_sync)
        {
            _allPosts.Clear();
            _controlPosts.Clear();
            _effectPosts.Clear();
            _syncs.Clear();
            _saved.Clear();
        }
    }

    /// <summary>Polls (no fixed sleeps) until the condition holds or the timeout elapses.</summary>
    public static void WaitUntil(Func<bool> condition, int timeoutMs = 2000, string? because = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Condition not met within {timeoutMs} ms{(because is null ? string.Empty : ": " + because)}");
            }
            Thread.Sleep(15);
        }
    }
}

/// <summary>
/// Provides a real WPF Application + running dispatcher on a background STA thread so the
/// coordinator's Application.Current.Dispatcher marshaling paths (pipeline errors) execute
/// under test. Created at most once per process; the background thread dies with the process.
/// </summary>
internal static class UiApp
{
    private static readonly Lazy<bool> Init = new(() =>
    {
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            if (System.Windows.Application.Current is null)
            {
                _ = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                };
            }
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "TestUiDispatcher";
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("Test UI dispatcher failed to start");
        }
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void EnsureRunning() => _ = Init.Value;
}

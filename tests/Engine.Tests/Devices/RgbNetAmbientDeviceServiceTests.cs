using AmbientFx.Bridge;
using AmbientFx.Devices;
using AmbientFx.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LedPoint = AmbientFx.Devices.LedProjection.LedPoint;

namespace AmbientFx.Engine.Tests.Devices;

/// <summary>
/// Lifecycle tests for <see cref="RgbNetAmbientDeviceService"/> against a fake backend
/// (Story 8.1 AC9 — no hardware, no native SDK in CI): async connect, state surfacing for
/// every unavailability mode, frame projection + rate-limited pushes, release-on-stop,
/// per-device placement (8.2), and provider selection + audio reactivity (8.3).
/// </summary>
public sealed class RgbNetAmbientDeviceServiceTests : IDisposable
{
    private readonly FakeBackend _backend = new();
    private readonly RgbNetAmbientDeviceService _service;
    private Func<IReadOnlyCollection<string>, IRgbDeviceBackend> _factory;
    private IReadOnlyCollection<string>? _requestedProviders;

    public RgbNetAmbientDeviceServiceTests()
    {
        _factory = _ => _backend;
        _service = new RgbNetAmbientDeviceService(
            providers =>
            {
                _requestedProviders = providers;
                return _factory(providers);
            },
            NullLogger<RgbNetAmbientDeviceService>.Instance);
    }

    public void Dispose() => _service.Dispose();

    private static EdgeColors MakeEdges(int seed = 0) => new()
    {
        Top = new[] { new[] { 200 + seed, 0, 0 }, new[] { 210 + seed, 0, 0 } },
        Bottom = new[] { new[] { 0, 200 + seed, 0 }, new[] { 0, 210 + seed, 0 } },
        Left = new[] { new[] { 0, 0, 200 + seed }, new[] { 0, 0, 210 + seed } },
        Right = new[] { new[] { 200 + seed, 200 + seed, 0 }, new[] { 210 + seed, 210 + seed, 0 } },
    };

    private static FramePayload MakeFrame(int seed = 0, float audioIntensity = 0f) => new()
    {
        Edges = MakeEdges(seed),
        Audio = new AudioData { Intensity = audioIntensity },
    };

    private static RgbBackendConnection ConnectedWithOneDevice() => new()
    {
        State = DeviceConnectionStates.Connected,
        Devices = new[]
        {
            new RgbBackendDevice
            {
                Id = "0:Test Keyboard",
                Name = "Test Keyboard",
                Type = "Keyboard",
                NormalizedLeds = new[] { new LedPoint(0.0, 0.5), new LedPoint(0.5, 0.0) },
            },
        },
        Providers = new[]
        {
            new RgbProviderStatus
            {
                Key = "corsair",
                Name = "Corsair iCUE",
                State = RgbProviderStates.Connected,
                DeviceCount = 1,
            },
        },
    };

    /// <summary>Polls (no fixed sleeps) until the condition holds or the timeout elapses.</summary>
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 2000, string? because = null)
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

    private void StartConnected()
    {
        _backend.ConnectResult = ConnectedWithOneDevice();
        _service.Start();
        WaitUntil(() => _service.Snapshot.ConnectionState == DeviceConnectionStates.Connected);
    }

    [Fact]
    public void Starts_disabled_with_no_devices()
    {
        Assert.Equal(DeviceConnectionStates.Disabled, _service.Snapshot.ConnectionState);
        Assert.Empty(_service.Snapshot.Devices);
        Assert.Empty(_service.Snapshot.Providers);
    }

    [Fact]
    public void Start_connects_asynchronously_and_reports_devices()
    {
        _backend.ConnectResult = ConnectedWithOneDevice();
        int stateChanges = 0;
        _service.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        _service.Start();

        WaitUntil(() => _service.Snapshot.ConnectionState == DeviceConnectionStates.Connected);
        var device = Assert.Single(_service.Snapshot.Devices);
        Assert.Equal("Test Keyboard", device.Name);
        Assert.Equal("Keyboard", device.Type);
        Assert.Equal(2, device.LedCount);
        Assert.True(stateChanges >= 2, "expected connecting + connected notifications");
    }

    [Fact]
    public void Frames_are_projected_per_led_and_pushed_to_the_backend()
    {
        StartConnected();

        var frame = MakeFrame();
        _service.SubmitFrame(frame);

        WaitUntil(() => _backend.Applied.Length >= 1, because: "a frame should be pushed");
        var deviceColors = Assert.Single(_backend.Applied[0]);
        Assert.Equal("0:Test Keyboard", deviceColors.DeviceId);
        Assert.Equal(
            LedProjection.Project(ConnectedWithOneDevice().Devices[0].NormalizedLeds, frame.Edges, 1f),
            deviceColors.Colors);
    }

    [Fact]
    public void An_unchanged_frame_is_not_repushed()
    {
        StartConnected();

        _service.SubmitFrame(MakeFrame());
        WaitUntil(() => _backend.Applied.Length == 1);

        Thread.Sleep(150); // several timer ticks
        Assert.Single(_backend.Applied);

        _service.SubmitFrame(MakeFrame(seed: 9)); // a new frame flows again
        WaitUntil(() => _backend.Applied.Length == 2);
    }

    [Fact]
    public void Brightness_change_repushes_the_current_frame_scaled()
    {
        StartConnected();

        var frame = MakeFrame();
        _service.SubmitFrame(frame);
        WaitUntil(() => _backend.Applied.Length == 1);

        _service.Brightness = 0.5f;
        WaitUntil(() => _backend.Applied.Length == 2, because: "brightness change should re-push");
        Assert.Equal(
            LedProjection.Project(ConnectedWithOneDevice().Devices[0].NormalizedLeds, frame.Edges, 0.5f),
            _backend.Applied[1][0].Colors);
    }

    [Fact]
    public void Brightness_is_clamped_and_nan_repaired()
    {
        _service.Brightness = 7f;
        Assert.Equal(1f, _service.Brightness);
        _service.Brightness = -2f;
        Assert.Equal(0f, _service.Brightness);
        _service.Brightness = float.NaN;
        Assert.Equal(1f, _service.Brightness);
    }

    [Fact]
    public void Stop_releases_the_backend_and_returns_to_disabled()
    {
        StartConnected();

        _service.Stop();

        Assert.Equal(1, _backend.DisposeCount); // disconnect = the vendor resumes its profiles (AC5)
        Assert.Equal(DeviceConnectionStates.Disabled, _service.Snapshot.ConnectionState);
        Assert.Empty(_service.Snapshot.Devices);
        Assert.Empty(_service.Snapshot.Providers);
    }

    [Theory]
    [InlineData(DeviceConnectionStates.IcueNotFound)]
    [InlineData(DeviceConnectionStates.Refused)]
    [InlineData(DeviceConnectionStates.NoDevices)]
    public void Unavailability_is_surfaced_as_a_state_not_an_exception(string state)
    {
        _backend.ConnectResult = new RgbBackendConnection { State = state };

        _service.Start();

        WaitUntil(() => _service.Snapshot.ConnectionState == state);
        Assert.Empty(_service.Snapshot.Devices);
        Assert.Equal(1, _backend.DisposeCount); // an unusable session is not held open
    }

    [Fact]
    public void Connected_with_zero_devices_maps_to_noDevices()
    {
        _backend.ConnectResult = new RgbBackendConnection
        {
            State = DeviceConnectionStates.Connected,
            Devices = Array.Empty<RgbBackendDevice>(),
        };

        _service.Start();

        WaitUntil(() => _service.Snapshot.ConnectionState == DeviceConnectionStates.NoDevices);
    }

    [Fact]
    public void A_throwing_backend_factory_yields_the_error_state()
    {
        _factory = _ => throw new InvalidOperationException("native SDK exploded");

        _service.Start();

        WaitUntil(() => _service.Snapshot.ConnectionState == DeviceConnectionStates.Error);
    }

    [Fact]
    public void Stop_during_connect_discards_the_late_session()
    {
        _backend.ConnectResult = ConnectedWithOneDevice();
        _backend.ConnectGate = new ManualResetEventSlim(false);

        _service.Start();
        _service.Stop(); // user toggles off while the SDK is still handshaking
        _backend.ConnectGate.Set();

        WaitUntil(() => _backend.DisposeCount == 1, because: "the stale session must be released");
        Assert.Equal(DeviceConnectionStates.Disabled, _service.Snapshot.ConnectionState);
        Assert.Empty(_backend.Applied);
    }

    // ------------------------------------------------------ placement (8.2) --

    [Fact]
    public void Placements_apply_per_device_and_repush_live()
    {
        StartConnected();

        var frame = MakeFrame();
        _service.SubmitFrame(frame);
        WaitUntil(() => _backend.Applied.Length == 1);

        // Anchoring the device "left" must re-project the SAME frame without a reconnect (AC1/AC3).
        _service.SetPlacements(new Dictionary<string, DevicePlacement>
        {
            ["0:Test Keyboard"] = new() { Anchor = DeviceAnchors.Left },
        });

        WaitUntil(() => _backend.Applied.Length == 2, because: "placement change should re-push");
        Assert.Equal(
            LedProjection.Project(
                ConnectedWithOneDevice().Devices[0].NormalizedLeds, frame.Edges, 1f, DeviceAnchors.Left, false),
            _backend.Applied[1][0].Colors);
    }

    [Fact]
    public void Per_device_brightness_stacks_on_the_global_brightness()
    {
        _service.Brightness = 0.5f;
        _service.SetPlacements(new Dictionary<string, DevicePlacement>
        {
            ["0:Test Keyboard"] = new() { Brightness = 0.5f },
        });
        StartConnected();

        var frame = MakeFrame();
        _service.SubmitFrame(frame);

        WaitUntil(() => _backend.Applied.Length == 1);
        Assert.Equal(
            LedProjection.Project(ConnectedWithOneDevice().Devices[0].NormalizedLeds, frame.Edges, 0.25f),
            _backend.Applied[0][0].Colors);
    }

    [Fact]
    public void An_excluded_device_goes_dark_instead_of_freezing()
    {
        _service.SetPlacements(new Dictionary<string, DevicePlacement>
        {
            ["0:Test Keyboard"] = new() { Enabled = false },
        });
        StartConnected();

        _service.SubmitFrame(MakeFrame());

        WaitUntil(() => _backend.Applied.Length == 1);
        Assert.All(_backend.Applied[0][0].Colors, c => Assert.Equal(new[] { 0, 0, 0 }, c));
    }

    // ------------------------------------------- providers + audio (8.3) --

    [Fact]
    public void Enabled_providers_are_passed_to_the_backend_factory()
    {
        _service.SetEnabledProviders(new[] { "corsair", "razer" });
        StartConnected();

        Assert.NotNull(_requestedProviders);
        Assert.Equal(new[] { "corsair", "razer" }, _requestedProviders!.ToArray());
    }

    [Fact]
    public void Provider_statuses_flow_into_the_snapshot()
    {
        StartConnected();

        var provider = Assert.Single(_service.Snapshot.Providers);
        Assert.Equal("corsair", provider.Key);
        Assert.Equal(RgbProviderStates.Connected, provider.State);
        Assert.Equal(1, provider.DeviceCount);
    }

    [Fact]
    public void Audio_reactive_layer_modulates_the_pushed_brightness()
    {
        _service.AudioReactiveEnabled = true;
        _service.AudioReactiveDepth = 1f; // silence = dark, full beat = full brightness
        StartConnected();

        var quiet = MakeFrame(audioIntensity: 0f);
        _service.SubmitFrame(quiet);
        WaitUntil(() => _backend.Applied.Length == 1);
        Assert.All(_backend.Applied[0][0].Colors, c => Assert.Equal(new[] { 0, 0, 0 }, c));

        var loud = MakeFrame(seed: 9, audioIntensity: 1f);
        _service.SubmitFrame(loud);
        WaitUntil(() => _backend.Applied.Length == 2);
        Assert.Equal(
            LedProjection.Project(ConnectedWithOneDevice().Devices[0].NormalizedLeds, loud.Edges, 1f),
            _backend.Applied[1][0].Colors);
    }

    [Fact]
    public void Audio_reactive_composes_with_global_and_per_device_brightness()
    {
        _service.AudioReactiveEnabled = true;
        _service.AudioReactiveDepth = 0.5f;
        _service.Brightness = 0.8f;
        StartConnected();

        var frame = MakeFrame(audioIntensity: 0.5f); // factor = 1 - 0.5 + 0.5*0.5 = 0.75
        _service.SubmitFrame(frame);

        WaitUntil(() => _backend.Applied.Length == 1);
        Assert.Equal(
            LedProjection.Project(ConnectedWithOneDevice().Devices[0].NormalizedLeds, frame.Edges, 0.8f * 0.75f),
            _backend.Applied[0][0].Colors);
    }

    [Fact]
    public void Toggling_audio_reactive_repushes_the_current_frame()
    {
        StartConnected();
        _service.SubmitFrame(MakeFrame(audioIntensity: 0f));
        WaitUntil(() => _backend.Applied.Length == 1);

        _service.AudioReactiveDepth = 1f;
        _service.AudioReactiveEnabled = true;

        // A timer tick can land between the two property sets, so wait for the final
        // (fully dark) re-push rather than asserting on a fixed push count.
        WaitUntil(
            () => _backend.Applied.Length >= 2
                && _backend.Applied[^1][0].Colors.All(c => c.SequenceEqual(new[] { 0, 0, 0 })),
            because: "the audio toggle should re-push the current frame fully dark");
    }

    // ----------------------------------------------------------- lifecycle --

    [Fact]
    public void SubmitFrame_and_stop_are_safe_when_never_started()
    {
        _service.SubmitFrame(MakeFrame());
        _service.Stop();
        _service.Stop();
        Assert.Equal(DeviceConnectionStates.Disabled, _service.Snapshot.ConnectionState);
    }

    [Fact]
    public void Dispose_stops_and_blocks_restart()
    {
        StartConnected();

        _service.Dispose();
        Assert.Equal(1, _backend.DisposeCount);

        _service.Start(); // must be a no-op after dispose
        Thread.Sleep(80);
        Assert.Equal(DeviceConnectionStates.Disabled, _service.Snapshot.ConnectionState);
    }

    /// <summary>Hand-rolled fake: records Apply calls, counts disposes, optional connect gate.</summary>
    private sealed class FakeBackend : IRgbDeviceBackend
    {
        private readonly object _sync = new();
        private readonly List<IReadOnlyList<RgbDeviceColors>> _applied = new();
        private int _disposeCount;

        public RgbBackendConnection ConnectResult { get; set; } =
            new() { State = DeviceConnectionStates.Error };

        /// <summary>When set, Connect blocks until signaled (simulates a slow SDK handshake).</summary>
        public ManualResetEventSlim? ConnectGate { get; set; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IReadOnlyList<RgbDeviceColors>[] Applied
        {
            get { lock (_sync) { return _applied.ToArray(); } }
        }

        public RgbBackendConnection Connect()
        {
            ConnectGate?.Wait(TimeSpan.FromSeconds(5));
            return ConnectResult;
        }

        public void Apply(IReadOnlyList<RgbDeviceColors> frame)
        {
            lock (_sync) { _applied.Add(frame); }
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}

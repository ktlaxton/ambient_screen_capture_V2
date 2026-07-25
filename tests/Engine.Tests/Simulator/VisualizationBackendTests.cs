#if SIMULATOR_ENABLED
using AmbientFx.Bridge;
using AmbientFx.Devices;
using AmbientFx.Models;
using AmbientFx.Simulator.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.4 AC7: the <see cref="VisualizationBackend"/> records exactly the per-LED colors the real
/// <see cref="RgbNetAmbientDeviceService"/> + <see cref="LedProjection"/> produce — the fidelity proof
/// that the simulator re-reads, never recomputes, the projection.
/// </summary>
public sealed class VisualizationBackendTests
{
    private const string Keyboard = SimDevices.KeyboardId;

    private static EdgeColors MakeEdges() => new()
    {
        Top = new[] { new[] { 200, 0, 0 }, new[] { 210, 0, 0 } },
        Bottom = new[] { new[] { 0, 200, 0 }, new[] { 0, 210, 0 } },
        Left = new[] { new[] { 0, 0, 200 }, new[] { 0, 0, 210 } },
        Right = new[] { new[] { 200, 200, 0 }, new[] { 210, 210, 0 } },
    };

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("condition not met in time");
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Drives the real device service wired to a VisualizationBackend (exactly as the composition does),
    /// applies the placement, submits the frame, and returns the backend once a push has landed.
    /// </summary>
    private static (VisualizationBackend Backend, RgbNetAmbientDeviceService Service) Run(
        DevicePlacement? keyboardPlacement, EdgeColors edges, float brightness = 1f)
    {
        var backend = new VisualizationBackend();
        var service = new RgbNetAmbientDeviceService(_ => backend, NullLogger<RgbNetAmbientDeviceService>.Instance)
        {
            Brightness = brightness,
            AudioReactiveEnabled = false, // isolate color math from the audio layer
        };
        if (keyboardPlacement is not null)
        {
            service.SetPlacements(new Dictionary<string, DevicePlacement> { [Keyboard] = keyboardPlacement });
        }
        service.SubmitFrame(new FramePayload { Edges = edges, Audio = new AudioData { Intensity = 0f } });
        service.Start();
        WaitUntil(() => backend.ColorsFor(Keyboard) is not null);
        return (backend, service);
    }

    private static void AssertRecordedEqualsProjection(
        VisualizationBackend backend, EdgeColors edges, string? anchor, bool flip, float effective)
    {
        var device = backend.Devices.First(d => d.Id == Keyboard);
        int[][] expected = LedProjection.Project(device.NormalizedLeds, edges, effective, anchor, flip);
        int[][]? recorded = backend.ColorsFor(Keyboard);

        Assert.NotNull(recorded);
        Assert.Equal(expected.Length, recorded!.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], recorded[i]);
        }
    }

    [Fact]
    public void Records_AutoAnchor_MatchesNearestEdgeProjection()
    {
        var edges = MakeEdges();
        var (backend, service) = Run(keyboardPlacement: null, edges);
        try
        {
            // No placement entry => the service passes anchor null (nearest-edge), brightness 1.
            AssertRecordedEqualsProjection(backend, edges, anchor: null, flip: false, effective: 1f);
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public void Records_LeftAnchor_MatchesSingleEdgeProjection()
    {
        var edges = MakeEdges();
        var (backend, service) = Run(new DevicePlacement { Anchor = DeviceAnchors.Left }, edges);
        try
        {
            AssertRecordedEqualsProjection(backend, edges, DeviceAnchors.Left, flip: false, effective: 1f);
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public void Records_SurroundAnchor_Flipped_MatchesProjection()
    {
        var edges = MakeEdges();
        var (backend, service) = Run(new DevicePlacement { Anchor = DeviceAnchors.Surround, Flip = true }, edges);
        try
        {
            AssertRecordedEqualsProjection(backend, edges, DeviceAnchors.Surround, flip: true, effective: 1f);
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public void Records_PerDeviceBrightness_ScalesTheColors()
    {
        var edges = MakeEdges();
        var (backend, service) = Run(new DevicePlacement { Anchor = DeviceAnchors.Right, Brightness = 0.5f }, edges);
        try
        {
            // effective = global(1) * perDevice(0.5) * audio(1) = 0.5
            AssertRecordedEqualsProjection(backend, edges, DeviceAnchors.Right, flip: false, effective: 0.5f);
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public void Records_DisabledDevice_GoesDark()
    {
        var edges = MakeEdges();
        var (backend, service) = Run(new DevicePlacement { Enabled = false }, edges);
        try
        {
            int[][]? recorded = backend.ColorsFor(Keyboard);
            Assert.NotNull(recorded);
            Assert.All(recorded!, led => Assert.Equal(new[] { 0, 0, 0 }, led));
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public void Connect_ReturnsTheSimDeviceSet()
    {
        var backend = new VisualizationBackend();
        var connection = backend.Connect();

        Assert.Equal(DeviceConnectionStates.Connected, connection.State);
        Assert.Equal(3, connection.Devices.Count);
        Assert.Equal(108, connection.Devices.First(d => d.Id == SimDevices.KeyboardId).NormalizedLeds.Length);
        Assert.Equal(4, connection.Devices.First(d => d.Id == SimDevices.MouseId).NormalizedLeds.Length);
        Assert.Equal(27, connection.Devices.First(d => d.Id == SimDevices.StripId).NormalizedLeds.Length);
        Assert.Contains(connection.Providers, p => p.Key == "corsair" && p.State == RgbProviderStates.Connected);
    }
}
#endif

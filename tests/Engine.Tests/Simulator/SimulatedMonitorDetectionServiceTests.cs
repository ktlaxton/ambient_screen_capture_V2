#if SIMULATOR_ENABLED
using AmbientFx.Models;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Coverage for <see cref="SimulatedMonitorDetectionService"/> (Story 10.1 AC1/AC2/AC10): fixture
/// load reproduces SIM_MONITORS, fresh snapshots, the mutation API, and the off-thread MonitorsChanged.
/// </summary>
public sealed class SimulatedMonitorDetectionServiceTests
{
    private const string D1 = @"\\.\SIM-DISPLAY1";
    private const string D2 = @"\\.\SIM-DISPLAY2";
    private const string D3 = @"\\.\SIM-DISPLAY3";

    private static SimulatedMonitorDetectionService FromDefault() =>
        new(NullLogger<SimulatedMonitorDetectionService>.Instance,
            SimulatorScenario.LoadDefault(NullLogger.Instance));

    [Fact]
    public void LoadDefault_ReproducesSimMonitors_WithSentinelHandles()
    {
        using var svc = FromDefault();

        var monitors = svc.GetMonitors();

        Assert.Equal(3, monitors.Count);
        Assert.Single(monitors, m => m.IsPrimary); // exactly one primary

        var d1 = Assert.Single(monitors, m => m.Id == D1);
        Assert.True(d1.IsPrimary);
        Assert.Equal((0, 0, 2560, 1440), (d1.X, d1.Y, d1.Width, d1.Height));

        var d2 = Assert.Single(monitors, m => m.Id == D2);
        Assert.False(d2.IsPrimary);
        Assert.Equal((2560, 180, 1920, 1080), (d2.X, d2.Y, d2.Width, d2.Height));

        var d3 = Assert.Single(monitors, m => m.Id == D3);
        Assert.Equal((320, -1080, 1920, 1080), (d3.X, d3.Y, d3.Width, d3.Height)); // negative Y is valid

        Assert.All(monitors, m => Assert.Equal(SimulatedMonitorDetectionService.SentinelHMonitor, m.HMonitor));
    }

    [Fact]
    public void GetMonitors_ReturnsAFreshSnapshotEachCall()
    {
        using var svc = FromDefault();

        var a = svc.GetMonitors();
        var b = svc.GetMonitors();

        Assert.False(ReferenceEquals(a, b));
        Assert.False(ReferenceEquals(a[0], b[0]));

        a[0].Width = 1; // mutating a returned snapshot must not leak into the next call
        Assert.NotEqual(1, svc.GetMonitors().First(m => m.Id == a[0].Id).Width);
    }

    [Fact]
    public void AddMonitor_AppearsInSubsequentSnapshot_WithSentinelHandle()
    {
        using var svc = FromDefault();

        svc.AddMonitor(new MonitorInfo { Id = @"\\.\SIM-DISPLAY9", Name = "Added", X = 5000, Y = 0, Width = 1280, Height = 720 });

        var added = Assert.Single(svc.GetMonitors(), m => m.Id == @"\\.\SIM-DISPLAY9");
        Assert.Equal(1280, added.Width);
        Assert.Equal(SimulatedMonitorDetectionService.SentinelHMonitor, added.HMonitor);
    }

    [Fact]
    public void RemoveMonitor_DropsItById()
    {
        using var svc = FromDefault();

        Assert.True(svc.RemoveMonitor(D3));
        Assert.DoesNotContain(svc.GetMonitors(), m => m.Id == D3);
        Assert.False(svc.RemoveMonitor(D3)); // already gone
    }

    [Fact]
    public void SetResolution_ChangesSubsequentSnapshot()
    {
        using var svc = FromDefault();

        Assert.True(svc.SetResolution(D2, 3840, 2160));

        var d2 = Assert.Single(svc.GetMonitors(), m => m.Id == D2);
        Assert.Equal((3840, 2160), (d2.Width, d2.Height));
    }

    [Fact]
    public void SetOrientation_PortraitSwapsWidthHeight_AndIsIdempotent()
    {
        using var svc = FromDefault();

        Assert.True(svc.SetOrientation(D2, portrait: true));
        var portrait = Assert.Single(svc.GetMonitors(), m => m.Id == D2);
        Assert.True(portrait.Height > portrait.Width);
        Assert.Equal((1080, 1920), (portrait.Width, portrait.Height));

        svc.SetOrientation(D2, portrait: true); // already portrait — no change
        Assert.Equal((1080, 1920), GetWh(svc, D2));

        svc.SetOrientation(D2, portrait: false); // back to landscape
        Assert.Equal((1920, 1080), GetWh(svc, D2));
    }

    [Fact]
    public void FireMonitorsChanged_RaisesEventOffTheCallingThread()
    {
        using var svc = FromDefault();
        int callingThread = Environment.CurrentManagedThreadId;
        int raisedThread = callingThread;
        var raised = new ManualResetEventSlim(false);

        svc.MonitorsChanged += (_, _) =>
        {
            raisedThread = Environment.CurrentManagedThreadId;
            raised.Set();
        };

        svc.StartMonitoring();
        svc.FireMonitorsChanged();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "MonitorsChanged was not raised");
        Assert.NotEqual(callingThread, raisedThread);
    }

    [Fact]
    public void StartStopMonitoring_AndDispose_AreIdempotent_AndNeverThrow()
    {
        var svc = FromDefault();
        var exception = Record.Exception(() =>
        {
            svc.StopMonitoring();   // stop before start
            svc.StartMonitoring();
            svc.StartMonitoring();  // double start
            svc.StopMonitoring();
            svc.Dispose();
            svc.Dispose();          // double dispose
            svc.FireMonitorsChanged(); // after dispose — no-op
        });

        Assert.Null(exception);
    }

    [Fact]
    public void AddMonitorAtDefault_Sized_AddsAtRightEdgeWithRequestedResolution()
    {
        using var svc = FromDefault();
        int rightEdge = svc.GetMonitors().Max(m => m.X + m.Width);

        string id = svc.AddMonitorAtDefault(3840, 2160);

        var added = svc.GetMonitors().Single(m => m.Id == id);
        Assert.Equal((rightEdge, 0, 3840, 2160), (added.X, added.Y, added.Width, added.Height));
        Assert.False(added.IsPrimary);

        // Non-positive dimensions coerce to 1080p; the parameterless overload delegates.
        string coercedId = svc.AddMonitorAtDefault(-5, 0);
        Assert.Equal((1920, 1080), GetWh(svc, coercedId));
        string plainId = svc.AddMonitorAtDefault();
        Assert.Equal((1920, 1080), GetWh(svc, plainId));
    }

    private static (int Width, int Height) GetWh(SimulatedMonitorDetectionService svc, string id)
    {
        var m = svc.GetMonitors().First(x => x.Id == id);
        return (m.Width, m.Height);
    }
}
#endif

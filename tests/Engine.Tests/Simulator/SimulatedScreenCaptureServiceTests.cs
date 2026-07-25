#if SIMULATOR_ENABLED
using AmbientFx.Capture;
using AmbientFx.Models;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Plumbing coverage for <see cref="SimulatedScreenCaptureService"/> (Story 10.1 AC3/AC5/AC10):
/// frame contract, buffer reuse, resolution-follows-source, source switch, maxFps bound, and the
/// never-throws (NFR5) posture. Exact pattern bytes are covered by <see cref="SyntheticPatternsTests"/>.
/// </summary>
public sealed class SimulatedScreenCaptureServiceTests
{
    private static SimulatedScreenCaptureService NewService() =>
        new(NullLogger<SimulatedScreenCaptureService>.Instance);

    private static MonitorInfo Monitor(string id, int w, int h) =>
        new() { Id = id, Name = id, Width = w, Height = h, HMonitor = SimulatedMonitorDetectionService.SentinelHMonitor };

    /// <summary>Captures the first emitted frame as an independent copy (consumed synchronously per contract).</summary>
    private static (byte[] Pixels, int Width, int Height) CaptureFirstFrame(SimulatedScreenCaptureService svc, MonitorInfo monitor)
    {
        var done = new ManualResetEventSlim(false);
        byte[] copy = Array.Empty<byte>();
        int w = 0, h = 0;

        void Handler(object? _, ScreenFrameEventArgs e)
        {
            if (done.IsSet)
            {
                return;
            }
            copy = (byte[])e.PixelsBgra.Clone();
            w = e.Width;
            h = e.Height;
            done.Set();
        }

        svc.FrameCaptured += Handler;
        try
        {
            svc.Start(monitor);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "no frame was emitted within 5s");
        }
        finally
        {
            svc.FrameCaptured -= Handler;
        }
        return (copy, w, h);
    }

    [Fact]
    public void Start_EmitsFrameAtSourceResolution_TightlyPackedTopDownBgra()
    {
        using var svc = NewService();

        var (pixels, w, h) = CaptureFirstFrame(svc, Monitor("m", 4, 3));

        Assert.Equal(4, w);
        Assert.Equal(3, h);
        Assert.Equal(4 * 3 * 4, pixels.Length); // tight pack: stride == width*4, no padding
        for (int i = 3; i < pixels.Length; i += 4)
        {
            Assert.Equal((byte)255, pixels[i]); // opaque alpha at every pixel
        }
        svc.Stop();
    }

    [Fact]
    public void Start_BarsPattern_EmitsBgraChannelOrder()
    {
        using var svc = NewService();
        svc.ConfigureMonitor("m", SyntheticPatterns.Bars, maxFps: 30);

        var (pixels, w, _) = CaptureFirstFrame(svc, Monitor("m", 16, 1));

        // Bar 0 = white.
        Assert.Equal((byte)255, pixels[0]);
        Assert.Equal((byte)255, pixels[1]);
        Assert.Equal((byte)255, pixels[2]);
        // Bar 5 (x=10) = red -> B=0, G=0, R=255 (would be 255,0,0 if it were RGBA).
        int p = (10) * 4;
        Assert.Equal((byte)0, pixels[p]);
        Assert.Equal((byte)0, pixels[p + 1]);
        Assert.Equal((byte)255, pixels[p + 2]);
        Assert.Equal(16, w);
        svc.Stop();
    }

    [Fact]
    public void Start_ReusesTheSameBufferAcrossFrames()
    {
        using var svc = NewService();
        var refs = new List<byte[]>();
        var done = new ManualResetEventSlim(false);

        void Handler(object? _, ScreenFrameEventArgs e)
        {
            lock (refs)
            {
                if (refs.Count < 2)
                {
                    refs.Add(e.PixelsBgra);
                }
                if (refs.Count >= 2)
                {
                    done.Set();
                }
            }
        }

        svc.FrameCaptured += Handler;
        svc.Start(Monitor("m", 8, 8));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "expected at least two frames");
        svc.Stop();

        Assert.True(ReferenceEquals(refs[0], refs[1]), "the BGRA buffer must be reused across frames");
    }

    [Fact]
    public void Start_WhileCapturing_SwitchesMonitorAndResizesBuffer()
    {
        using var svc = NewService();

        var first = CaptureFirstFrame(svc, Monitor("a", 4, 3));
        Assert.Equal(4 * 3 * 4, first.Pixels.Length);

        var second = CaptureFirstFrame(svc, Monitor("b", 8, 5));
        Assert.Equal(8, second.Width);
        Assert.Equal(5, second.Height);
        Assert.Equal(8 * 5 * 4, second.Pixels.Length);

        Assert.True(svc.IsCapturing);
        svc.Stop();
        Assert.False(svc.IsCapturing);
    }

    [Fact]
    public void OnTick_FollowsLiveSourceResolutionChange_WithoutRestart()
    {
        using var svc = NewService();
        var live = new int[] { 4, 3 }; // [width, height]; shared with the timer thread
        svc.MonitorResolver = _ => (Volatile.Read(ref live[0]), Volatile.Read(ref live[1]));

        int lastLen = 0;
        var grewToNewSize = new ManualResetEventSlim(false);
        void Handler(object? _, ScreenFrameEventArgs e)
        {
            lastLen = e.PixelsBgra.Length;
            if (e.Width == 8 && e.Height == 5)
            {
                grewToNewSize.Set();
            }
        }

        svc.FrameCaptured += Handler;
        svc.Start(Monitor("m", 4, 3)); // resolver agrees: 4x3

        // Mutate the live resolution; capture must follow WITHOUT Start() being re-called (the
        // coordinator's MonitorsChanged re-sync never restarts capture — mirrors ContentSize self-heal).
        Volatile.Write(ref live[1], 5);
        Volatile.Write(ref live[0], 8);

        Assert.True(grewToNewSize.Wait(TimeSpan.FromSeconds(5)),
            "simulated capture did not follow the live source-resolution change");
        Assert.Equal(8 * 5 * 4, lastLen);
        svc.Stop();
    }

    [Fact]
    public void Start_SameMonitorTwice_IsANoOp_AndKeepsCapturing()
    {
        using var svc = NewService();

        _ = CaptureFirstFrame(svc, Monitor("m", 4, 4));
        Assert.True(svc.IsCapturing);

        svc.Start(Monitor("m", 4, 4)); // no-op
        Assert.True(svc.IsCapturing);
        svc.Stop();
    }

    [Fact]
    public void MaxFps_BoundsEmissionRate()
    {
        using var svc = NewService();
        svc.ConfigureMonitor("m", SyntheticPatterns.Gradient, maxFps: 10);

        int count = 0;
        void Handler(object? _, ScreenFrameEventArgs __) => Interlocked.Increment(ref count);

        svc.FrameCaptured += Handler;
        svc.Start(Monitor("m", 32, 18));
        Thread.Sleep(600); // ~10 fps -> ~6-7 frames; far below an unthrottled/60fps stream
        svc.Stop();
        svc.FrameCaptured -= Handler;

        int observed = Volatile.Read(ref count);
        Assert.InRange(observed, 1, 16);
    }

    [Fact]
    public void Start_WithOverflowingResolution_RaisesError_DoesNotThrow_AndIsNotCapturing()
    {
        using var svc = NewService();
        var errors = new List<PipelineErrorEventArgs>();
        svc.Error += (_, e) => { lock (errors) { errors.Add(e); } };

        var exception = Record.Exception(() => svc.Start(Monitor("huge", int.MaxValue, int.MaxValue)));

        Assert.Null(exception); // never throws back into the pipeline (NFR5)
        Assert.False(svc.IsCapturing);
        lock (errors)
        {
            var error = Assert.Single(errors);
            Assert.Equal("capture", error.Source);
        }
    }

    [Fact]
    public void StartStopDispose_AreIdempotent_AndNeverThrow()
    {
        var svc = NewService();
        var exception = Record.Exception(() =>
        {
            svc.Stop();                       // stop before start
            svc.Start(Monitor("m", 4, 4));
            svc.Start(Monitor("m", 4, 4));    // same monitor — no-op
            svc.Stop();
            svc.Stop();                       // double stop
            svc.Dispose();
            svc.Dispose();                    // double dispose
            svc.Start(Monitor("m", 4, 4));    // start after dispose — ignored
        });

        Assert.Null(exception);
        Assert.False(svc.IsCapturing);
    }
}
#endif

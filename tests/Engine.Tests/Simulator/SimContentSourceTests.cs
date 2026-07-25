#if SIMULATOR_ENABLED
using System.IO;
using AmbientFx.Capture;
using AmbientFx.Models;
using AmbientFx.Simulator;
using AmbientFx.Simulator.Content;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.3 AC8: media decode → BGRA, missing-file fallback, mirror passthrough/rescale via a fake
/// capture (no real WGC in CI), buffer-reuse copy contract, error forwarding, and a live content switch.
/// </summary>
public sealed class SimContentSourceTests
{
    // 2x2: (0,0)=red, (1,0)=green, (0,1)=blue, (1,1)=white — BGRA top-down.
    private static byte[] Source2x2() => new byte[]
    {
        0, 0, 255, 255,   0, 255, 0, 255,
        255, 0, 0, 255,   255, 255, 255, 255,
    };

    private static MonitorInfo Mon(string id, int w, int h) => new() { Id = id, Name = id, Width = w, Height = h };

    private static bool AllOpaqueBlack(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0 || bgra[i + 3] != 255)
            {
                return false;
            }
        }
        return true;
    }

    // ── media ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Media_DecodesImageToBgra_AtSourceResolution()
    {
        string path = Path.Combine(Path.GetTempPath(), $"afx-media-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new System.Drawing.Bitmap(2, 2))
            {
                bmp.SetPixel(0, 0, System.Drawing.Color.FromArgb(255, 255, 0, 0));     // red
                bmp.SetPixel(1, 0, System.Drawing.Color.FromArgb(255, 0, 255, 0));     // green
                bmp.SetPixel(0, 1, System.Drawing.Color.FromArgb(255, 0, 0, 255));     // blue
                bmp.SetPixel(1, 1, System.Drawing.Color.FromArgb(255, 255, 255, 255)); // white
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            using var source = new MediaContentSource(path, NullLogger.Instance, SimMediaScaler.Mode.Fit);
            var buf = new byte[2 * 2 * 4];
            var err = source.Fill(buf, 2, 2, 0);

            Assert.Null(err);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, buf[..4]);        // (0,0) red -> B0 G0 R255
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, buf[12..16]); // (1,1) white
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Media_MissingFile_ReturnsErrorOnce_FillsBlank_NeverThrows()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"afx-missing-{Guid.NewGuid():N}.png");
        using var source = new MediaContentSource(missing, NullLogger.Instance);
        var buf = new byte[4 * 3 * 4];

        PipelineErrorEventArgs? err = null;
        var ex = Record.Exception(() => err = source.Fill(buf, 4, 3, 0));

        Assert.Null(ex);
        Assert.NotNull(err);
        Assert.Equal(SimContentSourceBase.ContentErrorSource, err!.Source); // non-fatal, not "capture"
        Assert.NotEqual("capture", err.Source); // must NOT hit the coordinator's fatal branch
        Assert.True(AllOpaqueBlack(buf));
        Assert.Null(source.Fill(buf, 4, 3, 1)); // reported only once
    }

    // ── mirror (fake real capture; no WGC) ─────────────────────────────────────────────────────────

    [Fact]
    public void Mirror_StartsRealCapture_AndPassesFramesThroughAtEqualResolution()
    {
        var fake = new FakeCapture();
        using var mirror = new MirrorContentSource(fake, Mon("phys", 2, 2), NullLogger.Instance, ownsReal: true, SimMediaScaler.Mode.Stretch);

        Assert.True(fake.IsCapturing);
        Assert.Equal("phys", fake.StartedMonitorId);

        fake.RaiseFrame(Source2x2(), 2, 2);
        var outBuf = new byte[2 * 2 * 4];
        var err = mirror.Fill(outBuf, 2, 2, 0);

        Assert.Null(err);
        Assert.Equal(Source2x2(), outBuf);
    }

    [Fact]
    public void Mirror_CopiesFrame_BeforeTheRealBufferIsReused()
    {
        var fake = new FakeCapture();
        using var mirror = new MirrorContentSource(fake, Mon("phys", 2, 2), NullLogger.Instance, ownsReal: true, SimMediaScaler.Mode.Stretch);

        var fed = Source2x2();
        fake.RaiseFrame(fed, 2, 2);
        Array.Clear(fed, 0, fed.Length); // the real service reuses/overwrites its buffer after the handler

        var outBuf = new byte[2 * 2 * 4];
        mirror.Fill(outBuf, 2, 2, 0);

        Assert.Equal(Source2x2(), outBuf); // mirror copied synchronously, so original colors survive
    }

    [Fact]
    public void Mirror_RescalesWhenResolutionsDiffer()
    {
        var fake = new FakeCapture();
        using var mirror = new MirrorContentSource(fake, Mon("phys", 2, 2), NullLogger.Instance, ownsReal: true, SimMediaScaler.Mode.Stretch);

        fake.RaiseFrame(Source2x2(), 2, 2);
        var outBuf = new byte[4 * 4 * 4];
        mirror.Fill(outBuf, 4, 4, 0);

        // Stretched 2x2 -> 4x4: top-left quadrant is red, bottom-right is white.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, outBuf[..4]);
        int br = (3 * 4 + 3) * 4;
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, outBuf[br..(br + 4)]);
    }

    [Fact]
    public void Mirror_ForwardsRealCaptureErrors_AndFallsBackToBlank()
    {
        var fake = new FakeCapture();
        using var mirror = new MirrorContentSource(fake, Mon("phys", 4, 3), NullLogger.Instance);

        fake.RaiseError("display disconnected");
        var outBuf = new byte[4 * 3 * 4];
        var err = mirror.Fill(outBuf, 4, 3, 0);

        Assert.NotNull(err);
        Assert.Equal(SimContentSourceBase.ContentErrorSource, err!.Source); // remapped to non-fatal
        Assert.NotEqual("capture", err.Source);
        Assert.True(AllOpaqueBlack(outBuf));
    }

    [Fact]
    public void Mirror_Dispose_StopsAndDisposesTheInnerRealCapture()
    {
        var fake = new FakeCapture();
        var mirror = new MirrorContentSource(fake, Mon("phys", 2, 2), NullLogger.Instance, ownsReal: true);

        mirror.Dispose();

        Assert.True(fake.Stopped);
        Assert.True(fake.Disposed);
    }

    // ── live content switch on a running simulated source ──────────────────────────────────────────

    [Fact]
    public void SetContentSource_LiveSwap_OnRunningSource_TakesEffect_NoThrow()
    {
        using var svc = new SimulatedScreenCaptureService(NullLogger<SimulatedScreenCaptureService>.Instance);
        var blankSeen = new ManualResetEventSlim(false);
        svc.FrameCaptured += (_, e) => { if (AllOpaqueBlack(e.PixelsBgra)) blankSeen.Set(); };

        svc.Start(Mon("m", 8, 8)); // synthetic gradient (never all-black)
        svc.SetContentSource("m", new BlankContentSource()); // live swap, no Stop/Start

        Assert.True(blankSeen.Wait(TimeSpan.FromSeconds(5)), "live content switch to blank did not take effect");
        svc.Stop();
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        public bool IsCapturing { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public string? StartedMonitorId { get; private set; }

        public event EventHandler<ScreenFrameEventArgs>? FrameCaptured;
        public event EventHandler<PipelineErrorEventArgs>? Error;

        public void Start(MonitorInfo monitor)
        {
            StartedMonitorId = monitor.Id;
            IsCapturing = true;
        }

        public void Stop()
        {
            Stopped = true;
            IsCapturing = false;
        }

        public void Dispose() => Disposed = true;

        public void RaiseFrame(byte[] bgra, int width, int height) =>
            FrameCaptured?.Invoke(this, new ScreenFrameEventArgs { PixelsBgra = bgra, Width = width, Height = height, TimestampMs = 0 });

        public void RaiseError(string message) =>
            Error?.Invoke(this, new PipelineErrorEventArgs { Source = "capture", Message = message });
    }
}
#endif

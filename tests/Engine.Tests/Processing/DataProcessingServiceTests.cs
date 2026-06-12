using System.Diagnostics;
using AmbientFx.Bridge;
using AmbientFx.Capture;
using AmbientFx.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AmbientFx.Engine.Tests.Processing;

public class MeanAbsoluteDiffTests
{
    [Fact]
    public void IdenticalBuffers_ReturnsZero()
    {
        var a = new byte[] { 1, 2, 3, 200 };
        var b = new byte[] { 1, 2, 3, 200 };

        Assert.Equal(0.0, DataProcessingService.MeanAbsoluteDiff(a, b));
    }

    [Fact]
    public void KnownDifference_ReturnsHandComputedMean()
    {
        // |0-5| + |10-10| + |20-26| = 11; 11/3 = 3.666...
        var a = new byte[] { 0, 10, 20 };
        var b = new byte[] { 5, 10, 26 };

        Assert.Equal(11.0 / 3.0, DataProcessingService.MeanAbsoluteDiff(a, b), 10);
    }

    [Fact]
    public void LengthMismatch_ReturnsMaximallyDifferent255()
    {
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 1, 2 };

        Assert.Equal(255.0, DataProcessingService.MeanAbsoluteDiff(a, b));
    }

    [Fact]
    public void BothEmpty_ReturnsZero()
    {
        Assert.Equal(0.0, DataProcessingService.MeanAbsoluteDiff(
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
    }
}

public sealed class DataProcessingServiceTests : IDisposable
{
    private readonly Mock<IScreenCaptureService> _screen = new();
    private readonly Mock<IAudioCaptureService> _audio = new();
    private readonly DataProcessingService _service;

    private readonly object _framesLock = new();
    private readonly List<FramePayload> _frames = new();

    public DataProcessingServiceTests()
    {
        _service = new DataProcessingService(
            _screen.Object, _audio.Object, NullLogger<DataProcessingService>.Instance);
        _service.FrameReady += (_, e) =>
        {
            lock (_framesLock)
                _frames.Add(e.Frame);
        };
    }

    public void Dispose() => _service.Dispose();

    private int FrameCount
    {
        get { lock (_framesLock) return _frames.Count; }
    }

    private FramePayload? FindFrame(Func<FramePayload, bool> predicate)
    {
        lock (_framesLock)
            return _frames.FirstOrDefault(predicate);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    private static void AssertAllZones(EdgeColors edges, int zones, int[] expected)
    {
        foreach (int[][] edge in new[] { edges.Top, edges.Bottom, edges.Left, edges.Right })
        {
            Assert.Equal(zones, edge.Length);
            foreach (int[] zone in edge)
                Assert.Equal(expected, zone);
        }
    }

    [Fact]
    public void BeforeAnyCapturedFrame_EmitsBlackZonesOfConfiguredLength()
    {
        _service.UpdateOptions(new ProcessingOptions
        {
            ZonesPerEdge = 4, MaxFps = 60, Smoothing = 0f, AudioSensitivity = 0.5f,
        });
        _service.Start();

        Assert.True(WaitUntil(() => FrameCount >= 1), "No FrameReady within 2s of Start.");

        FramePayload frame = FindFrame(_ => true)!;
        AssertAllZones(frame.Edges, 4, new[] { 0, 0, 0 });
        Assert.Equal(new[] { 0, 0, 0 }, frame.Dominant);
        Assert.NotNull(frame.Audio);
        Assert.Empty(frame.Audio.Bands); // no audio event yet
        Assert.Equal(0f, frame.Audio.Intensity);
    }

    [Fact]
    public void AfterFrameAndAudio_EmitsRealEdgeColorsDominantAndShapedAudio()
    {
        _service.UpdateOptions(new ProcessingOptions
        {
            ZonesPerEdge = 4, MaxFps = 60, Smoothing = 0f, AudioSensitivity = 1f,
        });
        _service.Start();

        byte[] pixels = SyntheticFrames.Solid(8, 8, r: 10, g: 20, b: 30);
        _screen.Raise(s => s.FrameCaptured += null, new ScreenFrameEventArgs
        {
            PixelsBgra = pixels, Width = 8, Height = 8, TimestampMs = 1.0,
        });
        _audio.Raise(a => a.AudioAnalyzed += null, new AudioAnalysisEventArgs
        {
            Bands = new[] { 0.25f, 0.25f }, Intensity = 0.25f, TimestampMs = 1.0,
        });

        // With Smoothing = 0 the smoother passes the real colors straight through once
        // the tick picks the frame up; poll for the first such payload.
        Assert.True(
            WaitUntil(() => FindFrame(f => f.Dominant.SequenceEqual(new[] { 10, 20, 30 })) is not null),
            "No FrameReady carrying the captured frame's colors within 2s.");

        FramePayload frame = FindFrame(f => f.Dominant.SequenceEqual(new[] { 10, 20, 30 }))!;
        AssertAllZones(frame.Edges, 4, new[] { 10, 20, 30 });
        Assert.Equal(new[] { 10, 20, 30 }, frame.Dominant);

        // Audio: sensitivity 1 => gain 2 => scaled band 0.5; attack envelope rises toward it.
        Assert.Equal(2, frame.Audio.Bands.Length);
        Assert.All(frame.Audio.Bands, b => Assert.InRange(b, 0.01f, 1f));
        Assert.InRange(frame.Audio.Intensity, 0.01f, 1f);
        Assert.True(frame.T >= 0.0);
    }

    [Fact]
    public void Stop_UnsubscribesAndStopsEmittingFrames()
    {
        _service.UpdateOptions(new ProcessingOptions
        {
            ZonesPerEdge = 4, MaxFps = 60, Smoothing = 0f, AudioSensitivity = 0.5f,
        });
        _service.Start();
        Assert.True(WaitUntil(() => FrameCount >= 1), "Service never emitted a frame before Stop.");

        _service.Stop();
        Thread.Sleep(150); // let any in-flight tick drain

        int countAfterStop = FrameCount;

        // Raising capture events after Stop must not revive processing (handlers unsubscribed).
        _screen.Raise(s => s.FrameCaptured += null, new ScreenFrameEventArgs
        {
            PixelsBgra = SyntheticFrames.Solid(4, 4, 255, 0, 0), Width = 4, Height = 4, TimestampMs = 2.0,
        });
        _audio.Raise(a => a.AudioAnalyzed += null, new AudioAnalysisEventArgs
        {
            Bands = new[] { 1f }, Intensity = 1f, TimestampMs = 2.0,
        });

        Thread.Sleep(300);
        Assert.Equal(countAfterStop, FrameCount);
    }

    [Fact]
    public void Lifecycle_IsIdempotentAndDisposeGuardsStart()
    {
        _service.Stop(); // Stop before Start: no-op, no throw

        _service.Start();
        _service.Start(); // second Start: no-op, no throw

        _service.Stop();
        _service.Stop(); // second Stop: no-op, no throw

        _service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _service.Start());
    }

    [Fact]
    public void UpdateOptions_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _service.UpdateOptions(null!));
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DataProcessingService(
            null!, _audio.Object, NullLogger<DataProcessingService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DataProcessingService(
            _screen.Object, null!, NullLogger<DataProcessingService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DataProcessingService(
            _screen.Object, _audio.Object, null!));
    }
}

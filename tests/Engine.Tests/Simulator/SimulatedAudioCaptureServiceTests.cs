#if SIMULATOR_ENABLED
using AmbientFx.Capture;
using AmbientFx.Devices;
using AmbientFx.Simulator.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.4 AC7: <see cref="SimulatedAudioCaptureService"/> cadence (~30–60 Hz) and range
/// (bands &amp; intensity ∈ [0,1], <c>Bands.Length == BandCount</c>) for both signals, plus pure-generator
/// range smoke and the <see cref="AudioModulation"/> factor staying in range.
/// </summary>
public sealed class SimulatedAudioCaptureServiceTests
{
    private static SimulatedAudioCaptureService New() =>
        new(NullLogger<SimulatedAudioCaptureService>.Instance);

    [Theory]
    [InlineData(SimulatedAudioCaptureService.Signal.Track124Bpm)]
    [InlineData(SimulatedAudioCaptureService.Signal.SineSweep)]
    public void EmitsInRangeBands_AtAudioCadence(SimulatedAudioCaptureService.Signal signal)
    {
        using var svc = New();
        svc.BandCount = 16;
        svc.Mode = signal;

        int count = 0;
        int outOfRange = 0;
        int wrongLength = 0;
        svc.AudioAnalyzed += (_, e) =>
        {
            Interlocked.Increment(ref count);
            if (e.Bands.Length != 16) Interlocked.Increment(ref wrongLength);
            if (e.Intensity is < 0f or > 1f) Interlocked.Increment(ref outOfRange);
            foreach (var b in e.Bands)
            {
                if (b is < 0f or > 1f) { Interlocked.Increment(ref outOfRange); break; }
            }
        };

        Assert.False(svc.IsCapturing);
        svc.Start();
        Assert.True(svc.IsCapturing);
        Thread.Sleep(500);
        svc.Stop();
        Assert.False(svc.IsCapturing);

        int observed = Volatile.Read(ref count);
        Assert.InRange(observed, 8, 50); // ~60 Hz over 0.5 s, generous bounds
        Assert.Equal(0, Volatile.Read(ref wrongLength));
        Assert.Equal(0, Volatile.Read(ref outOfRange));
    }

    [Fact]
    public void StopHaltsEmission()
    {
        using var svc = New();
        int count = 0;
        svc.AudioAnalyzed += (_, _) => Interlocked.Increment(ref count);

        svc.Start();
        Thread.Sleep(150);
        svc.Stop();
        int afterStop = Volatile.Read(ref count);
        Thread.Sleep(150);

        Assert.Equal(afterStop, Volatile.Read(ref count)); // nothing emitted after Stop
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(64)]
    public void Track124Bpm_StaysInRange_AcrossTime(int bands)
    {
        for (int ms = 0; ms < 4000; ms += 17)
        {
            var (band, intensity) = SimulatedAudioCaptureService.Track124Bpm(ms, bands);
            Assert.Equal(bands, band.Length);
            Assert.InRange(intensity, 0f, 1f);
            Assert.All(band, b => Assert.InRange(b, 0f, 1f));
            // The audio-reactive brightness factor must also stay in range on this signal.
            Assert.InRange(AudioModulation.BrightnessFactor(intensity, 0.5f), 0f, 1f);
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(32)]
    public void SineSweep_StaysInRange_AcrossTime(int bands)
    {
        for (int ms = 0; ms < 6000; ms += 23)
        {
            var (band, intensity) = SimulatedAudioCaptureService.SineSweep(ms, bands);
            Assert.Equal(bands, band.Length);
            Assert.InRange(intensity, 0f, 1f);
            Assert.All(band, b => Assert.InRange(b, 0f, 1f));
        }
    }

    [Fact]
    public void BandCount_IsHonored()
    {
        using var svc = New();
        svc.BandCount = 24;
        var done = new ManualResetEventSlim(false);
        int len = -1;
        svc.AudioAnalyzed += (_, e) => { len = e.Bands.Length; done.Set(); };

        svc.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        svc.Stop();

        Assert.Equal(24, len);
    }
}
#endif

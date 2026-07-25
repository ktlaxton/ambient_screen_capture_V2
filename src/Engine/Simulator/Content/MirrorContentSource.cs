#if SIMULATOR_ENABLED
using AmbientFx.Capture;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.3). Mirrors a real physical monitor into a virtual source slot — the
/// <b>only</b> place real WGC capture runs inside the simulator. It wraps its own
/// <see cref="IScreenCaptureService"/> (NOT the DI singleton, which is the simulated one), subscribes to
/// its frames, copies each one synchronously (the real buffer is reused after the handler returns), and
/// rescales the latest frame into the simulator's source-resolution buffer. The real service's errors are
/// forwarded; teardown stops/disposes the inner service. Never throws (NFR5). Compiled out of Release.
/// </summary>
public sealed class MirrorContentSource : SimContentSourceBase
{
    private readonly IScreenCaptureService _real;
    private readonly bool _ownsReal;
    private readonly ILogger _logger;
    private readonly SimMediaScaler.Mode _mode;
    private readonly object _lock = new();

    private byte[]? _latest;
    private int _latestWidth;
    private int _latestHeight;
    private PipelineErrorEventArgs? _pendingError;

    /// <param name="realCapture">A real capture service instance the source owns (or a fake, in tests).</param>
    /// <param name="physical">The physical monitor to mirror — must carry a live HMONITOR from the real
    /// <c>MonitorDetectionService</c>, never a fabricated sentinel.</param>
    public MirrorContentSource(
        IScreenCaptureService realCapture,
        MonitorInfo physical,
        ILogger logger,
        bool ownsReal = true,
        SimMediaScaler.Mode mode = SimMediaScaler.Mode.Cover)
    {
        _real = realCapture;
        _ownsReal = ownsReal;
        _logger = logger;
        _mode = mode;

        _real.FrameCaptured += OnRealFrame;
        _real.Error += OnRealError;
        _real.Start(physical); // real WGC (CreateItemForMonitor) for a true mirror; no-op for a fake
    }

    private void OnRealFrame(object? sender, ScreenFrameEventArgs e)
    {
        // Copy into a FRESH array (never an array a reader may be mid-read of) and publish it under the
        // lock. Fill() snapshots the reference under the lock and reads it after releasing — because a
        // published array is never overwritten, that read is race-free. (The real service reuses its own
        // buffer after this handler returns, so we must copy here regardless.)
        int required = checked(e.Width * e.Height * 4);
        var copy = new byte[required];
        Buffer.BlockCopy(e.PixelsBgra, 0, copy, 0, Math.Min(required, e.PixelsBgra.Length));
        lock (_lock)
        {
            _latest = copy;
            _latestWidth = e.Width;
            _latestHeight = e.Height;
        }
    }

    private void OnRealError(object? sender, PipelineErrorEventArgs e)
    {
        lock (_lock)
        {
            // Remap to the non-fatal content-error source: a dead mirror should blank, not tear the
            // whole simulator pipeline down (the coordinator treats Source="capture" as fatal).
            _pendingError = new PipelineErrorEventArgs
            {
                Source = ContentErrorSource,
                Message = e.Message,
                Exception = e.Exception,
            };
        }
    }

    public override PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex)
    {
        try
        {
            byte[]? src;
            int sw, sh;
            PipelineErrorEventArgs? pending;
            lock (_lock)
            {
                src = _latest;
                sw = _latestWidth;
                sh = _latestHeight;
                pending = _pendingError;
                _pendingError = null;
            }

            if (src is null)
            {
                FillBlank(bgra, width, height); // no real frame yet
                return pending;
            }

            SimMediaScaler.Scale(src, sw, sh, bgra, width, height, _mode);
            return pending;
        }
        catch (Exception ex)
        {
            FillBlank(bgra, width, height);
            return ErrorOnce($"Mirror rescale failed: {ex.Message}", ex);
        }
    }

    public override void Dispose()
    {
        try { _real.FrameCaptured -= OnRealFrame; } catch { /* best effort */ }
        try { _real.Error -= OnRealError; } catch { /* best effort */ }
        try { _real.Stop(); } catch (Exception ex) { _logger.LogDebug(ex, "Stopping the mirror's real capture failed."); }
        if (_ownsReal)
        {
            try { _real.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing the mirror's real capture failed."); }
        }
    }
}
#endif

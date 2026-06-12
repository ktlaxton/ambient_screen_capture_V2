using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AmbientFx.Capture.Interop;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using MapFlags = Vortice.Direct3D11.MapFlags;
using MapMode = Vortice.Direct3D11.MapMode;
using ResultCode = Vortice.DXGI.ResultCode;

namespace AmbientFx.Capture;

/// <summary>
/// Real per-monitor screen capture built on Windows.Graphics.Capture + Direct3D11 (Vortice).
/// Each captured frame stays on the GPU: it is copied into a mip-chain texture, auto-downscaled
/// via <c>GenerateMips</c>, and only a tiny (~64 px wide) mip level is read back to the CPU as a
/// tightly packed BGRA buffer and published via <see cref="FrameCaptured"/>.
/// </summary>
/// <remarks>
/// Threading: <see cref="Start"/>/<see cref="Stop"/>/<see cref="Dispose"/> may be called from any
/// thread and are serialized on an internal lock. The frame pool is created free-threaded, so
/// <see cref="FrameCaptured"/> and <see cref="Error"/> are raised on thread-pool worker threads —
/// subscribers must marshal to the UI thread themselves and must consume (or copy)
/// <see cref="ScreenFrameEventArgs.PixelsBgra"/> synchronously, because the buffer is reused for
/// the next frame.
/// Designed as a DI singleton; Start/Stop are idempotent and Stop never throws (NFR5).
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class ScreenCaptureService : IScreenCaptureService
{
    private const string ErrorSource = "capture";

    /// <summary>1–2 buffers keeps latency minimal for latest-frame-wins processing.</summary>
    private const int FramePoolBufferCount = 2;

    /// <summary>The mip level whose width is closest to this is read back for analysis.</summary>
    private const uint TargetMipWidth = 64;

    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    /// <summary>Shared monotonic clock for <see cref="ScreenFrameEventArgs.TimestampMs"/>.</summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly ILogger<ScreenCaptureService> _logger;

    /// <summary>Guards all pipeline state below and serializes GPU work against teardown.</summary>
    private readonly object _gate = new();

    // ── pipeline state (all access under _gate) ────────────────────────────────────────────────
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _mipTexture;       // full-res, full mip chain, auto-gen capable
    private ID3D11ShaderResourceView? _mipSrv;  // SRV over the full chain — GenerateMips target
    private ID3D11Texture2D? _staging;          // tiny CPU-readable copy of the chosen mip
    private uint _mipLevel;
    private uint _mipWidth;
    private uint _mipHeight;
    private SizeInt32 _contentSize;
    private byte[] _pixelBuffer = Array.Empty<byte>(); // reused — no per-frame allocation once warm
    private string? _monitorId;
    private bool _isCapturing;
    private bool _disposed;

    /// <summary>0/1 reentrancy guard: if a frame is still processing, new frames are skipped, never queued.</summary>
    private int _frameInFlight;

    /// <summary>Creates the service. Resolved as a DI singleton; no capture starts until <see cref="Start"/>.</summary>
    public ScreenCaptureService(ILogger<ScreenCaptureService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<ScreenFrameEventArgs>? FrameCaptured;

    /// <inheritdoc />
    public event EventHandler<PipelineErrorEventArgs>? Error;

    /// <inheritdoc />
    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _isCapturing;
            }
        }
    }

    /// <summary>
    /// Starts capturing <paramref name="monitor"/>. No-op when already capturing the same monitor id;
    /// switches cleanly when capturing a different one. Thread-safe; failures are logged and surfaced
    /// via <see cref="Error"/> instead of throwing (NFR5).
    /// </summary>
    public void Start(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        PipelineErrorEventArgs? error = null;

        lock (_gate)
        {
            if (_disposed)
            {
                _logger.LogWarning("Start({MonitorId}) ignored: the screen capture service is disposed.", monitor.Id);
                return;
            }

            if (_isCapturing && string.Equals(_monitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Already capturing monitor {MonitorId}; Start is a no-op.", monitor.Id);
                return;
            }

            StopLocked(); // clean switch (no-op when idle)

            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    _logger.LogError("Windows.Graphics.Capture is not supported on this system.");
                    error = new PipelineErrorEventArgs
                    {
                        Source = ErrorSource,
                        Message = "Screen capture is not supported on this version of Windows.",
                    };
                }
                else
                {
                    StartLocked(monitor);
                    _logger.LogInformation(
                        "Screen capture started on monitor {MonitorId} ({Width}x{Height}); analysis mip {Mip} ({MipWidth}x{MipHeight}).",
                        monitor.Id, _contentSize.Width, _contentSize.Height, _mipLevel, _mipWidth, _mipHeight);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start screen capture on monitor {MonitorId}.", monitor.Id);
                StopLocked();
                error = new PipelineErrorEventArgs
                {
                    Source = ErrorSource,
                    Message = $"Failed to start screen capture: {ex.Message}",
                    Exception = ex,
                };
            }
        }

        // Raised outside the lock so subscribers may call Start/Stop freely.
        RaiseError(error);
    }

    /// <summary>Stops capture and releases all GPU/WinRT resources. Idempotent; never throws.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            bool wasCapturing = _isCapturing;
            StopLocked();
            if (wasCapturing)
            {
                _logger.LogInformation("Screen capture stopped.");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked();
        }
    }

    // ── capture pipeline construction (under _gate) ─────────────────────────────────────────────

    private void StartLocked(MonitorInfo monitor)
    {
        (_device, _context) = Direct3D11DeviceHelper.CreateDevice();
        _winrtDevice = Direct3D11DeviceHelper.CreateWinRTDevice(_device);

        _item = CaptureInterop.CreateItemForMonitor(monitor.HMonitor);
        _item.Closed += OnItemClosed;

        _contentSize = _item.Size;
        CreateSizedResourcesLocked();

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            _contentSize);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false; // cursor pixels would pollute the color analysis
        TryDisableCaptureBorder(_session);

        _monitorId = monitor.Id;
        _isCapturing = true;
        _session.StartCapture();
    }

    /// <summary>
    /// Removes the yellow capture border on Windows 11 (build 20348+). On Windows 10 19041–19045 the
    /// property does not exist and the border cannot be removed — all failures are ignored by design.
    /// </summary>
    private void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                return;
            }

            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                return;
            }

            // Without this handshake, setting IsBorderRequired = false is silently ignored.
            // Unpackaged full-trust apps are auto-granted (no prompt).
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).GetAwaiter().GetResult();
            session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not disable the capture border; continuing with it visible.");
        }
    }

    /// <summary>
    /// (Re)creates the mip-chain texture, its SRV, the tiny staging texture, and the readback buffer
    /// for the current <see cref="_contentSize"/>. Called on start and whenever ContentSize changes.
    /// </summary>
    private void CreateSizedResourcesLocked()
    {
        _staging?.Dispose();
        _staging = null;
        _mipSrv?.Dispose();
        _mipSrv = null;
        _mipTexture?.Dispose();
        _mipTexture = null;

        uint width = (uint)Math.Max(1, _contentSize.Width);
        uint height = (uint)Math.Max(1, _contentSize.Height);

        // GenerateMips requires: full chain (MipLevels = 0), RenderTarget | ShaderResource, GenerateMips misc flag.
        var mipDesc = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            width,
            height,
            arraySize: 1,
            mipLevels: 0,
            bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            miscFlags: ResourceOptionFlags.GenerateMips);
        _mipTexture = _device!.CreateTexture2D(mipDesc);
        _mipSrv = _device.CreateShaderResourceView(_mipTexture);

        (_mipLevel, _mipWidth, _mipHeight) = PickMip(width, height, TargetMipWidth);

        var stagingDesc = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            _mipWidth,
            _mipHeight,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging,
            cpuAccessFlags: CpuAccessFlags.Read);
        _staging = _device.CreateTexture2D(stagingDesc);

        int requiredBytes = checked((int)(_mipWidth * _mipHeight * 4u));
        if (_pixelBuffer.Length != requiredBytes)
        {
            _pixelBuffer = new byte[requiredBytes];
        }
    }

    /// <summary>
    /// Picks the mip level whose width is closest to <paramref name="targetWidth"/>.
    /// D3D11 mip extents floor-halve per level: mip i of d is max(1, d >> i).
    /// </summary>
    private static (uint Level, uint Width, uint Height) PickMip(uint sourceWidth, uint sourceHeight, uint targetWidth)
    {
        uint mipCount = (uint)Math.Floor(Math.Log2(Math.Max(sourceWidth, sourceHeight))) + 1;
        uint level = (uint)Math.Clamp(
            (int)Math.Round(Math.Log2(sourceWidth / (double)targetWidth)),
            0,
            (int)mipCount - 1);
        uint width = Math.Max(1u, sourceWidth >> (int)level);
        uint height = Math.Max(1u, sourceHeight >> (int)level);
        return (level, width, height);
    }

    // ── frame processing (free-threaded pool: worker threads) ───────────────────────────────────

    /// <summary>
    /// Raised by the free-threaded frame pool on a thread-pool worker. Skips (never queues) when the
    /// previous frame is still processing; all other work is serialized with Start/Stop via the lock.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
    {
        if (Interlocked.CompareExchange(ref _frameInFlight, 1, 0) != 0)
        {
            return; // previous frame still in flight — drop this one
        }

        byte[]? pixels = null;
        int width = 0;
        int height = 0;
        double timestampMs = 0;
        PipelineErrorEventArgs? error = null;

        try
        {
            lock (_gate)
            {
                if (_disposed || !_isCapturing || !ReferenceEquals(sender, _framePool))
                {
                    return; // stale callback from a torn-down or replaced pool
                }

                // Drain to the newest frame. Every frame MUST be disposed or the pool starves
                // and FrameArrived silently stops firing.
                Direct3D11CaptureFrame? frame = null;
                while (sender.TryGetNextFrame() is { } next)
                {
                    frame?.Dispose();
                    frame = next;
                }

                if (frame is null)
                {
                    return;
                }

                using (frame)
                {
                    if (frame.ContentSize != _contentSize)
                    {
                        // Resolution/DPI change: rebuild the sized resources and the pool, skip this frame.
                        _logger.LogInformation(
                            "Capture content size changed to {Width}x{Height}; recreating frame pool and textures.",
                            frame.ContentSize.Width, frame.ContentSize.Height);
                        _contentSize = frame.ContentSize;
                        CreateSizedResourcesLocked();
                        sender.Recreate(
                            _winrtDevice!,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized,
                            FramePoolBufferCount,
                            _contentSize);
                        return; // next frame arrives at the new size
                    }

                    ProcessFrameLocked(frame);
                    pixels = _pixelBuffer;
                    width = (int)_mipWidth;
                    height = (int)_mipHeight;
                    timestampMs = Clock.Elapsed.TotalMilliseconds;
                }
            }
        }
        catch (Exception ex) when (IsDeviceLost(ex))
        {
            _logger.LogError(ex, "Direct3D device removed/reset during capture; tearing down the capture pipeline.");
            lock (_gate)
            {
                StopLocked();
            }

            error = new PipelineErrorEventArgs
            {
                Source = ErrorSource,
                Message = "The graphics device was removed or reset; screen capture stopped.",
                Exception = ex,
            };
        }
        catch (Exception ex)
        {
            // NFR5: a bad frame must never take down the host — log and skip it.
            _logger.LogError(ex, "Unexpected error while processing a captured frame; the frame was skipped.");
        }
        finally
        {
            try
            {
                if (pixels is not null)
                {
                    // Outside the lock so subscribers may call Start/Stop. The in-flight guard is still
                    // held, so _pixelBuffer cannot be overwritten by the next frame while handlers run.
                    FrameCaptured?.Invoke(this, new ScreenFrameEventArgs
                    {
                        PixelsBgra = pixels,
                        Width = width,
                        Height = height,
                        TimestampMs = timestampMs,
                    });
                }

                RaiseError(error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A FrameCaptured/Error subscriber threw; ignoring (NFR5).");
            }
            finally
            {
                Volatile.Write(ref _frameInFlight, 0);
            }
        }
    }

    /// <summary>
    /// GPU downscale + tiny readback for one frame. Must run under <see cref="_gate"/> (the immediate
    /// context is not thread-safe). CPU cost: one Map of ~<c>mipW*mipH*4</c> bytes (~9 KB at 64×36).
    /// </summary>
    private void ProcessFrameLocked(Direct3D11CaptureFrame frame)
    {
        using ID3D11Texture2D frameTexture = CaptureInterop.GetTexture(frame.Surface);

        // CopyResource is invalid here (1 mip vs full chain) — copy subresource 0 into mip 0,
        // bounded by ContentSize (the pool texture may be larger after display changes).
        _context!.CopySubresourceRegion(
            _mipTexture!, 0, 0, 0, 0,
            frameTexture, 0,
            new Box(0, 0, 0, frame.ContentSize.Width, frame.ContentSize.Height, 1));

        _context.GenerateMips(_mipSrv!);

        // Pull the chosen ~64 px mip into staging. Subresource index == mip level (arraySize == 1).
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, _mipLevel, null);

        // Map blocks until the GPU copy completes — fine at this tiny size and cadence.
        MappedSubresource mapped = _context.Map(_staging!, 0, MapMode.Read, MapFlags.None);
        try
        {
            // RowPitch is driver-aligned (>= mipWidth * 4); copy row by row into the tightly packed buffer.
            int rowBytes = (int)_mipWidth * 4;
            IntPtr source = mapped.DataPointer;
            int rowPitch = (int)mapped.RowPitch;
            for (int y = 0; y < (int)_mipHeight; y++)
            {
                Marshal.Copy(source + y * rowPitch, _pixelBuffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    // ── teardown & error plumbing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires when the captured display goes away (unplug, topology change). Raised by WinRT on an
    /// arbitrary thread.
    /// </summary>
    private void OnItemClosed(GraphicsCaptureItem sender, object? args)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _isCapturing && ReferenceEquals(sender, _item);
            if (wasActive)
            {
                StopLocked();
            }
        }

        if (wasActive)
        {
            _logger.LogWarning("The captured display was disconnected; screen capture stopped.");
            RaiseError(new PipelineErrorEventArgs
            {
                Source = ErrorSource,
                Message = "The captured display was disconnected",
            });
        }
    }

    /// <summary>
    /// Deterministic teardown in reverse creation order: session → pool → item → staging → SRV →
    /// mip texture → context → device → WinRT device. Idempotent; never throws. Runs under <see cref="_gate"/>.
    /// </summary>
    private void StopLocked()
    {
        try
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure unhooking capture events during teardown.");
        }

        SafeDispose(_session, "capture session");
        _session = null;
        SafeDispose(_framePool, "frame pool");
        _framePool = null;
        _item = null; // GraphicsCaptureItem has no Dispose; drop the reference
        SafeDispose(_staging, "staging texture");
        _staging = null;
        SafeDispose(_mipSrv, "mip shader resource view");
        _mipSrv = null;
        SafeDispose(_mipTexture, "mip texture");
        _mipTexture = null;
        SafeDispose(_context, "device context");
        _context = null;
        SafeDispose(_device, "device");
        _device = null;
        SafeDispose(_winrtDevice, "WinRT device");
        _winrtDevice = null;

        _monitorId = null;
        _isCapturing = false;
    }

    private void SafeDispose(IDisposable? disposable, string what)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure disposing the {What} during capture teardown.", what);
        }
    }

    /// <summary>Raises <see cref="Error"/> if non-null. Call outside <see cref="_gate"/>.</summary>
    private void RaiseError(PipelineErrorEventArgs? error)
    {
        if (error is null)
        {
            return;
        }

        try
        {
            Error?.Invoke(this, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An Error event subscriber threw; ignoring (NFR5).");
        }
    }

    /// <summary>True for DXGI device-removed/reset/hung failures from either Vortice or raw COM.</summary>
    private static bool IsDeviceLost(Exception exception) => exception switch
    {
        SharpGenException sharpGen =>
            sharpGen.ResultCode == ResultCode.DeviceRemoved ||
            sharpGen.ResultCode == ResultCode.DeviceReset ||
            sharpGen.ResultCode == ResultCode.DeviceHung,
        COMException com =>
            com.HResult is DxgiErrorDeviceRemoved or DxgiErrorDeviceHung or DxgiErrorDeviceReset,
        _ => false,
    };
}

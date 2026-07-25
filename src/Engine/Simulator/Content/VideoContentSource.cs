#if SIMULATOR_ENABLED
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmbientFx.Capture;
using Microsoft.Extensions.Logging;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Rect = System.Windows.Rect;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.6). Plays a video file (mp4/mov/avi/wmv/mkv/webm) and feeds its frames
/// to the simulated source — so the effect can be judged on real moving footage, not just a still image
/// or an image sequence. Decoding uses the <b>in-box</b> WPF <see cref="MediaPlayer"/> (Windows Media
/// Foundation codecs) — <b>no new native dependency</b> (ffmpeg/OpenCvSharp are still avoided).
/// </summary>
/// <remarks>
/// MediaPlayer is a <see cref="DispatcherObject"/>, so it (and the per-frame
/// <see cref="RenderTargetBitmap"/> grab) run on a dedicated STA dispatcher thread this source owns. Each
/// tick renders the current video frame to BGRA and publishes it to a latest-frame buffer; <see cref="Fill"/>
/// (called on the capture thread) snapshots that buffer and rescales it — the same producer/consumer
/// hand-off <see cref="MirrorContentSource"/> uses, so the read is race-free. The video loops. Any
/// failure (missing file, unsupported codec) degrades to a blank frame + a one-shot error and never throws
/// (NFR5). Known caveat: on some GPU/codec combinations hardware-accelerated frames can render black to a
/// RenderTargetBitmap — if that happens, mirror a monitor playing the clip, or use an image sequence.
/// Compiled out of Release.
/// </remarks>
public sealed class VideoContentSource : SimContentSourceBase
{
    private const int DecodeIntervalMs = 33; // ~30 fps decode cadence (Fill resamples to the source maxFps)

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SimMediaScaler.Mode _mode;
    private readonly object _lock = new();
    private readonly Thread _thread;

    private Dispatcher? _dispatcher;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private RenderTargetBitmap? _rtb;
    private int _videoWidth;
    private int _videoHeight;

    private byte[]? _latest;
    private int _latestWidth;
    private int _latestHeight;
    private volatile bool _loadFailed;
    private volatile bool _disposed;

    public VideoContentSource(string path, ILogger logger, SimMediaScaler.Mode mode = SimMediaScaler.Mode.Cover)
    {
        _path = path;
        _logger = logger;
        _mode = mode;

        _thread = new Thread(Run) { IsBackground = true, Name = "SimVideoDecode" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _loadFailed = true;
                _logger.LogWarning("Simulator video not found: '{Path}'.", _path);
                return;
            }

            _dispatcher = Dispatcher.CurrentDispatcher;
            _player = new MediaPlayer { Volume = 0.0 };
            _player.MediaOpened += (_, _) =>
            {
                _videoWidth = Math.Max(1, _player!.NaturalVideoWidth);
                _videoHeight = Math.Max(1, _player.NaturalVideoHeight);
            };
            _player.MediaEnded += (_, _) =>
            {
                try { _player!.Position = TimeSpan.Zero; _player.Play(); } // loop
                catch (Exception ex) { _logger.LogDebug(ex, "Simulator video loop reposition failed."); }
            };
            _player.MediaFailed += (_, e) =>
            {
                _loadFailed = true;
                _logger.LogWarning(e.ErrorException, "Simulator video '{Path}' failed to open (codec/format?).", _path);
            };

            _player.Open(new Uri(_path, UriKind.Absolute));
            _player.Play();

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(DecodeIntervalMs),
            };
            _timer.Tick += (_, _) => CaptureFrame();
            _timer.Start();

            Dispatcher.Run(); // pump the player + timer until Dispose() shuts it down
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _logger.LogWarning(ex, "Simulator video decode thread for '{Path}' crashed.", _path);
        }
    }

    private void CaptureFrame()
    {
        if (_disposed || _player is null || _videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }
        try
        {
            if (_rtb is null || _rtb.PixelWidth != _videoWidth || _rtb.PixelHeight != _videoHeight)
            {
                _rtb = new RenderTargetBitmap(_videoWidth, _videoHeight, 96, 96, PixelFormats.Pbgra32);
            }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawVideo(_player, new Rect(0, 0, _videoWidth, _videoHeight));
            }
            _rtb.Clear();
            _rtb.Render(visual);

            var converted = new FormatConvertedBitmap(_rtb, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            var buffer = new byte[checked(w * h * 4)];
            converted.CopyPixels(buffer, w * 4, 0);

            lock (_lock)
            {
                _latest = buffer; // a fresh array each tick — never mutated after publish (race-free with Fill)
                _latestWidth = w;
                _latestHeight = h;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Simulator video frame capture failed for '{Path}'.", _path);
        }
    }

    public override PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex)
    {
        if (_loadFailed)
        {
            FillBlank(bgra, width, height);
            return ErrorOnce($"Video source '{_path}' could not be played; showing a blank frame.");
        }

        byte[]? src;
        int sw, sh;
        lock (_lock)
        {
            src = _latest;
            sw = _latestWidth;
            sh = _latestHeight;
        }

        if (src is null)
        {
            FillBlank(bgra, width, height); // not decoded yet (still opening)
            return null;
        }

        try
        {
            SimMediaScaler.Scale(src, sw, sh, bgra, width, height, _mode);
            return null;
        }
        catch (Exception ex)
        {
            FillBlank(bgra, width, height);
            return ErrorOnce($"Video rescale failed for '{_path}': {ex.Message}", ex);
        }
    }

    public override void Dispose()
    {
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }
        try
        {
            dispatcher.Invoke(() =>
            {
                try { _timer?.Stop(); } catch { /* best effort */ }
                try { _player?.Close(); } catch { /* best effort */ }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the simulator video player failed.");
        }
        try { dispatcher.InvokeShutdown(); } catch { /* ends Dispatcher.Run so the thread exits */ }
    }
}
#endif

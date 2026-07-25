#if SIMULATOR_ENABLED
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmbientFx.Capture;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.3). A media content source that decodes an image (PNG/JPG/BMP) — or a
/// <b>folder of images</b> as a looping sequence — to BGRA and scales/letterboxes it into the source
/// resolution. Decoding uses in-box WIC (<see cref="BitmapDecoder"/>), so there is no new native
/// dependency; a moving source is achieved with an image sequence (the minimal-native-dep option from
/// the story's decode trade-off — heavy video stacks are deliberately avoided). Missing/undecodable
/// input degrades to a blank frame + a one-shot Error (NFR5). Compiled out of Release.
/// </summary>
public sealed class MediaContentSource : SimContentSourceBase
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SimMediaScaler.Mode _mode;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private byte[]? _decoded;          // single image, decoded once
    private int _decodedWidth;
    private int _decodedHeight;

    private string[]? _sequence;       // image-sequence frame paths
    private int _cachedIndex = -1;
    private bool _loadFailed;

    public MediaContentSource(string path, ILogger logger, SimMediaScaler.Mode mode = SimMediaScaler.Mode.Fit)
    {
        _path = path;
        _logger = logger;
        _mode = mode;
        LoadOnce();
    }

    private void LoadOnce()
    {
        try
        {
            if (Directory.Exists(_path))
            {
                _sequence = Directory.GetFiles(_path)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (_sequence.Length == 0)
                {
                    throw new FileNotFoundException($"No image frames found in directory '{_path}'.");
                }
            }
            else if (File.Exists(_path))
            {
                (_decoded, _decodedWidth, _decodedHeight) = DecodeBgra(_path);
            }
            else
            {
                throw new FileNotFoundException($"Media file not found: '{_path}'.");
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _logger.LogWarning(ex, "Simulator media source '{Path}' could not be loaded.", _path);
        }
    }

    public override PipelineErrorEventArgs? Fill(byte[] bgra, int width, int height, long frameIndex)
    {
        try
        {
            if (_loadFailed)
            {
                FillBlank(bgra, width, height);
                return ErrorOnce($"Media source '{_path}' could not be loaded; showing a blank frame.");
            }

            byte[] src;
            int sw, sh;
            if (_sequence is not null)
            {
                int index = (int)(((frameIndex % _sequence.Length) + _sequence.Length) % _sequence.Length);
                if (index != _cachedIndex)
                {
                    (_decoded, _decodedWidth, _decodedHeight) = DecodeBgra(_sequence[index]); // loops on EOF
                    _cachedIndex = index;
                }
            }

            src = _decoded!;
            sw = _decodedWidth;
            sh = _decodedHeight;
            SimMediaScaler.Scale(src, sw, sh, bgra, width, height, _mode);
            return null;
        }
        catch (Exception ex)
        {
            FillBlank(bgra, width, height);
            return ErrorOnce($"Media decode failed for '{_path}': {ex.Message}", ex);
        }
    }

    /// <summary>Decodes an image file to a tightly-packed, top-down BGRA buffer via in-box WIC.</summary>
    public static (byte[] Pixels, int Width, int Height) DecodeBgra(string path)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(path, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        var buffer = new byte[checked(w * h * 4)];
        converted.CopyPixels(buffer, w * 4, 0); // WIC is top-down, Bgra32 == B@0/G@1/R@2/A@3
        return (buffer, w, h);
    }
}
#endif

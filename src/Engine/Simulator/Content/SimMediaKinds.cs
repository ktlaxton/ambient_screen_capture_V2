#if SIMULATOR_ENABLED
using System.IO;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.6). Pure file-kind classification for the "media" content source so a
/// single picked file routes to the right decoder: a still image (<see cref="MediaContentSource"/>, in-box
/// WIC) or a video (<see cref="VideoContentSource"/>, in-box Media Foundation via WPF MediaPlayer). A
/// directory is treated as an image sequence. Side-effect-free and unit-tested. Compiled out of Release.
/// </summary>
public static class SimMediaKinds
{
    /// <summary>Still-image extensions decoded by WIC (<see cref="MediaContentSource"/>).</summary>
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>Video container extensions played by the in-box MediaPlayer (<see cref="VideoContentSource"/>).</summary>
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm" };

    /// <summary>True when the path's extension is a known video container (case-insensitive).</summary>
    public static bool IsVideo(string? path) => HasExtension(path, VideoExtensions);

    /// <summary>True when the path's extension is a known still image (case-insensitive).</summary>
    public static bool IsImage(string? path) => HasExtension(path, ImageExtensions);

    /// <summary>The OpenFileDialog filter offering pictures + video + all-files (Story 10.6 Browse…).</summary>
    public static string OpenFileFilter
    {
        get
        {
            string all = string.Join(";", ImageExtensions.Concat(VideoExtensions).Select(e => "*" + e));
            string pics = string.Join(";", ImageExtensions.Select(e => "*" + e));
            string vids = string.Join(";", VideoExtensions.Select(e => "*" + e));
            return $"Pictures & video|{all}|Pictures|{pics}|Video|{vids}|All files|*.*";
        }
    }

    private static bool HasExtension(string? path, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(extensions, ext) >= 0;
    }
}
#endif

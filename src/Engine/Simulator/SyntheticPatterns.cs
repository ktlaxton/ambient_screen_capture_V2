#if SIMULATOR_ENABLED
using System;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator). Pure synthetic test-pattern generators that fill a
/// tightly-packed, top-down 32-bit BGRA buffer (B@0/G@1/R@2/A@3, stride = <c>width*4</c>) — the
/// exact contract of <see cref="AmbientFx.Capture.ScreenFrameEventArgs"/>. Every generator is a
/// pure function of <c>(x, y, frameIndex)</c>, so output is deterministic for a fixed frame index
/// (Story 10.5's headless render hook relies on this). Compiled out of Release via
/// <c>SIMULATOR_ENABLED</c>.
/// </summary>
public static class SyntheticPatterns
{
    /// <summary>Animated diagonal gradient — moves with the frame index so the pipeline sees change.</summary>
    public const string Gradient = "gradient";

    /// <summary>Static 8-bar full-saturation color bars (white/yellow/cyan/green/magenta/red/blue/black).</summary>
    public const string Bars = "bars";

    /// <summary>Grey test card with a border, centre crosshair, and a sweeping marker.</summary>
    public const string TestCard = "testcard";

    /// <summary>Canonical pattern name, defaulting unknown/blank values to <see cref="Gradient"/>.</summary>
    public static string Normalize(string? pattern) => (pattern ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Bars => Bars,
        TestCard or "test-card" or "test card" => TestCard,
        _ => Gradient,
    };

    /// <summary>True when <paramref name="pattern"/> names a known pattern (case-insensitive).</summary>
    public static bool IsKnown(string? pattern) => (pattern ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Gradient or Bars or TestCard or "test-card" or "test card" => true,
        _ => false,
    };

    /// <summary>
    /// Fills <paramref name="bgra"/> in place with the chosen pattern. The buffer must hold at least
    /// <c>width*height*4</c> bytes; callers (the simulated capture service) guarantee that.
    /// </summary>
    public static void Fill(string? pattern, byte[] bgra, int width, int height, long frameIndex)
    {
        switch (Normalize(pattern))
        {
            case Bars:
                FillBars(bgra, width, height);
                break;
            case TestCard:
                FillTestCard(bgra, width, height, frameIndex);
                break;
            default:
                FillGradient(bgra, width, height, frameIndex);
                break;
        }
    }

    /// <summary>
    /// Animated diagonal gradient. R rises across X, G rises down Y, B follows the X+Y diagonal, and
    /// every channel is offset by a per-frame phase so consecutive frames differ (the pipeline must
    /// see motion). Pure: identical input frame index yields identical bytes.
    /// </summary>
    public static void FillGradient(byte[] bgra, int width, int height, long frameIndex)
    {
        int phase = (int)(frameIndex & 0xFF);
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        for (int y = 0; y < height; y++)
        {
            byte g = (byte)((y * 256 / h + phase) & 0xFF);
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x * 256 / w + phase) & 0xFF);
                byte b = (byte)((x + y + phase) & 0xFF);
                int p = (row + x) * 4;
                bgra[p] = b;       // B
                bgra[p + 1] = g;   // G
                bgra[p + 2] = r;   // R
                bgra[p + 3] = 255; // A
            }
        }
    }

    private static readonly (byte R, byte G, byte B)[] BarColors =
    {
        (255, 255, 255), // white
        (255, 255, 0),   // yellow
        (0, 255, 255),   // cyan
        (0, 255, 0),     // green
        (255, 0, 255),   // magenta
        (255, 0, 0),     // red
        (0, 0, 255),     // blue
        (0, 0, 0),       // black
    };

    /// <summary>Static vertical color bars; the left-to-right band a pixel falls in selects its color.</summary>
    public static void FillBars(byte[] bgra, int width, int height)
    {
        int w = Math.Max(1, width);
        for (int x = 0; x < width; x++)
        {
            int bar = Math.Min(BarColors.Length - 1, x * BarColors.Length / w);
            var (r, g, b) = BarColors[bar];
            for (int y = 0; y < height; y++)
            {
                int p = (y * width + x) * 4;
                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
                bgra[p + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Grey field with a white border, a centre crosshair, and a vertical marker that sweeps across
    /// with the frame index (so the card animates while staying deterministic per frame).
    /// </summary>
    public static void FillTestCard(byte[] bgra, int width, int height, long frameIndex)
    {
        const byte field = 96;
        int cx = width / 2;
        int cy = height / 2;
        int sweepX = width > 0 ? (int)(frameIndex % width) : 0;
        for (int y = 0; y < height; y++)
        {
            bool edgeRow = y == 0 || y == height - 1;
            for (int x = 0; x < width; x++)
            {
                byte r = field, g = field, b = field;
                bool border = edgeRow || x == 0 || x == width - 1;
                bool crosshair = x == cx || y == cy;
                bool sweep = x == sweepX;
                if (border || crosshair)
                {
                    r = g = b = 255; // white lines
                }
                else if (sweep)
                {
                    r = 255; g = 32; b = 32; // red sweep marker
                }
                int p = (y * width + x) * 4;
                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
                bgra[p + 3] = 255;
            }
        }
    }
}
#endif

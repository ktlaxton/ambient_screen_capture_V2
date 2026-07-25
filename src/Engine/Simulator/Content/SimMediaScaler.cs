#if SIMULATOR_ENABLED
using System;

namespace AmbientFx.Simulator.Content;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator, Story 10.3). Pure scaling helper: maps a source BGRA image
/// into a target BGRA buffer (tightly packed, top-down, opaque), independent of any media library so it
/// is fully unit-testable. Nearest-neighbor sampling keeps it dependency-free and fast enough for a dev
/// tool. Compiled out of Release.
/// </summary>
public static class SimMediaScaler
{
    public enum Mode
    {
        /// <summary>Preserve aspect, fit inside the target, letterbox the remainder with black.</summary>
        Fit,

        /// <summary>Stretch to exactly fill the target (aspect not preserved).</summary>
        Stretch,

        /// <summary>Preserve aspect, cover the target, cropping the overflow.</summary>
        Cover,
    }

    /// <summary>
    /// Scales <paramref name="src"/> (<paramref name="sw"/>×<paramref name="sh"/> BGRA) into
    /// <paramref name="dst"/> (<paramref name="dw"/>×<paramref name="dh"/> BGRA), per <paramref name="mode"/>.
    /// <paramref name="dst"/> must hold ≥ <c>dw*dh*4</c> bytes. Always writes opaque alpha.
    /// </summary>
    public static void Scale(byte[] src, int sw, int sh, byte[] dst, int dw, int dh, Mode mode = Mode.Fit)
    {
        if (dw <= 0 || dh <= 0)
        {
            return;
        }

        // Letterbox/background fill = opaque black.
        int total = checked(dw * dh * 4);
        for (int i = 0; i < total; i += 4)
        {
            dst[i] = 0;
            dst[i + 1] = 0;
            dst[i + 2] = 0;
            dst[i + 3] = 255;
        }

        if (src is null || sw <= 0 || sh <= 0)
        {
            return;
        }

        int regionW, regionH, dstX0, dstY0;
        switch (mode)
        {
            case Mode.Stretch:
                regionW = dw;
                regionH = dh;
                dstX0 = 0;
                dstY0 = 0;
                break;
            case Mode.Cover:
            {
                double s = Math.Max((double)dw / sw, (double)dh / sh);
                regionW = Math.Max(1, (int)Math.Round(sw * s));
                regionH = Math.Max(1, (int)Math.Round(sh * s));
                dstX0 = (dw - regionW) / 2; // negative => crop
                dstY0 = (dh - regionH) / 2;
                break;
            }
            default: // Fit
            {
                double s = Math.Min((double)dw / sw, (double)dh / sh);
                regionW = Math.Max(1, (int)Math.Round(sw * s));
                regionH = Math.Max(1, (int)Math.Round(sh * s));
                dstX0 = (dw - regionW) / 2; // positive => letterbox bars
                dstY0 = (dh - regionH) / 2;
                break;
            }
        }

        for (int y = 0; y < regionH; y++)
        {
            int dy = dstY0 + y;
            if (dy < 0 || dy >= dh)
            {
                continue;
            }
            int sy = Math.Min(sh - 1, (int)((long)y * sh / regionH));
            int dstRow = dy * dw;
            int srcRow = sy * sw;
            for (int x = 0; x < regionW; x++)
            {
                int dx = dstX0 + x;
                if (dx < 0 || dx >= dw)
                {
                    continue;
                }
                int sx = Math.Min(sw - 1, (int)((long)x * sw / regionW));
                int sp = (srcRow + sx) * 4;
                int dp = (dstRow + dx) * 4;
                dst[dp] = src[sp];
                dst[dp + 1] = src[sp + 1];
                dst[dp + 2] = src[sp + 2];
                dst[dp + 3] = 255;
            }
        }
    }
}
#endif

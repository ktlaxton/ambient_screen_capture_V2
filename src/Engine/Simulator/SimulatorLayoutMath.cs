#if SIMULATOR_ENABLED
namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.6). The <b>pure</b> geometry behind the composite canvas: it maps the
/// virtual-desktop monitor rects into canvas pixels under an auto-fit scale, a user zoom factor, and a
/// pan offset. <see cref="SimulatorWindow.Reflow"/> uses it to place every backdrop / effect viewport /
/// peripheral, and the wheel handler uses <see cref="PanForZoom"/> to keep the point under the cursor
/// fixed while zooming. Extracted as a side-effect-free static so the pan/zoom/Fit math is unit-testable
/// without a WPF window. Compiled out of Release.
/// </summary>
public static class SimulatorLayoutMath
{
    /// <summary>A virtual-desktop monitor rect in device pixels (negative coords / gaps allowed).</summary>
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>A placed element in canvas pixels.</summary>
    public readonly record struct Placed(double Left, double Top, double Width, double Height);

    /// <summary>
    /// The resolved canvas mapping: a uniform <see cref="Scale"/> plus an <see cref="OffsetX"/>/
    /// <see cref="OffsetY"/> origin, anchored at the bounding box's <see cref="MinX"/>/<see cref="MinY"/>.
    /// </summary>
    public readonly record struct CanvasLayout(double Scale, double OffsetX, double OffsetY, int MinX, int MinY)
    {
        /// <summary>Projects one virtual-desktop rect into canvas pixels (min size 1 so nothing collapses).</summary>
        public Placed Place(int x, int y, int width, int height) => new(
            OffsetX + (x - MinX) * Scale,
            OffsetY + (y - MinY) * Scale,
            System.Math.Max(1, width * Scale),
            System.Math.Max(1, height * Scale));
    }

    /// <summary>
    /// Computes the canvas mapping for the given rects. <paramref name="availW"/>/<paramref name="availH"/>
    /// are the canvas client area already net of <paramref name="pad"/> on each side. The auto-fit scale
    /// fits the whole bounding box; <paramref name="userZoom"/> multiplies it; the content is then centered
    /// and shifted by (<paramref name="panX"/>, <paramref name="panY"/>). With <c>userZoom==1</c> and
    /// <c>pan==0</c> this reproduces the pre-10.6 auto-fit exactly. Returns <c>false</c> for no rects.
    /// </summary>
    public static bool TryCompute(
        IReadOnlyList<Rect> rects,
        double availW,
        double availH,
        double pad,
        double userZoom,
        double panX,
        double panY,
        out CanvasLayout layout)
    {
        layout = default;
        if (rects is null || rects.Count == 0)
        {
            return false;
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var r in rects)
        {
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.Width > maxX) maxX = r.X + r.Width;
            if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
        }

        double spanW = System.Math.Max(1, maxX - minX);
        double spanH = System.Math.Max(1, maxY - minY);
        double aw = System.Math.Max(1, availW);
        double ah = System.Math.Max(1, availH);

        double fit = System.Math.Min(aw / spanW, ah / spanH);
        double scale = fit * userZoom;
        double offsetX = pad + (aw - spanW * scale) / 2 + panX;
        double offsetY = pad + (ah - spanH * scale) / 2 + panY;

        layout = new CanvasLayout(scale, offsetX, offsetY, minX, minY);
        return true;
    }

    /// <summary>
    /// Returns the pan offset that, combined with <paramref name="newZoom"/>, keeps the virtual-desktop
    /// point currently under (<paramref name="cursorX"/>, <paramref name="cursorY"/>) anchored under the
    /// cursor after the zoom (standard zoom-to-cursor). Pass the result back to <see cref="TryCompute"/>
    /// as the new pan. Returns the unchanged old pan if the rects/sizes are degenerate.
    /// </summary>
    public static (double PanX, double PanY) PanForZoom(
        IReadOnlyList<Rect> rects,
        double availW,
        double availH,
        double pad,
        double oldZoom,
        double oldPanX,
        double oldPanY,
        double newZoom,
        double cursorX,
        double cursorY)
    {
        if (!TryCompute(rects, availW, availH, pad, oldZoom, oldPanX, oldPanY, out var old)
            || !TryCompute(rects, availW, availH, pad, newZoom, 0, 0, out var basis)
            || old.Scale <= 0)
        {
            return (oldPanX, oldPanY);
        }

        // The virtual-desktop point currently under the cursor.
        double worldX = old.MinX + (cursorX - old.OffsetX) / old.Scale;
        double worldY = old.MinY + (cursorY - old.OffsetY) / old.Scale;

        // The origin that would place that world point back under the cursor at the new scale.
        double targetOffsetX = cursorX - (worldX - basis.MinX) * basis.Scale;
        double targetOffsetY = cursorY - (worldY - basis.MinY) * basis.Scale;

        // basis was computed with pan 0, so its offset is the zero-pan centering origin.
        return (targetOffsetX - basis.OffsetX, targetOffsetY - basis.OffsetY);
    }

    /// <summary>
    /// Snaps a dragged monitor's proposed top-left (<paramref name="x"/>, <paramref name="y"/>) so its
    /// edges click onto a neighbor's edges — adjacency (left↔right) or alignment (left↔left, etc.) —
    /// within <paramref name="snapDistance"/> virtual-desktop pixels. Each axis snaps independently to its
    /// nearest qualifying edge (the Windows "snap into place" feel). Returns the proposed position
    /// unchanged when nothing is within range.
    /// </summary>
    public static (int X, int Y) Snap(int x, int y, int w, int h, IReadOnlyList<Rect> others, double snapDistance)
    {
        int bestX = x, bestY = y;
        double bestDX = snapDistance, bestDY = snapDistance;
        int right = x + w, bottom = y + h;

        if (others is not null)
        {
            foreach (var o in others)
            {
                int oL = o.X, oR = o.X + o.Width, oT = o.Y, oB = o.Y + o.Height;

                Consider(ref bestX, ref bestDX, x, oL, oL);          // left → left (align)
                Consider(ref bestX, ref bestDX, x, oR, oR);          // left → right (adjacent)
                Consider(ref bestX, ref bestDX, right, oL, oL - w);  // right → left (adjacent)
                Consider(ref bestX, ref bestDX, right, oR, oR - w);  // right → right (align)

                Consider(ref bestY, ref bestDY, y, oT, oT);          // top → top
                Consider(ref bestY, ref bestDY, y, oB, oB);          // top → bottom
                Consider(ref bestY, ref bestDY, bottom, oT, oT - h); // bottom → top
                Consider(ref bestY, ref bestDY, bottom, oB, oB - h); // bottom → bottom
            }
        }
        return (bestX, bestY);
    }

    private static void Consider(ref int best, ref double bestDelta, int edge, int target, int resultPos)
    {
        double d = System.Math.Abs(edge - target);
        if (d < bestDelta)
        {
            bestDelta = d;
            best = resultPos;
        }
    }
}
#endif

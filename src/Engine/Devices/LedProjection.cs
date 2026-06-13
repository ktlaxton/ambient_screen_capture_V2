using AmbientFx.Bridge;
using AmbientFx.Models;

namespace AmbientFx.Devices;

/// <summary>
/// Pure spatial mapping for ambient RGB peripherals (Story 8.1, AC3): projects a device's
/// LED positions onto the source screen's edge-zone colors — the physical-hardware sibling
/// of <see cref="Services.MonitorLayout"/>. An LED nearest the left side of its device is fed
/// by the source's left edge at the matching height, and so on for every side.
/// Stateless, thread-safe, and free of vendor SDK types so it is reusable across vendors
/// and table-testable without hardware.
/// </summary>
public static class LedProjection
{
    /// <summary>
    /// One LED position. Raw units are vendor-specific (mm for keyboards/mice, logical units
    /// for strips/fans); after <see cref="Normalize"/> both axes are 0..1 with Y growing down.
    /// </summary>
    public readonly record struct LedPoint(double X, double Y);

    /// <summary>
    /// Normalizes raw LED positions into the device's own 0..1 bounding box, erasing the
    /// mm-vs-logical-unit difference between device classes (AC3). A degenerate axis
    /// (single LED, or all LEDs on one line) maps to the centered 0.5.
    /// </summary>
    public static LedPoint[] Normalize(IReadOnlyList<LedPoint> raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<LedPoint>();
        }

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var p in raw)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
            {
                continue;
            }
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        double spanX = maxX - minX;
        double spanY = maxY - minY;
        var result = new LedPoint[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            var p = raw[i];
            double x = double.IsFinite(p.X) && spanX > 0 ? (p.X - minX) / spanX : 0.5;
            double y = double.IsFinite(p.Y) && spanY > 0 ? (p.Y - minY) / spanY : 0.5;
            result[i] = new LedPoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }
        return result;
    }

    /// <summary>
    /// Colors each normalized LED from the edge zone it sits closest to. Conventions match
    /// the rest of the pipeline (<see cref="EdgeColors"/>, Story 7.5): top/bottom zones run
    /// left→right indexed by X, left/right zones run top→bottom indexed by Y. Ties resolve
    /// deterministically in top, bottom, left, right order. Returns one [r,g,b] (sRGB 0-255)
    /// per LED; <paramref name="brightness"/> scales linearly; a missing/empty edge is black.
    /// </summary>
    public static int[][] Project(IReadOnlyList<LedPoint> leds, EdgeColors edges, float brightness) =>
        Project(leds, edges, brightness, DeviceAnchors.Auto, flip: false);

    /// <summary>
    /// Placement-aware projection (Story 8.2): <paramref name="anchor"/> overrides how the
    /// device maps to the screen. auto/behind keep the nearest-edge heuristic; left/right/
    /// above/below feed every LED from that single edge (indexed by the LED's position along
    /// the edge's axis); surround wraps the full screen perimeter by the LED's angle around
    /// the device center (fan rings). <paramref name="flip"/> reverses the zone direction for
    /// single-edge anchors and the winding direction for surround; it is ignored for
    /// auto/behind, where there is no single direction to reverse.
    /// </summary>
    public static int[][] Project(
        IReadOnlyList<LedPoint> leds, EdgeColors edges, float brightness, string? anchor, bool flip)
    {
        if (leds is null || leds.Count == 0)
        {
            return Array.Empty<int[]>();
        }

        double scale = float.IsFinite(brightness) ? Math.Clamp(brightness, 0f, 1f) : 1.0;
        edges ??= new EdgeColors();

        var result = new int[leds.Count][];
        for (int i = 0; i < leds.Count; i++)
        {
            double x = Math.Clamp(leds[i].X, 0, 1);
            double y = Math.Clamp(leds[i].Y, 0, 1);

            result[i] = anchor switch
            {
                DeviceAnchors.Left => SampleZone(edges.Left ?? Array.Empty<int[]>(), flip ? 1 - y : y, scale),
                DeviceAnchors.Right => SampleZone(edges.Right ?? Array.Empty<int[]>(), flip ? 1 - y : y, scale),
                DeviceAnchors.Above => SampleZone(edges.Top ?? Array.Empty<int[]>(), flip ? 1 - x : x, scale),
                DeviceAnchors.Below => SampleZone(edges.Bottom ?? Array.Empty<int[]>(), flip ? 1 - x : x, scale),
                DeviceAnchors.Surround => SampleSurround(edges, x, y, flip, scale),
                _ => SampleNearestEdge(edges, x, y, scale), // auto, behind, unknown
            };
        }
        return result;
    }

    /// <summary>Nearest screen edge wins; ties resolve in top, bottom, left, right order.</summary>
    private static int[] SampleNearestEdge(EdgeColors edges, double x, double y, double scale)
    {
        (double Distance, int[][] Zones, double T)[] candidates =
        {
            (y, edges.Top ?? Array.Empty<int[]>(), x),
            (1 - y, edges.Bottom ?? Array.Empty<int[]>(), x),
            (x, edges.Left ?? Array.Empty<int[]>(), y),
            (1 - x, edges.Right ?? Array.Empty<int[]>(), y),
        };

        var best = candidates[0];
        for (int c = 1; c < candidates.Length; c++)
        {
            if (candidates[c].Distance < best.Distance)
            {
                best = candidates[c];
            }
        }

        return SampleZone(best.Zones, best.T, scale);
    }

    /// <summary>
    /// Walks the screen perimeter clockwise from the top-edge midpoint by the LED's angle
    /// around the device center: t∈[0,.25) top L→R, [.25,.5) right T→B, [.5,.75) bottom R→L,
    /// [.75,1) left B→T. A centered LED (no angle) reads the top-edge midpoint.
    /// </summary>
    private static int[] SampleSurround(EdgeColors edges, double x, double y, bool flip, double scale)
    {
        double dx = x - 0.5;
        double dy = y - 0.5;
        if (flip)
        {
            dx = -dx; // mirroring horizontally reverses the winding direction
        }

        double t;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            t = 0.125; // top middle
        }
        else
        {
            double turns = Math.Atan2(dx, -dy) / (2 * Math.PI); // clockwise turns from "up"
            if (turns < 0)
            {
                turns += 1;
            }
            t = (turns + 0.125) % 1;
        }

        int segment = Math.Clamp((int)(t * 4), 0, 3);
        double u = (t * 4) - segment;
        return segment switch
        {
            0 => SampleZone(edges.Top ?? Array.Empty<int[]>(), u, scale),
            1 => SampleZone(edges.Right ?? Array.Empty<int[]>(), u, scale),
            2 => SampleZone(edges.Bottom ?? Array.Empty<int[]>(), 1 - u, scale),
            _ => SampleZone(edges.Left ?? Array.Empty<int[]>(), 1 - u, scale),
        };
    }

    private static int[] SampleZone(int[][] zones, double t, double scale)
    {
        if (zones.Length == 0)
        {
            return new[] { 0, 0, 0 };
        }
        int index = Math.Clamp((int)(t * zones.Length), 0, zones.Length - 1);
        int[] zone = zones[index];
        if (zone is null || zone.Length < 3)
        {
            return new[] { 0, 0, 0 };
        }
        return new[]
        {
            Math.Clamp((int)Math.Round(zone[0] * scale), 0, 255),
            Math.Clamp((int)Math.Round(zone[1] * scale), 0, 255),
            Math.Clamp((int)Math.Round(zone[2] * scale), 0, 255),
        };
    }
}

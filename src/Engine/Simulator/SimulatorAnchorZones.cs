#if SIMULATOR_ENABLED
using AmbientFx.Devices;
using AmbientFx.Models;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). Pure geometry for the peripheral drag-and-snap experience:
/// the drop zones drawn around the SOURCE monitor while a device chip is dragged map 1:1 onto the
/// seven REAL <see cref="DeviceAnchors"/> the shipped product supports — left/right/above/below edge
/// strips, a "behind" center rect, a "surround" outer frame band, and an "auto" corner slot — so
/// what the user can drop is exactly what the production <c>setDevicePlacement</c> model can do.
/// Positions are canvas pixels (the same space <see cref="SimulatorLayoutMath.CanvasLayout.Place"/>
/// emits). GEOMETRY ONLY: LED colors always come from the real projection via
/// <c>VisualizationBackend</c> (fidelity invariant). Compiled out of Release.
/// </summary>
public static class SimulatorAnchorZones
{
    /// <summary>One drop zone: the anchor it commits, and its canvas-pixel rect. Surround is a
    /// frame made of four rects sharing the same anchor tag.</summary>
    public readonly record struct Zone(string Anchor, double X, double Y, double W, double H);

    /// <summary>
    /// Builds the drop zones around the source monitor's placed rect. <paramref name="thickness"/>
    /// is each band's depth, <paramref name="gap"/> the space between the monitor edge and the
    /// first band (both canvas px; coerced to sane minimums).
    /// </summary>
    public static IReadOnlyList<Zone> Zones(
        double srcX, double srcY, double srcW, double srcH, double thickness, double gap)
    {
        double t = System.Math.Max(1, thickness);
        double g = System.Math.Max(0, gap);
        double outer = g * 2 + t; // where the surround band starts, beyond the edge strips

        return new[]
        {
            new Zone(DeviceAnchors.Left, srcX - g - t, srcY, t, srcH),
            new Zone(DeviceAnchors.Right, srcX + srcW + g, srcY, t, srcH),
            new Zone(DeviceAnchors.Above, srcX, srcY - g - t, srcW, t),
            new Zone(DeviceAnchors.Below, srcX, srcY + srcH + g, srcW, t),
            new Zone(DeviceAnchors.Behind, srcX + srcW * 0.25, srcY + srcH * 0.25, srcW * 0.5, srcH * 0.5),
            new Zone(DeviceAnchors.Surround, srcX - outer - t, srcY - outer - t, srcW + 2 * (outer + t), t),
            new Zone(DeviceAnchors.Surround, srcX - outer - t, srcY + srcH + outer, srcW + 2 * (outer + t), t),
            new Zone(DeviceAnchors.Surround, srcX - outer - t, srcY - outer, t, srcH + 2 * outer),
            new Zone(DeviceAnchors.Surround, srcX + srcW + outer, srcY - outer, t, srcH + 2 * outer),
            new Zone(DeviceAnchors.Auto, srcX + srcW + g, srcY + srcH + g, t, t),
        };
    }

    /// <summary>
    /// Resolves the anchor for a drop at (<paramref name="px"/>, <paramref name="py"/>): the nearest
    /// zone (0 when inside one; earlier zones win exact ties) within <paramref name="snapDistance"/>;
    /// anywhere else resolves to <see cref="DeviceAnchors.Auto"/> — "just put it somewhere" is the
    /// product's own default.
    /// </summary>
    public static string HitTest(IReadOnlyList<Zone> zones, double px, double py, double snapDistance)
    {
        string best = DeviceAnchors.Auto;
        double bestDistance = double.PositiveInfinity;
        if (zones is not null)
        {
            foreach (var zone in zones)
            {
                double dx = System.Math.Max(System.Math.Max(zone.X - px, 0), px - (zone.X + zone.W));
                double dy = System.Math.Max(System.Math.Max(zone.Y - py, 0), py - (zone.Y + zone.H));
                double distance = System.Math.Sqrt(dx * dx + dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = zone.Anchor;
                }
            }
        }
        return bestDistance <= snapDistance ? best : DeviceAnchors.Auto;
    }

    /// <summary>
    /// Canvas positions for a surround-anchored device's LED dots: each normalized LED is walked
    /// onto the source rect's perimeter using the SAME angle convention as
    /// <see cref="LedProjection"/>'s surround sampling (clockwise from the top-edge midpoint by the
    /// LED's angle around the device center; <paramref name="flip"/> reverses the winding; a
    /// centered LED reads the top midpoint) — so a dot sits exactly where the color it shows is
    /// sampled from. Positions only; colors stay with the real projection.
    /// </summary>
    public static (double X, double Y)[] PerimeterPositions(
        double srcX, double srcY, double srcW, double srcH,
        IReadOnlyList<LedProjection.LedPoint> normalizedLeds, bool flip)
    {
        if (normalizedLeds is null || normalizedLeds.Count == 0)
        {
            return Array.Empty<(double, double)>();
        }

        var result = new (double X, double Y)[normalizedLeds.Count];
        for (int i = 0; i < normalizedLeds.Count; i++)
        {
            double dx = System.Math.Clamp(normalizedLeds[i].X, 0, 1) - 0.5;
            double dy = System.Math.Clamp(normalizedLeds[i].Y, 0, 1) - 0.5;
            if (flip)
            {
                dx = -dx; // mirrors LedProjection.SampleSurround: flip reverses the winding
            }

            double t;
            if (System.Math.Abs(dx) < 1e-9 && System.Math.Abs(dy) < 1e-9)
            {
                t = 0.125; // top middle — same convention as the projection
            }
            else
            {
                double turns = System.Math.Atan2(dx, -dy) / (2 * System.Math.PI);
                if (turns < 0)
                {
                    turns += 1;
                }
                t = (turns + 0.125) % 1;
            }

            int segment = System.Math.Clamp((int)(t * 4), 0, 3);
            double u = (t * 4) - segment;
            result[i] = segment switch
            {
                0 => (srcX + u * srcW, srcY),              // top edge, left → right
                1 => (srcX + srcW, srcY + u * srcH),       // right edge, top → bottom
                2 => (srcX + (1 - u) * srcW, srcY + srcH), // bottom edge, right → left
                _ => (srcX, srcY + (1 - u) * srcH),        // left edge, bottom → top
            };
        }
        return result;
    }
}
#endif

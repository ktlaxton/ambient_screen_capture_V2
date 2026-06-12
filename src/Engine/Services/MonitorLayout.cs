using AmbientFx.Models;

namespace AmbientFx.Services;

/// <summary>
/// Pure helper that maps physical monitor layout (FR7): given a source and a target monitor,
/// computes which side of the source the target sits on, so the correct screen edge
/// "spills" onto the physically adjacent target monitor.
/// Stateless and thread-safe; unit-testable with no OS dependencies.
/// </summary>
public static class MonitorLayout
{
    /// <summary>Normalized center-distance below which the monitors are considered co-located.</summary>
    private const double DeadZone = 0.05;

    /// <summary>
    /// Computes the spatial relation of <paramref name="target"/> relative to <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The capture (source) monitor.</param>
    /// <param name="target">The effect (target) monitor.</param>
    /// <returns>
    /// "left", "right", "above" or "below" (screen Y grows downward), or "none" when the ids match,
    /// either monitor has zero area, either argument is null, or the centers effectively coincide.
    /// </returns>
    public static string ComputeRelation(MonitorInfo source, MonitorInfo target)
    {
        // Defensive: a layout helper must never take the host down (NFR5).
        if (source is null || target is null)
        {
            return "none";
        }

        if (string.Equals(source.Id, target.Id, StringComparison.Ordinal))
        {
            return "none";
        }

        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return "none";
        }

        double sourceCenterX = source.X + source.Width / 2.0;
        double sourceCenterY = source.Y + source.Height / 2.0;
        double targetCenterX = target.X + target.Width / 2.0;
        double targetCenterY = target.Y + target.Height / 2.0;

        double dx = targetCenterX - sourceCenterX;
        double dy = targetCenterY - sourceCenterY;

        // Normalize by the average extent on each axis so the dominant direction is
        // judged in "monitor widths/heights", not raw pixels (mixed-resolution safe).
        double ndx = dx / ((source.Width + target.Width) / 2.0);
        double ndy = dy / ((source.Height + target.Height) / 2.0);

        if (Math.Abs(ndx) < DeadZone && Math.Abs(ndy) < DeadZone)
        {
            return "none";
        }

        if (Math.Abs(ndx) >= Math.Abs(ndy))
        {
            return ndx > 0 ? "right" : "left";
        }

        // Screen coordinates: Y grows downward, so a positive dy means physically below.
        return ndy > 0 ? "below" : "above";
    }
}

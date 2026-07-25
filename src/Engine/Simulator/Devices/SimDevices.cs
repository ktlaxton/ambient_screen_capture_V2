#if SIMULATOR_ENABLED
using AmbientFx.Devices;
using LedPoint = AmbientFx.Devices.LedProjection.LedPoint;

namespace AmbientFx.Simulator.Devices;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.4). The simulated RGB device set — keyboard / mouse / light strip —
/// mirroring the browser simulator's <c>SIM_DEVICES</c> (the C# port of the idea, not the file). Each
/// device carries plausible normalized (0..1) LED layouts so the placement anchors read sensibly.
/// Compiled out of Release.
/// </summary>
public static class SimDevices
{
    public const string KeyboardId = "0:K95 RGB Platinum (sim)";
    public const string MouseId = "1:Dark Core RGB Pro (sim)";
    public const string StripId = "2:LS100 Light Strip (sim)";

    public static IReadOnlyList<RgbBackendDevice> Build() => new[]
    {
        new RgbBackendDevice
        {
            Id = KeyboardId,
            Name = "K95 RGB Platinum (sim)",
            Type = "Keyboard",
            NormalizedLeds = Grid(18, 6), // 108 LEDs
        },
        new RgbBackendDevice
        {
            Id = MouseId,
            Name = "Dark Core RGB Pro (sim)",
            Type = "Mouse",
            NormalizedLeds = Cluster(),   // 4 LEDs
        },
        new RgbBackendDevice
        {
            Id = StripId,
            Name = "LS100 Light Strip (sim)",
            Type = "LedStripe",
            NormalizedLeds = Line(27),    // 27 LEDs
        },
    };

    /// <summary>A cols×rows grid normalized to 0..1 (single-axis degenerates to centered 0.5).</summary>
    public static LedPoint[] Grid(int cols, int rows)
    {
        var points = new LedPoint[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double x = cols <= 1 ? 0.5 : (double)c / (cols - 1);
                double y = rows <= 1 ? 0.5 : (double)r / (rows - 1);
                points[r * cols + c] = new LedPoint(x, y);
            }
        }
        return points;
    }

    /// <summary>A horizontal line of n LEDs at mid-height.</summary>
    public static LedPoint[] Line(int n)
    {
        var points = new LedPoint[n];
        for (int i = 0; i < n; i++)
        {
            points[i] = new LedPoint(n <= 1 ? 0.5 : (double)i / (n - 1), 0.5);
        }
        return points;
    }

    /// <summary>A small 2×2 cluster (a mouse's few zones).</summary>
    public static LedPoint[] Cluster() => new[]
    {
        new LedPoint(0.35, 0.30),
        new LedPoint(0.65, 0.30),
        new LedPoint(0.50, 0.55),
        new LedPoint(0.50, 0.80),
    };
}
#endif

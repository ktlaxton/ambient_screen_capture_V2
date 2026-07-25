#if SIMULATOR_ENABLED
using System.Linq;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). A curated static catalog of real-world monitor footprints —
/// the size classes that cover essentially the whole market, with a recognizable example model where
/// it helps ("Odyssey G9"). Deliberately STATIC and offline: to the simulator a monitor model is just
/// its pixel rect on the virtual desktop (no DPI/refresh/panel concept), the distinct footprints
/// number ~15, and they change so rarely that a baked-in list beats any network dependency (no
/// dependable free monitor-spec API exists anyway). Adding a new market entry is a one-line edit.
/// Feeds the toolbar's "+ Add monitor ▾" menu and the monitor card's size dropdown. Compiled out of
/// Release.
/// </summary>
public static class SimulatorMonitorCatalog
{
    /// <summary>One catalog row: menu category, display label (always includes the dimensions),
    /// and the footprint in virtual-desktop pixels.</summary>
    public readonly record struct Entry(string Category, string Label, int Width, int Height);

    public const string Laptop = "Laptop";
    public const string Desktop = "Desktop";
    public const string Ultrawide = "Ultrawide";
    public const string Portrait = "Portrait (rotated)";

    public static IReadOnlyList<Entry> All { get; } = new Entry[]
    {
        new(Laptop, "13–14″ laptop — 1920 × 1200", 1920, 1200),
        new(Laptop, "14″ hi-res laptop (MacBook-class) — 2880 × 1800", 2880, 1800),

        new(Desktop, "24″ Full HD — 1920 × 1080", 1920, 1080),
        new(Desktop, "27″ QHD — 2560 × 1440", 2560, 1440),
        new(Desktop, "27–32″ 4K — 3840 × 2160", 3840, 2160),
        new(Desktop, "27″ 5K (Studio Display) — 5120 × 2880", 5120, 2880),

        new(Ultrawide, "29″ ultrawide FHD — 2560 × 1080", 2560, 1080),
        new(Ultrawide, "34″ ultrawide QHD (LG UltraGear) — 3440 × 1440", 3440, 1440),
        new(Ultrawide, "40″ 5K2K ultrawide (LG 40WP95C) — 5120 × 2160", 5120, 2160),
        new(Ultrawide, "49″ super-ultrawide (Odyssey G9) — 5120 × 1440", 5120, 1440),
        new(Ultrawide, "57″ dual-4K (Odyssey Neo G9 57) — 7680 × 2160", 7680, 2160),

        new(Portrait, "Full HD portrait — 1080 × 1920", 1080, 1920),
        new(Portrait, "QHD portrait — 1440 × 2560", 1440, 2560),
    };

    /// <summary>Menu categories, in catalog order, without duplicates.</summary>
    public static IReadOnlyList<string> Categories { get; } = All
        .Select(e => e.Category)
        .Distinct()
        .ToList();

    /// <summary>The first catalog entry matching a footprint (the card's dropdown pre-selection),
    /// or null when the monitor is a custom size.</summary>
    public static Entry? FindByDimensions(int width, int height)
    {
        foreach (var entry in All)
        {
            if (entry.Width == width && entry.Height == height)
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>The catalog entry with the given label, or null.</summary>
    public static Entry? FindByLabel(string? label)
    {
        foreach (var entry in All)
        {
            if (string.Equals(entry.Label, label, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }
}
#endif

#if SIMULATOR_ENABLED
using System.Linq;
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (real-world monitor catalog): the curated static list is sane — positive footprints,
/// unique labels that state their dimensions, ordered categories — and the lookups used by the card
/// and toolbar resolve correctly.
/// </summary>
public sealed class SimulatorMonitorCatalogTests
{
    [Fact]
    public void AllEntries_HavePositiveDimensions_AndUniqueLabels()
    {
        Assert.True(SimulatorMonitorCatalog.All.Count >= 12);
        Assert.All(SimulatorMonitorCatalog.All, e =>
        {
            Assert.True(e.Width > 0 && e.Height > 0, e.Label);
            Assert.False(string.IsNullOrWhiteSpace(e.Category));
            // Every label states its footprint so the menu never hides the actual numbers.
            Assert.Contains($"{e.Width} × {e.Height}", e.Label);
        });
        Assert.Equal(
            SimulatorMonitorCatalog.All.Count,
            SimulatorMonitorCatalog.All.Select(e => e.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Categories_AreDistinct_InCatalogOrder()
    {
        Assert.Equal(
            new[]
            {
                SimulatorMonitorCatalog.Laptop,
                SimulatorMonitorCatalog.Desktop,
                SimulatorMonitorCatalog.Ultrawide,
                SimulatorMonitorCatalog.Portrait,
            },
            SimulatorMonitorCatalog.Categories);
    }

    [Fact]
    public void FindByDimensions_ResolvesKnownFootprints_NullForCustom()
    {
        Assert.Equal("27″ QHD — 2560 × 1440", SimulatorMonitorCatalog.FindByDimensions(2560, 1440)!.Value.Label);
        Assert.Contains("Odyssey G9", SimulatorMonitorCatalog.FindByDimensions(5120, 1440)!.Value.Label);
        Assert.Null(SimulatorMonitorCatalog.FindByDimensions(1234, 567));
    }

    [Fact]
    public void FindByLabel_RoundTripsEveryEntry()
    {
        foreach (var entry in SimulatorMonitorCatalog.All)
        {
            Assert.Equal(entry, SimulatorMonitorCatalog.FindByLabel(entry.Label));
        }
        Assert.Null(SimulatorMonitorCatalog.FindByLabel("Custom size"));
        Assert.Null(SimulatorMonitorCatalog.FindByLabel(null));
    }
}
#endif

#if SIMULATOR_ENABLED
using System.Linq;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (effect dropdown): the embedded <c>web/src/effects/manifest.json</c> is present in the
/// Debug build and parses into the full catalog; malformed input degrades to the edge-glow fallback.
/// </summary>
public sealed class SimulatorEffectsCatalogTests
{
    [Fact]
    public void Load_EmbeddedManifest_YieldsTheFullCatalog()
    {
        var catalog = SimulatorEffectsCatalog.Load(NullLogger.Instance);

        Assert.True(catalog.Count >= 10, $"expected the full manifest, got {catalog.Count} entries");
        Assert.Contains(catalog, e => e.Id == "edge-glow" && e.Name == "Edge Glow");
        Assert.All(catalog, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
        });
        Assert.Equal(catalog.Count, catalog.Select(e => e.Id).Distinct().Count()); // ids unique
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json {{{")]
    [InlineData("{}")]
    [InlineData("""{ "effects": "nope" }""")]
    [InlineData("""{ "effects": [] }""")]
    [InlineData("""{ "effects": [ { "name": "no id" } ] }""")]
    public void Parse_Unusable_ReturnsFallback(string? json)
    {
        var entry = Assert.Single(SimulatorEffectsCatalog.Parse(json));
        Assert.Equal("edge-glow", entry.Id);
    }

    [Fact]
    public void Parse_SkipsIdlessEntries_NameFallsBackToId()
    {
        var catalog = SimulatorEffectsCatalog.Parse("""
        {
          "effects": [
            { "id": "fire", "name": "Fire" },
            { "name": "orphan" },
            { "id": "rain" }
          ]
        }
        """);

        Assert.Equal(2, catalog.Count);
        Assert.Equal("Fire", catalog[0].Name);
        Assert.Equal("rain", catalog[1].Name); // name fell back to the id
    }
}
#endif

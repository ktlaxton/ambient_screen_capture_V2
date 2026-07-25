#if SIMULATOR_ENABLED
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The C# view of <c>web/src/effects/manifest.json</c> — the
/// single source of truth for effect ids/display names — embedded Debug-only by the csproj (the
/// simulator is always built from the repo, so the list cannot drift from the web bundle). Feeds
/// the monitor card's effect dropdown; the simulator runs sim-Premium so every entry is usable.
/// Compiled out of Release.
/// </summary>
public static class SimulatorEffectsCatalog
{
    /// <summary>One selectable effect: the bridge id (e.g. "edge-glow") and its display name.</summary>
    public readonly record struct EffectEntry(string Id, string Name);

    /// <summary>LogicalName of the embedded manifest (see AmbientFx.csproj, Debug-only ItemGroup).</summary>
    public const string ResourceName = "AmbientFx.Simulator.effects-manifest.json";

    private static readonly EffectEntry[] FallbackCatalog = { new("edge-glow", "Edge Glow") };

    private static IReadOnlyList<EffectEntry>? _cached;

    /// <summary>Loads (and caches) the catalog from the embedded manifest. Missing/malformed
    /// resource degrades to a one-entry fallback ("edge-glow"). Never throws.</summary>
    public static IReadOnlyList<EffectEntry> Load(ILogger logger)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        string? json = null;
        try
        {
            var assembly = typeof(SimulatorEffectsCatalog).Assembly;
            var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                string? suffixMatch = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("effects-manifest.json", StringComparison.OrdinalIgnoreCase));
                stream = suffixMatch is null ? null : assembly.GetManifestResourceStream(suffixMatch);
            }
            if (stream is not null)
            {
                using (stream)
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulator: failed to read the embedded effects manifest; using the fallback catalog.");
        }

        if (json is null)
        {
            logger.LogWarning("Simulator: effects manifest resource '{Resource}' not found; using the fallback catalog.", ResourceName);
        }

        _cached = Parse(json);
        return _cached;
    }

    /// <summary>Pure parse of the manifest shape <c>{ "effects": [ { "id", "name", … } ] }</c>.
    /// Entries without an id are skipped; a missing name falls back to the id; anything
    /// unparseable yields the fallback catalog. Never throws.</summary>
    public static IReadOnlyList<EffectEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return FallbackCatalog;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("effects", out var effects)
                || effects.ValueKind != JsonValueKind.Array)
            {
                return FallbackCatalog;
            }

            var list = new List<EffectEntry>();
            foreach (var entry in effects.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string? id = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                string? name = entry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()
                    : null;
                list.Add(new EffectEntry(id!, string.IsNullOrWhiteSpace(name) ? id! : name!));
            }
            return list.Count > 0 ? list : FallbackCatalog;
        }
        catch
        {
            return FallbackCatalog;
        }
    }
}
#endif

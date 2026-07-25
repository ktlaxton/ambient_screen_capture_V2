#if SIMULATOR_ENABLED
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator). A fabricated monitor topology plus per-monitor
/// synthetic-capture settings, loaded from a JSON scenario fixture. This is the single contract
/// shared across the epic: the simulated services consume it (10.1), the composite window draws it
/// (10.2), the content sources extend it (10.3), and the editor / library / automation hook author
/// and replay it (10.5).
/// </summary>
/// <remarks>
/// JSON schema (camelCase, System.Text.Json), schema <see cref="Version"/> 2:
/// <code>
/// {
///   "version": 2,
///   "name": "SIM_MONITORS",
///   "sourceMonitorId": "\\\\.\\SIM-DISPLAY1",   // optional; defaults to the primary monitor's id
///   "activeEffectId": "edge-glow",              // optional (v2): global effect applied on load
///   "globalMaxFps": 60,                         // optional (v2): global FPS ceiling applied on load
///   "devicePlacements": { "0:K95 (sim)": { "anchor", "flip", "brightness", "enabled" } }, // optional (v2)
///   "monitors": [
///     { "id", "name", "x", "y", "width", "height", "isPrimary", "pattern", "maxFps", "effect" }, ...
///   ]
/// }
/// </code>
/// Bounds are virtual-desktop device pixels; negative <c>x</c>/<c>y</c> are valid (monitors left of
/// or above the primary). Orientation is modeled as <c>width &lt; height</c> — there is no rotation
/// field, deliberately, to avoid a <see cref="MonitorInfo"/>/bridge contract change (Epic 10). The
/// loader is defensive: a malformed or missing fixture degrades to a clear log plus a sane default
/// topology and never throws. v1 files load unchanged (the v2 fields simply stay null) — the loader
/// is tolerant by construction, so there is no version gate.
/// </remarks>
public sealed class SimulatorScenario
{
    /// <summary>Embedded resource file name of the first checked-in fixture (reproduces SIM_MONITORS).</summary>
    public const string DefaultResource = "sim-monitors.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public int Version { get; set; } = 2;

    public string Name { get; set; } = "Untitled";

    /// <summary>Which monitor the engine captures. Null/unknown resolves to the primary monitor's id.</summary>
    public string? SourceMonitorId { get; set; }

    /// <summary>v2 (full-scene presets): the global effect id applied on load. Null = engine default.</summary>
    public string? ActiveEffectId { get; set; }

    /// <summary>v2 (full-scene presets): the global FPS ceiling applied on load. Null = engine default.</summary>
    public int? GlobalMaxFps { get; set; }

    /// <summary>
    /// v2 (full-scene presets): peripheral placements captured with the scene, keyed by stable device
    /// id — the same shape as <see cref="ApplicationSettings.DevicePlacements"/>. Null/empty = defaults.
    /// </summary>
    public Dictionary<string, DevicePlacement>? DevicePlacements { get; set; }

    public List<SimulatorMonitor> Monitors { get; set; } = new();

    /// <summary>
    /// Loads the default checked-in fixture from the engine assembly's embedded resources. Falls back
    /// to a built-in single-monitor topology if the resource is missing or malformed. Never throws.
    /// </summary>
    public static SimulatorScenario LoadDefault(ILogger logger)
    {
        var scenario = LoadEmbedded(DefaultResource, logger);
        if (scenario is not null)
        {
            return scenario;
        }

        logger.LogWarning("Simulator: default scenario '{Resource}' unavailable; using built-in fallback topology.", DefaultResource);
        return Fallback();
    }

    /// <summary>Loads a scenario from a JSON file path. Returns a fallback topology on any failure.</summary>
    public static SimulatorScenario LoadFromFile(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Simulator: scenario file not found at '{Path}'; using fallback topology.", path);
                return Fallback();
            }

            var scenario = Parse(File.ReadAllText(path), logger);
            scenario.Name = string.IsNullOrWhiteSpace(scenario.Name) || scenario.Name == "Untitled"
                ? Path.GetFileNameWithoutExtension(path)
                : scenario.Name;
            return scenario;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulator: failed to load scenario from '{Path}'; using fallback topology.", path);
            return Fallback();
        }
    }

    /// <summary>
    /// Loads a scenario from an embedded resource whose name ends with <paramref name="resourceSuffix"/>.
    /// Returns null when no matching resource exists; returns a fallback topology on a parse failure.
    /// </summary>
    public static SimulatorScenario? LoadEmbedded(string resourceSuffix, ILogger logger)
    {
        try
        {
            var assembly = typeof(SimulatorScenario).Assembly;
            string? name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd(), logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulator: failed to load embedded scenario '{Resource}'; using fallback topology.", resourceSuffix);
            return Fallback();
        }
    }

    /// <summary>
    /// Story 10.5 save: returns a new scenario whose geometry/primary come from <paramref name="monitors"/>
    /// (the live edited topology) while per-monitor <see cref="SimulatorMonitor.Pattern"/>/<c>MaxFps</c>/
    /// <c>Scale</c>/<c>Content</c> are PRESERVED from this scenario by id (added monitors get defaults,
    /// removed monitors drop out) — so the editor's save does not silently lose those fields.
    /// </summary>
    public SimulatorScenario WithTopology(IReadOnlyList<MonitorInfo> monitors)
    {
        var previous = new Dictionary<string, SimulatorMonitor>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Monitors)
        {
            previous[m.Id] = m;
        }

        var result = new SimulatorScenario
        {
            Version = Version,
            Name = Name,
            SourceMonitorId = SourceMonitorId,
            ActiveEffectId = ActiveEffectId,
            GlobalMaxFps = GlobalMaxFps,
            DevicePlacements = DevicePlacements is { Count: > 0 }
                ? DevicePlacements.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                : null,
        };
        foreach (var mi in monitors)
        {
            previous.TryGetValue(mi.Id, out var prev);
            result.Monitors.Add(new SimulatorMonitor
            {
                Id = mi.Id,
                Name = mi.Name,
                X = mi.X,
                Y = mi.Y,
                Width = mi.Width,
                Height = mi.Height,
                IsPrimary = mi.IsPrimary,
                Pattern = prev?.Pattern ?? SyntheticPatterns.Gradient,
                MaxFps = prev?.MaxFps ?? 60,
                Scale = prev?.Scale ?? 1.0,
                Content = prev?.Content,
                Effect = prev?.Effect,
            });
        }

        if (string.IsNullOrEmpty(result.SourceMonitorId)
            || !result.Monitors.Any(m => string.Equals(m.Id, result.SourceMonitorId, StringComparison.OrdinalIgnoreCase)))
        {
            result.SourceMonitorId = (result.Monitors.FirstOrDefault(m => m.IsPrimary) ?? result.Monitors.FirstOrDefault())?.Id;
        }
        return result;
    }

    /// <summary>Serializes this scenario to indented JSON (Story 10.5 save). Null v2 fields are
    /// omitted so v1-era scenes stay byte-clean.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonOptions)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>Saves this scenario to a JSON file (Story 10.5). Round-trips via <see cref="LoadFromFile"/>.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Parses + validates a scenario from JSON text. Defensive: never returns null, never throws.</summary>
    public static SimulatorScenario Parse(string json, ILogger logger)
    {
        SimulatorScenario? scenario = null;
        try
        {
            scenario = JsonSerializer.Deserialize<SimulatorScenario>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulator: scenario JSON could not be parsed; using fallback topology.");
        }

        scenario ??= Fallback();
        scenario.Validate(logger);
        return scenario;
    }

    /// <summary>A safe built-in topology used when no fixture is available: one 1080p primary at the origin.</summary>
    public static SimulatorScenario Fallback() => new()
    {
        Version = 2,
        Name = "Fallback",
        SourceMonitorId = @"\\.\SIM-DISPLAY1",
        Monitors = new List<SimulatorMonitor>
        {
            new()
            {
                Id = @"\\.\SIM-DISPLAY1",
                Name = "Primary 1080p (sim)",
                X = 0,
                Y = 0,
                Width = 1920,
                Height = 1080,
                IsPrimary = true,
                Pattern = SyntheticPatterns.Gradient,
                MaxFps = 60,
            },
        },
    };

    /// <summary>
    /// Coerces the scenario into a valid state: at least one monitor, exactly one primary, positive
    /// dimensions, sane fps, and a unique id per monitor. Logs every coercion. Never throws.
    /// </summary>
    public void Validate(ILogger logger)
    {
        if (Monitors is null || Monitors.Count == 0)
        {
            logger.LogWarning("Simulator: scenario '{Name}' has no monitors; substituting the fallback topology.", Name);
            Monitors = Fallback().Monitors;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Monitors.Count; i++)
        {
            var m = Monitors[i];
            if (string.IsNullOrWhiteSpace(m.Id))
            {
                m.Id = $@"\\.\SIM-DISPLAY{i + 1}";
                logger.LogWarning("Simulator: monitor #{Index} had no id; assigned '{Id}'.", i, m.Id);
            }

            if (!seenIds.Add(m.Id))
            {
                string original = m.Id;
                m.Id = $"{original}#{i + 1}";
                seenIds.Add(m.Id);
                logger.LogWarning("Simulator: duplicate monitor id '{Original}' renamed to '{Id}'.", original, m.Id);
            }

            if (string.IsNullOrWhiteSpace(m.Name))
            {
                m.Name = m.Id;
            }

            if (m.Width <= 0 || m.Height <= 0)
            {
                logger.LogWarning("Simulator: monitor '{Id}' had non-positive size {W}x{H}; defaulting to 1920x1080.", m.Id, m.Width, m.Height);
                m.Width = m.Width <= 0 ? 1920 : m.Width;
                m.Height = m.Height <= 0 ? 1080 : m.Height;
            }

            if (m.MaxFps <= 0)
            {
                m.MaxFps = 60;
            }

            if (!(m.Scale > 0) || double.IsNaN(m.Scale) || double.IsInfinity(m.Scale))
            {
                m.Scale = 1.0;
            }

            if (!SyntheticPatterns.IsKnown(m.Pattern))
            {
                m.Pattern = SyntheticPatterns.Gradient;
            }
        }

        if (DevicePlacements is not null)
        {
            foreach (string key in DevicePlacements.Keys.ToList())
            {
                var placement = DevicePlacements[key];
                if (string.IsNullOrWhiteSpace(key) || placement is null || !DeviceAnchors.IsValid(placement.Anchor))
                {
                    logger.LogWarning("Simulator: scenario '{Name}' dropped invalid device placement '{Key}' (anchor '{Anchor}').",
                        Name, key, placement?.Anchor);
                    DevicePlacements.Remove(key);
                    continue;
                }

                if (float.IsNaN(placement.Brightness))
                {
                    placement.Brightness = 1.0f;
                }
                placement.Brightness = Math.Clamp(placement.Brightness, 0f, 1f);
            }
        }

        int primaryCount = Monitors.Count(m => m.IsPrimary);
        if (primaryCount == 0)
        {
            Monitors[0].IsPrimary = true;
            logger.LogWarning("Simulator: scenario '{Name}' had no primary monitor; '{Id}' set primary.", Name, Monitors[0].Id);
        }
        else if (primaryCount > 1)
        {
            bool kept = false;
            foreach (var m in Monitors)
            {
                if (m.IsPrimary && !kept)
                {
                    kept = true;
                }
                else
                {
                    m.IsPrimary = false;
                }
            }
            logger.LogWarning("Simulator: scenario '{Name}' had {Count} primary monitors; kept the first.", Name, primaryCount);
        }
    }

    /// <summary>Resolves the source monitor id, falling back to the primary (then the first) monitor.</summary>
    public string ResolveSourceId()
    {
        if (!string.IsNullOrEmpty(SourceMonitorId)
            && Monitors.Any(m => string.Equals(m.Id, SourceMonitorId, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceMonitorId!;
        }

        var primary = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        return primary?.Id ?? string.Empty;
    }

    /// <summary>
    /// Projects the scenario into the <see cref="MonitorInfo"/> list the engine consumes, stamping the
    /// sentinel <c>HMonitor</c> that never reaches WGC (capture is simulated too).
    /// </summary>
    public List<MonitorInfo> ToMonitorInfos() => Monitors
        .Select(m => new MonitorInfo
        {
            Id = m.Id,
            Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
            X = m.X,
            Y = m.Y,
            Width = m.Width,
            Height = m.Height,
            IsPrimary = m.IsPrimary,
            HMonitor = SimulatedMonitorDetectionService.SentinelHMonitor,
        })
        .ToList();
}

/// <summary>One fabricated display in a <see cref="SimulatorScenario"/>.</summary>
public sealed class SimulatorMonitor
{
    /// <summary>Synthetic but stable device id, e.g. <c>\\.\SIM-DISPLAY1</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>Synthetic test pattern for this monitor's source frames: "gradient" (default), "bars", "testcard".</summary>
    public string Pattern { get; set; } = SyntheticPatterns.Gradient;

    /// <summary>Frame-emission ceiling for this monitor's synthetic source.</summary>
    public int MaxFps { get; set; } = 60;

    /// <summary>
    /// Per-monitor DPI scale factor (Story 10.5), e.g. 1.5 for "4K at 150%". Informational: the bounds
    /// above are the authoritative virtual-desktop device-pixel rect the engine sees; this records the
    /// intended scale for the editor and is an approximation of real per-monitor-V2 DPI (see SIMULATOR.md).
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Optional content assignment (Story 10.3). Null/absent = synthetic (the <see cref="Pattern"/>).</summary>
    public SimContent? Content { get; set; }

    /// <summary>
    /// v2 (full-scene presets): optional per-monitor effect override (effect id, e.g. "fire") applied
    /// on load via the real <c>setEffect</c> command. Null = the scene's global effect.
    /// </summary>
    public string? Effect { get; set; }
}

/// <summary>
/// Per-monitor content assignment (Story 10.3): what fills a virtual monitor's source frames. Only the
/// engine's single source monitor's content drives the effect; other monitors' content is composite
/// background context (documented in <c>SIMULATOR.md</c> as faithful single-source behavior).
/// </summary>
public sealed class SimContent
{
    public const string Synthetic = "synthetic";
    public const string Media = "media";
    public const string Mirror = "mirror";
    public const string Blank = "blank";

    /// <summary>One of: "synthetic" (default), "media", "mirror", "blank".</summary>
    public string Kind { get; set; } = Synthetic;

    /// <summary>For <see cref="Media"/>: an image file, or a folder of images played as a looping sequence.</summary>
    public string? MediaPath { get; set; }

    /// <summary>For <see cref="Mirror"/>: the stable id of the physical monitor to mirror via real WGC.</summary>
    public string? PhysicalMonitorId { get; set; }
}
#endif

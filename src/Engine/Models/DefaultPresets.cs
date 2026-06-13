using System.Text.Json;

namespace AmbientFx.Models;

/// <summary>
/// Curated presets shipped with the app (Story 7.2 AC6), seeded once on first run by the
/// coordinator. Snapshots deliberately leave the monitor selection empty: LoadPreset treats
/// an empty SourceMonitorId as "keep the user's current monitors/enabled state" so these
/// presets only restyle the look, never break the user's monitor setup.
/// </summary>
public static class DefaultPresets
{
    public static List<Preset> Build(ApplicationSettings current)
    {
        return new List<Preset>
        {
            Make(current, "Movie Night", "edge-glow", intensity: 0.8f, brightness: 0.6f,
                Params(("reach", 0.7), ("spread", 0.65), ("audioPulse", 0.15), ("colorBoost", 0.45),
                    ("screenColors", true))),

            Make(current, "Party Pulse", "audio-bars", intensity: 1.0f, brightness: 1.0f,
                Params(("barCount", 48), ("glow", 0.8), ("reflection", 0.5), ("style", "radial"),
                    ("screenColors", false), ("palette", "neon"))),

            Make(current, "Chill Plasma", "plasma", intensity: 0.7f, brightness: 0.7f,
                Params(("scale", 1.8), ("flowSpeed", 0.2), ("warp", 0.5), ("audioDrive", 0.3),
                    ("screenColors", true))),

            Make(current, "Deep Space", "particles", intensity: 0.85f, brightness: 0.8f,
                Params(("density", 1.4), ("baseSize", 0.9), ("flow", 0.3), ("audioPunch", 0.5),
                    ("screenColors", false), ("palette", "cool"), ("blendMode", "additive"))),

            Make(current, "Northern Calm", "aurora", intensity: 0.75f, brightness: 0.75f,
                Params(("ribbons", 4), ("sway", 0.35), ("tint", 0.7), ("audioDrive", 0.35),
                    ("screenColors", false), ("palette", "aurora"))),
        };
    }

    private static Preset Make(
        ApplicationSettings current,
        string name,
        string effectId,
        float intensity,
        float brightness,
        Dictionary<string, JsonElement> effectParams)
    {
        var snapshot = current.Clone();
        snapshot.Presets = new List<Preset>(); // snapshots never nest presets
        snapshot.ActivePresetName = string.Empty;

        // Machine-agnostic look: leave monitors/enabled untouched on load (see LoadPreset).
        snapshot.IsEnabled = false;
        snapshot.SourceMonitorId = string.Empty;
        snapshot.TargetMonitorIds = new List<string>();

        snapshot.ActiveEffectId = effectId;
        snapshot.EffectByMonitorId = new Dictionary<string, string>();
        snapshot.GlobalIntensity = intensity;
        snapshot.Brightness = brightness;
        snapshot.EffectParamsById = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            [effectId] = effectParams,
        };

        return new Preset { Name = name, Snapshot = snapshot };
    }

    private static Dictionary<string, JsonElement> Params(params (string Key, object Value)[] entries)
    {
        var bag = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in entries)
        {
            bag[key] = JsonSerializer.SerializeToElement(value);
        }
        return bag;
    }
}

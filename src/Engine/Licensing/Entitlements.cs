namespace AmbientFx.Licensing;

/// <summary>
/// THE free-vs-premium policy (Story 9.2) — every gate in the app reads from here, and the
/// free effect list is mirrored in web/src/control/premium.ts (treat like a bridge contract).
/// Free tier: ambilight on ONE target monitor with the four core effects.
/// Premium: unlimited monitors, the full effect library, per-monitor effect overrides,
/// and everything RGB-peripheral (Epic 8).
/// </summary>
public static class Entitlements
{
    /// <summary>Effects available without a license. Mirrored in web/src/control/premium.ts.</summary>
    public static readonly string[] FreeEffects = { "edge-glow", "plasma", "aurora", "particles" };

    /// <summary>Fallback when settings reference a premium effect without a license.</summary>
    public const string FallbackEffect = "edge-glow";

    public static int MaxTargetMonitors(bool premium) => premium ? int.MaxValue : 1;

    public static bool EffectAllowed(string effectId, bool premium) =>
        premium || FreeEffects.Contains(effectId);

    /// <summary>Per-monitor effect overrides (vs one global effect).</summary>
    public static bool PerMonitorEffects(bool premium) => premium;

    /// <summary>The whole Epic 8 ambient-RGB-peripherals feature.</summary>
    public static bool RgbPeripherals(bool premium) => premium;
}

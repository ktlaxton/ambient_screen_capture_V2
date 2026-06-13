namespace AmbientFx.Models;

/// <summary>
/// Per-device placement + tuning for ambient RGB peripherals (Story 8.2). Stored in
/// <see cref="ApplicationSettings.DevicePlacements"/> keyed by the device's stable id
/// (iCUE device id, not the enumeration index), so config survives replug/reconnect.
/// A device with no entry behaves like the default (Auto, full brightness, enabled).
/// </summary>
public sealed class DevicePlacement
{
    /// <summary>Where the device sits relative to the screen. See <see cref="DeviceAnchors"/>.</summary>
    public string Anchor { get; set; } = DeviceAnchors.Auto;

    /// <summary>Reverses the zone order along the fed edge (strips mounted "backwards").</summary>
    public bool Flip { get; set; }

    /// <summary>Per-device brightness multiplier 0..1, on top of the global peripheral brightness.</summary>
    public float Brightness { get; set; } = 1.0f;

    /// <summary>False excludes this device without disabling the whole feature (LEDs go dark).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True when every field is at its default — such entries are pruned from settings.</summary>
    public bool IsDefault =>
        Anchor == DeviceAnchors.Auto && !Flip && Brightness >= 0.9995f && Enabled;

    public DevicePlacement Clone() => new()
    {
        Anchor = Anchor,
        Flip = Flip,
        Brightness = Brightness,
        Enabled = Enabled,
    };
}

/// <summary>
/// Placement anchors (bridge values are camelCase strings, mirrored in bridge.ts).
/// auto/behind use the nearest-edge projection; left/right/above/below feed the device from
/// that single screen edge; surround wraps the full perimeter by LED angle (fan rings).
/// </summary>
public static class DeviceAnchors
{
    public const string Auto = "auto";
    public const string Left = "left";
    public const string Right = "right";
    public const string Above = "above";
    public const string Below = "below";
    public const string Behind = "behind";
    public const string Surround = "surround";

    public static readonly string[] All = { Auto, Left, Right, Above, Below, Behind, Surround };

    public static bool IsValid(string? value) =>
        value is Auto or Left or Right or Above or Below or Behind or Surround;
}

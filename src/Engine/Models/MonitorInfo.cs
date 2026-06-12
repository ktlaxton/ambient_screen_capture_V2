using System.Text.Json.Serialization;

namespace AmbientFx.Models;

/// <summary>
/// A connected display. Bounds are in device pixels in virtual-desktop coordinates.
/// </summary>
public class MonitorInfo
{
    /// <summary>Stable device id, e.g. @"\\.\DISPLAY1".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-friendly name (e.g. "Dell U2720Q"), falls back to the device id.</summary>
    public string Name { get; set; } = string.Empty;

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>Native HMONITOR handle. Only valid until the display configuration changes; never crosses the bridge.</summary>
    [JsonIgnore]
    public nint HMonitor { get; set; }
}

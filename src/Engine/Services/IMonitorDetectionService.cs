using AmbientFx.Models;

namespace AmbientFx.Services;

/// <summary>
/// Enumerates connected displays (device-pixel bounds, friendly names, HMONITOR handles)
/// and raises MonitorsChanged when the display configuration changes.
/// </summary>
public interface IMonitorDetectionService : IDisposable
{
    /// <summary>Fresh snapshot of all connected monitors, including current HMONITOR handles.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    void StartMonitoring();
    void StopMonitoring();

    /// <summary>Raised (on any thread) when displays are added/removed/rearranged or resolution changes.</summary>
    event EventHandler? MonitorsChanged;
}

using System.Runtime.InteropServices;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace AmbientFx.Services;

/// <summary>
/// Enumerates connected displays via <c>EnumDisplayMonitors</c> + <c>GetMonitorInfo</c>
/// (device-pixel bounds, primary flag, HMONITOR handles — the process is PMv2 DPI-aware)
/// and resolves a STABLE persistence id per monitor from <c>QueryDisplayConfig</c>'s
/// <c>monitorDevicePath</c> (stable across reboots/replug, unlike <c>\\.\DISPLAY1</c>).
/// This is what fixes the MVP's lost-monitor-selection bug end to end: settings persist
/// the stable id, and re-enumeration after topology changes re-resolves it.
/// <para>
/// <see cref="MonitorsChanged"/> is driven by <see cref="SystemEvents.DisplaySettingsChanged"/>,
/// debounced 500 ms because Windows fires it multiple times during a single topology change.
/// The event is raised on a ThreadPool (timer) thread — subscribers marshal as needed.
/// </para>
/// Thread safety: all members are safe to call from any thread.
/// </summary>
public sealed class MonitorDetectionService : IMonitorDetectionService
{
    private const int DebounceMs = 500;

    private readonly ILogger<MonitorDetectionService> _logger;
    private readonly object _lock = new();
    private readonly Timer _debounceTimer;
    private bool _monitoring;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler? MonitorsChanged;

    public MonitorDetectionService(ILogger<MonitorDetectionService> logger)
    {
        _logger = logger;
        // Created idle; each DisplaySettingsChanged pushes the single due-time out again.
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    /// <remarks>Always re-enumerates fresh — HMONITOR handles are only valid until the next topology change.</remarks>
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        try
        {
            // GDI device name (\\.\DISPLAY1) -> (stable device path, friendly name).
            Dictionary<string, (string? DevicePath, string? Friendly)> names = QueryDisplayNames();

            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref info))
                {
                    _logger.LogWarning("GetMonitorInfo failed for HMONITOR 0x{Handle:X}", hMonitor);
                    return true; // keep enumerating the rest
                }

                names.TryGetValue(info.szDevice, out (string? DevicePath, string? Friendly) resolved);

                monitors.Add(new MonitorInfo
                {
                    // Stable id preferred; volatile GDI name only when CCD data is unavailable.
                    Id = string.IsNullOrEmpty(resolved.DevicePath) ? info.szDevice : resolved.DevicePath!,
                    // EDIDs without a name yield an empty friendly string -> "Display N".
                    Name = string.IsNullOrEmpty(resolved.Friendly)
                        ? $"Display {monitors.Count + 1}"
                        : resolved.Friendly!,
                    X = info.rcMonitor.Left,
                    Y = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    HMonitor = hMonitor,
                });
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                _logger.LogWarning("EnumDisplayMonitors reported failure; returning {Count} monitor(s)", monitors.Count);
            }

            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            // Never crash the host (NFR5); an empty/partial list degrades gracefully upstream.
            _logger.LogError(ex, "Monitor enumeration failed");
        }

        _logger.LogDebug("Enumerated {Count} monitor(s)", monitors.Count);
        return monitors;
    }

    /// <inheritdoc />
    public void StartMonitoring()
    {
        lock (_lock)
        {
            if (_disposed || _monitoring)
            {
                return;
            }

            // Static event on a broadcast thread; handler must stay fast and MUST be
            // unsubscribed on Dispose or the service leaks (documented SystemEvents rule).
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _monitoring = true;
        }

        _logger.LogInformation("Display-change monitoring started");
    }

    /// <inheritdoc />
    public void StopMonitoring()
    {
        lock (_lock)
        {
            if (!_monitoring)
            {
                return;
            }

            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _monitoring = false;

            try
            {
                _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite); // cancel pending tick
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose; nothing left to cancel.
            }
        }

        _logger.LogInformation("Display-change monitoring stopped");
    }

    /// <summary>Runs on the SystemEvents broadcast thread; only re-arms the debounce timer.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between unsubscribe and a queued broadcast; ignore.
        }
    }

    /// <summary>Timer callback (ThreadPool thread): the debounced topology-change notification.</summary>
    private void OnDebounceElapsed(object? state)
    {
        lock (_lock)
        {
            if (_disposed || !_monitoring)
            {
                return;
            }
        }

        _logger.LogInformation("Display configuration changed (debounced)");
        try
        {
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A MonitorsChanged subscriber threw");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_monitoring)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _monitoring = false;
            }
        }

        _debounceTimer.Dispose();
    }

    // ---------------------------------------------------------------------
    // EnumDisplayMonitors / GetMonitorInfo interop
    // ---------------------------------------------------------------------

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice; // GDI name, e.g. \\.\DISPLAY1 (volatile; correlation key only)
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // ---------------------------------------------------------------------
    // QueryDisplayConfig (CCD) interop — stable monitorDevicePath + friendly name.
    // Not DPI-virtualized by design. Pattern verified in docs/_build/research/wpf-system.md.
    // ---------------------------------------------------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)] // union; contents unused here
    private struct DISPLAYCONFIG_MODE_INFO
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName; // \\.\DISPLAY1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath; // stable across reboots — the persistence key
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>
    /// One CCD pass: maps each active GDI device name to its stable device path and EDID
    /// friendly name. Returns an empty map on any failure (callers fall back to GDI names).
    /// </summary>
    private Dictionary<string, (string? DevicePath, string? Friendly)> QueryDisplayNames()
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
            if (status != 0)
            {
                _logger.LogWarning("GetDisplayConfigBufferSizes failed with {Status}; falling back to GDI names", status);
                return map;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (status != 0)
            {
                _logger.LogWarning("QueryDisplayConfig failed with {Status}; falling back to GDI names", status);
                return map;
            }

            for (int i = 0; i < numPaths; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    },
                };

                if (DisplayConfigGetDeviceInfo(ref source) == 0 && DisplayConfigGetDeviceInfo(ref target) == 0)
                {
                    map[source.viewGdiDeviceName] = (target.monitorDevicePath, target.monitorFriendlyDeviceName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryDisplayConfig name resolution failed; falling back to GDI names");
        }

        return map;
    }
}

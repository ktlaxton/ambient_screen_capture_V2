#if SIMULATOR_ENABLED
using AmbientFx.Models;
using AmbientFx.Services;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator). A drop-in <see cref="IMonitorDetectionService"/> that
/// serves a fabricated topology from a <see cref="SimulatorScenario"/> instead of enumerating real
/// displays. It satisfies the interface exactly, so the real <see cref="Services.EngineCoordinator"/>
/// drives its topology-change re-sync path unchanged. The simulator skips all OS interop:
/// <see cref="StartMonitoring"/>/<see cref="StopMonitoring"/> just flip an armed flag (no
/// <c>SystemEvents</c> hook), and <see cref="FireMonitorsChanged"/> raises the event off the UI
/// thread to match the real service's timing contract. Compiled out of Release via
/// <c>SIMULATOR_ENABLED</c>.
/// </summary>
public sealed class SimulatedMonitorDetectionService : IMonitorDetectionService
{
    /// <summary>
    /// Sentinel <c>HMONITOR</c> stamped on every fabricated monitor. It never reaches WGC because the
    /// capture seam is simulated too (the real path that would consume it,
    /// <see cref="Capture.ScreenCaptureService"/>'s <c>CreateItemForMonitor</c>, is never called).
    /// </summary>
    public static readonly nint SentinelHMonitor = IntPtr.Zero;

    private readonly ILogger<SimulatedMonitorDetectionService> _logger;
    private readonly object _lock = new();
    private readonly List<MonitorInfo> _monitors;

    private bool _monitoring;
    private bool _disposed;
    private int _addCounter = 100;

    /// <inheritdoc />
    public event EventHandler? MonitorsChanged;

    public SimulatedMonitorDetectionService(ILogger<SimulatedMonitorDetectionService> logger, SimulatorScenario scenario)
    {
        _logger = logger;
        scenario.Validate(logger);
        _monitors = scenario.ToMonitorInfos();
        _logger.LogInformation("Simulated monitor detection initialized from scenario '{Name}' with {Count} monitor(s).",
            scenario.Name, _monitors.Count);
    }

    /// <inheritdoc />
    /// <remarks>Returns a fresh snapshot each call, mirroring the real service's contract.</remarks>
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        lock (_lock)
        {
            return _monitors.Select(Clone).ToList();
        }
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
            _monitoring = true;
        }
        _logger.LogInformation("Simulated display-change monitoring started.");
    }

    /// <inheritdoc />
    public void StopMonitoring()
    {
        lock (_lock)
        {
            _monitoring = false;
        }
        _logger.LogInformation("Simulated display-change monitoring stopped.");
    }

    // ── dev-facing topology mutation (no OS interop) ───────────────────────────────────────────────

    /// <summary>Adds (or replaces by id) a monitor in the live topology. Does not fire MonitorsChanged.</summary>
    public void AddMonitor(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        lock (_lock)
        {
            _monitors.RemoveAll(m => string.Equals(m.Id, monitor.Id, StringComparison.OrdinalIgnoreCase));
            var copy = Clone(monitor);
            copy.HMonitor = SentinelHMonitor;
            _monitors.Add(copy);
        }
    }

    /// <summary>
    /// Story 10.6: adds a new 1920×1080 monitor at the right edge of the current topology with a unique
    /// synthetic id, and returns that id. Shared by the editor's "Add" button and the composite window's
    /// drag-to-arrange toolbar so both use the same id scheme. Does not fire MonitorsChanged.
    /// </summary>
    public string AddMonitorAtDefault() => AddMonitorAtDefault(1920, 1080);

    /// <summary>
    /// Sized variant (UX redesign): the toolbar's "+ Add monitor" size menu adds at the right edge
    /// with the requested dimensions (the monitor's virtual-desktop footprint — not a display
    /// "resolution"). Non-positive dimensions coerce to 1920×1080. Does not fire MonitorsChanged.
    /// </summary>
    public string AddMonitorAtDefault(int width, int height)
    {
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;
        lock (_lock)
        {
            int rightEdge = _monitors.Count == 0 ? 0 : _monitors.Max(m => m.X + m.Width);
            string id = $@"\\.\SIM-DISPLAY{_addCounter++}";
            _monitors.Add(new MonitorInfo
            {
                Id = id,
                Name = $"Added {id}",
                X = rightEdge,
                Y = 0,
                Width = width,
                Height = height,
                IsPrimary = _monitors.Count == 0,
                HMonitor = SentinelHMonitor,
            });
            return id;
        }
    }

    /// <summary>Replaces the entire live topology (Story 10.5 — loading a curated/saved scenario). Does
    /// not fire MonitorsChanged; the editor fires it explicitly after applying.</summary>
    public void ReplaceTopology(IEnumerable<MonitorInfo> monitors)
    {
        lock (_lock)
        {
            _monitors.Clear();
            foreach (var m in monitors)
            {
                var copy = Clone(m);
                copy.HMonitor = SentinelHMonitor;
                _monitors.Add(copy);
            }
        }
    }

    /// <summary>Removes a monitor by id. Does not fire MonitorsChanged.</summary>
    public bool RemoveMonitor(string monitorId)
    {
        lock (_lock)
        {
            return _monitors.RemoveAll(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    /// <summary>Changes a monitor's resolution. Does not fire MonitorsChanged.</summary>
    public bool SetResolution(string monitorId, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }
        lock (_lock)
        {
            var m = _monitors.FirstOrDefault(x => string.Equals(x.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
            {
                return false;
            }
            m.Width = width;
            m.Height = height;
            return true;
        }
    }

    /// <summary>
    /// Live (width, height) of a monitor by id, or null when it is not in the current topology. Lets
    /// the simulated capture follow a <see cref="SetResolution"/> mutation mid-stream (the detection
    /// service owns the live topology; the capture service pulls from it), mirroring the real service's
    /// ContentSize self-heal.
    /// </summary>
    public (int Width, int Height)? TryGetResolution(string monitorId)
    {
        lock (_lock)
        {
            var m = _monitors.FirstOrDefault(x => string.Equals(x.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            return m is null ? null : (m.Width, m.Height);
        }
    }

    /// <summary>Moves a monitor's top-left to (x, y) in virtual-desktop coords (Story 10.5). Negative valid.</summary>
    public bool SetPosition(string monitorId, int x, int y)
    {
        lock (_lock)
        {
            var m = _monitors.FirstOrDefault(z => string.Equals(z.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
            {
                return false;
            }
            m.X = x;
            m.Y = y;
            return true;
        }
    }

    /// <summary>Makes one monitor primary and clears the flag on all others (Story 10.5).</summary>
    public bool SetPrimary(string monitorId)
    {
        lock (_lock)
        {
            if (!_monitors.Any(z => string.Equals(z.Id, monitorId, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            foreach (var m in _monitors)
            {
                m.IsPrimary = string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
    }

    /// <summary>
    /// Forces a monitor's orientation by swapping width/height when needed. Portrait is modeled as
    /// <c>height &gt; width</c> (no rotation field — see <see cref="SimulatorScenario"/>). Does not
    /// fire MonitorsChanged.
    /// </summary>
    public bool SetOrientation(string monitorId, bool portrait)
    {
        lock (_lock)
        {
            var m = _monitors.FirstOrDefault(x => string.Equals(x.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
            {
                return false;
            }
            bool isPortrait = m.Height > m.Width;
            if (isPortrait != portrait)
            {
                (m.Width, m.Height) = (m.Height, m.Width);
            }
            return true;
        }
    }

    /// <summary>
    /// Raises <see cref="MonitorsChanged"/> on a background (ThreadPool) thread, exactly as the real
    /// service does from its debounce timer, so the coordinator's <c>OnMonitorsChanged</c> marshals to
    /// the dispatcher. Never throws.
    /// </summary>
    public void FireMonitorsChanged()
    {
        EventHandler? handler;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            handler = MonitorsChanged;
        }

        if (handler is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A simulated MonitorsChanged subscriber threw.");
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _monitoring = false;
        }
    }

    private static MonitorInfo Clone(MonitorInfo m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        X = m.X,
        Y = m.Y,
        Width = m.Width,
        Height = m.Height,
        IsPrimary = m.IsPrimary,
        HMonitor = m.HMonitor,
    };
}
#endif

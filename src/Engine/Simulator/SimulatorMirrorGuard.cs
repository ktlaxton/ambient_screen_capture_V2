#if SIMULATOR_ENABLED
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The mirror feedback-loop guard: mirroring the physical display
/// that hosts the composite window captures the window itself (hall of mirrors). The guard watches
/// where the window actually sits (debounced on move/resize) and PAUSES exactly those mirrors —
/// they install as synthetic while the DESIRED content stays recorded in
/// <see cref="SimulatorSceneController.Current"/>, so moving the window to another display restores
/// them automatically, and preset saves never serialize the paused stand-in. Compiled out of Release.
/// </summary>
public sealed class SimulatorMirrorGuard : IDisposable
{
    /// <summary>The pure decision core (xUnit-tested without a window).</summary>
    public static class Decision
    {
        /// <summary>Given the physical id hosting the simulator window and the desired mirrors
        /// (virtual monitor id → physical monitor id), returns the virtual ids that must pause,
        /// sorted for determinism. A null/unknown host pauses nothing.</summary>
        public static IReadOnlyList<string> PausedIds(
            string? hostPhysicalId, IReadOnlyDictionary<string, string>? desiredMirrors)
        {
            if (string.IsNullOrEmpty(hostPhysicalId) || desiredMirrors is null || desiredMirrors.Count == 0)
            {
                return Array.Empty<string>();
            }
            return desiredMirrors
                .Where(kv => string.Equals(kv.Value, hostPhysicalId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private readonly SimulatorWindow _window;
    private readonly SimulatorSceneController _scene;
    private readonly Func<IReadOnlyList<MonitorInfo>> _realMonitors;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _debounce;
    private HashSet<string> _paused = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SimulatorMirrorGuard(
        SimulatorWindow window,
        SimulatorSceneController scene,
        Func<IReadOnlyList<MonitorInfo>> realMonitors,
        ILogger logger)
    {
        _window = window;
        _scene = scene;
        _realMonitors = realMonitors;
        _logger = logger;

        // Moves/resizes fire in bursts — debounce so a drag across displays re-evaluates once.
        _debounce = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reevaluate();
        };

        _window.LocationChanged += OnWindowMoved;
        _window.SizeChanged += OnWindowMoved;
        // First evaluation once the HWND exists (Show() during composition); until then the host
        // display is unknown and nothing is paused.
        _window.SourceInitialized += OnSourceInitialized;
        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            Reevaluate(); // window was already shown before the guard attached
        }
    }

    /// <summary>Virtual monitor ids whose mirrors are currently paused (installed as synthetic).</summary>
    public IReadOnlyCollection<string> PausedMonitorIds => _paused;

    /// <summary>Raised (UI thread) when the paused set changes, with the new sorted set — drives the
    /// window's status strip and backdrop labels.</summary>
    public event Action<IReadOnlyList<string>>? PausedChanged;

    /// <summary>
    /// Recomputes the paused set from the window's current display and the scene's desired mirrors.
    /// Raises <see cref="PausedChanged"/> and returns the ids whose paused state CHANGED — the caller
    /// decides whether to (re)install them (<see cref="Reevaluate"/> does; the scene controller
    /// installs its own target itself). No content is swapped here.
    /// </summary>
    public IReadOnlyList<string> UpdatePausedSet()
    {
        if (_disposed)
        {
            return Array.Empty<string>();
        }

        var fresh = new HashSet<string>(
            Decision.PausedIds(HostPhysicalId(), DesiredMirrors()),
            StringComparer.OrdinalIgnoreCase);

        string[] changed = _paused.Where(id => !fresh.Contains(id))
            .Concat(fresh.Where(id => !_paused.Contains(id)))
            .ToArray();
        _paused = fresh;

        if (changed.Length > 0)
        {
            _logger.LogInformation("Simulator mirror guard: paused = [{Paused}].",
                string.Join(", ", fresh.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
            PausedChanged?.Invoke(fresh.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        return changed;
    }

    /// <summary>Recomputes the paused set AND swaps content for every monitor whose state changed
    /// (pause → synthetic stand-in, resume → rebuild the real mirror). UI thread.</summary>
    public void Reevaluate()
    {
        foreach (string id in UpdatePausedSet())
        {
            _scene.InstallDesiredContent(id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _debounce.Stop();
        _window.LocationChanged -= OnWindowMoved;
        _window.SizeChanged -= OnWindowMoved;
        _window.SourceInitialized -= OnSourceInitialized;
    }

    private void OnWindowMoved(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Reevaluate();

    /// <summary>The desired mirrors from the scene: virtual id → physical id.</summary>
    private Dictionary<string, string> DesiredMirrors()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _scene.Current.Monitors)
        {
            if (m.Content is { } content
                && string.Equals(content.Kind, SimContent.Mirror, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(content.PhysicalMonitorId))
            {
                result[m.Id] = content.PhysicalMonitorId!;
            }
        }
        return result;
    }

    /// <summary>The stable physical id of the display hosting the window, or null when unknown
    /// (no HWND yet, off-screen, enumeration failure). HMONITOR comparison stays within ONE fresh
    /// enumeration — handles are only stable while the topology is unchanged.</summary>
    private string? HostPhysicalId()
    {
        try
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero)
            {
                return null;
            }
            var hMonitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (hMonitor == IntPtr.Zero)
            {
                return null;
            }
            return _realMonitors().FirstOrDefault(m => m.HMonitor == hMonitor)?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Simulator mirror guard: host display resolution failed.");
            return null;
        }
    }
}
#endif

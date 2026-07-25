#if SIMULATOR_ENABLED
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmbientFx.Bridge;
using AmbientFx.Hosting;
using AmbientFx.Models;
using AmbientFx.Simulator.Capture;
using AmbientFx.Simulator.Content;
using AmbientFx.Simulator.Devices;
using Microsoft.Extensions.Logging;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator, Story 10.2). One window that composites every virtual
/// monitor, laid out as a <b>scaled</b> copy of the virtual desktop (negative coordinates, gaps, and
/// mixed sizes preserved — like the Windows "Display arrangement" diagram). Each monitor gets a
/// backdrop rect; each <i>target</i> monitor additionally gets a <see cref="SimulatorEffectSurface"/>
/// viewport running the real effect runtime. The window provides the surface factory injected into the
/// unmodified <see cref="WebViewWindowManager"/>; it never touches the bridge, coordinator, or window
/// configs. Compiled out of Release.
/// </summary>
public sealed class SimulatorWindow : Window
{
    /// <summary>Documented multi-WebView2 ceiling (AC9): each viewport is a separate render process
    /// sharing one environment; beyond this, GPU memory pressure is likely, so the tool warns.</summary>
    public const int MaxViewports = 6;

    private const double EdgePadding = 28;

    // Story 10.6: free pan/zoom over the virtual-desktop canvas.
    private const double ZoomStep = 1.1;
    private const double MinZoom = 0.2;
    private const double MaxZoom = 6.0;

    private readonly ILogger<SimulatorWindow> _logger;
    private readonly Canvas _canvas;
    private readonly TextBlock _warning;
    private readonly Dictionary<string, SimulatorEffectSurface> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _backdrops = new(StringComparer.Ordinal);

    // Pan/zoom state (Story 10.6). userZoom multiplies the auto-fit scale; pan shifts the centered origin.
    private double _userZoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _panning;
    private Point _panStart;
    private double _panOriginX;
    private double _panOriginY;

    // Drag-to-arrange state (Story 10.6). Effects are hidden while arranging (a windowed WebView2 can't
    // be grabbed through), so the monitors become draggable boxes like the Windows arrangement screen.
    private const double SnapScreenPx = 9; // edge-snap radius in screen pixels
    // Story 10.6 (UX): open in layout-edit mode so monitors are draggable the instant the window opens —
    // no mode to discover. "▶ Preview effects" switches to the live composite.
    private bool _arrangeMode = true;
    private string? _selectedId;
    private Border? _dragBox;
    private string? _dragId;
    private Point _dragStart;
    private int _dragOrigX;
    private int _dragOrigY;
    private SimulatorLayoutMath.CanvasLayout _dragLayout; // mapping captured at mouse-down (stable during a drag)

    private SimulatorToolbar? _toolbar;
    private TextBlock? _arrangeHint;
    private readonly TextBlock _status;
    private readonly HashSet<string> _pausedMirrors = new(StringComparer.OrdinalIgnoreCase);
    private SimulatorMonitorCard? _card;
    private SimulatorPeripheralCard? _periCard;
    private const double CardGap = 10; // canvas px between a monitor/chip and its floating card

    /// <summary>Hook (wired by composition): set a monitor's top-left in virtual-desktop pixels. Does not
    /// re-sync the engine — that happens once via <see cref="TopologyCommitted"/>.</summary>
    public Action<string, int, int>? MoveMonitorRequested { get; set; }

    /// <summary>Hook: re-sync the engine to the current topology (fires the simulated MonitorsChanged).</summary>
    public Action? TopologyCommitted { get; set; }

    /// <summary>Hook: add a (width × height) monitor to the topology and return its new id (no engine
    /// re-sync). The toolbar's size menu drives it via <see cref="AddMonitorSized"/>.</summary>
    public Func<int, int, string?>? AddMonitorRequested { get; set; }

    private Func<IReadOnlyList<MonitorInfo>>? _topologyProvider;

    private SimulatorPeripheralLayer? _peripherals;
    private string? _peripheralSourceId;

    /// <summary>Resolves a monitor's synthetic background pattern (from the scenario); defaults to gradient.</summary>
    public Func<string, string>? PatternResolver { get; set; }

    public SimulatorWindow(ILogger<SimulatorWindow> logger)
    {
        _logger = logger;

        Title = "AmbientFx — Layout Simulator";
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // A non-null background makes empty canvas areas hit-testable so a drag there starts a pan
        // (Story 10.6) — without it, WPF would only deliver mouse events over the child viewports.
        _canvas = new Canvas
        {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
        };
        _canvas.MouseLeftButtonDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeftButtonUp += OnCanvasMouseUp;
        _canvas.MouseWheel += OnCanvasMouseWheel;

        _warning = new TextBlock
        {
            Foreground = Brushes.Orange,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_warning);

        // Screen-fixed navigation hint (Story 10.6), top-right — lives in the root Grid, not the
        // panning canvas, so it never moves with pan/zoom. (Fit moved into the toolbar.)
        var hint = new TextBlock
        {
            Text = "drag to pan · wheel to zoom",
            Foreground = SimulatorTheme.TextMuted,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 14, 0),
        };
        Panel.SetZIndex(hint, 50);
        root.Children.Add(hint);

        // UX redesign: the mirror guard's status strip, top-left. The ONLY paused-mirror indicator
        // that works in preview mode too — overlay badges over a live windowed WebView2 are
        // impossible (airspace), but this sits in the root Grid above the padded canvas edge.
        _status = new TextBlock
        {
            Foreground = Brushes.Khaki,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(_status, 50);
        root.Children.Add(_status);

        _arrangeHint = new TextBlock
        {
            Text = "Editing layout — drag monitors to arrange · click a monitor to edit it (content / effect / source) · drag a device chip onto an anchor zone · ▶ Preview effects when ready",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x2d, 0x5b, 0x8c)),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 42, 0, 0),
            Visibility = Visibility.Visible, // edit mode is the default
        };
        Panel.SetZIndex(_arrangeHint, 50);
        root.Children.Add(_arrangeHint);

        Content = root;

        SizeChanged += (_, _) => Reflow();
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// UX redesign: opens the window on a real display OTHER than the one the scene's source monitor
    /// mirrors (usually the primary). Whichever display hosts this window gets its mirror paused by
    /// the feedback-loop guard — so hosting it on the source's display would pause the one mirror
    /// that matters most, right at launch. Positioning happens at SourceInitialized via SetWindowPos
    /// in DEVICE pixels (correct under per-monitor DPI, unlike WPF's DIP-based Left/Top).
    /// Single-display machines keep the centered default. Call before Show(), UI thread.
    /// </summary>
    public void PreferDisplayAwayFrom(string? sourcePhysicalId, Func<IReadOnlyList<MonitorInfo>> realMonitors)
    {
        VerifyAccess();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var monitors = realMonitors();
                if (monitors.Count < 2)
                {
                    return; // nowhere better to go
                }
                var target = monitors.FirstOrDefault(m =>
                        !string.Equals(m.Id, sourcePhysicalId, StringComparison.OrdinalIgnoreCase) && !m.IsPrimary)
                    ?? monitors.FirstOrDefault(m => !string.Equals(m.Id, sourcePhysicalId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    return;
                }
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    SetWindowPos(handle, IntPtr.Zero, target.X + 40, target.Y + 40, 0, 0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate);
                    _logger.LogInformation("Simulator window placed on '{Name}' (away from the source's display).", target.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Simulator: preferred window placement failed; keeping the default position.");
            }
        };
    }

    /// <summary>UI thread. Closing the composite window ends the whole dev session (Story 10.6); the
    /// idempotent <see cref="SimulatorShutdown"/> guard means this is a no-op if shutdown already began
    /// (e.g. the user closed the control window first).</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        SimulatorShutdown.Request();
    }

    /// <summary>
    /// Wires a live-topology source (the simulated detection service) so the composite draws a backdrop
    /// for <b>every</b> monitor — including the source, which has no effect surface — and immediately
    /// reflows. Call on the UI thread.
    /// </summary>
    public void SetTopologyProvider(Func<IReadOnlyList<MonitorInfo>> provider)
    {
        _topologyProvider = provider;
        RefreshTopology();
    }

    /// <summary>
    /// The surface factory injected into <see cref="WebViewWindowManager"/>. Creates a viewport for the
    /// monitor, adds it to the canvas, and reflows. Must run on the UI thread (the manager's
    /// <c>SyncEffectWindowsAsync</c> already runs on the dispatcher).
    /// </summary>
    public IEffectSurfaceHost CreateSurface(MonitorInfo monitor)
    {
        VerifyAccess();

        if (_surfaces.TryGetValue(monitor.Id, out var existing))
        {
            existing.RepositionTo(monitor);
            return existing;
        }

        var surface = new SimulatorEffectSurface(monitor, _logger)
        {
            SourcePattern = PatternResolver?.Invoke(monitor.Id) ?? SyntheticPatterns.Gradient,
            LayoutRequested = Reflow,
        };
        surface.Removed = s =>
        {
            _canvas.Children.Remove(s);
            _surfaces.Remove(s.MonitorId);
            UpdateCeilingWarning();
            Reflow();
        };
        _surfaces[monitor.Id] = surface;
        Panel.SetZIndex(surface, 10); // effect viewports sit above the backdrops
        _canvas.Children.Add(surface);
        // Authoritative over the manager's Show() so a new surface stays hidden in edit mode (airspace —
        // a visible windowed WebView2 would cover and block the draggable box underneath).
        surface.ArrangeHidden = _arrangeMode;

        UpdateCeilingWarning();
        Reflow();
        return surface;
    }

    /// <summary>
    /// Story 10.4: shows virtual RGB peripherals (live LED dots) positioned around the source monitor's
    /// viewport per their placement anchor. Colors come from the <see cref="VisualizationBackend"/>'s
    /// recorded real-projection output; <paramref name="applyPlacements"/> pushes anchor changes back to
    /// the real device service. Call on the UI thread.
    /// </summary>
    public void ConfigurePeripherals(
        VisualizationBackend backend,
        Dictionary<string, DevicePlacement> placements,
        Action<IReadOnlyDictionary<string, DevicePlacement>> applyPlacements,
        string sourceMonitorId)
    {
        VerifyAccess();
        _peripheralSourceId = sourceMonitorId;
        _peripherals = new SimulatorPeripheralLayer(_canvas, backend, placements, applyPlacements, _logger);
        _peripherals.SetEditMode(_arrangeMode);

        // UX redesign: clicking a chip (a sub-slop press) opens its mini-card (anchor/flip/
        // brightness/enabled) — edit mode only, like the monitor card.
        var periCard = new SimulatorPeripheralCard(_peripherals);
        _periCard = periCard;
        Panel.SetZIndex(periCard, 60);
        _canvas.Children.Add(periCard);
        _peripherals.ChipSelected += deviceId =>
        {
            if (!_arrangeMode)
            {
                return;
            }
            _card?.Hide(); // one card at a time keeps the canvas readable
            periCard.ShowFor(deviceId);
            Reflow();
        };

        Reflow();
    }

    /// <summary>UX redesign: docks the screen-fixed top toolbar (Presets / Add monitor / mode toggle /
    /// display change / FPS / Fit). Call once, on the UI thread, before Show().</summary>
    public void ConfigureChrome(SimulatorToolbar toolbar)
    {
        VerifyAccess();
        _toolbar = toolbar;
        toolbar.SetModeLabel(_arrangeMode ? "▶ Preview effects" : "✏ Edit layout");
        Panel.SetZIndex(toolbar, 50);
        if (Content is Grid grid)
        {
            grid.Children.Add(toolbar);
        }
    }

    /// <summary>UX redesign: the mirror guard's paused set changed. Updates the status strip and the
    /// backdrop labels (edit-mode view). UI thread.</summary>
    public void SetMirrorStatus(IReadOnlyList<string> pausedMonitorIds)
    {
        VerifyAccess();
        _pausedMirrors.Clear();
        foreach (string id in pausedMonitorIds)
        {
            _pausedMirrors.Add(id);
        }

        if (_pausedMirrors.Count == 0)
        {
            _status.Visibility = Visibility.Collapsed;
        }
        else
        {
            var monitors = _topologyProvider?.Invoke() ?? Array.Empty<MonitorInfo>();
            var names = pausedMonitorIds
                .Select(id => monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id)
                .ToList();
            _status.Text = $"⏸ Mirror paused on {string.Join(", ", names)} — the simulator window is on that display " +
                           "(mirroring it would feed back). Move this window to another display to restore.";
            _status.Visibility = Visibility.Visible;
        }
        RefreshTopology(); // re-label the backdrops with/without the paused suffix
    }

    /// <summary>
    /// UX redesign: wires the canvas-first scene UI — the floating per-monitor card (select a
    /// monitor in edit mode) and the scene-level events (scene swap → re-fit; source change →
    /// re-home the peripherals). Replaces the retired docked editor panel. Call once, UI thread.
    /// </summary>
    public void ConfigureScene(
        SimulatorSceneController scene,
        SimulatedMonitorDetectionService detection,
        IReadOnlyList<SimulatorEffectsCatalog.EffectEntry> effects,
        Func<IReadOnlyList<MonitorInfo>> realMonitors,
        Func<ApplicationSettings?> liveSettings)
    {
        VerifyAccess();
        var card = new SimulatorMonitorCard(scene, detection, effects, realMonitors, liveSettings, _logger);
        _card = card;
        Panel.SetZIndex(card, 60); // above backdrops/surfaces/peripherals; placed by Reflow
        _canvas.Children.Add(card);

        card.GeometryEdited += RefreshTopology; // includes Reflow
        card.MonitorRemoved += _ =>
        {
            _selectedId = null;
            RefreshTopology();
            if (!_arrangeMode)
            {
                TopologyCommitted?.Invoke(); // live mode: tear the removed monitor's surface down now
            }
        };

        scene.SceneReplaced += () =>
        {
            _selectedId = null;
            card.Hide();
            _periCard?.Hide();
            RefreshTopology();
            FitToWindow();
        };
        scene.SourceChanged += sourceId =>
        {
            _peripheralSourceId = sourceId; // the peripheral cluster follows the engine source
            Reflow();
        };
    }

    /// <summary>Redraws the per-monitor backdrops from the current topology (UI thread).</summary>
    public void RefreshTopology()
    {
        VerifyAccess();

        var monitors = _topologyProvider?.Invoke() ?? Array.Empty<MonitorInfo>();
        var ids = monitors.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var staleId in _backdrops.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _canvas.Children.Remove(_backdrops[staleId]);
            _backdrops.Remove(staleId);
        }

        // The card's monitor may have been resized/moved/removed under it (topology swaps arrive on
        // the ThreadPool MonitorsChanged) — re-populate or dismiss.
        if (_card?.MonitorId is { } cardMonitorId)
        {
            if (ids.Contains(cardMonitorId))
            {
                _card.ShowFor(cardMonitorId);
            }
            else
            {
                _card.Hide();
            }
        }

        foreach (var m in monitors)
        {
            if (!_backdrops.TryGetValue(m.Id, out var border))
            {
                border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x22)),
                    Child = new TextBlock
                    {
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(4),
                        VerticalAlignment = VerticalAlignment.Top,
                    },
                };
                Panel.SetZIndex(border, 0);
                // Story 10.6: backdrops are the drag targets in arrange mode (the handlers no-op otherwise).
                border.MouseLeftButtonDown += OnBoxMouseDown;
                border.MouseMove += OnBoxMouseMove;
                border.MouseLeftButtonUp += OnBoxMouseUp;
                _backdrops[m.Id] = border;
                _canvas.Children.Add(border);
            }

            if (border.Child is TextBlock label)
            {
                label.Text = $"{m.Name}\n{m.Width}×{m.Height}{(m.IsPrimary ? "  • primary" : string.Empty)}"
                    + (_pausedMirrors.Contains(m.Id) ? "\n⏸ mirror paused" : string.Empty);
            }
            border.Tag = m; // rect for the layout pass
        }

        ApplyBackdropStyling(); // Story 10.6: arrange-vs-live look for any new/updated backdrops
        Reflow();
    }

    /// <summary>Resets zoom and pan and re-fits the whole virtual desktop to the window (Story 10.6).</summary>
    public void FitToWindow()
    {
        VerifyAccess();
        _userZoom = 1.0;
        _panX = 0;
        _panY = 0;
        Reflow();
    }

    /// <summary>Every monitor currently drawn (backdrops + any surface without a backdrop).</summary>
    private List<MonitorInfo> CollectMonitors()
    {
        var monitors = new List<MonitorInfo>();
        foreach (var b in _backdrops.Values)
        {
            if (b.Tag is MonitorInfo m) monitors.Add(m);
        }
        foreach (var s in _surfaces.Values)
        {
            if (!_backdrops.ContainsKey(s.MonitorId)) monitors.Add(s.Monitor);
        }
        return monitors;
    }

    /// <summary>
    /// Lays out every backdrop / effect viewport / peripheral with one uniform mapping (auto-fit scale ×
    /// user zoom, plus pan), computed by the pure <see cref="SimulatorLayoutMath"/>. Folding pan/zoom into
    /// the placement (rather than a WPF <c>RenderTransform</c>) keeps the windowed WebView2 surfaces
    /// correctly sized/placed. With zoom 1 / pan 0 this is the pre-10.6 auto-fit.
    /// </summary>
    private void Reflow()
    {
        var monitors = CollectMonitors();
        if (!TryCurrentLayout(out var layout))
        {
            return;
        }

        void Place(FrameworkElement element, MonitorInfo m)
        {
            var p = layout.Place(m.X, m.Y, m.Width, m.Height);
            Canvas.SetLeft(element, p.Left);
            Canvas.SetTop(element, p.Top);
            element.Width = p.Width;
            element.Height = p.Height;
        }

        foreach (var border in _backdrops.Values)
        {
            if (border.Tag is MonitorInfo m) Place(border, m);
        }
        foreach (var surface in _surfaces.Values)
        {
            Place(surface, surface.Monitor);
        }

        // Position the virtual peripherals around the source monitor's viewport (Story 10.4).
        if (_peripherals is not null && _peripheralSourceId is not null)
        {
            var src = monitors.FirstOrDefault(m => string.Equals(m.Id, _peripheralSourceId, StringComparison.OrdinalIgnoreCase));
            if (src is not null)
            {
                var p = layout.Place(src.X, src.Y, src.Width, src.Height);
                _peripherals.Layout(new Rect(p.Left, p.Top, p.Width, p.Height));
            }
        }

        // Dock the floating monitor card beside its monitor (UX redesign) — same coordinate space as
        // everything else, so it follows pan/zoom naturally.
        if (_card is { Visibility: Visibility.Visible, MonitorId: { } cardId })
        {
            var m = monitors.FirstOrDefault(x => string.Equals(x.Id, cardId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
            {
                _card.Hide();
            }
            else
            {
                _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var monitorPlaced = layout.Place(m.X, m.Y, m.Width, m.Height);
                var cardPlaced = SimulatorCardMath.PlaceCard(
                    monitorPlaced, _card.Width, _card.DesiredSize.Height,
                    _canvas.ActualWidth, _canvas.ActualHeight, CardGap);
                Canvas.SetLeft(_card, cardPlaced.Left);
                Canvas.SetTop(_card, cardPlaced.Top);
            }
        }

        // Same for the peripheral mini-card, docked beside its chip.
        if (_periCard is { Visibility: Visibility.Visible, DeviceId: { } deviceId })
        {
            var chipRect = _peripherals?.ChipRect(deviceId);
            if (chipRect is not { } chip)
            {
                _periCard.Hide();
            }
            else
            {
                _periCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var placedCard = SimulatorCardMath.PlaceCard(
                    new SimulatorLayoutMath.Placed(chip.X, chip.Y, chip.Width, chip.Height),
                    _periCard.Width, _periCard.DesiredSize.Height,
                    _canvas.ActualWidth, _canvas.ActualHeight, CardGap);
                Canvas.SetLeft(_periCard, placedCard.Left);
                Canvas.SetTop(_periCard, placedCard.Top);
            }
        }
    }

    // ── pan / zoom (Story 10.6) ──────────────────────────────────────────────────────────────────────

    /// <summary>Pan starts only on an empty canvas hit, so it never steals clicks on a viewport, a
    /// backdrop, or a peripheral LED dot (those remain interactive).</summary>
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _canvas))
        {
            return;
        }
        SelectMonitor(null); // clicking empty canvas deselects (and dismisses the card)
        _panning = true;
        _panStart = e.GetPosition(_canvas);
        _panOriginX = _panX;
        _panOriginY = _panY;
        _canvas.CaptureMouse();
        _canvas.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }
        var p = e.GetPosition(_canvas);
        _panX = _panOriginX + (p.X - _panStart.X);
        _panY = _panOriginY + (p.Y - _panStart.Y);
        Reflow();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning)
        {
            return;
        }
        _panning = false;
        _canvas.ReleaseMouseCapture();
        _canvas.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    /// <summary>Wheel zoom about the cursor: the virtual-desktop point under the pointer stays put.</summary>
    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var monitors = CollectMonitors();
        if (monitors.Count == 0)
        {
            return;
        }
        var rects = monitors
            .Select(m => new SimulatorLayoutMath.Rect(m.X, m.Y, m.Width, m.Height))
            .ToList();
        double availW = _canvas.ActualWidth - 2 * EdgePadding;
        double availH = _canvas.ActualHeight - 2 * EdgePadding;

        double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        double newZoom = Math.Clamp(_userZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _userZoom) < 1e-9)
        {
            return;
        }

        var cursor = e.GetPosition(_canvas);
        (_panX, _panY) = SimulatorLayoutMath.PanForZoom(
            rects, availW, availH, EdgePadding, _userZoom, _panX, _panY, newZoom, cursor.X, cursor.Y);
        _userZoom = newZoom;
        Reflow();
        e.Handled = true;
    }

    /// <summary>The current canvas mapping (auto-fit × zoom + pan) for the drawn monitors.</summary>
    private bool TryCurrentLayout(out SimulatorLayoutMath.CanvasLayout layout)
    {
        layout = default;
        var monitors = CollectMonitors();
        if (monitors.Count == 0)
        {
            return false;
        }
        var rects = monitors
            .Select(m => new SimulatorLayoutMath.Rect(m.X, m.Y, m.Width, m.Height))
            .ToList();
        double availW = _canvas.ActualWidth - 2 * EdgePadding;
        double availH = _canvas.ActualHeight - 2 * EdgePadding;
        return SimulatorLayoutMath.TryCompute(rects, availW, availH, EdgePadding, _userZoom, _panX, _panY, out layout);
    }

    // ── drag-to-arrange (Story 10.6) ─────────────────────────────────────────────────────────────────

    /// <summary>Flips arrange mode. ON hides the live effect surfaces (so the WPF monitor boxes can be
    /// grabbed — a windowed WebView2 can't be dragged through) and makes the boxes draggable; OFF restores
    /// the effects and re-syncs the engine to the arranged layout.</summary>
    public void ToggleArrangeMode() => SetArrangeMode(!_arrangeMode);

    private void SetArrangeMode(bool on)
    {
        VerifyAccess();
        if (_arrangeMode == on)
        {
            return;
        }
        _arrangeMode = on;

        foreach (var surface in _surfaces.Values)
        {
            surface.ArrangeHidden = on; // authoritative over Show() (see SimulatorEffectSurface)
        }
        // The toggle always offers the *other* mode, so its label is the action it performs.
        _toolbar?.SetModeLabel(on ? "▶ Preview effects" : "✏ Edit layout");
        if (_arrangeHint is not null)
        {
            _arrangeHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        _peripherals?.SetEditMode(on);
        if (!on)
        {
            _card?.Hide();     // preview mode: the live surfaces come back (airspace) — no cards
            _periCard?.Hide();
        }

        ApplyBackdropStyling();
        if (!on)
        {
            // Re-sync the engine to the final arranged layout, then the restored surfaces reposition.
            TopologyCommitted?.Invoke();
        }
        Reflow();
    }

    /// <summary>UX redesign: the toolbar's size menu adds a (width × height) monitor at the right edge,
    /// selects it, and — in live mode — re-syncs so the effect surface appears immediately.</summary>
    public void AddMonitorSized(int width, int height)
    {
        VerifyAccess();
        var id = AddMonitorRequested?.Invoke(width, height);
        RefreshTopology();            // the new backdrop appears immediately (synchronous)
        if (id is not null)
        {
            SelectMonitor(id);
        }
        if (!_arrangeMode)
        {
            TopologyCommitted?.Invoke(); // live mode: let the engine create the effect surface now
        }
    }

    private void SelectMonitor(string? id, bool showCard = true)
    {
        _selectedId = id;
        ApplyBackdropStyling();
        if (id is null)
        {
            _card?.Hide();
            _periCard?.Hide();
        }
        else if (showCard && _arrangeMode)
        {
            _periCard?.Hide(); // one card at a time
            _card?.ShowFor(id);
            Reflow(); // place the card next to its monitor immediately
        }
    }

    /// <summary>Arrange-mode look (draggable, centered label, selection highlight) vs the quiet live look.</summary>
    private void ApplyBackdropStyling()
    {
        foreach (var (id, border) in _backdrops)
        {
            bool selected = _arrangeMode && string.Equals(id, _selectedId, StringComparison.Ordinal);
            if (_arrangeMode)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2c, 0x3a));
                border.BorderBrush = selected ? SimulatorTheme.Accent : SimulatorTheme.ControlBorder;
                border.BorderThickness = new Thickness(selected ? 3 : 2);
                border.Cursor = Cursors.SizeAll;
                if (border.Child is TextBlock tb)
                {
                    tb.Margin = new Thickness(4);
                    tb.HorizontalAlignment = HorizontalAlignment.Center;
                    tb.VerticalAlignment = VerticalAlignment.Center;
                    tb.TextAlignment = TextAlignment.Center;
                    tb.FontSize = 13;
                    tb.Foreground = Brushes.White;
                }
            }
            else
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x22));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66));
                border.BorderThickness = new Thickness(1);
                border.Cursor = Cursors.Arrow;
                if (border.Child is TextBlock tb)
                {
                    tb.Margin = new Thickness(4);
                    tb.HorizontalAlignment = HorizontalAlignment.Left;
                    tb.VerticalAlignment = VerticalAlignment.Top;
                    tb.TextAlignment = TextAlignment.Left;
                    tb.FontSize = 12;
                    tb.Foreground = Brushes.Gray;
                }
            }
        }
    }

    private void OnBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_arrangeMode || sender is not Border box || box.Tag is not MonitorInfo m)
        {
            return;
        }
        if (!TryCurrentLayout(out _dragLayout))
        {
            return;
        }
        _dragBox = box;
        _dragId = m.Id;
        _dragOrigX = m.X;
        _dragOrigY = m.Y;
        _dragStart = e.GetPosition(_canvas);
        SelectMonitor(m.Id, showCard: false); // the card would chase the drag — it re-shows on drop
        _card?.Hide();
        box.CaptureMouse();
        e.Handled = true; // don't let the canvas start a pan
    }

    private void OnBoxMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragBox is null || _dragId is null || _dragBox.Tag is not MonitorInfo m || _dragLayout.Scale <= 0)
        {
            return;
        }
        var p = e.GetPosition(_canvas);
        int nx = _dragOrigX + (int)Math.Round((p.X - _dragStart.X) / _dragLayout.Scale);
        int ny = _dragOrigY + (int)Math.Round((p.Y - _dragStart.Y) / _dragLayout.Scale);
        (nx, ny) = SnapPosition(_dragId, nx, ny, m.Width, m.Height, _dragLayout.Scale);

        m.X = nx;
        m.Y = ny;
        MoveMonitorRequested?.Invoke(_dragId, nx, ny); // keep the topology in sync (no engine re-sync yet)

        // Move only the dragged box, using the mapping captured at mouse-down so the view doesn't
        // re-fit/jump mid-drag (matches the Windows arrangement feel). A full Reflow happens on drop.
        var placed = _dragLayout.Place(nx, ny, m.Width, m.Height);
        Canvas.SetLeft(_dragBox, placed.Left);
        Canvas.SetTop(_dragBox, placed.Top);
        e.Handled = true;
    }

    private void OnBoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragBox is null)
        {
            return;
        }
        _dragBox.ReleaseMouseCapture();
        _dragBox = null;
        _dragId = null;
        if (_selectedId is not null)
        {
            _card?.ShowFor(_selectedId); // the card returns, with the dropped X/Y in its fields
        }
        Reflow(); // re-fit the whole arrangement now that the drag is done
        e.Handled = true;
    }

    /// <summary>Snaps the proposed top-left to neighboring monitors' edges (pure math in
    /// <see cref="SimulatorLayoutMath.Snap"/>), within <see cref="SnapScreenPx"/> screen pixels.</summary>
    private (int X, int Y) SnapPosition(string id, int x, int y, int w, int h, double scale)
    {
        var others = new List<SimulatorLayoutMath.Rect>();
        foreach (var border in _backdrops.Values)
        {
            if (border.Tag is MonitorInfo o && !string.Equals(o.Id, id, StringComparison.Ordinal))
            {
                others.Add(new SimulatorLayoutMath.Rect(o.X, o.Y, o.Width, o.Height));
            }
        }
        return SimulatorLayoutMath.Snap(x, y, w, h, others, SnapScreenPx / scale);
    }

    private void UpdateCeilingWarning()
    {
        if (_surfaces.Count > MaxViewports)
        {
            _warning.Text =
                $"⚠ {_surfaces.Count} effect viewports exceed the supported maximum of {MaxViewports}. " +
                "Each viewport is a separate WebView2 render process — GPU memory may be exhausted.";
            _warning.Visibility = Visibility.Visible;
            _logger.LogWarning("Simulator viewport count {Count} exceeds the supported ceiling {Max}", _surfaces.Count, MaxViewports);
        }
        else
        {
            _warning.Visibility = Visibility.Collapsed;
        }
    }
}
#endif

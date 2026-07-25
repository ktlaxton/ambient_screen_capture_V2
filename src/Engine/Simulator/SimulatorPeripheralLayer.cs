#if SIMULATOR_ENABLED
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AmbientFx.Devices;
using AmbientFx.Models;
using AmbientFx.Simulator.Devices;
using Microsoft.Extensions.Logging;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.4 + UX redesign). Draws the simulated RGB peripherals as chips of
/// live LED dots on the composite canvas, positioned per each device's <see cref="DevicePlacement"/>
/// anchor relative to the source monitor's viewport. Colors come straight from the
/// <see cref="VisualizationBackend"/>'s recorded <c>Apply()</c> output (the real
/// <see cref="LedProjection"/>); the layer never recomputes them — geometry only (fidelity invariant).
///
/// UX redesign: chips are <b>draggable</b> (edit mode). Dragging shows the seven REAL anchor drop
/// zones around the source monitor (<see cref="SimulatorAnchorZones"/>), highlights the zone the drop
/// would commit, and the drop hot-applies the placement through the existing
/// <c>SetPlacements</c> path. A sub-slop mouse-up counts as a click and raises
/// <see cref="ChipSelected"/> (the mini-card). Distinct placements replace the old collapse-to-below:
/// <c>behind</c> centers the chip ON the monitor (dimmed), <c>surround</c> parks the chip at the
/// bottom-right corner and draws a live perimeter ring of dots exactly where the projection samples.
/// Compiled out of Release.
/// </summary>
public sealed class SimulatorPeripheralLayer
{
    private const double Dot = 7;
    private const double RingDot = 8;
    private const double Gap = 14;
    private const double DragSlopPx = 4;    // below this, a press-release is a click (select)
    private const double ZoneThickness = 30;
    private const double ZoneGap = 6;
    private const double ZoneSnapPx = 64;

    private readonly Canvas _canvas;
    private readonly VisualizationBackend _backend;
    private readonly ILogger _logger;
    private readonly Dictionary<string, DevicePlacement> _placements;
    private readonly Action<IReadOnlyDictionary<string, DevicePlacement>> _applyPlacements;
    private readonly List<DeviceVisual> _visuals = new();

    private Rect _sourceRect = Rect.Empty;
    private bool _editMode = true; // matches the window's edit-first default

    // Drag state.
    private DeviceVisual? _dragVisual;
    private Point _dragStart;
    private Point _chipOrigin;
    private bool _dragStarted;
    private readonly List<(SimulatorAnchorZones.Zone Zone, Shape Shape)> _zoneShapes = new();
    private readonly List<UIElement> _zoneExtras = new(); // zone labels

    /// <summary>Raised when a chip is clicked (not dragged) in edit mode — opens the mini-card.</summary>
    public event Action<string>? ChipSelected;

    public SimulatorPeripheralLayer(
        Canvas canvas,
        VisualizationBackend backend,
        Dictionary<string, DevicePlacement> placements,
        Action<IReadOnlyDictionary<string, DevicePlacement>> applyPlacements,
        ILogger logger)
    {
        _canvas = canvas;
        _backend = backend;
        _placements = placements;
        _applyPlacements = applyPlacements;
        _logger = logger;

        foreach (var device in backend.Devices)
        {
            _visuals.Add(BuildVisual(device));
        }

        _backend.ColorsChanged += (_, _) =>
            _canvas.Dispatcher.BeginInvoke(new Action(UpdateColors));
    }

    /// <summary>Edit mode: chips are draggable/clickable; preview mode: passive dots. Cancels any
    /// drag in flight when switching.</summary>
    public void SetEditMode(bool on)
    {
        _editMode = on;
        if (!on)
        {
            CancelDrag();
        }
        foreach (var v in _visuals)
        {
            v.Container.Cursor = on ? Cursors.Hand : Cursors.Arrow;
            v.Container.ToolTip = on
                ? "Drag to an anchor zone around the source monitor · click to edit (flip / brightness / enabled)"
                : null;
        }
    }

    /// <summary>The chip's current canvas rect (mini-card placement), or null if unknown.</summary>
    public Rect? ChipRect(string deviceId)
    {
        var v = Find(deviceId);
        if (v is null)
        {
            return null;
        }
        double x = Canvas.GetLeft(v.Container);
        double y = Canvas.GetTop(v.Container);
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return null;
        }
        double w = v.Container.ActualWidth > 0 ? v.Container.ActualWidth : 160;
        double h = v.Container.ActualHeight > 0 ? v.Container.ActualHeight : 60;
        return new Rect(x, y, w, h);
    }

    public string DeviceName(string deviceId) => Find(deviceId)?.Device.Name ?? deviceId;

    /// <summary>A read snapshot of a device's placement (defaults if none recorded).</summary>
    public DevicePlacement PlacementSnapshot(string deviceId) =>
        _placements.TryGetValue(deviceId, out var p) ? p.Clone() : new DevicePlacement();

    /// <summary>The one write path for placement edits (chip drop + mini-card): mutates the SHARED
    /// placements map, hot-applies through the real device service, and repositions the chips.</summary>
    public void SetPlacement(string deviceId, Action<DevicePlacement> mutate)
    {
        var placement = GetOrCreate(deviceId);
        mutate(placement);
        _logger.LogInformation("Simulator: device '{Id}' placement -> anchor={Anchor} flip={Flip} brightness={Brightness:0.##} enabled={Enabled}",
            deviceId, placement.Anchor, placement.Flip, placement.Brightness, placement.Enabled);
        _applyPlacements(_placements.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()));
        if (_sourceRect != Rect.Empty)
        {
            Layout(_sourceRect);
        }
    }

    /// <summary>Positions each device chip around the source monitor's (scaled) viewport rect.</summary>
    public void Layout(Rect sourceRect)
    {
        _sourceRect = sourceRect;
        foreach (var v in _visuals)
        {
            if (ReferenceEquals(v, _dragVisual) && _dragStarted)
            {
                continue; // don't yank the chip out from under the pointer mid-drag
            }

            string anchor = Anchor(v.Device.Id);
            v.Label.Text = $"{Short(v.Device.Name)}  [{anchor}]";
            double cw = v.Container.Width > 0 ? v.Container.Width : v.Container.ActualWidth;
            double ch = v.Container.Height > 0 ? v.Container.Height : v.Container.ActualHeight;
            if (double.IsNaN(cw) || cw <= 0) cw = 160;
            if (double.IsNaN(ch) || ch <= 0) ch = 60;

            double cx = sourceRect.X + (sourceRect.Width - cw) / 2;
            double cy = sourceRect.Y + (sourceRect.Height - ch) / 2;
            (double x, double y) = anchor switch
            {
                DeviceAnchors.Left => (sourceRect.X - cw - Gap, cy),
                DeviceAnchors.Right => (sourceRect.Right + Gap, cy),
                DeviceAnchors.Above => (cx, sourceRect.Y - ch - Gap),
                DeviceAnchors.Below => (cx, sourceRect.Bottom + Gap),
                DeviceAnchors.Behind => (cx, cy),                                  // centered ON the monitor
                DeviceAnchors.Surround => (sourceRect.Right + Gap, sourceRect.Bottom + Gap), // corner: the ring shows the mapping
                _ => (cx, sourceRect.Bottom + Gap),                                // auto: just below the source
            };
            v.Container.Opacity = anchor == DeviceAnchors.Behind ? 0.55 : 1.0; // "behind the screen"
            Canvas.SetLeft(v.Container, x);
            Canvas.SetTop(v.Container, y);

            UpdateRing(v, on: anchor == DeviceAnchors.Surround);
        }
    }

    // ── surround perimeter ring ─────────────────────────────────────────────────────────────────────

    /// <summary>For a surround-anchored device, draws one dot per LED ON the source monitor's
    /// perimeter, at exactly the spot the real projection samples for that LED
    /// (<see cref="SimulatorAnchorZones.PerimeterPositions"/> mirrors the LedProjection convention).</summary>
    private void UpdateRing(DeviceVisual v, bool on)
    {
        if (!on)
        {
            if (v.RingDots is not null)
            {
                foreach (var dot in v.RingDots)
                {
                    _canvas.Children.Remove(dot);
                }
                v.RingDots = null;
            }
            return;
        }

        if (v.RingDots is null)
        {
            v.RingDots = new Ellipse[v.Device.NormalizedLeds.Length];
            for (int i = 0; i < v.RingDots.Length; i++)
            {
                var dot = new Ellipse
                {
                    Width = RingDot,
                    Height = RingDot,
                    Fill = Brushes.Black,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3a)),
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                };
                Panel.SetZIndex(dot, 20);
                v.RingDots[i] = dot;
                _canvas.Children.Add(dot);
            }
        }

        bool flip = _placements.TryGetValue(v.Device.Id, out var p) && p.Flip;
        var positions = SimulatorAnchorZones.PerimeterPositions(
            _sourceRect.X, _sourceRect.Y, _sourceRect.Width, _sourceRect.Height,
            v.Device.NormalizedLeds, flip);
        for (int i = 0; i < v.RingDots.Length && i < positions.Length; i++)
        {
            Canvas.SetLeft(v.RingDots[i], positions[i].X - RingDot / 2);
            Canvas.SetTop(v.RingDots[i], positions[i].Y - RingDot / 2);
        }
    }

    private void UpdateColors()
    {
        foreach (var v in _visuals)
        {
            int[][]? colors = _backend.ColorsFor(v.Device.Id);
            for (int i = 0; i < v.Leds.Length; i++)
            {
                Color c = Color.FromRgb(0, 0, 0);
                if (colors is not null && i < colors.Length && colors[i] is { Length: >= 3 } rgb)
                {
                    c = Color.FromRgb(Clamp(rgb[0]), Clamp(rgb[1]), Clamp(rgb[2]));
                }
                var brush = new SolidColorBrush(c);
                v.Leds[i].Fill = brush;
                if (v.RingDots is not null && i < v.RingDots.Length)
                {
                    v.RingDots[i].Fill = brush; // the ring mirrors the same recorded colors
                }
            }
        }
    }

    // ── chip drag-to-anchor ─────────────────────────────────────────────────────────────────────────

    private void OnChipMouseDown(DeviceVisual v, MouseButtonEventArgs e)
    {
        if (!_editMode)
        {
            return;
        }
        _dragVisual = v;
        _dragStarted = false;
        _dragStart = e.GetPosition(_canvas);
        _chipOrigin = new Point(Canvas.GetLeft(v.Container), Canvas.GetTop(v.Container));
        v.Container.CaptureMouse();
        e.Handled = true; // don't start a canvas pan
    }

    private void OnChipMouseMove(DeviceVisual v, MouseEventArgs e)
    {
        if (_dragVisual is null || !ReferenceEquals(v, _dragVisual))
        {
            return;
        }
        var p = e.GetPosition(_canvas);
        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;

        if (!_dragStarted)
        {
            if (Math.Abs(dx) < DragSlopPx && Math.Abs(dy) < DragSlopPx)
            {
                return; // still within click slop
            }
            if (_sourceRect == Rect.Empty)
            {
                return; // no source viewport to anchor around
            }
            _dragStarted = true;
            ShowZones();
        }

        Canvas.SetLeft(v.Container, _chipOrigin.X + dx);
        Canvas.SetTop(v.Container, _chipOrigin.Y + dy);
        HighlightZone(ResolveAnchor(p));
        e.Handled = true;
    }

    private void OnChipMouseUp(DeviceVisual v, MouseButtonEventArgs e)
    {
        if (_dragVisual is null || !ReferenceEquals(v, _dragVisual))
        {
            return;
        }
        v.Container.ReleaseMouseCapture();
        bool wasDrag = _dragStarted;
        var dropPoint = e.GetPosition(_canvas);
        _dragVisual = null;
        _dragStarted = false;
        HideZones();

        if (wasDrag)
        {
            string anchor = ResolveAnchor(dropPoint);
            SetPlacement(v.Device.Id, placement => placement.Anchor = anchor); // hot-applies + re-lays out
        }
        else
        {
            ChipSelected?.Invoke(v.Device.Id); // a click: open the mini-card
        }
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (_dragVisual is not null)
        {
            _dragVisual.Container.ReleaseMouseCapture();
            _dragVisual = null;
            _dragStarted = false;
            HideZones();
            if (_sourceRect != Rect.Empty)
            {
                Layout(_sourceRect); // snap the chip back to its anchor position
            }
        }
    }

    private string ResolveAnchor(Point p) => SimulatorAnchorZones.HitTest(CurrentZones(), p.X, p.Y, ZoneSnapPx);

    private IReadOnlyList<SimulatorAnchorZones.Zone> CurrentZones() => SimulatorAnchorZones.Zones(
        _sourceRect.X, _sourceRect.Y, _sourceRect.Width, _sourceRect.Height, ZoneThickness, ZoneGap);

    private void ShowZones()
    {
        HideZones();
        foreach (var zone in CurrentZones())
        {
            var shape = new Rectangle
            {
                Width = Math.Max(1, zone.W),
                Height = Math.Max(1, zone.H),
                Fill = new SolidColorBrush(Color.FromArgb(0x28, 0x2d, 0x5b, 0x8c)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0x37, 0x6b, 0xa6)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                RadiusX = 3,
                RadiusY = 3,
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(shape, 15); // above backdrops, below the chips
            Canvas.SetLeft(shape, zone.X);
            Canvas.SetTop(shape, zone.Y);
            _canvas.Children.Add(shape);
            _zoneShapes.Add((zone, shape));

            // Label the single-rect zones (surround's 4 bands share one tag — label only the top band,
            // recognizable as the widest one starting above the monitor).
            bool label = zone.Anchor != DeviceAnchors.Surround || zone.Y < _sourceRect.Y - ZoneThickness;
            if (label)
            {
                var text = new TextBlock
                {
                    Text = zone.Anchor,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    Opacity = 0.85,
                    IsHitTestVisible = false,
                };
                Panel.SetZIndex(text, 16);
                Canvas.SetLeft(text, zone.X + 4);
                Canvas.SetTop(text, zone.Y + 2);
                _canvas.Children.Add(text);
                _zoneExtras.Add(text);
            }
        }
    }

    private void HighlightZone(string anchor)
    {
        foreach (var (zone, shape) in _zoneShapes)
        {
            bool hit = string.Equals(zone.Anchor, anchor, StringComparison.OrdinalIgnoreCase);
            shape.Fill = new SolidColorBrush(hit
                ? Color.FromArgb(0x66, 0x37, 0x6b, 0xa6)
                : Color.FromArgb(0x28, 0x2d, 0x5b, 0x8c));
            shape.StrokeThickness = hit ? 2 : 1;
        }
    }

    private void HideZones()
    {
        foreach (var (_, shape) in _zoneShapes)
        {
            _canvas.Children.Remove(shape);
        }
        _zoneShapes.Clear();
        foreach (var extra in _zoneExtras)
        {
            _canvas.Children.Remove(extra);
        }
        _zoneExtras.Clear();
    }

    // ── construction ────────────────────────────────────────────────────────────────────────────────

    private DeviceVisual BuildVisual(RgbBackendDevice device)
    {
        int ledCount = device.NormalizedLeds.Length;
        int columns = device.Type switch
        {
            "Keyboard" => 18,
            "LedStripe" => Math.Min(ledCount, 27),
            "Mouse" => 2,
            _ => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(ledCount))),
        };

        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(2) };
        var leds = new Ellipse[ledCount];
        for (int i = 0; i < ledCount; i++)
        {
            var dot = new Ellipse
            {
                Width = Dot,
                Height = Dot,
                Margin = new Thickness(1),
                Fill = Brushes.Black,
                Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3a)),
                StrokeThickness = 0.5,
            };
            leds[i] = dot;
            grid.Children.Add(dot);
        }

        var label = new TextBlock
        {
            Foreground = Brushes.Silver,
            FontSize = 10,
            Margin = new Thickness(2, 0, 2, 2),
        };

        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(grid);

        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Cursor = Cursors.Hand,
            ToolTip = "Drag to an anchor zone around the source monitor · click to edit (flip / brightness / enabled)",
            Child = stack,
        };
        Panel.SetZIndex(container, 20); // above effect viewports

        var visual = new DeviceVisual { Device = device, Container = container, Leds = leds, Label = label };
        container.MouseLeftButtonDown += (_, e) => OnChipMouseDown(visual, e);
        container.MouseMove += (_, e) => OnChipMouseMove(visual, e);
        container.MouseLeftButtonUp += (_, e) => OnChipMouseUp(visual, e);
        _canvas.Children.Add(container);

        return visual;
    }

    private DeviceVisual? Find(string deviceId) => _visuals
        .FirstOrDefault(v => string.Equals(v.Device.Id, deviceId, StringComparison.OrdinalIgnoreCase));

    private DevicePlacement GetOrCreate(string deviceId)
    {
        if (!_placements.TryGetValue(deviceId, out var placement))
        {
            placement = new DevicePlacement();
            _placements[deviceId] = placement;
        }
        return placement;
    }

    private string Anchor(string deviceId) =>
        _placements.TryGetValue(deviceId, out var p) ? p.Anchor : DeviceAnchors.Auto;

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    private static string Short(string name)
    {
        int idx = name.IndexOf('(');
        return idx > 0 ? name[..idx].Trim() : name;
    }

    private sealed class DeviceVisual
    {
        public required RgbBackendDevice Device { get; init; }
        public required Border Container { get; init; }
        public required Ellipse[] Leds { get; init; }
        public required TextBlock Label { get; init; }
        public Ellipse[]? RingDots { get; set; }
    }
}
#endif

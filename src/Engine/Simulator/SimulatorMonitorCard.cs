#if SIMULATOR_ENABLED
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The floating per-monitor card: select a monitor on the canvas
/// (edit mode) and everything about it is right there — geometry (live-apply), primary/rotate,
/// <b>Set as source</b>, the content picker (mirror a real display / picture or video / demo pattern /
/// blank), the effect dropdown (the real catalog), and Remove. Replaces the docked
/// <c>SimulatorEditorPanel</c> form. Every action funnels through the existing seams
/// (<see cref="SimulatorSceneController"/> + the simulated detection service); airspace is a non-issue
/// because the card exists only in edit mode, when the effect surfaces are hidden. Placed by
/// <see cref="SimulatorWindow.Reflow"/> via the pure <see cref="SimulatorCardMath"/>. Compiled out of
/// Release.
/// </summary>
public sealed class SimulatorMonitorCard : Border
{
    // Sizes come from the curated real-world catalog (SimulatorMonitorCatalog) — the monitor's
    // DIMENSIONS on the virtual desktop, never framed as a display "resolution" (there is no
    // DPI/scaling concept under test, so the UI never pretends otherwise).
    private const string CustomSize = "Custom size";
    private const string GlobalDefaultEffect = "(global default)";

    private const string ModeDemo = "Demo pattern";
    private const string ModeMedia = "Picture / video";
    private const string ModeMirror = "Mirror real display";
    private const string ModeBlank = "Blank";

    private readonly SimulatorSceneController _scene;
    private readonly SimulatedMonitorDetectionService _detection;
    private readonly IReadOnlyList<SimulatorEffectsCatalog.EffectEntry> _effects;
    private readonly Func<IReadOnlyList<MonitorInfo>> _realMonitors;
    private readonly Func<ApplicationSettings?> _liveSettings;
    private readonly ILogger _logger;

    private readonly TextBlock _title = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeights.Bold,
        FontSize = 13,
        Margin = new Thickness(0, 0, 0, 4),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly ComboBox _size = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _x = NumBox();
    private readonly TextBox _y = NumBox();
    private readonly TextBox _w = NumBox();
    private readonly TextBox _h = NumBox();
    private readonly CheckBox _primary = new() { Content = "Primary", Margin = new Thickness(0, 3, 0, 2) };
    private readonly ComboBox _contentMode = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly ComboBox _pattern = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly ComboBox _mirror = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _browse;
    private readonly TextBlock _mediaPath = new()
    {
        FontSize = 10,
        Margin = new Thickness(0, 1, 0, 1),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly ComboBox _effect = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBlock _sourceBadge = new()
    {
        Text = "● this monitor drives the effect (source)",
        FontSize = 10,
        Margin = new Thickness(0, 2, 0, 2),
        Visibility = Visibility.Collapsed,
    };

    private bool _updating; // suppress control events while (re)populating

    /// <summary>The monitor this card is showing, or null when hidden.</summary>
    public string? MonitorId { get; private set; }

    /// <summary>Raised after any geometry/primary change so the window can RefreshTopology + Reflow.</summary>
    public event Action? GeometryEdited;

    /// <summary>Raised after Remove (the id no longer exists).</summary>
    public event Action<string>? MonitorRemoved;

    public SimulatorMonitorCard(
        SimulatorSceneController scene,
        SimulatedMonitorDetectionService detection,
        IReadOnlyList<SimulatorEffectsCatalog.EffectEntry> effects,
        Func<IReadOnlyList<MonitorInfo>> realMonitors,
        Func<ApplicationSettings?> liveSettings,
        ILogger logger)
    {
        _scene = scene;
        _detection = detection;
        _effects = effects;
        _realMonitors = realMonitors;
        _liveSettings = liveSettings;
        _logger = logger;

        Width = 270;
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x16, 0x16, 0x1c));
        BorderBrush = SimulatorTheme.ControlBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10);
        Visibility = Visibility.Collapsed;
        SimulatorTheme.Apply(Resources);

        _sourceBadge.Foreground = SimulatorTheme.Accent;
        _mediaPath.Foreground = SimulatorTheme.TextMuted;

        var root = new StackPanel();
        root.Children.Add(_title);
        root.Children.Add(_sourceBadge);

        // ── geometry ────────────────────────────────────────────────────────────────────────────────
        root.Children.Add(Caption("Monitor size & position (Enter applies)"));
        foreach (var entry in SimulatorMonitorCatalog.All)
        {
            _size.Items.Add(entry.Label);
        }
        _size.Items.Add(CustomSize);
        _size.ToolTip = "Real-world monitor sizes (the monitor's dimensions on the virtual desktop)";
        _size.SelectionChanged += (_, _) => OnSizePreset();
        root.Children.Add(_size);
        root.Children.Add(Row("W", _w, "H", _h));
        root.Children.Add(Row("X", _x, "Y", _y));
        WireGeometryBox(_x); WireGeometryBox(_y); WireGeometryBox(_w); WireGeometryBox(_h);

        var flagsRow = new StackPanel { Orientation = Orientation.Horizontal };
        _primary.Checked += (_, _) => OnPrimaryChecked();
        flagsRow.Children.Add(_primary);
        var rotate = SmallBtn("Rotate", "Swap width/height (portrait ↔ landscape)");
        rotate.Click += (_, _) => OnRotate();
        flagsRow.Children.Add(rotate);
        root.Children.Add(flagsRow);

        var setSource = new Button
        {
            Content = "★ Set as source (drives the effect)",
            Style = SimulatorTheme.AccentButtonStyle,
            Margin = new Thickness(0, 5, 0, 2),
            Padding = new Thickness(6, 4, 6, 4),
            ToolTip = "The engine captures exactly one monitor; every other monitor projects from it",
        };
        setSource.Click += (_, _) => OnSetSource();
        root.Children.Add(setSource);

        // ── content ─────────────────────────────────────────────────────────────────────────────────
        root.Children.Add(Caption("Screen content"));
        foreach (string mode in new[] { ModeDemo, ModeMedia, ModeMirror, ModeBlank })
        {
            _contentMode.Items.Add(mode);
        }
        _contentMode.SelectionChanged += (_, _) => OnContentModeChanged();
        root.Children.Add(_contentMode);

        foreach (string pattern in new[] { SyntheticPatterns.Gradient, SyntheticPatterns.Bars, SyntheticPatterns.TestCard })
        {
            _pattern.Items.Add(pattern);
        }
        _pattern.SelectionChanged += (_, _) => OnPatternPicked();
        root.Children.Add(_pattern);

        _browse = SmallBtn("Browse picture / video…", "Pick an image, an image folder, or a video file");
        _browse.HorizontalAlignment = HorizontalAlignment.Stretch;
        _browse.Click += (_, _) => OnBrowseMedia();
        root.Children.Add(_browse);
        root.Children.Add(_mediaPath);

        _mirror.ToolTip = "Mirror one of your ACTUAL displays onto this virtual monitor (real screen capture)";
        _mirror.SelectionChanged += (_, _) => OnMirrorPicked();
        root.Children.Add(_mirror);

        // ── effect ──────────────────────────────────────────────────────────────────────────────────
        root.Children.Add(Caption("Effect on this monitor"));
        _effect.Items.Add(GlobalDefaultEffect);
        foreach (var entry in _effects)
        {
            _effect.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Id });
        }
        _effect.SelectionChanged += (_, _) => OnEffectPicked();
        root.Children.Add(_effect);

        // ── remove ──────────────────────────────────────────────────────────────────────────────────
        var remove = SmallBtn("Remove monitor", "Remove this monitor from the layout");
        remove.HorizontalAlignment = HorizontalAlignment.Stretch;
        remove.Margin = new Thickness(0, 6, 0, 0);
        remove.Click += (_, _) => OnRemove();
        root.Children.Add(remove);

        Child = root;
    }

    // ── show / hide ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows the card for a monitor, populating every control from the live topology + scene.
    /// No-ops (hides) if the id vanished — topology can change under the card (ThreadPool
    /// MonitorsChanged). UI thread.</summary>
    public void ShowFor(string monitorId)
    {
        var monitor = _detection.GetMonitors()
            .FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        if (monitor is null)
        {
            Hide();
            return;
        }

        _updating = true;
        try
        {
            MonitorId = monitor.Id;
            _title.Text = monitor.Name;
            _sourceBadge.Visibility =
                string.Equals(_scene.Current.ResolveSourceId(), monitor.Id, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            _x.Text = monitor.X.ToString(CultureInfo.InvariantCulture);
            _y.Text = monitor.Y.ToString(CultureInfo.InvariantCulture);
            _w.Text = monitor.Width.ToString(CultureInfo.InvariantCulture);
            _h.Text = monitor.Height.ToString(CultureInfo.InvariantCulture);
            _size.SelectedItem =
                SimulatorMonitorCatalog.FindByDimensions(monitor.Width, monitor.Height)?.Label
                ?? CustomSize;
            _primary.IsChecked = monitor.IsPrimary;

            PopulateMirrors();
            var sceneMonitor = _scene.Current.Monitors
                .FirstOrDefault(m => string.Equals(m.Id, monitor.Id, StringComparison.OrdinalIgnoreCase));
            _pattern.SelectedItem = sceneMonitor?.Pattern ?? SyntheticPatterns.Gradient;
            SelectContentMode(sceneMonitor?.Content);
            SelectEffect(monitor.Id, sceneMonitor?.Effect);

            Visibility = Visibility.Visible;
        }
        finally
        {
            _updating = false;
        }
    }

    public void Hide()
    {
        MonitorId = null;
        Visibility = Visibility.Collapsed;
    }

    // ── geometry handlers ───────────────────────────────────────────────────────────────────────────

    private void WireGeometryBox(TextBox box)
    {
        box.KeyDown += (_, e) => OnGeometryKey(e);
        box.LostFocus += (_, _) => ApplyGeometry();
    }

    private void OnGeometryKey(KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            ApplyGeometry();
            e.Handled = true;
        }
    }

    private void ApplyGeometry()
    {
        if (_updating || MonitorId is not { } id)
        {
            return;
        }
        bool edited = false;
        if (TryInt(_x, out int x) && TryInt(_y, out int y))
        {
            edited |= _detection.SetPosition(id, x, y);
        }
        if (TryInt(_w, out int w) && TryInt(_h, out int h) && w > 0 && h > 0)
        {
            edited |= _detection.SetResolution(id, w, h);
        }
        if (edited)
        {
            GeometryEdited?.Invoke();
        }
    }

    private void OnSizePreset()
    {
        if (_updating || MonitorId is not { } id
            || SimulatorMonitorCatalog.FindByLabel(_size.SelectedItem as string) is not { } entry)
        {
            return; // "Custom size" (or nothing) selected — the W/H boxes rule
        }
        _detection.SetResolution(id, entry.Width, entry.Height);
        _w.Text = entry.Width.ToString(CultureInfo.InvariantCulture);
        _h.Text = entry.Height.ToString(CultureInfo.InvariantCulture);
        GeometryEdited?.Invoke();
    }

    private void OnPrimaryChecked()
    {
        if (_updating || MonitorId is not { } id)
        {
            return;
        }
        _detection.SetPrimary(id);
        GeometryEdited?.Invoke();
    }

    private void OnRotate()
    {
        if (MonitorId is not { } id)
        {
            return;
        }
        var monitor = _detection.GetMonitors().FirstOrDefault(m => m.Id == id);
        if (monitor is null)
        {
            return;
        }
        _detection.SetOrientation(id, portrait: monitor.Width >= monitor.Height); // toggle
        GeometryEdited?.Invoke();
        ShowFor(id); // refresh W/H fields
    }

    private void OnSetSource()
    {
        if (MonitorId is not { } id)
        {
            return;
        }
        _scene.SetSource(id);
        _sourceBadge.Visibility = Visibility.Visible;
    }

    private void OnRemove()
    {
        if (MonitorId is not { } id)
        {
            return;
        }
        _detection.RemoveMonitor(id);
        Hide();
        MonitorRemoved?.Invoke(id);
    }

    // ── content handlers ────────────────────────────────────────────────────────────────────────────

    private void PopulateMirrors()
    {
        _mirror.Items.Clear();
        foreach (var real in _realMonitors())
        {
            _mirror.Items.Add(new ComboBoxItem { Content = real.Name, Tag = real.Id, ToolTip = real.Id });
        }
        _mirror.IsEnabled = _mirror.Items.Count > 0;
    }

    private void SelectContentMode(SimContent? content)
    {
        string mode = content?.Kind?.ToLowerInvariant() switch
        {
            SimContent.Media => ModeMedia,
            SimContent.Mirror => ModeMirror,
            SimContent.Blank => ModeBlank,
            _ => ModeDemo,
        };
        _contentMode.SelectedItem = mode;
        _mediaPath.Text = content?.MediaPath ?? string.Empty;
        if (content?.PhysicalMonitorId is { } physicalId)
        {
            foreach (var item in _mirror.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, physicalId, StringComparison.OrdinalIgnoreCase))
                {
                    _mirror.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            _mirror.SelectedItem = null;
        }
        UpdateContentRows(mode);
    }

    private void UpdateContentRows(string mode)
    {
        _pattern.Visibility = mode == ModeDemo ? Visibility.Visible : Visibility.Collapsed;
        _browse.Visibility = mode == ModeMedia ? Visibility.Visible : Visibility.Collapsed;
        _mediaPath.Visibility = mode == ModeMedia && _mediaPath.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _mirror.Visibility = mode == ModeMirror ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnContentModeChanged()
    {
        if (_contentMode.SelectedItem is not string mode)
        {
            return;
        }
        UpdateContentRows(mode);
        if (_updating || MonitorId is not { } id)
        {
            return;
        }
        switch (mode)
        {
            case ModeDemo:
                _scene.SetMonitorContent(id, null); // back to the synthetic pattern
                break;
            case ModeBlank:
                _scene.SetMonitorContent(id, new SimContent { Kind = SimContent.Blank });
                break;
            case ModeMedia:
                OnBrowseMedia(); // straight into the picker — one gesture to content
                break;
            case ModeMirror:
                if (_mirror.SelectedItem is ComboBoxItem picked && picked.Tag is string physicalId)
                {
                    _scene.SetMonitorContent(id, new SimContent { Kind = SimContent.Mirror, PhysicalMonitorId = physicalId });
                }
                // else: wait for the dropdown pick (OnMirrorPicked applies it)
                break;
        }
    }

    private void OnPatternPicked()
    {
        if (_updating || MonitorId is not { } id || _pattern.SelectedItem is not string pattern)
        {
            return;
        }
        _scene.SetMonitorPattern(id, pattern);
        if (_contentMode.SelectedItem as string == ModeDemo)
        {
            _scene.SetMonitorContent(id, null); // make sure the pattern actually shows
        }
    }

    private void OnBrowseMedia()
    {
        if (MonitorId is not { } id)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Pick a picture or video for this monitor",
            Filter = Content.SimMediaKinds.OpenFileFilter,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        _mediaPath.Text = dialog.FileName;
        UpdateContentRows(ModeMedia);
        _scene.SetMonitorContent(id, new SimContent { Kind = SimContent.Media, MediaPath = dialog.FileName });
    }

    private void OnMirrorPicked()
    {
        if (_updating || MonitorId is not { } id
            || _mirror.SelectedItem is not ComboBoxItem item || item.Tag is not string physicalId)
        {
            return;
        }
        _scene.SetMonitorContent(id, new SimContent { Kind = SimContent.Mirror, PhysicalMonitorId = physicalId });
    }

    // ── effect handlers ─────────────────────────────────────────────────────────────────────────────

    private void SelectEffect(string monitorId, string? sceneEffect)
    {
        // Live engine state wins (a control-window change counts too); the scene value is the fallback.
        string? effectId = sceneEffect;
        if (_liveSettings() is { } live && live.EffectByMonitorId.TryGetValue(monitorId, out string? liveEffect)
            && !string.IsNullOrWhiteSpace(liveEffect))
        {
            effectId = liveEffect;
        }

        if (string.IsNullOrWhiteSpace(effectId))
        {
            _effect.SelectedIndex = 0; // "(global default)"
            return;
        }
        foreach (var item in _effect.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, effectId, StringComparison.OrdinalIgnoreCase))
            {
                _effect.SelectedItem = item;
                return;
            }
        }
        _effect.SelectedIndex = 0;
    }

    private void OnEffectPicked()
    {
        if (_updating || MonitorId is not { } id)
        {
            return;
        }
        string? effectId = (_effect.SelectedItem as ComboBoxItem)?.Tag as string; // null for "(global default)"
        _scene.SetMonitorEffect(id, effectId);
    }

    // ── tiny UI builders ────────────────────────────────────────────────────────────────────────────

    private static TextBox NumBox() => new() { Width = 62, Margin = new Thickness(2) };

    private static bool TryInt(TextBox box, out int value) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static Button SmallBtn(string text, string tooltip) => new()
    {
        Content = text,
        Style = SimulatorTheme.ButtonStyle,
        Margin = new Thickness(4, 3, 0, 2),
        Padding = new Thickness(8, 2, 8, 2),
        ToolTip = tooltip,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = SimulatorTheme.TextMuted,
        FontSize = 10,
        Margin = new Thickness(0, 7, 0, 1),
    };

    private static StackPanel Row(string l1, TextBox t1, string l2, TextBox t2)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = l1, Foreground = Brushes.Silver, VerticalAlignment = VerticalAlignment.Center, Width = 16 });
        row.Children.Add(t1);
        row.Children.Add(new TextBlock { Text = l2, Foreground = Brushes.Silver, VerticalAlignment = VerticalAlignment.Center, Width = 16, Margin = new Thickness(8, 0, 0, 0) });
        row.Children.Add(t2);
        return row;
    }
}
#endif

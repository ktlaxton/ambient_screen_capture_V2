#if SIMULATOR_ENABLED
using System.Windows;
using System.Windows.Controls;
using AmbientFx.Models;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The mini-card for a clicked peripheral chip: anchor dropdown
/// (the same seven REAL anchors the drop zones offer), flip, per-device brightness, and enabled —
/// exactly the <see cref="DevicePlacement"/> fields the shipped product supports, hot-applied through
/// <see cref="SimulatorPeripheralLayer.SetPlacement"/> (the existing SetPlacements path). Edit mode
/// only; placed by <see cref="SimulatorWindow.Reflow"/> beside the chip. Compiled out of Release.
/// </summary>
public sealed class SimulatorPeripheralCard : Border
{
    private readonly SimulatorPeripheralLayer _layer;

    private readonly TextBlock _title = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeights.Bold,
        FontSize = 12,
        Margin = new Thickness(0, 0, 0, 4),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly ComboBox _anchor = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _flip = new()
    {
        Content = "Flip direction",
        Margin = new Thickness(0, 3, 0, 2),
        ToolTip = "Reverse the zone order along the fed edge (strip mounted backwards)",
    };
    private readonly Slider _brightness = new()
    {
        Minimum = 0,
        Maximum = 1,
        Value = 1,
        Margin = new Thickness(0, 2, 0, 0),
        Style = SimulatorTheme.SliderStyle,
        ToolTip = "Per-device brightness multiplier",
    };
    private readonly TextBlock _brightnessValue = new()
    {
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
        Width = 32,
    };
    private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(0, 3, 0, 0) };

    private bool _updating;

    /// <summary>The device this card is editing, or null when hidden.</summary>
    public string? DeviceId { get; private set; }

    public SimulatorPeripheralCard(SimulatorPeripheralLayer layer)
    {
        _layer = layer;

        Width = 210;
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x16, 0x16, 0x1c));
        BorderBrush = SimulatorTheme.ControlBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10);
        Visibility = Visibility.Collapsed;
        SimulatorTheme.Apply(Resources);

        _brightnessValue.Foreground = SimulatorTheme.TextMuted;

        var root = new StackPanel();
        root.Children.Add(_title);

        root.Children.Add(Caption("Anchor (or drag the chip)"));
        foreach (string anchor in DeviceAnchors.All)
        {
            _anchor.Items.Add(anchor);
        }
        _anchor.SelectionChanged += (_, _) => Apply(p => p.Anchor = _anchor.SelectedItem as string ?? DeviceAnchors.Auto);
        root.Children.Add(_anchor);

        _flip.Checked += (_, _) => Apply(p => p.Flip = true);
        _flip.Unchecked += (_, _) => Apply(p => p.Flip = false);
        root.Children.Add(_flip);

        root.Children.Add(Caption("Brightness"));
        var brightnessRow = new DockPanel();
        DockPanel.SetDock(_brightnessValue, Dock.Right);
        brightnessRow.Children.Add(_brightnessValue);
        brightnessRow.Children.Add(_brightness);
        _brightness.ValueChanged += (_, _) =>
        {
            _brightnessValue.Text = _brightness.Value.ToString("0.00");
            Apply(p => p.Brightness = (float)_brightness.Value);
        };
        root.Children.Add(brightnessRow);

        _enabled.Checked += (_, _) => Apply(p => p.Enabled = true);
        _enabled.Unchecked += (_, _) => Apply(p => p.Enabled = false);
        root.Children.Add(_enabled);

        Child = root;
    }

    /// <summary>Shows the card for a device, populated from the live shared placements. UI thread.</summary>
    public void ShowFor(string deviceId)
    {
        _updating = true;
        try
        {
            DeviceId = deviceId;
            var placement = _layer.PlacementSnapshot(deviceId);
            _title.Text = _layer.DeviceName(deviceId);
            _anchor.SelectedItem = DeviceAnchors.IsValid(placement.Anchor) ? placement.Anchor : DeviceAnchors.Auto;
            _flip.IsChecked = placement.Flip;
            _brightness.Value = Math.Clamp(placement.Brightness, 0f, 1f);
            _brightnessValue.Text = _brightness.Value.ToString("0.00");
            _enabled.IsChecked = placement.Enabled;
            Visibility = Visibility.Visible;
        }
        finally
        {
            _updating = false;
        }
    }

    public void Hide()
    {
        DeviceId = null;
        Visibility = Visibility.Collapsed;
    }

    private void Apply(Action<DevicePlacement> mutate)
    {
        if (_updating || DeviceId is not { } id)
        {
            return;
        }
        _layer.SetPlacement(id, mutate);
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = SimulatorTheme.TextMuted,
        FontSize = 10,
        Margin = new Thickness(0, 6, 0, 1),
    };
}
#endif

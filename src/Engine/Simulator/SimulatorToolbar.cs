#if SIMULATOR_ENABLED
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). The screen-fixed top toolbar of the composite window — the
/// scene-level controls: <b>Presets ▾</b> (save the current scene / load user presets / curated
/// templates / "⟳ My real setup" / blank slate), <b>+ Add monitor ▾</b> (common sizes),
/// <b>Edit ↔ Preview</b>, <b>⚡ Display change</b>, an <b>FPS</b> picker, and <b>Fit</b>. Every
/// action funnels through <see cref="SimulatorSceneController"/> or a window callback — no engine
/// logic lives here. Replaces the Story 10.6 inline toolbar + the editor panel's scenario-library /
/// save / FPS / simulate-change controls. Compiled out of Release.
/// </summary>
public sealed class SimulatorToolbar : Border
{
    // Sizes come from the curated real-world catalog (SimulatorMonitorCatalog) — plain footprints,
    // never framed as display "resolutions" (the simulator has no DPI/scaling concept).

    private readonly SimulatorSceneController _scene;
    private readonly Func<IReadOnlyList<MonitorInfo>> _realMonitors;
    private readonly Func<ApplicationSettings?> _liveSettings;
    private readonly ILogger _logger;

    private readonly Button _modeButton;
    private readonly Popup _presetsPopup;
    private readonly StackPanel _presetsPanel = new();
    private readonly TextBox _presetName = new() { MinWidth = 118, Margin = new Thickness(0, 0, 4, 0) };

    public SimulatorToolbar(
        SimulatorSceneController scene,
        Func<IReadOnlyList<MonitorInfo>> realMonitors,
        Func<ApplicationSettings?> liveSettings,
        Action toggleMode,
        Action<int, int> addMonitor,
        Action simulateDisplayChange,
        Action fit,
        ILogger logger)
    {
        _scene = scene;
        _realMonitors = realMonitors;
        _liveSettings = liveSettings;
        _logger = logger;

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 8, 0, 0);
        SimulatorTheme.Apply(Resources);

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        // ── Presets ▾ ────────────────────────────────────────────────────────────────────────────────
        var presetsButton = Chip("Presets ▾", "Save or load a whole test bench — layout, content, effects, peripherals");
        _presetsPopup = MakePopup(presetsButton, _presetsPanel);
        presetsButton.Click += (_, _) =>
        {
            RebuildPresetsMenu();
            _presetsPopup.IsOpen = !_presetsPopup.IsOpen;
        };
        row.Children.Add(presetsButton);

        // ── + Add monitor ▾ ─────────────────────────────────────────────────────────────────────────
        var addButton = Chip("+ Add monitor ▾", "Add a real-world monitor size at the right edge, then drag it into place");
        var addPanel = new StackPanel();
        var addPopup = MakePopup(addButton, addPanel);
        foreach (string category in SimulatorMonitorCatalog.Categories)
        {
            addPanel.Children.Add(Caption(category));
            foreach (var entry in SimulatorMonitorCatalog.All.Where(e => e.Category == category))
            {
                var captured = entry;
                addPanel.Children.Add(MenuRow("  " + captured.Label, () =>
                {
                    addPopup.IsOpen = false;
                    addMonitor(captured.Width, captured.Height);
                }));
            }
        }
        addButton.Click += (_, _) => addPopup.IsOpen = !addPopup.IsOpen;
        row.Children.Add(addButton);

        // ── Edit ↔ Preview ──────────────────────────────────────────────────────────────────────────
        _modeButton = new Button
        {
            Content = "▶ Preview effects",
            Style = SimulatorTheme.AccentButtonStyle,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(4, 0, 4, 0),
            ToolTip = "Switch between editing the layout (drag monitors) and previewing the live effects",
        };
        _modeButton.Click += (_, _) => toggleMode();
        row.Children.Add(_modeButton);

        // ── ⚡ Display change ────────────────────────────────────────────────────────────────────────
        var simulate = Chip("⚡ Display change", "Fire the real hot-plug/resolution-change path (MonitorsChanged)");
        simulate.Click += (_, _) => simulateDisplayChange();
        row.Children.Add(simulate);

        // ── FPS ─────────────────────────────────────────────────────────────────────────────────────
        row.Children.Add(new TextBlock
        {
            Text = "FPS",
            Foreground = SimulatorTheme.TextMuted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 3, 0),
        });
        var fps = new ComboBox { Width = 58, ToolTip = "Global FPS ceiling (setGlobal)" };
        foreach (int value in new[] { 30, 60, 120 })
        {
            fps.Items.Add(value);
        }
        fps.SelectedItem = 60;
        fps.SelectionChanged += (_, _) =>
        {
            if (fps.SelectedItem is int value)
            {
                _scene.SetGlobalFps(value);
            }
        };
        row.Children.Add(fps);

        // ── Fit ─────────────────────────────────────────────────────────────────────────────────────
        var fitButton = Chip("Fit", "Reset zoom & pan to fit the whole layout");
        fitButton.Click += (_, _) => fit();
        row.Children.Add(fitButton);

        Child = row;
    }

    /// <summary>Called by the window's SetArrangeMode so the toggle always offers the OTHER mode.</summary>
    public void SetModeLabel(string label) => _modeButton.Content = label;

    // ── presets menu ────────────────────────────────────────────────────────────────────────────────

    private void RebuildPresetsMenu()
    {
        _presetsPanel.Children.Clear();

        // Save the current scene under a typed name.
        _presetsPanel.Children.Add(Caption("Save current scene"));
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 4) };
        _presetName.Text = string.IsNullOrWhiteSpace(_scene.Current.Name) ? "my-setup" : _scene.Current.Name;
        var saveButton = new Button
        {
            Content = "Save",
            Style = SimulatorTheme.AccentButtonStyle,
            Padding = new Thickness(10, 2, 10, 2),
        };
        saveButton.Click += (_, _) => SaveCurrentScene();
        saveRow.Children.Add(_presetName);
        saveRow.Children.Add(saveButton);
        _presetsPanel.Children.Add(saveRow);

        // The user's saved presets.
        var presets = SimulatorPresetStore.List();
        if (presets.Count > 0)
        {
            _presetsPanel.Children.Add(Caption("My presets"));
            foreach (var preset in presets)
            {
                string path = preset.Path;
                _presetsPanel.Children.Add(MenuRow("  " + preset.Name, () => LoadPreset(path)));
            }
        }

        // Curated templates (the Story 10.5 library).
        _presetsPanel.Children.Add(Caption("Templates"));
        foreach (string name in SimulatorScenarioLibrary.Names)
        {
            string captured = name;
            _presetsPanel.Children.Add(MenuRow("  " + captured, () =>
            {
                _presetsPopup.IsOpen = false;
                _scene.ApplyScene(SimulatorScenarioLibrary.Load(captured, _logger));
            }));
        }

        // Start-over actions.
        _presetsPanel.Children.Add(Separator());
        _presetsPanel.Children.Add(MenuRow("⟳ My real setup (mirrored)", () =>
        {
            _presetsPopup.IsOpen = false;
            _scene.ApplyRealSetup(_realMonitors());
        }));
        _presetsPanel.Children.Add(MenuRow("▢ Blank slate", () =>
        {
            _presetsPopup.IsOpen = false;
            _scene.ApplyBlank();
        }));
    }

    private void SaveCurrentScene()
    {
        var scene = _scene.CaptureScene(_liveSettings());
        if (SimulatorPresetStore.Save(scene, _presetName.Text, _logger))
        {
            _scene.Current.Name = scene.Name; // keep the on-screen scene name in sync with the file
            RebuildPresetsMenu();             // the new preset appears immediately
        }
    }

    private void LoadPreset(string path)
    {
        _presetsPopup.IsOpen = false;
        if (SimulatorPresetStore.TryLoad(path, _logger) is { } scene)
        {
            _scene.ApplyScene(scene);
        }
    }

    // ── tiny UI builders ────────────────────────────────────────────────────────────────────────────

    private static Button Chip(string text, string tooltip) => new()
    {
        Content = text,
        Style = SimulatorTheme.ButtonStyle,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(4, 0, 4, 0),
        ToolTip = tooltip,
    };

    private static Popup MakePopup(UIElement target, StackPanel content)
    {
        var panel = SimulatorTheme.PopupPanel();
        panel.Child = content;
        return new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = 2,
            Child = panel,
        };
    }

    private static Button MenuRow(string text, Action onClick)
    {
        var button = new Button { Content = text, Style = SimulatorTheme.MenuRowStyle };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = SimulatorTheme.TextMuted,
        FontSize = 10,
        Margin = new Thickness(8, 6, 8, 1),
    };

    private static Border Separator() => new()
    {
        Height = 1,
        Background = SimulatorTheme.ControlBorder,
        Margin = new Thickness(4, 5, 4, 4),
    };
}
#endif

#if SIMULATOR_ENABLED
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.6). A small dark theme for the simulator's editor controls. The
/// pre-10.6 panel used default (light) WPF chrome — non-accent buttons were even drawn with a null
/// background, rendering as black-on-dark — so several controls were unreadable on the dark panel. This
/// builds readable light-on-dark styles (with hover/pressed/selected/disabled states) once and applies
/// them as implicit styles to a control's <see cref="ResourceDictionary"/>, so every descendant of that
/// control picks them up. Pure WPF styling — no behavior change, no new dependency. Compiled out of
/// Release.
/// </summary>
public static class SimulatorTheme
{
    public static readonly Brush PanelBg = Frozen(0x16, 0x16, 0x1c);
    public static readonly Brush FieldBg = Frozen(0x1e, 0x1e, 0x26);
    public static readonly Brush ControlBg = Frozen(0x2c, 0x2c, 0x38);
    public static readonly Brush ControlBgHover = Frozen(0x3a, 0x3a, 0x49);
    public static readonly Brush ControlBgPressed = Frozen(0x22, 0x22, 0x2b);
    public static readonly Brush ControlBgDisabled = Frozen(0x20, 0x20, 0x28);
    public static readonly Brush ControlBorder = Frozen(0x55, 0x55, 0x6a);
    public static readonly Brush Text = Frozen(0xec, 0xec, 0xf0);
    public static readonly Brush TextMuted = Frozen(0x9a, 0x9a, 0xa8);
    public static readonly Brush TextDisabled = Frozen(0x70, 0x70, 0x80);
    public static readonly Brush Accent = Frozen(0x2d, 0x5b, 0x8c);
    public static readonly Brush AccentHover = Frozen(0x37, 0x6b, 0xa6);
    public static readonly Brush AccentPressed = Frozen(0x24, 0x49, 0x70);
    public static readonly Brush Selection = Frozen(0x33, 0x55, 0x80);

    private static Style? _buttonStyle;
    private static Style? _accentButtonStyle;
    private static Style? _menuRowStyle;
    private static Style? _sliderStyle;

    /// <summary>The readable dark button style (also applied implicitly to every <see cref="Button"/>).</summary>
    public static Style ButtonStyle => _buttonStyle ??= BuildButtonStyle(ControlBg, Text, ControlBgHover, ControlBgPressed);

    /// <summary>The distinct accent button style (e.g. "Simulate display change").</summary>
    public static Style AccentButtonStyle => _accentButtonStyle ??= BuildButtonStyle(Accent, Brushes.White, AccentHover, AccentPressed);

    /// <summary>UX redesign: a flat full-width row for popup menus (Presets / Add-monitor dropdowns) —
    /// transparent until hovered, content left-aligned, no border.</summary>
    public static Style MenuRowStyle => _menuRowStyle ??= BuildMenuRowStyle();

    /// <summary>UX redesign: a readable dark slider (peripheral mini-card brightness).</summary>
    public static Style SliderStyle => _sliderStyle ??= BuildSliderStyle();

    /// <summary>UX redesign: the standard chrome for a dropdown popup panel (opaque dark, bordered,
    /// rounded). The caller fills <see cref="Border.Child"/>.</summary>
    public static Border PopupPanel() => new()
    {
        Background = Frozen(0x1a, 0x1a, 0x21),
        BorderBrush = ControlBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6),
        MinWidth = 210,
    };

    /// <summary>
    /// Registers the dark implicit styles on a control's resources. Buttons, dropdowns, the list, text
    /// boxes, and checkboxes under that control become readable on the dark panel. Call once, before the
    /// children are realized (style resolution happens at load time).
    /// </summary>
    public static void Apply(ResourceDictionary res)
    {
        res[typeof(Button)] = ButtonStyle;
        res[typeof(TextBox)] = BuildTextBoxStyle();
        res[typeof(ComboBox)] = BuildComboBoxStyle();
        res[typeof(ComboBoxItem)] = BuildComboBoxItemStyle();
        res[typeof(ListBox)] = BuildListBoxStyle();
        res[typeof(ListBoxItem)] = BuildListBoxItemStyle();
        res[typeof(CheckBox)] = BuildCheckBoxStyle();
    }

    // ── buttons ──────────────────────────────────────────────────────────────────────────────────────

    private static Style BuildButtonStyle(Brush bg, Brush fg, Brush hover, Brush pressed)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetBinding(Border.BackgroundProperty, Tpl(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, ControlBorder);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        bd.SetBinding(Border.PaddingProperty, Tpl(Control.PaddingProperty));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        AddTrigger(template, UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, hover, "bd");
        AddTrigger(template, ButtonBase.IsPressedProperty, true, Border.BackgroundProperty, pressed, "bd");

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, ControlBgDisabled, "bd"));
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, TextDisabled));
        template.Triggers.Add(disabled);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, bg));
        style.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildMenuRowStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetBinding(Border.BackgroundProperty, Tpl(Control.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        bd.SetBinding(Border.PaddingProperty, Tpl(Control.PaddingProperty));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        AddTrigger(template, UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, ControlBgHover, "bd");
        AddTrigger(template, ButtonBase.IsPressedProperty, true, Border.BackgroundProperty, ControlBgPressed, "bd");

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    // ── slider (peripheral mini-card brightness) ─────────────────────────────────────────────────────

    /// <summary>Built from XAML: a Track's thumb/repeat-button parts aren't assignable through
    /// FrameworkElementFactory, so this one template is parsed instead of composed.</summary>
    private static Style BuildSliderStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="Slider">
              <Setter Property="Height" Value="18"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Slider">
                    <Grid VerticalAlignment="Center" Background="Transparent">
                      <Border Height="4" CornerRadius="2" Background="#2c2c38"
                              BorderBrush="#55556a" BorderThickness="1"/>
                      <Track x:Name="PART_Track">
                        <Track.DecreaseRepeatButton>
                          <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Opacity="0" Focusable="False"/>
                        </Track.DecreaseRepeatButton>
                        <Track.IncreaseRepeatButton>
                          <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Opacity="0" Focusable="False"/>
                        </Track.IncreaseRepeatButton>
                        <Track.Thumb>
                          <Thumb Width="12" Height="12" Focusable="False">
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Ellipse Fill="#ececf0" Stroke="#55556a" StrokeThickness="1"/>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                      </Track>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // ── text boxes (default template already honors Background/BorderBrush) ─────────────────────────────

    private static Style BuildTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, FieldBg));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 2, 3, 2)));
        style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Text));
        style.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, Selection));
        return style;
    }

    // ── combo box + items ──────────────────────────────────────────────────────────────────────────────

    private static Style BuildComboBoxStyle()
    {
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ControlBg));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 24.0));
        style.Setters.Add(new Setter(Control.TemplateProperty, BuildComboBoxTemplate()));
        return style;
    }

    private static ControlTemplate BuildComboBoxTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));

        // The clickable field (dark border + chevron); a ToggleButton drives IsDropDownOpen.
        var toggle = new FrameworkElementFactory(typeof(ToggleButton), "toggle");
        toggle.SetValue(ToggleButton.FocusableProperty, false);
        toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding
        {
            Path = new PropertyPath(ComboBox.IsDropDownOpenProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Mode = BindingMode.TwoWay,
        });
        toggle.SetValue(Control.TemplateProperty, BuildToggleTemplate());
        root.AppendChild(toggle);

        // The selected item, drawn over the field.
        var content = new FrameworkElementFactory(typeof(ContentPresenter), "ContentSite");
        content.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(7, 0, 22, 0));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetBinding(ContentPresenter.ContentProperty, Tpl(ComboBox.SelectionBoxItemProperty));
        content.SetBinding(ContentPresenter.ContentTemplateProperty, Tpl(ComboBox.SelectionBoxItemTemplateProperty));
        root.AppendChild(content);

        // The dropdown (PART_Popup is required by ComboBox for placement).
        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetBinding(Popup.IsOpenProperty, Tpl(ComboBox.IsDropDownOpenProperty));

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, FieldBg);
        popupBorder.SetValue(Border.BorderBrushProperty, ControlBorder);
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.MarginProperty, new Thickness(0, 1, 0, 0));
        popupBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding
        {
            Path = new PropertyPath(FrameworkElement.ActualWidthProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
        });

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.MaxHeightProperty, 240.0);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        popupBorder.AppendChild(scroll);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        return new ControlTemplate(typeof(ComboBox)) { VisualTree = root };
    }

    private static ControlTemplate BuildToggleTemplate()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, ControlBg);
        bd.SetValue(Border.BorderBrushProperty, ControlBorder);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

        var chevron = new FrameworkElementFactory(typeof(Path));
        chevron.SetValue(Path.DataProperty, Geometry.Parse("M 0 0 L 8 0 L 4 4 Z"));
        chevron.SetValue(Shape.FillProperty, TextMuted);
        chevron.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        bd.AppendChild(chevron);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = bd };
        AddTrigger(template, UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, ControlBgHover, "bd");
        return template;
    }

    private static Style BuildComboBoxItemStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetBinding(Border.BackgroundProperty, Tpl(Control.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new Thickness(7, 4, 7, 4));
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = bd };
        AddTrigger(template, ComboBoxItem.IsHighlightedProperty, true, Border.BackgroundProperty, Selection, "bd");

        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    // ── list box + items ────────────────────────────────────────────────────────────────────────────────

    private static Style BuildListBoxStyle()
    {
        var style = new Style(typeof(ListBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, FieldBg));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        return style;
    }

    private static Style BuildListBoxItemStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetBinding(Border.BackgroundProperty, Tpl(Control.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd };
        AddTrigger(template, UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, ControlBgHover, "bd");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, true, Border.BackgroundProperty, Selection, "bd");

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildCheckBoxStyle()
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        return style;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static void AddTrigger(
        ControlTemplate template, DependencyProperty when, object isValue,
        DependencyProperty set, object toValue, string targetName)
    {
        var trigger = new Trigger { Property = when, Value = isValue };
        trigger.Setters.Add(new Setter(set, toValue, targetName));
        template.Triggers.Add(trigger);
    }

    private static Binding Tpl(DependencyProperty property) => new()
    {
        Path = new PropertyPath(property),
        RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
#endif

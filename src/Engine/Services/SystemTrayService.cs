using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinForms = System.Windows.Forms;

namespace AmbientFx.Services;

/// <summary>
/// System tray icon (FR10) built on WinForms <see cref="WinForms.NotifyIcon"/> (the WPF
/// dispatcher pumps its messages; never call WinForms Application.Run). The icon is drawn
/// procedurally — no .ico asset to ship.
/// <para>
/// Threading: <see cref="Initialize"/> must run on the WPF UI thread; it captures the UI
/// <see cref="SynchronizationContext"/> so <see cref="Update"/> can be called from any thread.
/// Menu events are raised on the UI thread.
/// </para>
/// </summary>
public sealed class SystemTrayService : ISystemTrayService
{
    private readonly ILogger<SystemTrayService> _logger;

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _menu;
    private WinForms.ToolStripMenuItem? _toggleItem;
    private WinForms.ToolStripMenuItem? _presetsRoot;
    private Icon? _icon;
    private SynchronizationContext? _uiContext;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler? ToggleEnabledRequested;

    /// <inheritdoc />
    public event EventHandler<string>? PresetSelected;

    /// <inheritdoc />
    public event EventHandler? OpenSettingsRequested;

    /// <inheritdoc />
    public event EventHandler? ExitRequested;

    public SystemTrayService(ILogger<SystemTrayService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Idempotent; must be called on the WPF UI thread (needs its message pump).</remarks>
    public void Initialize()
    {
        if (_disposed)
        {
            _logger.LogWarning("Initialize called on a disposed SystemTrayService; ignored");
            return;
        }

        if (_notifyIcon is not null)
        {
            return; // already initialized
        }

        try
        {
            _uiContext = SynchronizationContext.Current;

            _menu = new WinForms.ContextMenuStrip();

            // Checked state is owned by the coordinator: clicking only raises the request;
            // the checkmark updates when the coordinator calls Update(...) back.
            _toggleItem = new WinForms.ToolStripMenuItem("Effects enabled");
            _toggleItem.Click += (_, _) => ToggleEnabledRequested?.Invoke(this, EventArgs.Empty);

            _presetsRoot = new WinForms.ToolStripMenuItem("Presets");

            var openItem = new WinForms.ToolStripMenuItem("Open AmbientFx");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold); // default-action hint
            openItem.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

            var exitItem = new WinForms.ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

            _menu.Items.Add(_toggleItem);
            _menu.Items.Add(_presetsRoot);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(openItem);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(exitItem);

            RebuildPresetMenu(Array.Empty<string>(), string.Empty);

            _icon = CreateTrayIcon();
            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = _icon,
                Text = "AmbientFx", // NotifyIcon.Text throws beyond 127 chars on .NET 6+
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

            _logger.LogInformation("System tray icon initialized");
        }
        catch (Exception ex)
        {
            // Tray failure must not take the host down (NFR5); the app still works via the control window.
            _logger.LogError(ex, "Failed to initialize the system tray icon");
        }
    }

    /// <inheritdoc />
    /// <remarks>Safe from any thread; marshals to the UI thread captured in <see cref="Initialize"/>.</remarks>
    public void Update(bool isEnabled, IReadOnlyList<string> presetNames, string activePresetName)
    {
        if (_disposed || _notifyIcon is null)
        {
            return;
        }

        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => UpdateCore(isEnabled, presetNames, activePresetName), null);
        }
        else
        {
            UpdateCore(isEnabled, presetNames, activePresetName);
        }
    }

    /// <inheritdoc />
    /// <remarks>Safe from any thread; marshals to the UI thread captured in <see cref="Initialize"/>.</remarks>
    public void ShowNotification(string title, string message, bool isError)
    {
        if (_disposed || _notifyIcon is null)
        {
            return;
        }

        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ShowNotificationCore(title, message, isError), null);
        }
        else
        {
            ShowNotificationCore(title, message, isError);
        }
    }

    private void ShowNotificationCore(string title, string message, bool isError)
    {
        var icon = _notifyIcon;
        if (_disposed || icon is null) return;
        try
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = message.Length > 250 ? message[..250] + "…" : message;
            icon.BalloonTipIcon = isError ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Info;
            icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray balloon notification failed");
        }
    }

    /// <summary>UI-thread body of <see cref="Update"/>; rebuilds dynamic menu state idempotently.</summary>
    private void UpdateCore(bool isEnabled, IReadOnlyList<string> presetNames, string activePresetName)
    {
        if (_disposed || _toggleItem is null || _presetsRoot is null)
        {
            return;
        }

        try
        {
            _toggleItem.Checked = isEnabled;
            RebuildPresetMenu(presetNames ?? Array.Empty<string>(), activePresetName ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update tray menu");
        }
    }

    /// <summary>Replaces the Presets submenu contents. UI thread only.</summary>
    private void RebuildPresetMenu(IReadOnlyList<string> presetNames, string activePresetName)
    {
        if (_presetsRoot is null)
        {
            return;
        }

        // Snapshot then clear: disposing an item detaches it from its owner collection,
        // which would break enumeration if done in place.
        WinForms.ToolStripItem[] stale = _presetsRoot.DropDownItems
            .Cast<WinForms.ToolStripItem>()
            .ToArray();
        _presetsRoot.DropDownItems.Clear();
        foreach (WinForms.ToolStripItem item in stale)
        {
            item.Dispose();
        }

        if (presetNames.Count == 0)
        {
            _presetsRoot.DropDownItems.Add(new WinForms.ToolStripMenuItem("(no presets)") { Enabled = false });
            return;
        }

        foreach (string name in presetNames)
        {
            var item = new WinForms.ToolStripMenuItem(name)
            {
                Checked = string.Equals(name, activePresetName, StringComparison.Ordinal),
            };
            string captured = name;
            item.Click += (_, _) => PresetSelected?.Invoke(this, captured);
            _presetsRoot.DropDownItems.Add(item);
        }
    }

    /// <summary>
    /// Draws the tray icon procedurally: dark disc with a cyan-to-violet gradient ring + orb.
    /// Uses the GetHicon/Icon.FromHandle pattern — the clone owns a managed handle, then the
    /// original GDI handle is released with DestroyIcon (FromHandle never owns it).
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark base disc.
            using (var background = new SolidBrush(Color.FromArgb(16, 18, 30)))
            {
                g.FillEllipse(background, 1f, 1f, 30f, 30f);
            }

            // Cyan -> violet gradient shared by the ring and the center orb.
            using var gradient = new LinearGradientBrush(
                new Rectangle(0, 0, 32, 32),
                Color.FromArgb(0, 229, 255),   // cyan
                Color.FromArgb(155, 77, 255),  // violet
                45f);

            using (var ring = new Pen(gradient, 3f))
            {
                g.DrawEllipse(ring, 4.5f, 4.5f, 23f, 23f);
            }

            g.FillEllipse(gradient, 11f, 11f, 10f, 10f);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon unowned = Icon.FromHandle(hIcon);
            return (Icon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <inheritdoc />
    /// <remarks>Hide before dispose, or a ghost icon lingers in the tray until hover.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _menu?.Dispose();
            _menu = null;
            _icon?.Dispose();
            _icon = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while disposing the system tray icon");
        }
    }
}

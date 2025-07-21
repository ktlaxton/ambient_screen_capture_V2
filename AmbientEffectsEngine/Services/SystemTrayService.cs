using System.Drawing;
using System.Windows.Forms;

namespace AmbientEffectsEngine.Services;

public class SystemTrayService : ISystemTrayService
{
    private NotifyIcon? _notifyIcon;

    public void Initialize()
    {
        // Create the system tray icon
        _notifyIcon = new NotifyIcon
        {
            Text = "Ambient Effects Engine",
            Visible = true,
            Icon = SystemIcons.Application // Use built-in icon for now
        };

        // Create context menu
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Show", null, (s, e) => ShowMainWindow());
        contextMenu.Items.Add("Hide", null, (s, e) => HideMainWindow());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        var mainWindow = System.Windows.Application.Current.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.Show();
            mainWindow.WindowState = System.Windows.WindowState.Normal;
            mainWindow.Activate();
        }
    }

    public void HideMainWindow()
    {
        var mainWindow = System.Windows.Application.Current.MainWindow;
        mainWindow?.Hide();
    }

    public void Shutdown()
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }
}
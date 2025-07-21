using AmbientEffectsEngine.Services;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace AmbientEffectsEngine;

public partial class App : WpfApplication
{
    private SystemTrayService? _systemTrayService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Initialize system tray
        _systemTrayService = new SystemTrayService();
        _systemTrayService.Initialize();
        
        // The main window will be created automatically due to StartupUri
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _systemTrayService?.Shutdown();
        base.OnExit(e);
    }
}
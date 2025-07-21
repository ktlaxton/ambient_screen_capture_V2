using AmbientEffectsEngine.Services;
using AmbientEffectsEngine.Services.Capture;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace AmbientEffectsEngine;

public partial class App : WpfApplication
{
    private SystemTrayService? _systemTrayService;
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Configure services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        
        // Initialize system tray
        _systemTrayService = new SystemTrayService();
        _systemTrayService.Initialize();
        
        // Start screen capture service
        var screenCaptureService = _serviceProvider.GetRequiredService<IScreenCaptureService>();
        screenCaptureService.Start();
        
        // The main window will be created automatically due to StartupUri
    }

    private void ConfigureServices(ServiceCollection services)
    {
        // Register services
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop screen capture
        var screenCaptureService = _serviceProvider?.GetService<IScreenCaptureService>();
        screenCaptureService?.Stop();
        
        _systemTrayService?.Shutdown();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
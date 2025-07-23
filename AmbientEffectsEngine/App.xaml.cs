using AmbientEffectsEngine.Services;
using AmbientEffectsEngine.Services.Capture;
using AmbientEffectsEngine.Services.Processing;
using AmbientEffectsEngine.Services.Rendering;
using AmbientEffectsEngine.ViewModels;
using AmbientEffectsEngine.Views;
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
        
        // Start audio capture service
        var audioCaptureService = _serviceProvider.GetRequiredService<IAudioCaptureService>();
        audioCaptureService.Start();
        
        // Start data processing service
        var dataProcessingService = _serviceProvider.GetRequiredService<IDataProcessingService>();
        dataProcessingService.Start();
        
        // Create and show main window
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private void ConfigureServices(ServiceCollection services)
    {
        // Register services
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IDataProcessingService, DataProcessingService>();
        services.AddSingleton<IEffectFactory, EffectFactory>();
        services.AddSingleton<IEffectsRenderingService, EffectsRenderingService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMonitorDetectionService, MonitorDetectionService>();
        
        // Register ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<MonitorSetupViewModel>();
        
        // Register Views
        services.AddTransient<MainWindow>(provider => new MainWindow(
            provider.GetRequiredService<MainViewModel>(),
            provider));
        services.AddTransient<MonitorSetupPage>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop screen capture
        var screenCaptureService = _serviceProvider?.GetService<IScreenCaptureService>();
        screenCaptureService?.Stop();
        
        // Stop audio capture
        var audioCaptureService = _serviceProvider?.GetService<IAudioCaptureService>();
        audioCaptureService?.Stop();
        
        // Stop data processing service
        var dataProcessingService = _serviceProvider?.GetService<IDataProcessingService>();
        dataProcessingService?.Stop();
        
        // Stop effects rendering service
        var effectsRenderingService = _serviceProvider?.GetService<IEffectsRenderingService>();
        effectsRenderingService?.Stop();
        
        // Stop monitor detection service
        var monitorDetectionService = _serviceProvider?.GetService<IMonitorDetectionService>();
        if (monitorDetectionService is IDisposable disposableService)
        {
            disposableService.Dispose();
        }
        
        _systemTrayService?.Shutdown();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
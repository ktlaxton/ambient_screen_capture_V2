using System.IO;
using AmbientFx.Capture;
using AmbientFx.Hosting;
using AmbientFx.Processing;
using AmbientFx.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;

namespace AmbientFx;

/// <summary>
/// Application shell: Serilog bootstrap, single-instance enforcement, the global
/// exception handler trio, DI composition, and engine lifecycle (start/shutdown).
/// Tray-first lifetime — the process only exits via an explicit Shutdown().
/// </summary>
public partial class App : Application
{
    private const string MutexName = "AmbientFx_SingleInstance_B6E3";
    private const string ActivateEventName = "AmbientFx_Activate_B6E3";

    // Held for the process lifetime — GC of an unreferenced mutex silently breaks single-instancing.
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private ServiceProvider? _provider;
    private IEngineCoordinator? _coordinator;

    private volatile bool _exiting;
    private volatile bool _startupCompleted;

    /// <summary>UI thread. async void is the standard WPF pattern here; failures are caught and fatal-logged.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        // Single instance: first process owns the mutex; later ones signal it to come forward and die.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Log.Information("Another AmbientFx instance is already running; signaling it and exiting");
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivationListener(_activateEvent);

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _provider = services.BuildServiceProvider();

            _coordinator = _provider.GetRequiredService<IEngineCoordinator>();
            var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
            await _coordinator.StartAsync(startMinimized);

            _startupCompleted = true;
            Log.Information("AmbientFx started (minimized={Minimized})", startMinimized);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal startup failure — shutting down");
            Shutdown();
        }
    }

    /// <summary>UI thread. Mirror of OnStartup: engine shutdown, DI disposal, log flush, mutex release.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        try { _activateEvent?.Set(); } catch { /* wake the listener loop so it can exit */ }

        if (_coordinator is not null)
        {
            try
            {
                _coordinator.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during engine shutdown");
            }
        }

        try { _provider?.Dispose(); }
        catch (Exception ex) { Log.Error(ex, "Error disposing the service provider"); }

        try { _activateEvent?.Dispose(); } catch { /* best effort */ }

        if (_mutex is not null)
        {
            // ReleaseMutex is only valid on the acquiring thread; OnStartup/OnExit are both the UI thread.
            try { if (_ownsMutex) _mutex.ReleaseMutex(); } catch { /* abandoned is fine */ }
            _mutex.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AmbientFx", "logs");
        Directory.CreateDirectory(logDir);

        var config = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDir, "ambientfx-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
#if DEBUG
        config.MinimumLevel.Debug();
        config.WriteTo.Debug();
#else
        config.MinimumLevel.Information();
#endif
        Log.Logger = config.CreateLogger();
    }

    /// <summary>
    /// The exception trio (NFR5): dispatcher exceptions are swallowed after startup completes
    /// (one bad handler never kills the app); AppDomain crashes flush the log before dying;
    /// unobserved task faults are logged and observed.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled exception on the UI dispatcher thread");
            args.Handled = _startupCompleted;
            if (!args.Handled)
            {
                Log.CloseAndFlush();
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception,
                "Fatal unhandled exception (terminating={Terminating})", args.IsTerminating);
            Log.CloseAndFlush(); // must flush inside the handler or the crash log is lost
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder.AddSerilog(dispose: false));

        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IDataProcessingService, DataProcessingService>();
        services.AddSingleton<IWebViewWindowManager, WebViewWindowManager>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMonitorDetectionService, MonitorDetectionService>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        services.AddSingleton<IAutostartService, AutostartService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IEngineCoordinator, EngineCoordinator>();
    }

    /// <summary>Second-instance path: poke the first instance's activation event, then exit.</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
            evt.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // First instance is mid-startup and hasn't created the event yet; nothing to activate.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not signal the running instance");
        }
    }

    /// <summary>
    /// Background task that waits on the activation event; each signal marshals to the
    /// dispatcher and brings the control window to front (the window manager handles the
    /// show + Topmost pulse, since Activate() alone may only flash the taskbar).
    /// </summary>
    private void StartActivationListener(EventWaitHandle activateEvent)
    {
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    activateEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (_exiting)
                {
                    return;
                }

                Dispatcher.InvokeAsync(() => _ = ActivateControlWindowAsync());
            }
        });
    }

    /// <summary>UI thread. Never throws — activation is best-effort.</summary>
    private async Task ActivateControlWindowAsync()
    {
        try
        {
            var windowManager = _provider?.GetService<IWebViewWindowManager>();
            if (windowManager is not null)
            {
                await windowManager.ShowControlWindowAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to bring the control window to front");
        }
    }
}

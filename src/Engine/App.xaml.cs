using System.IO;
using AmbientFx.Capture;
using AmbientFx.Devices;
using AmbientFx.Hosting;
using AmbientFx.Licensing;
using AmbientFx.Processing;
using AmbientFx.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

#if SIMULATOR_ENABLED
        // Headless automation hook (Story 10.5): --simulator-render <scenario> [--out <dir>] renders one
        // scenario to a deterministic PNG and exits, without spinning up the engine or any window.
        using (var renderLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(dispose: false)))
        {
            var renderedPath = Simulator.SimulatorRenderHook.TryRunFromArgs(e.Args, renderLoggerFactory.CreateLogger("SimRender"));
            if (renderedPath is not null)
            {
                Log.Information("Simulator headless render complete: {Path}", renderedPath);
                Shutdown();
                return;
            }
        }
#endif

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
#if SIMULATOR_ENABLED
            if (IsSimulatorRequested(e.Args))
            {
                Simulator.SimulatorComposition.Apply(services);
                Log.Information("Layout Simulator enabled (--simulator/AMBIENTFX_SIMULATOR) — capture & monitor detection are simulated");
            }
#endif
            _provider = services.BuildServiceProvider();

            _coordinator = _provider.GetRequiredService<IEngineCoordinator>();
            var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
            await _coordinator.StartAsync(startMinimized);

#if SIMULATOR_ENABLED
            if (IsSimulatorRequested(e.Args))
            {
                // Story 10.6 — linked-window shutdown: in simulator mode the control window and the
                // composite window are one session, so closing EITHER ends the whole thing. The control
                // window's user-close surfaces as ControlWindowCloseRequested (production hides to tray);
                // here it instead triggers the idempotent app shutdown. The SimulatorWindow handles its
                // own close in SimulatorWindow.OnClosed. Production lifetime is untouched (this is behind
                // the simulator gate). The manager already exists (built with the coordinator above), so
                // resolving it here does not re-enter the surface-factory DI cycle.
                if (_provider.GetService<IWebViewWindowManager>() is { } windowManager)
                {
                    windowManager.ControlWindowCloseRequested += (_, _) => Simulator.SimulatorShutdown.Request();
                }

                // Open the composite window eagerly (UI thread, after StartAsync wired the topology). The
                // surface factory only fires when there is a target monitor, so a source-only / single-
                // monitor (or Fallback) scenario would otherwise show nothing.
                _ = _provider.GetService<Simulator.SimulatorWindow>();
            }
#endif

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

#if SIMULATOR_ENABLED
    /// <summary>
    /// Dev/QA runtime gate for the Epic 10 Layout Simulator: the simulated capture/monitor seams are
    /// composed only when <c>--simulator</c> is passed (mirrors the <c>--minimized</c> parse above) or
    /// the <c>AMBIENTFX_SIMULATOR</c> environment variable is set. The whole gate lives behind
    /// <c>SIMULATOR_ENABLED</c>, so it is absent from the signed Release build.
    /// </summary>
    private static bool IsSimulatorRequested(string[] args) =>
        args.Any(a => string.Equals(a, "--simulator", StringComparison.OrdinalIgnoreCase))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMBIENTFX_SIMULATOR"));
#endif

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder.AddSerilog(dispose: false));

        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IDataProcessingService, DataProcessingService>();
        // The window manager builds each effect surface through an injected factory (Story 10.2): the
        // production factory returns a real per-monitor EffectWindow; the simulator overrides this
        // registration with a composite-window viewport factory.
        services.AddSingleton<IWebViewWindowManager>(sp => new WebViewWindowManager(
            sp.GetRequiredService<ILogger<WebViewWindowManager>>(),
            monitor => new EffectWindow(monitor, sp.GetRequiredService<ILogger<EffectWindow>>())));
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMonitorDetectionService, MonitorDetectionService>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        services.AddSingleton<IAutostartService, AutostartService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        // One backend per connection session (Story 8.1): the factory creates a fresh
        // RGB.NET session on each enable (with the currently enabled vendor providers,
        // Story 8.3); disposing it returns lighting control to the vendor software.
        services.AddSingleton<IAmbientDeviceService>(sp => new RgbNetAmbientDeviceService(
            providers => new RgbNetBackend(sp.GetRequiredService<ILogger<RgbNetBackend>>(), providers),
            sp.GetRequiredService<ILogger<RgbNetAmbientDeviceService>>()));
        services.AddSingleton<ILicenseService, LicenseService>();
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

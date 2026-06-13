using System.Reflection;
using System.Text.Json;
using AmbientFx.Bridge;
using AmbientFx.Capture;
using AmbientFx.Devices;
using AmbientFx.Hosting;
using AmbientFx.Models;
using AmbientFx.Processing;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Services;

/// <summary>
/// The conductor: owns the <see cref="ApplicationSettings"/> state, starts/stops the
/// capture -&gt; processing -&gt; effect-window pipeline, handles every bridge command,
/// tray/hotkey events, monitor topology changes, presets, and debounced persistence.
///
/// Threading model: bridge commands, tray events and hotkeys arrive on the UI thread.
/// FrameReady and pipeline Error events arrive on background threads (frames are posted
/// directly via the thread-safe Post* methods; error handling is marshaled to the
/// dispatcher). MonitorsChanged may fire on any thread and is marshaled to the dispatcher.
/// Settings mutations are guarded by <see cref="_gate"/> so the threadpool save can
/// take a consistent snapshot.
/// </summary>
public sealed class EngineCoordinator : IEngineCoordinator
{
    private const int SaveDebounceMs = 600;
    private static readonly TimeSpan AudioErrorToastInterval = TimeSpan.FromMinutes(5);
    private static readonly int[] AllowedFps = { 30, 60, 120 };

    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly ISettingsService _settingsService;
    private readonly IMonitorDetectionService _monitorDetection;
    private readonly ISystemTrayService _tray;
    private readonly IAutostartService _autostart;
    private readonly IHotkeyService _hotkeys;
    private readonly IScreenCaptureService _capture;
    private readonly IAudioCaptureService _audio;
    private readonly IDataProcessingService _processing;
    private readonly IWebViewWindowManager _windowManager;
    private readonly IUpdateService _updates;
    private readonly IAmbientDeviceService _ambientDevices;
    private readonly ILogger<EngineCoordinator> _logger;

    /// <summary>Guards _settings mutations and snapshot clones (UI thread writes, threadpool save reads).</summary>
    private readonly object _gate = new();

    private readonly System.Threading.Timer _saveTimer;

    private ApplicationSettings _settings = new();
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();

    private int _shutdownFlag;
    private int _consecutiveSaveFailures;
    private bool _trayHintShown;
    private DateTime _lastAudioErrorToastUtc = DateTime.MinValue;

    /// <summary>Constructed once by DI as a singleton; all collaborators are singletons too.</summary>
    public EngineCoordinator(
        ISettingsService settingsService,
        IMonitorDetectionService monitorDetection,
        ISystemTrayService tray,
        IAutostartService autostart,
        IHotkeyService hotkeys,
        IScreenCaptureService capture,
        IAudioCaptureService audio,
        IDataProcessingService processing,
        IWebViewWindowManager windowManager,
        IUpdateService updates,
        IAmbientDeviceService ambientDevices,
        ILogger<EngineCoordinator> logger)
    {
        _settingsService = settingsService;
        _monitorDetection = monitorDetection;
        _tray = tray;
        _autostart = autostart;
        _hotkeys = hotkeys;
        _capture = capture;
        _audio = audio;
        _processing = processing;
        _windowManager = windowManager;
        _updates = updates;
        _ambientDevices = ambientDevices;
        _logger = logger;

        // Debounce timer; callback runs on the threadpool and only touches a cloned snapshot.
        _saveTimer = new System.Threading.Timer(static state => _ = ((EngineCoordinator)state!).SaveCoreAsync(),
            this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    /// <remarks>Must be called (and awaited) on the UI thread.</remarks>
    public async Task StartAsync(bool startMinimized)
    {
        _logger.LogInformation("Engine starting (minimized={Minimized}, version={Version})", startMinimized, AppVersion);

        _settings = await _settingsService.LoadAsync();
        _monitors = _monitorDetection.GetMonitors();

        // First run: seed the curated preset library (Story 7.2 AC6) so the preset
        // panel/tray aren't empty out of the box. Never re-seed once the user has data.
        if (!_settings.FirstRunCompleted && _settings.Presets.Count == 0)
        {
            lock (_gate) { _settings.Presets = DefaultPresets.Build(_settings); }
            _logger.LogInformation("Seeded {Count} default presets (first run)", _settings.Presets.Count);
            SaveNow();
        }

        await _windowManager.InitializeAsync();

        // Wire everything before any service starts producing events.
        _windowManager.CommandReceived += OnCommandReceived;
        _windowManager.ControlWindowCloseRequested += OnControlWindowCloseRequested;
        _windowManager.Error += OnPipelineError;
        _processing.FrameReady += OnFrameReady;
        _ambientDevices.StateChanged += OnAmbientDevicesStateChanged;
        _capture.Error += OnPipelineError;
        _audio.Error += OnPipelineError;
        _tray.ToggleEnabledRequested += OnTrayToggleRequested;
        _tray.PresetSelected += OnTrayPresetSelected;
        _tray.OpenSettingsRequested += OnOpenSettingsRequested;
        _tray.ExitRequested += OnTrayExitRequested;
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _monitorDetection.MonitorsChanged += OnMonitorsChanged;

        _tray.Initialize();
        UpdateTray();

        _audio.BandCount = _settings.AudioBands;
        _processing.UpdateOptions(OptionsFromSettings());

        var failed = _hotkeys.Apply(_settings.Hotkeys);
        if (failed.Count > 0)
        {
            Toast("warn", $"Some hotkeys could not be registered: {string.Join(", ", failed)}");
        }

        _monitorDetection.StartMonitoring();

        if (!startMinimized)
        {
            await _windowManager.ShowControlWindowAsync();
        }

        ApplyEnabledState();

        // Launch-time update check (Story 7.4 AC6): background, never blocks startup,
        // silent when up to date. Only meaningful in a Velopack-installed build.
        if (_updates.IsSupported)
        {
            _ = Task.Run(() => CheckForUpdatesSafeAsync(notifyWhenCurrent: false));
        }

        foreach (var m in _monitors)
        {
            _logger.LogInformation("Monitor: id={Id} name={Name} bounds={X},{Y} {Width}x{Height} primary={Primary}",
                m.Id, m.Name, m.X, m.Y, m.Width, m.Height, m.IsPrimary);
        }
        _logger.LogInformation("Engine started with {MonitorCount} monitor(s); enabled={Enabled}",
            _monitors.Count, _settings.IsEnabled);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Called on the UI thread from App.OnExit, which BLOCKS the dispatcher on this task.
    /// Everything dispatcher-bound therefore runs synchronously before the only await
    /// (the settings flush), which continues on the threadpool.
    /// </remarks>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownFlag, 1) == 1)
        {
            return;
        }

        _logger.LogInformation("Engine shutting down");

        // FlushSave: cancel the debounce and persist the current snapshot (awaited at the end).
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        var saveTask = SaveCoreAsync();

        try { _monitorDetection.StopMonitoring(); }
        catch (Exception ex) { _logger.LogError(ex, "Error stopping monitor detection"); }

        StopPipeline();

        try
        {
            // The dispatcher is blocked on us, so we must not await a dispatcher-bound task here.
            // A well-behaved manager closes windows synchronously when called on the UI thread;
            // Dispose below is the guaranteed cleanup either way.
            var syncTask = _windowManager.SyncEffectWindowsAsync(Array.Empty<EffectWindowSpec>());
            if (!syncTask.IsCompleted)
            {
                _logger.LogDebug("Effect-window sync still pending at shutdown; Dispose will close them");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing effect windows during shutdown");
        }

        try { _windowManager.Dispose(); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing the window manager"); }

        try { _tray.Dispose(); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing the tray"); }

        try { _saveTimer.Dispose(); } catch { /* best effort */ }

        await saveTask.ConfigureAwait(false);
        _logger.LogInformation("Engine shutdown complete");
    }

    // ---------------------------------------------------------------------
    // Pipeline state
    // ---------------------------------------------------------------------

    /// <summary>
    /// UI thread. Brings the whole pipeline in line with IsEnabled + SourceMonitorId:
    /// starts/stops capture, audio and processing, and syncs effect windows (FR13).
    /// </summary>
    private void ApplyEnabledState()
    {
        var source = ResolveSource();

        if (_settings.IsEnabled && source is not null)
        {
            _capture.Start(source); // switches monitors internally if already capturing
            _audio.Start();
            _processing.Start();
            ApplyAmbientDeviceState();
            _ = SyncEffectWindowsSafeAsync(BuildSpecs());
        }
        else if (_settings.IsEnabled) // enabled but no usable source
        {
            lock (_gate) { _settings.IsEnabled = false; }
            Toast("warn", string.IsNullOrEmpty(_settings.SourceMonitorId)
                ? "Select a source monitor first"
                : "Source monitor not found");
            PushConfig();
            StopPipeline();
            _ = SyncEffectWindowsSafeAsync(Array.Empty<EffectWindowSpec>());
        }
        else
        {
            StopPipeline();
            _ = SyncEffectWindowsSafeAsync(Array.Empty<EffectWindowSpec>());
        }

        UpdateTray();
    }

    private void StopPipeline()
    {
        try { _processing.Stop(); }
        catch (Exception ex) { _logger.LogError(ex, "Error stopping processing"); }
        try { _capture.Stop(); }
        catch (Exception ex) { _logger.LogError(ex, "Error stopping screen capture"); }
        try { _audio.Stop(); }
        catch (Exception ex) { _logger.LogError(ex, "Error stopping audio capture"); }
        try { _ambientDevices.Stop(); } // releases lighting control back to iCUE (Story 8.1 AC5)
        catch (Exception ex) { _logger.LogError(ex, "Error stopping ambient devices"); }
    }

    /// <summary>
    /// Brings the ambient peripheral sink in line with settings (Story 8.1): runs only while
    /// the pipeline runs AND the feature toggle is on. Never throws (NFR5).
    /// </summary>
    private void ApplyAmbientDeviceState()
    {
        bool run;
        float brightness;
        bool audioReactive;
        float audioDepth;
        Dictionary<string, DevicePlacement> placements;
        List<string> providers;
        lock (_gate)
        {
            run = _settings.IsEnabled && _settings.AmbientDevicesEnabled;
            brightness = _settings.PeripheralBrightness;
            audioReactive = _settings.AudioReactiveDevices;
            audioDepth = _settings.AudioReactiveDepth;
            // Private snapshots: the timer thread reads these while commands mutate the originals.
            placements = _settings.DevicePlacements.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
            providers = new List<string>(_settings.RgbProviders);
        }
        try
        {
            _ambientDevices.Brightness = brightness;
            _ambientDevices.AudioReactiveEnabled = audioReactive;
            _ambientDevices.AudioReactiveDepth = audioDepth;
            _ambientDevices.SetPlacements(placements);
            _ambientDevices.SetEnabledProviders(providers);
            if (run && ResolveSource() is not null)
            {
                _ambientDevices.Start();
            }
            else
            {
                _ambientDevices.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying ambient device state");
        }
    }

    private MonitorInfo? ResolveSource() =>
        _monitors.FirstOrDefault(m => m.Id == _settings.SourceMonitorId);

    /// <summary>One spec per existing, non-source target monitor.</summary>
    private IReadOnlyList<EffectWindowSpec> BuildSpecs()
    {
        var source = ResolveSource();
        var specs = new List<EffectWindowSpec>();
        foreach (var id in _settings.TargetMonitorIds)
        {
            if (id == _settings.SourceMonitorId)
            {
                continue;
            }
            var monitor = _monitors.FirstOrDefault(m => m.Id == id);
            if (monitor is null)
            {
                continue;
            }
            specs.Add(new EffectWindowSpec
            {
                Monitor = monitor,
                Config = BuildWindowConfigFor(monitor, source),
            });
        }
        return specs;
    }

    private WindowConfigPayload BuildWindowConfigFor(MonitorInfo monitor, MonitorInfo? source) => new()
    {
        MonitorId = monitor.Id,
        EffectId = EffectiveEffectId(monitor.Id),
        Monitor = monitor,
        Source = source,
        Relation = source is null ? "none" : MonitorLayout.ComputeRelation(source, monitor),
    };

    private string EffectiveEffectId(string monitorId) =>
        _settings.EffectByMonitorId.GetValueOrDefault(monitorId, _settings.ActiveEffectId);

    private async Task SyncEffectWindowsSafeAsync(IReadOnlyList<EffectWindowSpec> specs)
    {
        try
        {
            await _windowManager.SyncEffectWindowsAsync(specs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync effect windows");
        }
    }

    // ---------------------------------------------------------------------
    // Bridge commands (UI thread)
    // ---------------------------------------------------------------------

    private void OnCommandReceived(object? sender, BridgeCommandEventArgs e)
    {
        try
        {
            HandleCommand(e.Command, e.SourceWindow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge command {Type} from {Source} failed", e.Command.Type, e.SourceWindow);
        }
    }

    private void HandleCommand(CommandEnvelope envelope, string sourceWindow)
    {
        switch (envelope.Type)
        {
            case CommandTypes.SetEnabled:
            {
                if (envelope.PayloadAs<SetEnabledCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                SetEnabledCore(cmd.Enabled);
                break;
            }

            case CommandTypes.SetSourceMonitor:
            {
                if (envelope.PayloadAs<SetSourceMonitorCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                lock (_gate) { _settings.SourceMonitorId = cmd.MonitorId ?? string.Empty; }
                if (_settings.IsEnabled)
                {
                    ApplyEnabledState(); // capture.Start switches monitors; windows resync (relations changed)
                }
                PushWindowConfigs(); // re-push to every open effect window
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.SetTargetMonitors:
            {
                if (envelope.PayloadAs<SetTargetMonitorsCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                lock (_gate)
                {
                    _settings.TargetMonitorIds = (cmd.MonitorIds ?? new List<string>())
                        .Where(id => !string.IsNullOrEmpty(id) && id != _settings.SourceMonitorId)
                        .Distinct()
                        .ToList();
                }
                if (_settings.IsEnabled)
                {
                    _ = SyncEffectWindowsSafeAsync(BuildSpecs());
                }
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.SetEffect:
            {
                if (envelope.PayloadAs<SetEffectCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                bool global = string.IsNullOrEmpty(cmd.MonitorId) || cmd.MonitorId == "all";
                if (global)
                {
                    if (string.IsNullOrEmpty(cmd.EffectId)) { LogBadPayload(envelope); break; }
                    lock (_gate)
                    {
                        _settings.ActiveEffectId = cmd.EffectId;
                        _settings.EffectByMonitorId.Clear();
                    }
                    PushWindowConfigs(); // every open effect window is affected
                }
                else
                {
                    // Empty effectId clears the per-monitor override back to the global effect.
                    lock (_gate)
                    {
                        if (string.IsNullOrEmpty(cmd.EffectId)) _settings.EffectByMonitorId.Remove(cmd.MonitorId!);
                        else _settings.EffectByMonitorId[cmd.MonitorId!] = cmd.EffectId;
                    }
                    var monitor = _monitors.FirstOrDefault(m => m.Id == cmd.MonitorId);
                    if (monitor is not null)
                    {
                        _windowManager.PostToEffectWindow(cmd.MonitorId!, MessageTypes.WindowConfig,
                            BuildWindowConfigFor(monitor, ResolveSource()));
                    }
                }
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.SetEffectParams:
            {
                if (envelope.PayloadAs<SetEffectParamsCmd>() is not { } cmd || string.IsNullOrEmpty(cmd.EffectId))
                {
                    LogBadPayload(envelope);
                    break;
                }
                lock (_gate)
                {
                    if (!_settings.EffectParamsById.TryGetValue(cmd.EffectId, out var bag))
                    {
                        bag = new Dictionary<string, JsonElement>();
                        _settings.EffectParamsById[cmd.EffectId] = bag;
                    }
                    foreach (var (key, value) in cmd.Params ?? new Dictionary<string, JsonElement>())
                    {
                        // Bound hand-crafted payloads: param bags stay small (ParamDefs are
                        // single-digit counts); never let a rogue page grow settings.json unboundedly.
                        if (key.Length > 100) continue;
                        if (bag.Count >= 64 && !bag.ContainsKey(key)) continue;
                        bag[key] = value; // merge: overwrite existing keys, keep the rest
                    }
                }
                PushConfig(); // effect windows consume params from config
                ScheduleSave();
                break;
            }

            case CommandTypes.SetGlobal:
            {
                if (envelope.PayloadAs<SetGlobalCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                lock (_gate)
                {
                    if (cmd.Intensity is { } intensity) _settings.GlobalIntensity = Math.Clamp(intensity, 0f, 1f);
                    if (cmd.Smoothing is { } smoothing) _settings.Smoothing = Math.Clamp(smoothing, 0f, 1f);
                    if (cmd.Brightness is { } brightness) _settings.Brightness = Math.Clamp(brightness, 0f, 1f);
                    if (cmd.AudioSensitivity is { } sensitivity) _settings.AudioSensitivity = Math.Clamp(sensitivity, 0f, 1f);
                    if (cmd.MaxFps is { } maxFps) _settings.MaxFps = SnapFps(maxFps);
                }
                _processing.UpdateOptions(OptionsFromSettings());
                PushConfig();
                ScheduleSave();
                break;
            }

            case CommandTypes.SetDevices:
            {
                if (envelope.PayloadAs<SetDevicesCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                bool toggled = false;
                lock (_gate)
                {
                    if (cmd.Enabled is { } enabled)
                    {
                        toggled = _settings.AmbientDevicesEnabled != enabled;
                        _settings.AmbientDevicesEnabled = enabled;
                    }
                    if (cmd.Brightness is { } brightness)
                    {
                        _settings.PeripheralBrightness = Math.Clamp(brightness, 0f, 1f);
                    }
                    if (cmd.AudioReactive is { } audioReactive)
                    {
                        toggled |= _settings.AudioReactiveDevices != audioReactive;
                        _settings.AudioReactiveDevices = audioReactive;
                    }
                    if (cmd.AudioDepth is { } audioDepth)
                    {
                        _settings.AudioReactiveDepth = Math.Clamp(audioDepth, 0f, 1f);
                    }
                }
                ApplyAmbientDeviceState();
                PushConfig();
                if (toggled) SaveNow(); else ScheduleSave(); // sliders debounce, toggles save now
                break;
            }

            case CommandTypes.SetRgbProviders:
            {
                if (envelope.PayloadAs<SetRgbProvidersCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                lock (_gate)
                {
                    _settings.RgbProviders = (cmd.Providers ?? new List<string>())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct()
                        .Take(16)
                        .ToList();
                }
                // Provider set only takes effect on connect — force a fresh session (Story 8.3).
                try { _ambientDevices.Stop(); }
                catch (Exception ex) { _logger.LogError(ex, "Error stopping ambient devices for provider change"); }
                ApplyAmbientDeviceState();
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.SetDevicePlacement:
            {
                if (envelope.PayloadAs<SetDevicePlacementCmd>() is not { } cmd
                    || string.IsNullOrEmpty(cmd.DeviceId))
                {
                    LogBadPayload(envelope);
                    break;
                }
                // Anchor/flip/enable are discrete clicks; brightness alone is a slider drag.
                bool discrete = cmd.Anchor is not null || cmd.Flip is not null || cmd.Enabled is not null;
                lock (_gate)
                {
                    if (!_settings.DevicePlacements.TryGetValue(cmd.DeviceId, out var placement))
                    {
                        // Bounded like the effect-param bags: a rogue page can't grow settings.json.
                        if (cmd.DeviceId.Length > 200 || _settings.DevicePlacements.Count >= 128)
                        {
                            break;
                        }
                        placement = new DevicePlacement();
                        _settings.DevicePlacements[cmd.DeviceId] = placement;
                    }
                    if (cmd.Anchor is { } anchor && DeviceAnchors.IsValid(anchor)) placement.Anchor = anchor;
                    if (cmd.Flip is { } flip) placement.Flip = flip;
                    if (cmd.Brightness is { } brightness) placement.Brightness = Math.Clamp(brightness, 0f, 1f);
                    if (cmd.Enabled is { } enabled) placement.Enabled = enabled;
                    if (placement.IsDefault)
                    {
                        _settings.DevicePlacements.Remove(cmd.DeviceId); // back to Auto = no entry
                    }
                }
                ApplyAmbientDeviceState(); // pushes the new mapping live (no reconnect)
                PushConfig();
                if (discrete) SaveNow(); else ScheduleSave();
                break;
            }

            case CommandTypes.SavePreset:
            {
                if (envelope.PayloadAs<PresetCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                var name = (cmd.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    _logger.LogWarning("savePreset ignored: empty name");
                    break;
                }
                lock (_gate)
                {
                    var snapshot = _settings.Clone();
                    snapshot.Presets = new List<Preset>(); // never nest presets
                    snapshot.ActivePresetName = string.Empty;

                    var existing = _settings.Presets.FirstOrDefault(
                        p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        existing.Name = name;
                        existing.Snapshot = snapshot;
                    }
                    else
                    {
                        _settings.Presets.Add(new Preset { Name = name, Snapshot = snapshot });
                    }
                    _settings.ActivePresetName = name;
                }
                PushConfig();
                SaveNow();
                UpdateTray();
                break;
            }

            case CommandTypes.LoadPreset:
            {
                if (envelope.PayloadAs<PresetCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                LoadPreset(cmd.Name);
                break;
            }

            case CommandTypes.DeletePreset:
            {
                if (envelope.PayloadAs<PresetCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                lock (_gate)
                {
                    _settings.Presets.RemoveAll(
                        p => string.Equals(p.Name, cmd.Name, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(_settings.ActivePresetName, cmd.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.ActivePresetName = string.Empty;
                    }
                }
                PushConfig();
                SaveNow();
                UpdateTray();
                break;
            }

            case CommandTypes.SetAutostart:
            {
                if (envelope.PayloadAs<SetAutostartCmd>() is not { } cmd) { LogBadPayload(envelope); break; }
                try
                {
                    _autostart.SetEnabled(cmd.Enabled);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update the autostart registry entry");
                    Toast("error", "Could not update the start-with-Windows setting");
                }
                lock (_gate) { _settings.Autostart = _autostart.IsEnabled; } // reflect real state
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.SetHotkey:
            {
                if (envelope.PayloadAs<SetHotkeyCmd>() is not { } cmd || string.IsNullOrEmpty(cmd.Action))
                {
                    LogBadPayload(envelope);
                    break;
                }
                lock (_gate) { _settings.Hotkeys[cmd.Action] = cmd.Keys ?? string.Empty; }
                var failed = _hotkeys.Apply(_settings.Hotkeys);
                if (failed.Contains(cmd.Action))
                {
                    Toast("warn", "That key combination is unavailable");
                }
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.RequestState:
            {
                if (sourceWindow == "control")
                {
                    _windowManager.PostToControl(MessageTypes.Config, BuildConfig());
                    _windowManager.PostToControl(MessageTypes.Monitors, BuildMonitorsPayload());
                    _windowManager.PostToControl(MessageTypes.Devices, BuildDevicesPayload());
                }
                else
                {
                    _windowManager.PostToEffectWindow(sourceWindow, MessageTypes.Config, BuildConfig());
                    _windowManager.PostToEffectWindow(sourceWindow, MessageTypes.WindowConfig, BuildWindowConfigForId(sourceWindow));
                }
                break;
            }

            case CommandTypes.WindowCommand:
            {
                if (envelope.PayloadAs<WindowCommandCmd>() is not { } cmd || string.IsNullOrEmpty(cmd.Action))
                {
                    LogBadPayload(envelope);
                    break;
                }
                _windowManager.HandleControlWindowCommand(cmd.Action);
                break;
            }

            case CommandTypes.CompleteOnboarding:
            {
                lock (_gate) { _settings.FirstRunCompleted = true; }
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.QuitApp:
            {
                _logger.LogInformation("Quit requested from the control UI");
                RequestShutdown();
                break;
            }

            case CommandTypes.SetCloseAction:
            {
                if (envelope.PayloadAs<SetCloseActionCmd>() is not { } cmd || !CloseActions.IsValid(cmd.Action))
                {
                    LogBadPayload(envelope);
                    break;
                }
                lock (_gate) { _settings.CloseAction = cmd.Action; }
                PushConfig();
                SaveNow();
                break;
            }

            case CommandTypes.ResolveClosePrompt:
            {
                if (envelope.PayloadAs<ResolveClosePromptCmd>() is not { } cmd ||
                    cmd.Action is not (CloseActions.Quit or CloseActions.MinimizeToTray))
                {
                    LogBadPayload(envelope);
                    break;
                }
                if (cmd.Remember)
                {
                    lock (_gate) { _settings.CloseAction = cmd.Action; }
                    PushConfig();
                    SaveNow();
                }
                if (cmd.Action == CloseActions.Quit)
                {
                    _logger.LogInformation("Close prompt resolved: quit (remember={Remember})", cmd.Remember);
                    RequestShutdown();
                }
                else
                {
                    HideToTray();
                }
                break;
            }

            case CommandTypes.CheckForUpdates:
            {
                _ = Task.Run(() => CheckForUpdatesSafeAsync(notifyWhenCurrent: true));
                break;
            }

            case CommandTypes.ReportError:
            {
                if (envelope.PayloadAs<ReportErrorCmd>() is not { } cmd || string.IsNullOrEmpty(cmd.Message))
                {
                    LogBadPayload(envelope);
                    break;
                }
                _logger.LogError("Web layer reported an error ({Source} in {Window}): {Message}",
                    cmd.Source, sourceWindow, cmd.Message);
                string where = sourceWindow == "control"
                    ? "the control window"
                    : $"the effect window on {_monitors.FirstOrDefault(m => m.Id == sourceWindow)?.Name ?? "a target monitor"}";
                string detail = cmd.Message.Length > 200 ? cmd.Message[..200] + "…" : cmd.Message;
                Toast("error", $"A visual effect failed in {where}: {detail} Try another effect or toggle effects off and on.");
                break;
            }

            default:
                _logger.LogWarning("Unknown bridge command {Type} from {Source}", envelope.Type, sourceWindow);
                break;
        }
    }

    private void LogBadPayload(CommandEnvelope envelope) =>
        _logger.LogWarning("Bridge command {Type} ignored: malformed payload", envelope.Type);

    /// <summary>UI thread. Shared by the setEnabled command, the tray toggle and the hotkey.</summary>
    private void SetEnabledCore(bool enabled)
    {
        lock (_gate) { _settings.IsEnabled = enabled; }
        ApplyEnabledState();
        PushConfig();
        ScheduleSave();
    }

    /// <summary>UI thread. Shared by the loadPreset command, tray preset selection and the nextPreset hotkey.</summary>
    private void LoadPreset(string name)
    {
        Preset? preset;
        lock (_gate)
        {
            preset = _settings.Presets.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        if (preset is null)
        {
            Toast("warn", $"Preset \"{name}\" not found");
            return;
        }

        lock (_gate)
        {
            var applied = preset.Snapshot.Clone(); // never alias the stored snapshot
            applied.Presets = _settings.Presets;          // keep the existing preset list
            applied.FirstRunCompleted = _settings.FirstRunCompleted;
            applied.ActivePresetName = preset.Name;
            // Machine-agnostic presets (e.g. the shipped defaults) carry no monitor
            // selection — keep the user's monitors and enabled state instead of wiping them.
            if (string.IsNullOrEmpty(applied.SourceMonitorId))
            {
                applied.SourceMonitorId = _settings.SourceMonitorId;
                if (applied.TargetMonitorIds.Count == 0)
                {
                    applied.TargetMonitorIds = new List<string>(_settings.TargetMonitorIds);
                }
                applied.IsEnabled = _settings.IsEnabled;
                // Same grace for the desk layout (Story 8.2): a preset that knows nothing
                // about this machine's RGB devices must not wipe their placements.
                if (applied.DevicePlacements.Count == 0)
                {
                    applied.DevicePlacements = _settings.DevicePlacements
                        .ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
                }
            }
            _settings = applied;
        }

        _audio.BandCount = _settings.AudioBands;
        _processing.UpdateOptions(OptionsFromSettings());

        var failed = _hotkeys.Apply(_settings.Hotkeys);
        if (failed.Count > 0)
        {
            Toast("warn", $"Some hotkeys could not be registered: {string.Join(", ", failed)}");
        }

        if (_autostart.IsEnabled != _settings.Autostart)
        {
            try
            {
                _autostart.SetEnabled(_settings.Autostart);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync autostart while loading preset {Preset}", preset.Name);
                Toast("error", "Could not update the start-with-Windows setting");
            }
            lock (_gate) { _settings.Autostart = _autostart.IsEnabled; }
        }

        ApplyEnabledState(); // handles windows + capture restart
        PushWindowConfigs();
        PushConfig();
        SaveNow();
        UpdateTray();
        Toast("info", $"Preset \"{preset.Name}\" loaded");
    }

    // ---------------------------------------------------------------------
    // Engine -> web pushes
    // ---------------------------------------------------------------------

    private void PushConfig() => _windowManager.PostToAll(MessageTypes.Config, BuildConfig());

    private ConfigPayload BuildConfig()
    {
        ApplicationSettings snapshot;
        lock (_gate) { snapshot = _settings.Clone(); }
        return new ConfigPayload
        {
            Settings = snapshot,
            FirstRun = !snapshot.FirstRunCompleted,
            AppVersion = AppVersion,
        };
    }

    private MonitorsPayload BuildMonitorsPayload() => new() { Monitors = _monitors.ToList() };

    /// <summary>Re-posts windowConfig to every open effect window (no-op for closed ones).</summary>
    private void PushWindowConfigs()
    {
        var source = ResolveSource();
        foreach (var id in _settings.TargetMonitorIds)
        {
            if (id == _settings.SourceMonitorId)
            {
                continue;
            }
            var monitor = _monitors.FirstOrDefault(m => m.Id == id);
            if (monitor is null)
            {
                continue;
            }
            _windowManager.PostToEffectWindow(id, MessageTypes.WindowConfig, BuildWindowConfigFor(monitor, source));
        }
    }

    private WindowConfigPayload BuildWindowConfigForId(string monitorId)
    {
        var monitor = _monitors.FirstOrDefault(m => m.Id == monitorId);
        if (monitor is null)
        {
            _logger.LogWarning("requestState from effect window on unknown monitor {MonitorId}", monitorId);
            return new WindowConfigPayload
            {
                MonitorId = monitorId,
                EffectId = EffectiveEffectId(monitorId),
                Relation = "none",
            };
        }
        return BuildWindowConfigFor(monitor, ResolveSource());
    }

    /// <summary>Thread-safe: PostToControl marshals internally and the logger is thread-safe.</summary>
    private void Toast(string level, string message)
    {
        var logLevel = level switch
        {
            "error" => LogLevel.Error,
            "warn" => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        _logger.Log(logLevel, "Toast [{ToastLevel}]: {ToastMessage}", level, message);
        _windowManager.PostToControl(MessageTypes.Status, new StatusPayload { Level = level, Message = message });

        // A hidden (or impossible-to-create) control window would swallow the toast — fall
        // back to a tray balloon so status is never silently lost (FR12/NFR5). This also
        // gives hotkey/tray actions visible feedback while gaming.
        if (!_windowManager.IsControlWindowVisible)
        {
            _tray.ShowNotification("AmbientFx", message, level == "error");
        }
    }

    // ---------------------------------------------------------------------
    // Pipeline events (background threads)
    // ---------------------------------------------------------------------

    /// <summary>Background thread; PostToAll is thread-safe, so no marshaling (high frequency path).</summary>
    private void OnFrameReady(object? sender, FrameReadyEventArgs e)
    {
        // Second consumer of the same frame (Story 8.1; audio added in 8.3): non-blocking
        // latest-wins hand-off; the device service rate-limits its own hardware pushes.
        _ambientDevices.SubmitFrame(e.Frame);
        _windowManager.PostToAll(MessageTypes.Frame, e.Frame);
    }

    /// <summary>May fire on any thread; PostToControl marshals internally.</summary>
    private void OnAmbientDevicesStateChanged(object? sender, EventArgs e) =>
        _windowManager.PostToControl(MessageTypes.Devices, BuildDevicesPayload());

    private DevicesPayload BuildDevicesPayload()
    {
        var snapshot = _ambientDevices.Snapshot;
        return new DevicesPayload
        {
            ConnectionState = snapshot.ConnectionState,
            Devices = snapshot.Devices.ToList(),
            Providers = snapshot.Providers.ToList(),
        };
    }

    /// <summary>May fire on any thread; reaction is marshaled to the dispatcher (NFR5 — never crash).</summary>
    private void OnPipelineError(object? sender, PipelineErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Pipeline error from {Source}: {Message}", e.Source, e.Message);
        if (Volatile.Read(ref _shutdownFlag) == 1)
        {
            return;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }
        dispatcher.BeginInvoke(new Action(() => HandlePipelineErrorOnUi(e)));
    }

    /// <summary>UI thread.</summary>
    private void HandlePipelineErrorOnUi(PipelineErrorEventArgs e)
    {
        switch (e.Source)
        {
            case "capture":
                if (_settings.IsEnabled)
                {
                    lock (_gate) { _settings.IsEnabled = false; }
                    ApplyEnabledState();
                    Toast("error", "Screen capture failed — effects disabled");
                    PushConfig();
                }
                break;

            case "audio":
                // Keep running (visuals just lose audio reactivity); throttle toasts to avoid spam.
                if (DateTime.UtcNow - _lastAudioErrorToastUtc >= AudioErrorToastInterval)
                {
                    _lastAudioErrorToastUtc = DateTime.UtcNow;
                    Toast("warn", "Audio capture problem — effects may not react to sound");
                }
                break;

            default: // "hosting" and anything unexpected
                Toast("error", $"Window error: {e.Message}");
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Monitor topology (any thread -> dispatcher)
    // ---------------------------------------------------------------------

    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _shutdownFlag) == 1)
        {
            return;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(HandleMonitorsChangedOnUi));
    }

    /// <summary>UI thread.</summary>
    private void HandleMonitorsChangedOnUi()
    {
        if (Volatile.Read(ref _shutdownFlag) == 1)
        {
            return;
        }

        _monitors = _monitorDetection.GetMonitors();
        _logger.LogInformation("Monitor topology changed: {Count} monitor(s)", _monitors.Count);
        _windowManager.PostToControl(MessageTypes.Monitors, BuildMonitorsPayload());

        var sourceExists = ResolveSource() is not null;
        if (_settings.IsEnabled && !sourceExists)
        {
            lock (_gate) { _settings.IsEnabled = false; }
            ApplyEnabledState();
            Toast("error", "Source display disconnected — effects paused");
        }
        else if (_settings.IsEnabled)
        {
            // Targets may have come/gone; bounds may have moved.
            _ = SyncEffectWindowsSafeAsync(BuildSpecs());
            PushWindowConfigs();
        }

        PushConfig();
    }

    // ---------------------------------------------------------------------
    // Tray + hotkeys (UI thread)
    // ---------------------------------------------------------------------

    private void OnTrayToggleRequested(object? sender, EventArgs e) => SetEnabledCore(!_settings.IsEnabled);

    private void OnTrayPresetSelected(object? sender, string presetName) => LoadPreset(presetName);

    private void OnOpenSettingsRequested(object? sender, EventArgs e) => _ = ShowControlWindowSafeAsync();

    private void OnTrayExitRequested(object? sender, EventArgs e) => RequestShutdown();

    /// <summary>
    /// THE one shutdown path (Story 7.3 AC2/AC7): tray Exit, the in-app Quit command,
    /// close-with-CloseAction=Quit and the close prompt all funnel here. Marshals to the
    /// application's dispatcher; ShutdownAsync's Interlocked guard makes re-entry safe.
    /// </summary>
    private void RequestShutdown()
    {
        var requester = ShutdownRequester;
        if (requester is not null)
        {
            requester();
            return;
        }
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            _logger.LogWarning("Shutdown requested but no Application instance exists");
            return;
        }
        app.Dispatcher.InvokeAsync(app.Shutdown);
    }

    /// <summary>Test seam: replaces Application.Current.Shutdown() in unit tests.</summary>
    internal Action? ShutdownRequester { get; set; }

    // ---------------------------------------------------------------------
    // Auto-update (Story 7.4)
    // ---------------------------------------------------------------------

    /// <summary>Threadpool. Never throws; surfaces results as toasts.</summary>
    private async Task CheckForUpdatesSafeAsync(bool notifyWhenCurrent)
    {
        if (!_updates.IsSupported)
        {
            if (notifyWhenCurrent)
            {
                Toast("info", "Updates are only available in the installed version of AmbientFx");
            }
            return;
        }

        string feedUrl;
        lock (_gate) { feedUrl = _settings.UpdateFeedUrl; }

        try
        {
            var staged = await _updates.CheckAndStageAsync(feedUrl).ConfigureAwait(false);
            if (staged is not null)
            {
                Toast("info", $"AmbientFx {staged} downloaded — it will be applied the next time AmbientFx starts");
            }
            else if (notifyWhenCurrent)
            {
                Toast("info", "You're running the latest version");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            if (notifyWhenCurrent)
            {
                Toast("warn", "Could not check for updates — see the log for details");
            }
        }
    }

    private void OnHotkeyPressed(object? sender, string action)
    {
        switch (action)
        {
            case HotkeyActions.ToggleEnabled:
                SetEnabledCore(!_settings.IsEnabled);
                break;
            case HotkeyActions.OpenSettings:
                _ = ShowControlWindowSafeAsync();
                break;
            case HotkeyActions.NextPreset:
                CycleNextPreset();
                break;
            default:
                _logger.LogWarning("Unknown hotkey action {Action}", action);
                break;
        }
    }

    /// <summary>UI thread. Cycles presets in list order, wrapping; no-op when there are none.</summary>
    private void CycleNextPreset()
    {
        string? nextName = null;
        lock (_gate)
        {
            if (_settings.Presets.Count > 0)
            {
                var index = _settings.Presets.FindIndex(
                    p => string.Equals(p.Name, _settings.ActivePresetName, StringComparison.OrdinalIgnoreCase));
                nextName = _settings.Presets[(index + 1) % _settings.Presets.Count].Name; // -1 -> 0
            }
        }
        if (nextName is not null)
        {
            LoadPreset(nextName); // toasts "Preset <name> loaded"
        }
    }

    /// <summary>
    /// Close routing (Story 7.3 AC1): the window's close intercept asks us what to do.
    /// Quit really quits; MinimizeToTray hides as before; Ask defers to an in-page modal
    /// (the window stays open until the user answers via resolveClosePrompt).
    /// </summary>
    private void OnControlWindowCloseRequested(object? sender, EventArgs e)
    {
        string closeAction;
        lock (_gate) { closeAction = _settings.CloseAction; }

        switch (closeAction)
        {
            case CloseActions.Quit:
                _logger.LogInformation("Window close with CloseAction=quit — shutting down");
                RequestShutdown();
                break;
            case CloseActions.MinimizeToTray:
                HideToTray();
                break;
            default: // Ask
                _windowManager.PostToControl(MessageTypes.ClosePrompt, new { });
                break;
        }
    }

    private void HideToTray()
    {
        _windowManager.HideControlWindow();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Toast("info", "AmbientFx keeps running in the tray");
        }
    }

    private async Task ShowControlWindowSafeAsync()
    {
        try
        {
            await _windowManager.ShowControlWindowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the control window");
        }
    }

    // ---------------------------------------------------------------------
    // Settings persistence
    // ---------------------------------------------------------------------

    private ProcessingOptions OptionsFromSettings() => new()
    {
        ZonesPerEdge = _settings.ZonesPerEdge,
        Smoothing = _settings.Smoothing,
        AudioSensitivity = _settings.AudioSensitivity,
        MaxFps = _settings.MaxFps,
    };

    private static int SnapFps(int requested) =>
        AllowedFps.OrderBy(f => Math.Abs(f - requested)).ThenBy(f => f).First();

    /// <summary>Debounced save (600 ms) for high-frequency updates (sliders).</summary>
    private void ScheduleSave() => _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);

    /// <summary>Immediate save for discrete actions; cancels any pending debounce.</summary>
    private void SaveNow()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _ = SaveCoreAsync();
    }

    /// <summary>Threadpool. Clones under the gate, persists, never throws.</summary>
    private async Task SaveCoreAsync()
    {
        ApplicationSettings snapshot;
        lock (_gate) { snapshot = _settings.Clone(); }
        try
        {
            await _settingsService.SaveAsync(snapshot).ConfigureAwait(false);
            Interlocked.Exchange(ref _consecutiveSaveFailures, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            if (Interlocked.Increment(ref _consecutiveSaveFailures) == 2)
            {
                Toast("error", "Settings could not be saved");
            }
        }
    }

    private void UpdateTray()
    {
        List<string> names;
        string active;
        bool enabled;
        lock (_gate)
        {
            names = _settings.Presets.Select(p => p.Name).ToList();
            active = _settings.ActivePresetName;
            enabled = _settings.IsEnabled;
        }
        _tray.Update(enabled, names, active);
    }
}

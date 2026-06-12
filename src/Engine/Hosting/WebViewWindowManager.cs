using System.IO;
using System.Windows;
using System.Windows.Threading;
using AmbientFx.Bridge;
using AmbientFx.Capture;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Application = System.Windows.Application;

namespace AmbientFx.Hosting;

/// <summary>
/// Owns every WebView2-hosted window: the lazily created (and reused) control window plus
/// one fullscreen <see cref="EffectWindow"/> per target monitor, all sharing a single
/// CoreWebView2Environment (one browser + one GPU process for the whole app).
/// Singleton; resolved from DI. All members are UI-thread affine EXCEPT the Post*
/// methods, which are safe from any thread: they serialize on the caller's thread and
/// marshal to the dispatcher with latest-only coalescing for the 60fps frame stream.
/// Failures degrade gracefully via <see cref="Error"/> + logs — never crash the host (NFR5).
/// </summary>
public sealed class WebViewWindowManager : IWebViewWindowManager
{
    private readonly ILogger<WebViewWindowManager> _logger;

    /// <summary>Guards <see cref="_effectWindows"/> (mutated on the UI thread, read from
    /// any thread by the Post* hot path).</summary>
    private readonly object _gate = new();

    private readonly Dictionary<string, EffectEntry> _effectWindows = new();
    private readonly PostTarget _controlTarget;

    private CoreWebView2Environment? _environment;
    private Dispatcher? _dispatcher;
    private ControlWindow? _controlWindow;
    private volatile bool _disposed;

    /// <summary>Thread-safe mirror of the control window's visibility (WPF's IsVisible is
    /// UI-thread affine; the frame hot path reads this from background threads). A hidden
    /// (tray-closed) control window is skipped for 60fps frame posts — NFR1.</summary>
    private volatile bool _controlWindowFrameTarget;

    // Sync serialization (all touched on the UI thread only): WebView2 init awaits inside a
    // sync pump the dispatcher, so unserialized calls could interleave — resurrecting a
    // deselected monitor's window, or orphaning a live topmost window whose dictionary entry
    // a stale catch block evicted. Each call supersedes queued ones; only the latest
    // requested state runs once in-flight work finishes.
    private Task _syncChain = Task.CompletedTask;
    private int _syncVersion;
    private IReadOnlyList<EffectWindowSpec> _latestSyncSpecs = Array.Empty<EffectWindowSpec>();

    /// <inheritdoc />
    public event EventHandler<BridgeCommandEventArgs>? CommandReceived;

    /// <inheritdoc />
    public event EventHandler? ControlWindowCloseRequested;

    /// <inheritdoc />
    public event EventHandler<PipelineErrorEventArgs>? Error;

    /// <summary>Creates the manager. Resolve as a singleton; call <see cref="InitializeAsync"/>
    /// once on the UI thread before showing any window.</summary>
    public WebViewWindowManager(ILogger<WebViewWindowManager> logger)
    {
        _logger = logger;
        _controlTarget = new PostTarget(json => _controlWindow?.TryPostWebMessage(json));
    }

    /// <inheritdoc />
    /// <remarks>Backed by a volatile mirror of the window's visibility so it is safe to read
    /// from any thread (WPF's IsVisible is UI-thread affine).</remarks>
    public bool IsControlWindowVisible => _controlWindowFrameTarget;

    /// <summary>
    /// Creates the shared CoreWebView2Environment (idempotent). Call once at startup, on
    /// the UI thread, before any window is shown. A missing WebView2 runtime surfaces via
    /// <see cref="Error"/> — the app keeps running headless (tray) instead of crashing.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposed || _environment is not null) return;

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // Must be set BEFORE any WebView2 object is created: prevents the white flash
        // while the controller initializes (AARRGGBB; only alpha 00/FF is valid).
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF000000");

        try
        {
            string userDataFolder = WebViewHelpers.UserDataFolder;
            Directory.CreateDirectory(userDataFolder);
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder);
            _logger.LogInformation(
                "WebView2 environment ready (runtime {Version}, user data folder {Folder})",
                _environment.BrowserVersionString, userDataFolder);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            RaiseError(
                "The Microsoft WebView2 Runtime is not installed. AmbientFx needs it to display its UI and effects — " +
                "install it from https://developer.microsoft.com/microsoft-edge/webview2/ and restart the app.",
                ex);
        }
        catch (Exception ex)
        {
            RaiseError($"Failed to initialize the WebView2 environment: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task ShowControlWindowAsync()
    {
        if (_disposed) return;
        if (_environment is null)
        {
            RaiseError("Cannot show the control window: the WebView2 environment is unavailable.");
            return;
        }

        if (_controlWindow is not null)
        {
            _controlWindow.ShowAndActivate();
            return;
        }

        var window = new ControlWindow(_logger);
        window.CloseRequested += OnControlWindowCloseRequested;
        window.BridgeMessageReceived += OnControlBridgeMessage;
        window.IsVisibleChanged += (_, _) => _controlWindowFrameTarget = window.IsVisible;
        window.StateChanged += (_, _) => PostToControl(MessageTypes.WindowState, new WindowStatePayload
        {
            State = window.WindowState switch
            {
                WindowState.Maximized => "maximized",
                WindowState.Minimized => "minimized",
                _ => "normal",
            },
        });
        _controlWindow = window;

        try
        {
            window.Show();
            await window.InitializeWebViewAsync(_environment);
        }
        catch (Exception ex)
        {
            _controlWindow = null;
            try { window.ForceClose(); } catch { /* best effort */ }
            RaiseError($"Failed to open the control window: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void HideControlWindow()
    {
        if (_disposed) return;
        _controlWindow?.Hide();
    }

    /// <inheritdoc />
    public Task SyncEffectWindowsAsync(IReadOnlyList<EffectWindowSpec> specs)
    {
        if (_disposed) return Task.CompletedTask;

        int version = ++_syncVersion;
        _latestSyncSpecs = specs;
        Task previous = _syncChain;
        _syncChain = Run();
        return _syncChain;

        async Task Run()
        {
            try { await previous; } catch { /* that run already logged its failure */ }
            if (_disposed || version != _syncVersion) return; // superseded by a newer request
            await SyncCoreAsync(_latestSyncSpecs);
        }
    }

    private async Task SyncCoreAsync(IReadOnlyList<EffectWindowSpec> specs)
    {
        if (_disposed) return;

        // Desired state by monitor id (last spec wins on duplicates).
        var desired = new Dictionary<string, EffectWindowSpec>(StringComparer.Ordinal);
        foreach (EffectWindowSpec spec in specs)
        {
            if (string.IsNullOrEmpty(spec.Monitor.Id))
            {
                _logger.LogWarning("Ignoring effect window spec with an empty monitor id");
                continue;
            }
            desired[spec.Monitor.Id] = spec;
        }

        if (desired.Count > 0 && _environment is null)
        {
            RaiseError("Cannot open effect windows: the WebView2 environment is unavailable.");
            return;
        }

        // Close removed windows first (FR13 — the monitor is fully released).
        List<string> removed;
        lock (_gate)
        {
            removed = _effectWindows.Keys.Where(id => !desired.ContainsKey(id)).ToList();
        }
        foreach (string id in removed)
        {
            CloseEffectWindow(id);
        }

        // Keep existing (refresh placement + assignment), create the rest.
        foreach ((string id, EffectWindowSpec spec) in desired)
        {
            string configJson = BridgeJson.Serialize(
                new OutboundEnvelope<WindowConfigPayload>(MessageTypes.WindowConfig, spec.Config));

            EffectEntry? entry;
            lock (_gate)
            {
                _effectWindows.TryGetValue(id, out entry);
            }

            if (entry is not null)
            {
                entry.Window.RepositionTo(spec.Monitor);
                entry.ConfigJson = configJson;
                entry.Window.TryPostWebMessage(configJson); // re-push the assignment to the live page
                continue;
            }

            var window = new EffectWindow(spec.Monitor, _logger);
            entry = new EffectEntry
            {
                Window = window,
                Target = new PostTarget(json => window.TryPostWebMessage(json)),
                ConfigJson = configJson,
            };
            window.BridgeMessageReceived += (_, json) => DispatchBridgeMessage(id, json);
            window.PageReady += (_, _) => OnEffectPageReady(id);
            lock (_gate)
            {
                _effectWindows[id] = entry;
            }

            try
            {
                window.Show();
                await window.InitializeWebViewAsync(_environment!);
                _logger.LogInformation("Effect window created on {MonitorId} ({Name})", id, spec.Monitor.Name);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    // Identity check (defense-in-depth alongside sync serialization): only
                    // evict the entry THIS call created — never one a newer sync installed.
                    if (_effectWindows.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
                    {
                        _effectWindows.Remove(id);
                    }
                }
                try { window.Close(); } catch { /* best effort */ }
                RaiseError($"Failed to open the effect window on '{spec.Monitor.Name}': {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc />
    public void PostToAll(string type, object payload)
    {
        if (_disposed) return;
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null) return;

        string json = SerializeEnvelope(type, payload);

        if (type == MessageTypes.Frame)
        {
            if (_controlWindowFrameTarget) _controlTarget.Post(json, dispatcher);
            foreach (EffectEntry entry in SnapshotEffectEntries())
            {
                entry.Target.Post(json, dispatcher);
            }
        }
        else
        {
            // Low-frequency messages (status/config/monitors) must not be coalesced away.
            dispatcher.InvokeAsync(() =>
            {
                _controlWindow?.TryPostWebMessage(json);
                foreach (EffectEntry entry in SnapshotEffectEntries())
                {
                    entry.Window.TryPostWebMessage(json);
                }
            });
        }
    }

    /// <inheritdoc />
    public void PostToControl(string type, object payload)
    {
        if (_disposed) return;
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null) return;

        string json = SerializeEnvelope(type, payload);

        if (type == MessageTypes.Frame)
        {
            if (_controlWindowFrameTarget) _controlTarget.Post(json, dispatcher);
        }
        else
        {
            dispatcher.InvokeAsync(() => _controlWindow?.TryPostWebMessage(json));
        }
    }

    /// <inheritdoc />
    public void PostToEffectWindow(string monitorId, string type, object payload)
    {
        if (_disposed) return;
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null) return;

        EffectEntry? entry;
        lock (_gate)
        {
            _effectWindows.TryGetValue(monitorId, out entry);
        }
        if (entry is null) return;

        string json = SerializeEnvelope(type, payload);

        if (type == MessageTypes.Frame)
        {
            entry.Target.Post(json, dispatcher);
        }
        else
        {
            if (type == MessageTypes.WindowConfig)
            {
                // Keep the stored assignment current: it is replayed on PageReady after a
                // render-process recovery, and a stale one would resurrect the old effect.
                entry.ConfigJson = json;
            }
            dispatcher.InvokeAsync(() => entry.Window.TryPostWebMessage(json));
        }
    }

    /// <inheritdoc />
    public void HandleControlWindowCommand(string action)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
        {
            HandleControlWindowCommandCore(action);
        }
        else
        {
            dispatcher.InvokeAsync(() => HandleControlWindowCommandCore(action));
        }
    }

    /// <summary>
    /// Closes every hosted window (effect windows, then the control window). The shared
    /// browser process exits on its own once the last webview is released. Safe to call
    /// from any thread; marshals to the dispatcher. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Dispatcher? dispatcher = _dispatcher;
        try
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(CloseAllWindows);
            }
            else
            {
                CloseAllWindows();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing WebView2 windows during dispose failed");
        }
        _environment = null;
    }

    // ---------------------------------------------------------------- internals

    private void HandleControlWindowCommandCore(string action)
    {
        if (_disposed) return;
        ControlWindow? window = _controlWindow;
        switch (action?.ToLowerInvariant())
        {
            case "minimize":
                if (window is not null) window.WindowState = WindowState.Minimized;
                break;
            case "maximize":
                if (window is not null)
                {
                    window.WindowState = window.WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                }
                break;
            case "restore":
                if (window is not null) window.WindowState = WindowState.Normal;
                break;
            case "close":
                ControlWindowCloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                _logger.LogWarning("Unknown control window command: {Action}", action);
                break;
        }
    }

    private void OnControlWindowCloseRequested(object? sender, EventArgs e) =>
        ControlWindowCloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnControlBridgeMessage(object? sender, string json) =>
        DispatchBridgeMessage(WebViewHelpers.ControlSource, json);

    /// <summary>
    /// Parses a raw web message and raises <see cref="CommandReceived"/>. The raise is
    /// deferred via the dispatcher so subscribers never run inside the WebView2
    /// WebMessageReceived callback — handlers may close/dispose webviews (e.g.
    /// setEnabled{false} → SyncEffectWindowsAsync([])), which is banned reentrantly.
    /// </summary>
    private void DispatchBridgeMessage(string sourceWindow, string json)
    {
        if (_disposed) return;

        CommandEnvelope? command = CommandParser.Parse(json);
        if (command is null)
        {
            _logger.LogWarning(
                "Ignoring malformed bridge message from {Source}: {Json}", sourceWindow, Truncate(json));
            return;
        }

        _dispatcher?.InvokeAsync(() =>
        {
            if (_disposed) return;
            try
            {
                CommandReceived?.Invoke(this, new BridgeCommandEventArgs
                {
                    Command = command,
                    SourceWindow = sourceWindow,
                });
            }
            catch (Exception ex)
            {
                // A misbehaving subscriber must not take down the dispatcher (NFR5).
                _logger.LogError(ex, "Unhandled exception in a CommandReceived handler for '{Type}'", command.Type);
            }
        });
    }

    /// <summary>UI thread (WebView2 NavigationCompleted). Pushes the windowConfig envelope
    /// to the freshly loaded page — messages posted before load die with the old document.</summary>
    private void OnEffectPageReady(string monitorId)
    {
        EffectEntry? entry;
        lock (_gate)
        {
            _effectWindows.TryGetValue(monitorId, out entry);
        }
        entry?.Window.TryPostWebMessage(entry.ConfigJson);
    }

    /// <summary>UI thread only. Removing + closing disposes the webview via the window's
    /// OnClosed; safe here because CommandReceived dispatch is already deferred out of
    /// WebView2 callbacks.</summary>
    private void CloseEffectWindow(string monitorId)
    {
        EffectEntry? entry;
        lock (_gate)
        {
            if (_effectWindows.TryGetValue(monitorId, out entry))
            {
                _effectWindows.Remove(monitorId);
            }
        }
        if (entry is null) return;

        _logger.LogInformation("Closing effect window on {MonitorId}", monitorId);
        try
        {
            entry.Window.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the effect window on {MonitorId} failed", monitorId);
        }
    }

    private void CloseAllWindows()
    {
        List<EffectEntry> entries;
        lock (_gate)
        {
            entries = _effectWindows.Values.ToList();
            _effectWindows.Clear();
        }
        foreach (EffectEntry entry in entries)
        {
            try { entry.Window.Close(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing an effect window during dispose failed"); }
        }

        ControlWindow? control = _controlWindow;
        _controlWindow = null;
        if (control is not null)
        {
            try { control.ForceClose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing the control window during dispose failed"); }
        }

        _logger.LogInformation(
            "All WebView2 windows closed; the shared browser process exits once the last webview is released");
    }

    private EffectEntry[] SnapshotEffectEntries()
    {
        lock (_gate)
        {
            return _effectWindows.Values.ToArray();
        }
    }

    /// <summary>Serialized once per send on the caller's thread — the dispatcher only posts.</summary>
    private static string SerializeEnvelope(string type, object payload) =>
        BridgeJson.Serialize(new OutboundEnvelope<object>(type, payload));

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";

    private void RaiseError(string message, Exception? exception = null)
    {
        _logger.LogError(exception, "Hosting error: {Message}", message);
        try
        {
            Error?.Invoke(this, new PipelineErrorEventArgs
            {
                Source = "hosting",
                Message = message,
                Exception = exception,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in an Error handler");
        }
    }

    /// <summary>An effect window plus its coalescing gate and latest windowConfig envelope.</summary>
    private sealed class EffectEntry
    {
        public required EffectWindow Window { get; init; }
        public required PostTarget Target { get; init; }

        /// <summary>Latest serialized windowConfig envelope; UI thread only.</summary>
        public string ConfigJson = string.Empty;
    }

    /// <summary>
    /// Latest-only coalescing gate for the 60fps frame stream: at most one dispatcher
    /// operation in flight per window; a newer frame simply overwrites the pending one, so
    /// a busy dispatcher never builds a queue and always posts the freshest data.
    /// </summary>
    private sealed class PostTarget
    {
        private readonly Action<string> _send; // runs on the dispatcher thread; must not throw
        private string? _pending;
        private int _scheduled; // 0 = idle, 1 = a drain is queued/running

        public PostTarget(Action<string> send) => _send = send;

        /// <summary>Publishes <paramref name="json"/> as the latest payload. Any thread.</summary>
        public void Post(string json, Dispatcher dispatcher)
        {
            Interlocked.Exchange(ref _pending, json);
            Schedule(dispatcher);
        }

        private void Schedule(Dispatcher dispatcher)
        {
            if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;
            try
            {
                dispatcher.InvokeAsync(() => Drain(dispatcher));
            }
            catch
            {
                // Dispatcher shutting down — drop the frame, allow future attempts.
                Volatile.Write(ref _scheduled, 0);
            }
        }

        private void Drain(Dispatcher dispatcher)
        {
            string? json = Interlocked.Exchange(ref _pending, null);
            Volatile.Write(ref _scheduled, 0);
            if (json is not null)
            {
                try { _send(json); }
                catch { /* senders are no-throw; belt and braces */ }
            }
            // A producer may have published after our exchange but before the reset above,
            // in which case its Schedule lost the CAS — pick the frame up ourselves.
            if (Volatile.Read(ref _pending) is not null)
            {
                Schedule(dispatcher);
            }
        }
    }
}

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Brushes = System.Windows.Media.Brushes;

namespace AmbientFx.Hosting;

/// <summary>
/// One borderless, topmost, fullscreen effect surface per target monitor (FR5): a WPF
/// window hosting a locked-down WebView2 that renders the WebGL effect runtime.
/// Placement happens in device pixels via SetWindowPos in SourceInitialized (never WPF
/// Left/Top DIPs, never WindowState.Maximized — mixed-DPI trap, research note §7).
/// The window never takes focus or appears in Alt-Tab/taskbar.
/// Pure code-behind — no XAML. All members are UI-thread affine.
/// </summary>
internal sealed class EffectWindow : Window
{
    private readonly ILogger _logger;
    private readonly WebView2 _webView;
    private MonitorInfo _monitor;
    private bool _hwndReady;
    private bool _webViewDisposed;
    private bool _postFailureLogged;

    /// <summary>The monitor this window was created for. Stable for the window's lifetime.</summary>
    public string MonitorId { get; }

    /// <summary>Raw bridge JSON received from the hosted page. Fires on the UI thread,
    /// from inside the WebView2 WebMessageReceived callback — subscribers must not
    /// dispose webviews or pump nested message loops synchronously.</summary>
    public event EventHandler<string>? BridgeMessageReceived;

    /// <summary>Raised on the UI thread when the effect page finished loading and is able
    /// to receive web messages (messages posted before this are lost with the old document).</summary>
    public event EventHandler? PageReady;

    public EffectWindow(MonitorInfo monitor, ILogger logger)
    {
        _monitor = monitor;
        _logger = logger;
        MonitorId = monitor.Id;

        Title = $"AmbientFx — {monitor.Name}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        // Must stay false: WebView2 (HwndHost) does not render inside layered windows.
        AllowsTransparency = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Black,
            Focusable = false,
        };
        Content = _webView;

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Initializes the hosted webview against the shared environment and navigates to the
    /// effect runtime for this monitor. Call once, after Show(). Order is mandatory:
    /// EnsureCoreWebView2Async(sharedEnv) → kiosk settings → virtual-host mapping → Navigate.
    /// </summary>
    public async Task InitializeWebViewAsync(CoreWebView2Environment environment)
    {
        await _webView.EnsureCoreWebView2Async(environment);

        CoreWebView2 core = _webView.CoreWebView2;
        CoreWebView2Settings settings = core.Settings;

        // Kiosk lockdown (research note §6): the monitor is dedicated to the effect.
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        // IsWebMessageEnabled and IsScriptEnabled stay true (defaults).

        WebViewHelpers.MapVirtualHost(core);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;

        string url = WebViewHelpers.EffectUrl(MonitorId);
        core.Navigate(url);
        _logger.LogInformation("Effect window on {MonitorId} navigating to {Url}", MonitorId, url);
    }

    /// <summary>
    /// Re-applies device-pixel placement after a display-topology change. No-op until the
    /// HWND exists or when the bounds are unchanged. UI thread only.
    /// </summary>
    public void RepositionTo(MonitorInfo monitor)
    {
        bool boundsChanged =
            monitor.X != _monitor.X || monitor.Y != _monitor.Y ||
            monitor.Width != _monitor.Width || monitor.Height != _monitor.Height;
        _monitor = monitor;
        if (!_hwndReady || !boundsChanged) return;

        ApplyMonitorBounds(new WindowInteropHelper(this).Handle);
        _logger.LogInformation(
            "Effect window on {MonitorId} repositioned to {X},{Y} {Width}x{Height} (device px)",
            MonitorId, monitor.X, monitor.Y, monitor.Width, monitor.Height);
    }

    /// <summary>Posts pre-serialized envelope JSON to the page. UI thread only. Never throws;
    /// returns false when the webview is not yet initialized or already disposed.</summary>
    public bool TryPostWebMessage(string json)
    {
        if (_webViewDisposed) return false;
        CoreWebView2? core = _webView.CoreWebView2;
        if (core is null) return false;
        try
        {
            core.PostWebMessageAsJson(json);
            return true;
        }
        catch (Exception ex)
        {
            if (!_postFailureLogged)
            {
                _postFailureLogged = true;
                _logger.LogWarning(ex,
                    "Posting to the effect window webview on {MonitorId} failed; further failures suppressed",
                    MonitorId);
            }
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Dispose the WebView2 control with its window (research note §8). This is a WPF
        // window event, not a WebView2 event handler, so a synchronous Dispose is safe.
        if (!_webViewDisposed)
        {
            _webViewDisposed = true;
            try
            {
                _webView.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing the effect window webview on {MonitorId} failed", MonitorId);
            }
        }
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // Out of Alt-Tab, never activates — the effect must not steal focus from the game.
        NativeMethods.AddExtendedStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        _hwndReady = true;
        ApplyMonitorBounds(hwnd);
    }

    private void ApplyMonitorBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        // DEVICE pixels straight to the target monitor: PerMonitorV2 assigns the right DPI
        // once the window lands there and WPF lays out from the final pixel rect. A
        // borderless window exactly matching the monitor rect also gets DWM's
        // fullscreen-optimization (independent flip).
        if (!NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED))
        {
            _logger.LogWarning("SetWindowPos failed for the effect window on {MonitorId}", MonitorId);
        }
    }

    /// <summary>Fires on the UI thread from the WebView2 callback; forwards the raw JSON.</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Effect window web message on {MonitorId} could not be read as JSON", MonitorId);
            return;
        }
        BridgeMessageReceived?.Invoke(this, json);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            PageReady?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _logger.LogWarning(
                "Effect page navigation failed on {MonitorId}: {Status}", MonitorId, e.WebErrorStatus);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning(
            "Effect window WebView2 process failure on {MonitorId}: {Kind} (exit code {ExitCode})",
            MonitorId, e.ProcessFailedKind, e.ExitCode);

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            // Recoverable: reload outside the WebView2 callback (no reentrant calls).
            Dispatcher.InvokeAsync(() =>
            {
                if (_webViewDisposed) return;
                try
                {
                    _webView.CoreWebView2?.Reload();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Effect window reload on {MonitorId} after render-process exit failed", MonitorId);
                }
            });
        }
    }
}

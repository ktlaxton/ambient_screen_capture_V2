using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Brushes = System.Windows.Media.Brushes;

namespace AmbientFx.Hosting;

/// <summary>
/// The single settings/control window (FR8): a borderless WPF shell (WindowChrome supplies
/// the resize borders) hosting one WebView2 that renders the entire control UI. The web
/// page supplies the caption via CSS app-region:drag, enabled through
/// CoreWebView2Settings.IsNonClientRegionSupportEnabled. Closing is intercepted and
/// surfaced as <see cref="CloseRequested"/> so the coordinator can hide to tray (FR10);
/// real shutdown goes through <see cref="ForceClose"/>.
/// Pure code-behind — no XAML. All members are UI-thread affine.
/// </summary>
internal sealed class ControlWindow : Window
{
    /// <summary>Matches WindowChrome.ResizeBorderThickness; the webview is inset by this
    /// in the Normal state so the resize edges stay WPF-owned (the WebView2 child HWND
    /// swallows mouse input — research note §5 caveat).</summary>
    private const double ResizeBorder = 6.0;

    private readonly ILogger _logger;
    private readonly WebView2 _webView;
    private bool _forceClosing;
    private bool _webViewDisposed;
    private bool _postFailureLogged;

    /// <summary>Raised instead of closing when the user closes the window. The owner
    /// decides what to do (hide to tray); the window never closes itself.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raw bridge JSON received from the hosted page. Fires on the UI thread,
    /// from inside the WebView2 WebMessageReceived callback — subscribers must not
    /// dispose webviews or pump nested message loops synchronously.</summary>
    public event EventHandler<string>? BridgeMessageReceived;

    public ControlWindow(ILogger logger)
    {
        _logger = logger;

        Title = "AmbientFx";
        Width = 1280;
        Height = 800;
        MinWidth = 980;
        MinHeight = 640;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        // Must stay false: WebView2 (HwndHost) does not render inside layered windows.
        AllowsTransparency = false;
        Background = Brushes.Black;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, // the caption is HTML with CSS app-region:drag
            ResizeBorderThickness = new Thickness(ResizeBorder),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Black,
            Margin = new Thickness(ResizeBorder),
        };
        Content = _webView;

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Initializes the hosted webview against the shared environment and navigates to the
    /// control UI. Call once, after Show(). Order is mandatory (research note §2/§5):
    /// EnsureCoreWebView2Async(sharedEnv) → settings → virtual-host mapping → Navigate.
    /// Never set Source before Ensure — implicit init with a default environment would
    /// make the shared-environment Ensure throw ArgumentException.
    /// </summary>
    public async Task InitializeWebViewAsync(CoreWebView2Environment environment)
    {
        await _webView.EnsureCoreWebView2Async(environment);

        CoreWebView2 core = _webView.CoreWebView2;
        CoreWebView2Settings settings = core.Settings;

        bool devTooling = Debugger.IsAttached;
        // Takes effect on the NEXT navigation — must be set before the first Navigate.
        settings.IsNonClientRegionSupportEnabled = true;
        settings.AreDefaultContextMenusEnabled = devTooling;
        settings.AreDevToolsEnabled = devTooling;
        settings.AreBrowserAcceleratorKeysEnabled = devTooling;
        settings.IsStatusBarEnabled = false;

        WebViewHelpers.MapVirtualHost(core);

        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        core.Navigate(WebViewHelpers.ControlUrl);
        _logger.LogInformation("Control window navigating to {Url}", WebViewHelpers.ControlUrl);
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
                _logger.LogWarning(ex, "Posting to the control window webview failed; further failures suppressed");
            }
            return false;
        }
    }

    /// <summary>Shows the window, restores it if minimized, and brings it to the foreground.</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>Really closes the window (app shutdown), bypassing the hide-to-tray intercept.</summary>
    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    /// <summary>User-initiated close (Alt+F4 / system menu) becomes <see cref="CloseRequested"/>;
    /// the window only really closes via <see cref="ForceClose"/>.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClosing || e.Cancel) return;
        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
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
                _logger.LogWarning(ex, "Disposing the control window webview failed");
            }
        }
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Maximized: no resize edges needed, let the web content reach every pixel.
        _webView.Margin = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(ResizeBorder);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            // A WindowStyle=None + WindowChrome window over-expands past the work area when
            // maximized (covers the taskbar); clamp to the current monitor's work area and
            // re-apply MinWidth/MinHeight in device pixels (handling this message bypasses
            // WPF's own min-size translation).
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            NativeMethods.ClampMaximizedToWorkArea(
                hwnd, lParam, MinWidth * dpi.DpiScaleX, MinHeight * dpi.DpiScaleY);
            handled = true;
        }
        return IntPtr.Zero;
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
            _logger.LogDebug(ex, "Control window web message could not be read as JSON");
            return;
        }
        BridgeMessageReceived?.Invoke(this, json);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning(
            "Control window WebView2 process failure: {Kind} (exit code {ExitCode})",
            e.ProcessFailedKind, e.ExitCode);

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
                    _logger.LogError(ex, "Control window reload after render-process exit failed");
                }
            });
        }
    }
}

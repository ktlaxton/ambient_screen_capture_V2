#if SIMULATOR_ENABLED
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmbientFx.Hosting;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
// Disambiguate WPF types from the global WinForms/System.Drawing usings (UseWindowsForms=true).
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 Layout Simulator, Story 10.2). One effect viewport inside the composite
/// <see cref="SimulatorWindow"/>: a child <see cref="WebView2"/> running the <b>real</b> effect runtime
/// (the same <c>effects.html</c> + <c>monitorProjection.ts</c> the production <see cref="EffectWindow"/>
/// loads), with the monitor's synthetic source content drawn behind it. Implements
/// <see cref="IEffectSurfaceHost"/> so the unmodified <see cref="WebViewWindowManager"/> drives it
/// exactly like a real <see cref="EffectWindow"/> — the only difference is placement (a scaled
/// <see cref="Canvas"/> rect instead of <c>SetWindowPos</c> in device pixels). Compiled out of Release.
/// </summary>
/// <remarks>
/// Hosted as a <see cref="Grid"/> (a panel that directly hosts the background image + WebView2) rather
/// than a templated <see cref="Control"/>; the base type is not load-bearing — the
/// <see cref="IEffectSurfaceHost"/> contract is. Known fidelity caveat: WebView2 uses windowed hosting,
/// so whether the synthetic background shows through depends on the effect leaving its canvas
/// transparent; the effect (the thing under test) always renders correctly on top regardless.
/// </remarks>
public sealed class SimulatorEffectSurface : Grid, IEffectSurfaceHost
{
    private readonly ILogger _logger;
    private readonly WebView2 _webView;
    private readonly Image _background;
    private MonitorInfo _monitor;
    private bool _webViewDisposed;
    private bool _postFailureLogged;
    private bool _arrangeHidden;

    /// <inheritdoc />
    public string MonitorId { get; }

    /// <summary>The monitor this viewport represents; read by <see cref="SimulatorWindow"/>'s layout pass.</summary>
    public MonitorInfo Monitor => _monitor;

    /// <summary>Synthetic pattern drawn as the viewport background (the monitor's source content).</summary>
    public string SourcePattern { get; set; } = SyntheticPatterns.Gradient;

    /// <summary>Raised when this viewport's bounds change so the owner re-runs its layout pass.</summary>
    public Action? LayoutRequested { get; set; }

    /// <summary>Raised when this viewport is closing so the owner removes it from the canvas.</summary>
    public Action<SimulatorEffectSurface>? Removed { get; set; }

    /// <inheritdoc />
    public event EventHandler<string>? BridgeMessageReceived;

    /// <inheritdoc />
    public event EventHandler? PageReady;

    public SimulatorEffectSurface(MonitorInfo monitor, ILogger logger)
    {
        _monitor = monitor;
        _logger = logger;
        MonitorId = monitor.Id;

        Focusable = false;
        IsHitTestVisible = false; // the composite is for viewing; never steal focus per frame
        Background = Brushes.Black;
        ClipToBounds = true;

        _background = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        _webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent, // let the source content show through
            Focusable = false,
            IsHitTestVisible = false,
        };
        Children.Add(_background);
        Children.Add(_webView);
        RenderBackground();
    }

    /// <inheritdoc />
    public async Task InitializeWebViewAsync(CoreWebView2Environment environment)
    {
        await _webView.EnsureCoreWebView2Async(environment);

        CoreWebView2 core = _webView.CoreWebView2;
        CoreWebView2Settings settings = core.Settings;

        // Same kiosk lockdown as EffectWindow (the surface runs the same production runtime).
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;

        WebViewHelpers.MapVirtualHost(core);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;

        string url = WebViewHelpers.EffectUrl(MonitorId);
        core.Navigate(url);
        _logger.LogInformation("Simulator effect surface on {MonitorId} navigating to {Url}", MonitorId, url);
    }

    /// <inheritdoc />
    public void RepositionTo(MonitorInfo monitor)
    {
        bool boundsChanged =
            monitor.X != _monitor.X || monitor.Y != _monitor.Y ||
            monitor.Width != _monitor.Width || monitor.Height != _monitor.Height;
        _monitor = monitor;
        if (boundsChanged)
        {
            RenderBackground(); // resolution/orientation may have changed
            LayoutRequested?.Invoke();
        }
    }

    /// <inheritdoc />
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
                _logger.LogWarning(ex, "Posting to the simulator surface on {MonitorId} failed; further failures suppressed", MonitorId);
            }
            return false;
        }
    }

    /// <summary>
    /// Story 10.6 (UX): when the composite is in layout-edit mode the effect viewports are hidden so the
    /// monitor boxes underneath can be grabbed (a windowed WebView2 can't be dragged through). This flag is
    /// <b>authoritative over <see cref="Show"/></b> — the window manager calls <see cref="Show"/> right
    /// after creating a surface, which would otherwise un-hide it and re-cover the draggable box.
    /// </summary>
    public bool ArrangeHidden
    {
        get => _arrangeHidden;
        set
        {
            _arrangeHidden = value;
            Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <inheritdoc />
    public void Show()
    {
        if (!_arrangeHidden)
        {
            Visibility = Visibility.Visible;
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        if (_webViewDisposed) return;
        _webViewDisposed = true;
        Removed?.Invoke(this);
        try
        {
            _webView.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the simulator surface webview on {MonitorId} failed", MonitorId);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Simulator surface web message on {MonitorId} could not be read as JSON", MonitorId);
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
            _logger.LogWarning("Simulator effect page navigation failed on {MonitorId}: {Status}", MonitorId, e.WebErrorStatus);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning("Simulator surface WebView2 process failure on {MonitorId}: {Kind}", MonitorId, e.ProcessFailedKind);

        // Mirror EffectWindow: a crashed viewport reloads itself, never taking down the composite.
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_webViewDisposed) return;
                try
                {
                    _webView.CoreWebView2?.Reload();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Simulator surface reload on {MonitorId} after render-process exit failed", MonitorId);
                }
            });
        }
    }

    /// <summary>Renders the monitor's synthetic pattern (downscaled for cost) as the viewport background.</summary>
    private void RenderBackground()
    {
        try
        {
            const int maxWidth = 480;
            int w = Math.Max(1, _monitor.Width);
            int h = Math.Max(1, _monitor.Height);
            if (w > maxWidth)
            {
                h = Math.Max(1, (int)((long)h * maxWidth / w));
                w = maxWidth;
            }

            var buffer = new byte[w * h * 4];
            SyntheticPatterns.Fill(SourcePattern, buffer, w, h, frameIndex: 0);

            var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, w, h), buffer, w * 4, 0);
            bitmap.Freeze();
            _background.Source = bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rendering the simulator background for {MonitorId} failed", MonitorId);
        }
    }
}
#endif

using AmbientFx.Models;
using Microsoft.Web.WebView2.Core;

namespace AmbientFx.Hosting;

/// <summary>
/// Abstraction over one effect surface, so <see cref="WebViewWindowManager"/> can host either a real
/// per-monitor <see cref="EffectWindow"/> (production: a borderless top-most window placed in device
/// pixels via SetWindowPos) or a simulator viewport (a child WebView2 inside one composite window,
/// placed on a scaled canvas — Story 10.2). It mirrors exactly the public surface
/// <see cref="EffectWindow"/> already exposes; the only behavioral difference between implementations
/// is placement. All members are UI-thread affine.
/// </summary>
public interface IEffectSurfaceHost
{
    /// <summary>The monitor this surface was created for. Stable for the surface's lifetime.</summary>
    string MonitorId { get; }

    /// <summary>Raw bridge JSON received from the hosted page (UI thread).</summary>
    event EventHandler<string>? BridgeMessageReceived;

    /// <summary>Raised on the UI thread when the effect page finished loading and can receive messages.</summary>
    event EventHandler? PageReady;

    /// <summary>Initializes the hosted WebView2 against the shared environment and navigates to the effect runtime.</summary>
    Task InitializeWebViewAsync(CoreWebView2Environment environment);

    /// <summary>Re-applies placement for a (possibly moved/resized) monitor.</summary>
    void RepositionTo(MonitorInfo monitor);

    /// <summary>Posts pre-serialized envelope JSON to the page. Never throws; false if not yet initialized.</summary>
    bool TryPostWebMessage(string json);

    /// <summary>Makes the surface visible (production: <c>Window.Show()</c>; simulator: shows the viewport).</summary>
    void Show();

    /// <summary>Closes the surface and disposes its WebView2 (production: <c>Window.Close()</c>).</summary>
    void Close();
}

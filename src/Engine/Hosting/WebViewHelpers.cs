using System.IO;
using Microsoft.Web.WebView2.Core;

namespace AmbientFx.Hosting;

/// <summary>
/// Shared constants and helpers for the WebView2 hosting layer: the virtual-host origin
/// the bundled web app is served from, the well-known entry-point URLs, and the
/// per-webview virtual-host mapping that must be applied to every CoreWebView2.
/// </summary>
internal static class WebViewHelpers
{
    /// <summary>
    /// Virtual host the bundled web app is served from. Uses the RFC 6761 ".example" TLD —
    /// ".local" triggers mDNS resolution that can stall navigations (research note §3).
    /// </summary>
    internal const string VirtualHost = "ambientfx.example";

    /// <summary>SourceWindow value used for commands originating from the control window.</summary>
    internal const string ControlSource = "control";

    /// <summary>Folder containing the built web assets, copied next to the exe at build time.</summary>
    internal static string WebRootPath => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    /// <summary>Entry point of the control UI (FR8).</summary>
    internal static string ControlUrl => $"https://{VirtualHost}/control.html";

    /// <summary>
    /// Entry point of the effect runtime for one monitor. The query string is ignored for
    /// file resolution by the virtual host but fully available to JS via location.search.
    /// </summary>
    internal static string EffectUrl(string monitorId) =>
        $"https://{VirtualHost}/effects.html?monitorId={Uri.EscapeDataString(monitorId)}";

    /// <summary>
    /// Explicit user data folder. The default (the exe directory) is typically not writable
    /// under Program Files (research note gotcha #8).
    /// </summary>
    internal static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmbientFx", "WebView2");

    /// <summary>
    /// Maps the bundled wwwroot onto <see cref="VirtualHost"/>. The mapping is PER
    /// CoreWebView2 — call on every webview after EnsureCoreWebView2Async, before Navigate.
    /// </summary>
    internal static void MapVirtualHost(CoreWebView2 core) =>
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, WebRootPath, CoreWebView2HostResourceAccessKind.DenyCors);
}

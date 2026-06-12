using AmbientFx.Bridge;
using AmbientFx.Capture;
using AmbientFx.Models;

namespace AmbientFx.Hosting;

/// <summary>
/// Owns every WebView2-hosted window: the single control window (custom chrome) and one
/// borderless fullscreen effect window per target monitor. All members must be called on
/// the WPF dispatcher thread EXCEPT the Post* methods, which marshal internally.
/// </summary>
public interface IWebViewWindowManager : IDisposable
{
    /// <summary>Creates the shared CoreWebView2Environment. Call once at startup before any window.</summary>
    Task InitializeAsync();

    Task ShowControlWindowAsync();
    void HideControlWindow();
    bool IsControlWindowVisible { get; }

    /// <summary>
    /// Creates/closes/reassigns effect windows so exactly one exists per spec entry.
    /// An empty list closes all effect windows (FR13 — monitors fully released).
    /// </summary>
    Task SyncEffectWindowsAsync(IReadOnlyList<EffectWindowSpec> specs);

    /// <summary>Posts {type,payload} to every open WebView2 (control + all effect windows). Thread-safe.</summary>
    void PostToAll(string type, object payload);

    /// <summary>Posts to the control window only (no-op if closed). Thread-safe.</summary>
    void PostToControl(string type, object payload);

    /// <summary>Posts to the effect window on the given monitor (no-op if none). Thread-safe.</summary>
    void PostToEffectWindow(string monitorId, string type, object payload);

    /// <summary>Executes a custom-chrome window command on the control window: "minimize" | "maximize" | "restore" | "close".</summary>
    void HandleControlWindowCommand(string action);

    /// <summary>A command arrived from any hosted web page.</summary>
    event EventHandler<BridgeCommandEventArgs>? CommandReceived;

    /// <summary>The user closed the control window (host hides to tray instead of exiting).</summary>
    event EventHandler? ControlWindowCloseRequested;

    event EventHandler<PipelineErrorEventArgs>? Error;
}

public sealed class EffectWindowSpec
{
    public required MonitorInfo Monitor { get; init; }
    public required WindowConfigPayload Config { get; init; }
}

public sealed class BridgeCommandEventArgs : EventArgs
{
    public required CommandEnvelope Command { get; init; }

    /// <summary>"control" or the monitorId of the effect window that sent it.</summary>
    public required string SourceWindow { get; init; }
}

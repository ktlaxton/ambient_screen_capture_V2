namespace AmbientFx.Services;

/// <summary>
/// System tray icon with quick toggle, preset switching, open-settings and quit (FR10).
/// Raises events; the coordinator decides what they mean.
/// </summary>
public interface ISystemTrayService : IDisposable
{
    /// <summary>Creates the tray icon. Must be called on the UI thread.</summary>
    void Initialize();

    /// <summary>Refreshes menu state: enabled checkmark, preset list with the active one marked.</summary>
    void Update(bool isEnabled, IReadOnlyList<string> presetNames, string activePresetName);

    /// <summary>
    /// Shows a tray balloon notification. Used as the toast fallback whenever the control
    /// window is hidden or cannot exist (e.g. WebView2 runtime missing) so status is never
    /// silently lost (FR12/NFR5). Safe to call from any thread.
    /// </summary>
    void ShowNotification(string title, string message, bool isError);

    event EventHandler? ToggleEnabledRequested;
    event EventHandler<string>? PresetSelected;
    event EventHandler? OpenSettingsRequested;
    event EventHandler? ExitRequested;
}

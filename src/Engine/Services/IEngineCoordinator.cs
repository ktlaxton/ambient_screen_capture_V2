namespace AmbientFx.Services;

/// <summary>
/// The conductor: owns the ApplicationSettings state, starts/stops the
/// capture -> processing -> windows pipeline, handles every bridge command,
/// tray/hotkey events, monitor changes, presets, and settings persistence.
/// </summary>
public interface IEngineCoordinator
{
    /// <summary>Boots services, restores settings, shows the control window (unless startMinimized) and applies IsEnabled.</summary>
    Task StartAsync(bool startMinimized);

    /// <summary>Stops the pipeline, closes all windows, flushes settings.</summary>
    Task ShutdownAsync();
}

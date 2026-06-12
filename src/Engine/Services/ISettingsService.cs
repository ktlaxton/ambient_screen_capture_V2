using AmbientFx.Models;

namespace AmbientFx.Services;

/// <summary>
/// JSON settings persistence in %AppData%\AmbientFx\settings.json with a .backup fallback.
/// Load never throws (falls back to backup, then defaults). Save is atomic (temp + move).
/// </summary>
public interface ISettingsService
{
    Task<ApplicationSettings> LoadAsync();
    Task SaveAsync(ApplicationSettings settings);
    ApplicationSettings GetDefaults();
}

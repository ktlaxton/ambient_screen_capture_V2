namespace AmbientFx.Services;

/// <summary>Start-with-Windows via the HKCU Run registry key. The autostart entry launches with --minimized.</summary>
public interface IAutostartService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AmbientFx.Services;

/// <summary>
/// Start-with-Windows via the per-user HKCU Run registry key (no admin rights required).
/// The autostart entry launches the current executable with <c>--minimized</c> so the app
/// starts straight to the tray.
/// <para>
/// Thread safety: stateless apart from the injected logger; registry access is safe from
/// any thread. Never throws — registry failures are logged and reported as disabled (NFR5).
/// </para>
/// </summary>
public sealed class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AmbientFx";
    private const string Arguments = "--minimized";

    private readonly ILogger<AutostartService> _logger;

    public AutostartService(ILogger<AutostartService> logger)
    {
        _logger = logger;
    }

    /// <summary>The registry value we write: quoted exe path + launch argument.</summary>
    private static string? BuildCommand()
    {
        string? exe = Environment.ProcessPath;
        return exe is null ? null : $"\"{exe}\" {Arguments}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports true only when the value exists AND its path portion points at THIS executable
    /// (case-insensitive). Stale entries from moved/renamed installs therefore read as disabled,
    /// so the UI always shows the real state.
    /// </remarks>
    public bool IsEnabled
    {
        get
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe is null)
                {
                    return false;
                }

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key?.GetValue(ValueName) is not string value || value.Length == 0)
                {
                    return false;
                }

                return string.Equals(ExtractPath(value), exe, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read autostart state from HKCU\\{KeyPath}", RunKeyPath);
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                     ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                _logger.LogError("Could not open or create HKCU\\{KeyPath}", RunKeyPath);
                return;
            }

            if (enabled)
            {
                string? command = BuildCommand();
                if (command is null)
                {
                    _logger.LogError("Cannot enable autostart: Environment.ProcessPath is unavailable");
                    return;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
                _logger.LogInformation("Autostart enabled: {Command}", command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Autostart disabled (Run value removed if present)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set autostart to {Enabled}", enabled);
        }
    }

    /// <summary>
    /// Extracts the executable path portion from a Run-key command line:
    /// a leading quoted segment, or everything up to the first space otherwise.
    /// </summary>
    private static string ExtractPath(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);
            return closing > 0 ? trimmed[1..closing] : trimmed.Trim('"');
        }

        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}

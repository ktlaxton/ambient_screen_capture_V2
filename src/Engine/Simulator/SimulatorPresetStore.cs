#if SIMULATOR_ENABLED
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). Per-user storage for full-scene simulator presets:
/// <c>%AppData%\AmbientFx\simulator\presets\*.json</c> — a simulator-only folder, deliberately
/// separate from the user's real <c>settings.json</c> (the simulator never persists engine state;
/// see <see cref="SimulatorSettingsService"/>). Files are plain <see cref="SimulatorScenario"/> v2
/// JSON, interchangeable with the curated library and the automation hook. Every method takes an
/// optional directory override so tests run against a temp folder. Compiled out of Release.
/// </summary>
public static class SimulatorPresetStore
{
    /// <summary>One saved preset: display name (the file stem) and its full path.</summary>
    public readonly record struct Entry(string Name, string Path);

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AmbientFx", "simulator", "presets");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Pure: coerces a user-typed preset name into a safe Windows file stem — invalid filename
    /// characters become '-', leading/trailing spaces and trailing dots are trimmed, length is
    /// capped at 60, reserved device names (CON, NUL, COM1…) get a suffix, and an empty result
    /// becomes "preset".
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        }

        string result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length > 60)
        {
            result = result[..60].Trim().TrimEnd('.');
        }
        if (result.Length == 0)
        {
            result = "preset";
        }
        if (ReservedNames.Contains(result))
        {
            result += "-preset";
        }
        return result;
    }

    /// <summary>Lists saved presets (name = file stem), sorted by name. Missing folder or any IO
    /// failure yields an empty list — never throws.</summary>
    public static IReadOnlyList<Entry> List(string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        try
        {
            if (!Directory.Exists(dir))
            {
                return Array.Empty<Entry>();
            }
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(p => new Entry(System.IO.Path.GetFileNameWithoutExtension(p), p))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<Entry>();
        }
    }

    /// <summary>Loads a preset file. Returns null when the file is gone (deleted since listing);
    /// a malformed file degrades to the scenario loader's fallback (logged there). Never throws.</summary>
    public static SimulatorScenario? TryLoad(string path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        return SimulatorScenario.LoadFromFile(path, logger);
    }

    /// <summary>Saves the scene under a sanitized name (overwrites an existing preset of the same
    /// name). Sets <see cref="SimulatorScenario.Name"/> to the sanitized name so the file and its
    /// content agree. Returns false (logged) on failure — never throws.</summary>
    public static bool Save(SimulatorScenario scenario, string name, ILogger logger, string? directory = null)
    {
        try
        {
            string safe = SanitizeFileName(name);
            string path = System.IO.Path.Combine(directory ?? DefaultDirectory, safe + ".json");
            scenario.Name = safe;
            scenario.Save(path);
            logger.LogInformation("Simulator: preset '{Name}' saved to '{Path}'.", safe, path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulator: failed to save preset '{Name}'.", name);
            return false;
        }
    }
}
#endif

using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AmbientFx.Bridge;
using AmbientFx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Allow the test project to use the internal test constructor (see SettingsService(string)).
[assembly: InternalsVisibleTo("AmbientFx.Engine.Tests")]

namespace AmbientFx.Services;

/// <summary>
/// JSON settings persistence in %AppData%\AmbientFx\settings.json (Roaming, FR9).
/// Fixes the MVP bug where monitor selection was never persisted: this service
/// round-trips the entire <see cref="ApplicationSettings"/> object verbatim.
/// <para>
/// Save is atomic-ish: write to settings.tmp, then move over settings.json, with the
/// previous file preserved as settings.backup.json. Load never throws: it falls back
/// from file to backup to <see cref="GetDefaults"/>.
/// </para>
/// Thread safety: all file access is serialized through an async gate; safe to call
/// from any thread.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string SettingsFileName = "settings.json";
    private const string BackupFileName = "settings.backup.json";
    private const string TempFileName = "settings.tmp";

    /// <summary>
    /// Same camelCase shape the web layer sees over the bridge, just indented so the
    /// on-disk file is human-readable. Reads always use <see cref="BridgeJson.Options"/>
    /// (case-insensitive; unknown fields are ignored).
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new(BridgeJson.Options)
    {
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the service against %AppData%\AmbientFx with no logging.</summary>
    public SettingsService()
        : this(GetDefaultDirectory(), NullLogger<SettingsService>.Instance)
    {
    }

    /// <summary>DI constructor: %AppData%\AmbientFx + injected logger.</summary>
    public SettingsService(ILogger<SettingsService> logger)
        : this(GetDefaultDirectory(), logger)
    {
    }

    /// <summary>Test constructor: persist into an arbitrary directory.</summary>
    internal SettingsService(string directory, ILogger<SettingsService>? logger = null)
    {
        _directory = directory;
        _settingsPath = Path.Combine(directory, SettingsFileName);
        _backupPath = Path.Combine(directory, BackupFileName);
        _tempPath = Path.Combine(directory, TempFileName);
        _logger = logger ?? NullLogger<SettingsService>.Instance;
    }

    private static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // Roaming (FR9)
            "AmbientFx");

    /// <inheritdoc />
    public async Task<ApplicationSettings> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ApplicationSettings? settings = await TryReadAsync(_settingsPath).ConfigureAwait(false);
            if (settings is not null)
            {
                _logger.LogDebug("Settings loaded from {Path}", _settingsPath);
                return Normalize(settings);
            }

            settings = await TryReadAsync(_backupPath).ConfigureAwait(false);
            if (settings is not null)
            {
                _logger.LogWarning(
                    "Settings file missing or unreadable; recovered from backup {Path}", _backupPath);
                return Normalize(settings);
            }

            _logger.LogInformation(
                "No usable settings at {Path}; using defaults (first run or corrupt files)", _settingsPath);
            return GetDefaults();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Never throws: failures are logged and the previous file (plus backup) is left intact.</remarks>
    public async Task SaveAsync(ApplicationSettings settings)
    {
        if (settings is null)
        {
            _logger.LogWarning("SaveAsync called with null settings; ignored");
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);

            if (File.Exists(_settingsPath))
            {
                File.Copy(_settingsPath, _backupPath, overwrite: true);
            }

            string json = JsonSerializer.Serialize(settings, WriteOptions);
            await File.WriteAllTextAsync(_tempPath, json).ConfigureAwait(false);
            File.Move(_tempPath, _settingsPath, overwrite: true);

            _logger.LogDebug("Settings saved to {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ApplicationSettings GetDefaults() => new()
    {
        IsEnabled = false,
        SourceMonitorId = string.Empty,
        TargetMonitorIds = new List<string>(),
        ActiveEffectId = "edge-glow",
        EffectByMonitorId = new Dictionary<string, string>(),
        AudioSensitivity = 0.5f,
        GlobalIntensity = 1.0f,
        Smoothing = 0.5f,
        Brightness = 0.85f,
        MaxFps = 60,
        ZonesPerEdge = 8,
        AudioBands = 12,
        Autostart = false,
        EffectParamsById = new Dictionary<string, Dictionary<string, JsonElement>>(),
        // All known actions present but unbound, so the web UI sees stable keys.
        Hotkeys = HotkeyActions.All.ToDictionary(action => action, _ => string.Empty),
        Presets = new List<Preset>(),
        ActivePresetName = string.Empty,
        FirstRunCompleted = false,
    };

    /// <summary>Reads + deserializes one candidate file; returns null on any failure (never throws).</summary>
    private async Task<ApplicationSettings?> TryReadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ApplicationSettings>(json, BridgeJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read settings from {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Repairs nulls AND out-of-range values that explicit JSON nulls or hand-edited files
    /// can leave behind, so callers never see null collections/strings or values that would
    /// throw downstream (e.g. AudioBands &lt; 1 would crash SpectrumAnalyzer at startup —
    /// bricking every launch until the file is deleted).
    /// </summary>
    private static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        settings.SourceMonitorId ??= string.Empty;
        settings.TargetMonitorIds = settings.TargetMonitorIds
            ?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? new List<string>();
        settings.ActiveEffectId = string.IsNullOrWhiteSpace(settings.ActiveEffectId)
            ? "edge-glow" : settings.ActiveEffectId;
        settings.EffectByMonitorId ??= new Dictionary<string, string>();
        settings.EffectParamsById ??= new Dictionary<string, Dictionary<string, JsonElement>>();
        settings.Hotkeys ??= new Dictionary<string, string>();
        settings.ActivePresetName ??= string.Empty;

        settings.AudioSensitivity = ClampUnit(settings.AudioSensitivity, 0.5f);
        settings.GlobalIntensity = ClampUnit(settings.GlobalIntensity, 1.0f);
        settings.Smoothing = ClampUnit(settings.Smoothing, 0.5f);
        settings.Brightness = ClampUnit(settings.Brightness, 0.85f);
        settings.MaxFps = settings.MaxFps is >= 1 and <= 240 ? settings.MaxFps : 60;
        settings.ZonesPerEdge = Math.Clamp(settings.ZonesPerEdge, 1, 64);
        settings.AudioBands = Math.Clamp(settings.AudioBands, 1, 64);

        // Presets: drop null/nameless/snapshot-less entries and normalize each snapshot the
        // same way (loadPreset funnels snapshot values into the same throwing paths).
        var presets = new List<Preset>();
        foreach (Preset? preset in settings.Presets ?? new List<Preset>())
        {
            if (preset?.Snapshot is null || string.IsNullOrWhiteSpace(preset.Name)) continue;
            preset.Snapshot.Presets = new List<Preset>(); // snapshots never nest presets
            Normalize(preset.Snapshot);
            presets.Add(preset);
        }
        settings.Presets = presets;
        return settings;
    }

    /// <summary>0..1 clamp that also repairs NaN/Infinity (NaN poisons smoothing EMAs downstream).</summary>
    private static float ClampUnit(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
}

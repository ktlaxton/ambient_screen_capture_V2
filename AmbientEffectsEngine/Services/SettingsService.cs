using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsDirectoryPath;
        private readonly string _settingsFilePath;
        private readonly string _backupFilePath;

        public SettingsService() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmbientEffectsEngine"))
        {
        }

        // Constructor for testing with custom directory
        internal SettingsService(string settingsDirectory)
        {
            _settingsDirectoryPath = settingsDirectory;
            _settingsFilePath = Path.Combine(_settingsDirectoryPath, "settings.json");
            _backupFilePath = Path.Combine(_settingsDirectoryPath, "settings.backup.json");
        }

        public async Task<ApplicationSettings> LoadAsync()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return GetDefaults();
                }

                var json = await File.ReadAllTextAsync(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<ApplicationSettings>(json);
                
                return settings ?? GetDefaults();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                
                // Try to load from backup
                if (File.Exists(_backupFilePath))
                {
                    try
                    {
                        var backupJson = await File.ReadAllTextAsync(_backupFilePath);
                        var backupSettings = JsonSerializer.Deserialize<ApplicationSettings>(backupJson);
                        return backupSettings ?? GetDefaults();
                    }
                    catch (Exception backupEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load backup settings: {backupEx.Message}");
                    }
                }
                
                return GetDefaults();
            }
        }

        public async Task SaveAsync(ApplicationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            try
            {
                // Ensure directory exists
                Directory.CreateDirectory(_settingsDirectoryPath);

                // Create backup if settings file exists
                if (File.Exists(_settingsFilePath))
                {
                    File.Copy(_settingsFilePath, _backupFilePath, overwrite: true);
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
                throw;
            }
        }

        public ApplicationSettings GetDefaults()
        {
            return new ApplicationSettings
            {
                IsEnabled = false,
                SelectedEffectId = "softglow",
                AudioSensitivity = 0.5f,
                SourceMonitorId = string.Empty,
                TargetMonitorIds = new System.Collections.Generic.List<string>()
            };
        }
    }
}
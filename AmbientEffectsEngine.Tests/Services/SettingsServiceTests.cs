using System;
using System.IO;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services;
using Xunit;

namespace AmbientEffectsEngine.Tests.Services
{
    public class SettingsServiceTests : IDisposable
    {
        private readonly SettingsService _settingsService;
        private readonly string _testDirectory;

        public SettingsServiceTests()
        {
            // Use a unique test directory to avoid conflicts
            _testDirectory = Path.Combine(Path.GetTempPath(), "AmbientEffectsEngine_Tests_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_testDirectory);

            // Create settings service with test directory using internal constructor
            _settingsService = new SettingsService(_testDirectory);
        }

        [Fact]
        public void GetDefaults_ShouldReturnValidDefaultSettings()
        {
            // Act
            var defaults = _settingsService.GetDefaults();

            // Assert
            Assert.NotNull(defaults);
            Assert.False(defaults.IsEnabled);
            Assert.Equal("softglow", defaults.SelectedEffectId);
            Assert.Equal(0.5f, defaults.AudioSensitivity);
            Assert.Equal(string.Empty, defaults.SourceMonitorId);
            Assert.NotNull(defaults.TargetMonitorIds);
            Assert.Empty(defaults.TargetMonitorIds);
        }

        [Fact]
        public async Task LoadAsync_WhenNoFileExists_ShouldReturnDefaults()
        {
            // Act
            var settings = await _settingsService.LoadAsync();

            // Assert
            Assert.NotNull(settings);
            Assert.False(settings.IsEnabled);
            Assert.Equal("softglow", settings.SelectedEffectId);
            Assert.Equal(0.5f, settings.AudioSensitivity);
        }

        [Fact]
        public async Task SaveAsync_ShouldCreateDirectoryAndFile()
        {
            // Arrange
            var testSettings = new ApplicationSettings
            {
                IsEnabled = true,
                SelectedEffectId = "generativevisualizer",
                AudioSensitivity = 0.8f,
                SourceMonitorId = "monitor1",
                TargetMonitorIds = new System.Collections.Generic.List<string> { "monitor2", "monitor3" }
            };

            // Act
            await _settingsService.SaveAsync(testSettings);

            // Assert
            Assert.True(Directory.Exists(_testDirectory));
            Assert.True(File.Exists(Path.Combine(_testDirectory, "settings.json")));
        }

        [Fact]
        public async Task SaveAndLoadAsync_ShouldPersistAllSettings()
        {
            // Arrange
            var originalSettings = new ApplicationSettings
            {
                IsEnabled = true,
                SelectedEffectId = "generativevisualizer",
                AudioSensitivity = 0.75f,
                SourceMonitorId = "primary_monitor",
                TargetMonitorIds = new System.Collections.Generic.List<string> { "secondary1", "secondary2" }
            };

            // Act
            await _settingsService.SaveAsync(originalSettings);
            var loadedSettings = await _settingsService.LoadAsync();

            // Assert
            Assert.NotNull(loadedSettings);
            Assert.Equal(originalSettings.IsEnabled, loadedSettings.IsEnabled);
            Assert.Equal(originalSettings.SelectedEffectId, loadedSettings.SelectedEffectId);
            Assert.Equal(originalSettings.AudioSensitivity, loadedSettings.AudioSensitivity);
            Assert.Equal(originalSettings.SourceMonitorId, loadedSettings.SourceMonitorId);
            Assert.Equal(originalSettings.TargetMonitorIds, loadedSettings.TargetMonitorIds);
        }

        [Fact]
        public async Task SaveAsync_ShouldCreateBackupOfExistingFile()
        {
            // Arrange
            var firstSettings = new ApplicationSettings { IsEnabled = false, AudioSensitivity = 0.3f };
            var secondSettings = new ApplicationSettings { IsEnabled = true, AudioSensitivity = 0.7f };

            // Act
            await _settingsService.SaveAsync(firstSettings);
            await _settingsService.SaveAsync(secondSettings);

            // Assert
            Assert.True(File.Exists(Path.Combine(_testDirectory, "settings.json")));
            Assert.True(File.Exists(Path.Combine(_testDirectory, "settings.backup.json")));

            // Verify backup contains first settings
            var backupContent = await File.ReadAllTextAsync(Path.Combine(_testDirectory, "settings.backup.json"));
            Assert.Contains("0.3", backupContent);
            Assert.Contains("false", backupContent);

            // Verify main file contains second settings
            var mainContent = await File.ReadAllTextAsync(Path.Combine(_testDirectory, "settings.json"));
            Assert.Contains("0.7", mainContent);
            Assert.Contains("true", mainContent);
        }

        [Fact]
        public async Task LoadAsync_WithCorruptedFile_ShouldTryBackupThenDefaults()
        {
            // Arrange - Create a valid backup by saving twice
            var firstSettings = new ApplicationSettings { IsEnabled = true, AudioSensitivity = 0.9f };
            await _settingsService.SaveAsync(firstSettings);
            
            var secondSettings = new ApplicationSettings { IsEnabled = false, AudioSensitivity = 0.3f };
            await _settingsService.SaveAsync(secondSettings); // This creates backup of firstSettings

            // Corrupt the main settings file
            var settingsFile = Path.Combine(_testDirectory, "settings.json");
            await File.WriteAllTextAsync(settingsFile, "{ corrupted json content");

            // Act
            var loadedSettings = await _settingsService.LoadAsync();

            // Assert - Should load from backup (which contains firstSettings)
            Assert.NotNull(loadedSettings);
            Assert.True(loadedSettings.IsEnabled);
            Assert.Equal(0.9f, loadedSettings.AudioSensitivity);
        }

        [Fact]
        public async Task LoadAsync_WithBothFilesCorrupted_ShouldReturnDefaults()
        {
            // Arrange - Create corrupted files
            var settingsFile = Path.Combine(_testDirectory, "settings.json");
            var backupFile = Path.Combine(_testDirectory, "settings.backup.json");
            
            Directory.CreateDirectory(_testDirectory);
            await File.WriteAllTextAsync(settingsFile, "{ corrupted json }");
            await File.WriteAllTextAsync(backupFile, "{ also corrupted }");

            // Act
            var loadedSettings = await _settingsService.LoadAsync();

            // Assert - Should return defaults
            Assert.NotNull(loadedSettings);
            Assert.False(loadedSettings.IsEnabled);
            Assert.Equal("softglow", loadedSettings.SelectedEffectId);
            Assert.Equal(0.5f, loadedSettings.AudioSensitivity);
        }

        [Fact]
        public async Task SaveAsync_WithNullSettings_ShouldThrow()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() => _settingsService.SaveAsync(null!));
        }

        [Fact]
        public async Task SaveAndLoadAsync_WithSpecialCharacters_ShouldHandleCorrectly()
        {
            // Arrange
            var settingsWithSpecialChars = new ApplicationSettings
            {
                IsEnabled = true,
                SelectedEffectId = "test_effect_with_underscore",
                AudioSensitivity = 0.123456f,
                SourceMonitorId = "Monitor with spaces and symbols !@#",
                TargetMonitorIds = new System.Collections.Generic.List<string> 
                { 
                    "Monitor 1 (Primary)", 
                    "Monitor-2_Secondary" 
                }
            };

            // Act
            await _settingsService.SaveAsync(settingsWithSpecialChars);
            var loadedSettings = await _settingsService.LoadAsync();

            // Assert
            Assert.Equal(settingsWithSpecialChars.SelectedEffectId, loadedSettings.SelectedEffectId);
            Assert.Equal(settingsWithSpecialChars.SourceMonitorId, loadedSettings.SourceMonitorId);
            Assert.Equal(settingsWithSpecialChars.TargetMonitorIds, loadedSettings.TargetMonitorIds);
        }

        [Fact]
        public async Task SaveAsync_WithReadOnlyDirectory_ShouldHandleGracefully()
        {
            // This test would require platform-specific directory permission handling
            // For now, we'll test that the method doesn't crash with invalid paths
            
            // Arrange - Create a new service with invalid path using internal constructor
            var invalidService = new SettingsService("C:\\Windows\\System32\\InvalidPath");
            var settings = new ApplicationSettings { IsEnabled = true };

            // Act & Assert - Should throw but not crash
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => invalidService.SaveAsync(settings));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
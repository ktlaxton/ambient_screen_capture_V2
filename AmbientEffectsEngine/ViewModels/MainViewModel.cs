using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services;
using AmbientEffectsEngine.Services.Processing;
using AmbientEffectsEngine.Services.Rendering;

namespace AmbientEffectsEngine.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IEffectsRenderingService? _effectsRenderingService;
        private readonly IEffectFactory? _effectFactory;
        private readonly ISettingsService? _settingsService;
        private readonly IDataProcessingService? _dataProcessingService;
        private bool _isEnabled;
        private EffectStyle _selectedEffect = new EffectStyle();
        private float _audioSensitivity;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    UpdateEffectsRenderingState();
                    SaveSettingsAsync();
                }
            }
        }

        public EffectStyle SelectedEffect
        {
            get => _selectedEffect;
            set
            {
                if (_selectedEffect != value)
                {
                    _selectedEffect = value;
                    OnPropertyChanged();
                    OnEffectSelectionChanged();
                }
            }
        }

        public float AudioSensitivity
        {
            get => _audioSensitivity;
            set
            {
                if (Math.Abs(_audioSensitivity - value) > 0.001f)
                {
                    _audioSensitivity = Math.Clamp(value, 0.0f, 1.0f);
                    OnPropertyChanged();
                    OnAudioSensitivityChanged();
                }
            }
        }

        public ObservableCollection<EffectStyle> AvailableEffects { get; }

        public MainViewModel(IEffectsRenderingService? effectsRenderingService = null, 
                            IEffectFactory? effectFactory = null, 
                            ISettingsService? settingsService = null, 
                            IDataProcessingService? dataProcessingService = null)
        {
            _effectsRenderingService = effectsRenderingService;
            _effectFactory = effectFactory;
            _settingsService = settingsService;
            _dataProcessingService = dataProcessingService;
            
            // Initialize available effects from factory
            AvailableEffects = new ObservableCollection<EffectStyle>();
            LoadAvailableEffects();
            
            // Load settings or use defaults
            LoadSettingsAsync();
        }

        private void LoadAvailableEffects()
        {
            AvailableEffects.Clear();
            
            if (_effectFactory != null)
            {
                // Get available effects from the factory and create effect instances to get their metadata
                foreach (var effectId in _effectFactory.GetAvailableEffectIds())
                {
                    try
                    {
                        using var effect = _effectFactory.CreateEffect(effectId);
                        AvailableEffects.Add(new EffectStyle
                        {
                            Id = effect.EffectId,
                            Name = effect.Name,
                            Description = effect.Description
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load effect '{effectId}': {ex.Message}");
                    }
                }
            }
            
            // Fallback to hardcoded effects if factory is not available
            if (AvailableEffects.Count == 0)
            {
                AvailableEffects.Add(new EffectStyle { Id = "softglow", Name = "Soft Glow", Description = "Ambient glow effect" });
                AvailableEffects.Add(new EffectStyle { Id = "generativevisualizer", Name = "Generative Visualizer", Description = "Dynamic particle-based visualization" });
            }
        }

        private async void LoadSettingsAsync()
        {
            if (_settingsService == null)
            {
                // Use defaults if no settings service
                _isEnabled = false;
                _audioSensitivity = 0.5f;
                _selectedEffect = AvailableEffects.FirstOrDefault(e => e.Id == "softglow") ?? AvailableEffects.FirstOrDefault() ?? new EffectStyle();
                return;
            }

            try
            {
                var settings = await _settingsService.LoadAsync();
                
                _isEnabled = settings.IsEnabled;
                _audioSensitivity = Math.Clamp(settings.AudioSensitivity, 0.0f, 1.0f);
                _selectedEffect = AvailableEffects.FirstOrDefault(e => e.Id == settings.SelectedEffectId) 
                                ?? AvailableEffects.FirstOrDefault() ?? new EffectStyle();
                
                // Update data processing service with loaded sensitivity
                if (_dataProcessingService != null)
                {
                    _dataProcessingService.AudioSensitivity = _audioSensitivity;
                }
                
                // Apply monitor selections to effects rendering service
                if (_effectsRenderingService != null && settings.TargetMonitorIds != null)
                {
                    _effectsRenderingService.SetTargetMonitors(settings.TargetMonitorIds);
                }
                
                // Notify UI of loaded values
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(AudioSensitivity));
                OnPropertyChanged(nameof(SelectedEffect));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
                
                // Fallback to defaults
                _isEnabled = false;
                _audioSensitivity = 0.5f;
                _selectedEffect = AvailableEffects.FirstOrDefault(e => e.Id == "softglow") ?? AvailableEffects.FirstOrDefault() ?? new EffectStyle();
            }
        }

        private async void SaveSettingsAsync()
        {
            if (_settingsService == null) return;

            try
            {
                var settings = new ApplicationSettings
                {
                    IsEnabled = _isEnabled,
                    SelectedEffectId = _selectedEffect?.Id ?? "softglow",
                    AudioSensitivity = _audioSensitivity,
                    SourceMonitorId = string.Empty,
                    TargetMonitorIds = new System.Collections.Generic.List<string>()
                };

                await _settingsService.SaveAsync(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        private void OnEffectSelectionChanged()
        {
            if (_effectsRenderingService == null || _selectedEffect == null) return;

            try
            {
                _effectsRenderingService.SetCurrentEffect(_selectedEffect.Id);
                SaveSettingsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error changing effect to '{_selectedEffect.Id}': {ex.Message}");
            }
        }

        private void OnAudioSensitivityChanged()
        {
            if (_dataProcessingService != null)
            {
                _dataProcessingService.AudioSensitivity = _audioSensitivity;
            }
            
            SaveSettingsAsync();
        }

        private void UpdateEffectsRenderingState()
        {
            if (_effectsRenderingService == null) return;

            try
            {
                if (_isEnabled)
                {
                    _effectsRenderingService.Start();
                }
                else
                {
                    _effectsRenderingService.Stop();
                }
            }
            catch (Exception ex)
            {
                // Show error to user instead of hiding it
                System.Diagnostics.Debug.WriteLine($"Error updating effects rendering state: {ex.Message}");
                System.Windows.MessageBox.Show($"Error starting ambient effects: {ex.Message}\n\nDetails: {ex.StackTrace}", 
                    "Effects Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private EffectStyle _selectedEffect;
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
                    _audioSensitivity = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<EffectStyle> AvailableEffects { get; }

        public MainViewModel()
        {
            // Initialize with default values
            _isEnabled = false;
            _audioSensitivity = 0.5f;
            
            // Initialize available effects with placeholder data
            AvailableEffects = new ObservableCollection<EffectStyle>
            {
                new EffectStyle { Id = "none", Name = "None", Description = "No effect" },
                new EffectStyle { Id = "solid", Name = "Solid Color", Description = "Single dominant color" },
                new EffectStyle { Id = "gradient", Name = "Gradient", Description = "Color gradient effect" },
                new EffectStyle { Id = "pulse", Name = "Pulse", Description = "Pulsing color effect" }
            };

            // Set default selected effect
            _selectedEffect = AvailableEffects[0];
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
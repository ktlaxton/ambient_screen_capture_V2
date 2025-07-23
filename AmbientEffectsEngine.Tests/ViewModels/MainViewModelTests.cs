using System.ComponentModel;
using System.Linq;
using Xunit;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.ViewModels;

namespace AmbientEffectsEngine.Tests.ViewModels
{
    public class MainViewModelTests
    {
        [Fact]
        public void Constructor_InitializesWithDefaultValues()
        {
            // Act
            var viewModel = new MainViewModel();

            // Assert
            Assert.False(viewModel.IsEnabled);
            Assert.Equal(0.5f, viewModel.AudioSensitivity);
            Assert.NotNull(viewModel.SelectedEffect);
            Assert.Equal("softglow", viewModel.SelectedEffect.Id);
            Assert.NotNull(viewModel.AvailableEffects);
            Assert.Equal(2, viewModel.AvailableEffects.Count);
        }

        [Fact]
        public void IsEnabled_PropertyChanged_RaisesNotification()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var propertyChangedRaised = false;
            string? changedPropertyName = null;

            viewModel.PropertyChanged += (sender, args) =>
            {
                propertyChangedRaised = true;
                changedPropertyName = args.PropertyName;
            };

            // Act
            viewModel.IsEnabled = true;

            // Assert
            Assert.True(propertyChangedRaised);
            Assert.Equal(nameof(MainViewModel.IsEnabled), changedPropertyName);
            Assert.True(viewModel.IsEnabled);
        }

        [Fact]
        public void IsEnabled_SameValue_DoesNotRaiseNotification()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var propertyChangedRaised = false;

            viewModel.PropertyChanged += (sender, args) => propertyChangedRaised = true;

            // Act - setting to same value (false)
            viewModel.IsEnabled = false;

            // Assert
            Assert.False(propertyChangedRaised);
        }

        [Fact]
        public void AudioSensitivity_PropertyChanged_RaisesNotification()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var propertyChangedRaised = false;
            string? changedPropertyName = null;

            viewModel.PropertyChanged += (sender, args) =>
            {
                propertyChangedRaised = true;
                changedPropertyName = args.PropertyName;
            };

            // Act
            viewModel.AudioSensitivity = 0.75f;

            // Assert
            Assert.True(propertyChangedRaised);
            Assert.Equal(nameof(MainViewModel.AudioSensitivity), changedPropertyName);
            Assert.Equal(0.75f, viewModel.AudioSensitivity);
        }

        [Fact]
        public void AudioSensitivity_SameValue_DoesNotRaiseNotification()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var propertyChangedRaised = false;

            viewModel.PropertyChanged += (sender, args) => propertyChangedRaised = true;

            // Act - setting to same value (0.5f)
            viewModel.AudioSensitivity = 0.5f;

            // Assert
            Assert.False(propertyChangedRaised);
        }

        [Fact]
        public void SelectedEffect_PropertyChanged_RaisesNotification()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var newEffect = viewModel.AvailableEffects.First(e => e.Id == "generativevisualizer");
            var propertyChangedRaised = false;
            string? changedPropertyName = null;

            viewModel.PropertyChanged += (sender, args) =>
            {
                propertyChangedRaised = true;
                changedPropertyName = args.PropertyName;
            };

            // Act
            viewModel.SelectedEffect = newEffect;

            // Assert
            Assert.True(propertyChangedRaised);
            Assert.Equal(nameof(MainViewModel.SelectedEffect), changedPropertyName);
            Assert.Equal(newEffect, viewModel.SelectedEffect);
            Assert.Equal("generativevisualizer", viewModel.SelectedEffect.Id);
        }

        [Fact]
        public void SelectedEffect_SameValue_DoesNotRaiseNotification()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var currentEffect = viewModel.SelectedEffect;
            var propertyChangedRaised = false;

            viewModel.PropertyChanged += (sender, args) => propertyChangedRaised = true;

            // Act - setting to same value
            viewModel.SelectedEffect = currentEffect;

            // Assert
            Assert.False(propertyChangedRaised);
        }

        [Fact]
        public void AvailableEffects_InitializedWithCorrectValues()
        {
            // Act
            var viewModel = new MainViewModel();

            // Assert
            Assert.NotNull(viewModel.AvailableEffects);
            Assert.Equal(2, viewModel.AvailableEffects.Count);
            
            var effects = viewModel.AvailableEffects.ToList();
            Assert.Contains(effects, e => e.Id == "softglow" && e.Name == "Soft Glow");
            Assert.Contains(effects, e => e.Id == "generativevisualizer" && e.Name == "Generative Visualizer");
        }

        [Fact]
        public void AvailableEffects_AllEffectsHaveRequiredProperties()
        {
            // Act
            var viewModel = new MainViewModel();

            // Assert
            foreach (var effect in viewModel.AvailableEffects)
            {
                Assert.False(string.IsNullOrEmpty(effect.Id));
                Assert.False(string.IsNullOrEmpty(effect.Name));
                Assert.False(string.IsNullOrEmpty(effect.Description));
            }
        }

        [Fact]
        public void PropertyGetters_ReturnCorrectValues()
        {
            // Arrange
            var viewModel = new MainViewModel();
            var testEffect = viewModel.AvailableEffects.First(e => e.Id == "generativevisualizer");

            // Act
            viewModel.IsEnabled = true;
            viewModel.AudioSensitivity = 0.8f;
            viewModel.SelectedEffect = testEffect;

            // Assert
            Assert.True(viewModel.IsEnabled);
            Assert.Equal(0.8f, viewModel.AudioSensitivity);
            Assert.Equal(testEffect, viewModel.SelectedEffect);
        }

        [Fact]
        public void AudioSensitivity_BoundaryValues_HandleCorrectly()
        {
            // Arrange
            var viewModel = new MainViewModel();

            // Act & Assert - Minimum value
            viewModel.AudioSensitivity = 0.0f;
            Assert.Equal(0.0f, viewModel.AudioSensitivity);

            // Act & Assert - Maximum value
            viewModel.AudioSensitivity = 1.0f;
            Assert.Equal(1.0f, viewModel.AudioSensitivity);
        }

        [Fact]
        public void ImplementsINotifyPropertyChanged()
        {
            // Act
            var viewModel = new MainViewModel();

            // Assert
            Assert.IsAssignableFrom<INotifyPropertyChanged>(viewModel);
        }
    }
}
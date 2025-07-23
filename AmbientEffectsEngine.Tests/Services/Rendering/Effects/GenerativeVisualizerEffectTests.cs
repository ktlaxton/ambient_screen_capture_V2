using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services.Rendering.Effects;
using Xunit;

namespace AmbientEffectsEngine.Tests.Services.Rendering.Effects
{
    public class GenerativeVisualizerEffectTests : IDisposable
    {
        private readonly GenerativeVisualizerEffect _effect;

        public GenerativeVisualizerEffectTests()
        {
            _effect = new GenerativeVisualizerEffect();
        }

        [Fact]
        public void Constructor_ShouldInitializeWithCorrectProperties()
        {
            // Assert
            Assert.Equal("generativevisualizer", _effect.EffectId);
            Assert.Equal("Generative Visualizer", _effect.Name);
            Assert.Equal("Dynamic particle-based visualization that responds to audio intensity and screen colors", _effect.Description);
        }

        [Fact]
        public void Initialize_WithValidMonitors_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };

            // Act & Assert
            var exception = Record.Exception(() => _effect.Initialize(monitors));
            Assert.Null(exception);
        }

        [Fact]
        public void Initialize_WithEmptyMonitorList_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>();

            // Act & Assert
            var exception = Record.Exception(() => _effect.Initialize(monitors));
            Assert.Null(exception);
        }

        [Fact]
        public void Initialize_WithNullMonitors_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _effect.Initialize(null!));
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateEffect_WithValidProcessedData_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            var processedData = new ProcessedData(Color.Red, 0.5f, DateTime.UtcNow);

            // Act & Assert
            var exception = Record.Exception(() => _effect.UpdateEffect(processedData));
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateEffect_WithNullData_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() => _effect.UpdateEffect(null!));
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateEffect_WithMaxAudioIntensity_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            var processedData = new ProcessedData(Color.Blue, 1.0f, DateTime.UtcNow);

            // Act & Assert
            var exception = Record.Exception(() => _effect.UpdateEffect(processedData));
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateEffect_WithZeroAudioIntensity_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            var processedData = new ProcessedData(Color.Green, 0.0f, DateTime.UtcNow);

            // Act & Assert
            var exception = Record.Exception(() => _effect.UpdateEffect(processedData));
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateEffect_WithVariousColors_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            var colors = new[] { Color.Red, Color.Green, Color.Blue, Color.White, Color.Black, Color.Yellow, Color.Magenta };

            // Act & Assert
            foreach (var color in colors)
            {
                var processedData = new ProcessedData(color, 0.5f, DateTime.UtcNow);
                var exception = Record.Exception(() => _effect.UpdateEffect(processedData));
                Assert.Null(exception);
            }
        }

        [Fact]
        public void Show_AfterInitialization_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() => _effect.Show());
            Assert.Null(exception);
        }

        [Fact]
        public void Hide_AfterInitialization_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() => _effect.Hide());
            Assert.Null(exception);
        }

        [Fact]
        public void Show_WithoutInitialization_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _effect.Show());
            Assert.Null(exception);
        }

        [Fact]
        public void Hide_WithoutInitialization_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _effect.Hide());
            Assert.Null(exception);
        }

        [Fact]
        public void ShowAndHide_Sequence_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() =>
            {
                _effect.Show();
                _effect.Hide();
                _effect.Show();
                _effect.Hide();
            });
            Assert.Null(exception);
        }

        [Fact]
        public void MultipleUpdates_WithDifferentData_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    var intensity = (float)(i % 100) / 100.0f;
                    var color = Color.FromArgb(i % 255, (i * 2) % 255, (i * 3) % 255);
                    var processedData = new ProcessedData(color, intensity, DateTime.UtcNow);
                    _effect.UpdateEffect(processedData);
                }
            });
            Assert.Null(exception);
        }

        [Fact]
        public void Initialize_FiltersPrimaryMonitors_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Primary", Name = "Primary Monitor", IsPrimary = true },
                new DisplayMonitor { Id = "Secondary1", Name = "Secondary Monitor 1", IsPrimary = false },
                new DisplayMonitor { Id = "Secondary2", Name = "Secondary Monitor 2", IsPrimary = false }
            };

            // Act & Assert
            var exception = Record.Exception(() => _effect.Initialize(monitors));
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);
            _effect.Show();

            // Act & Assert
            var exception = Record.Exception(() => _effect.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_MultipleTimesAfterDisposal_ShouldNotThrow()
        {
            // Arrange
            _effect.Initialize(new List<DisplayMonitor>());

            // Act & Assert
            var exception = Record.Exception(() =>
            {
                _effect.Dispose();
                _effect.Dispose(); // Second disposal should be safe
                _effect.Dispose(); // Third disposal should also be safe
            });
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateEffect_AfterDisposal_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "Monitor1", Name = "Test Monitor", IsPrimary = false }
            };
            _effect.Initialize(monitors);
            _effect.Dispose();

            var processedData = new ProcessedData(Color.Red, 0.5f, DateTime.UtcNow);

            // Act & Assert - Should not throw but should also not process
            var exception = Record.Exception(() => _effect.UpdateEffect(processedData));
            Assert.Null(exception);
        }

        public void Dispose()
        {
            _effect?.Dispose();
        }
    }
}
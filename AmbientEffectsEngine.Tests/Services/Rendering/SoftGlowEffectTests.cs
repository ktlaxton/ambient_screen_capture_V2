using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services.Rendering.Effects;
using Xunit;

namespace AmbientEffectsEngine.Tests.Services.Rendering
{
    public class SoftGlowEffectTests : IDisposable
    {
        private readonly SoftGlowEffect _effect;

        public SoftGlowEffectTests()
        {
            _effect = new SoftGlowEffect();
        }

        [Fact]
        public void Initialize_WithValidMonitors_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = true },
                new DisplayMonitor { Id = "DISPLAY2", Name = "Monitor 2", IsPrimary = false }
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
        [System.STAThread]
        public void UpdateEffect_WithValidData_ShouldNotThrow()
        {
            // Arrange & Act & Assert
            Exception? testException = null;

            var staThread = new Thread(() =>
            {
                try
                {
                    var monitors = new List<DisplayMonitor>
                    {
                        new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
                    };
                    _effect.Initialize(monitors);

                    var processedData = new ProcessedData(Color.Red, 0.5f, DateTime.UtcNow);
                    _effect.UpdateEffect(processedData);
                }
                catch (Exception ex)
                {
                    testException = ex;
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Assert.Null(testException);
        }

        [Fact]
        public void UpdateEffect_WithNullData_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() => _effect.UpdateEffect(null!));
            Assert.Null(exception);
        }

        [Fact]
        [System.STAThread]
        public void UpdateEffect_WithZeroIntensity_ShouldNotThrow()
        {
            // Arrange & Act & Assert
            Exception? testException = null;

            var staThread = new Thread(() =>
            {
                try
                {
                    var monitors = new List<DisplayMonitor>
                    {
                        new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
                    };
                    _effect.Initialize(monitors);

                    var processedData = new ProcessedData(Color.Blue, 0.0f, DateTime.UtcNow);
                    _effect.UpdateEffect(processedData);
                }
                catch (Exception ex)
                {
                    testException = ex;
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Assert.Null(testException);
        }

        [Fact]
        [System.STAThread]
        public void UpdateEffect_WithMaxIntensity_ShouldNotThrow()
        {
            // Arrange & Act & Assert
            Exception? testException = null;

            var staThread = new Thread(() =>
            {
                try
                {
                    var monitors = new List<DisplayMonitor>
                    {
                        new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
                    };
                    _effect.Initialize(monitors);

                    var processedData = new ProcessedData(Color.Green, 1.0f, DateTime.UtcNow);
                    _effect.UpdateEffect(processedData);
                }
                catch (Exception ex)
                {
                    testException = ex;
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Assert.Null(testException);
        }

        [Fact]
        public void Show_AfterInitialization_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
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
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
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
        public void Initialize_CalledMultipleTimes_ShouldNotThrow()
        {
            // Arrange
            var monitors1 = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
            };
            var monitors2 = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY2", Name = "Monitor 2", IsPrimary = false },
                new DisplayMonitor { Id = "DISPLAY3", Name = "Monitor 3", IsPrimary = false }
            };

            // Act & Assert
            var exception1 = Record.Exception(() => _effect.Initialize(monitors1));
            var exception2 = Record.Exception(() => _effect.Initialize(monitors2));
            
            Assert.Null(exception1);
            Assert.Null(exception2);
        }

        [Fact]
        public void Dispose_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception = Record.Exception(() => _effect.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_ShouldNotThrow()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "DISPLAY1", Name = "Monitor 1", IsPrimary = false }
            };
            _effect.Initialize(monitors);

            // Act & Assert
            var exception1 = Record.Exception(() => _effect.Dispose());
            var exception2 = Record.Exception(() => _effect.Dispose());
            
            Assert.Null(exception1);
            Assert.Null(exception2);
        }

        public void Dispose()
        {
            _effect?.Dispose();
        }
    }
}
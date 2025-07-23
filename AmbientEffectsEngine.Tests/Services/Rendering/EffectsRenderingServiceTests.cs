using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services;
using AmbientEffectsEngine.Services.Processing;
using AmbientEffectsEngine.Services.Rendering;
using AmbientEffectsEngine.Services.Rendering.Effects;
using Moq;
using Xunit;

namespace AmbientEffectsEngine.Tests.Services.Rendering
{
    public class EffectsRenderingServiceTests : IDisposable
    {
        private readonly Mock<IDataProcessingService> _mockDataProcessingService;
        private readonly Mock<IEffectFactory> _mockEffectFactory;
        private readonly Mock<IMonitorDetectionService> _mockMonitorDetectionService;
        private readonly EffectsRenderingService _service;

        public EffectsRenderingServiceTests()
        {
            _mockDataProcessingService = new Mock<IDataProcessingService>();
            _mockEffectFactory = new Mock<IEffectFactory>();
            _mockMonitorDetectionService = new Mock<IMonitorDetectionService>();
            
            // Setup mock effect factory to return registered effects
            _mockEffectFactory.Setup(f => f.IsEffectRegistered("softglow")).Returns(true);
            _mockEffectFactory.Setup(f => f.CreateEffect("softglow")).Returns(Mock.Of<IEffect>(e => 
                e.EffectId == "softglow" && 
                e.Name == "Soft Glow" && 
                e.Description == "Test effect"));
            
            // Setup mock monitor detection service
            var testMonitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "1", Name = "Primary Monitor", IsPrimary = true },
                new DisplayMonitor { Id = "2", Name = "Secondary Monitor", IsPrimary = false }
            };
            _mockMonitorDetectionService.Setup(m => m.GetConnectedMonitorsAsync())
                .Returns(Task.FromResult<IEnumerable<DisplayMonitor>>(testMonitors));
            
            _service = new EffectsRenderingService(_mockDataProcessingService.Object, _mockEffectFactory.Object, _mockMonitorDetectionService.Object);
        }

        [Fact]
        public void Constructor_WithValidDataProcessingService_ShouldInitialize()
        {
            // Assert
            Assert.NotNull(_service);
            Assert.False(_service.IsRunning);
        }

        [Fact]
        public void Constructor_WithNullDataProcessingService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new EffectsRenderingService(null!, _mockEffectFactory.Object, _mockMonitorDetectionService.Object));
        }

        [Fact]
        public void Constructor_WithNullEffectFactory_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new EffectsRenderingService(_mockDataProcessingService.Object, null!, _mockMonitorDetectionService.Object));
        }

        [Fact]
        public void Constructor_WithNullMonitorDetectionService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new EffectsRenderingService(_mockDataProcessingService.Object, _mockEffectFactory.Object, null!));
        }

        [Fact]
        public void IsRunning_Initially_ShouldBeFalse()
        {
            // Assert
            Assert.False(_service.IsRunning);
        }

        [Fact]
        public void Start_WhenNotRunning_ShouldSetIsRunningToTrue()
        {
            // Act
            _service.Start();

            // Assert
            Assert.True(_service.IsRunning);
        }

        [Fact]
        public void Start_WhenAlreadyRunning_ShouldNotThrow()
        {
            // Arrange
            _service.Start();

            // Act & Assert
            var exception = Record.Exception(() => _service.Start());
            Assert.Null(exception);
            Assert.True(_service.IsRunning);
        }

        [Fact]
        public void Stop_WhenRunning_ShouldSetIsRunningToFalse()
        {
            // Arrange
            _service.Start();
            Assert.True(_service.IsRunning);

            // Act
            _service.Stop();

            // Assert
            Assert.False(_service.IsRunning);
        }

        [Fact]
        public void Stop_WhenNotRunning_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _service.Stop());
            Assert.Null(exception);
            Assert.False(_service.IsRunning);
        }

        [Fact]
        public void StatusChanged_OnStart_ShouldRaiseEvent()
        {
            // Arrange
            var statusChanged = false;
            string? receivedStatus = null;
            _service.StatusChanged += (sender, status) =>
            {
                statusChanged = true;
                receivedStatus = status;
            };

            // Act
            _service.Start();

            // Assert
            Assert.True(statusChanged);
            Assert.Contains("started", receivedStatus, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StatusChanged_OnStop_ShouldRaiseEvent()
        {
            // Arrange
            _service.Start();
            var statusChanged = false;
            string? receivedStatus = null;
            _service.StatusChanged += (sender, status) =>
            {
                statusChanged = true;
                receivedStatus = status;
            };

            // Act
            _service.Stop();

            // Assert
            Assert.True(statusChanged);
            Assert.Contains("stopped", receivedStatus, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ProcessedDataAvailable_WhenRunning_ShouldHandleEvent()
        {
            // Arrange
            _service.Start();
            var processedData = new ProcessedData(Color.Red, 0.5f, DateTime.UtcNow);
            var eventArgs = new ProcessedDataEventArgs(processedData);

            // Act & Assert - Should not throw
            var exception = Record.Exception(() => 
                _mockDataProcessingService.Raise(x => x.ProcessedDataAvailable += null, eventArgs));
            
            Assert.Null(exception);
        }

        [Fact]
        public void ProcessedDataAvailable_WhenNotRunning_ShouldHandleEventSafely()
        {
            // Arrange
            var processedData = new ProcessedData(Color.Blue, 0.8f, DateTime.UtcNow);
            var eventArgs = new ProcessedDataEventArgs(processedData);

            // Act & Assert - Should not throw even when not running
            var exception = Record.Exception(() => 
                _mockDataProcessingService.Raise(x => x.ProcessedDataAvailable += null, eventArgs));
            
            Assert.Null(exception);
        }

        [Fact]
        public void ProcessedDataAvailable_WithValidData_WhenNotRunning_ShouldHandleSafely()
        {
            // Arrange - Service not started
            var processedData = new ProcessedData(Color.Green, 0.3f, DateTime.UtcNow);
            var eventArgs = new ProcessedDataEventArgs(processedData);

            // Act & Assert - Should not throw when service is not running
            var exception = Record.Exception(() => 
                _mockDataProcessingService.Raise(x => x.ProcessedDataAvailable += null, eventArgs));
            
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_ShouldStopServiceAndUnsubscribeEvents()
        {
            // Arrange
            _service.Start();
            Assert.True(_service.IsRunning);

            // Act
            _service.Dispose();

            // Assert
            Assert.False(_service.IsRunning);
            
            // Verify that the service can be disposed multiple times
            var exception = Record.Exception(() => _service.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_WhenNotStarted_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _service.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void SetCurrentEffect_WithValidEffectId_ShouldNotThrow()
        {
            // Arrange
            _mockEffectFactory.Setup(f => f.IsEffectRegistered("generativevisualizer")).Returns(true);
            _mockEffectFactory.Setup(f => f.CreateEffect("generativevisualizer")).Returns(Mock.Of<IEffect>(e => 
                e.EffectId == "generativevisualizer" && 
                e.Name == "Generative Visualizer" && 
                e.Description == "Test visualizer"));

            // Act & Assert
            var exception = Record.Exception(() => _service.SetCurrentEffect("generativevisualizer"));
            Assert.Null(exception);
        }

        [Fact]
        public void SetCurrentEffect_WithSameEffectId_ShouldNotChangeEffect()
        {
            // Arrange - Current effect should be "softglow" by default
            var initialCreateCallCount = 0;
            _mockEffectFactory.Setup(f => f.CreateEffect("softglow"))
                .Returns(() =>
                {
                    initialCreateCallCount++;
                    return Mock.Of<IEffect>(e => e.EffectId == "softglow" && e.Name == "Soft Glow");
                });

            // Start service to create initial effect
            _service.Start();

            // Act - Try to set the same effect
            _service.SetCurrentEffect("softglow");

            // Assert - Should not create a new effect instance
            _mockEffectFactory.Verify(f => f.CreateEffect("softglow"), Times.Once);
        }

        [Fact]
        public void SetCurrentEffect_WithUnregisteredEffect_ShouldThrowInvalidOperationException()
        {
            // Arrange
            _mockEffectFactory.Setup(f => f.IsEffectRegistered("unregistered")).Returns(false);

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => _service.SetCurrentEffect("unregistered"));
            Assert.Contains("unregistered", exception.Message);
            Assert.Contains("not registered", exception.Message);
        }

        [Fact]
        public void SetCurrentEffect_WithNullOrEmpty_ShouldNotChangeEffect()
        {
            // Act & Assert - Should not throw but should also not change
            var exception = Record.Exception(() => _service.SetCurrentEffect(null!));
            Assert.Null(exception);

            exception = Record.Exception(() => _service.SetCurrentEffect(""));
            Assert.Null(exception);

            exception = Record.Exception(() => _service.SetCurrentEffect("   "));
            Assert.Null(exception);
        }

        [Fact]
        public void SetCurrentEffect_WhenServiceRunning_ShouldSwitchEffectWithoutRestart()
        {
            // Arrange
            var mockOldEffect = new Mock<IEffect>();
            mockOldEffect.Setup(e => e.EffectId).Returns("softglow");
            mockOldEffect.Setup(e => e.Name).Returns("Soft Glow");

            var mockNewEffect = new Mock<IEffect>();
            mockNewEffect.Setup(e => e.EffectId).Returns("generativevisualizer");
            mockNewEffect.Setup(e => e.Name).Returns("Generative Visualizer");

            _mockEffectFactory.Setup(f => f.CreateEffect("softglow")).Returns(mockOldEffect.Object);
            _mockEffectFactory.Setup(f => f.IsEffectRegistered("generativevisualizer")).Returns(true);
            _mockEffectFactory.Setup(f => f.CreateEffect("generativevisualizer")).Returns(mockNewEffect.Object);

            // Start service
            _service.Start();
            Assert.True(_service.IsRunning);

            // Act
            _service.SetCurrentEffect("generativevisualizer");

            // Assert - Service should still be running
            Assert.True(_service.IsRunning);

            // Verify old effect was hidden and disposed
            mockOldEffect.Verify(e => e.Hide(), Times.Once);
            mockOldEffect.Verify(e => e.Dispose(), Times.Once);

            // Verify new effect was shown
            mockNewEffect.Verify(e => e.Show(), Times.Once);
        }

        [Fact]
        public void SetCurrentEffect_WhenServiceNotRunning_ShouldSwitchEffectWithoutShowing()
        {
            // Arrange
            var mockOldEffect = new Mock<IEffect>();
            mockOldEffect.Setup(e => e.EffectId).Returns("softglow");
            mockOldEffect.Setup(e => e.Name).Returns("Soft Glow");

            var mockNewEffect = new Mock<IEffect>();
            mockNewEffect.Setup(e => e.EffectId).Returns("generativevisualizer");
            mockNewEffect.Setup(e => e.Name).Returns("Generative Visualizer");

            _mockEffectFactory.Setup(f => f.CreateEffect("softglow")).Returns(mockOldEffect.Object);
            _mockEffectFactory.Setup(f => f.IsEffectRegistered("generativevisualizer")).Returns(true);
            _mockEffectFactory.Setup(f => f.CreateEffect("generativevisualizer")).Returns(mockNewEffect.Object);

            // Don't start service - just initialize the initial effect by switching
            _service.SetCurrentEffect("generativevisualizer");

            // Assert
            Assert.False(_service.IsRunning);

            // Verify new effect was not shown (since service is not running)
            mockNewEffect.Verify(e => e.Show(), Times.Never);
            mockNewEffect.Verify(e => e.Hide(), Times.Never);
        }

        public void Dispose()
        {
            _service?.Dispose();
        }
    }
}
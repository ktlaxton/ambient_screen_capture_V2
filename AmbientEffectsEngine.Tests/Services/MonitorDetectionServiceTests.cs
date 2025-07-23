using System;
using System.Linq;
using System.Threading.Tasks;
using AmbientEffectsEngine.Services;
using Xunit;

namespace AmbientEffectsEngine.Tests.Services
{
    public class MonitorDetectionServiceTests : IDisposable
    {
        private readonly MonitorDetectionService _monitorDetectionService;

        public MonitorDetectionServiceTests()
        {
            _monitorDetectionService = new MonitorDetectionService();
        }

        [Fact]
        public async Task GetConnectedMonitorsAsync_ShouldReturnAtLeastOneMonitor()
        {
            // Act
            var monitors = await _monitorDetectionService.GetConnectedMonitorsAsync();

            // Assert
            Assert.NotNull(monitors);
            Assert.NotEmpty(monitors);
        }

        [Fact]
        public async Task GetConnectedMonitorsAsync_ShouldReturnMonitorsWithRequiredProperties()
        {
            // Act
            var monitors = await _monitorDetectionService.GetConnectedMonitorsAsync();

            // Assert
            foreach (var monitor in monitors)
            {
                Assert.NotNull(monitor);
                Assert.NotNull(monitor.Id);
                Assert.NotEmpty(monitor.Id);
                Assert.NotNull(monitor.Name);
                Assert.NotEmpty(monitor.Name);
                // IsPrimary is a boolean, so no null check needed
            }
        }

        [Fact]
        public async Task GetConnectedMonitorsAsync_ShouldHaveExactlyOnePrimaryMonitor()
        {
            // Act
            var monitors = await _monitorDetectionService.GetConnectedMonitorsAsync();

            // Assert
            var primaryMonitors = monitors.Where(m => m.IsPrimary).ToList();
            Assert.Single(primaryMonitors);
        }

        [Fact]
        public async Task GetConnectedMonitorsAsync_ShouldReturnConsistentResults()
        {
            // Act
            var firstCall = await _monitorDetectionService.GetConnectedMonitorsAsync();
            var secondCall = await _monitorDetectionService.GetConnectedMonitorsAsync();

            // Assert
            Assert.Equal(firstCall.Count(), secondCall.Count());
            
            var firstCallList = firstCall.ToList();
            var secondCallList = secondCall.ToList();
            
            for (int i = 0; i < firstCallList.Count; i++)
            {
                Assert.Equal(firstCallList[i].Id, secondCallList[i].Id);
                Assert.Equal(firstCallList[i].Name, secondCallList[i].Name);
                Assert.Equal(firstCallList[i].IsPrimary, secondCallList[i].IsPrimary);
            }
        }

        [Fact]
        public void StartMonitoring_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _monitorDetectionService.StartMonitoring());
            Assert.Null(exception);
        }

        [Fact]
        public void StopMonitoring_ShouldNotThrow()
        {
            // Arrange
            _monitorDetectionService.StartMonitoring();

            // Act & Assert
            var exception = Record.Exception(() => _monitorDetectionService.StopMonitoring());
            Assert.Null(exception);
        }

        [Fact]
        public void StartMonitoring_CalledMultipleTimes_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() =>
            {
                _monitorDetectionService.StartMonitoring();
                _monitorDetectionService.StartMonitoring();
                _monitorDetectionService.StartMonitoring();
            });
            Assert.Null(exception);
        }

        [Fact]
        public void StopMonitoring_CalledWithoutStart_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _monitorDetectionService.StopMonitoring());
            Assert.Null(exception);
        }

        [Fact]
        public void MonitorConfigurationChanged_EventShouldNotBeNull()
        {
            // This test verifies that the event can be subscribed to without issues
            // Act & Assert
            var exception = Record.Exception(() =>
            {
                _monitorDetectionService.MonitorConfigurationChanged += (sender, args) => { };
                _monitorDetectionService.MonitorConfigurationChanged -= (sender, args) => { };
            });
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _monitorDetectionService.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() =>
            {
                _monitorDetectionService.Dispose();
                _monitorDetectionService.Dispose();
                _monitorDetectionService.Dispose();
            });
            Assert.Null(exception);
        }

        public void Dispose()
        {
            _monitorDetectionService?.Dispose();
        }
    }
}
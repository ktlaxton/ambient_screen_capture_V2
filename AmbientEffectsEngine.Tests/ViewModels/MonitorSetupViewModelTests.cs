using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services;
using AmbientEffectsEngine.Services.Rendering;
using AmbientEffectsEngine.ViewModels;
using Moq;
using Xunit;

namespace AmbientEffectsEngine.Tests.ViewModels
{
    public class MonitorSetupViewModelTests : IDisposable
    {
        private readonly Mock<IMonitorDetectionService> _mockMonitorDetectionService;
        private readonly Mock<ISettingsService> _mockSettingsService;
        private readonly Mock<IEffectsRenderingService> _mockEffectsRenderingService;
        private readonly MonitorSetupViewModel _viewModel;

        public MonitorSetupViewModelTests()
        {
            _mockMonitorDetectionService = new Mock<IMonitorDetectionService>();
            _mockSettingsService = new Mock<ISettingsService>();
            _mockEffectsRenderingService = new Mock<IEffectsRenderingService>();
            _viewModel = new MonitorSetupViewModel(_mockMonitorDetectionService.Object, _mockSettingsService.Object, _mockEffectsRenderingService.Object);
        }

        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenMonitorDetectionServiceIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new MonitorSetupViewModel(null!, _mockSettingsService.Object, _mockEffectsRenderingService.Object));
        }

        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenSettingsServiceIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new MonitorSetupViewModel(_mockMonitorDetectionService.Object, null!, _mockEffectsRenderingService.Object));
        }

        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenEffectsRenderingServiceIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new MonitorSetupViewModel(_mockMonitorDetectionService.Object, _mockSettingsService.Object, null!));
        }

        [Fact]
        public void Constructor_ShouldStartMonitoring()
        {
            // Assert
            _mockMonitorDetectionService.Verify(x => x.StartMonitoring(), Times.Once);
        }

        [Fact]
        public async Task LoadMonitorsAsync_ShouldSetIsLoadingFlagCorrectly()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "1", Name = "Monitor 1", IsPrimary = true },
                new DisplayMonitor { Id = "2", Name = "Monitor 2", IsPrimary = false }
            };
            var settings = new ApplicationSettings { TargetMonitorIds = new List<string>() };

            // Use a delay to simulate async operation
            _mockMonitorDetectionService.Setup(x => x.GetConnectedMonitorsAsync())
                .Returns(async () =>
                {
                    await Task.Delay(50);
                    return monitors;
                });
            _mockSettingsService.Setup(x => x.LoadAsync())
                .Returns(Task.FromResult(settings));

            // Create a new viewModel without automatic loading
            var mockMonitorService = new Mock<IMonitorDetectionService>();
            var mockSettings = new Mock<ISettingsService>();
            
            mockMonitorService.Setup(x => x.GetConnectedMonitorsAsync())
                .Returns(async () =>
                {
                    await Task.Delay(50);
                    return monitors;
                });
            mockSettings.Setup(x => x.LoadAsync())
                .Returns(Task.FromResult(settings));

            var mockEffectsService = new Mock<IEffectsRenderingService>();
            var testViewModel = new MonitorSetupViewModel(mockMonitorService.Object, mockSettings.Object, mockEffectsService.Object);
            
            // Wait for constructor's initial load to complete
            await Task.Delay(100);

            // Act
            var loadTask = testViewModel.LoadMonitorsAsync();
            
            // Assert - Should be loading during the operation
            Assert.True(testViewModel.IsLoading);
            
            await loadTask;
            
            // Assert - Should not be loading after completion
            Assert.False(testViewModel.IsLoading);
        }

        [Fact]
        public async Task LoadMonitorsAsync_ShouldPopulateMonitors()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "1", Name = "Monitor 1", IsPrimary = true },
                new DisplayMonitor { Id = "2", Name = "Monitor 2", IsPrimary = false },
                new DisplayMonitor { Id = "3", Name = "Monitor 3", IsPrimary = false }
            };
            var settings = new ApplicationSettings { TargetMonitorIds = new List<string> { "2" } };

            _mockMonitorDetectionService.Setup(x => x.GetConnectedMonitorsAsync())
                .Returns(Task.FromResult<IEnumerable<DisplayMonitor>>(monitors));
            _mockSettingsService.Setup(x => x.LoadAsync())
                .Returns(Task.FromResult(settings));

            // Act
            await _viewModel.LoadMonitorsAsync();

            // Assert
            Assert.Equal(3, _viewModel.Monitors.Count);
            
            var monitor1VM = _viewModel.Monitors.FirstOrDefault(m => m.Monitor.Id == "1");
            var monitor2VM = _viewModel.Monitors.FirstOrDefault(m => m.Monitor.Id == "2");
            var monitor3VM = _viewModel.Monitors.FirstOrDefault(m => m.Monitor.Id == "3");
            
            Assert.NotNull(monitor1VM);
            Assert.NotNull(monitor2VM);
            Assert.NotNull(monitor3VM);
            
            // Primary monitor should not be selected
            Assert.False(monitor1VM.IsSelected);
            // Monitor 2 should be selected based on settings
            Assert.True(monitor2VM.IsSelected);
            // Monitor 3 should not be selected
            Assert.False(monitor3VM.IsSelected);
        }

        [Fact]
        public async Task LoadMonitorsAsync_ShouldNotSelectPrimaryMonitorEvenIfInSettings()
        {
            // Arrange
            var monitors = new List<DisplayMonitor>
            {
                new DisplayMonitor { Id = "1", Name = "Monitor 1", IsPrimary = true },
                new DisplayMonitor { Id = "2", Name = "Monitor 2", IsPrimary = false }
            };
            // Settings incorrectly include primary monitor
            var settings = new ApplicationSettings { TargetMonitorIds = new List<string> { "1", "2" } };

            _mockMonitorDetectionService.Setup(x => x.GetConnectedMonitorsAsync())
                .Returns(Task.FromResult<IEnumerable<DisplayMonitor>>(monitors));
            _mockSettingsService.Setup(x => x.LoadAsync())
                .Returns(Task.FromResult(settings));

            // Act
            await _viewModel.LoadMonitorsAsync();

            // Assert
            var primaryMonitorVM = _viewModel.Monitors.FirstOrDefault(m => m.Monitor.IsPrimary);
            var secondaryMonitorVM = _viewModel.Monitors.FirstOrDefault(m => !m.Monitor.IsPrimary);
            
            Assert.NotNull(primaryMonitorVM);
            Assert.NotNull(secondaryMonitorVM);
            
            // Primary monitor should never be selected
            Assert.False(primaryMonitorVM.IsSelected);
            // Secondary monitor should be selected
            Assert.True(secondaryMonitorVM.IsSelected);
        }

        [Fact]
        public async Task LoadMonitorsAsync_ShouldHandleExceptionGracefully()
        {
            // Arrange
            _mockMonitorDetectionService.Setup(x => x.GetConnectedMonitorsAsync())
                .ThrowsAsync(new InvalidOperationException("Test exception"));

            // Act & Assert - Should not throw
            var exception = await Record.ExceptionAsync(() => _viewModel.LoadMonitorsAsync());
            Assert.Null(exception);
            Assert.False(_viewModel.IsLoading);
        }

        [Fact]
        public void Monitors_ShouldBeInitializedAsEmptyCollection()
        {
            // Assert
            Assert.NotNull(_viewModel.Monitors);
            Assert.Empty(_viewModel.Monitors);
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }

    public class MonitorSelectionViewModelTests
    {
        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenMonitorIsNull()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new MonitorSelectionViewModel(null!));
        }

        [Fact]
        public void Constructor_ShouldInitializeWithMonitor()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = false };

            // Act
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Assert
            Assert.Equal(monitor, viewModel.Monitor);
            Assert.False(viewModel.IsSelected);
        }

        [Fact]
        public void CanSelect_ShouldReturnTrue_WhenMonitorIsNotPrimary()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = false };
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Act & Assert
            Assert.True(viewModel.CanSelect);
        }

        [Fact]
        public void CanSelect_ShouldReturnFalse_WhenMonitorIsPrimary()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = true };
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Act & Assert
            Assert.False(viewModel.CanSelect);
        }

        [Fact]
        public void IsSelected_ShouldNotChange_WhenMonitorIsPrimary()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = true };
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Act
            viewModel.IsSelected = true;

            // Assert
            Assert.False(viewModel.IsSelected);
        }

        [Fact]
        public void IsSelected_ShouldChange_WhenMonitorIsNotPrimary()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = false };
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Act
            viewModel.IsSelected = true;

            // Assert
            Assert.True(viewModel.IsSelected);
        }

        [Fact]
        public void DisplayName_ShouldIncludePrimaryText_WhenMonitorIsPrimary()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = true };
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Act & Assert
            Assert.Equal("Test Monitor (Primary)", viewModel.DisplayName);
        }

        [Fact]
        public void DisplayName_ShouldNotIncludePrimaryText_WhenMonitorIsNotPrimary()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = false };
            var viewModel = new MonitorSelectionViewModel(monitor);

            // Act & Assert
            Assert.Equal("Test Monitor", viewModel.DisplayName);
        }

        [Fact]
        public void IsSelected_ShouldRaisePropertyChanged()
        {
            // Arrange
            var monitor = new DisplayMonitor { Id = "1", Name = "Test Monitor", IsPrimary = false };
            var viewModel = new MonitorSelectionViewModel(monitor);
            var propertyChangedRaised = false;

            viewModel.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(MonitorSelectionViewModel.IsSelected))
                    propertyChangedRaised = true;
            };

            // Act
            viewModel.IsSelected = true;

            // Assert
            Assert.True(propertyChangedRaised);
        }
    }
}
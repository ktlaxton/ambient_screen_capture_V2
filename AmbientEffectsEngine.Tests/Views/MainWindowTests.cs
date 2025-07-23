using System;
using System.Threading;
using Xunit;
using AmbientEffectsEngine.Views;
using AmbientEffectsEngine.ViewModels;
using Moq;

namespace AmbientEffectsEngine.Tests.Views
{
    public class MainWindowTests
    {
        [Fact]
        [System.STAThread]
        public void Constructor_InitializesSuccessfully()
        {
            // Arrange & Act
            MainWindow? window = null;
            Exception? exception = null;

            var thread = new Thread(() =>
            {
                try
                {
                    var viewModel = new MainViewModel();
                    var mockServiceProvider = new Mock<IServiceProvider>();
                    window = new MainWindow(viewModel, mockServiceProvider.Object);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            // Assert
            if (exception != null)
                throw exception;

            Assert.NotNull(window);
        }

        [Fact]
        [System.STAThread]
        public void Constructor_SetsDataContextToMainViewModel()
        {
            // Arrange & Act
            MainViewModel? viewModel = null;
            Exception? exception = null;

            var thread = new Thread(() =>
            {
                try
                {
                    var testViewModel = new MainViewModel();
                    var mockServiceProvider = new Mock<IServiceProvider>();
                    var window = new MainWindow(testViewModel, mockServiceProvider.Object);
                    viewModel = window.DataContext as MainViewModel;
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            // Assert
            if (exception != null)
                throw exception;

            Assert.NotNull(viewModel);
            Assert.IsType<MainViewModel>(viewModel);
        }

        [Fact]
        [System.STAThread]
        public void DataContext_HasCorrectInitialValues()
        {
            // Arrange & Act
            MainViewModel? viewModel = null;
            Exception? exception = null;

            var thread = new Thread(() =>
            {
                try
                {
                    var testViewModel = new MainViewModel();
                    var mockServiceProvider = new Mock<IServiceProvider>();
                    var window = new MainWindow(testViewModel, mockServiceProvider.Object);
                    viewModel = window.DataContext as MainViewModel;
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            // Assert
            if (exception != null)
                throw exception;

            Assert.NotNull(viewModel);
            Assert.False(viewModel.IsEnabled);
            Assert.Equal(0.5f, viewModel.AudioSensitivity);
            Assert.NotNull(viewModel.SelectedEffect);
            Assert.Equal("softglow", viewModel.SelectedEffect.Id);
        }

        [Fact]
        public void MainViewModel_CanBeInstantiatedDirectly()
        {
            // This test validates that the ViewModel works independently of UI threading
            
            // Act
            var viewModel = new MainViewModel();

            // Assert
            Assert.NotNull(viewModel);
            Assert.False(viewModel.IsEnabled);
            Assert.Equal(0.5f, viewModel.AudioSensitivity);
            Assert.NotNull(viewModel.AvailableEffects);
            Assert.Equal(2, viewModel.AvailableEffects.Count);
        }

        [Fact]
        public void MainViewModel_PropertyBindings_WorkCorrectly()
        {
            // This test validates the ViewModel logic that the UI will bind to
            
            // Arrange
            var viewModel = new MainViewModel();
            var propertyChanged = false;
            viewModel.PropertyChanged += (s, e) => propertyChanged = true;

            // Act
            viewModel.IsEnabled = true;

            // Assert
            Assert.True(viewModel.IsEnabled);
            Assert.True(propertyChanged);
        }
    }
}
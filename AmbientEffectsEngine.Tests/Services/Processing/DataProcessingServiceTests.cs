using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services.Processing;
using AmbientEffectsEngine.Services.Capture;

namespace AmbientEffectsEngine.Tests.Services.Processing
{
    public class DataProcessingServiceTests : IDisposable
    {
        private readonly Mock<IScreenCaptureService> _mockScreenCapture;
        private readonly Mock<IAudioCaptureService> _mockAudioCapture;
        private readonly DataProcessingService _service;

        public DataProcessingServiceTests()
        {
            _mockScreenCapture = new Mock<IScreenCaptureService>();
            _mockAudioCapture = new Mock<IAudioCaptureService>();
            _service = new DataProcessingService(_mockScreenCapture.Object, _mockAudioCapture.Object);
        }

        [Fact]
        public void Constructor_WithValidDependencies_ShouldInitializeSuccessfully()
        {
            // Act & Assert
            Assert.NotNull(_service);
            Assert.False(_service.IsProcessing);
        }

        [Fact]
        public void Constructor_WithNullScreenCapture_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new DataProcessingService(null!, _mockAudioCapture.Object));
        }

        [Fact]
        public void Constructor_WithNullAudioCapture_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new DataProcessingService(_mockScreenCapture.Object, null!));
        }

        [Fact]
        public void IsProcessing_InitialState_ShouldBeFalse()
        {
            // Assert
            Assert.False(_service.IsProcessing);
        }

        [Fact]
        public void Start_ShouldSetIsProcessingToTrue()
        {
            // Act
            _service.Start();
            
            // Assert
            Assert.True(_service.IsProcessing);
        }

        [Fact]
        public void Start_WhenAlreadyStarted_ShouldNotThrow()
        {
            // Arrange
            _service.Start();
            
            // Act & Assert
            var exception = Record.Exception(() => _service.Start());
            Assert.Null(exception);
            Assert.True(_service.IsProcessing);
        }

        [Fact]
        public void Stop_ShouldSetIsProcessingToFalse()
        {
            // Arrange
            _service.Start();
            
            // Act
            _service.Stop();
            
            // Assert
            Assert.False(_service.IsProcessing);
        }

        [Fact]
        public void Stop_WhenNotStarted_ShouldNotThrow()
        {
            // Act & Assert
            var exception = Record.Exception(() => _service.Stop());
            Assert.Null(exception);
            Assert.False(_service.IsProcessing);
        }

        [Fact]
        public async Task OnFrameCaptured_ShouldFireProcessedDataAvailableEvent()
        {
            // Arrange
            var eventFired = false;
            ProcessedDataEventArgs? capturedEventArgs = null;
            var tcs = new TaskCompletionSource<bool>();

            _service.ProcessedDataAvailable += (sender, e) =>
            {
                eventFired = true;
                capturedEventArgs = e;
                tcs.SetResult(true);
            };

            _service.Start();

            // Act
            var frameEventArgs = new ScreenCaptureFrameEventArgs(new object(), 1920, 1080, DateTime.UtcNow);
            _mockScreenCapture.Raise(s => s.FrameCaptured += null, _mockScreenCapture.Object, frameEventArgs);

            // Wait for async processing
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(eventFired);
            Assert.NotNull(capturedEventArgs);
            Assert.NotNull(capturedEventArgs.Data);
            Assert.True(capturedEventArgs.Data.DominantColor != Color.Empty);
        }

        [Fact]
        public async Task OnAudioDataAvailable_ShouldFireProcessedDataAvailableEvent()
        {
            // Arrange
            var eventFired = false;
            ProcessedDataEventArgs? capturedEventArgs = null;
            var tcs = new TaskCompletionSource<bool>();

            _service.ProcessedDataAvailable += (sender, e) =>
            {
                eventFired = true;
                capturedEventArgs = e;
                tcs.SetResult(true);
            };

            _service.Start();

            // Act
            var audioEventArgs = new AudioDataEventArgs
            {
                VolumeLevel = 0.5f,
                Timestamp = DateTime.UtcNow
            };
            _mockAudioCapture.Raise(a => a.AudioDataAvailable += null, _mockAudioCapture.Object, audioEventArgs);

            // Wait for async processing
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(eventFired);
            Assert.NotNull(capturedEventArgs);
            Assert.NotNull(capturedEventArgs.Data);
            Assert.Equal(0.5f, capturedEventArgs.Data.AudioIntensity);
        }

        [Fact]
        public void ProcessedDataEventArgs_Constructor_WithValidData_ShouldInitialize()
        {
            // Arrange
            var processedData = new ProcessedData(Color.Red, 0.7f, DateTime.UtcNow);
            
            // Act
            var eventArgs = new ProcessedDataEventArgs(processedData);
            
            // Assert
            Assert.Equal(processedData, eventArgs.Data);
        }

        [Fact]
        public void ProcessedDataEventArgs_Constructor_WithNullData_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new ProcessedDataEventArgs(null!));
        }

        [Fact]
        public void ProcessedData_AudioIntensityPercent_ShouldReturnCorrectScale()
        {
            // Arrange & Act
            var processedData = new ProcessedData(Color.Red, 0.5f, DateTime.UtcNow);
            
            // Assert
            Assert.Equal(50.0f, processedData.AudioIntensityPercent);
        }

        [Fact]
        public void Start_AfterDispose_ShouldThrowObjectDisposedException()
        {
            // Arrange
            _service.Dispose();
            
            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => _service.Start());
        }

        [Fact]
        public void AudioIntensity_ShouldBeClampedToValidRange()
        {
            // Arrange
            var eventFired = false;
            ProcessedDataEventArgs? capturedEventArgs = null;
            var tcs = new TaskCompletionSource<bool>();

            _service.ProcessedDataAvailable += (sender, e) =>
            {
                eventFired = true;
                capturedEventArgs = e;
                tcs.SetResult(true);
            };

            _service.Start();

            // Act - test with value above 1.0
            var audioEventArgs = new AudioDataEventArgs
            {
                VolumeLevel = 2.5f, // Above maximum
                Timestamp = DateTime.UtcNow
            };
            _mockAudioCapture.Raise(a => a.AudioDataAvailable += null, _mockAudioCapture.Object, audioEventArgs);

            // Wait and Assert
            var completed = tcs.Task.Wait(TimeSpan.FromSeconds(5));
            Assert.True(completed);
            Assert.True(eventFired);
            Assert.NotNull(capturedEventArgs);
            Assert.True(capturedEventArgs.Data.AudioIntensity <= 1.0f);
            Assert.True(capturedEventArgs.Data.AudioIntensity >= 0.0f);
        }

        public void Dispose()
        {
            _service?.Dispose();
        }
    }
}
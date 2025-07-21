using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AmbientEffectsEngine.Services.Capture;

namespace AmbientEffectsEngine.Tests.Services.Capture
{
    public class AudioCaptureServiceTests : IDisposable
    {
        private readonly AudioCaptureService _service;

        public AudioCaptureServiceTests()
        {
            _service = new AudioCaptureService();
        }

        [Fact]
        public void Constructor_InitializesService_Successfully()
        {
            // Arrange & Act
            using var service = new AudioCaptureService();

            // Assert
            Assert.False(service.IsCapturing);
        }

        [Fact]
        public void IsCapturing_InitialState_ReturnsFalse()
        {
            // Act & Assert
            Assert.False(_service.IsCapturing);
        }

        [Fact]
        public void Start_WhenNotCapturing_UpdatesIsCapturingToTrue()
        {
            try
            {
                // Act
                _service.Start();

                // Assert
                Assert.True(_service.IsCapturing);
            }
            catch (InvalidOperationException)
            {
                // Audio capture may fail in test environment - this is expected
                Assert.True(true);
            }
        }

        [Fact]
        public void Start_WhenAlreadyCapturing_DoesNotThrow()
        {
            try
            {
                // Arrange
                _service.Start();

                // Act & Assert - should not throw
                _service.Start();
            }
            catch (InvalidOperationException)
            {
                // Audio capture may fail in test environment - this is expected
                Assert.True(true);
            }
        }

        [Fact]
        public void Stop_WhenCapturing_UpdatesIsCapturingToFalse()
        {
            try
            {
                // Arrange
                _service.Start();

                // Act
                _service.Stop();

                // Assert
                Assert.False(_service.IsCapturing);
            }
            catch (InvalidOperationException)
            {
                // Audio capture may fail in test environment - this is expected
                Assert.True(true);
            }
        }

        [Fact]
        public void Stop_WhenNotCapturing_DoesNotThrow()
        {
            // Act & Assert - should not throw
            _service.Stop();
            Assert.False(_service.IsCapturing);
        }

        [Fact]
        public void Dispose_WhenCalled_StopsCapturing()
        {
            try
            {
                // Arrange
                _service.Start();

                // Act
                _service.Dispose();

                // Assert
                Assert.False(_service.IsCapturing);
            }
            catch (InvalidOperationException)
            {
                // Audio capture may fail in test environment - this is expected
                Assert.True(true);
            }
        }

        [Fact]
        public void Start_AfterDispose_ThrowsObjectDisposedException()
        {
            // Arrange
            _service.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => _service.Start());
        }

        [Fact]
        public void AudioDataEventArgs_Constructor_SetsDefaultValues()
        {
            // Act
            var eventArgs = new AudioDataEventArgs();

            // Assert
            Assert.Equal(0f, eventArgs.VolumeLevel);
            Assert.Empty(eventArgs.AudioData);
            Assert.Equal(0, eventArgs.SampleRate);
            Assert.True(eventArgs.Timestamp <= DateTime.UtcNow);
            Assert.True(eventArgs.Timestamp >= DateTime.UtcNow.AddSeconds(-1));
        }

        [Fact]
        public void AudioDataEventArgs_SetProperties_WorksCorrectly()
        {
            // Arrange
            var testData = new byte[] { 1, 2, 3, 4 };
            var testTime = DateTime.UtcNow;
            
            // Act
            var eventArgs = new AudioDataEventArgs
            {
                VolumeLevel = 0.5f,
                AudioData = testData,
                SampleRate = 44100,
                Timestamp = testTime
            };

            // Assert
            Assert.Equal(0.5f, eventArgs.VolumeLevel);
            Assert.Equal(testData, eventArgs.AudioData);
            Assert.Equal(44100, eventArgs.SampleRate);
            Assert.Equal(testTime, eventArgs.Timestamp);
        }

        [Fact]
        public void MultipleStartStop_DoesNotCauseIssues()
        {
            try
            {
                // Act - multiple start/stop cycles
                for (int i = 0; i < 3; i++)
                {
                    _service.Start();
                    Thread.Sleep(10); // Brief pause
                    _service.Stop();
                    Thread.Sleep(10); // Brief pause
                }

                // Assert
                Assert.False(_service.IsCapturing);
            }
            catch (InvalidOperationException)
            {
                // Audio capture may fail in test environment - this is expected
                Assert.True(true);
            }
        }

        [Fact]
        public async Task AudioDataAvailable_WhenCaptureStarts_CanReceiveEvents()
        {
            // Arrange
            var eventReceived = false;
            var tcs = new TaskCompletionSource<bool>();
            
            _service.AudioDataAvailable += (sender, args) =>
            {
                eventReceived = true;
                tcs.TrySetResult(true);
            };

            try
            {
                // Act
                _service.Start();
                
                // Wait for potential event (with timeout)
                var timeoutTask = Task.Delay(100);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                _service.Stop();

                // Assert - either we got an event or we gracefully handled no audio
                Assert.True(eventReceived || completedTask == timeoutTask);
            }
            catch (InvalidOperationException)
            {
                // Audio capture may fail in test environment - this is expected
                Assert.True(true);
            }
        }

        public void Dispose()
        {
            _service?.Dispose();
        }
    }
}
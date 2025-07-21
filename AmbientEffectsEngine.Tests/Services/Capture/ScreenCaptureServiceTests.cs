using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using AmbientEffectsEngine.Services.Capture;

namespace AmbientEffectsEngine.Tests.Services.Capture
{
    public class ScreenCaptureServiceTests : IDisposable
    {
        private readonly ScreenCaptureService _screenCaptureService;

        public ScreenCaptureServiceTests()
        {
            _screenCaptureService = new ScreenCaptureService();
        }

        [Fact]
        public void IsCapturing_InitialState_ShouldBeFalse()
        {
            Assert.False(_screenCaptureService.IsCapturing);
        }

        [Fact]
        public void Start_ShouldSetIsCaptturingToTrue()
        {
            _screenCaptureService.Start();
            
            // Allow some time for async initialization
            Thread.Sleep(100);
            
            Assert.True(_screenCaptureService.IsCapturing);
        }

        [Fact]
        public void Stop_AfterStart_ShouldSetIsCapturingToFalse()
        {
            _screenCaptureService.Start();
            Thread.Sleep(100); // Allow async start
            _screenCaptureService.Stop();
            
            Assert.False(_screenCaptureService.IsCapturing);
        }

        [Fact]
        public void Start_WhenAlreadyCapturing_ShouldNotThrow()
        {
            _screenCaptureService.Start();
            Thread.Sleep(100); // Allow async start
            
            var exception = Record.Exception(() => _screenCaptureService.Start());
            
            Assert.Null(exception);
            Assert.True(_screenCaptureService.IsCapturing);
        }

        [Fact]
        public void Stop_WhenNotCapturing_ShouldNotThrow()
        {
            var exception = Record.Exception(() => _screenCaptureService.Stop());
            
            Assert.Null(exception);
            Assert.False(_screenCaptureService.IsCapturing);
        }

        [Fact]
        public async Task FrameCaptured_WhenStarted_ShouldFireEventOrError()
        {
            ScreenCaptureFrameEventArgs capturedFrame = null;
            string errorMessage = null;
            var eventReceived = new ManualResetEventSlim(false);

            _screenCaptureService.FrameCaptured += (sender, args) =>
            {
                capturedFrame = args;
                eventReceived.Set();
            };

            _screenCaptureService.CaptureError += (sender, message) =>
            {
                errorMessage = message;
                eventReceived.Set();
            };

            _screenCaptureService.Start();
            
            // Wait for either frame capture or error
            var eventFired = eventReceived.Wait(TimeSpan.FromSeconds(5));
            
            // Either we get a frame or an error (depending on system capabilities)
            Assert.True(eventFired, "Either frame capture or error event should fire within timeout");
            
            if (capturedFrame != null)
            {
                // If we got a frame, validate its properties
                Assert.NotNull(capturedFrame.Surface);
                Assert.True(capturedFrame.Width > 0);
                Assert.True(capturedFrame.Height > 0);
                Assert.True(capturedFrame.Timestamp <= DateTime.UtcNow);
            }
            else if (errorMessage != null)
            {
                // If we got an error, that's acceptable in test environment
                Assert.NotNull(errorMessage);
                Assert.NotEmpty(errorMessage);
            }
        }

        [Fact]
        public void CaptureError_EventCanBeSubscribed()
        {
            string errorMessage = null;
            
            _screenCaptureService.CaptureError += (sender, message) =>
            {
                errorMessage = message;
            };

            // Verify the event can be subscribed to
            Assert.NotNull(_screenCaptureService);
        }

        [Fact]
        public void Dispose_ShouldStopCapture()
        {
            _screenCaptureService.Start();
            Thread.Sleep(100); // Allow async start
            
            _screenCaptureService.Dispose();
            
            Assert.False(_screenCaptureService.IsCapturing);
        }

        [Fact]
        public void ScreenCaptureFrameEventArgs_Constructor_ShouldSetPropertiesCorrectly()
        {
            var mockSurface = new object();
            var width = 1920;
            var height = 1080;
            var timestamp = DateTime.UtcNow;

            var eventArgs = new ScreenCaptureFrameEventArgs(mockSurface, width, height, timestamp);

            Assert.Equal(mockSurface, eventArgs.Surface);
            Assert.Equal(width, eventArgs.Width);
            Assert.Equal(height, eventArgs.Height);
            Assert.Equal(timestamp, eventArgs.Timestamp);
        }

        [Fact]
        public void Service_ShouldImplementIDisposable()
        {
            Assert.True(_screenCaptureService is IDisposable);
        }

        [Fact]
        public void Service_ShouldImplementIScreenCaptureService()
        {
            Assert.True(_screenCaptureService is IScreenCaptureService);
        }

        public void Dispose()
        {
            _screenCaptureService?.Dispose();
        }
    }
}
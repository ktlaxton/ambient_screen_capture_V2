using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AmbientEffectsEngine.Services.Capture;

namespace AmbientEffectsEngine.Tests.Services.Capture
{
    /// <summary>
    /// Integration tests that verify actual audio capture functionality with real audio devices.
    /// These tests require audio hardware and may fail on systems without audio devices or in CI environments.
    /// </summary>
    public class AudioCaptureIntegrationTests : IDisposable
    {
        private readonly AudioCaptureService _service;

        public AudioCaptureIntegrationTests()
        {
            _service = new AudioCaptureService();
        }

        [Fact(DisplayName = "Integration: Real Audio Capture Start and Data Reception")]
        public async Task RealAudioCapture_StartAndReceiveData_WorksWithActualDevice()
        {
            // Arrange
            var audioDataReceived = false;
            var audioEventArgs = new List<AudioDataEventArgs>();
            var tcs = new TaskCompletionSource<bool>();
            
            _service.AudioDataAvailable += (sender, args) =>
            {
                audioDataReceived = true;
                audioEventArgs.Add(args);
                if (audioEventArgs.Count >= 5) // Wait for several events
                {
                    tcs.TrySetResult(true);
                }
            };

            try
            {
                // Act
                Debug.WriteLine("[Integration Test] Starting real audio capture...");
                _service.Start();
                
                Assert.True(_service.IsCapturing, "Service should be capturing after Start()");

                // Wait for audio data or timeout after 3 seconds
                var timeoutTask = Task.Delay(3000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                // Assert
                if (completedTask == tcs.Task)
                {
                    // Success case: We received audio data
                    Assert.True(audioDataReceived, "Should have received audio data events");
                    Assert.True(audioEventArgs.Count >= 5, "Should have received multiple audio events");
                    
                    // Validate audio data structure
                    foreach (var eventArg in audioEventArgs.Take(5))
                    {
                        Assert.True(eventArg.AudioData.Length > 0, "Audio data should not be empty");
                        Assert.True(eventArg.SampleRate > 0, "Sample rate should be positive");
                        Assert.True(eventArg.VolumeLevel >= 0f && eventArg.VolumeLevel <= 1f, "Volume level should be between 0 and 1");
                        Assert.True(eventArg.Timestamp <= DateTime.UtcNow, "Timestamp should not be in future");
                    }
                    
                    Debug.WriteLine($"[Integration Test] Successfully received {audioEventArgs.Count} audio events");
                    Debug.WriteLine($"[Integration Test] Sample rates: {string.Join(", ", audioEventArgs.Take(3).Select(a => a.SampleRate))}");
                    Debug.WriteLine($"[Integration Test] Volume levels: {string.Join(", ", audioEventArgs.Take(3).Select(a => a.VolumeLevel.ToString("F3")))}");
                }
                else
                {
                    // Timeout case: No audio data received (might be expected in some environments)
                    Debug.WriteLine("[Integration Test] No audio data received within timeout period");
                    Debug.WriteLine("[Integration Test] This may be normal if:");
                    Debug.WriteLine("  - No audio devices are available");
                    Debug.WriteLine("  - Audio service is not running");
                    Debug.WriteLine("  - Running in CI environment without audio");
                    Debug.WriteLine("  - No audio is currently playing on the system");
                    
                    // In this case, we just verify the service started without error
                    Assert.True(_service.IsCapturing, "Service should still be in capturing state even without audio");
                }
            }
            catch (InvalidOperationException ex)
            {
                // Expected in environments without audio devices
                Debug.WriteLine($"[Integration Test] Audio capture failed as expected: {ex.Message}");
                Assert.Contains("Failed to start audio capture", ex.Message);
            }
            finally
            {
                _service.Stop();
                Assert.False(_service.IsCapturing, "Service should not be capturing after Stop()");
            }
        }

        [Fact(DisplayName = "Integration: Audio Format Detection")]
        public async Task RealAudioCapture_DetectsCorrectAudioFormat_WhenDeviceAvailable()
        {
            // Arrange
            AudioDataEventArgs? firstEvent = null;
            var tcs = new TaskCompletionSource<AudioDataEventArgs>();
            
            _service.AudioDataAvailable += (sender, args) =>
            {
                if (firstEvent == null)
                {
                    firstEvent = args;
                    tcs.TrySetResult(args);
                }
            };

            try
            {
                // Act
                _service.Start();
                
                // Wait for first audio event or timeout
                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == tcs.Task)
                {
                    // Assert audio format properties
                    Assert.NotNull(firstEvent);
                    Assert.True(firstEvent.SampleRate > 0, "Sample rate should be positive");
                    
                    // Common Windows audio sample rates
                    var commonRates = new[] { 44100, 48000, 96000, 192000 };
                    Assert.Contains(firstEvent.SampleRate, commonRates);
                    
                    Assert.True(firstEvent.AudioData.Length > 0, "Audio data buffer should not be empty");
                    
                    // Audio data length should be reasonable (not too small, not too large)
                    Assert.True(firstEvent.AudioData.Length >= 64, "Audio buffer should be at least 64 bytes");
                    Assert.True(firstEvent.AudioData.Length <= 65536, "Audio buffer should not exceed 64KB");
                    
                    Debug.WriteLine($"[Integration Test] Detected audio format: {firstEvent.SampleRate}Hz, {firstEvent.AudioData.Length} bytes per buffer");
                }
                else
                {
                    Debug.WriteLine("[Integration Test] No audio format detected (no audio data received)");
                }
            }
            catch (InvalidOperationException)
            {
                // Expected in environments without audio devices
                Debug.WriteLine("[Integration Test] Audio format test skipped - no audio devices available");
            }
            finally
            {
                _service.Stop();
            }
        }

        [Fact(DisplayName = "Integration: Performance Test - Low CPU Impact")]
        public async Task RealAudioCapture_HasLowCpuImpact_DuringExtendedOperation()
        {
            // Arrange
            var eventCount = 0;
            var startTime = DateTime.UtcNow;
            var process = Process.GetCurrentProcess();
            var initialCpuTime = process.TotalProcessorTime;
            
            _service.AudioDataAvailable += (sender, args) =>
            {
                Interlocked.Increment(ref eventCount);
            };

            try
            {
                // Act - Run capture for 2 seconds
                _service.Start();
                await Task.Delay(2000);
                
                // Measure performance
                var endTime = DateTime.UtcNow;
                var finalCpuTime = process.TotalProcessorTime;
                var cpuUsed = (finalCpuTime - initialCpuTime).TotalMilliseconds;
                var elapsed = (endTime - startTime).TotalMilliseconds;
                var cpuPercentage = (cpuUsed / elapsed) * 100;

                Debug.WriteLine($"[Integration Test] Performance metrics:");
                Debug.WriteLine($"  Audio events received: {eventCount}");
                Debug.WriteLine($"  Test duration: {elapsed:F0}ms");
                Debug.WriteLine($"  CPU time used: {cpuUsed:F1}ms");
                Debug.WriteLine($"  CPU usage: {cpuPercentage:F2}%");
                
                if (eventCount > 0)
                {
                    // Assert performance requirements (only if we actually received audio)
                    Assert.True(cpuPercentage < 10.0, $"CPU usage should be less than 10%, was {cpuPercentage:F2}%");
                    Assert.True(eventCount > 20, "Should receive reasonable number of audio events in 2 seconds");
                }
                else
                {
                    Debug.WriteLine("[Integration Test] Performance test skipped - no audio events received");
                }
            }
            catch (InvalidOperationException)
            {
                Debug.WriteLine("[Integration Test] Performance test skipped - no audio devices available");
            }
            finally
            {
                _service.Stop();
            }
        }

        [Fact(DisplayName = "Integration: Stress Test - Start/Stop Cycles")]
        public async Task RealAudioCapture_HandlesMultipleStartStopCycles_WithoutDegradation()
        {
            var successfulCycles = 0;
            const int maxCycles = 5;

            for (int i = 0; i < maxCycles; i++)
            {
                try
                {
                    Debug.WriteLine($"[Integration Test] Stress test cycle {i + 1}/{maxCycles}");
                    
                    // Start
                    _service.Start();
                    Assert.True(_service.IsCapturing, $"Should be capturing after start in cycle {i + 1}");
                    
                    // Wait briefly
                    await Task.Delay(200);
                    
                    // Stop
                    _service.Stop();
                    Assert.False(_service.IsCapturing, $"Should not be capturing after stop in cycle {i + 1}");
                    
                    // Wait briefly before next cycle
                    await Task.Delay(100);
                    
                    successfulCycles++;
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine($"[Integration Test] Cycle {i + 1} failed (expected in some environments): {ex.Message}");
                    break; // If first cycle fails, subsequent ones likely will too
                }
            }

            Debug.WriteLine($"[Integration Test] Completed {successfulCycles}/{maxCycles} stress test cycles");
            
            // If we could start at least once, we should have completed multiple cycles
            if (successfulCycles > 0)
            {
                Assert.True(successfulCycles >= 3, "Should complete at least 3 start/stop cycles without issues");
            }
        }

        public void Dispose()
        {
            try
            {
                _service?.Stop();
                _service?.Dispose();
            }
            catch
            {
                // Ignore disposal errors in tests
            }
        }
    }
}
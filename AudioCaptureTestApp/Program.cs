using System;
using System.Threading;
using AmbientEffectsEngine.Services.Capture;

namespace AudioCaptureTestApp;

class Program
{
    private static AudioCaptureService? _audioService;
    private static readonly CancellationTokenSource _cancellationTokenSource = new();
    private static int _eventCount = 0;
    private static DateTime _lastVolumeReport = DateTime.Now;
    private static float _maxVolume = 0f;
    private static float _minVolume = 1f;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Audio Capture Test Application ===");
        Console.WriteLine("This will test the AudioCaptureService functionality");
        Console.WriteLine();
        
        _audioService = new AudioCaptureService();
        _audioService.AudioDataAvailable += OnAudioDataAvailable;

        Console.WriteLine("Starting audio capture...");
        Console.WriteLine("Make sure you have audio playing on your system to see volume changes.");
        Console.WriteLine("Press 'q' to quit, 's' to show statistics");
        Console.WriteLine();

        try
        {
            _audioService.Start();
            Console.WriteLine($"Audio capture started. IsCapturing: {_audioService.IsCapturing}");
            
            // Run the interactive loop
            await RunInteractiveLoop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting audio capture: {ex.Message}");
            Console.WriteLine($"Exception type: {ex.GetType().Name}");
            Console.WriteLine();
            Console.WriteLine("This might happen if:");
            Console.WriteLine("- No audio devices are available");
            Console.WriteLine("- Audio service is not running");
            Console.WriteLine("- Insufficient permissions");
        }
        finally
        {
            Console.WriteLine("\nStopping audio capture...");
            _audioService?.Stop();
            _audioService?.Dispose();
            Console.WriteLine("Test completed.");
        }
    }

    private static async Task RunInteractiveLoop()
    {
        var startTime = DateTime.Now;
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                
                switch (key.KeyChar)
                {
                    case 'q':
                    case 'Q':
                        Console.WriteLine("Quit requested by user.");
                        _cancellationTokenSource.Cancel();
                        break;
                        
                    case 's':
                    case 'S':
                        ShowStatistics(startTime);
                        break;
                }
            }

            // Show periodic status updates
            if (DateTime.Now - _lastVolumeReport > TimeSpan.FromSeconds(5))
            {
                Console.WriteLine($"Status: {_eventCount} audio events received. Max volume: {_maxVolume:F3}, Min volume: {_minVolume:F3}");
                _lastVolumeReport = DateTime.Now;
            }

            await Task.Delay(100, _cancellationTokenSource.Token);
        }
    }

    private static void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        _eventCount++;
        
        // Track volume statistics
        if (e.VolumeLevel > _maxVolume) _maxVolume = e.VolumeLevel;
        if (e.VolumeLevel < _minVolume) _minVolume = e.VolumeLevel;

        // Show first few events for verification
        if (_eventCount <= 5)
        {
            Console.WriteLine($"Audio Event #{_eventCount}:");
            Console.WriteLine($"  Volume Level: {e.VolumeLevel:F4}");
            Console.WriteLine($"  Data Size: {e.AudioData.Length} bytes");
            Console.WriteLine($"  Sample Rate: {e.SampleRate} Hz");
            Console.WriteLine($"  Timestamp: {e.Timestamp:HH:mm:ss.fff}");
            Console.WriteLine();
        }
        
        // Show significant volume changes
        if (e.VolumeLevel > 0.1f && _eventCount % 50 == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Volume spike detected: {e.VolumeLevel:F3}");
        }
    }

    private static void ShowStatistics(DateTime startTime)
    {
        var elapsed = DateTime.Now - startTime;
        var eventsPerSecond = _eventCount / elapsed.TotalSeconds;
        
        Console.WriteLine();
        Console.WriteLine("=== Audio Capture Statistics ===");
        Console.WriteLine($"Running time: {elapsed:mm\\:ss}");
        Console.WriteLine($"Total audio events: {_eventCount}");
        Console.WriteLine($"Events per second: {eventsPerSecond:F1}");
        Console.WriteLine($"Max volume detected: {_maxVolume:F4}");
        Console.WriteLine($"Min volume detected: {_minVolume:F4}");
        Console.WriteLine($"Is currently capturing: {_audioService?.IsCapturing}");
        Console.WriteLine();
    }
}

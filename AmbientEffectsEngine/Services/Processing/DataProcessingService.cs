using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services.Capture;

namespace AmbientEffectsEngine.Services.Processing
{
    public class DataProcessingService : IDataProcessingService
    {
        private readonly IScreenCaptureService _screenCaptureService;
        private readonly IAudioCaptureService _audioCaptureService;
        private readonly object _lock = new object();
        private volatile bool _isProcessing;
        private bool _disposed;
        private float _audioSensitivity = 0.5f;

        // Smoothing data
        private Color _lastDominantColor = Color.Black;
        private float _lastAudioIntensity = 0.0f;
        private readonly Queue<Color> _colorHistory = new Queue<Color>();
        private readonly Queue<float> _audioHistory = new Queue<float>();
        private const int SMOOTHING_WINDOW = 5;

        public event EventHandler<ProcessedDataEventArgs>? ProcessedDataAvailable;

        public bool IsProcessing
        {
            get
            {
                lock (_lock)
                {
                    return _isProcessing;
                }
            }
        }

        public float AudioSensitivity
        {
            get
            {
                lock (_lock)
                {
                    return _audioSensitivity;
                }
            }
            set
            {
                lock (_lock)
                {
                    _audioSensitivity = Math.Clamp(value, 0.0f, 1.0f);
                }
            }
        }

        public DataProcessingService(IScreenCaptureService screenCaptureService, IAudioCaptureService audioCaptureService)
        {
            _screenCaptureService = screenCaptureService ?? throw new ArgumentNullException(nameof(screenCaptureService));
            _audioCaptureService = audioCaptureService ?? throw new ArgumentNullException(nameof(audioCaptureService));
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DataProcessingService));

                if (_isProcessing)
                    return;

                // Subscribe to capture service events
                _screenCaptureService.FrameCaptured += OnFrameCaptured;
                _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;

                _isProcessing = true;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isProcessing)
                    return;

                // Unsubscribe from capture service events
                _screenCaptureService.FrameCaptured -= OnFrameCaptured;
                _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;

                _isProcessing = false;
            }
        }

        private void OnFrameCaptured(object? sender, ScreenCaptureFrameEventArgs e)
        {
            if (!_isProcessing) return;

            Task.Run(() =>
            {
                try
                {
                    var dominantColor = CalculateDominantColor(e);
                    var smoothedColor = SmoothColor(dominantColor);
                    
                    // Create processed data with current audio intensity
                    var processedData = new ProcessedData(smoothedColor, _lastAudioIntensity, e.Timestamp);
                    
                    // Fire event with minimal latency
                    ProcessedDataAvailable?.Invoke(this, new ProcessedDataEventArgs(processedData));
                }
                catch (Exception ex)
                {
                    // Handle processing errors gracefully - don't crash the service
                    System.Diagnostics.Debug.WriteLine($"[DataProcessing] Frame processing error: {ex.Message}");
                }
            });
        }

        private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
        {
            if (!_isProcessing) return;

            // Process on background thread to avoid blocking audio capture
            Task.Run(() =>
            {
                try
                {
                    var intensity = CalculateAudioIntensity(e.VolumeLevel);
                    var smoothedIntensity = SmoothAudioIntensity(intensity);
                    
                    _lastAudioIntensity = smoothedIntensity;

                    // Create processed data with current color
                    var processedData = new ProcessedData(_lastDominantColor, smoothedIntensity, e.Timestamp);
                    
                    // Fire event with minimal latency
                    ProcessedDataAvailable?.Invoke(this, new ProcessedDataEventArgs(processedData));
                }
                catch (Exception ex)
                {
                    // Handle processing errors gracefully
                    System.Diagnostics.Debug.WriteLine($"[DataProcessing] Audio processing error: {ex.Message}");
                }
            });
        }

        private Color CalculateDominantColor(ScreenCaptureFrameEventArgs frameArgs)
        {
            // For now, return a simple calculation - in a real implementation,
            // this would analyze the actual frame data from frameArgs.Surface
            
            // Mock implementation that generates a color based on frame dimensions and timestamp
            var hash = (frameArgs.Width * frameArgs.Height + frameArgs.Timestamp.Millisecond) % 360;
            var r = (byte)((Math.Sin(hash * 0.1) + 1) * 127.5);
            var g = (byte)((Math.Cos(hash * 0.1) + 1) * 127.5);
            var b = (byte)((Math.Sin(hash * 0.2) + 1) * 127.5);
            
            return Color.FromArgb(255, r, g, b);
        }

        private float CalculateAudioIntensity(float volumeLevel)
        {
            // Normalize volume level to 0.0-1.0 range
            var normalizedVolume = Math.Clamp(volumeLevel, 0.0f, 1.0f);
            
            // Apply audio sensitivity scaling
            float sensitivity;
            lock (_lock)
            {
                sensitivity = _audioSensitivity;
            }
            
            // Scale the intensity based on sensitivity
            // Higher sensitivity = more responsive to quieter sounds
            var scaledIntensity = normalizedVolume * (0.5f + sensitivity);
            
            return Math.Clamp(scaledIntensity, 0.0f, 1.0f);
        }

        private Color SmoothColor(Color newColor)
        {
            lock (_lock)
            {
                _colorHistory.Enqueue(newColor);
                
                if (_colorHistory.Count > SMOOTHING_WINDOW)
                    _colorHistory.Dequeue();

                // Average the colors for smoothing
                var avgR = _colorHistory.Average(c => c.R);
                var avgG = _colorHistory.Average(c => c.G);
                var avgB = _colorHistory.Average(c => c.B);

                var smoothedColor = Color.FromArgb(255, (int)avgR, (int)avgG, (int)avgB);
                _lastDominantColor = smoothedColor;
                
                return smoothedColor;
            }
        }

        private float SmoothAudioIntensity(float newIntensity)
        {
            lock (_lock)
            {
                _audioHistory.Enqueue(newIntensity);
                
                if (_audioHistory.Count > SMOOTHING_WINDOW)
                    _audioHistory.Dequeue();

                // Average for smoothing
                return _audioHistory.Average();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            Stop();
            _disposed = true;
        }
    }
}
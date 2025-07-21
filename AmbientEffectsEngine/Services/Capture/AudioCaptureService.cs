using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wasapi;
using NAudio.Wave;

namespace AmbientEffectsEngine.Services.Capture
{
    public class AudioCaptureService : IAudioCaptureService
    {
        private WasapiLoopbackCapture? _capture;
        private bool _disposed;
        private readonly object _lock = new object();
        private volatile bool _isCapturing;
        private int _audioDataCount;

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
        
        public bool IsCapturing 
        { 
            get 
            { 
                lock (_lock)
                {
                    return _isCapturing && _capture != null;
                }
            } 
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(AudioCaptureService));

                if (_isCapturing || _capture != null)
                    return;

                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    
                    Debug.WriteLine($"[AudioCapture] Starting audio capture - Format: {_capture.WaveFormat}");
                    _capture.StartRecording();
                    _isCapturing = true;
                    _audioDataCount = 0;
                    Debug.WriteLine("[AudioCapture] Audio capture started successfully");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioCapture] Failed to start audio capture: {ex.Message}");
                    _capture?.Dispose();
                    _capture = null;
                    _isCapturing = false;
                    throw new InvalidOperationException("Failed to start audio capture.", ex);
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isCapturing || _capture == null)
                    return;

                try
                {
                    Debug.WriteLine("[AudioCapture] Stopping audio capture");
                    _capture.StopRecording();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioCapture] Exception during stop: {ex.Message}");
                }
                finally
                {
                    _isCapturing = false;
                    Debug.WriteLine($"[AudioCapture] Audio capture stopped. Total data events received: {_audioDataCount}");
                }
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_disposed || e.Buffer == null || e.BytesRecorded == 0)
                return;

            try
            {
                _audioDataCount++;
                var volumeLevel = CalculateVolumeLevel(e.Buffer, e.BytesRecorded);
                var sampleRate = _capture?.WaveFormat?.SampleRate ?? 44100;
                
                var audioData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioData, e.BytesRecorded);

                var eventArgs = new AudioDataEventArgs
                {
                    VolumeLevel = volumeLevel,
                    AudioData = audioData,
                    SampleRate = sampleRate,
                    Timestamp = DateTime.UtcNow
                };

                // Log every 100th data event to avoid spam
                if (_audioDataCount % 100 == 0)
                {
                    Debug.WriteLine($"[AudioCapture] Data #{_audioDataCount}: {e.BytesRecorded} bytes, Volume: {volumeLevel:F3}, Rate: {sampleRate}Hz");
                }

                AudioDataAvailable?.Invoke(this, eventArgs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioCapture] Exception in OnDataAvailable: {ex.Message}");
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lock)
            {
                _isCapturing = false;
                
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.Dispose();
                    _capture = null;
                }
            }

            if (e.Exception != null)
            {
                Debug.WriteLine($"[AudioCapture] Recording stopped with error: {e.Exception.Message}");
            }
            else
            {
                Debug.WriteLine("[AudioCapture] Recording stopped normally");
            }
        }

        private static float CalculateVolumeLevel(byte[] buffer, int bytesRecorded)
        {
            if (bytesRecorded == 0)
                return 0f;

            long sum = 0;
            int sampleCount = bytesRecorded / 4; // 32-bit samples (4 bytes each)
            
            for (int i = 0; i < bytesRecorded - 3; i += 4)
            {
                var sample = BitConverter.ToInt32(buffer, i);
                sum += Math.Abs(sample);
            }

            if (sampleCount == 0)
                return 0f;

            var average = sum / (double)sampleCount;
            var normalizedLevel = (float)(average / int.MaxValue);
            
            return Math.Min(1.0f, normalizedLevel);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                Stop();
                
                lock (_lock)
                {
                    _capture?.Dispose();
                    _capture = null;
                }
            }

            _disposed = true;
        }

        ~AudioCaptureService()
        {
            Dispose(false);
        }
    }
}
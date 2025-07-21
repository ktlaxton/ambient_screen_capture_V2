using System;

namespace AmbientEffectsEngine.Services.Capture
{
    public interface IAudioCaptureService : IDisposable
    {
        event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
        
        bool IsCapturing { get; }
        
        void Start();
        void Stop();
    }
    
    public class AudioDataEventArgs : EventArgs
    {
        public float VolumeLevel { get; set; }
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public int SampleRate { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
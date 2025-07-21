using System;

namespace AmbientEffectsEngine.Services.Capture
{
    public interface IScreenCaptureService : IDisposable
    {
        event EventHandler<ScreenCaptureFrameEventArgs> FrameCaptured;
        event EventHandler<string> CaptureError;
        
        bool IsCapturing { get; }
        
        void Start();
        void Stop();
    }

    public class ScreenCaptureFrameEventArgs : EventArgs
    {
        public object Surface { get; }
        public int Width { get; }
        public int Height { get; }
        public DateTime Timestamp { get; }

        public ScreenCaptureFrameEventArgs(object surface, int width, int height, DateTime timestamp)
        {
            Surface = surface;
            Width = width;
            Height = height;
            Timestamp = timestamp;
        }
    }
}
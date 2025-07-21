using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AmbientEffectsEngine.Services.Capture
{
    public class ScreenCaptureService : IScreenCaptureService
    {
        private System.Threading.Timer _captureTimer;
        private bool _isCapturing;
        private readonly object _lockObject = new object();
        private readonly int _captureIntervalMs = 33; // ~30 FPS

        public event EventHandler<ScreenCaptureFrameEventArgs> FrameCaptured;
        public event EventHandler<string> CaptureError;

        public bool IsCapturing 
        { 
            get 
            { 
                lock (_lockObject) 
                { 
                    return _isCapturing; 
                } 
            } 
        }

        public void Start()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_isCapturing)
                        return;

                    // For now, use GDI+ capture as a working baseline
                    // This will be replaced with Windows.Graphics.Capture when available
                    _captureTimer = new System.Threading.Timer(CaptureFrame, null, 0, _captureIntervalMs);
                    _isCapturing = true;
                }
            }
            catch (Exception ex)
            {
                CaptureError?.Invoke(this, $"Failed to start capture: {ex.Message}");
            }
        }

        public void Stop()
        {
            lock (_lockObject)
            {
                if (!_isCapturing)
                    return;

                _captureTimer?.Dispose();
                _captureTimer = null;
                _isCapturing = false;
            }
        }

        private void CaptureFrame(object state)
        {
            try
            {
                var primaryScreen = Screen.PrimaryScreen;
                var bounds = primaryScreen.Bounds;

                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

                // Create a mock Direct3D surface for compatibility with the interface
                var surface = new MockDirect3DSurface(bitmap);
                
                var eventArgs = new ScreenCaptureFrameEventArgs(
                    surface,
                    bounds.Width,
                    bounds.Height,
                    DateTime.UtcNow);

                FrameCaptured?.Invoke(this, eventArgs);
            }
            catch (Exception ex)
            {
                CaptureError?.Invoke(this, $"Frame capture error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // Temporary mock implementation for development/testing
    public class MockDirect3DSurface : IDisposable
    {
        private readonly byte[] _imageData;
        
        public MockDirect3DSurface(Bitmap bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            _imageData = stream.ToArray();
        }

        public byte[] GetImageData() => _imageData;

        public void Dispose()
        {
            // Mock implementation - nothing to dispose
        }
    }
}
using System;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services.Processing
{
    public interface IDataProcessingService : IDisposable
    {
        event EventHandler<ProcessedDataEventArgs>? ProcessedDataAvailable;
        
        bool IsProcessing { get; }
        float AudioSensitivity { get; set; }
        
        void Start();
        void Stop();
    }
    
    public class ProcessedDataEventArgs : EventArgs
    {
        public ProcessedData Data { get; }
        
        public ProcessedDataEventArgs(ProcessedData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }
    }
}
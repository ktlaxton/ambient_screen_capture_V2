using System;
using System.Collections.Generic;

namespace AmbientEffectsEngine.Services.Rendering
{
    public interface IEffectsRenderingService : IDisposable
    {
        event EventHandler<string>? StatusChanged;
        
        bool IsRunning { get; }
        
        void Start();
        void Stop();
        void SetCurrentEffect(string effectId);
        void SetTargetMonitors(IEnumerable<string> monitorIds);
    }
}
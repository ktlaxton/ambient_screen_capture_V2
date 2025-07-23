using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services
{
    public interface IMonitorDetectionService
    {
        /// <summary>
        /// Gets all connected monitors with their display information
        /// </summary>
        /// <returns>List of DisplayMonitor objects representing connected displays</returns>
        Task<IEnumerable<DisplayMonitor>> GetConnectedMonitorsAsync();
        
        /// <summary>
        /// Event fired when monitor configuration changes (monitors connected/disconnected)
        /// </summary>
        event EventHandler<MonitorConfigurationChangedEventArgs>? MonitorConfigurationChanged;
        
        /// <summary>
        /// Starts monitoring for display configuration changes
        /// </summary>
        void StartMonitoring();
        
        /// <summary>
        /// Stops monitoring for display configuration changes
        /// </summary>
        void StopMonitoring();
    }
    
    public class MonitorConfigurationChangedEventArgs : EventArgs
    {
        public IEnumerable<DisplayMonitor> ConnectedMonitors { get; }
        
        public MonitorConfigurationChangedEventArgs(IEnumerable<DisplayMonitor> connectedMonitors)
        {
            ConnectedMonitors = connectedMonitors ?? throw new ArgumentNullException(nameof(connectedMonitors));
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services.Processing;
using AmbientEffectsEngine.Services.Rendering.Effects;

namespace AmbientEffectsEngine.Services.Rendering
{
    public class EffectsRenderingService : IEffectsRenderingService
    {
        private readonly IDataProcessingService _dataProcessingService;
        private readonly IEffectFactory _effectFactory;
        private readonly IMonitorDetectionService _monitorDetectionService;
        private List<DisplayMonitor> _availableMonitors = new List<DisplayMonitor>();
        private List<string> _targetMonitorIds = new List<string>();
        private IEffect? _currentEffect;
        private string _currentEffectId = "softglow"; // Default effect
        private bool _disposed = false;
        private bool _isRunning = false;

        public event EventHandler<string>? StatusChanged;
        
        public bool IsRunning => _isRunning;

        public EffectsRenderingService(IDataProcessingService dataProcessingService, IEffectFactory effectFactory, IMonitorDetectionService monitorDetectionService)
        {
            _dataProcessingService = dataProcessingService ?? throw new ArgumentNullException(nameof(dataProcessingService));
            _effectFactory = effectFactory ?? throw new ArgumentNullException(nameof(effectFactory));
            _monitorDetectionService = monitorDetectionService ?? throw new ArgumentNullException(nameof(monitorDetectionService));
            
            // Subscribe to data processing events
            _dataProcessingService.ProcessedDataAvailable += OnProcessedDataAvailable;
            
            // Subscribe to monitor configuration changes
            _monitorDetectionService.MonitorConfigurationChanged += OnMonitorConfigurationChanged;
            
            // Initialize monitor detection
            _ = LoadMonitorsAsync();
        }

        public void Start()
        {
            if (_disposed || _isRunning) return;

            try
            {
                InitializeCurrentEffect();
                _currentEffect?.Show();
                
                _isRunning = true;
                OnStatusChanged($"Effects rendering started with {_currentEffect?.Name ?? "unknown"} effect");
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Failed to start effects rendering: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Effects Rendering Error: {ex}");
                throw;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                _currentEffect?.Hide();
                _isRunning = false;
                OnStatusChanged("Effects rendering stopped");
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Error stopping effects rendering: {ex.Message}");
                throw;
            }
        }

        public void SetCurrentEffect(string effectId)
        {
            if (string.IsNullOrWhiteSpace(effectId) || _currentEffectId == effectId) return;

            try
            {
                var wasRunning = _isRunning;
                
                // Stop current effect if running
                if (wasRunning)
                {
                    _currentEffect?.Hide();
                }
                
                // Dispose current effect
                _currentEffect?.Dispose();
                _currentEffect = null;
                
                // Update effect ID
                _currentEffectId = effectId;
                
                // Initialize new effect
                InitializeCurrentEffect();
                
                // Show new effect if we were running
                if (wasRunning)
                {
                    _currentEffect?.Show();
                }
                
                OnStatusChanged($"Switched to {_currentEffect?.Name ?? "unknown"} effect");
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Failed to switch to effect '{effectId}': {ex.Message}");
                throw;
            }
        }

        public void SetTargetMonitors(IEnumerable<string> monitorIds)
        {
            if (monitorIds == null) throw new ArgumentNullException(nameof(monitorIds));

            var newTargetIds = monitorIds.ToList();
            
            // If target monitors haven't changed, no need to update
            if (_targetMonitorIds.SequenceEqual(newTargetIds)) return;

            try
            {
                var wasRunning = _isRunning;
                
                // Stop current effect if running to reinitialize with new monitors
                if (wasRunning)
                {
                    _currentEffect?.Hide();
                }
                
                // Update target monitor IDs
                _targetMonitorIds = newTargetIds;
                
                // Reinitialize effect with new monitors if we have a current effect
                if (_currentEffect != null)
                {
                    _currentEffect.Dispose();
                    _currentEffect = null;
                    InitializeCurrentEffect();
                }
                
                // Restart effect if it was running
                if (wasRunning)
                {
                    _currentEffect?.Show();
                }
                
                var targetCount = GetTargetMonitors().Count();
                OnStatusChanged($"Updated target monitors: {targetCount} monitor(s) selected for effects");
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Failed to update target monitors: {ex.Message}");
                throw;
            }
        }

        private void InitializeCurrentEffect()
        {
            if (!_effectFactory.IsEffectRegistered(_currentEffectId))
            {
                throw new InvalidOperationException($"Effect '{_currentEffectId}' is not registered");
            }

            _currentEffect = _effectFactory.CreateEffect(_currentEffectId);
            
            // Initialize effect with selected target monitors
            var targetMonitors = GetTargetMonitors().ToList();
            _currentEffect.Initialize(targetMonitors);
        }

        private IEnumerable<DisplayMonitor> GetTargetMonitors()
        {
            // If no specific targets are set, default to all secondary monitors
            if (!_targetMonitorIds.Any())
            {
                return _availableMonitors.Where(m => !m.IsPrimary);
            }

            // Return monitors that match the target IDs and are not primary
            return _availableMonitors.Where(m => _targetMonitorIds.Contains(m.Id) && !m.IsPrimary);
        }

        private void OnProcessedDataAvailable(object? sender, ProcessedDataEventArgs e)
        {
            if (_isRunning && e.Data != null && _currentEffect != null)
            {
                _currentEffect.UpdateEffect(e.Data);
            }
        }

        private async Task LoadMonitorsAsync()
        {
            try
            {
                var monitors = await _monitorDetectionService.GetConnectedMonitorsAsync();
                _availableMonitors = monitors.ToList();

                OnStatusChanged($"Detected {_availableMonitors.Count} monitors ({_availableMonitors.Count(m => !m.IsPrimary)} secondary)");
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Failed to load monitors: {ex.Message}");
                // Don't throw here to avoid breaking service initialization
                _availableMonitors.Clear();
            }
        }

        private async void OnMonitorConfigurationChanged(object? sender, MonitorConfigurationChangedEventArgs e)
        {
            try
            {
                // Reload available monitors when configuration changes
                await LoadMonitorsAsync();
                
                // Reinitialize current effect if running to adapt to new monitor configuration
                if (_isRunning && _currentEffect != null)
                {
                    var wasRunning = _isRunning;
                    _currentEffect.Hide();
                    _currentEffect.Dispose();
                    _currentEffect = null;
                    
                    InitializeCurrentEffect();
                    
                    if (wasRunning)
                    {
                        _currentEffect?.Show();
                    }
                }
                
                OnStatusChanged("Monitor configuration changed - effects updated");
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Error handling monitor configuration change: {ex.Message}");
            }
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                
                if (_dataProcessingService != null)
                {
                    _dataProcessingService.ProcessedDataAvailable -= OnProcessedDataAvailable;
                }
                
                if (_monitorDetectionService != null)
                {
                    _monitorDetectionService.MonitorConfigurationChanged -= OnMonitorConfigurationChanged;
                }
                
                _currentEffect?.Dispose();
                _disposed = true;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;
using AmbientEffectsEngine.Services;
using AmbientEffectsEngine.Services.Rendering;

namespace AmbientEffectsEngine.ViewModels
{
    public class MonitorSetupViewModel : INotifyPropertyChanged
    {
        private readonly IMonitorDetectionService _monitorDetectionService;
        private readonly ISettingsService _settingsService;
        private readonly IEffectsRenderingService _effectsRenderingService;
        private bool _isLoading;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<MonitorSelectionViewModel> Monitors { get; }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                }
            }
        }

        public MonitorSetupViewModel(IMonitorDetectionService monitorDetectionService, ISettingsService settingsService, IEffectsRenderingService effectsRenderingService)
        {
            _monitorDetectionService = monitorDetectionService ?? throw new ArgumentNullException(nameof(monitorDetectionService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _effectsRenderingService = effectsRenderingService ?? throw new ArgumentNullException(nameof(effectsRenderingService));
            
            Monitors = new ObservableCollection<MonitorSelectionViewModel>();
            
            // Subscribe to monitor configuration changes
            _monitorDetectionService.MonitorConfigurationChanged += OnMonitorConfigurationChanged;
            
            // Start monitoring for changes
            _monitorDetectionService.StartMonitoring();
            
            // Load monitors and settings
            _ = LoadMonitorsAsync();
        }

        public async Task LoadMonitorsAsync()
        {
            IsLoading = true;
            
            try
            {
                // Get connected monitors
                var connectedMonitors = await _monitorDetectionService.GetConnectedMonitorsAsync();
                
                // Load saved settings to get selected monitor IDs
                var settings = await _settingsService.LoadAsync();
                var selectedMonitorIds = settings.TargetMonitorIds ?? new List<string>();
                
                // Clear existing monitors
                Monitors.Clear();
                
                // Create monitor selection view models
                foreach (var monitor in connectedMonitors)
                {
                    var monitorVM = new MonitorSelectionViewModel(monitor)
                    {
                        IsSelected = selectedMonitorIds.Contains(monitor.Id) && !monitor.IsPrimary
                    };
                    
                    // Subscribe to selection changes
                    monitorVM.PropertyChanged += OnMonitorSelectionChanged;
                    
                    Monitors.Add(monitorVM);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading monitors: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async void OnMonitorSelectionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MonitorSelectionViewModel.IsSelected))
            {
                await SaveSelectionAsync();
            }
        }

        private async Task SaveSelectionAsync()
        {
            try
            {
                var settings = await _settingsService.LoadAsync();
                
                // Get selected monitor IDs (excluding primary monitors)
                var selectedMonitorIds = Monitors
                    .Where(m => m.IsSelected && !m.Monitor.IsPrimary)
                    .Select(m => m.Monitor.Id)
                    .ToList();
                
                settings.TargetMonitorIds = selectedMonitorIds;
                
                await _settingsService.SaveAsync(settings);
                
                // Apply monitor selections to effects rendering service immediately
                _effectsRenderingService.SetTargetMonitors(selectedMonitorIds);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving monitor selection: {ex.Message}");
            }
        }

        private async void OnMonitorConfigurationChanged(object? sender, MonitorConfigurationChangedEventArgs e)
        {
            // Reload monitors when configuration changes
            await LoadMonitorsAsync();
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class MonitorSelectionViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public DisplayMonitor Monitor { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value && !Monitor.IsPrimary)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool CanSelect => !Monitor.IsPrimary;

        public string DisplayName => Monitor.IsPrimary ? $"{Monitor.Name} (Primary)" : Monitor.Name;

        public MonitorSelectionViewModel(DisplayMonitor monitor)
        {
            Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
using System;
using System.Windows;
using System.Windows.Navigation;
using AmbientEffectsEngine.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AmbientEffectsEngine.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(MainViewModel viewModel, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        DataContext = _viewModel;
    }

    private void MonitorSetupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var monitorSetupViewModel = _serviceProvider.GetRequiredService<MonitorSetupViewModel>();
            var monitorSetupPage = new MonitorSetupPage(monitorSetupViewModel);
            
            var monitorSetupWindow = new Window
            {
                Title = "Monitor Setup",
                Content = monitorSetupPage,
                Width = 800,
                Height = 600,
                MinWidth = 600,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            
            monitorSetupWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error opening Monitor Setup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel = null;
        base.OnClosed(e);
    }
}
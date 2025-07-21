using System.Windows;
using AmbientEffectsEngine.ViewModels;

namespace AmbientEffectsEngine.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel = null;
        base.OnClosed(e);
    }
}
using System.Windows.Controls;
using AmbientEffectsEngine.ViewModels;

namespace AmbientEffectsEngine.Views
{
    public partial class MonitorSetupPage : Page
    {
        public MonitorSetupPage()
        {
            InitializeComponent();
        }

        public MonitorSetupPage(MonitorSetupViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }
    }
}
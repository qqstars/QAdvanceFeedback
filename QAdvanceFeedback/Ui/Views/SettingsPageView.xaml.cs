using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The plugin settings page. Wiring only: the host (Settings\SettingsControl) passes the plugin's
    /// SettingsPageViewModel in, or sets <c>DataContext</c>; on screen = the view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML).
    /// </summary>
    public partial class SettingsPageView : UserControl
    {
        public SettingsPageView()
        {
            InitializeComponent();
        }

        public SettingsPageView(SettingsPageViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }
    }
}

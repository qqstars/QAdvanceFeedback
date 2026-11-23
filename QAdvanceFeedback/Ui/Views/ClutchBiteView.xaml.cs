using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The Clutch Bite view (plugin page tab and ShakeIt panel). Wiring only: on screen = its view model
    /// active (<c>v:ViewModelLifetime.Activate</c> in the XAML, which reads DataContext at Loaded/Unloaded).
    /// The ShakeIt panel passes the view model in; the plugin page sets <c>DataContext</c>.
    /// </summary>
    public partial class ClutchBiteView : UserControl
    {
        public ClutchBiteView()
        {
            InitializeComponent();
        }

        public ClutchBiteView(ClutchBiteViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }
    }
}

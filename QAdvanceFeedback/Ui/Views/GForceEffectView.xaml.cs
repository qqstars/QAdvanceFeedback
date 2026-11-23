using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>The G-Force ShakeIt panel. Wiring only: on screen = view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML).</summary>
    public partial class GForceEffectView : UserControl
    {
        public GForceEffectView(GForceEffectViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

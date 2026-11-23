using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The Slip Angle ShakeIt panel. Wiring only: this view on screen = its view model active (the hub
    /// subscription, the 1 s refresh, and the pending key data edit written when it closes), declared by
    /// <c>v:ViewModelLifetime.Activate</c> in the XAML.
    /// </summary>
    public partial class SlipAngleEffectView : UserControl
    {
        public SlipAngleEffectView(SlipAngleEffectViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

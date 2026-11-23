using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// A WheelLock/WheelSlip ShakeIt panel. Wiring only: this view on screen = its view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML). Its lifetime alone stops a running Test Effect (the
    /// graph is rebuilt per request and never does). Opening the source dropdown asks the view model to
    /// re-check which sources are available (<c>v:EventCommand.DropDownOpened</c>).
    /// </summary>
    public partial class WheelChannelEffectView : UserControl
    {
        public WheelChannelEffectView(WheelChannelEffectViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

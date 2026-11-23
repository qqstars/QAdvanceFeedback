using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The Pulse group of a Wheel Lock / Wheel Slip tab. Wiring only: the tab view sets
    /// <c>DataContext</c> to its PulseViewModel.
    /// </summary>
    public partial class WheelChannelPulseView : UserControl
    {
        public WheelChannelPulseView()
        {
            InitializeComponent();
        }
    }
}

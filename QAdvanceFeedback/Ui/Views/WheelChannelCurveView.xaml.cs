using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The "Output data and shaping" group of a Wheel Lock / Wheel Slip tab. Wiring only: the tab view sets
    /// <c>DataContext</c> to its CurveEditorViewModel.
    /// </summary>
    public partial class WheelChannelCurveView : UserControl
    {
        public WheelChannelCurveView()
        {
            InitializeComponent();
        }
    }
}

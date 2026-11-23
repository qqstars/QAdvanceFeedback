using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The key data block of a Wheel Lock / Wheel Slip tab. Wiring only: the tab view sets
    /// <c>DataContext</c> to its KeyDataPageViewModel; on screen = the view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML).
    /// </summary>
    public partial class WheelChannelKeyDataView : UserControl
    {
        public WheelChannelKeyDataView()
        {
            InitializeComponent();
        }
    }
}

using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The plugin page's Wheel Lock / Wheel Slip tab body (one view for both tabs). Wiring only: the page sets
    /// <c>DataContext</c> to the tab's WheelChannelPageViewModel; on screen = the view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML).
    /// </summary>
    public partial class WheelChannelPageView : UserControl
    {
        public WheelChannelPageView()
        {
            InitializeComponent();
        }
    }
}

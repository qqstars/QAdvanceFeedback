using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The plugin page's Slip Angle tab (docs\slip-angle-design.md 9.9). Wiring only: the page sets
    /// <c>DataContext</c> to its SlipAngleTabViewModel; on screen = the view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML, which reads DataContext at Loaded/Unloaded).
    /// </summary>
    public partial class SlipAngleTabView : UserControl
    {
        public SlipAngleTabView()
        {
            InitializeComponent();
        }
    }
}

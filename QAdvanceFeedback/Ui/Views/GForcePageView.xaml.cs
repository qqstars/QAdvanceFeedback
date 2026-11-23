using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The plugin page's G-Force tab. Wiring only: the page sets <c>DataContext</c> to its
    /// GForcePageViewModel by binding; on screen = the view model active
    /// (<c>v:ViewModelLifetime.Activate</c> in the XAML).
    /// </summary>
    public partial class GForcePageView : UserControl
    {
        public GForcePageView()
        {
            InitializeComponent();
        }
    }
}

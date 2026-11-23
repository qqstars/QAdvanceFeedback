using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The plugin page's General tab. Wiring only: the page shell sets <c>DataContext</c> to its
    /// GeneralViewModel; on screen = the view model active (<c>v:ViewModelLifetime.Activate</c> in the XAML).
    /// </summary>
    public partial class GeneralPageView : UserControl
    {
        public GeneralPageView()
        {
            InitializeComponent();
        }
    }
}

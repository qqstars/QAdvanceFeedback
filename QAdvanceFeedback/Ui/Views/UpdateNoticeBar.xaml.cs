using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The green "new version" notice. Wiring only: its <c>DataContext</c> is the shared
    /// <c>UpdateStatusViewModel.Current</c> (set in XAML) and every visual follows it by one-way bindings.
    /// </summary>
    public partial class UpdateNoticeBar : UserControl
    {
        public UpdateNoticeBar()
        {
            InitializeComponent();
        }
    }
}

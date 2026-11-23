using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>The graph in the effect's curve slot. Wiring only: it redraws from the view model when shown
    /// (<c>v:EventCommand.Loaded="{Binding RefreshCommand}"</c> in the XAML).</summary>
    public partial class GraphView : UserControl
    {
        public GraphView(GraphViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

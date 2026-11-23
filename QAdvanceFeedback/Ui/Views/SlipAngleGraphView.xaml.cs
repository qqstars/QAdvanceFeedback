using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>The graph in the Slip Angle effect's curve slot. Wiring only: it redraws from the view model
    /// when shown (<c>v:EventCommand.Loaded="{Binding RedrawGraphCommand}"</c> in the XAML).</summary>
    public partial class SlipAngleGraphView : UserControl
    {
        public SlipAngleGraphView(SlipAngleEffectViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

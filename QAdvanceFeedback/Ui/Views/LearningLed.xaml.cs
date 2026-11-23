using System.Windows.Controls;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The learning maturity LED (docs\learning-maturity-indicator-report.md). Wiring only: the usage sets
    /// <c>DataContext</c> to a <c>LearningIndicator</c> and every visual follows it by one-way bindings
    /// (converters in <see cref="LearningLedConverters"/>).
    /// </summary>
    public partial class LearningLed : UserControl
    {
        public LearningLed()
        {
            InitializeComponent();
        }
    }
}

using System.Windows.Controls;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The plugin's WPF settings control - the one <see cref="Control"/> IWPFSettingsV2 gives a plugin. A
    /// thin host (docs\full-mvvm-plan.md 3.3): its content is <c>Ui\Views\SettingsPageView</c> bound to the
    /// plugin's one <see cref="SettingsPageViewModel"/>, which owns the draft, Apply and every tab. No
    /// logic lives here (MvvmRuleTests).
    /// </summary>
    public sealed partial class SettingsControl : UserControl
    {
        public SettingsControl(SettingsPageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

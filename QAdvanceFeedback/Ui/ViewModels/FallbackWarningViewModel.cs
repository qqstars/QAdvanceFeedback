using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The orange "not natively supported by this game" warning of one Wheel Lock / Wheel Slip channel
    /// (docs\slip-angle-design.md section 7). Shown only while the channel's source is Plugin Internal
    /// (<see cref="SourceMode.Manual"/>) and the selected branch is a fallback estimate; ShakeIt Built-in,
    /// Viper and Custom never warn.
    /// </summary>
    public sealed class FallbackWarningViewModel : ObservableObject
    {
        private bool _show;
        private string _text = string.Empty;

        public bool ShowWarning { get => _show; private set => SetProperty(ref _show, value); }
        public string Text { get => _text; private set => SetProperty(ref _text, value); }

        /// <summary>True only for Plugin Internal with a fallback branch.</summary>
        public static bool Applies(SourceMode? mode, SourceNativeness nativeness) =>
            mode == SourceMode.Manual && nativeness == SourceNativeness.Fallback;

        public static string Message => Strings.Get("Source.FallbackWarning.LockSlip");

        public void Refresh(SourceMode? mode, SourceNativeness nativeness)
        {
            bool show = Applies(mode, nativeness);
            Text = show ? Message : string.Empty;
            ShowWarning = show;
        }
    }
}

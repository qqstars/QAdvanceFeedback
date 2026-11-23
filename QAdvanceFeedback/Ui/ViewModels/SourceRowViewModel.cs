using System;
using System.Threading.Tasks;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// One wheel's source row on the Wheel Lock / Wheel Slip tab: the text, the script-type toggle (P/J/N)
    /// and the single Pick / Edit button. State lives in the draft channel (<see cref="SourcesViewModel"/>
    /// owns every write, so a preset edit moves the channel to Custom in one place):
    /// <list type="bullet">
    /// <item>While the channel is shadowed to Raw the row shows Raw's property name, Plain, and every control
    /// of the row is disabled - the boxes show what is actually read, and the readout is never saved.</item>
    /// <item>The toggle cycles Plain -&gt; JavaScript -&gt; NCalc -&gt; Plain (BUG 1: Plain used to be
    /// unreachable once left) and is hidden without the expression evaluator, which also shows (and keys)
    /// every stored type as Plain.</item>
    /// <item>The button follows the ROW'S OWN script type (BUG 2, <see cref="SourceButtonModeSelector"/>);
    /// an editor result sets the text AND the dialect it produced (BUG 3); a cancelled or empty pick changes
    /// nothing.</item>
    /// <item>Typed text is stored trimmed (the old save trimmed) while the box keeps what was typed.</item>
    /// </list>
    /// </summary>
    public sealed class SourceRowViewModel : ObservableObject
    {
        private readonly SourcesViewModel _owner;
        private string _typed;

        internal SourceRowViewModel(SourcesViewModel owner, int wheel)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Wheel = wheel;
            Label = Strings.Get(wheel == 0 ? "Sources.FrontLeft" : wheel == 1 ? "Sources.FrontRight"
                : wheel == 2 ? "Sources.RearLeft" : "Sources.RearRight");
            CycleScriptTypeCommand = new RelayCommand(() =>
            {
                if (IsEnabled) _owner.EditRow(Wheel, null, ScriptSyntax.Cycle(ScriptType));
            });
            ActionCommand = new RelayCommand(() => { _ = RunActionAsync(); });
        }

        /// <summary>0 FrontLeft, 1 FrontRight, 2 RearLeft, 3 RearRight.</summary>
        public int Wheel { get; }
        public string Label { get; }
        public RelayCommand CycleScriptTypeCommand { get; }
        public RelayCommand ActionCommand { get; }

        /// <summary>Every control of the row is live: false while the channel is shadowed to Raw.</summary>
        public bool IsEnabled => !_owner.IsShadowing;

        public string Text
        {
            get
            {
                if (_owner.IsShadowing) return RawSourceFallback.PropertyName(_owner.IsLock, Wheel);
                string stored = _owner.StoredText(Wheel) ?? string.Empty;
                return _typed != null && string.Equals(_typed.Trim(), stored, StringComparison.Ordinal) ? _typed : stored;
            }
            set
            {
                if (!IsEnabled) { OnPropertyChanged(nameof(Text)); return; }
                _typed = value ?? string.Empty;
                _owner.EditRow(Wheel, _typed.Trim(), null);
                OnPropertyChanged(nameof(Text));
            }
        }

        /// <summary>The shown script type: Raw's Plain while shadowed, Plain without the evaluator.</summary>
        public ScriptType ScriptType => _owner.IsShadowing ? RawSourceFallback.RawScriptType : _owner.ShownScriptType(Wheel);

        public string ScriptLetter => ScriptType == ScriptType.JavaScript ? "J" : ScriptType == ScriptType.NCalc ? "N" : "P";

        public string ScriptTooltip => Strings.Get(ScriptType == ScriptType.JavaScript ? "Sources.ScriptType.TooltipJavaScript"
            : ScriptType == ScriptType.NCalc ? "Sources.ScriptType.TooltipNCalc" : "Sources.ScriptType.TooltipPlain");

        /// <summary>Hidden without the expression evaluator (a plain property is then the only syntax).</summary>
        public bool ShowScriptType => _owner.EvaluatorAvailable;

        private SourceButtonMode Mode => _owner.ResolveButton(ScriptType);
        public bool ShowAction => Mode != SourceButtonMode.None;
        public string ActionLabel => Mode == SourceButtonMode.None ? string.Empty
            : Strings.Get(Mode == SourceButtonMode.Editor ? "Sources.EditScriptButton" : "Curve.PickButton");

        /// <summary>The draft (or the shadow) changed: re-read everything. A typed value no longer matching
        /// the stored text is dropped.</summary>
        internal void Refresh()
        {
            if (_typed != null && !string.Equals(_typed.Trim(), _owner.StoredText(Wheel) ?? string.Empty, StringComparison.Ordinal))
                _typed = null;
            OnPropertyChanged(null);
        }

        private async Task RunActionAsync()
        {
            try
            {
                if (!IsEnabled) return;
                SourceButtonMode mode = Mode;
                if (mode == SourceButtonMode.None) return;
                SourceEdit result = await _owner.EditSourceAsync(mode, Text, ScriptType);
                if (result == null) return;
                if (mode == SourceButtonMode.Picker)
                {
                    if (string.IsNullOrEmpty(result.Text)) return;
                    _typed = null;
                    _owner.EditRow(Wheel, result.Text.Trim(), null);
                }
                else
                {
                    // BUG 3: the dialect the editor produced, whatever the row was before.
                    _typed = null;
                    _owner.EditRow(Wheel, (result.Text ?? string.Empty).Trim(), result.ScriptType);
                }
            }
            catch (Exception e)
            {
                _owner.ReportFailure(e);
            }
        }
    }
}

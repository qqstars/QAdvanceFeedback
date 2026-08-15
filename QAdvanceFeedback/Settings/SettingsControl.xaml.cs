using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Projection;
using SimHub.Plugins;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The plugin's WPF settings control - the one <see cref="Control"/> IWPFSettingsV2 gives a
    /// plugin, so Wheel Lock, Wheel Slip, G-Force and General are TABS inside it. Wheel Lock and Wheel
    /// Slip are each self-contained (source mode toggle, sources, pedal thresholds, curve editor,
    /// pulse group). Ported patterns from the sibling ReliableWheelLockSlip project's own
    /// <c>Settings\SettingsControl.xaml(.cs)</c> - it already solves the source-field button cascade,
    /// the script-type toggle, and the curve editor; this class reuses that architecture against this
    /// plugin's own settings shape.
    /// <para/>
    /// Every field is edited into a scratch copy (<see cref="_workingLockProjector"/>/
    /// <see cref="_workingSlipProjector"/>) rather than directly into <see cref="QAdvanceFeedback.Settings"/>,
    /// so a half-typed value never corrupts the live settings object; only the single global Apply
    /// button (shared across every tab) commits it. Source text/script-type/mode fields are simple
    /// enough that they are still read/written directly (matching the pre-existing convention for
    /// those specific controls).
    /// </summary>
    public sealed partial class SettingsControl : UserControl
    {
        private readonly QAdvanceFeedback _plugin;
        private readonly PluginManager _pluginManager;
        private readonly PropertyPickerLauncher _picker = new PropertyPickerLauncher();
        private readonly SimHubExpressionEvaluator _evaluator = new SimHubExpressionEvaluator();
        private readonly ShakeItSourceProvider _shakeIt = new ShakeItSourceProvider();

        // Resolved ONCE at construction (mirrors every other reflection-wrapper IsAvailable check in
        // this plugin). IMPORTANT - what this now controls: ONLY whether the "not available yet" inline
        // note is shown while ShakeIt mode is selected - it NEVER hides the mode toggle itself any
        // more. A previous version of this settings control hid the entire toggle (combo + notes)
        // whenever this came back false, which is exactly why the owner could not find the toggle at
        // all: on a system where ShakeIt Motors is not yet configured (the common case before a driver
        // has followed docs\shakeit-export-guide.md), availability is false on every visit, so the
        // toggle was permanently invisible. The owner's correction: "never show a mode that cannot
        // work" was the wrong rule here - the toggle is what LETS a driver discover and select ShakeIt
        // mode before they have configured SimHub, so it must always render; only the truthful "is it
        // actually resolving right now" status is conditional. See RefreshSourceModeUi.
        private readonly bool _lockShakeItAvailable;
        private readonly bool _slipShakeItAvailable;

        private readonly ProjectorSettings _workingLockProjector = new ProjectorSettings();
        private readonly ProjectorSettings _workingSlipProjector = new ProjectorSettings();

        // Guards every programmatic write to a control (loading settings, applying a preset) so the
        // resulting ValueChanged/SelectionChanged events do not re-enter the "user edited this"
        // handlers.
        private bool _isUpdatingUi;

        /// <summary>One source row's three controls, so the button-cascade logic (BUG 2's fix - the
        /// button depends on the ROW'S OWN script type, not just availability) can be refreshed
        /// per-row instead of once for the whole tab.</summary>
        private sealed class SourceRow
        {
            public Button ActionButton;
            public TextBox SourceBox;
            public Button ScriptTypeButton;
        }

        private readonly List<SourceRow> _lockRows = new List<SourceRow>();
        private readonly List<SourceRow> _slipRows = new List<SourceRow>();

        public SettingsControl(QAdvanceFeedback plugin, PluginManager pluginManager)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _pluginManager = pluginManager;

            _lockShakeItAvailable = _shakeIt.IsLockAvailable(_pluginManager);
            _slipShakeItAvailable = _shakeIt.IsSlipAvailable(_pluginManager);

            LocalizeStaticText();
            WireSourceButtons();
            WireScriptTypeToggles();
            WireSourceModeToggles();

            WireAnchorEvents(_workingLockProjector, LockStartRaw, LockSlightlyRaw, LockSlightlyOutput,
                LockModerateRaw, LockModerateOutput, LockCriticalRaw, LockCriticalOutput, LockEndRaw,
                LockPresetCombo, ProjectionChannel.Lock);
            WireAnchorEvents(_workingSlipProjector, SlipStartRaw, SlipSlightlyRaw, SlipSlightlyOutput,
                SlipModerateRaw, SlipModerateOutput, SlipCriticalRaw, SlipCriticalOutput, SlipEndRaw,
                SlipPresetCombo, ProjectionChannel.Slip);

            LoadFromSettings();

            ApplyButton.Click += (s, e) => SaveToSettings();
            RestoreAllDefaultsButton.Click += (s, e) => RestoreAllDefaults();
            LockResetSources.Click += (s, e) => ResetSourcesToDefault(isLock: true);
            SlipResetSources.Click += (s, e) => ResetSourcesToDefault(isLock: false);

            // Under Auto, the fixed-value spinner stays visible but read-only (IsEnabled=false, not
            // Collapsed) - the brief's own wording - and its value is refreshed to the currently
            // learned figure so the field reads as "here is what Auto has learned", not a stale
            // Fixed-mode number sitting there doing nothing.
            GForceAccelModeCombo.SelectionChanged += (s, e) => RefreshGForceModeControls();
            GForceDecelModeCombo.SelectionChanged += (s, e) => RefreshGForceModeControls();
            RefreshGForceModeControls();

            // While the shake feature is OFF, the settings it governs (frequency, both scales) are
            // greyed out - obvious at a glance that they currently do nothing. Pure UI wiring (sets
            // IsEnabled on WPF controls from a WPF control's own IsChecked) - there is no non-trivial
            // logic here to extract into a testable seam; "unverified without a live WPF window",
            // same as every other visual-only behaviour in this control (see the settings-control
            // wiring report's own remarks on the three UI bugs it fixed).
            GForceShakeEnabled.Checked += (s, e) => RefreshGForceShakeControls();
            GForceShakeEnabled.Unchecked += (s, e) => RefreshGForceShakeControls();
            RefreshGForceShakeControls();
        }

        private void RefreshGForceShakeControls()
        {
            bool enabled = GForceShakeEnabled.IsChecked == true;
            GForceShakeFrequency.IsEnabled = enabled;
            GForceShakeLockScale.IsEnabled = enabled;
            GForceShakeSlipScale.IsEnabled = enabled;
        }

        private void RefreshGForceModeControls()
        {
            bool accelAuto = string.Equals(GetSelectedTag(GForceAccelModeCombo, "Fixed"), "Auto", StringComparison.Ordinal);
            bool decelAuto = string.Equals(GetSelectedTag(GForceDecelModeCombo, "Fixed"), "Auto", StringComparison.Ordinal);

            GForceFixedAccelMax.IsEnabled = !accelAuto;
            GForceFixedDecelMax.IsEnabled = !decelAuto;

            if (accelAuto) GForceFixedAccelMax.Value = _plugin.Settings.GForce.CurrentLearnedAccelMaxG;
            if (decelAuto) GForceFixedDecelMax.Value = _plugin.Settings.GForce.CurrentLearnedDecelMaxG;

            GForceAccelLearnedText.Visibility = accelAuto ? Visibility.Visible : Visibility.Collapsed;
            GForceDecelLearnedText.Visibility = decelAuto ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------------------------
        // Localisation - every literal the user sees comes from Strings.Get.
        // ------------------------------------------------------------------------------------

        private void LocalizeStaticText()
        {
            WheelLockTab.Header = Strings.Get("Tab.WheelLock");
            WheelSlipTab.Header = Strings.Get("Tab.WheelSlip");
            GForceTab.Header = Strings.Get("Tab.GForce");
            GeneralTab.Header = Strings.Get("Tab.General");
            ApplyButton.Content = Strings.Get("Common.Apply");
            RestoreAllDefaultsButton.Content = Strings.Get("Common.RestoreAllDefaults");

            LockSourcesGroup.Header = Strings.Get("Group.Sources");
            SlipSourcesGroup.Header = Strings.Get("Group.Sources");
            LockSourcesNote.Text = Strings.Get("Sources.Note");
            SlipSourcesNote.Text = Strings.Get("Sources.Note");

            string contractNote = _evaluator.IsAvailable
                ? Strings.Get("Sources.ContractNote")
                : Strings.Get("Sources.ContractNote.NoEvaluator");
            LockSourcesContractNote.Text = contractNote;
            SlipSourcesContractNote.Text = contractNote;

            LockLblFrontLeft.Text = SlipLblFrontLeft.Text = Strings.Get("Sources.FrontLeft");
            LockLblFrontRight.Text = SlipLblFrontRight.Text = Strings.Get("Sources.FrontRight");
            LockLblRearLeft.Text = SlipLblRearLeft.Text = Strings.Get("Sources.RearLeft");
            LockLblRearRight.Text = SlipLblRearRight.Text = Strings.Get("Sources.RearRight");
            LockResetSources.Content = SlipResetSources.Content = Strings.Get("Sources.ResetToDefault");

            // ---- Source mode toggle (Manual vs. ShakeIt Motors) - ALWAYS visible on both tabs (see
            // _lockShakeItAvailable/_slipShakeItAvailable's own remarks for why this changed). ----
            LockLblSourceMode.Text = SlipLblSourceMode.Text = Strings.Get("Sources.Mode.Label");
            LockSourceModeManual.Content = SlipSourceModeManual.Content = Strings.Get("Sources.Mode.Manual");
            LockSourceModeShakeIt.Content = SlipSourceModeShakeIt.Content = Strings.Get("Sources.Mode.ShakeIt");
            LockShakeItSetupNote.Text = Strings.Get("Sources.ShakeItSetup.Lock");
            SlipShakeItSetupNote.Text = Strings.Get("Sources.ShakeItSetup.Slip");
            LockSourcesShakeItNote.Text = Strings.Get("Sources.ShakeItUnavailable.Note");
            SlipSourcesShakeItNote.Text = Strings.Get("Sources.ShakeItUnavailable.Note");
            LockManualNote.Text = Strings.Get("Sources.Mode.ManualNote.Lock");
            SlipManualNote.Text = Strings.Get("Sources.Mode.ManualNote.Slip");

            // Visibility of the three mode-dependent notes below the toggle is set from the CURRENT
            // mode (and, for the "unavailable" note, current availability) in RefreshSourceModeUi -
            // called from here once, and again every time the mode combo changes or settings reload.
            RefreshSourceModeUi(isLock: true);
            RefreshSourceModeUi(isLock: false);

            // ---- Pedal-pressed thresholds (owner-requested, both modes) ----
            LockLblBrakeThreshold.Text = Strings.Get("Sources.Threshold.LockBrake");
            LockLblBrakeThreshold.ToolTip = Strings.Get("Sources.Threshold.Lock.Note");
            LockBrakeThreshold.ToolTip = Strings.Get("Sources.Threshold.Lock.Note");
            SlipLblBrakeThreshold.Text = Strings.Get("Sources.Threshold.SlipBrake");
            SlipLblThrottleThreshold.Text = Strings.Get("Sources.Threshold.SlipThrottle");
            SlipLblBrakeThreshold.ToolTip = SlipLblThrottleThreshold.ToolTip = Strings.Get("Sources.Threshold.Slip.Note");
            SlipBrakeThreshold.ToolTip = SlipThrottleThreshold.ToolTip = Strings.Get("Sources.Threshold.Slip.Note");

            LockCurveGroup.Header = SlipCurveGroup.Header = Strings.Get("Group.Curve");
            LockLblPreset.Text = SlipLblPreset.Text = Strings.Get("Curve.Preset.Label");
            LockPresetLinear.Content = SlipPresetLinear.Content = Strings.Get("Curve.Preset.Linear");
            LockPresetCurve.Content = SlipPresetCurve.Content = Strings.Get("Curve.Preset.Curve");
            LockPresetCustom.Content = SlipPresetCustom.Content = Strings.Get("Curve.Preset.Custom");
            LockColRawHeader.Text = SlipColRawHeader.Text = Strings.Get("Curve.Column.RawValue");
            LockColOutputHeader.Text = SlipColOutputHeader.Text = Strings.Get("Curve.Column.OutputValue");

            LockAnchorNoteText.Text = Strings.Get("Curve.Lock.AnchorNote");
            LockLblStart.Text = Strings.Get("Curve.StartPoint.Label");
            LockStartOutputStatic.Text = Strings.Get("Curve.StartPoint.AlwaysZero");
            LockDescStart.Text = Strings.Get("Curve.Lock.StartPoint.Desc");
            LockLblSlightly.Text = Strings.Get("Curve.Anchor.Slightly") + ":";
            LockDescSlightly.Text = Strings.Get("Curve.Lock.Slightly.Desc");
            LockLblModerate.Text = Strings.Get("Curve.Anchor.Ideal") + ":";
            LockDescModerate.Text = Strings.Get("Curve.Lock.Ideal.Desc");
            LockLblCritical.Text = Strings.Get("Curve.Anchor.Critical") + ":";
            LockDescCritical.Text = Strings.Get("Curve.Lock.Critical.Desc");
            LockLblEnd.Text = Strings.Get("Curve.EndPoint.Label");
            LockEndOutputStatic.Text = Strings.Get("Curve.EndPoint.AlwaysFull");
            LockDescEnd.Text = Strings.Get("Curve.Lock.EndPoint.Desc");

            SlipAnchorNoteText.Text = Strings.Get("Curve.Slip.AnchorNote");
            SlipLblStart.Text = Strings.Get("Curve.StartPoint.Label");
            SlipStartOutputStatic.Text = Strings.Get("Curve.StartPoint.AlwaysZero");
            SlipDescStart.Text = Strings.Get("Curve.Slip.StartPoint.Desc");
            SlipLblSlightly.Text = Strings.Get("Curve.Anchor.Slightly") + ":";
            SlipDescSlightly.Text = Strings.Get("Curve.Slip.Slightly.Desc");
            SlipLblModerate.Text = Strings.Get("Curve.Anchor.Ideal") + ":";
            SlipDescModerate.Text = Strings.Get("Curve.Slip.Ideal.Desc");
            SlipLblCritical.Text = Strings.Get("Curve.Anchor.Critical") + ":";
            SlipDescCritical.Text = Strings.Get("Curve.Slip.Critical.Desc");
            SlipLblEnd.Text = Strings.Get("Curve.EndPoint.Label");
            SlipEndOutputStatic.Text = Strings.Get("Curve.EndPoint.AlwaysFull");
            SlipDescEnd.Text = Strings.Get("Curve.Slip.EndPoint.Desc");

            LockPulseGroup.Header = SlipPulseGroup.Header = Strings.Get("Group.Pulse");
            LockPulseEnabled.Content = SlipPulseEnabled.Content = Strings.Get("Pulse.Enable");
            LockLblGapMs.Text = SlipLblGapMs.Text = Strings.Get("Pulse.GapMs");
            LockLblMinValue.Text = SlipLblMinValue.Text = Strings.Get("Pulse.MinValue");
            LockPulseFloorNote.Text = SlipPulseFloorNote.Text = Strings.Get("Pulse.FloorNote");

            GForceMaximaGroup.Header = Strings.Get("Group.GForceMaxima");
            GForceMaximaNote.Text = Strings.Get("GForce.Maxima.Note");
            GForceLblAccelMax.Text = Strings.Get("GForce.AccelMax.Label");
            GForceLblDecelMax.Text = Strings.Get("GForce.DecelMax.Label");
            GForceAccelModeFixed.Content = GForceDecelModeFixed.Content = Strings.Get("GForce.Mode.Fixed");
            GForceAccelModeAuto.Content = GForceDecelModeAuto.Content = Strings.Get("GForce.Mode.Auto");

            GForceSustainGroup.Header = Strings.Get("Group.GForceSustain");
            GForceSustainNote.Text = Strings.Get("GForce.Sustain.Note");
            GForceLblBrakeBottomRear.Text = Strings.Get("GForce.Sustain.BrakeBottomRear");
            GForceLblBrakeBackLow.Text = Strings.Get("GForce.Sustain.BrakeBackLow");
            GForceLblAccelBottomRear.Text = Strings.Get("GForce.Sustain.AccelBottomRear");
            GForceLblAccelBackLow.Text = Strings.Get("GForce.Sustain.AccelBackLow");

            GForceMotionGroup.Header = Strings.Get("Group.GForceMotion");
            GForceMotionNote.Text = Strings.Get("GForce.Motion.Note");
            GForceLblSustainTau.Text = Strings.Get("GForce.SustainTau.Label");
            GForceLblTransientTau.Text = Strings.Get("GForce.TransientTau.Label");
            GForceLblTransientGain.Text = Strings.Get("GForce.TransientGain.Label");

            GForceLateralGroup.Header = Strings.Get("Group.GForceLateral");
            GForceLblLateralDirection.Text = Strings.Get("GForce.LateralDirection.Label");
            GForceLateralNormal.Content = Strings.Get("GForce.LateralDirection.Normal");
            GForceLateralReversed.Content = Strings.Get("GForce.LateralDirection.Reversed");
            GForceLateralDirectionNote.Text = Strings.Get("GForce.LateralDirection.Note");

            GForceShakeGroup.Header = Strings.Get("Group.GForceShake");
            // A feature SWITCH, not a minor checkbox option (owner's explicit distinction) - the bold
            // header names the feature, the ToggleSwitch itself spells out both states via
            // OnLabel/OffLabel (never inferred from a highlight alone).
            GForceShakeHeader.Text = Strings.Get("GForce.Shake.Enable");
            GForceShakeEnabled.OnLabel = Strings.Get("GForce.Shake.On");
            GForceShakeEnabled.OffLabel = Strings.Get("GForce.Shake.Off");
            GForceShakeNote.Text = Strings.Get("GForce.Shake.Note");
            GForceLblShakeFrequency.Text = Strings.Get("GForce.Shake.Frequency.Label");
            GForceLblShakeLockScale.Text = Strings.Get("GForce.Shake.LockScale.Label");
            GForceLblShakeSlipScale.Text = Strings.Get("GForce.Shake.SlipScale.Label");

            GeneralGroup.Header = Strings.Get("Group.General");
            EnableDiagnosticsCheckBox.Content = Strings.Get("General.EnableDiagnostics");
            EnableDiagnosticsNote.Text = Strings.Get("General.EnableDiagnostics.Note");
            EnableDiagnosticsRestartNote.Text = Strings.Get("General.EnableDiagnostics.RestartNote");
            ExportCsvCheckBox.Content = Strings.Get("General.ExportCsv");
            ExportCsvNote.Text = Strings.Get("General.ExportCsv.Note");
        }

        // ------------------------------------------------------------------------------------
        // Source field button cascade (script editor / property picker / none) - BUG 2's fix: the
        // button now depends on the ROW'S OWN script type as well as availability
        // (SourceButtonModeSelector.Resolve), refreshed per row whenever that row's type changes,
        // not resolved once for the whole tab.
        // ------------------------------------------------------------------------------------

        private void WireSourceButtons()
        {
            AddRow(_lockRows, LockPickFl, LockSourceFl, LockScriptTypeFl);
            AddRow(_lockRows, LockPickFr, LockSourceFr, LockScriptTypeFr);
            AddRow(_lockRows, LockPickRl, LockSourceRl, LockScriptTypeRl);
            AddRow(_lockRows, LockPickRr, LockSourceRr, LockScriptTypeRr);
            AddRow(_slipRows, SlipPickFl, SlipSourceFl, SlipScriptTypeFl);
            AddRow(_slipRows, SlipPickFr, SlipSourceFr, SlipScriptTypeFr);
            AddRow(_slipRows, SlipPickRl, SlipSourceRl, SlipScriptTypeRl);
            AddRow(_slipRows, SlipPickRr, SlipSourceRr, SlipScriptTypeRr);

            foreach (SourceRow row in Rows())
            {
                row.ActionButton.Click += async (s, e) => await OnActionButtonClickAsync(row);
                RefreshRowButton(row);
            }
        }

        private IEnumerable<SourceRow> Rows()
        {
            foreach (SourceRow row in _lockRows) yield return row;
            foreach (SourceRow row in _slipRows) yield return row;
        }

        private void AddRow(List<SourceRow> rows, Button actionButton, TextBox sourceBox, Button scriptTypeButton)
            => rows.Add(new SourceRow { ActionButton = actionButton, SourceBox = sourceBox, ScriptTypeButton = scriptTypeButton });

        private async Task OnActionButtonClickAsync(SourceRow row)
        {
            SourceButtonMode mode = ResolveMode(row);
            if (mode == SourceButtonMode.Editor) await EditInto(row);
            else if (mode == SourceButtonMode.Picker) await PickInto(row.SourceBox);
        }

        private SourceButtonMode ResolveMode(SourceRow row)
            => SourceButtonModeSelector.Resolve(GetScriptType(row.ScriptTypeButton), SimHubScriptEditor.IsAvailable, _picker.IsAvailable, _evaluator.IsAvailable);

        /// <summary>Refreshes ONE row's action button (visibility/label) from its CURRENT script
        /// type - called at wire time, whenever that row's script-type toggle is clicked, after a
        /// script edit completes, and after loading/resetting/applying ShakeIt defaults.</summary>
        private void RefreshRowButton(SourceRow row)
        {
            switch (ResolveMode(row))
            {
                case SourceButtonMode.Editor:
                    row.ActionButton.Visibility = Visibility.Visible;
                    row.ActionButton.Content = Strings.Get("Sources.EditScriptButton");
                    break;
                case SourceButtonMode.Picker:
                    row.ActionButton.Visibility = Visibility.Visible;
                    row.ActionButton.Content = Strings.Get("Curve.PickButton");
                    break;
                default:
                    row.ActionButton.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private async Task PickInto(TextBox box)
        {
            string chosen = await _picker.PickPropertyAsync(box.Text);
            if (!string.IsNullOrEmpty(chosen)) box.Text = chosen;
        }

        /// <summary>
        /// BUG 3 (confirmed by decompiling ExpressionValue.UseJavascript - it already resolves
        /// ForceJavascript internally, so reading it back, exactly as SimHubScriptEditor already
        /// does, is never ambiguous between JavaScript/NCalc): after a completed edit, this row's
        /// script-type indicator and button are updated to whatever dialect the editor actually
        /// produced - unconditionally, regardless of what the row's type was BEFORE the edit (so
        /// even a defensive/future path that somehow reaches the editor from a Plain row cannot leave
        /// the row stuck showing Plain with expression text in it, which would silently be read back
        /// as a literal property name and fail).
        /// </summary>
        private async Task EditInto(SourceRow row)
        {
            ScriptEditResult result = await SimHubScriptEditor.EditExpressionAsync(
                Window.GetWindow(this), row.SourceBox.Text, GetScriptType(row.ScriptTypeButton));
            if (result == null) return;

            row.SourceBox.Text = result.Expression ?? string.Empty;
            SetScriptTypeVisual(row.ScriptTypeButton, result.ScriptType);
            RefreshRowButton(row);
        }

        // ------------------------------------------------------------------------------------
        // Script-type indicator/toggle - a rounded-rectangle Button per source field, grey "P" for a
        // Plain property, green "J" for JavaScript, blue "N" for NCalc, hidden entirely when the
        // expression evaluator is unavailable (a plain SimHub property is then the only usable
        // syntax).
        // <para/>
        // BUG 1 FIX (read before changing this again): clicking used to only flip between JavaScript
        // and NCalc, so Plain - the shipped default every field starts on - was UNREACHABLE once
        // left; there was no way back to a plain property reference through the UI at all. The click
        // now cycles all three: Plain -&gt; JavaScript -&gt; NCalc -&gt; Plain.
        // ------------------------------------------------------------------------------------

        private static readonly Brush ScriptTypeJavaScriptBrush = CreateVerticalGradient(
            Color.FromRgb(0x66, 0xBB, 0x6A), Color.FromRgb(0x1B, 0x5E, 0x20));
        private static readonly Brush ScriptTypeNCalcBrush = CreateVerticalGradient(
            Color.FromRgb(0x42, 0xA5, 0xF5), Color.FromRgb(0x0D, 0x47, 0xA1));
        private static readonly Brush ScriptTypePlainBrush = CreateVerticalGradient(
            Color.FromRgb(0x9E, 0x9E, 0x9E), Color.FromRgb(0x42, 0x42, 0x42));

        private static Brush CreateVerticalGradient(Color top, Color bottom)
        {
            var brush = new LinearGradientBrush(top, bottom, 90.0);
            brush.Freeze();
            return brush;
        }

        private void WireScriptTypeToggles()
        {
            Visibility v = _evaluator.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
            LockScriptTypeFl.Visibility = LockScriptTypeFr.Visibility = v;
            LockScriptTypeRl.Visibility = LockScriptTypeRr.Visibility = v;
            SlipScriptTypeFl.Visibility = SlipScriptTypeFr.Visibility = v;
            SlipScriptTypeRl.Visibility = SlipScriptTypeRr.Visibility = v;

            foreach (SourceRow row in Rows())
                row.ScriptTypeButton.Click += ScriptTypeToggle_Click;
        }

        private void ScriptTypeToggle_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            ScriptType next = ScriptSyntax.Cycle(GetScriptType(button));
            SetScriptTypeVisual(button, next);

            SourceRow row = FindRowByScriptTypeButton(button);
            if (row != null) RefreshRowButton(row);
        }

        private SourceRow FindRowByScriptTypeButton(Button button)
        {
            foreach (SourceRow row in Rows())
                if (ReferenceEquals(row.ScriptTypeButton, button)) return row;
            return null;
        }

        private static void SetScriptTypeVisual(Button button, ScriptType type)
        {
            button.Tag = type;
            switch (type)
            {
                case ScriptType.JavaScript:
                    button.Content = "J";
                    button.Background = ScriptTypeJavaScriptBrush;
                    button.ToolTip = Strings.Get("Sources.ScriptType.TooltipJavaScript");
                    break;
                case ScriptType.NCalc:
                    button.Content = "N";
                    button.Background = ScriptTypeNCalcBrush;
                    button.ToolTip = Strings.Get("Sources.ScriptType.TooltipNCalc");
                    break;
                default:
                    button.Content = "P";
                    button.Background = ScriptTypePlainBrush;
                    button.ToolTip = Strings.Get("Sources.ScriptType.TooltipPlain");
                    break;
            }
        }

        private static ScriptType GetScriptType(Button button)
            => button.Tag is ScriptType type ? type : ScriptType.Plain;

        /// <summary>BUG 1's other half: if the expression evaluator is unavailable this session,
        /// Plain must be the ONLY reachable state - a config saved while an evaluator WAS present
        /// (JavaScript/NCalc stored) must not leave a field stuck on a dialect nothing can run.
        /// Applied wherever a script type is loaded FROM settings (never when the user is actively
        /// editing - the toggle is simply hidden in that case, per <see cref="WireScriptTypeToggles"/>).</summary>
        private ScriptType CoerceLoadedScriptType(ScriptType stored) => _evaluator.IsAvailable ? stored : ScriptType.Plain;

        /// <summary>
        /// THE PER-SOURCE "Reset to default" button (under the four source fields - NOT the global
        /// "Restore all default settings"). Follows the CURRENT mode rather than forcing Manual:
        /// delegates to <see cref="WheelChannelSettings.ResetSourcesForCurrentMode"/> (unit-tested in
        /// Core) so this UI-only method stays a thin read-combo/copy-into-textboxes wrapper around
        /// already-tested logic, exactly like <see cref="OnSourceModeChanged"/> below.
        /// </summary>
        private void ResetSourcesToDefault(bool isLock)
        {
            SourceMode currentMode = ParseEnum(GetSelectedTag(isLock ? LockSourceModeCombo : SlipSourceModeCombo, "ShakeIt"), SourceMode.ShakeIt);
            ApplySourceDefaultsForMode(isLock, currentMode);
        }

        // ------------------------------------------------------------------------------------
        // Source mode toggle - Manual vs. SimHub's own ShakeIt Motors export. ALWAYS visible and
        // switchable on both tabs, regardless of whether ShakeIt is currently resolving (see
        // _lockShakeItAvailable/_slipShakeItAvailable's own remarks - a previous version of this
        // control hid the toggle whenever availability could not be confirmed, which is why the owner
        // could not find it at all).
        // ------------------------------------------------------------------------------------

        private void WireSourceModeToggles()
        {
            LockSourceModeCombo.SelectionChanged += (s, e) => OnSourceModeChanged(isLock: true);
            SlipSourceModeCombo.SelectionChanged += (s, e) => OnSourceModeChanged(isLock: false);
        }

        private void OnSourceModeChanged(bool isLock)
        {
            if (_isUpdatingUi) return;

            ComboBox combo = isLock ? LockSourceModeCombo : SlipSourceModeCombo;
            SourceMode mode = ParseEnum(GetSelectedTag(combo, "ShakeIt"), SourceMode.ShakeIt);
            ApplySourceDefaultsForMode(isLock, mode);
        }

        /// <summary>
        /// Shared by <see cref="OnSourceModeChanged"/> (switching the combo to a NEW mode) and
        /// <see cref="ResetSourcesToDefault"/> (resetting under the CURRENT mode) - both cases reduce to
        /// exactly the same operation: "make this channel's four source fields match <paramref name="mode"/>'s
        /// own defaults", via the same tested <see cref="WheelChannelSettings.ResetSourcesForCurrentMode"/>
        /// this class's UI never re-implements. Also refreshes the mode-dependent notes (setup guide /
        /// unavailable warning / manual note) so they always match whatever is showing in the four text
        /// boxes.
        /// </summary>
        private void ApplySourceDefaultsForMode(bool isLock, SourceMode mode)
        {
            List<SourceRow> rows = isLock ? _lockRows : _slipRows;

            WheelChannelSettings scratch = new WheelChannelSettings { SourceMode = mode };
            scratch.ResetSourcesForCurrentMode(isLock);

            rows[0].SourceBox.Text = scratch.SourceFrontLeft; SetScriptTypeVisual(rows[0].ScriptTypeButton, scratch.ScriptTypeFrontLeft);
            rows[1].SourceBox.Text = scratch.SourceFrontRight; SetScriptTypeVisual(rows[1].ScriptTypeButton, scratch.ScriptTypeFrontRight);
            rows[2].SourceBox.Text = scratch.SourceRearLeft; SetScriptTypeVisual(rows[2].ScriptTypeButton, scratch.ScriptTypeRearLeft);
            rows[3].SourceBox.Text = scratch.SourceRearRight; SetScriptTypeVisual(rows[3].ScriptTypeButton, scratch.ScriptTypeRearRight);
            foreach (SourceRow row in rows) RefreshRowButton(row);

            RefreshSourceModeUi(isLock);
        }

        /// <summary>
        /// Updates the three mode-dependent notes under the toggle for one channel, from whatever the
        /// combo currently shows:
        /// <list type="bullet">
        /// <item>ShakeIt mode: the concise setup guide is shown; the "not available yet" warning is
        /// ALSO shown, but only if <see cref="_lockShakeItAvailable"/>/<see cref="_slipShakeItAvailable"/>
        /// says SimHub is not currently reporting the four expected properties.</item>
        /// <item>Manual mode: neither ShakeIt note is shown; the short "supply a 0-100 value" note is
        /// shown instead.</item>
        /// </list>
        /// Called at construction (via <see cref="LocalizeStaticText"/>), on every mode change, on every
        /// per-source reset, and after loading settings.
        /// </summary>
        private void RefreshSourceModeUi(bool isLock)
        {
            ComboBox combo = isLock ? LockSourceModeCombo : SlipSourceModeCombo;
            SourceMode mode = ParseEnum(GetSelectedTag(combo, "ShakeIt"), SourceMode.ShakeIt);
            bool available = isLock ? _lockShakeItAvailable : _slipShakeItAvailable;
            bool isShakeIt = mode == SourceMode.ShakeIt;

            TextBlock setupNote = isLock ? LockShakeItSetupNote : SlipShakeItSetupNote;
            TextBlock unavailableNote = isLock ? LockSourcesShakeItNote : SlipSourcesShakeItNote;
            TextBlock manualNote = isLock ? LockManualNote : SlipManualNote;

            setupNote.Visibility = isShakeIt ? Visibility.Visible : Visibility.Collapsed;
            unavailableNote.Visibility = (isShakeIt && !available) ? Visibility.Visible : Visibility.Collapsed;
            manualNote.Visibility = isShakeIt ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetSourceModeCombo(bool isLock, SourceMode mode)
        {
            ComboBox combo = isLock ? LockSourceModeCombo : SlipSourceModeCombo;
            _isUpdatingUi = true;
            try { SelectComboItemByTag(combo, mode.ToString()); }
            finally { _isUpdatingUi = false; }
        }

        // ------------------------------------------------------------------------------------
        // "Restore all default settings" - available beside Apply on every tab (it lives outside
        // the TabControl entirely, see the XAML). Resets EVERY setting - Wheel Lock, Wheel Slip,
        // G-Force, General - to the single internally-defined default object
        // (QAdvanceFeedbackSettings.CreateDefault/RestoreDefaults), so this button and the shipped
        // defaults can never drift apart. Destructive and immediate (it also persists, exactly like
        // the ordinary Apply flow), so it is gated behind a confirmation dialog first - mirroring the
        // sibling project's own "Reset all learned profiles" button.
        // ------------------------------------------------------------------------------------

        private void RestoreAllDefaults()
        {
            if (MessageBox.Show(Strings.Get("Common.RestoreAllConfirm"), Strings.Get("Common.DialogTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _plugin.Settings.RestoreDefaults();
            _plugin.ApplySettings();
            LoadFromSettings();
            RefreshGForceModeControls();
            RefreshGForceShakeControls();
        }

        // ------------------------------------------------------------------------------------
        // Curve editor - all eight spinners per channel funnel through ProjectorAnchorEditor, which
        // owns the "null means leave unchanged" and "a real edit switches the preset to Custom"
        // rules, implemented once in Core and unit-tested there.
        // ------------------------------------------------------------------------------------

        private void WireAnchorEvents(
            ProjectorSettings working,
            MahApps.Metro.Controls.NumericUpDown startRaw,
            MahApps.Metro.Controls.NumericUpDown slightlyRaw, MahApps.Metro.Controls.NumericUpDown slightlyOutput,
            MahApps.Metro.Controls.NumericUpDown moderateRaw, MahApps.Metro.Controls.NumericUpDown moderateOutput,
            MahApps.Metro.Controls.NumericUpDown criticalRaw, MahApps.Metro.Controls.NumericUpDown criticalOutput,
            MahApps.Metro.Controls.NumericUpDown endRaw,
            ComboBox presetCombo, ProjectionChannel channel)
        {
            startRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Start, e.NewValue, channel);
            slightlyRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Slightly, e.NewValue, channel);
            moderateRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Moderate, e.NewValue, channel);
            criticalRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Critical, e.NewValue, channel);
            endRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.End, e.NewValue, channel);

            slightlyOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Slightly, e.NewValue, channel);
            moderateOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Moderate, e.NewValue, channel);
            criticalOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Critical, e.NewValue, channel);

            presetCombo.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                ProjectorPreset preset = ParseEnum(GetSelectedTag(presetCombo, "Custom"), ProjectorPreset.Custom);
                working.ApplyPreset(preset, channel);
                LoadAnchorControls(working, startRaw, slightlyRaw, slightlyOutput, moderateRaw, moderateOutput,
                    criticalRaw, criticalOutput, endRaw, presetCombo);
                RefreshCurvePlot(working, channel);
            };
        }

        private void OnAnchorRawChanged(ProjectorSettings working, AnchorSlot slot, double? value, ProjectionChannel channel)
        {
            if (_isUpdatingUi) return;
            ProjectorAnchorEditor.SetRaw(working, slot, value);
            SyncPresetCombo(working, channel);
            RefreshCurvePlot(working, channel);
        }

        private void OnAnchorOutputChanged(ProjectorSettings working, AnchorSlot slot, double? value, ProjectionChannel channel)
        {
            if (_isUpdatingUi) return;
            ProjectorAnchorEditor.SetOutput(working, slot, value);
            SyncPresetCombo(working, channel);
            RefreshCurvePlot(working, channel);
        }

        private void SyncPresetCombo(ProjectorSettings working, ProjectionChannel channel)
        {
            ComboBox combo = channel == ProjectionChannel.Lock ? LockPresetCombo : SlipPresetCombo;
            _isUpdatingUi = true;
            try { SelectComboItemByTag(combo, working.Preset.ToString()); }
            finally { _isUpdatingUi = false; }
        }

        private void LoadAnchorControls(
            ProjectorSettings working,
            MahApps.Metro.Controls.NumericUpDown startRaw,
            MahApps.Metro.Controls.NumericUpDown slightlyRaw, MahApps.Metro.Controls.NumericUpDown slightlyOutput,
            MahApps.Metro.Controls.NumericUpDown moderateRaw, MahApps.Metro.Controls.NumericUpDown moderateOutput,
            MahApps.Metro.Controls.NumericUpDown criticalRaw, MahApps.Metro.Controls.NumericUpDown criticalOutput,
            MahApps.Metro.Controls.NumericUpDown endRaw, ComboBox presetCombo)
        {
            _isUpdatingUi = true;
            try
            {
                startRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Start);
                slightlyRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Slightly);
                slightlyOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Slightly);
                moderateRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Moderate);
                moderateOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Moderate);
                criticalRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Critical);
                criticalOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Critical);
                endRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.End);
                SelectComboItemByTag(presetCombo, working.Preset.ToString());
            }
            finally { _isUpdatingUi = false; }
        }

        private void RefreshCurvePlot(ProjectorSettings working, ProjectionChannel channel)
        {
            bool isLock = channel == ProjectionChannel.Lock;
            Canvas canvas = isLock ? LockCurveCanvas : SlipCurveCanvas;
            Polyline polyline = isLock ? LockCurvePolyline : SlipCurvePolyline;

            var projector = new OutputProjector(working);
            double w = canvas.Width;
            double h = canvas.Height;

            var points = new PointCollection();
            for (int i = 0; i <= 100; i++)
            {
                double y = projector.Project(i);
                points.Add(new Point(i / 100.0 * w, h - y / 100.0 * h));
            }
            polyline.Points = points;

            PlaceMarker(isLock ? LockStartMarker : SlipStartMarker, working.StartInput, 0.0, w, h);
            PlaceMarker(isLock ? LockSlightlyMarker : SlipSlightlyMarker, working.SlightlyInput, working.SlightlyOutput, w, h);
            PlaceMarker(isLock ? LockModerateMarker : SlipModerateMarker, working.ModerateInput, working.ModerateOutput, w, h);
            PlaceMarker(isLock ? LockCriticalMarker : SlipCriticalMarker, working.CriticalInput, working.CriticalOutput, w, h);
            PlaceMarker(isLock ? LockEndMarker : SlipEndMarker, working.EndInput, 100.0, w, h);

            UpdateLiveOutputText(projector, working, isLock ? LockLiveSlightly : SlipLiveSlightly, AnchorSlot.Slightly);
            UpdateLiveOutputText(projector, working, isLock ? LockLiveModerate : SlipLiveModerate, AnchorSlot.Moderate);
            UpdateLiveOutputText(projector, working, isLock ? LockLiveCritical : SlipLiveCritical, AnchorSlot.Critical);
        }

        private void UpdateLiveOutputText(OutputProjector projector, ProjectorSettings source, TextBlock block, AnchorSlot slot)
        {
            double raw = ProjectorAnchorEditor.GetRaw(source, slot);
            double live = projector.Project(raw);
            block.Text = string.Format(Strings.Get("Curve.LiveOutputFormat"), FormatNum(live));
        }

        private static void PlaceMarker(Ellipse marker, double x0100, double y0100, double w, double h)
        {
            double cx = ClampMath.To0100(x0100) / 100.0 * w;
            double cy = h - ClampMath.To0100(y0100) / 100.0 * h;
            Canvas.SetLeft(marker, cx - marker.Width / 2.0);
            Canvas.SetTop(marker, cy - marker.Height / 2.0);
        }

        // ------------------------------------------------------------------------------------
        // Settings round-trip
        // ------------------------------------------------------------------------------------

        private void LoadFromSettings()
        {
            QAdvanceFeedbackSettings s = _plugin.Settings;

            LoadChannel(s.Lock, _lockRows, LockPulseEnabled, LockPulseGapMs, LockPulseMinValue);
            SetSourceModeCombo(isLock: true, s.Lock.SourceMode);
            RefreshSourceModeUi(isLock: true);
            LockBrakeThreshold.Value = s.Lock.BrakeThresholdPercent;

            _workingLockProjector.Preset = s.Lock.Projector.Preset;
            _workingLockProjector.StartInput = s.Lock.Projector.StartInput;
            _workingLockProjector.EndInput = s.Lock.Projector.EndInput;
            _workingLockProjector.SlightlyInput = s.Lock.Projector.SlightlyInput;
            _workingLockProjector.SlightlyOutput = s.Lock.Projector.SlightlyOutput;
            _workingLockProjector.ModerateInput = s.Lock.Projector.ModerateInput;
            _workingLockProjector.ModerateOutput = s.Lock.Projector.ModerateOutput;
            _workingLockProjector.CriticalInput = s.Lock.Projector.CriticalInput;
            _workingLockProjector.CriticalOutput = s.Lock.Projector.CriticalOutput;
            LoadAnchorControls(_workingLockProjector, LockStartRaw, LockSlightlyRaw, LockSlightlyOutput,
                LockModerateRaw, LockModerateOutput, LockCriticalRaw, LockCriticalOutput, LockEndRaw, LockPresetCombo);
            RefreshCurvePlot(_workingLockProjector, ProjectionChannel.Lock);

            LoadChannel(s.Slip, _slipRows, SlipPulseEnabled, SlipPulseGapMs, SlipPulseMinValue);
            SetSourceModeCombo(isLock: false, s.Slip.SourceMode);
            RefreshSourceModeUi(isLock: false);
            SlipBrakeThreshold.Value = s.Slip.BrakeThresholdPercent;
            SlipThrottleThreshold.Value = s.Slip.ThrottleThresholdPercent;

            _workingSlipProjector.Preset = s.Slip.Projector.Preset;
            _workingSlipProjector.StartInput = s.Slip.Projector.StartInput;
            _workingSlipProjector.EndInput = s.Slip.Projector.EndInput;
            _workingSlipProjector.SlightlyInput = s.Slip.Projector.SlightlyInput;
            _workingSlipProjector.SlightlyOutput = s.Slip.Projector.SlightlyOutput;
            _workingSlipProjector.ModerateInput = s.Slip.Projector.ModerateInput;
            _workingSlipProjector.ModerateOutput = s.Slip.Projector.ModerateOutput;
            _workingSlipProjector.CriticalInput = s.Slip.Projector.CriticalInput;
            _workingSlipProjector.CriticalOutput = s.Slip.Projector.CriticalOutput;
            LoadAnchorControls(_workingSlipProjector, SlipStartRaw, SlipSlightlyRaw, SlipSlightlyOutput,
                SlipModerateRaw, SlipModerateOutput, SlipCriticalRaw, SlipCriticalOutput, SlipEndRaw, SlipPresetCombo);
            RefreshCurvePlot(_workingSlipProjector, ProjectionChannel.Slip);

            SelectComboItemByTag(GForceAccelModeCombo, s.GForce.AccelMaxMode.ToString());
            SelectComboItemByTag(GForceDecelModeCombo, s.GForce.DecelMaxMode.ToString());
            GForceFixedAccelMax.Value = s.GForce.FixedAccelMaxG;
            GForceFixedDecelMax.Value = s.GForce.FixedDecelMaxG;
            RefreshGForceLearnedText();
            GForceRecommendedHzText.Text = string.Format(Strings.Get("GForce.RecommendedHz.Note"), s.GForce.RecommendedFromHz, s.GForce.RecommendedToHz);

            GForceBrakeBottomRearSustain.Value = s.GForce.BrakeBottomRearSustainPercent;
            GForceBrakeBackLowSustain.Value = s.GForce.BrakeBackLowSustainPercent;
            GForceAccelBottomRearSustain.Value = s.GForce.AccelBottomRearSustainPercent;
            GForceAccelBackLowSustain.Value = s.GForce.AccelBackLowSustainPercent;

            GForceSustainTau.Value = s.GForce.SustainTimeConstantSeconds;
            GForceTransientTau.Value = s.GForce.TransientTimeConstantSeconds;
            GForceTransientGain.Value = s.GForce.TransientGain;

            SelectComboItemByTag(GForceLateralDirectionCombo, s.GForce.LateralDirection.ToString());

            GForceShakeEnabled.IsChecked = s.GForce.IntegrateWheelLockAndSlip;
            GForceShakeFrequency.Value = s.GForce.ShakeFrequencyHz;
            GForceShakeLockScale.Value = s.GForce.WheelLockShakeScale;
            GForceShakeSlipScale.Value = s.GForce.WheelSlipShakeScale;

            EnableDiagnosticsCheckBox.IsChecked = s.General.EnableDiagnostics;
            ExportCsvCheckBox.IsChecked = s.General.ExportCsv;
        }

        private void RefreshGForceLearnedText()
        {
            QAdvanceFeedbackSettings s = _plugin.Settings;
            GForceAccelLearnedText.Text = string.Format(Strings.Get("GForce.LearnedValueFormat"), s.GForce.CurrentLearnedAccelMaxG);
            GForceDecelLearnedText.Text = string.Format(Strings.Get("GForce.LearnedValueFormat"), s.GForce.CurrentLearnedDecelMaxG);
        }

        private void LoadChannel(
            WheelChannelSettings channel, List<SourceRow> rows,
            CheckBox pulseEnabled, MahApps.Metro.Controls.NumericUpDown pulseGap, MahApps.Metro.Controls.NumericUpDown pulseMin)
        {
            rows[0].SourceBox.Text = channel.SourceFrontLeft; SetScriptTypeVisual(rows[0].ScriptTypeButton, CoerceLoadedScriptType(channel.ScriptTypeFrontLeft));
            rows[1].SourceBox.Text = channel.SourceFrontRight; SetScriptTypeVisual(rows[1].ScriptTypeButton, CoerceLoadedScriptType(channel.ScriptTypeFrontRight));
            rows[2].SourceBox.Text = channel.SourceRearLeft; SetScriptTypeVisual(rows[2].ScriptTypeButton, CoerceLoadedScriptType(channel.ScriptTypeRearLeft));
            rows[3].SourceBox.Text = channel.SourceRearRight; SetScriptTypeVisual(rows[3].ScriptTypeButton, CoerceLoadedScriptType(channel.ScriptTypeRearRight));
            foreach (SourceRow row in rows) RefreshRowButton(row);

            pulseEnabled.IsChecked = channel.Pulse.Enabled;
            pulseGap.Value = channel.Pulse.GapMs;
            pulseMin.Value = channel.Pulse.MinValue;
        }

        private void SaveToSettings()
        {
            QAdvanceFeedbackSettings s = _plugin.Settings;

            SaveChannel(s.Lock, _lockRows, LockPulseEnabled, LockPulseGapMs, LockPulseMinValue);
            s.Lock.SourceMode = ParseEnum(GetSelectedTag(LockSourceModeCombo, s.Lock.SourceMode.ToString()), s.Lock.SourceMode);
            s.Lock.BrakeThresholdPercent = LockBrakeThreshold.Value ?? s.Lock.BrakeThresholdPercent;
            CopyProjector(_workingLockProjector, s.Lock.Projector);

            SaveChannel(s.Slip, _slipRows, SlipPulseEnabled, SlipPulseGapMs, SlipPulseMinValue);
            s.Slip.SourceMode = ParseEnum(GetSelectedTag(SlipSourceModeCombo, s.Slip.SourceMode.ToString()), s.Slip.SourceMode);
            s.Slip.BrakeThresholdPercent = SlipBrakeThreshold.Value ?? s.Slip.BrakeThresholdPercent;
            s.Slip.ThrottleThresholdPercent = SlipThrottleThreshold.Value ?? s.Slip.ThrottleThresholdPercent;
            CopyProjector(_workingSlipProjector, s.Slip.Projector);

            s.GForce.AccelMaxMode = ParseEnum(GetSelectedTag(GForceAccelModeCombo, s.GForce.AccelMaxMode.ToString()), s.GForce.AccelMaxMode);
            s.GForce.DecelMaxMode = ParseEnum(GetSelectedTag(GForceDecelModeCombo, s.GForce.DecelMaxMode.ToString()), s.GForce.DecelMaxMode);
            s.GForce.FixedAccelMaxG = GForceFixedAccelMax.Value ?? s.GForce.FixedAccelMaxG;
            s.GForce.FixedDecelMaxG = GForceFixedDecelMax.Value ?? s.GForce.FixedDecelMaxG;

            s.GForce.BrakeBottomRearSustainPercent = GForceBrakeBottomRearSustain.Value ?? s.GForce.BrakeBottomRearSustainPercent;
            s.GForce.BrakeBackLowSustainPercent = GForceBrakeBackLowSustain.Value ?? s.GForce.BrakeBackLowSustainPercent;
            s.GForce.AccelBottomRearSustainPercent = GForceAccelBottomRearSustain.Value ?? s.GForce.AccelBottomRearSustainPercent;
            s.GForce.AccelBackLowSustainPercent = GForceAccelBackLowSustain.Value ?? s.GForce.AccelBackLowSustainPercent;

            s.GForce.SustainTimeConstantSeconds = GForceSustainTau.Value ?? s.GForce.SustainTimeConstantSeconds;
            s.GForce.TransientTimeConstantSeconds = GForceTransientTau.Value ?? s.GForce.TransientTimeConstantSeconds;
            s.GForce.TransientGain = GForceTransientGain.Value ?? s.GForce.TransientGain;

            s.GForce.LateralDirection = ParseEnum(GetSelectedTag(GForceLateralDirectionCombo, s.GForce.LateralDirection.ToString()), s.GForce.LateralDirection);

            s.GForce.IntegrateWheelLockAndSlip = GForceShakeEnabled.IsChecked == true;
            s.GForce.ShakeFrequencyHz = GForceShakeFrequency.Value ?? s.GForce.ShakeFrequencyHz;
            s.GForce.WheelLockShakeScale = GForceShakeLockScale.Value ?? s.GForce.WheelLockShakeScale;
            s.GForce.WheelSlipShakeScale = GForceShakeSlipScale.Value ?? s.GForce.WheelSlipShakeScale;

            s.General.EnableDiagnostics = EnableDiagnosticsCheckBox.IsChecked == true;
            s.General.ExportCsv = ExportCsvCheckBox.IsChecked == true;

            _plugin.ApplySettings();
            LoadFromSettings();
            RefreshGForceModeControls();
            RefreshGForceShakeControls();
        }

        private void SaveChannel(
            WheelChannelSettings channel, List<SourceRow> rows,
            CheckBox pulseEnabled, MahApps.Metro.Controls.NumericUpDown pulseGap, MahApps.Metro.Controls.NumericUpDown pulseMin)
        {
            channel.SourceFrontLeft = rows[0].SourceBox.Text.Trim();
            channel.SourceFrontRight = rows[1].SourceBox.Text.Trim();
            channel.SourceRearLeft = rows[2].SourceBox.Text.Trim();
            channel.SourceRearRight = rows[3].SourceBox.Text.Trim();

            // BUG 1's other half, applied on the way BACK into settings too: a hand-toggled
            // JavaScript/NCalc row while the evaluator is unavailable this session (impossible via
            // the UI, since the toggle is hidden - but defensive here regardless) is never persisted
            // as anything other than Plain.
            channel.ScriptTypeFrontLeft = CoerceLoadedScriptType(GetScriptType(rows[0].ScriptTypeButton));
            channel.ScriptTypeFrontRight = CoerceLoadedScriptType(GetScriptType(rows[1].ScriptTypeButton));
            channel.ScriptTypeRearLeft = CoerceLoadedScriptType(GetScriptType(rows[2].ScriptTypeButton));
            channel.ScriptTypeRearRight = CoerceLoadedScriptType(GetScriptType(rows[3].ScriptTypeButton));

            channel.Pulse.Enabled = pulseEnabled.IsChecked == true;
            channel.Pulse.GapMs = pulseGap.Value ?? channel.Pulse.GapMs;
            channel.Pulse.MinValue = pulseMin.Value ?? channel.Pulse.MinValue;
        }

        /// <summary>
        /// Persisted verbatim, NOT through WithClampedSortedAnchors - that method's own remarks warn
        /// against reading its result back as "the safe values" (it resolves neither duplicate inputs
        /// nor falling outputs, so it is not actually safe on its own). OutputProjector re-sanitises
        /// from scratch every time the engine is rebuilt, so persisting exactly what the driver typed
        /// is both simpler and correct.
        /// </summary>
        private static void CopyProjector(ProjectorSettings from, ProjectorSettings to)
        {
            to.Preset = from.Preset;
            to.StartInput = from.StartInput;
            to.EndInput = from.EndInput;
            to.SlightlyInput = from.SlightlyInput;
            to.SlightlyOutput = from.SlightlyOutput;
            to.ModerateInput = from.ModerateInput;
            to.ModerateOutput = from.ModerateOutput;
            to.CriticalInput = from.CriticalInput;
            to.CriticalOutput = from.CriticalOutput;
        }

        // ------------------------------------------------------------------------------------
        // Small formatting/parsing helpers
        // ------------------------------------------------------------------------------------

        private static void SelectComboItemByTag(ComboBox box, string tag)
        {
            foreach (var obj in box.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static string GetSelectedTag(ComboBox box, string fallback)
            => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

        private static string FormatNum(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static T ParseEnum<T>(string text, T fallback) where T : struct
            => Enum.TryParse(text, out T parsed) ? parsed : fallback;
    }
}

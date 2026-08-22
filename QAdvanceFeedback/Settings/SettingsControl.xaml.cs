using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
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
        private readonly MotorsExportAvailabilityProvider _motorsExport = new MotorsExportAvailabilityProvider();

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
        private readonly bool _lockMotorsExportAvailable;
        private readonly bool _slipMotorsExportAvailable;

        private readonly ProjectorSettings _workingLockProjector = new ProjectorSettings();
        private readonly ProjectorSettings _workingSlipProjector = new ProjectorSettings();

        // Guards every programmatic write to a control (loading settings, applying a preset) so the
        // resulting ValueChanged/SelectionChanged events do not re-enter the "user edited this"
        // handlers, AND drives the Apply button's enabled state (see MarkDirty/MarkClean below and
        // ApplyDirtyState's own remarks for why this single small class replaced the old bare bool).
        private readonly ApplyDirtyState _dirty = new ApplyDirtyState();

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

        /// <summary>
        /// G-FORCE READOUT LIVE-REFRESH FIX (docs\v1068-curve-ui-port-and-gforce-readout-report.md):
        /// <see cref="RefreshGForceLearnedText"/> used to be invoked ONLY at construction
        /// (<see cref="LoadFromSettings"/>) and whenever the Accel/Decel mode combo's own selection
        /// actually changed - never on a timer. The underlying <c>GForceMaxLearner</c> instances keep
        /// accumulating correctly every frame regardless (confirmed against the <c>1.0.6_logs</c>
        /// sessions: <c>Diag.GForce.LearnedDecelMaxG</c> genuinely reaches 3.85-3.94g), and
        /// <see cref="Settings.GForceSettings.EffectiveAccelMaxG"/>/<c>EffectiveDecelMaxG</c> - the
        /// values that actually FEED the live G-derived severity path - are queried fresh every single
        /// telemetry frame, so THAT path was never affected. Only this settings-panel TEXT was a
        /// one-shot snapshot: once the panel loaded (typically before the driver had done a single lap,
        /// still "no data yet") nothing ever told it to look again, so it read as permanently stuck
        /// even after evidence existed - a driver who left the panel open while driving would never see
        /// it update. This is a PURELY COSMETIC bug (confirmed - the behavioural path never read a
        /// stale/cached value; only this label's own text did), unlike the class of bug it superficially
        /// resembles (a wrong/empty lookup KEY causing a frozen readout - see
        /// docs\shakeit-silence-diagnosis-report.md's own surface-bucket precedent) - here every key/
        /// channel/learner reference was already correct throughout (verified byte-for-byte identical
        /// across 1.0.6.5 through 1.0.6.8's own G-Force settings/engine/wiring files); the defect was
        /// simply the absence of any periodic re-query. Fixed with a lightweight one-second
        /// <see cref="DispatcherTimer"/>, started in the constructor and stopped on <see cref="FrameworkElement.Unloaded"/>
        /// so it cannot outlive this control or accumulate across repeated settings-panel opens.
        /// </summary>
        private readonly DispatcherTimer _gforceRefreshTimer;

        public SettingsControl(QAdvanceFeedback plugin, PluginManager pluginManager)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _pluginManager = pluginManager;

            _lockMotorsExportAvailable = _motorsExport.IsLockAvailable(_pluginManager);
            _slipMotorsExportAvailable = _motorsExport.IsSlipAvailable(_pluginManager);

            LocalizeStaticText();
            WireSourceButtons();
            WireScriptTypeToggles();
            WireSourceModeToggles();
            WireDirtyTracking();

            WireAnchorEvents(_workingLockProjector, LockStartRaw, LockStartOutput, LockSlightlyRaw, LockSlightlyOutput,
                LockModerateRaw, LockModerateOutput, LockCriticalRaw, LockCriticalOutput, LockEndRaw, LockEndOutput,
                LockPresetCombo, ProjectionChannel.Lock);
            WireAnchorEvents(_workingSlipProjector, SlipStartRaw, SlipStartOutput, SlipSlightlyRaw, SlipSlightlyOutput,
                SlipModerateRaw, SlipModerateOutput, SlipCriticalRaw, SlipCriticalOutput, SlipEndRaw, SlipEndOutput,
                SlipPresetCombo, ProjectionChannel.Slip);

            WireFlattenRangeEvents(_workingLockProjector, LockSlightlyFlattenRange, LockModerateFlattenRange, LockCriticalFlattenRange, ProjectionChannel.Lock);
            WireFlattenRangeEvents(_workingSlipProjector, SlipSlightlyFlattenRange, SlipModerateFlattenRange, SlipCriticalFlattenRange, ProjectionChannel.Slip);

            LoadFromSettings();

            // G-FORCE READOUT LIVE-REFRESH FIX - see _gforceRefreshTimer's own remarks for the root
            // cause. One second is frequent enough to feel live without meaningfully taxing the UI
            // thread; DispatcherPriority.Background keeps it from competing with anything more urgent
            // SimHub's own Dispatcher queue is doing. Guarded by SafeUiAction like every other UI-thread
            // entry point in this control (see this constructor's own remarks below), and stopped on
            // Unloaded so a torn-down settings panel cannot leave a ticking timer behind.
            _gforceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _gforceRefreshTimer.Tick += (s, e) => SafeUiAction(RefreshGForceLearnedText, HealthSubsystems.SettingsUi);
            _gforceRefreshTimer.Start();
            Unloaded += (s, e) => _gforceRefreshTimer.Stop();

            // PIPELINE-EXCEPTION-SAFETY (docs\pipeline-exception-safety-report.md): unlike this
            // control's own CONSTRUCTOR (already guarded by QAdvanceFeedback.GetWPFSettingsControl's
            // try/catch), these are WPF Button.Click handlers invoked directly off SimHub's own
            // Dispatcher, well after construction - nothing upstream of THEM would catch a throw. Each
            // is wrapped in SafeUiAction so a fault here degrades to "this click did nothing, see the
            // health section" rather than reaching SimHub's own UI thread unguarded.
            ApplyButton.Click += (s, e) => SafeUiAction(SaveToSettings, HealthSubsystems.SettingsUi);
            RestoreAllDefaultsButton.Click += (s, e) => SafeUiAction(RestoreAllDefaults, HealthSubsystems.SettingsUi);
            LockResetSources.Click += (s, e) => SafeUiAction(() => ResetSourcesToDefault(isLock: true), HealthSubsystems.SettingsUi);
            SlipResetSources.Click += (s, e) => SafeUiAction(() => ResetSourcesToDefault(isLock: false), HealthSubsystems.SettingsUi);

            // Under Auto, the fixed-value spinner stays visible but read-only (IsEnabled=false, not
            // Collapsed) - the brief's own wording - and its value is refreshed to the currently
            // learned figure so the field reads as "here is what Auto has learned", not a stale
            // Fixed-mode number sitting there doing nothing. MarkDirty() is called explicitly here
            // (rather than relying on the fixed spinner's own ValueChanged, wired in
            // WireDirtyTracking) because switching to Auto need not actually change that spinner's
            // Value at all, yet is itself very much a setting the driver just edited.
            GForceAccelModeCombo.SelectionChanged += (s, e) => { MarkDirty(); RefreshGForceModeControls(); };
            GForceDecelModeCombo.SelectionChanged += (s, e) => { MarkDirty(); RefreshGForceModeControls(); };
            GForceLateralDirectionCombo.SelectionChanged += (s, e) => MarkDirty();
            // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock only; see
            // RefreshLockAnchorLabelsForPattern's own remarks.
            LockNormalizePatternCombo.SelectionChanged += (s, e) => { MarkDirty(); RefreshLockAnchorLabelsForPattern(); };
            RefreshGForceModeControls();

            // While the shake feature is OFF, the settings it governs (frequency, both scales) are
            // greyed out - obvious at a glance that they currently do nothing. Pure UI wiring (sets
            // IsEnabled on WPF controls from a WPF control's own IsChecked) - there is no non-trivial
            // logic here to extract into a testable seam; "unverified without a live WPF window",
            // same as every other visual-only behaviour in this control (see the settings-control
            // wiring report's own remarks on the three UI bugs it fixed).
            GForceShakeEnabled.Checked += (s, e) => { MarkDirty(); RefreshGForceShakeControls(); };
            GForceShakeEnabled.Unchecked += (s, e) => { MarkDirty(); RefreshGForceShakeControls(); };
            RefreshGForceShakeControls();

            // Belt-and-suspenders: every load path above already runs inside a BeginLoading scope
            // (LoadFromSettings wraps its own body; RefreshGForceModeControls/RefreshGForceShakeControls
            // wrap theirs), so IsDirty should already be false here - this final call is what actually
            // GUARANTEES a fresh control starts with Apply disabled, even if some future load path
            // forgets its own guard.
            MarkClean();

            // LAST, deliberately: every reflection wrapper this control uses (SimHubScriptEditor/
            // PropertyPickerLauncher/SimHubExpressionEvaluator/MotorsExportAvailabilityProvider) has
            // already been force-resolved above (WireSourceButtons -> RefreshRowButton -> ResolveMode
            // reads SimHubScriptEditor.IsAvailable/_picker.IsAvailable for every row;
            // LocalizeStaticText/WireScriptTypeToggles read _evaluator.IsAvailable; the constructor's
            // own _lockMotorsExportAvailable/_slipMotorsExportAvailable fields already probed
            // MotorsExportAvailabilityProvider) - so any fault any of them hit has already been
            // recorded into HealthRegistry by the time we get here, and this reads a fully up-to-date
            // snapshot rather than a stale one.
            HealthCopyDetailsButton.Click += (s, e) => CopyHealthDetailsToClipboard();
            RefreshHealthUi();
        }

        // ------------------------------------------------------------------------------------
        // Plugin health section (Core.Health.HealthRegistry) - invisible/one-line when nothing is
        // wrong; expands to one warning block per degraded subsystem otherwise. See this control's own
        // constructor remarks for why this is refreshed exactly once, last, rather than reactively.
        // ------------------------------------------------------------------------------------

        private void RefreshHealthUi()
        {
            IReadOnlyList<HealthEntry> entries = HealthRegistry.Snapshot();
            HealthIssuesPanel.Children.Clear();

            if (entries.Count == 0)
            {
                HealthAllGoodText.Visibility = Visibility.Visible;
                HealthAllGoodText.Text = Strings.Get("Health.AllGood");
                HealthCopyDetailsButton.Visibility = Visibility.Collapsed;
                HealthCopyDetailsDoneText.Visibility = Visibility.Collapsed;
                return;
            }

            HealthAllGoodText.Visibility = Visibility.Collapsed;
            HealthCopyDetailsButton.Visibility = Visibility.Visible;

            foreach (HealthEntry entry in entries)
            {
                string subsystemName = Strings.Get("Health.Subsystem." + entry.Subsystem);
                string impact = Strings.Get(entry.ImpactKey);
                string text = subsystemName + ": " + impact;
                // THE CASE THE OWNER NAMED EXPLICITLY: a reflection target moved/a SimHub dependency
                // changed - say so plainly rather than showing an opaque failure.
                if (entry.IsSimHubCompatibilityIssue)
                    text += " " + Strings.Get("Health.SimHubUpdateNeeded");

                var block = new TextBlock
                {
                    Text = "⚠ " + text,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.Bold,
                    Foreground = entry.Severity == HealthSeverity.Failed ? Brushes.Firebrick : Brushes.DarkOrange,
                    Margin = new Thickness(4, 0, 4, 8),
                };
                HealthIssuesPanel.Children.Add(block);
            }
        }

        /// <summary>Copies every current entry's raw technical detail (subsystem, severity, timestamps,
        /// occurrence count, exception text) to the clipboard - deliberately English/unlocalized, since
        /// this exists purely so the owner can paste it into a bug report, not for a driver to read as
        /// prose (see <see cref="HealthEntry.Detail"/>'s own remarks). Clipboard access itself is
        /// wrapped - a locked clipboard (another app mid-copy) must not throw out of a settings-UI
        /// button click.</summary>
        private void CopyHealthDetailsToClipboard()
        {
            var sb = new System.Text.StringBuilder();
            foreach (HealthEntry entry in HealthRegistry.Snapshot())
            {
                sb.Append('[').Append(entry.Severity).Append("] ").Append(entry.Subsystem);
                if (entry.IsSimHubCompatibilityIssue) sb.Append(" (possible SimHub version mismatch)");
                sb.Append(" - first: ").Append(entry.FirstOccurredUtc.ToString("u", CultureInfo.InvariantCulture));
                sb.Append(", last: ").Append(entry.LastOccurredUtc.ToString("u", CultureInfo.InvariantCulture));
                sb.Append(", occurrences: ").Append(entry.OccurrenceCount);
                sb.AppendLine();
                sb.AppendLine(entry.Detail);
                sb.AppendLine();
            }

            try { Clipboard.SetText(sb.ToString()); }
            catch (Exception) { /* clipboard can be locked by another process - never let this throw */ }

            HealthCopyDetailsDoneText.Visibility = Visibility.Visible;
            HealthCopyDetailsDoneText.Text = Strings.Get("Health.CopyDetails.Done");
        }

        // ------------------------------------------------------------------------------------
        // Apply-button dirty tracking - see ApplyDirtyState's own remarks for the guard/counter
        // design. MarkDirty/MarkClean are the ONE centralised path every control's change handler (in
        // WireDirtyTracking below, plus the few handlers with their own extra business logic elsewhere
        // in this file) calls, so a newly added control has one obvious thing to wire.
        // ------------------------------------------------------------------------------------

        private void MarkDirty()
        {
            _dirty.MarkDirty();
            ApplyButton.IsEnabled = _dirty.IsDirty;
        }

        private void MarkClean()
        {
            _dirty.MarkClean();
            ApplyButton.IsEnabled = false;
        }

        /// <summary>
        /// Routes every remaining PLAIN input control - the ones with no business-logic handler of
        /// their own elsewhere in this file - to MarkDirty. Enumerated explicitly, by control type,
        /// rather than hooked selectively: a control silently left out here would let a driver believe
        /// an edit was saved when Apply never actually enabled, which is worse than no dirty tracking
        /// at all. The controls NOT listed here still reach MarkDirty, just via a handler that also
        /// does other work:
        /// <list type="bullet">
        /// <item>Curve anchor spinners (Lock/SlipStartRaw..EndRaw) - <see cref="OnAnchorRawChanged"/>/
        /// <see cref="OnAnchorOutputChanged"/>, wired in <see cref="WireAnchorEvents"/>.</item>
        /// <item>Curve preset combos (Lock/SlipPresetCombo) - the SelectionChanged lambda in
        /// <see cref="WireAnchorEvents"/>.</item>
        /// <item>Source mode combos (Lock/SlipSourceModeCombo) - <see cref="ApplySourceDefaultsForMode"/>,
        /// reached via <see cref="OnSourceModeChanged"/>.</item>
        /// <item>Per-source "Reset to default" buttons - also <see cref="ApplySourceDefaultsForMode"/>,
        /// reached via <see cref="ResetSourcesToDefault"/>.</item>
        /// <item>Script-type toggle buttons - <see cref="ScriptTypeToggle_Click"/> and, for the
        /// script-editor round trip, <see cref="EditInto"/>.</item>
        /// <item>Property-picker/script-editor buttons - indirectly, via the source TextBox's own
        /// TextChanged (wired below), since both PickInto and EditInto write into that TextBox.</item>
        /// <item>GForce accel/decel mode combos, lateral direction combo, and the shake ToggleSwitch -
        /// wired directly in the constructor, beside their own RefreshGForceModeControls/
        /// RefreshGForceShakeControls calls.</item>
        /// </list>
        /// </summary>
        private void WireDirtyTracking()
        {
            // ---- mah:NumericUpDown spinners NOT already covered by the curve anchor editor. ----
            MahApps.Metro.Controls.NumericUpDown[] spinners =
            {
                LockBrakeThreshold, LockSensibility, LockWMax, LockWMin, LockWFront, LockWRear,
                LockPulseGapMs, LockPulseMinValue,
                SlipBrakeThreshold, SlipThrottleThreshold, SlipWMax, SlipWMin, SlipWFront, SlipWRear,
                SlipFloorFactor, SlipPulseGapMs, SlipPulseMinValue,
                GForceFixedAccelMax, GForceFixedDecelMax,
                GForceBrakeBottomRearSustain, GForceBrakeBackLowSustain,
                GForceAccelBottomRearSustain, GForceAccelBackLowSustain,
                GForceSustainTau, GForceTransientTau, GForceTransientGain,
                GForceAutoTransitionScale, GForceFixedTransitionScale,
                GForceShakeFrequency, GForceShakeLockScale, GForceShakeSlipScale
            };
            foreach (MahApps.Metro.Controls.NumericUpDown spinner in spinners)
                spinner.ValueChanged += (s, e) => MarkDirty();

            // ---- Source text boxes. ----
            TextBox[] sourceBoxes =
            {
                LockSourceFl, LockSourceFr, LockSourceRl, LockSourceRr,
                SlipSourceFl, SlipSourceFr, SlipSourceRl, SlipSourceRr
            };
            foreach (TextBox box in sourceBoxes)
                box.TextChanged += (s, e) => MarkDirty();

            // ---- Checkboxes. ----
            CheckBox[] checkBoxes = { LockPulseEnabled, SlipPulseEnabled, EnableDiagnosticsCheckBox, ExportCsvCheckBox };
            foreach (CheckBox box in checkBoxes)
            {
                box.Checked += (s, e) => MarkDirty();
                box.Unchecked += (s, e) => MarkDirty();
            }
        }

        private void RefreshGForceShakeControls()
        {
            // Self-guarded: called both from a genuine user edit (the ToggleSwitch handlers above,
            // which call MarkDirty() themselves BEFORE this) and from pure reload paths (construction,
            // after Apply, after Restore) where the IsEnabled flips below must never mark dirty.
            using (_dirty.BeginLoading())
            {
                bool enabled = GForceShakeEnabled.IsChecked == true;
                GForceShakeFrequency.IsEnabled = enabled;
                GForceShakeLockScale.IsEnabled = enabled;
                GForceShakeSlipScale.IsEnabled = enabled;
            }
        }

        /// <summary>
        /// Refreshes the mode-dependent bits of the G-Force Maxima group (docs\robust-auto-gforce-report.md,
        /// UI readout requirement). UNLIKE this control's previous revision: the FIXED-value spinner is
        /// now ALWAYS enabled/editable in BOTH modes (never disabled, never overwritten with the learned
        /// value) - it is what AUTO falls back to below the evidence threshold, so hiding or overwriting
        /// it would make that fallback value impossible to see or change. The readout label to its right
        /// is what actually communicates AUTO's own state - see <see cref="RefreshGForceLearnedText"/>.
        /// </summary>
        private void RefreshGForceModeControls()
        {
            // Self-guarded - see RefreshGForceShakeControls's own remarks; the readout-text refresh below
            // is a DERIVED refresh, never itself "the user's edit".
            using (_dirty.BeginLoading())
            {
                RefreshGForceLearnedText();
            }
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
            // _lockMotorsExportAvailable/_slipMotorsExportAvailable's own remarks for why this changed). ----
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

            // ---- TRIGGER THRESHOLD - its own section, above Sources (owner-requested restructure,
            // both modes, gates the WHOLE channel - see docs\lock-and-animation-report.md and the
            // note text itself for the exact semantics, including the ShakeIt-source caveat). ----
            LockTriggerThresholdGroup.Header = SlipTriggerThresholdGroup.Header = Strings.Get("Group.TriggerThreshold");
            LockLblBrakeThreshold.Text = Strings.Get("Sources.Threshold.LockBrake");
            LockLblBrakeThreshold.ToolTip = Strings.Get("Sources.Threshold.Lock.Note");
            LockBrakeThreshold.ToolTip = Strings.Get("Sources.Threshold.Lock.Note");
            LockThresholdNote.Text = Strings.Get("Sources.Threshold.Lock.Note");
            SlipLblBrakeThreshold.Text = Strings.Get("Sources.Threshold.SlipBrake");
            SlipLblThrottleThreshold.Text = Strings.Get("Sources.Threshold.SlipThrottle");
            SlipLblBrakeThreshold.ToolTip = SlipLblThrottleThreshold.ToolTip = Strings.Get("Sources.Threshold.Slip.Note");
            SlipBrakeThreshold.ToolTip = SlipThrottleThreshold.ToolTip = Strings.Get("Sources.Threshold.Slip.Note");
            SlipThresholdNote.Text = Strings.Get("Sources.Threshold.Slip.Note");

            // LockSensibility (docs\lock-and-animation-report.md) - matches SimHub's own
            // WheelsLockContainer.LockSensibility exactly; Lock-only (Slip has no equivalent).
            LockLblSensibility.Text = Strings.Get("Sources.Threshold.LockSensibility");
            LockSensibilityNote.Text = Strings.Get("Sources.Threshold.LockSensibility.Note");
            LockLblSensibility.ToolTip = LockSensibility.ToolTip = Strings.Get("Sources.Threshold.LockSensibility.Note");

            // ---- AGGREGATION (docs\aggregation-report.md) - how the four wheels combine into
            // Front/Rear/Left/Right/All; the owner's own physically-motivated scheme. Only Wheel Slip
            // has a floor-factor control (see SlipLblFloorFactor/SlipFloorFactorNote below). ----
            LockAggregationGroup.Header = SlipAggregationGroup.Header = Strings.Get("Group.Aggregation");
            LockAggregationNote.Text = Strings.Get("Aggregation.Lock.Note");
            SlipAggregationNote.Text = Strings.Get("Aggregation.Slip.Note");
            LockLblWMax.Text = SlipLblWMax.Text = Strings.Get("Aggregation.WMax.Label");
            LockLblWMin.Text = SlipLblWMin.Text = Strings.Get("Aggregation.WMin.Label");
            LockLblWFront.Text = SlipLblWFront.Text = Strings.Get("Aggregation.WFront.Label");
            LockLblWRear.Text = SlipLblWRear.Text = Strings.Get("Aggregation.WRear.Label");
            LockAggregationAxleHelp.Text = SlipAggregationAxleHelp.Text = Strings.Get("Aggregation.AxleHelp");
            LockAggregationSideHelp.Text = Strings.Get("Aggregation.SideHelp.Lock");
            SlipAggregationSideHelp.Text = Strings.Get("Aggregation.SideHelp.Slip");
            SlipLblFloorFactor.Text = Strings.Get("Aggregation.FloorFactor.Label");
            SlipFloorFactorNote.Text = Strings.Get("Aggregation.FloorFactor.Note");
            SlipLblFloorFactor.ToolTip = SlipFloorFactor.ToolTip = Strings.Get("Aggregation.FloorFactor.Note");

            LockCurveGroup.Header = SlipCurveGroup.Header = Strings.Get("Group.Curve");

            // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock ONLY; Slip has no selector.
            LockLblNormalizePattern.Text = Strings.Get("Curve.NormalizePattern.Label");
            LockNormalizePatternMaxGripOnly.Content = Strings.Get("Curve.NormalizePattern.MaxGripOnly");
            LockNormalizePatternMapping.Content = Strings.Get("Curve.NormalizePattern.Mapping");

            LockLblPreset.Text = SlipLblPreset.Text = Strings.Get("Curve.Preset.Label");
            LockPresetLinear.Content = SlipPresetLinear.Content = Strings.Get("Curve.Preset.Linear");
            LockPresetCurve.Content = SlipPresetCurve.Content = Strings.Get("Curve.Preset.Curve");
            LockPresetCustom.Content = SlipPresetCustom.Content = Strings.Get("Curve.Preset.Custom");
            LockColRawHeader.Text = SlipColRawHeader.Text = Strings.Get("Curve.Column.RawValue");
            LockColOutputHeader.Text = SlipColOutputHeader.Text = Strings.Get("Curve.Column.OutputValue");
            LockColFlattenRangeHeader.Text = SlipColFlattenRangeHeader.Text = Strings.Get("Curve.Column.FlattenRange");
            LockColFlattenRangeHeader.ToolTip = SlipColFlattenRangeHeader.ToolTip
                = LockSlightlyFlattenRange.ToolTip = LockModerateFlattenRange.ToolTip = LockCriticalFlattenRange.ToolTip
                = SlipSlightlyFlattenRange.ToolTip = SlipModerateFlattenRange.ToolTip = SlipCriticalFlattenRange.ToolTip
                = Strings.Get("Curve.Column.FlattenRange.Note");

            // Start/End's own Desc text quotes this channel's shipped DEFAULT input threshold (Ships
            // at {0} by default) - read from ProjectorSettings.CreateShippedDefault, the single source
            // of truth the "Curve" preset itself stamps, rather than a hand-typed number that could
            // drift from the real default the next time it changes (see that method's own remarks).
            ProjectorSettings lockDefaults = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            ProjectorSettings slipDefaults = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Slip);

            LockAnchorNoteText.Text = Strings.Get("Curve.Lock.AnchorNote");
            LockLblStart.Text = Strings.Get("Curve.StartPoint.Label");
            LockStartOutput.ToolTip = Strings.Get("Curve.StartOutput.Help");
            LockDescStart.Text = string.Format(Strings.Get("Curve.Lock.StartPoint.Desc"), FormatNum(lockDefaults.StartInput));
            // Lock's Slightly/Ideal label+description LIVE-SWITCH with the Normalize Pattern selector -
            // set by RefreshLockAnchorLabelsForPattern (called once below, then again on every
            // LockNormalizePatternCombo.SelectionChanged and LoadFromSettings), not here.
            LockLblCritical.Text = Strings.Get("Curve.Anchor.Critical") + ":";
            LockDescCritical.Text = Strings.Get("Curve.Lock.Critical.Desc");
            LockLblEnd.Text = Strings.Get("Curve.EndPoint.Label");
            LockEndOutput.ToolTip = Strings.Get("Curve.EndOutput.Help");
            LockDescEnd.Text = string.Format(Strings.Get("Curve.Lock.EndPoint.Desc"), FormatNum(lockDefaults.EndInput));

            SlipAnchorNoteText.Text = Strings.Get("Curve.Slip.AnchorNote");
            SlipLblStart.Text = Strings.Get("Curve.StartPoint.Label");
            SlipStartOutput.ToolTip = Strings.Get("Curve.StartOutput.Help");
            SlipDescStart.Text = string.Format(Strings.Get("Curve.Slip.StartPoint.Desc"), FormatNum(slipDefaults.StartInput));
            // Slip has NO Normalize Pattern selector - always the same fixed labels/descriptions.
            SlipLblSlightly.Text = Strings.Get("Curve.Anchor.Slip.Slightly") + ":";
            SlipDescSlightly.Text = Strings.Get("Curve.Slip.Slightly.Desc");
            SlipLblModerate.Text = Strings.Get("Curve.Anchor.Slip.Ideal") + ":";
            SlipDescModerate.Text = Strings.Get("Curve.Slip.Ideal.Desc");
            SlipLblCritical.Text = Strings.Get("Curve.Anchor.Critical") + ":";
            SlipDescCritical.Text = Strings.Get("Curve.Slip.Critical.Desc");
            SlipLblEnd.Text = Strings.Get("Curve.EndPoint.Label");
            SlipEndOutput.ToolTip = Strings.Get("Curve.EndOutput.Help");
            SlipDescEnd.Text = string.Format(Strings.Get("Curve.Slip.EndPoint.Desc"), FormatNum(slipDefaults.EndInput));

            RefreshLockAnchorLabelsForPattern();

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
            GForceLblAutoTransitionScale.Text = Strings.Get("GForce.TransitionScale.Auto.Label");
            GForceLblFixedTransitionScale.Text = Strings.Get("GForce.TransitionScale.Fixed.Label");
            GForceTransitionScaleNote.Text = Strings.Get("GForce.TransitionScale.Note");

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
            GeneralVersionText.Text = string.Format(Strings.Get("General.Version.Label"), GetRunningAssemblyFileVersion());

            HealthGroup.Header = Strings.Get("Group.Health");
            HealthCopyDetailsButton.Content = Strings.Get("Health.CopyDetails");
        }

        /// <summary>
        /// 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - LIVE-SWITCHES Wheel Lock's own
        /// Slightly/Ideal anchor label AND description, plus the pattern's own short explanatory text,
        /// to match whichever <see cref="NormalizePattern"/> is currently selected in
        /// <see cref="LockNormalizePatternCombo"/>. Called once from <see cref="LocalizeStaticText"/>
        /// (culture switch), again from <see cref="LoadFromSettings"/> (a driver's persisted choice),
        /// and on every <see cref="LockNormalizePatternCombo"/>.SelectionChanged (a live edit) - see
        /// this control's constructor. Self-guarded with <see cref="_dirty"/>.BeginLoading() so calling
        /// it from a pure reload path never marks the panel dirty; the SelectionChanged handler itself
        /// calls MarkDirty() explicitly first, mirroring GForceAccelModeCombo's own convention.
        /// <para/>
        /// Wheel Slip has NO equivalent - its own Slightly/Ideal label+description are set once, always,
        /// in <see cref="LocalizeStaticText"/> above.
        /// </summary>
        private void RefreshLockAnchorLabelsForPattern()
        {
            using (_dirty.BeginLoading())
            {
                bool mapping = GetSelectedTag(LockNormalizePatternCombo, "Mapping") != "MaxGripOnly";
                if (mapping)
                {
                    LockLblSlightly.Text = Strings.Get("Curve.Anchor.Lock.Mapping.Slightly") + ":";
                    LockDescSlightly.Text = Strings.Get("Curve.Lock.Mapping.Slightly.Desc");
                    LockLblModerate.Text = Strings.Get("Curve.Anchor.Lock.Mapping.Ideal") + ":";
                    LockDescModerate.Text = Strings.Get("Curve.Lock.Mapping.Ideal.Desc");
                    LockNormalizePatternDesc.Text = Strings.Get("Curve.NormalizePattern.Mapping.Desc");
                }
                else
                {
                    LockLblSlightly.Text = Strings.Get("Curve.Anchor.Lock.MaxGripOnly.Slightly") + ":";
                    LockDescSlightly.Text = Strings.Get("Curve.Lock.MaxGripOnly.Slightly.Desc");
                    LockLblModerate.Text = Strings.Get("Curve.Anchor.Lock.MaxGripOnly.Ideal") + ":";
                    LockDescModerate.Text = Strings.Get("Curve.Lock.MaxGripOnly.Ideal.Desc");
                    LockNormalizePatternDesc.Text = Strings.Get("Curve.NormalizePattern.MaxGripOnly.Desc");
                }
            }
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
                row.ActionButton.Click += async (s, e) => await SafeUiActionAsync(() => OnActionButtonClickAsync(row), HealthSubsystems.SettingsUi);
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

            // Explicit call (not just relying on the SourceBox's own TextChanged->MarkDirty wiring):
            // an edit that changes ONLY the script type/dialect, with the expression text coming back
            // unchanged, would not raise TextChanged at all (WPF's Text DP no-ops on an unchanged
            // value), which would silently leave Apply disabled despite a real, savable change.
            MarkDirty();
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

            MarkDirty();
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
            SourceMode currentMode = ParseEnum(GetSelectedTag(isLock ? LockSourceModeCombo : SlipSourceModeCombo, "Manual"), SourceMode.Manual);
            ApplySourceDefaultsForMode(isLock, currentMode);
        }

        // ------------------------------------------------------------------------------------
        // Source mode toggle - Manual vs. SimHub's own ShakeIt Motors export. ALWAYS visible and
        // switchable on both tabs, regardless of whether ShakeIt is currently resolving (see
        // _lockMotorsExportAvailable/_slipMotorsExportAvailable's own remarks - a previous version of this
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
            if (_dirty.IsLoading) return;

            ComboBox combo = isLock ? LockSourceModeCombo : SlipSourceModeCombo;
            SourceMode mode = ParseEnum(GetSelectedTag(combo, "Manual"), SourceMode.Manual);
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

            // Reached only from a genuine user action (the source-mode combo's own change, already
            // guarded above in OnSourceModeChanged, or the per-source "Reset to default" button) -
            // never from a load path - so this is safe to call unconditionally. Explicit rather than
            // relying solely on the four SourceBox TextChanged hooks: if the mode being (re)applied
            // happens to match the text already showing, WPF's Text DP would not raise TextChanged at
            // all, yet the driver still clicked something that is meant to require an Apply.
            MarkDirty();
        }

        /// <summary>
        /// Updates the three mode-dependent notes under the toggle for one channel, from whatever the
        /// combo currently shows:
        /// <list type="bullet">
        /// <item>ShakeIt mode: the concise setup guide is shown; the "not available yet" warning is
        /// ALSO shown, but only if <see cref="_lockMotorsExportAvailable"/>/<see cref="_slipMotorsExportAvailable"/>
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
            SourceMode mode = ParseEnum(GetSelectedTag(combo, "Manual"), SourceMode.Manual);
            bool available = isLock ? _lockMotorsExportAvailable : _slipMotorsExportAvailable;
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
            using (_dirty.BeginLoading())
            {
                SelectComboItemByTag(combo, mode.ToString());
            }
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
            // NOTE ON APPLY'S ENABLED STATE AFTER THIS: the brief asked for Restore to leave Apply
            // enabled, reasoning that "the restore is not persisted until Apply" - but that is not what
            // this method actually does, and was already true before dirty tracking existed (see this
            // method's own doc comment above: "Destructive and immediate (it also persists, exactly
            // like the ordinary Apply flow)"). ApplySettings() below calls ConfigStore.Save
            // immediately, so by the time this method returns, the restored defaults are ALREADY on
            // disk - there is nothing left for a subsequent Apply click to do. Matching that actual
            // behaviour (per the brief's own "unless you determine restore already saves" escape
            // hatch) rather than the stated assumption, this leaves Apply DISABLED, exactly like a
            // normal Apply click does.
            _plugin.ApplySettings();
            LoadFromSettings();
            RefreshGForceModeControls();
            RefreshGForceShakeControls();
            MarkClean();
        }

        // ------------------------------------------------------------------------------------
        // Curve editor - all eight spinners per channel funnel through ProjectorAnchorEditor, which
        // owns the "null means leave unchanged" and "a real edit switches the preset to Custom"
        // rules, implemented once in Core and unit-tested there.
        // ------------------------------------------------------------------------------------

        private void WireAnchorEvents(
            ProjectorSettings working,
            MahApps.Metro.Controls.NumericUpDown startRaw, MahApps.Metro.Controls.NumericUpDown startOutput,
            MahApps.Metro.Controls.NumericUpDown slightlyRaw, MahApps.Metro.Controls.NumericUpDown slightlyOutput,
            MahApps.Metro.Controls.NumericUpDown moderateRaw, MahApps.Metro.Controls.NumericUpDown moderateOutput,
            MahApps.Metro.Controls.NumericUpDown criticalRaw, MahApps.Metro.Controls.NumericUpDown criticalOutput,
            MahApps.Metro.Controls.NumericUpDown endRaw, MahApps.Metro.Controls.NumericUpDown endOutput,
            ComboBox presetCombo, ProjectionChannel channel)
        {
            startRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Start, e.NewValue, channel);
            slightlyRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Slightly, e.NewValue, channel);
            moderateRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Moderate, e.NewValue, channel);
            criticalRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.Critical, e.NewValue, channel);
            endRaw.ValueChanged += (s, e) => OnAnchorRawChanged(working, AnchorSlot.End, e.NewValue, channel);

            // PRE-RELEASE ADDITION: Start/End output funnel through the SAME ProjectorAnchorEditor
            // "editing switches the preset to Custom" rule as every other output cell - see
            // ProjectorAnchorEditor.SetOutput's own remarks for why Start/End are no longer rejected
            // there.
            startOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Start, e.NewValue, channel);
            slightlyOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Slightly, e.NewValue, channel);
            moderateOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Moderate, e.NewValue, channel);
            criticalOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.Critical, e.NewValue, channel);
            endOutput.ValueChanged += (s, e) => OnAnchorOutputChanged(working, AnchorSlot.End, e.NewValue, channel);

            presetCombo.SelectionChanged += (s, e) =>
            {
                if (_dirty.IsLoading) return;
                ProjectorPreset preset = ParseEnum(GetSelectedTag(presetCombo, "Custom"), ProjectorPreset.Custom);
                working.ApplyPreset(preset, channel);
                LoadAnchorControls(working, startRaw, startOutput, slightlyRaw, slightlyOutput, moderateRaw, moderateOutput,
                    criticalRaw, criticalOutput, endRaw, endOutput, presetCombo);
                RefreshCurvePlot(working, channel);
                MarkDirty();
            };
        }

        /// <summary>
        /// PRE-RELEASE Change 2b (configurable per-setpoint flatten ranges): unlike the eight anchor
        /// spinners above, a range edit does NOT switch the preset to Custom (the range is a plateau-
        /// width tuning knob, not part of what defines Linear/Curve/Custom's own anchor shape) - it only
        /// updates the working settings object and refreshes the live curve plot so the driver sees the
        /// plateau widen/narrow immediately.
        /// </summary>
        private void WireFlattenRangeEvents(
            ProjectorSettings working,
            MahApps.Metro.Controls.NumericUpDown slightlyRange,
            MahApps.Metro.Controls.NumericUpDown moderateRange,
            MahApps.Metro.Controls.NumericUpDown criticalRange,
            ProjectionChannel channel)
        {
            slightlyRange.ValueChanged += (s, e) => OnFlattenRangeChanged(working, channel, v => working.SlightlyFlattenRange = v, e.NewValue);
            moderateRange.ValueChanged += (s, e) => OnFlattenRangeChanged(working, channel, v => working.ModerateFlattenRange = v, e.NewValue);
            criticalRange.ValueChanged += (s, e) => OnFlattenRangeChanged(working, channel, v => working.CriticalFlattenRange = v, e.NewValue);
        }

        private void OnFlattenRangeChanged(ProjectorSettings working, ProjectionChannel channel, Action<double> assign, double? value)
        {
            if (_dirty.IsLoading || !value.HasValue) return;
            assign(value.Value);
            RefreshCurvePlot(working, channel);
            MarkDirty();
        }

        private void OnAnchorRawChanged(ProjectorSettings working, AnchorSlot slot, double? value, ProjectionChannel channel)
        {
            if (_dirty.IsLoading) return;
            ProjectorAnchorEditor.SetRaw(working, slot, value);
            SyncPresetCombo(working, channel);
            RefreshCurvePlot(working, channel);
            MarkDirty();
        }

        private void OnAnchorOutputChanged(ProjectorSettings working, AnchorSlot slot, double? value, ProjectionChannel channel)
        {
            if (_dirty.IsLoading) return;
            ProjectorAnchorEditor.SetOutput(working, slot, value);
            SyncPresetCombo(working, channel);
            RefreshCurvePlot(working, channel);
            MarkDirty();
        }

        private void SyncPresetCombo(ProjectorSettings working, ProjectionChannel channel)
        {
            ComboBox combo = channel == ProjectionChannel.Lock ? LockPresetCombo : SlipPresetCombo;
            using (_dirty.BeginLoading())
            {
                SelectComboItemByTag(combo, working.Preset.ToString());
            }
        }

        private void LoadAnchorControls(
            ProjectorSettings working,
            MahApps.Metro.Controls.NumericUpDown startRaw, MahApps.Metro.Controls.NumericUpDown startOutput,
            MahApps.Metro.Controls.NumericUpDown slightlyRaw, MahApps.Metro.Controls.NumericUpDown slightlyOutput,
            MahApps.Metro.Controls.NumericUpDown moderateRaw, MahApps.Metro.Controls.NumericUpDown moderateOutput,
            MahApps.Metro.Controls.NumericUpDown criticalRaw, MahApps.Metro.Controls.NumericUpDown criticalOutput,
            MahApps.Metro.Controls.NumericUpDown endRaw, MahApps.Metro.Controls.NumericUpDown endOutput, ComboBox presetCombo)
        {
            using (_dirty.BeginLoading())
            {
                startRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Start);
                startOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Start);
                slightlyRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Slightly);
                slightlyOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Slightly);
                moderateRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Moderate);
                moderateOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Moderate);
                criticalRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.Critical);
                criticalOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.Critical);
                endRaw.Value = ProjectorAnchorEditor.GetRaw(working, AnchorSlot.End);
                endOutput.Value = ProjectorAnchorEditor.GetOutput(working, AnchorSlot.End);
                SelectComboItemByTag(presetCombo, working.Preset.ToString());
            }
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

            ProjectorSettings defaults = ProjectorSettings.CreateShippedDefault(channel);
            UpdateAnchorHelpText(projector, working, defaults, isLock ? LockLiveSlightly : SlipLiveSlightly, AnchorSlot.Slightly);
            UpdateAnchorHelpText(projector, working, defaults, isLock ? LockLiveModerate : SlipLiveModerate, AnchorSlot.Moderate);
            UpdateAnchorHelpText(projector, working, defaults, isLock ? LockLiveCritical : SlipLiveCritical, AnchorSlot.Critical);
        }

        /// <summary>
        /// Fills one anchor row's help text (the table's 4th column): the SHIPPED DEFAULT input-&gt;
        /// output mapping for this anchor/channel (read from <paramref name="defaults"/>, itself
        /// sourced from <see cref="ProjectorSettings.CreateShippedDefault"/> so the number can never
        /// drift out of sync with the real default), which stays stable no matter what the driver has
        /// typed - unlike a live "the curve currently sends X" readout, which is redundant with the
        /// spinner textboxes sitting right next to it.
        /// <para/>
        /// That live comparison is NOT thrown away, though: <see cref="OutputProjector"/> drops an
        /// anchor entirely if it falls at/below the Start input or at/above the End input, and always
        /// forces outputs non-decreasing after sorting by input - so a driver can type a raw/output
        /// pair that the curve does not actually honour, with nothing in the spinners themselves ever
        /// showing that. Appending "Curve.Anchor.LiveDiffersFormat" ONLY when the curve's actual
        /// output at the driver's own typed raw input differs from what they typed keeps that safety
        /// net (as a warning that only appears when it matters) without restoring the noisy always-on
        /// live line the owner asked to remove.
        /// </summary>
        private void UpdateAnchorHelpText(OutputProjector projector, ProjectorSettings source, ProjectorSettings defaults, TextBlock block, AnchorSlot slot)
        {
            double defaultRaw = ProjectorAnchorEditor.GetRaw(defaults, slot);
            double defaultOutput = ProjectorAnchorEditor.GetOutput(defaults, slot);
            string text = string.Format(Strings.Get("Curve.Anchor.DefaultFormat"), FormatNum(defaultRaw), FormatNum(defaultOutput));

            double typedRaw = ProjectorAnchorEditor.GetRaw(source, slot);
            double typedOutput = ProjectorAnchorEditor.GetOutput(source, slot);
            double actualOutput = projector.Project(typedRaw);

            if (Math.Abs(actualOutput - typedOutput) > 0.05)
            {
                text += " " + string.Format(Strings.Get("Curve.Anchor.LiveDiffersFormat"), FormatNum(actualOutput));
            }

            block.Text = text;
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
            // Wraps the ENTIRE method body - every control assignment below is a programmatic load,
            // never a user edit, so none of it may mark Apply dirty (see ApplyDirtyState's own remarks
            // on why this is a reentrant depth counter rather than a bool: this outer scope stays open
            // across the several nested BeginLoading calls inside LoadAnchorControls/SetSourceModeCombo
            // reached from here, so THEIR completion can never prematurely re-enable dirty tracking
            // while this outer load is still assigning further controls below).
            using (_dirty.BeginLoading())
            {
                QAdvanceFeedbackSettings s = _plugin.Settings;

                LoadChannel(s.Lock, _lockRows, LockPulseEnabled, LockPulseGapMs, LockPulseMinValue);
                SetSourceModeCombo(isLock: true, s.Lock.SourceMode);
                RefreshSourceModeUi(isLock: true);
                LockBrakeThreshold.Value = s.Lock.BrakeThresholdPercent;
                LockSensibility.Value = s.Lock.LockSensibility;
                // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock only.
                SelectComboItemByTag(LockNormalizePatternCombo, s.Lock.NormalizePattern.ToString());
                RefreshLockAnchorLabelsForPattern();

                LockWMax.Value = s.Lock.AggregationWMax;
                LockWMin.Value = s.Lock.AggregationWMin;
                LockWFront.Value = s.Lock.AggregationWFront;
                LockWRear.Value = s.Lock.AggregationWRear;

                _workingLockProjector.Preset = s.Lock.Projector.Preset;
                _workingLockProjector.StartInput = s.Lock.Projector.StartInput;
                _workingLockProjector.EndInput = s.Lock.Projector.EndInput;
                _workingLockProjector.StartOutput = s.Lock.Projector.StartOutput;
                _workingLockProjector.EndOutput = s.Lock.Projector.EndOutput;
                _workingLockProjector.SlightlyInput = s.Lock.Projector.SlightlyInput;
                _workingLockProjector.SlightlyOutput = s.Lock.Projector.SlightlyOutput;
                _workingLockProjector.ModerateInput = s.Lock.Projector.ModerateInput;
                _workingLockProjector.ModerateOutput = s.Lock.Projector.ModerateOutput;
                _workingLockProjector.CriticalInput = s.Lock.Projector.CriticalInput;
                _workingLockProjector.CriticalOutput = s.Lock.Projector.CriticalOutput;
                _workingLockProjector.SlightlyFlattenRange = s.Lock.Projector.SlightlyFlattenRange;
                _workingLockProjector.ModerateFlattenRange = s.Lock.Projector.ModerateFlattenRange;
                _workingLockProjector.CriticalFlattenRange = s.Lock.Projector.CriticalFlattenRange;
                LoadAnchorControls(_workingLockProjector, LockStartRaw, LockStartOutput, LockSlightlyRaw, LockSlightlyOutput,
                    LockModerateRaw, LockModerateOutput, LockCriticalRaw, LockCriticalOutput, LockEndRaw, LockEndOutput, LockPresetCombo);
                LockSlightlyFlattenRange.Value = _workingLockProjector.SlightlyFlattenRange;
                LockModerateFlattenRange.Value = _workingLockProjector.ModerateFlattenRange;
                LockCriticalFlattenRange.Value = _workingLockProjector.CriticalFlattenRange;
                RefreshCurvePlot(_workingLockProjector, ProjectionChannel.Lock);

                LoadChannel(s.Slip, _slipRows, SlipPulseEnabled, SlipPulseGapMs, SlipPulseMinValue);
                SetSourceModeCombo(isLock: false, s.Slip.SourceMode);
                RefreshSourceModeUi(isLock: false);
                SlipBrakeThreshold.Value = s.Slip.BrakeThresholdPercent;
                SlipThrottleThreshold.Value = s.Slip.ThrottleThresholdPercent;

                SlipWMax.Value = s.Slip.AggregationWMax;
                SlipWMin.Value = s.Slip.AggregationWMin;
                SlipWFront.Value = s.Slip.AggregationWFront;
                SlipWRear.Value = s.Slip.AggregationWRear;
                SlipFloorFactor.Value = s.Slip.SlipFloorFactor;

                _workingSlipProjector.Preset = s.Slip.Projector.Preset;
                _workingSlipProjector.StartInput = s.Slip.Projector.StartInput;
                _workingSlipProjector.EndInput = s.Slip.Projector.EndInput;
                _workingSlipProjector.StartOutput = s.Slip.Projector.StartOutput;
                _workingSlipProjector.EndOutput = s.Slip.Projector.EndOutput;
                _workingSlipProjector.SlightlyInput = s.Slip.Projector.SlightlyInput;
                _workingSlipProjector.SlightlyOutput = s.Slip.Projector.SlightlyOutput;
                _workingSlipProjector.ModerateInput = s.Slip.Projector.ModerateInput;
                _workingSlipProjector.ModerateOutput = s.Slip.Projector.ModerateOutput;
                _workingSlipProjector.CriticalInput = s.Slip.Projector.CriticalInput;
                _workingSlipProjector.CriticalOutput = s.Slip.Projector.CriticalOutput;
                _workingSlipProjector.SlightlyFlattenRange = s.Slip.Projector.SlightlyFlattenRange;
                _workingSlipProjector.ModerateFlattenRange = s.Slip.Projector.ModerateFlattenRange;
                _workingSlipProjector.CriticalFlattenRange = s.Slip.Projector.CriticalFlattenRange;
                LoadAnchorControls(_workingSlipProjector, SlipStartRaw, SlipStartOutput, SlipSlightlyRaw, SlipSlightlyOutput,
                    SlipModerateRaw, SlipModerateOutput, SlipCriticalRaw, SlipCriticalOutput, SlipEndRaw, SlipEndOutput, SlipPresetCombo);
                SlipSlightlyFlattenRange.Value = _workingSlipProjector.SlightlyFlattenRange;
                SlipModerateFlattenRange.Value = _workingSlipProjector.ModerateFlattenRange;
                SlipCriticalFlattenRange.Value = _workingSlipProjector.CriticalFlattenRange;
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
                GForceAutoTransitionScale.Value = s.GForce.AutoTransitionAnimationScale;
                GForceFixedTransitionScale.Value = s.GForce.FixedTransitionAnimationScale;

                SelectComboItemByTag(GForceLateralDirectionCombo, s.GForce.LateralDirection.ToString());

                GForceShakeEnabled.IsChecked = s.GForce.IntegrateWheelLockAndSlip;
                GForceShakeFrequency.Value = s.GForce.ShakeFrequencyHz;
                GForceShakeLockScale.Value = s.GForce.WheelLockShakeScale;
                GForceShakeSlipScale.Value = s.GForce.WheelSlipShakeScale;

                EnableDiagnosticsCheckBox.IsChecked = s.General.EnableDiagnostics;
                ExportCsvCheckBox.IsChecked = s.General.ExportCsv;
            }
        }

        /// <summary>
        /// Builds the readout label to the right of each Fixed-value spinner (docs\robust-auto-gforce-report.md,
        /// UI requirement, one-decimal formatting):
        /// <list type="bullet">
        /// <item>FIXED mode: "Fixed: 1.5G".</item>
        /// <item>AUTO mode, no evidence yet for the current game/car: "Default: 1.5G. Auto: still using
        /// default (no data yet)" - so the driver can tell "auto hasn't kicked in" apart from "auto
        /// agrees with the default".</item>
        /// <item>AUTO mode, evidence exists: "Default: 1.5G. Auto detected: 2.3G".</item>
        /// </list>
        /// The FIXED-value spinner itself is untouched by mode (see <see cref="RefreshGForceModeControls"/>'s
        /// own remarks) - this label is the ONLY thing that changes with mode/evidence.
        /// </summary>
        private void RefreshGForceLearnedText()
        {
            QAdvanceFeedbackSettings s = _plugin.Settings;
            bool accelAuto = string.Equals(GetSelectedTag(GForceAccelModeCombo, "Fixed"), "Auto", StringComparison.Ordinal);
            bool decelAuto = string.Equals(GetSelectedTag(GForceDecelModeCombo, "Fixed"), "Auto", StringComparison.Ordinal);

            GForceAccelLearnedText.Text = BuildGForceReadoutText(accelAuto, s.GForce.FixedAccelMaxG, s.GForce.TryGetCurrentAccelAutoDetected(out double accelDetected), accelDetected);
            GForceDecelLearnedText.Text = BuildGForceReadoutText(decelAuto, s.GForce.FixedDecelMaxG, s.GForce.TryGetCurrentDecelAutoDetected(out double decelDetected), decelDetected);
        }

        private static string BuildGForceReadoutText(bool autoMode, double fixedValue, bool hasDetected, double detectedValue)
        {
            if (!autoMode) return string.Format(Strings.Get("GForce.Readout.Fixed"), fixedValue);
            return hasDetected
                ? string.Format(Strings.Get("GForce.Readout.Auto.Detected"), fixedValue, detectedValue)
                : string.Format(Strings.Get("GForce.Readout.Auto.NoDataYet"), fixedValue);
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
            s.Lock.LockSensibility = LockSensibility.Value ?? s.Lock.LockSensibility;
            s.Lock.AggregationWMax = LockWMax.Value ?? s.Lock.AggregationWMax;
            s.Lock.AggregationWMin = LockWMin.Value ?? s.Lock.AggregationWMin;
            s.Lock.AggregationWFront = LockWFront.Value ?? s.Lock.AggregationWFront;
            s.Lock.AggregationWRear = LockWRear.Value ?? s.Lock.AggregationWRear;
            // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock only.
            s.Lock.NormalizePattern = ParseEnum(GetSelectedTag(LockNormalizePatternCombo, s.Lock.NormalizePattern.ToString()), s.Lock.NormalizePattern);
            CopyProjector(_workingLockProjector, s.Lock.Projector);

            SaveChannel(s.Slip, _slipRows, SlipPulseEnabled, SlipPulseGapMs, SlipPulseMinValue);
            s.Slip.SourceMode = ParseEnum(GetSelectedTag(SlipSourceModeCombo, s.Slip.SourceMode.ToString()), s.Slip.SourceMode);
            s.Slip.BrakeThresholdPercent = SlipBrakeThreshold.Value ?? s.Slip.BrakeThresholdPercent;
            s.Slip.ThrottleThresholdPercent = SlipThrottleThreshold.Value ?? s.Slip.ThrottleThresholdPercent;
            s.Slip.AggregationWMax = SlipWMax.Value ?? s.Slip.AggregationWMax;
            s.Slip.AggregationWMin = SlipWMin.Value ?? s.Slip.AggregationWMin;
            s.Slip.AggregationWFront = SlipWFront.Value ?? s.Slip.AggregationWFront;
            s.Slip.AggregationWRear = SlipWRear.Value ?? s.Slip.AggregationWRear;
            s.Slip.SlipFloorFactor = SlipFloorFactor.Value ?? s.Slip.SlipFloorFactor;
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
            s.GForce.AutoTransitionAnimationScale = GForceAutoTransitionScale.Value ?? s.GForce.AutoTransitionAnimationScale;
            s.GForce.FixedTransitionAnimationScale = GForceFixedTransitionScale.Value ?? s.GForce.FixedTransitionAnimationScale;

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
            MarkClean();
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
            to.StartOutput = from.StartOutput;
            to.EndOutput = from.EndOutput;
            to.SlightlyInput = from.SlightlyInput;
            to.SlightlyOutput = from.SlightlyOutput;
            to.ModerateInput = from.ModerateInput;
            to.ModerateOutput = from.ModerateOutput;
            to.CriticalInput = from.CriticalInput;
            to.CriticalOutput = from.CriticalOutput;
            to.SlightlyFlattenRange = from.SlightlyFlattenRange;
            to.ModerateFlattenRange = from.ModerateFlattenRange;
            to.CriticalFlattenRange = from.CriticalFlattenRange;
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

        /// <summary>
        /// 1.0.6.0 (docs\release-1060-report.md, Part 4f) - the General tab's own version label reads
        /// THIS, never a hand-typed literal, so it can never drift from the DLL actually loaded (the
        /// exact failure mode docs\curve-help-text-report.md already documented for a hand-copied
        /// number elsewhere in this control - see ProjectorSettings.CreateShippedDefault's own remarks).
        /// Reads the running assembly's own <see cref="FileVersionInfo.FileVersion"/> (the csproj's
        /// <c>&lt;FileVersion&gt;</c>, i.e. exactly what a driver sees in Windows Explorer's own
        /// "Details" tab for this DLL) rather than <see cref="Assembly.GetName"/>'s Version (the
        /// AssemblyVersion, which happens to be numerically identical today but is a semantically
        /// different field .NET does not guarantee stays in lockstep). Falls back to the
        /// AssemblyVersion, then to a literal "unknown", only if the file-version query itself throws
        /// (e.g. a locked-down environment that denies FileVersionInfo) - this label must never take
        /// down the whole settings panel merely to report its own version.
        /// </summary>
        private static string GetRunningAssemblyFileVersion()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location)?.FileVersion;
                if (!string.IsNullOrWhiteSpace(fileVersion)) return fileVersion;
                return assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Guards a top-level UI action (Apply/Restore/per-source reset) invoked directly off a WPF
        /// <c>Button.Click</c> event - see this control's own constructor remarks for why these
        /// specifically need their own guard distinct from the constructor's. Any fault degrades to
        /// "this click did nothing" (plus a health-registry entry the General tab's own health section
        /// surfaces) rather than reaching SimHub's Dispatcher unguarded.
        /// </summary>
        private void SafeUiAction(Action action, string subsystem)
        {
            try { action(); }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: settings UI action failed - " + e);
                HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Health.Impact.SettingsUi", e.ToString());
                RefreshHealthUi();
            }
        }

        /// <summary>Async counterpart of <see cref="SafeUiAction"/>, for the per-source action button
        /// (script editor/property picker) - assigned as <c>async void</c>-equivalent event handlers,
        /// where an unhandled exception cannot be caught by any caller at all.</summary>
        private async Task SafeUiActionAsync(Func<Task> action, string subsystem)
        {
            try { await action(); }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: settings UI action failed - " + e);
                HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Health.Impact.SettingsUi", e.ToString());
                RefreshHealthUi();
            }
        }

        private static string FormatNum(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static T ParseEnum<T>(string text, T fallback) where T : struct
            => Enum.TryParse(text, out T parsed) ? parsed : fallback;
    }
}

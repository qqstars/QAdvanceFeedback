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
            // AUTO/MANUAL IS NOW A TOGGLE (v1.0.7.2), not a two-item combo - checked means Auto.
            GForceAccelAutoToggle.Checked += (s, e) => { MarkDirty(); RefreshGForceModeControls(); };
            GForceAccelAutoToggle.Unchecked += (s, e) => { MarkDirty(); RefreshGForceModeControls(); };
            GForceDecelAutoToggle.Checked += (s, e) => { MarkDirty(); RefreshGForceModeControls(); };
            GForceDecelAutoToggle.Unchecked += (s, e) => { MarkDirty(); RefreshGForceModeControls(); };
            GForceLateralDirectionCombo.SelectionChanged += (s, e) => MarkDirty();
            // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock only; see
            // RefreshLockAnchorLabelsForPattern's own remarks.
            LockNormalizePatternCombo.SelectionChanged += (s, e) =>
            {
                MarkDirty(); RefreshLockAnchorLabelsForPattern();
                FillLowerAnchorsIfMappingSelected(isLock: true);
                RefreshKeyDataPointUi(); RefreshBothCurvePlots();
            };
            SlipNormalizePatternCombo.SelectionChanged += (s, e) =>
            {
                MarkDirty();
                FillLowerAnchorsIfMappingSelected(isLock: false);
                RefreshKeyDataPointUi(); RefreshBothCurvePlots();
            };
            WireKeyDataPointEvents();
            StartGraphRefreshTimer();
            LockKeyDataReset.Click += (s, e) => SafeUiAction(() => ResetKeyDataPoints(isLock: true), HealthSubsystems.SettingsUi);
            SlipKeyDataReset.Click += (s, e) => SafeUiAction(() => ResetKeyDataPoints(isLock: false), HealthSubsystems.SettingsUi);
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
            ShakeItImportButton.Click += (s, e) => RunShakeItImport();
            RefreshHealthUi();
        }

        // ------------------------------------------------------------------------------------
        // Plugin health section (Core.Health.HealthRegistry) - invisible/one-line when nothing is
        // wrong; expands to one warning block per degraded subsystem otherwise. See this control's own
        // constructor remarks for why this is refreshed exactly once, last, rather than reactively.
        // ------------------------------------------------------------------------------------

        // ================= KEY DATA POINTS (v1.0.7.2) =================
        //
        // The driver-facing SMax/S90/S75 (Perfect/Great/Good for Slip) rows. Three things make this
        // more than a plain settings block:
        //
        //  - LEARNING NEVER STOPS. Auto only decides which values are PUBLISHED. The learned numbers
        //    are always available, which is what the "[Learned Value: xx.x]" hint shows even in manual
        //    mode, and why toggling back to Auto is instant rather than a fresh cold start.
        //  - THE BOXES ARE NOT LIVE IMMEDIATELY. A manual value only applies once this session has
        //    finished calibrating AND 30 seconds of driving have accumulated (ManualOverrideGate). The
        //    boxes stay disabled until then, so an edit cannot silently fail to take effect.
        //  - MAX-GRIP-ONLY HIDES THE LOWER TWO ROWS, and keeps them consistent behind the scenes at
        //    0.90/0.70 of SMax, so switching back to the three-point mapping never finds them stale.

        /// <summary>Set while the UI is being populated from settings, so the handlers below do not
        /// treat programmatic writes as driver edits (which would MarkDirty on every load).</summary>
        private bool _suppressKeyDataEvents;

        /// <summary>Latest learned values, pushed in by the plugin - see <see cref="UpdateLearnedKeyDataPoints"/>.</summary>
        private double? _lockLearnedSMax, _lockLearnedS90, _lockLearnedS75;
        private double? _slipLearnedSMax, _slipLearnedS90, _slipLearnedS75;
        private bool _lockManualLive, _slipManualLive;

        /// <summary>
        /// Give every switch the same spelled-out On/Off labels the "Integrate Wheel Lock and Slip"
        /// feature switch already used. A toggle's state is never inferred from a highlight alone -
        /// that was the standing rule for that one switch, and it now applies to all of them.
        /// </summary>
        private void ApplyOnOffLabelsToEverySwitch()
        {
            string on = Strings.Get("GForce.Shake.On");
            string off = Strings.Get("GForce.Shake.Off");

            var switches = new[]
            {
                LockPulseEnabled, SlipPulseEnabled, EnableDiagnosticsCheckBox, ExportCsvCheckBox,
                ShakeItImportOverrideCheckBox, GForceAccelAutoToggle, GForceDecelAutoToggle,
                LockKeyDataAutoToggle, LockKeyDataPerGameToggle,
                SlipKeyDataAutoToggle, SlipKeyDataPerGameToggle,
            };
            foreach (MahApps.Metro.Controls.ToggleSwitch box in switches)
            {
                box.OnLabel = on;
                box.OffLabel = off;
            }
        }

        private void LocalizeKeyDataPoints()
        {
            LockKeyDataReset.Content = SlipKeyDataReset.Content = Strings.Get("KeyData.ResetToDefault");
            LockKeyDataHeader.Text = Strings.Get("KeyData.Section.Lock");
            SlipKeyDataHeader.Text = Strings.Get("KeyData.Section.Slip");

            LockKeyDataAutoLabel.Text = SlipKeyDataAutoLabel.Text = Strings.Get("KeyData.AutoGenerate.Label");
            LockKeyDataAutoDesc.Text = SlipKeyDataAutoDesc.Text = Strings.Get("KeyData.AutoGenerate.Desc");
            LockKeyDataPerGameLabel.Text = SlipKeyDataPerGameLabel.Text = Strings.Get("KeyData.PerGame.Label");
            LockKeyDataPerGameDesc.Text = SlipKeyDataPerGameDesc.Text = Strings.Get("KeyData.PerGame.Desc");

            LockKeyDataSMaxLabel.Text = Strings.Get("KeyData.Lock.SMax.Label");
            LockKeyDataS90Label.Text = Strings.Get("KeyData.Lock.S90.Label");
            LockKeyDataS75Label.Text = Strings.Get("KeyData.Lock.S75.Label");
            LockKeyDataSMaxDesc.Text = Strings.Get("KeyData.Lock.SMax.Desc");
            LockKeyDataS90Desc.Text = Strings.Get("KeyData.Lock.S90.Desc");
            LockKeyDataS75Desc.Text = Strings.Get("KeyData.Lock.S75.Desc");

            SlipKeyDataSMaxLabel.Text = Strings.Get("KeyData.Slip.SMax.Label");
            SlipKeyDataS90Label.Text = Strings.Get("KeyData.Slip.S90.Label");
            SlipKeyDataS75Label.Text = Strings.Get("KeyData.Slip.S75.Label");
            SlipKeyDataSMaxDesc.Text = Strings.Get("KeyData.Slip.SMax.Desc");
            SlipKeyDataS90Desc.Text = Strings.Get("KeyData.Slip.S90.Desc");
            SlipKeyDataS75Desc.Text = Strings.Get("KeyData.Slip.S75.Desc");
        }

        private void WireKeyDataPointEvents()
        {
            var toggles = new[] { LockKeyDataAutoToggle, LockKeyDataPerGameToggle,
                                  SlipKeyDataAutoToggle, SlipKeyDataPerGameToggle };
            foreach (MahApps.Metro.Controls.ToggleSwitch t in toggles)
            {
                t.Checked += (s, e) => OnKeyDataToggleChanged();
                t.Unchecked += (s, e) => OnKeyDataToggleChanged();
            }

            var boxes = new[] { LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75,
                                SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75 };
            foreach (MahApps.Metro.Controls.NumericUpDown box in boxes)
                box.ValueChanged += (s, e) => OnKeyDataValueChanged();
        }

        private void OnKeyDataToggleChanged()
        {
            if (_suppressKeyDataEvents) return;
            MarkDirty();
            EnforceSlipPatternForAutoMode();
            // AUTO/MANUAL and GLOBAL/PER-GAME both change which slot is in play, so reload that slot's
            // stored numbers first. Only if it has none do we fall back to showing the learned values.
            ReloadKeyDataForCurrentContext();
            SeedManualBoxesFromLearnedIfNeeded();
            EnforceSlipPatternForAutoMode();
            RefreshKeyDataPointUi();
        }

        /// <summary>
        /// Slip under Auto is always Perfect-Point-Only, and the selector is locked to say so.
        /// <para/>
        /// Slip has no native measurement of its Great and Good points - they can only ever be DERIVED
        /// from the Perfect point (see KeyDataPointSettings.DeriveLowerAnchors). Offering the three-point
        /// mapping while the plugin is generating the values itself would present two derived numbers as
        /// though they had been measured. In MANUAL mode the driver is supplying all three, so the choice
        /// is real and the selector is enabled again.
        /// <para/>
        /// Lock is untouched: it measures all three anchors, so both patterns are honest for it.
        /// </summary>
        private void EnforceSlipPatternForAutoMode()
        {
            // NO LONGER RESTRICTED (owner's change of mind). Auto can drive the three-point mapping on
            // either channel now: S90/S75 are derived from the learned SMax by a fixed percentage
            // (KeyDataPointSettings.DerivedS90Fraction/DerivedS75Fraction), so the two lower anchors
            // exist even for a channel that cannot measure them. The selector stays enabled in both
            // modes; only SLIP's shipped DEFAULT remains single-point (Lock ships the full three-point
            // mapping - see WheelChannelSettings.CreateDefaults for why the two channels differ).
            SlipNormalizePatternCombo.IsEnabled = true;
        }

        /// <summary>
        /// Switching from the single-point pattern to the three-point one reveals two rows that were
        /// hidden - and, until now, empty, because nothing had ever filled them. A driver arriving at
        /// Perfect/Great/Good with only Perfect populated has an unusable configuration and no hint of
        /// what to type.
        /// <para/>
        /// So they are filled on the way in: the shipped defaults when the source is one this plugin
        /// recognises, otherwise derived from whatever Perfect/Max-Grip currently holds. Values the
        /// driver has already set are never overwritten.
        /// </summary>
        private void FillLowerAnchorsIfMappingSelected(bool isLock)
        {
            bool maxGripOnly = GetSelectedTag(
                isLock ? LockNormalizePatternCombo : SlipNormalizePatternCombo,
                isLock ? "Mapping" : "MaxGripOnly") == "MaxGripOnly";
            if (maxGripOnly) return;

            MahApps.Metro.Controls.NumericUpDown sMaxBox = isLock ? LockKeyDataSMax : SlipKeyDataSMax;
            MahApps.Metro.Controls.NumericUpDown s90Box = isLock ? LockKeyDataS90 : SlipKeyDataS90;
            MahApps.Metro.Controls.NumericUpDown s75Box = isLock ? LockKeyDataS75 : SlipKeyDataS75;

            bool s90Missing = (s90Box.Value ?? 0.0) <= 0.0;
            bool s75Missing = (s75Box.Value ?? 0.0) <= 0.0;
            if (!s90Missing && !s75Missing) return;

            string sourceIdentity = isLock ? _currentLockSource : _currentSlipSource;
            double sMax = sMaxBox.Value ?? 0.0;

            double defaultSMax, defaultS90, defaultS75;
            bool shipped = KeyDataPointSettings.TryResolveShippedDefaults(
                sourceIdentity, isLock, ConfiguredDefaults, out defaultSMax, out defaultS90, out defaultS75);

            _suppressKeyDataEvents = true;
            try
            {
                if (shipped && sMax <= 0.0)
                {
                    sMaxBox.Value = defaultSMax; s90Box.Value = defaultS90; s75Box.Value = defaultS75;
                }
                else if (shipped && Math.Abs(sMax - defaultSMax) < 0.05)
                {
                    // Perfect is still the shipped number, so the shipped companions belong with it.
                    if (s90Missing) s90Box.Value = defaultS90;
                    if (s75Missing) s75Box.Value = defaultS75;
                }
                else if (sMax > 0.0)
                {
                    double derivedS90, derivedS75;
                    KeyDataPointSettings.DeriveLowerAnchors(sMax, out derivedS90, out derivedS75);
                    if (s90Missing) s90Box.Value = Math.Round(derivedS90, 1);
                    if (s75Missing) s75Box.Value = Math.Round(derivedS75, 1);
                }
            }
            finally { _suppressKeyDataEvents = false; }

            ScheduleKeyDataPersist();
        }

        private void OnKeyDataValueChanged()
        {
            if (_suppressKeyDataEvents) return;
            MarkDirty();
            // A TYPED VALUE APPLIES AT ONCE, and settles to disk shortly after - see
            // ScheduleKeyDataPersist. It also latches the slot as seeded, so the automatic one-time
            // write can never come along later and overwrite what the driver just entered.
            ScheduleKeyDataPersist();
            RefreshKeyDataPointUi();
            RefreshBothCurvePlots();
        }

        /// <summary>Copy the learned values into the manual boxes - on switching to Auto (so they show
        /// what Auto uses) and the first time manual mode has nothing configured.</summary>
        /// <summary>
        /// Fill the value boxes for whatever state the channel is in.
        /// <para/>
        /// AUTO shows the learned values, so the driver can see what the plugin is actually using.
        /// <para/>
        /// MANUAL on one of the two SHIPPED sources fills the shipped defaults straight away - those
        /// configurations have a measured range, so there is nothing to wait for and no reason to make
        /// the driver sit through a warm-up before the boxes mean anything.
        /// <para/>
        /// MANUAL on any OTHER source leaves the boxes EMPTY - they show "---" via their watermark.
        /// Nobody has measured that signal's range, so there is no honest number to prefill; the driver
        /// either types one (which applies immediately) or waits for the plugin to learn one.
        /// </summary>
        /// <summary>
        /// Pull learned ceilings straight from the plugin, so the page has an answer before any telemetry
        /// arrives. Without this the boxes fall back to shipped defaults whenever SimHub is sitting on
        /// the menu - even with a parameters file full of evidence for exactly this source.
        /// <para/>
        /// The per-frame push still refines these once driving starts; this only fills the gap before it.
        /// </summary>
        /// <summary>The configured starting points, or the built-in ones when there is no settings
        /// object yet (construction order, or a harness hosting this control standalone).</summary>
        private KeyDataPointDefaults ConfiguredDefaults
            => _plugin != null && _plugin.Settings != null
                ? _plugin.Settings.KeyDataPointDefaults
                : KeyDataPointDefaults.CreateShipped();

        private void PullPersistedLearnedValues()
        {
            if (_plugin == null) return;

            double? lockSMax = _plugin.GetLearnedCeilingForDisplay(isLockChannel: true, _currentLockSource);
            if (lockSMax.HasValue && lockSMax.Value > 0.0)
            {
                _lockLearnedSMax = lockSMax;
                double s90, s75;
                KeyDataPointSettings.DeriveLowerAnchors(lockSMax.Value, out s90, out s75);
                _lockLearnedS90 = s90; _lockLearnedS75 = s75;
            }

            double? slipSMax = _plugin.GetLearnedCeilingForDisplay(isLockChannel: false, _currentSlipSource);
            if (slipSMax.HasValue && slipSMax.Value > 0.0)
            {
                _slipLearnedSMax = slipSMax;
                double s90, s75;
                KeyDataPointSettings.DeriveLowerAnchors(slipSMax.Value, out s90, out s75);
                _slipLearnedS90 = s90; _slipLearnedS75 = s75;
            }
        }

        private void SeedManualBoxesFromLearnedIfNeeded()
        {
            _suppressKeyDataEvents = true;
            try
            {
                SeedChannel(LockKeyDataAutoToggle, _currentLockSource, isLock: true,
                            LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75,
                            _lockLearnedSMax, _lockLearnedS90, _lockLearnedS75, ConfiguredDefaults);
                SeedChannel(SlipKeyDataAutoToggle, _currentSlipSource, isLock: false,
                            SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75,
                            _slipLearnedSMax, _slipLearnedS90, _slipLearnedS75, ConfiguredDefaults);
            }
            finally { _suppressKeyDataEvents = false; }
        }

        private static void SeedChannel(
            MahApps.Metro.Controls.ToggleSwitch auto, string sourceIdentity, bool isLock,
            MahApps.Metro.Controls.NumericUpDown sMaxBox,
            MahApps.Metro.Controls.NumericUpDown s90Box,
            MahApps.Metro.Controls.NumericUpDown s75Box,
            double? learnedSMax, double? learnedS90, double? learnedS75,
            KeyDataPointDefaults defaults)
        {
            bool empty = (sMaxBox.Value ?? 0.0) <= 0.0;

            if (auto.IsChecked == true)
            {
                if (learnedSMax.HasValue && learnedSMax.Value > 0.0)
                {
                    // S90/S75 are a fixed PERCENTAGE of the learned SMax, on both channels. Lock does
                    // learn its own per-corner ratios, but showing one channel a measured pair and the
                    // other a derived pair - while both are labelled the same way - made the two read as
                    // different kinds of number. One rule, visible in the box, is easier to trust.
                    double sMaxValue = System.Math.Round(learnedSMax.Value, 1);
                    double s90Derived, s75Derived;
                    KeyDataPointSettings.DeriveLowerAnchors(sMaxValue, out s90Derived, out s75Derived);
                    sMaxBox.Value = sMaxValue;
                    s90Box.Value = System.Math.Round(s90Derived, 1);
                    s75Box.Value = System.Math.Round(s75Derived, 1);
                    return;
                }

                // NOTHING LEARNED YET - with no game running, or on a brand new key. Under Auto these
                // boxes are a readout of what the channel is using, and for a recognised source that is
                // the shipped reference, so show it. "---" is reserved for the case where there really is
                // no honest number: an unrecognised source nobody has measured.
                double autoSMax, autoS90, autoS75;
                if (KeyDataPointSettings.TryResolveShippedDefaults(sourceIdentity, isLock, defaults, out autoSMax, out autoS90, out autoS75))
                {
                    sMaxBox.Value = autoSMax; s90Box.Value = autoS90; s75Box.Value = autoS75;
                }
                else
                {
                    sMaxBox.Value = null; s90Box.Value = null; s75Box.Value = null;
                }
                return;
            }

            if (!empty) return;   // manual and already configured - the driver's values stand

            double sMax, s90, s75;
            if (KeyDataPointSettings.TryResolveShippedDefaults(sourceIdentity, isLock, defaults, out sMax, out s90, out s75))
            {
                sMaxBox.Value = sMax; s90Box.Value = s90; s75Box.Value = s75;
                return;
            }

            // Unknown source - leave empty so the watermark reads "---".
            sMaxBox.Value = null; s90Box.Value = null; s75Box.Value = null;
        }

        /// <summary>
        /// This channel's source identity, computed from the CONTROLS rather than waiting for the
        /// plugin to push one.
        /// <para/>
        /// THE BUG THIS FIXES: the identity used to arrive only from the frame-loop push, which needs a
        /// game running and reflects the SAVED configuration. So while a driver was editing sources on
        /// this page it was stale, or null with no game running - and every decision that keys off it
        /// (shipped defaults, which stored slot to load, known vs unknown) was made against the wrong
        /// source. Switching to manual filled nothing, switching Raw to ShakeIt kept the old numbers, and
        /// editing one wheel to an expression still counted as a shipped source.
        /// <para/>
        /// Uses the SAME <see cref="SourceIdentity.Compute"/> the engine keys its learners by, so the two
        /// agree on what "this source" means - including that a scripted wheel is hashed and can never
        /// match a shipped configuration.
        /// </summary>
        private string ComputeCurrentSourceIdentity(bool isLock)
        {
            List<SourceRow> rows = isLock ? _lockRows : _slipRows;
            if (rows == null || rows.Count < 4) return null;

            return global::QAdvanceFeedback.Core.Normalized.SourceIdentity.Compute(
                rows[0].SourceBox.Text, GetScriptType(rows[0].ScriptTypeButton).ToString(),
                rows[1].SourceBox.Text, GetScriptType(rows[1].ScriptTypeButton).ToString(),
                rows[2].SourceBox.Text, GetScriptType(rows[2].ScriptTypeButton).ToString(),
                rows[3].SourceBox.Text, GetScriptType(rows[3].ScriptTypeButton).ToString());
        }

        /// <summary>
        /// Re-resolve both channels' sources and, if either changed, reload the key data points for the
        /// slot that now applies.
        /// <para/>
        /// A source change is a change of SCALE, so carrying the previous source's numbers across would
        /// be worse than showing none: it would look configured while describing a different signal.
        /// <see cref="ReloadKeyDataForCurrentContext"/> clears the boxes when the new slot is empty, and
        /// the seeding pass then fills shipped defaults for a recognised source, or leaves "---" for one
        /// nobody has measured.
        /// </summary>
        private void OnSourceConfigurationChanged()
        {
            if (_dirty != null && _dirty.IsLoading) return;

            string lockSource = ComputeCurrentSourceIdentity(isLock: true);
            string slipSource = ComputeCurrentSourceIdentity(isLock: false);
            if (string.Equals(lockSource, _currentLockSource, StringComparison.Ordinal)
                && string.Equals(slipSource, _currentSlipSource, StringComparison.Ordinal)) return;

            _currentLockSource = lockSource;
            _currentSlipSource = slipSource;

            PullPersistedLearnedValues();
            ReloadKeyDataForCurrentContext();
            SeedManualBoxesFromLearnedIfNeeded();
            RefreshKeyDataPointUi();
            RefreshBothCurvePlots();
        }

        /// <summary>
        /// Populate both channels' boxes from the slot the CURRENT (mode, game, source) selects.
        /// <para/>
        /// Called on every change that moves the slot - the Auto toggle, the Per-Game toggle, a source
        /// edit, and a game switch - which is "whenever the source, auto/manual, global/per-game, or the
        /// running game changes, load the numbers for that target". A slot with nothing stored leaves the
        /// boxes alone, so the seeding path can fill them instead.
        /// </summary>
        private void ReloadKeyDataForCurrentContext()
        {
            if (_plugin == null || _plugin.Settings == null) return;

            _suppressKeyDataEvents = true;
            try
            {
                ReloadChannel(_plugin.Settings.Lock.KeyDataPoints, LockKeyDataPerGameToggle,
                              _currentLockSource, LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75);
                ReloadChannel(_plugin.Settings.Slip.KeyDataPoints, SlipKeyDataPerGameToggle,
                              _currentSlipSource, SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75);
            }
            finally { _suppressKeyDataEvents = false; }
        }

        private void ReloadChannel(KeyDataPointSettings k,
            MahApps.Metro.Controls.ToggleSwitch perGame,
            string sourceIdentity,
            MahApps.Metro.Controls.NumericUpDown sMaxBox,
            MahApps.Metro.Controls.NumericUpDown s90Box,
            MahApps.Metro.Controls.NumericUpDown s75Box)
        {
            if (k == null) return;

            // Read the TOGGLE rather than the persisted flag: the driver may have just flipped it without
            // pressing Apply, and the slot they are looking at is the one the toggles describe.
            bool previousPerGame = k.PerGame;
            k.PerGame = perGame.IsChecked == true;
            try
            {
                double sMax, s90, s75;
                if (k.TryGetManual(_currentGameId, sourceIdentity, out sMax, out s90, out s75))
                {
                    sMaxBox.Value = sMax; s90Box.Value = s90; s75Box.Value = s75;
                }
                else
                {
                    // Nothing stored for this slot - clear, so the watermark reads "---" and the seeding
                    // path decides what (if anything) belongs here.
                    sMaxBox.Value = null; s90Box.Value = null; s75Box.Value = null;
                }
            }
            finally { k.PerGame = previousPerGame; }
        }

        private void LoadKeyDataPoints(QAdvanceFeedbackSettings s)
        {
            _suppressKeyDataEvents = true;
            try
            {
                LoadChannel(s.Lock.KeyDataPoints, LockKeyDataAutoToggle, LockKeyDataPerGameToggle,
                            _currentLockSource, LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75);
                LoadChannel(s.Slip.KeyDataPoints, SlipKeyDataAutoToggle, SlipKeyDataPerGameToggle,
                            _currentSlipSource, SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75);
            }
            finally { _suppressKeyDataEvents = false; }

            SeedManualBoxesFromLearnedIfNeeded();
            RefreshKeyDataPointUi();
        }

        private void LoadChannel(KeyDataPointSettings k,
            MahApps.Metro.Controls.ToggleSwitch auto,
            MahApps.Metro.Controls.ToggleSwitch perGame,
            string sourceIdentity,
            MahApps.Metro.Controls.NumericUpDown sMaxBox,
            MahApps.Metro.Controls.NumericUpDown s90Box,
            MahApps.Metro.Controls.NumericUpDown s75Box)
        {
            if (k == null) return;
            auto.IsChecked = k.AutoGenerate;
            perGame.IsChecked = k.PerGame;

            // ROUTED BY PerGame, not read straight off the global fields. Reading k.SMax directly would
            // show the global numbers while Per-Game was on, so the boxes would display one set and the
            // engine would apply another.
            double sMax, s90, s75;
            if (!k.TryGetManual(_currentGameId, sourceIdentity, out sMax, out s90, out s75)) return;
            sMaxBox.Value = sMax; s90Box.Value = s90; s75Box.Value = s75;
        }

        private void SaveKeyDataPoints(QAdvanceFeedbackSettings s)
        {
            SaveChannel(s.Lock.KeyDataPoints, LockKeyDataAutoToggle, LockKeyDataPerGameToggle,
                        _currentLockSource, LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75,
                        GetSelectedTag(LockNormalizePatternCombo, "Mapping") == "MaxGripOnly");
            SaveChannel(s.Slip.KeyDataPoints, SlipKeyDataAutoToggle, SlipKeyDataPerGameToggle,
                        _currentSlipSource, SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75,
                        GetSelectedTag(SlipNormalizePatternCombo, "MaxGripOnly") == "MaxGripOnly");
        }

        private void SaveChannel(KeyDataPointSettings k,
            MahApps.Metro.Controls.ToggleSwitch auto,
            MahApps.Metro.Controls.ToggleSwitch perGame,
            string sourceIdentity,
            MahApps.Metro.Controls.NumericUpDown sMaxBox,
            MahApps.Metro.Controls.NumericUpDown s90Box,
            MahApps.Metro.Controls.NumericUpDown s75Box,
            bool maxGripOnly)
        {
            if (k == null) return;
            k.AutoGenerate = auto.IsChecked == true;
            k.PerGame = perGame.IsChecked == true;

            double sMax = sMaxBox.Value ?? 0.0;
            double s90 = s90Box.Value ?? 0.0;
            double s75 = s75Box.Value ?? 0.0;

            // MAX-GRIP-ONLY keeps the two hidden anchors derived from SMax, so switching to the
            // three-point mapping later never finds them stale or out of order (the owner's "just in
            // case the user switch to SMax/S90/S75, the 3 values will not messed up").
            if (maxGripOnly) KeyDataPointSettings.DeriveLowerAnchors(sMax, out s90, out s75);

            if (!KeyDataPointSettings.IsValid(sMax, s90, s75)) return;   // keep the last good triple

            // ROUTED BY PerGame - writing k.SMax directly would put the current game's numbers into the
            // GLOBAL slot, silently overwriting the shared values and leaving the per-game entry stale.
            // Per-Game with no game running writes nothing rather than guessing a key.
            // SAVE WRITES THE SLOT THE TOGGLES DESCRIBE - global mode writes the source's global slot,
            // per-game mode writes only the current game's. An unknown source with nothing learned yet
            // simply has no valid triple to write, so the SELECTION (Auto/Per-Game) is still persisted by
            // the assignments above and the numbers arrive later via the one-time seed.
            k.SetManual(_currentGameId, sourceIdentity, sMax, s90, s75, seeded: false);
        }

        /// <summary>
        /// Push the current learned values (and whether a manual value is actually live yet) in from the
        /// plugin, so the "[Learned Value: xx.x]" hints stay current while the settings page is open.
        /// </summary>
        public void UpdateLearnedKeyDataPoints(
            string gameId, string lockSourceIdentity, string slipSourceIdentity, int keyDataRevision,
            double? lockSMax, double? lockS90, double? lockS75, bool lockManualLive,
            double? slipSMax, double? slipS90, double? slipS75, bool slipManualLive)
        {
            // A CHANGE OF GAME OR SOURCE IS A CHANGE OF SLOT. Reload before anything else, so the boxes
            // show the target slot's own numbers rather than the previous context's - and so the seeding
            // check below tests the RIGHT slot's latch.
            bool contextChanged = !string.Equals(_currentGameId, gameId, StringComparison.Ordinal);

            // A SEED CHANGES THE STORED VALUES WITHOUT CHANGING THE CONTEXT - the plugin owns the
            // one-time seed now (it must work with this page closed), so the page finds out via the
            // revision counter and reloads the boxes to match what was just written.
            bool seeded = keyDataRevision != _lastKeyDataRevision;
            _lastKeyDataRevision = keyDataRevision;

            // The GAME can only come from the plugin. The SOURCES deliberately do NOT: this page may be
            // showing edits that have not been applied yet, and the push carries the SAVED configuration -
            // letting it win would snap the key data points back to the old source mid-edit.
            _currentGameId = gameId;
            if (contextChanged || seeded)
            {
                ReloadKeyDataForCurrentContext();
                RefreshBothCurvePlots();   // the one-time write after warm-up changes the mapping
            }
            _lockLearnedSMax = lockSMax; _lockLearnedS90 = lockS90; _lockLearnedS75 = lockS75;
            _slipLearnedSMax = slipSMax; _slipLearnedS90 = slipS90; _slipLearnedS75 = slipS75;
            _lockManualLive = lockManualLive; _slipManualLive = slipManualLive;

            if (LockKeyDataAutoToggle.IsChecked == true || SlipKeyDataAutoToggle.IsChecked == true)
                SeedManualBoxesFromLearnedIfNeeded();

            RefreshKeyDataPointUi();
        }

        /// <summary>The context the stored values are keyed by - game and each channel's source. Pushed
        /// in with the learned values; a change to any of them selects a DIFFERENT slot and therefore
        /// reloads the boxes (see <see cref="ReloadKeyDataForCurrentContext"/>).</summary>
        private string _currentGameId;
        private string _currentLockSource;
        private string _currentSlipSource;

        /// <summary>Last seed revision seen from the plugin - see <see cref="UpdateLearnedKeyDataPoints"/>.
        /// The SEED ITSELF lives in the plugin's frame loop, deliberately: it has to happen whether or
        /// not this page is open, so this class only ever reflects the result.</summary>
        private int _lastKeyDataRevision;

        /// <summary>
        /// Reset this channel's key data points, from the button that only appears in manual mode.
        /// <para/>
        /// A KNOWN source (our own Raw, a ShakeIt export) has shipped defaults, so those are written in
        /// and the slot is marked seeded - the driver asked for the shipped numbers, and the one-time
        /// learned-value seed must not immediately overwrite them.
        /// <para/>
        /// An UNKNOWN source (a script, an NCalc expression) has no honest default, so the slot is
        /// CLEARED instead - values and seeded latch both - which re-arms the one-time seed. The boxes
        /// stay empty until the first valid learned value arrives and fills them, which is the owner's
        /// "reset to empty for first valid data".
        /// <para/>
        /// Persists immediately, like the Sources reset and Restore-to-Default already do, so the driver
        /// does not have to remember a second click for a destructive action.
        /// </summary>
        private void ResetKeyDataPoints(bool isLock)
        {
            if (_plugin == null || _plugin.Settings == null) return;

            KeyDataPointSettings keyData = isLock
                ? _plugin.Settings.Lock.KeyDataPoints : _plugin.Settings.Slip.KeyDataPoints;
            if (keyData == null) return;

            string sourceIdentity = isLock ? _currentLockSource : _currentSlipSource;
            keyData.PerGame = (isLock ? LockKeyDataPerGameToggle : SlipKeyDataPerGameToggle).IsChecked == true;

            MahApps.Metro.Controls.NumericUpDown sMaxBox = isLock ? LockKeyDataSMax : SlipKeyDataSMax;
            MahApps.Metro.Controls.NumericUpDown s90Box = isLock ? LockKeyDataS90 : SlipKeyDataS90;
            MahApps.Metro.Controls.NumericUpDown s75Box = isLock ? LockKeyDataS75 : SlipKeyDataS75;

            // The STRICT check, matching every other decision on this page - the lenient Classify would
            // hand shipped numbers to a driver-named export whose range nobody has measured.
            double sMax, s90, s75;
            bool hasDefault = KeyDataPointSettings.TryResolveShippedDefaults(sourceIdentity, isLock, ConfiguredDefaults, out sMax, out s90, out s75);

            _suppressKeyDataEvents = true;
            try
            {
                if (hasDefault)
                {
                    sMaxBox.Value = sMax; s90Box.Value = s90; s75Box.Value = s75;
                    keyData.SetManual(_currentGameId, sourceIdentity, sMax, s90, s75, seeded: true);
                }
                else
                {
                    sMaxBox.Value = null; s90Box.Value = null; s75Box.Value = null;
                    keyData.ClearSlot(_currentGameId, sourceIdentity);
                }
            }
            finally { _suppressKeyDataEvents = false; }

            _plugin.ApplySettings();
            RefreshKeyDataPointUi();
        }

        /// <summary>Debounce for writing typed key data points to disk - long enough that holding a
        /// spinner does not write once per tick, short enough to feel immediate.</summary>
        private static readonly TimeSpan KeyDataPersistDelay = TimeSpan.FromMilliseconds(700);

        private System.Windows.Threading.DispatcherTimer _keyDataPersistTimer;
        private System.Windows.Threading.DispatcherTimer _graphRefreshTimer;

        /// <summary>
        /// Persist the typed values shortly after the driver stops changing them, and mark the slot
        /// seeded so the automatic one-time write stands down for good.
        /// </summary>
        private void ScheduleKeyDataPersist()
        {
            if (_keyDataPersistTimer == null)
            {
                _keyDataPersistTimer = new System.Windows.Threading.DispatcherTimer { Interval = KeyDataPersistDelay };
                _keyDataPersistTimer.Tick += (s, e) =>
                {
                    _keyDataPersistTimer.Stop();
                    SafeUiAction(PersistKeyDataPointsNow, HealthSubsystems.SettingsUi);
                };
            }
            _keyDataPersistTimer.Stop();
            _keyDataPersistTimer.Start();
        }

        private void PersistKeyDataPointsNow()
        {
            if (_plugin == null || _plugin.Settings == null) return;

            bool wrote = false;
            wrote |= PersistChannelIfManual(_plugin.Settings.Lock.KeyDataPoints, LockKeyDataAutoToggle,
                LockKeyDataPerGameToggle, _currentLockSource,
                LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75,
                GetSelectedTag(LockNormalizePatternCombo, "Mapping") == "MaxGripOnly");
            wrote |= PersistChannelIfManual(_plugin.Settings.Slip.KeyDataPoints, SlipKeyDataAutoToggle,
                SlipKeyDataPerGameToggle, _currentSlipSource,
                SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75,
                GetSelectedTag(SlipNormalizePatternCombo, "MaxGripOnly") == "MaxGripOnly");

            if (wrote) _plugin.ApplySettings();
        }

        private bool PersistChannelIfManual(KeyDataPointSettings k,
            MahApps.Metro.Controls.ToggleSwitch auto,
            MahApps.Metro.Controls.ToggleSwitch perGame,
            string sourceIdentity,
            MahApps.Metro.Controls.NumericUpDown sMaxBox,
            MahApps.Metro.Controls.NumericUpDown s90Box,
            MahApps.Metro.Controls.NumericUpDown s75Box,
            bool maxGripOnly)
        {
            if (k == null || auto.IsChecked == true) return false;

            k.PerGame = perGame.IsChecked == true;
            if (k.PerGame && string.IsNullOrEmpty(_currentGameId)) return false;

            double sMax = sMaxBox.Value ?? 0.0, s90 = s90Box.Value ?? 0.0, s75 = s75Box.Value ?? 0.0;
            if (maxGripOnly) KeyDataPointSettings.DeriveLowerAnchors(sMax, out s90, out s75);
            if (!KeyDataPointSettings.IsValid(sMax, s90, s75)) return false;

            // seeded: true - the driver has spoken, so the automatic one-time write must not fire here.
            k.SetManual(_currentGameId, sourceIdentity, sMax, s90, s75, seeded: true);
            return true;
        }

        /// <summary>
        /// Redraw both channels' graphs from the current working projector settings. Cheap - a couple of
        /// hundred sampled points and some lines - so it is fine on a timer.
        /// </summary>
        private void RefreshBothCurvePlots()
        {
            if (_workingLockProjector != null) RefreshCurvePlot(_workingLockProjector, ProjectionChannel.Lock);
            if (_workingSlipProjector != null) RefreshCurvePlot(_workingSlipProjector, ProjectionChannel.Slip);
        }

        /// <summary>
        /// While the page is open, keep the source-to-projected graph current.
        /// <para/>
        /// Under AUTO the key data points move on their own as the plugin learns, and nothing else would
        /// prompt a redraw - so it ticks once a second, matching the existing learned-value readouts. In
        /// MANUAL the graph follows the driver's own edits instead (see OnKeyDataValueChanged), and the
        /// one-time write after warm-up arrives through the revision counter, so a timer would only be
        /// redrawing an unchanged picture.
        /// </summary>
        /// <summary>The window this control is hosted in, once it has one - kept so its handlers can be
        /// detached again on unload rather than outliving the control.</summary>
        private Window _hostWindow;

        private void StartGraphRefreshTimer()
        {
            if (_graphRefreshTimer != null) return;
            _graphRefreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _graphRefreshTimer.Tick += (s, e) =>
            {
                // Under manual the graph follows the driver's own edits, so a tick would only redraw an
                // unchanged picture. Only Auto has values that move on their own.
                if (LockKeyDataAutoToggle.IsChecked != true && SlipKeyDataAutoToggle.IsChecked != true) return;
                SafeUiAction(RefreshBothCurvePlots, HealthSubsystems.SettingsUi);
            };

            // The control has no Window at construction time, so the window-level hooks wait for Loaded.
            IsVisibleChanged += (s, e) => UpdateGraphRefreshState();
            Loaded += (s, e) => { AttachHostWindowHandlers(); UpdateGraphRefreshState(); };
            Unloaded += (s, e) => { DetachHostWindowHandlers(); StopGraphRefreshTimer(); };

            AttachHostWindowHandlers();
            UpdateGraphRefreshState();
        }

        private void AttachHostWindowHandlers()
        {
            Window window = Window.GetWindow(this);
            if (window == null || ReferenceEquals(window, _hostWindow)) return;

            DetachHostWindowHandlers();
            _hostWindow = window;
            _hostWindow.Activated += OnHostWindowStateChanged;
            _hostWindow.Deactivated += OnHostWindowStateChanged;
            _hostWindow.StateChanged += OnHostWindowStateChanged;
        }

        private void DetachHostWindowHandlers()
        {
            if (_hostWindow == null) return;
            _hostWindow.Activated -= OnHostWindowStateChanged;
            _hostWindow.Deactivated -= OnHostWindowStateChanged;
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = null;
        }

        private void OnHostWindowStateChanged(object sender, EventArgs e) => UpdateGraphRefreshState();

        private void StopGraphRefreshTimer()
        {
            if (_graphRefreshTimer != null) _graphRefreshTimer.Stop();
        }

        /// <summary>
        /// Run the once-a-second redraw only while the driver can actually SEE it, and pick straight back
        /// up the moment they can.
        /// <para/>
        /// Nobody benefits from a timer redrawing a canvas behind a minimised window or an unfocused one,
        /// and this runs inside SimHub's own process while a game is being driven - so the cheapest thing
        /// to do off-screen is nothing at all. Resuming does an IMMEDIATE redraw rather than waiting out
        /// the next tick, so the graph is already current when the page comes back into view instead of
        /// showing up to a second of stale mapping.
        /// <para/>
        /// Three signals, because no single one covers every way a page stops being visible:
        /// <list type="bullet">
        /// <item><see cref="UIElement.IsVisible"/> - a collapsed control, or a different settings tab.</item>
        /// <item><see cref="Window.WindowState"/> - minimised, which leaves IsVisible true.</item>
        /// <item><see cref="Window.IsActive"/> - alt-tabbed away to the game, which leaves both of the
        /// above unchanged.</item>
        /// </list>
        /// If the control has no Window yet (or is hosted somewhere that reports neither), the
        /// window-level checks are simply skipped and visibility alone governs - the owner's "cover
        /// whatever we can detect".
        /// </summary>
        private void UpdateGraphRefreshState()
        {
            if (_graphRefreshTimer == null) return;

            bool visible = IsVisible;
            Window window = _hostWindow ?? Window.GetWindow(this);
            if (window != null)
            {
                if (window.WindowState == WindowState.Minimized) visible = false;
                if (!window.IsActive) visible = false;
            }

            if (!visible)
            {
                _graphRefreshTimer.Stop();
                return;
            }

            if (!_graphRefreshTimer.IsEnabled)
            {
                _graphRefreshTimer.Start();
                SafeUiAction(RefreshBothCurvePlots, HealthSubsystems.SettingsUi);   // catch up at once
            }
        }

        private void RefreshKeyDataPointUi()
        {
            RefreshKeyDataChannel(
                LockKeyDataAutoToggle, LockKeyDataPerGameToggle, LockKeyDataPerGameLabel, LockKeyDataPerGameDesc, LockKeyDataReset,
                LockKeyDataSMax, LockKeyDataS90, LockKeyDataS75,
                LockKeyDataSMaxLearned, LockKeyDataS90Learned, LockKeyDataS75Learned,
                LockKeyDataS90Label, LockKeyDataS75Label, LockKeyDataS90Desc, LockKeyDataS75Desc,
                LockKeyDataStatus,
                _lockLearnedSMax, _lockLearnedS90, _lockLearnedS75, _lockManualLive,
                GetSelectedTag(LockNormalizePatternCombo, "Mapping") == "MaxGripOnly",
                KeyDataPointSettings.IsExactShippedSource(_currentLockSource, isLockChannel: true),
                ConfiguredDefaults,
                Strings.Get("KeyData.Invalid"));

            RefreshKeyDataChannel(
                SlipKeyDataAutoToggle, SlipKeyDataPerGameToggle, SlipKeyDataPerGameLabel, SlipKeyDataPerGameDesc, SlipKeyDataReset,
                SlipKeyDataSMax, SlipKeyDataS90, SlipKeyDataS75,
                SlipKeyDataSMaxLearned, SlipKeyDataS90Learned, SlipKeyDataS75Learned,
                SlipKeyDataS90Label, SlipKeyDataS75Label, SlipKeyDataS90Desc, SlipKeyDataS75Desc,
                SlipKeyDataStatus,
                _slipLearnedSMax, _slipLearnedS90, _slipLearnedS75, _slipManualLive,
                GetSelectedTag(SlipNormalizePatternCombo, "MaxGripOnly") == "MaxGripOnly",
                KeyDataPointSettings.IsExactShippedSource(_currentSlipSource, isLockChannel: false),
                ConfiguredDefaults,
                Strings.Get("KeyData.Invalid.Slip"));
        }

        private static void RefreshKeyDataChannel(
            MahApps.Metro.Controls.ToggleSwitch auto,
            MahApps.Metro.Controls.ToggleSwitch perGame,
            TextBlock perGameLabel, TextBlock perGameDesc, Button resetButton,
            MahApps.Metro.Controls.NumericUpDown sMaxBox,
            MahApps.Metro.Controls.NumericUpDown s90Box,
            MahApps.Metro.Controls.NumericUpDown s75Box,
            TextBlock sMaxLearned, TextBlock s90Learned, TextBlock s75Learned,
            TextBlock s90Label, TextBlock s75Label, TextBlock s90Desc, TextBlock s75Desc,
            TextBlock status,
            double? learnedSMax, double? learnedS90, double? learnedS75, bool manualLive,
            bool maxGripOnly, bool knownSource, KeyDataPointDefaults defaults, string invalidMessage)
        {
            bool isAuto = auto.IsChecked == true;

            // PER-GAME is meaningless under Auto - hidden rather than disabled, per the owner's "this row
            // ONLY visible if Auto-Generate untoggled".
            Visibility perGameVisibility = isAuto ? Visibility.Collapsed : Visibility.Visible;
            perGame.Visibility = perGameVisibility;
            perGameLabel.Visibility = perGameVisibility;
            perGameDesc.Visibility = perGameVisibility;
            resetButton.Visibility = perGameVisibility;

            // MAX-GRIP-ONLY hides the two lower anchors entirely - they are derived, not configured.
            Visibility lowerVisibility = maxGripOnly ? Visibility.Collapsed : Visibility.Visible;
            // s90Learned/s75Learned are deliberately NOT in this list - their visibility also depends on
            // Auto, and is set below where both conditions are known.
            foreach (UIElement e in new UIElement[] { s90Box, s75Box, s90Label, s75Label, s90Desc, s75Desc })
                e.Visibility = lowerVisibility;

            // EDITABLE WHENEVER MANUAL. An earlier revision also required the warm-up gate, which meant
            // a driver who chose manual could not type anything for the first 30 seconds - the setting
            // looked broken. Typing a value now applies it immediately (see OnKeyDataValueChanged); the
            // gate only governs the automatic one-time seed, which is a different thing.
            bool editable = !isAuto;
            sMaxBox.IsEnabled = editable;
            s90Box.IsEnabled = editable && !maxGripOnly;
            s75Box.IsEnabled = editable && !maxGripOnly;

            // UNDER AUTO THE LEARNED VALUE *IS* THE PUBLISHED VALUE, shown in the boxes themselves - so a
            // second copy beside them would just repeat the same number. The hint exists to let a driver
            // compare their own figure against what the plugin measured, which only means anything in
            // manual mode.
            //
            // On an UNKNOWN source it is also withheld until the warm-up finishes: before then the
            // learned value is still the cold-start blend rather than a measurement of this signal, and
            // showing it would invite the driver to copy a number that is about to move.
            bool hintReady = knownSource || manualLive;
            Visibility hintVisibility = isAuto || !hintReady ? Visibility.Collapsed : Visibility.Visible;
            sMaxLearned.Visibility = hintVisibility;
            s90Learned.Visibility = maxGripOnly ? Visibility.Collapsed : hintVisibility;
            s75Learned.Visibility = maxGripOnly ? Visibility.Collapsed : hintVisibility;

            sMaxLearned.Text = FormatLearned(learnedSMax);
            s90Learned.Text = FormatLearned(learnedS90);
            s75Learned.Text = FormatLearned(learnedS75);

            if (isAuto)
            {
                status.Visibility = Visibility.Collapsed;
                return;
            }

            // The warm-up notice belongs ONLY to an unknown source with nothing configured: a shipped
            // source has its defaults in the boxes already, and a driver who typed a value is not waiting
            // for anything either. Showing it in those cases was just noise.
            bool configured = (sMaxBox.Value ?? 0.0) > 0.0;
            if (!knownSource && !configured && !manualLive)
            {
                status.Text = Strings.Get("KeyData.Waiting");
                status.Visibility = Visibility.Visible;
                return;
            }

            if (!configured)
            {
                status.Visibility = Visibility.Collapsed;
                return;
            }

            double sMax = sMaxBox.Value ?? 0.0, s90 = s90Box.Value ?? 0.0, s75 = s75Box.Value ?? 0.0;
            if (maxGripOnly) KeyDataPointSettings.DeriveLowerAnchors(sMax, out s90, out s75);
            bool valid = KeyDataPointSettings.IsValid(sMax, s90, s75);
            status.Text = invalidMessage;
            status.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The learned-value hint. Fixed width, monospace column in the XAML, and the number
        /// right-aligned in a 5-character field, so 9.3 and 10.5 line up (the owner's explicit
        /// requirement) rather than shifting with the digit count.</summary>
        private static string FormatLearned(double? learned)
            => learned.HasValue && learned.Value > 0.0
                ? string.Format(Strings.Get("KeyData.LearnedPrefix"),
                    learned.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5))
                : Strings.Get("KeyData.LearnedPending");

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
                box.TextChanged += (s, e) => { MarkDirty(); OnSourceConfigurationChanged(); };

            // ---- Toggles (were CheckBoxes before v1.0.7.2). Every switch in this UI is now the SAME
            //      MahApps ToggleSwitch the "Integrate Wheel Lock and Slip" feature switch already used,
            //      so a toggle reads identically wherever it appears. ----
            MahApps.Metro.Controls.ToggleSwitch[] checkBoxes =
                { LockPulseEnabled, SlipPulseEnabled, EnableDiagnosticsCheckBox, ExportCsvCheckBox };
            foreach (MahApps.Metro.Controls.ToggleSwitch box in checkBoxes)
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

            // SLIP'S OWN PATTERN SELECTOR (v1.0.7.2) - same enum as Lock, named for what Slip measures.
            SlipLblNormalizePattern.Text = Strings.Get("Curve.NormalizePattern.Label");
            SlipNormalizePatternMaxGripOnly.Content = Strings.Get("Curve.NormalizePattern.Slip.MaxGripOnly");
            SlipNormalizePatternMapping.Content = Strings.Get("Curve.NormalizePattern.Slip.Mapping");

            LocalizeKeyDataPoints();

            LockLblPreset.Text = SlipLblPreset.Text = Strings.Get("Curve.Preset.Label");
            LockPresetLinear.Content = SlipPresetLinear.Content = Strings.Get("Curve.Preset.Linear");
            LockPresetCurve.Content = SlipPresetCurve.Content = Strings.Get("Curve.Preset.Curve");
            LockPresetCustom.Content = SlipPresetCustom.Content = Strings.Get("Curve.Preset.Custom");
            LockPresetLinearCustom.Content = SlipPresetLinearCustom.Content = Strings.Get("Curve.Preset.LinearCustom");
            LockPresetCurveCustom.Content = SlipPresetCurveCustom.Content = Strings.Get("Curve.Preset.CurveCustom");
            LockCurveGraphCaption.Text = SlipCurveGraphCaption.Text = Strings.Get("Curve.Graph.Normalized");
            LockSourceGraphCaption.Text = SlipSourceGraphCaption.Text = Strings.Get("Curve.Graph.Source");
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
            LockPulseEnabledLabel.Text = SlipPulseEnabledLabel.Text = Strings.Get("Pulse.Enable");
            LockLblGapMs.Text = SlipLblGapMs.Text = Strings.Get("Pulse.GapMs");
            LockLblMinValue.Text = SlipLblMinValue.Text = Strings.Get("Pulse.MinValue");
            LockPulseFloorNote.Text = SlipPulseFloorNote.Text = Strings.Get("Pulse.FloorNote");

            GForceMaximaGroup.Header = Strings.Get("Group.GForceMaxima");
            GForceMaximaNote.Text = Strings.Get("GForce.Maxima.Note");
            GForceLblAccelMax.Text = Strings.Get("GForce.AccelMax.Label");
            GForceLblDecelMax.Text = Strings.Get("GForce.DecelMax.Label");
            GForceAccelAutoLabel.Text = GForceDecelAutoLabel.Text = Strings.Get("GForce.Mode.Auto.Short");
            ApplyOnOffLabelsToEverySwitch();

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
            EnableDiagnosticsHeader.Text = Strings.Get("General.EnableDiagnostics");
            EnableDiagnosticsNote.Text = Strings.Get("General.EnableDiagnostics.Note");
            EnableDiagnosticsRestartNote.Text = Strings.Get("General.EnableDiagnostics.RestartNote");
            ExportCsvHeader.Text = Strings.Get("General.ExportCsv");
            ExportCsvNote.Text = Strings.Get("General.ExportCsv.Note");
            GeneralVersionText.Text = string.Format(Strings.Get("General.Version.Label"), GetRunningAssemblyFileVersion());

            HealthGroup.Header = Strings.Get("Group.Health");
            HealthCopyDetailsButton.Content = Strings.Get("Health.CopyDetails");

            ShakeItImportGroup.Header = Strings.Get("General.ShakeItImport.Group");
            ShakeItImportNote.Text = Strings.Get("General.ShakeItImport.Note");
            ShakeItImportOverrideHeader.Text = Strings.Get("General.ShakeItImport.Override");
            ShakeItImportOverrideNote.Text = Strings.Get("General.ShakeItImport.Override.Note");
            ShakeItImportButton.Content = Strings.Get("General.ShakeItImport.Button");
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
            // A wheel switching between Plain and a script changes the identity (scripts are hashed), so
            // the key data points must be re-resolved for the source that now applies.
            OnSourceConfigurationChanged();

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
            OnSourceConfigurationChanged();
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

        // ================= GRAPH RENDERING (1.0.7.0) =================

        /// <summary>Tag on every element this class draws into a graph canvas, so a redraw can remove
        /// exactly its own decorations without touching the XAML-declared curve and markers.</summary>
        private const string GraphDecorationTag = "graph-decoration";

        /// <summary>Height of the bottom tick marking every 20% of the horizontal axis. Tall enough
        /// to read as a scale against the baseline rather than as noise on it.</summary>
        private const double HorizontalTickHeight = 5.0;

        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(26, 0, 0, 0));   // 10% black
        private static readonly Brush AxisBrush = Brushes.Black;
        private static readonly Brush KeyPointBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)); // 50% black

        private static void ClearDecorations(Canvas canvas)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                var element = canvas.Children[i] as FrameworkElement;
                if (element != null && GraphDecorationTag.Equals(element.Tag)) canvas.Children.RemoveAt(i);
            }
        }

        private static void AddDecoration(Canvas canvas, FrameworkElement element)
        {
            element.Tag = GraphDecorationTag;
            canvas.Children.Insert(0, element);   // behind the curve and its markers
        }

        /// <summary>
        /// Grid and axis furniture, identical on both graphs: a 25%-black horizontal rule every 20% of
        /// height (including 0 and 100), the 0% rule redrawn solid black as the baseline, and a 3px tick
        /// below every 20% of width.
        /// </summary>
        private static void RenderGraphDecorations(Canvas canvas)
        {
            ClearDecorations(canvas);
            double w = canvas.Width, h = canvas.Height;

            for (int pct = 0; pct <= 100; pct += 20)
            {
                double y = h - pct / 100.0 * h;
                // The 0% line is the baseline the eye reads the whole curve against, so it is full black
                // rather than another faint grid rule.
                bool baseline = pct == 0;
                if (baseline) y -= 0.5;   // keep a 1px line crisp instead of straddling two rows
                AddDecoration(canvas, new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = baseline ? AxisBrush : GridBrush,
                    StrokeThickness = 1,
                });
            }

            for (int pct = 0; pct <= 100; pct += 20)
            {
                double x = pct / 100.0 * w;
                if (pct == 100) x -= 1;   // the last tick would otherwise fall outside the clip
                AddDecoration(canvas, new Line
                {
                    X1 = x, X2 = x, Y1 = h - HorizontalTickHeight, Y2 = h,
                    Stroke = AxisBrush,
                    StrokeThickness = 1,
                });
            }
        }

        /// <summary>
        /// The key data points, drawn on the NORMALIZED graph at the positions the four-range curve maps
        /// them to by construction: S75 -> 30, S90 -> 60, SMax -> 80. Under Max-Grip-Only the two lower
        /// anchors do not exist, so only SMax is drawn.
        /// <para/>
        /// Labels sit at the top, right-aligned against their own line, and are skipped when they would
        /// collide with the next line along - a label crossing a neighbouring marker reads as though it
        /// belongs to that one.
        /// </summary>
        private void RenderKeyPointMarkers(Canvas canvas, bool isLock)
        {
            double w = canvas.Width, h = canvas.Height;
            bool maxGripOnly = GetSelectedTag(
                isLock ? LockNormalizePatternCombo : SlipNormalizePatternCombo,
                isLock ? "Mapping" : "MaxGripOnly") == "MaxGripOnly";

            // Canonical normalized positions of the three anchors - see
            // NormalizedWheelLockSlipEngine.TryBuildLockRangeCurve's own knot table.
            var points = maxGripOnly
                ? new[] { new { At = 80.0, Key = isLock ? "KeyData.Marker.SMax" : "KeyData.Marker.Perfect" } }
                : new[]
                {
                    new { At = 30.0, Key = isLock ? "KeyData.Marker.S75" : "KeyData.Marker.Good" },
                    new { At = 60.0, Key = isLock ? "KeyData.Marker.S90" : "KeyData.Marker.Great" },
                    new { At = 80.0, Key = isLock ? "KeyData.Marker.SMax" : "KeyData.Marker.Perfect" },
                };

            for (int i = 0; i < points.Length; i++)
            {
                double x = points[i].At / 100.0 * w;
                AddDecoration(canvas, new Line
                {
                    X1 = x, X2 = x, Y1 = 0, Y2 = h,
                    Stroke = KeyPointBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                });

                var label = new TextBlock
                {
                    Text = Strings.Get(points[i].Key),
                    Foreground = KeyPointBrush,
                    FontSize = 9,
                    Margin = new Thickness(0),
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double labelWidth = label.DesiredSize.Width;

                // Right-aligned against its own line; dropped entirely if it would run back across the
                // previous line rather than sitting in the gap between them.
                double previousX = i == 0 ? 0.0 : points[i - 1].At / 100.0 * w;
                if (x - labelWidth - 2 < previousX + 1) continue;

                Canvas.SetLeft(label, x - labelWidth - 2);
                Canvas.SetTop(label, 1);
                AddDecoration(canvas, label);
            }
        }

        /// <summary>
        /// The right-hand graph: SOURCE value all the way through to the published projection.
        /// <para/>
        /// There is no closed form for this - the key data points and the projector curve compose - so it
        /// is SAMPLED every 5 across 0-100 and the points joined, exactly as the owner specified. The
        /// normalization step mirrors what the engine does: the four-range curve under Mapping, or the
        /// plain SMax rescale under Max-Grip-Only.
        /// </summary>
        private void RenderSourceToProjectedGraph(bool isLock, OutputProjector projector)
        {
            Canvas canvas = isLock ? LockSourceCanvas : SlipSourceCanvas;
            Polyline polyline = isLock ? LockSourcePolyline : SlipSourcePolyline;
            RenderGraphDecorations(canvas);

            double w = canvas.Width, h = canvas.Height;
            bool maxGripOnly = GetSelectedTag(
                isLock ? LockNormalizePatternCombo : SlipNormalizePatternCombo,
                isLock ? "Mapping" : "MaxGripOnly") == "MaxGripOnly";

            double sMax = (isLock ? LockKeyDataSMax : SlipKeyDataSMax).Value
                ?? (isLock ? KeyDataPointSettings.LockDefaultSMax : KeyDataPointSettings.SlipDefaultSMax);
            double s90 = (isLock ? LockKeyDataS90 : SlipKeyDataS90).Value ?? sMax * KeyDataPointSettings.DerivedS90Fraction;
            double s75 = (isLock ? LockKeyDataS75 : SlipKeyDataS75).Value ?? sMax * KeyDataPointSettings.DerivedS75Fraction;

            // The SAME piecewise construction the engine uses - four segments under Mapping, two under
            // Max-Grip-Only. Drawing the old straight rescale here would show a graph that disagreed with
            // what the channel actually publishes.
            MonotoneCubicCurve rangeCurve = null;
            if (sMax > 0.0 && sMax < 100.0)
            {
                rangeCurve = maxGripOnly
                    ? new MonotoneCubicCurve(new[] { 0.0, sMax, 100.0 }, new[] { 0.0, 80.0, 100.0 })
                    : KeyDataPointSettings.IsValid(sMax, s90, s75) && s75 > 0.0
                        ? new MonotoneCubicCurve(
                            new[] { 0.0, s75, s90, sMax, 100.0 },
                            new[] { 0.0, 30.0, 60.0, 80.0, 100.0 })
                        : null;
            }

            var points = new PointCollection();
            for (int source = 0; source <= 100; source += 5)
            {
                double normalized = rangeCurve != null
                    ? rangeCurve.Evaluate(source)
                    : ClampMath.To0100(sMax > 0.0 ? source * global::QAdvanceFeedback.Core.Normalized.KeyedScaleLearner.CanonicalAtLimitAnchor / sMax : source);
                double projected = projector.Project(ClampMath.To0100(normalized));
                points.Add(new Point(source / 100.0 * w, h - projected / 100.0 * h));
            }
            polyline.Points = points;
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

            RenderGraphDecorations(canvas);
            RenderKeyPointMarkers(canvas, isLock);
            RenderSourceToProjectedGraph(isLock, projector);

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

                // BOTH CHANNELS' SOURCES LOAD FIRST, before either identity is computed from them.
                //
                // THE BUG THIS ORDERING FIXES: ComputeCurrentSourceIdentity hashes the source TEXT BOXES,
                // so a channel whose rows have not been filled in yet hashes four EMPTY strings - an
                // identity that matches no shipped configuration. Slip's own LoadChannel used to sit ~30
                // lines below the identity calls, so on every single load Wheel Slip was classified as an
                // unknown source: no shipped default reached the box ("---" where 75 belonged), the stored
                // slot looked up under the empty identity so a driver's saved manual values never came
                // back, and every known-vs-unknown branch in RefreshKeyDataChannel decided against an
                // identity that was never real. Lock was unaffected only because its rows happened to load
                // first, which is exactly why the two channels disagreed on screen.
                LoadChannel(s.Lock, _lockRows, LockPulseEnabled, LockPulseGapMs, LockPulseMinValue);
                SetSourceModeCombo(isLock: true, s.Lock.SourceMode);
                RefreshSourceModeUi(isLock: true);
                LoadChannel(s.Slip, _slipRows, SlipPulseEnabled, SlipPulseGapMs, SlipPulseMinValue);
                SetSourceModeCombo(isLock: false, s.Slip.SourceMode);
                RefreshSourceModeUi(isLock: false);

                LockBrakeThreshold.Value = s.Lock.BrakeThresholdPercent;
                LockSensibility.Value = s.Lock.LockSensibility;
                // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock only.
                SelectComboItemByTag(LockNormalizePatternCombo, s.Lock.NormalizePattern.ToString());
                SelectComboItemByTag(SlipNormalizePatternCombo, s.Slip.NormalizePattern.ToString());
                _currentLockSource = ComputeCurrentSourceIdentity(isLock: true);
                _currentSlipSource = ComputeCurrentSourceIdentity(isLock: false);
                PullPersistedLearnedValues();
                LoadKeyDataPoints(s);
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

                // Slip's sources/mode are loaded ABOVE, alongside Lock's - see the ordering note there.
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

                GForceAccelAutoToggle.IsChecked = s.GForce.AccelMaxMode == GMaxMode.Auto;
                GForceDecelAutoToggle.IsChecked = s.GForce.DecelMaxMode == GMaxMode.Auto;
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
            bool accelAuto = GForceAccelAutoToggle.IsChecked == true;
            bool decelAuto = GForceDecelAutoToggle.IsChecked == true;

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
            MahApps.Metro.Controls.ToggleSwitch pulseEnabled, MahApps.Metro.Controls.NumericUpDown pulseGap, MahApps.Metro.Controls.NumericUpDown pulseMin)
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
            s.Slip.NormalizePattern = ParseEnum(GetSelectedTag(SlipNormalizePatternCombo, s.Slip.NormalizePattern.ToString()), s.Slip.NormalizePattern);
            SaveKeyDataPoints(s);
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

            s.GForce.AccelMaxMode = GForceAccelAutoToggle.IsChecked == true ? GMaxMode.Auto : GMaxMode.Fixed;
            s.GForce.DecelMaxMode = GForceDecelAutoToggle.IsChecked == true ? GMaxMode.Auto : GMaxMode.Fixed;
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
            MahApps.Metro.Controls.ToggleSwitch pulseEnabled, MahApps.Metro.Controls.NumericUpDown pulseGap, MahApps.Metro.Controls.NumericUpDown pulseMin)
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
    
        /// <summary>
        /// Runs the ShakeIt precalibration import and reports the outcome inline. Deliberately reports
        /// BOTH the success and the not-found cases in the same place - a driver whose SimHub lives
        /// somewhere unexpected needs to be told that, not left with a silent button.
        /// </summary>
        private void RunShakeItImport()
        {
            ShakeItConversionOutcome outcome =
                _plugin.ImportShakeItPrecalibration(ShakeItImportOverrideCheckBox.IsChecked == true);

            ShakeItImportResultText.Text = outcome?.Message ?? string.Empty;
            ShakeItImportResultText.Visibility = string.IsNullOrEmpty(ShakeItImportResultText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
}
}

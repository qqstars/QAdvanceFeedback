using System;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The G-Force tab's shake block (docs\architecture.md, "UI: MVVM"; final review ruling R40): the
    /// Integrate Wheel Lock and Slip / Integrate Slip Angle switches, the shared shake settings (mode,
    /// trigger, feeling, frequency, hold), the three per-source scales, their enabled state, and the two
    /// rewrite rules. The page's existing XAML binds to it; the page joins it to its Apply exactly like
    /// the Slip Angle tab - <see cref="Load"/> fills it (never dirty), a driver edit raises
    /// <see cref="Edited"/> (MarkDirty), <see cref="SaveTo"/> writes it back, and
    /// <see cref="SetIntegration"/> takes a hub write from elsewhere (never dirty).
    /// <list type="bullet">
    /// <item>ENABLED STATE: the shared settings run while EITHER integration is on; each scale only while
    /// its own source is integrated (docs\slip-angle-design.md section 10).</item>
    /// <item>A MODE PICK RESTORES THAT MODE'S OWN SCALES AND TRIGGER (owner, 2026-09-07):
    /// <see cref="GForceSettings.DefaultShakeScaleFor"/> into all three scales and
    /// <see cref="GForceSettings.DefaultShakeTriggerFor"/> into the trigger. A load never does.</item>
    /// <item>A FEELING PICK REWRITES THE FREQUENCY to <see cref="GForceSettings.DefaultShakeFrequencyFor"/>
    /// (owner, 2026-09-06) and hides the hold for Blending, which pins its own. A load never does.</item>
    /// </list>
    /// WPF-free; the preview graph is drawn by the page from these values.
    /// </summary>
    public sealed class GForceShakeViewModel : ObservableObject
    {
        private bool _loading;
        private bool _integrateLockSlip = true;
        private bool _integrateAngle = true;
        private ShakeApplyMode _mode = GForceSettings.DefaultShakeApplyMode;
        private ShakeFeeling _feeling = GForceSettings.DefaultShakeFeeling;
        private double? _trigger, _frequency, _sustain, _lockScale, _slipScale, _angleScale;
        private string _modeDescription, _feelingDescription;

        public GForceShakeViewModel()
        {
            Load(new GForceSettings());
        }

        /// <summary>A driver edit (never raised by <see cref="Load"/> / <see cref="SetIntegration"/>).</summary>
        public event Action Edited;

        // ---------------- the two switches ----------------

        public bool IntegrateWheelLockAndSlip
        {
            get => _integrateLockSlip;
            set { if (SetProperty(ref _integrateLockSlip, value)) OnIntegrationChanged(); }
        }

        public bool IntegrateSlipAngle
        {
            get => _integrateAngle;
            set { if (SetProperty(ref _integrateAngle, value)) OnIntegrationChanged(); }
        }

        /// <summary>The shared shake settings (mode, trigger, feeling, frequency, hold) are live.</summary>
        public bool SharedEnabled => _integrateLockSlip || _integrateAngle;

        /// <summary>The Wheel Lock and Wheel Slip scales are live.</summary>
        public bool LockSlipScalesEnabled => _integrateLockSlip;

        /// <summary>The Slip Angle scale is live.</summary>
        public bool AngleScaleEnabled => _integrateAngle;

        // ---------------- mode / feeling ----------------

        public ShakeApplyMode Mode
        {
            get => _mode;
            set
            {
                if (!SetProperty(ref _mode, value)) return;
                if (!_loading)
                {
                    // Deliberately overwrites hand-tuned values (owner, 2026-09-07).
                    double scale = GForceSettings.DefaultShakeScaleFor(value);
                    LockScale = scale;
                    SlipScale = scale;
                    AngleScale = scale;
                    Trigger = GForceSettings.DefaultShakeTriggerFor(value);
                }
                RefreshModeDescription();
                RaiseEdited();
            }
        }

        public ShakeFeeling Feeling
        {
            get => _feeling;
            set
            {
                if (!SetProperty(ref _feeling, value)) return;
                if (!_loading) Frequency = GForceSettings.DefaultShakeFrequencyFor(value);   // owner, 2026-09-06
                RefreshFeelingDescription();
                OnPropertyChanged(nameof(SustainVisible));
                RaiseEdited();
            }
        }

        /// <summary>The hold row is shown for every feeling except Blending (which pins its own hold).</summary>
        public bool SustainVisible => _feeling != ShakeFeeling.Blending;

        public string ModeDescription
        {
            get => _modeDescription;
            private set => SetProperty(ref _modeDescription, value);
        }

        public string FeelingDescription
        {
            get => _feelingDescription;
            private set => SetProperty(ref _feelingDescription, value);
        }

        // ---------------- numbers ----------------

        public double? Trigger { get => _trigger; set { if (SetProperty(ref _trigger, value)) RaiseEdited(); } }
        public double? Frequency { get => _frequency; set { if (SetProperty(ref _frequency, value)) RaiseEdited(); } }
        public double? Sustain { get => _sustain; set { if (SetProperty(ref _sustain, value)) RaiseEdited(); } }
        public double? LockScale { get => _lockScale; set { if (SetProperty(ref _lockScale, value)) RaiseEdited(); } }
        public double? SlipScale { get => _slipScale; set { if (SetProperty(ref _slipScale, value)) RaiseEdited(); } }
        public double? AngleScale { get => _angleScale; set { if (SetProperty(ref _angleScale, value)) RaiseEdited(); } }

        // ---------------- load / save ----------------

        /// <summary>Fill from <paramref name="g"/> - shows the driver's own saved values (no mode/feeling
        /// rewrite) and is never <see cref="Edited"/>.</summary>
        public void Load(GForceSettings g)
        {
            if (g == null) return;
            _loading = true;
            try
            {
                IntegrateWheelLockAndSlip = g.IntegrateWheelLockAndSlip;
                IntegrateSlipAngle = g.IntegrateSlipAngle;
                Mode = g.ShakeApplyMode;
                Trigger = g.ShakeTriggerThresholdPercent;
                Feeling = g.ShakeFeeling;
                Frequency = g.ShakeFrequencyHz;
                Sustain = g.ShakeSustainPercent;
                LockScale = g.WheelLockShakeScale;
                SlipScale = g.WheelSlipShakeScale;
                AngleScale = g.WheelSlipAngleShakeScale;
                RefreshTexts();
            }
            finally { _loading = false; }
        }

        /// <summary>Write the draft into <paramref name="g"/>; an empty number keeps the existing value.</summary>
        public void SaveTo(GForceSettings g)
        {
            if (g == null) return;
            g.IntegrateWheelLockAndSlip = _integrateLockSlip;
            g.IntegrateSlipAngle = _integrateAngle;
            g.ShakeApplyMode = _mode;
            g.ShakeTriggerThresholdPercent = _trigger ?? g.ShakeTriggerThresholdPercent;
            g.ShakeFeeling = _feeling;
            g.ShakeFrequencyHz = _frequency ?? g.ShakeFrequencyHz;
            g.ShakeSustainPercent = _sustain ?? g.ShakeSustainPercent;
            g.WheelLockShakeScale = _lockScale ?? g.WheelLockShakeScale;
            g.WheelSlipShakeScale = _slipScale ?? g.WheelSlipShakeScale;
            g.WheelSlipAngleShakeScale = _angleScale ?? g.WheelSlipAngleShakeScale;
        }

        /// <summary>A hub write from elsewhere (ShakeIt panel / config file) of one integration switch:
        /// shown at once, never <see cref="Edited"/>.</summary>
        public void SetIntegration(bool slipAngle, bool value)
        {
            _loading = true;
            try
            {
                if (slipAngle) IntegrateSlipAngle = value;
                else IntegrateWheelLockAndSlip = value;
            }
            finally { _loading = false; }
        }

        /// <summary>Re-read the localised description lines (culture change).</summary>
        public void RefreshTexts()
        {
            RefreshModeDescription();
            RefreshFeelingDescription();
        }

        private void OnIntegrationChanged()
        {
            OnPropertyChanged(nameof(SharedEnabled));
            OnPropertyChanged(nameof(LockSlipScalesEnabled));
            OnPropertyChanged(nameof(AngleScaleEnabled));
            RaiseEdited();
        }

        private void RefreshModeDescription()
        {
            switch (_mode)
            {
                case ShakeApplyMode.AllChannelsGForce: ModeDescription = Strings.Get("GForce.Shake.Mode.AllGForce.Desc"); break;
                case ShakeApplyMode.AllChannelsLockSlip: ModeDescription = Strings.Get("GForce.Shake.Mode.AllLockSlip.Desc"); break;
                case ShakeApplyMode.HigherOfGForceOrLockSlip: ModeDescription = Strings.Get("GForce.Shake.Mode.HigherOfBoth.Desc"); break;
                default: ModeDescription = Strings.Get("GForce.Shake.Mode.PerChannel.Desc"); break;
            }
        }

        private void RefreshFeelingDescription()
        {
            switch (_feeling)
            {
                case ShakeFeeling.SamePhase: FeelingDescription = Strings.Get("GForce.Shake.Feeling.Same.Desc"); break;
                case ShakeFeeling.Blending: FeelingDescription = Strings.Get("GForce.Shake.Feeling.Blending.Desc"); break;
                default: FeelingDescription = Strings.Get("GForce.Shake.Feeling.Opposite.Desc"); break;
            }
        }

        private void RaiseEdited()
        {
            if (!_loading) Edited?.Invoke();
        }
    }
}

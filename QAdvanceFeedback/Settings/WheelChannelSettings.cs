using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.MotorsExport;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Everything Layers 4/5 need for ONE channel (Wheel Lock or Wheel Slip), independently of the
    /// other - one instance per channel (see <see cref="QAdvanceFeedbackSettings.Lock"/>/
    /// <see cref="QAdvanceFeedbackSettings.Slip"/>), not a single shared instance, exactly per the
    /// brief ("PER CHANNEL (lock and slip independently)").
    /// <para/>
    /// Holds three independent things:
    /// <list type="bullet">
    /// <item>Layer 4's four per-wheel SOURCE fields (only FrontLeft/FrontRight/RearLeft/RearRight are
    /// configurable - All/Front/Rear/Left/Right are always aggregated from them, confirmed in the
    /// brief: "the algorithm does not provide them natively") plus each field's own
    /// <see cref="ScriptType"/> (always <see cref="Settings.ScriptType.Plain"/> under either shipped
    /// default) and <see cref="SourceMode"/> (default <see cref="Settings.SourceMode.ShakeIt"/> - see
    /// that property's own remarks; <see cref="Settings.SourceMode.Manual"/>'s own default is a PLAIN
    /// reference to the matching Layer 3 Raw property - see <see cref="DefaultWheelSources"/>).</item>
    /// <item>Layer 5's curve (<see cref="Projector"/>, a <see cref="ProjectorSettings"/> - ported
    /// wholesale from the sibling project, see that class's remarks).</item>
    /// <item>Layer 5's pulse (<see cref="Pulse"/>, a <see cref="PulseSettings"/>).</item>
    /// </list>
    /// Public settable properties with a parameterless constructor (Newtonsoft convention, matching
    /// every other settings POCO in this plugin family) - <see cref="CreateLockDefaults"/>/
    /// <see cref="CreateSlipDefaults"/> are what actually stamp the two channels' DIFFERENT shipped
    /// numbers (a plain parameterless <c>new WheelChannelSettings()</c> alone cannot, since one class
    /// is reused for both channels and C# field initialisers cannot vary per call site) - the root
    /// <see cref="QAdvanceFeedbackSettings"/> object calls these, never relies on the bare constructor
    /// for its own field defaults.
    /// </summary>
    public sealed class WheelChannelSettings
    {
        public string SourceFrontLeft { get; set; }
        public string SourceFrontRight { get; set; }
        public string SourceRearLeft { get; set; }
        public string SourceRearRight { get; set; }

        public ScriptType ScriptTypeFrontLeft { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeFrontRight { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeRearLeft { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeRearRight { get; set; } = ScriptType.Plain;

        /// <summary>
        /// Manual (this channel's four Source*/ScriptType* fields above, pointed at Layer 3's own Raw
        /// property) vs. ShakeIt (SimHub's own ShakeIt Motors export - see
        /// <see cref="MotorsExportPropertyNames"/>).
        /// <para/>
        /// DEFAULT IS <see cref="Settings.SourceMode.ShakeIt"/> - per the owner's explicit instruction
        /// ("Default, globally: ShakeIt Plugin Output Properties"), NOT Manual. A fresh install (and
        /// "Restore all default settings") therefore ships both channels already pointed at the four
        /// confirmed ShakeIt Motors export names (see <see cref="ApplyMotorsExportDefaults"/>) - the settings
        /// UI always shows the toggle and, when the export is not yet configured, an inline note
        /// explaining how to produce it (see <c>docs\shakeit-export-guide.md</c>); the plugin never
        /// silently reads a missing export as 0 either way (see <see cref="WheelSourceResolver"/>'s
        /// fallback to this channel's own Raw value).
        /// </summary>
        public SourceMode SourceMode { get; set; } = SourceMode.ShakeIt;

        private double _brakeThresholdPercent;
        private double _throttleThresholdPercent;

        /// <summary>Brake-pedal-pressed threshold (0-100) gating this channel - see
        /// <see cref="Core.LegacyThresholds"/>. For the Lock channel this is its own, only pedal gate;
        /// for the Slip channel it is checked FIRST, with priority over
        /// <see cref="ThrottleThresholdPercent"/> (see <see cref="Core.LegacyThresholds"/>'s own
        /// remarks on why). Applies in BOTH <see cref="Settings.SourceMode.Manual"/> and
        /// <see cref="Settings.SourceMode.ShakeIt"/> - it gates Layer 3's own algorithm, upstream of
        /// which Layer 4 source is selected.</summary>
        public double BrakeThresholdPercent
        {
            get => _brakeThresholdPercent;
            set => _brakeThresholdPercent = ClampMath.To0100(value);
        }

        /// <summary>Throttle-pedal-pressed threshold (0-100) - only meaningful for the Slip channel
        /// (Lock never reads it); see <see cref="BrakeThresholdPercent"/>'s remarks on priority.</summary>
        public double ThrottleThresholdPercent
        {
            get => _throttleThresholdPercent;
            set => _throttleThresholdPercent = ClampMath.To0100(value);
        }

        private double _lockSensibility = 50.0;

        /// <summary>
        /// Wheel Lock's own sensitivity (0-100), ONLY meaningful for the Lock channel (Slip never reads
        /// it) - matches SimHub's own <c>WheelsLockContainer.LockSensibility</c> exactly (name, range,
        /// default 50) since it now drives the withheld Layer 3 Lock algorithm directly (see
        /// <c>Core.LegacyThresholds.LockSensibility</c> and docs\lock-and-animation-report.md for why
        /// Lock's own algorithm needed this). Higher values respond sooner (more sensitive) but - a
        /// faithful, if counter-intuitive, characteristic of SimHub's own formula - also cap the
        /// achievable ceiling further below full scale; only 50 (the default) reaches a genuine 100.
        /// </summary>
        public double LockSensibility
        {
            get => _lockSensibility;
            set => _lockSensibility = ClampMath.To0100(value);
        }

        public ProjectorSettings Projector { get; set; } = new ProjectorSettings();

        public PulseSettings Pulse { get; set; } = new PulseSettings();

        // ---- Aggregation (docs\aggregation-report.md) - the owner's physically-motivated, per-channel
        // Max/Min axle blend + Front/Rear weight-transfer scheme, REPLACING the previous generic
        // p-norm/GroupMode aggregation (retired - see Core.Aggregator's own remarks). Every one of the
        // five numbers below is independently driver-configurable, per the owner's explicit
        // instruction ("drivetrain layout varies by car... they want to tune without a rebuild") -
        // exposed in its own labelled section on both the Wheel Lock and Wheel Slip tabs (the slip
        // floor factor spinner only appears on the Slip tab - see SettingsControl's own remarks).
        //
        // BOUNDS: every setter below clamps to >= 0 only (SlipFloorFactor additionally caps at 1) via
        // Core.AggregationWeights' own constructor - see that struct's remarks on why WMax/WMin and
        // WFront/WRear are NOT forced to sum to 1: a driver who types weights that do not sum to 1 is
        // never silently rescaled to "fix" what they typed. The bare field initialisers below are the
        // NEUTRAL 0.5/0.5/0.5/0.5/0.0 fallback (Core.AggregationWeights.Neutral), NOT either channel's
        // real shipped numbers - a single shared class cannot give two different field-initialiser
        // defaults for the same property, so CreateLockDefaults/CreateSlipDefaults (like
        // BrakeThresholdPercent/ThrottleThresholdPercent before them) are what actually stamp the
        // correct per-channel numbers for a FRESH install.
        //
        // A settings file saved BEFORE this feature existed (missing these five JSON keys entirely)
        // does NOT read this neutral fallback for either channel, despite that: QAdvanceFeedbackSettings.
        // Lock/Slip are themselves field-initialised to CreateLockDefaults()/CreateSlipDefaults() (see
        // that class), and Newtonsoft's default object-population behaviour (ObjectCreationHandling.
        // Auto) REUSES an existing non-null property value rather than replacing it with a bare
        // WheelChannelSettings() - so deserialising a Lock/Slip object with these keys absent only
        // overwrites the JSON-present properties, leaving every absent one (these five included) at
        // whatever CreateLockDefaults()/CreateSlipDefaults() already set. The neutral fallback below is
        // therefore only ever actually observed from a genuinely BARE `new WheelChannelSettings()` -
        // e.g. a driver's own code, or the settings UI's per-source-mode scratch objects - never from
        // ConfigStore.Load (verified in ConfigStoreTests).
        private double _aggregationWMax = AggregationWeights.Neutral.WMax;
        private double _aggregationWMin = AggregationWeights.Neutral.WMin;
        private double _aggregationWFront = AggregationWeights.Neutral.WFront;
        private double _aggregationWRear = AggregationWeights.Neutral.WRear;
        private double _slipFloorFactor = AggregationWeights.Neutral.SlipFloorFactor;

        /// <summary>Axle blend (Front/Rear): weight given to the STRONGER of the two wheels on that
        /// axle. Clamped to &gt;= 0 (see this class's own remarks on why it is not forced to sum to 1
        /// with <see cref="AggregationWMin"/>).</summary>
        public double AggregationWMax
        {
            get => _aggregationWMax;
            set => _aggregationWMax = ClampNonNegative(value);
        }

        /// <summary>Axle blend (Front/Rear): weight given to the WEAKER of the two wheels on that axle.
        /// Clamped to &gt;= 0.</summary>
        public double AggregationWMin
        {
            get => _aggregationWMin;
            set => _aggregationWMin = ClampNonNegative(value);
        }

        /// <summary>Side (Left/Right) and car (All) blend: weight given to the FRONT-position value -
        /// under braking the front wheels carry the load, so a high value here makes them dominate the
        /// combined reading. Clamped to &gt;= 0.</summary>
        public double AggregationWFront
        {
            get => _aggregationWFront;
            set => _aggregationWFront = ClampNonNegative(value);
        }

        /// <summary>Side (Left/Right) and car (All) blend: weight given to the REAR-position value -
        /// under power the driven wheels (commonly the rear ones) are what spin, so a higher value here
        /// makes them matter more. Clamped to &gt;= 0.</summary>
        public double AggregationWRear
        {
            get => _aggregationWRear;
            set => _aggregationWRear = ClampNonNegative(value);
        }

        /// <summary>
        /// The floor that stops a single strongly-spinning wheel being diluted away: the combined
        /// reading (All/Front/Rear/Left/Right) can never fall below this fraction of the strongest
        /// wheel that fed it - see <see cref="Core.Aggregator"/>'s own remarks for the exact mechanism
        /// and why it ships enabled (0.4) for Wheel Slip but disabled (0.0) for Wheel Lock. Clamped to
        /// [0,1] - see <see cref="AggregationWeights"/>'s own remarks on why a factor above 1 is not
        /// meaningful for this mechanism.
        /// </summary>
        public double SlipFloorFactor
        {
            get => _slipFloorFactor;
            set => _slipFloorFactor = ClampMath.Clamp(value, 0.0, 1.0);
        }

        private static double ClampNonNegative(double value) => ClampMath.IsFinite(value) && value > 0.0 ? value : 0.0;

        /// <summary>Bundles this channel's five aggregation numbers into the plain
        /// <see cref="Core.AggregationWeights"/> struct <see cref="Core.Aggregator"/> actually consumes -
        /// the single conversion point every caller (Layer 3/4/Diag.Source) uses, so Core never needs a
        /// reference back to this Settings-layer POCO.</summary>
        public AggregationWeights ToAggregationWeights()
            => new AggregationWeights(AggregationWMax, AggregationWMin, AggregationWFront, AggregationWRear, SlipFloorFactor);

        /// <summary>
        /// The Wheel Lock channel's shipped defaults: sources point at Layer 3's own
        /// <c>WheelLock.Raw.*</c> properties (plain references), and the curve is the brief's lock
        /// preset (start 20, end 100, 30-&gt;10, 60-&gt;30, 80-&gt;80). Pulse ships disabled - see
        /// <see cref="PulseSettings.Enabled"/>'s remarks.
        /// </summary>
        public static WheelChannelSettings CreateLockDefaults() => CreateDefaults(isLockChannel: true);

        /// <summary>
        /// The Wheel Slip channel's shipped defaults: sources point at Layer 3's own
        /// <c>WheelSlip.Raw.*</c> properties, and the curve is the brief's slip preset (start 20, end
        /// 100, 30-&gt;8, 45-&gt;25, 75-&gt;75) - earlier and gentler than the lock channel's, exactly
        /// as the sibling project's own traction curve is.
        /// </summary>
        public static WheelChannelSettings CreateSlipDefaults() => CreateDefaults(isLockChannel: false);

        private static WheelChannelSettings CreateDefaults(bool isLockChannel)
        {
            var settings = new WheelChannelSettings();
            // Global shipped default is ShakeIt (see SourceMode's own remarks) - NOT
            // ResetSourcesToDefault (which would force Manual/Raw).
            settings.ApplyMotorsExportDefaults(isLockChannel);
            settings.Projector.ApplyPreset(
                ProjectorPreset.Curve,
                isLockChannel ? ProjectionChannel.Lock : ProjectionChannel.Slip);

            // Owner-requested pedal thresholds (see Core.LegacyThresholds) - Lock only ever reads its
            // own BrakeThresholdPercent; Slip reads both (brake checked first, throttle only if the
            // brake condition is not satisfied - see LegacyThresholds' own remarks). These equal
            // LegacyThresholds.Defaults exactly, duplicated here (rather than read from there) so this
            // class stays a plain settings POCO with no dependency direction back onto the algorithm
            // struct beyond the plain doubles it already needs.
            if (isLockChannel)
            {
                settings.BrakeThresholdPercent = 20.0;
                settings.ThrottleThresholdPercent = 40.0; // unused by Lock, kept at a sane value
            }
            else
            {
                // 100.0 - the owner's explicit, deliberate instruction ("by default, set the break
                // pedal presses as 100%, which means only throttle pedal pressed will trigger
                // wheelSlip"), confirmed after driving it ("feels good, reasonable - you can remain the
                // current WheelSlip"). A previous pass changed this to 20.0, reasoning that SimHub's own
                // decompiled GetRpmSpeedSlipLegacy has no IsLock-conditional branch at all (Lock and Slip
                // get the identical Brake>20 gate) - that finding is real (see
                // docs\raw-match-rootcause-report.md) but does NOT justify overriding the owner's own
                // explicit choice; it was reverted back to 100.0. A driver who wants SimHub's real,
                // brake-responsive Slip can still lower this to 20 (matching Lock) themselves.
                settings.BrakeThresholdPercent = 100.0;
                settings.ThrottleThresholdPercent = 40.0;
            }

            // Owner-tested aggregation defaults (docs\aggregation-report.md) - the two channels'
            // numbers are genuinely different (see AggregationWeights.LockDefaults/SlipDefaults' own
            // remarks), which is exactly why this shared class's own field initialisers above are only
            // the NEUTRAL fallback and this method is what stamps the real, channel-specific numbers.
            AggregationWeights aggregationDefaults = isLockChannel ? AggregationWeights.LockDefaults : AggregationWeights.SlipDefaults;
            settings.AggregationWMax = aggregationDefaults.WMax;
            settings.AggregationWMin = aggregationDefaults.WMin;
            settings.AggregationWFront = aggregationDefaults.WFront;
            settings.AggregationWRear = aggregationDefaults.WRear;
            settings.SlipFloorFactor = aggregationDefaults.SlipFloorFactor;

            return settings;
        }

        /// <summary>Resets all four source fields (and their script types) back to a plain
        /// reference to this channel's Layer 3 Raw property, and switches this channel back to
        /// <see cref="Settings.SourceMode.Manual"/> - the model-level equivalent of the sibling
        /// project's "Reset to default" button (which the settings UI calls).</summary>
        public void ResetSourcesToDefault(bool isLockChannel)
        {
            SourceFrontLeft = DefaultWheelSources.RawPropertyName(isLockChannel, "FrontLeft");
            SourceFrontRight = DefaultWheelSources.RawPropertyName(isLockChannel, "FrontRight");
            SourceRearLeft = DefaultWheelSources.RawPropertyName(isLockChannel, "RearLeft");
            SourceRearRight = DefaultWheelSources.RawPropertyName(isLockChannel, "RearRight");

            ScriptTypeFrontLeft = ScriptType.Plain;
            ScriptTypeFrontRight = ScriptType.Plain;
            ScriptTypeRearLeft = ScriptType.Plain;
            ScriptTypeRearRight = ScriptType.Plain;

            SourceMode = SourceMode.Manual;
        }

        /// <summary>Switches this channel to <see cref="Settings.SourceMode.ShakeIt"/> and points all
        /// four source fields at SimHub's own ShakeIt Motors export names
        /// (<see cref="MotorsExportPropertyNames"/>), forcing <see cref="ScriptType.Plain"/> on all four
        /// (a ShakeIt export is always read as a plain property, never scripted).
        /// <para/>
        /// This does NOT check availability, and never has to: the settings UI's toggle is ALWAYS
        /// visible and switchable regardless of whether SimHub currently reports the four expected
        /// properties (the owner's explicit correction - a previous version of this toggle hid itself
        /// whenever availability could not be confirmed, which is why the owner could not find it at
        /// all) - if the export is not yet configured, the UI shows an inline note explaining the setup
        /// steps instead of hiding anything. Either way, <see cref="WheelSourceResolver"/>'s existing
        /// per-source fallback to this channel's own Raw value keeps the plugin's output safe whenever
        /// a configured source (ShakeIt or otherwise) fails to resolve.</summary>
        public void ApplyMotorsExportDefaults(bool isLockChannel)
        {
            SourceFrontLeft = MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontLeft);
            SourceFrontRight = MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontRight);
            SourceRearLeft = MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearLeft);
            SourceRearRight = MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearRight);

            ScriptTypeFrontLeft = ScriptType.Plain;
            ScriptTypeFrontRight = ScriptType.Plain;
            ScriptTypeRearLeft = ScriptType.Plain;
            ScriptTypeRearRight = ScriptType.Plain;

            SourceMode = SourceMode.ShakeIt;
        }

        /// <summary>
        /// THE PER-SOURCE "Reset to default" button's actual logic - deliberately distinct from the
        /// GLOBAL "Restore all default settings" button (<see cref="QAdvanceFeedbackSettings.RestoreDefaults"/>),
        /// which always restores the shipped ShakeIt default regardless of what was there before.
        /// <para/>
        /// This one instead respects whichever <see cref="SourceMode"/> is CURRENTLY selected and
        /// restores THAT mode's own defaults, without changing the mode itself - the owner's explicit
        /// requirement: a driver who is in ShakeIt mode and has typed over one of the four property
        /// names gets the four ShakeIt names back (still in ShakeIt mode); a driver in Manual mode who
        /// has customised a source gets this channel's own Raw property names back (still in Manual
        /// mode). It never silently switches the driver to a different mode than the one they were
        /// already using.
        /// </summary>
        public void ResetSourcesForCurrentMode(bool isLockChannel)
        {
            if (SourceMode == SourceMode.ShakeIt) ApplyMotorsExportDefaults(isLockChannel);
            else ResetSourcesToDefault(isLockChannel);
        }
    }
}

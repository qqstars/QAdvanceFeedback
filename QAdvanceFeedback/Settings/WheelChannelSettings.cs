using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;

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
    /// default) and <see cref="SourceMode"/> (default <see cref="Settings.SourceMode.Manual"/> - see
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

        // ---- PER-SOURCE ARCHIVES (owner, 2026-09-28: "switching between the sources will NOT derive
        //      the settings from each other"). The four fields ABOVE remain the single ACTIVE
        //      configuration - the only thing the engine reads each frame. These hold the modes that
        //      are not currently selected, and SwitchSourceMode moves values between the two. An
        //      existing config migrates for free: its active fields are already correct and these
        //      simply start empty. See WheelSourceSet for the full reasoning. ----

        public WheelSourceSet RawSources { get; set; }
        public WheelSourceSet ShakeItSources { get; set; }
        public WheelSourceSet ViperSources { get; set; }
        public WheelSourceSet CustomSources { get; set; }

        /// <summary>The archive slot for one mode, created on demand.</summary>
        private WheelSourceSet ArchiveFor(SourceMode mode)
        {
            switch (mode)
            {
                case SourceMode.ShakeIt: return ShakeItSources ?? (ShakeItSources = new WheelSourceSet());
                case SourceMode.Viper: return ViperSources ?? (ViperSources = new WheelSourceSet());
                case SourceMode.Custom: return CustomSources ?? (CustomSources = new WheelSourceSet());
                default: return RawSources ?? (RawSources = new WheelSourceSet());
            }
        }

        /// <summary>
        /// Move this channel to <paramref name="mode"/>, keeping every mode's own configuration intact.
        /// <para/>
        /// Archives whatever is currently active under the CURRENT mode first, then restores
        /// <paramref name="mode"/>'s archive - or, when that mode has never been configured here,
        /// applies its shipped preset. So Raw -&gt; Viper -&gt; Raw returns the driver's own Raw text
        /// rather than a regenerated default, and a first-ever switch to Viper still lands on a working
        /// preset.
        /// <para/>
        /// A switch to <see cref="SourceMode.Custom"/> with no archive yet deliberately KEEPS the
        /// current text instead of resetting: Custom is reached by editing another mode's source, so the
        /// edit in progress is exactly what it should start from.
        /// </summary>
        public void SwitchSourceMode(SourceMode mode, bool isLockChannel)
        {
            if (mode == SourceMode) return;

            ArchiveCurrentSources();

            WheelSourceSet target = ArchiveFor(mode);
            SourceMode = mode;

            if (target.HasContent()) target.ApplyTo(this);
            else if (mode != SourceMode.Custom) ResetSourcesForCurrentMode(isLockChannel);
        }

        /// <summary>Snapshot the active four wheels into the CURRENT mode's archive. Called before every
        /// switch, and by the settings page before it hands a working copy to Apply.</summary>
        public void ArchiveCurrentSources()
        {
            WheelSourceSet current = WheelSourceSet.CaptureFrom(this);
            switch (SourceMode)
            {
                case SourceMode.ShakeIt: ShakeItSources = current; break;
                case SourceMode.Viper: ViperSources = current; break;
                case SourceMode.Custom: CustomSources = current; break;
                default: RawSources = current; break;
            }
        }

        /// <summary>
        /// Manual (this channel's four Source*/ScriptType* fields above, pointed at Layer 3's own Raw
        /// property) vs. ShakeIt (SimHub's own ShakeIt Motors export - see
        /// <see cref="MotorsExportPropertyNames"/>).
        /// <para/>
        /// DEFAULT IS <see cref="Settings.SourceMode.Manual"/> (docs\relative-fallback-and-raw-default-report.md
        /// - FLIPPED from the previous shipped default, <see cref="Settings.SourceMode.ShakeIt"/>).
        /// SUPERSEDES the earlier "Default, globally: ShakeIt Plugin Output Properties" instruction:
        /// the owner's own controlled F1 25 comparison (four matched wet/dry, Raw/ShakeIt logs, then a
        /// fifth wet/dry-in-one-session pair) established that Layer 3's own Raw needs NO SimHub-side
        /// setup at all and reads consistently across both cars/conditions from the very first braking
        /// zone, while ShakeIt requires BOTH the export to be configured (steps 1-6 of
        /// docs\shakeit-export-guide.md) AND its own internal per-car calibration to mature (up to 7000
        /// samples - docs\shakeit-silence-diagnosis-report.md) before its numbers are trustworthy - on a
        /// car it has not yet "seen" enough, ShakeIt's own native scale for the SAME physical event can
        /// differ more than 3x from one car to another (measured: Sauber's own near-limit reading sits
        /// close to Raw's, F1 Generic's sits at roughly a third of it), something Raw does not exhibit.
        /// A fresh install (and "Restore all default settings") therefore now ships both channels
        /// already pointed at this channel's own Layer 3 Raw property (see
        /// <see cref="ResetSourcesToDefault"/>) - a driver who prefers ShakeIt's own export can still
        /// switch to it via the settings UI's toggle (see <see cref="ApplyMotorsExportDefaults"/>), which
        /// remains fully supported and unchanged; the settings UI always shows the toggle and, when the
        /// export is not yet configured, an inline note explaining how to produce it (see
        /// <c>docs\shakeit-export-guide.md</c>); the plugin never silently reads a missing export as 0
        /// either way (see <see cref="WheelSourceResolver"/>'s fallback to this channel's own Raw value).
        /// </summary>
        public SourceMode SourceMode { get; set; } = SourceMode.Manual;

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

        /// <summary>
        /// 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - which severity formula Wheel
        /// Lock's own published output uses, ONLY meaningful for the Lock channel (Slip has no
        /// selector at all - it always uses the Mapping-equivalent, four-range-free formula it always
        /// has). Persisted here (rather than only living on <see cref="Core.Normalized.NormalizedWheelLockSlipEngine"/>,
        /// which has no settings-file awareness of its own) so a driver's choice survives a SimHub
        /// restart; <see cref="QAdvanceFeedback"/>'s own per-frame read of this property (mirroring how
        /// <see cref="LockSensibility"/>/aggregation weights above are re-read every frame rather than
        /// baked in at construction) is what actually feeds
        /// <see cref="Core.Normalized.NormalizedWheelLockSlipEngine.LockNormalizePattern"/>. Defaults to
        /// <see cref="NormalizePattern.Mapping"/>, matching the engine's own field-initialiser default.
        /// </summary>
        public NormalizePattern NormalizePattern { get; set; } = NormalizePattern.Mapping;

        /// <summary>
        /// The driver-facing key data points for this channel - see <see cref="KeyDataPointSettings"/>.
        /// Never null: a save written before v1.0.7.2 simply has no entry, and the shipped default
        /// (Auto on) reproduces exactly the pre-1.0.7.2 behaviour, so there is no migration.
        /// </summary>
        public KeyDataPointSettings KeyDataPoints { get; set; } = new KeyDataPointSettings();

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
        /// <c>WheelSlip.Raw.*</c> properties, and the curve is the slip preset (start 20, end 100,
        /// 30-&gt;10, 60-&gt;35, 80-&gt;70 - Critical/Max-Grip output SOFTENED 75 -&gt; 70 in this build
        /// (owner-confirmed, direct response to "with ShakeIt, WheelSlip shakes much harder than using
        /// Raw" - see <see cref="Core.Projection.ProjectorSettings.ApplyPreset"/>'s own remarks for the
        /// full rationale); earlier still 30-&gt;8/60-&gt;20/80-&gt;75, see
        /// docs\slip-source-consistency-report.md) - the mid-range "Ideal" output is close to Lock's
        /// own, while Critical (80-&gt;70, still below Lock's 80) keeps full spin gentler than full
        /// lock. WheelLock's own Critical output is UNCHANGED (stays 60) - the owner was explicit that
        /// only Slip's max-grip output should move here.
        /// </summary>
        public static WheelChannelSettings CreateSlipDefaults() => CreateDefaults(isLockChannel: false);

        private static WheelChannelSettings CreateDefaults(bool isLockChannel)
        {
            var settings = new WheelChannelSettings();
            // NORMALIZE PATTERN - BOTH CHANNELS SHIP THE THREE-POINT MAPPING (v1.0.8, owner's call after
            // driving 1.0.7.0): Lock's SMax/S90/S75 and Slip's Perfect/Great/Good.
            //
            // Lock has always shipped it, and measures all three points physically - LockAnchorLearner
            // reads the source at 90% and 75% of the corner's own g-limit.
            //
            // SLIP'S TWO LOWER POINTS ARE DERIVED, NOT MEASURED (a fixed percentage of the Perfect point -
            // see KeyDataPointSettings.DeriveLowerAnchors), because Slip has no native 90%/75% grip
            // measurement. Earlier builds treated that as a reason to ship Slip single-point; driving it
            // settled the question the other way. Derived anchors still shape the curve honestly - they
            // are exactly what the settings page shows in the boxes - and the graded feel through the
            // range is worth more than the purity of shipping only measured knots.
            //
            // Either channel can still be switched to Max-Grip/Perfect-Point-Only by hand; this is only
            // what a fresh install feels like before anyone touches the selector.
            settings.NormalizePattern = NormalizePattern.Mapping;
            // Global shipped default is now Manual/Raw (docs\relative-fallback-and-raw-default-report.md
            // - FLIPPED from the previous ShakeIt default; see SourceMode's own remarks for the evidence)
            // - NOT ApplyMotorsExportDefaults (which would force ShakeIt).
            settings.ResetSourcesToDefault(isLockChannel);
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
        /// Points this channel's four wheels at viper4gh's CalcLngWheelSlip plugin - see
        /// <see cref="Core.Viper.ViperPropertyNames"/> for the property names, the sign convention and
        /// the measured limitation of the shipped expression.
        /// <para/>
        /// SETS THE SCRIPT TYPE TO NCalc, which is the half of this that cannot be left to the driver.
        /// Viper's value is signed, so each channel must select and rescale its own half; left on
        /// <see cref="ScriptType.Plain"/> SimHub would look up a property literally named
        /// "min(1.0, max(0, ..." and quietly fall back to Raw forever.
        /// </summary>
        public void ApplyViperDefaults(bool isLockChannel)
        {
            SourceFrontLeft = Core.Viper.ViperPropertyNames.GetScript(isLockChannel, Core.MotorsExport.MotorsExportPropertyNames.FrontLeft);
            SourceFrontRight = Core.Viper.ViperPropertyNames.GetScript(isLockChannel, Core.MotorsExport.MotorsExportPropertyNames.FrontRight);
            SourceRearLeft = Core.Viper.ViperPropertyNames.GetScript(isLockChannel, Core.MotorsExport.MotorsExportPropertyNames.RearLeft);
            SourceRearRight = Core.Viper.ViperPropertyNames.GetScript(isLockChannel, Core.MotorsExport.MotorsExportPropertyNames.RearRight);

            ScriptTypeFrontLeft = ScriptType.NCalc;
            ScriptTypeFrontRight = ScriptType.NCalc;
            ScriptTypeRearLeft = ScriptType.NCalc;
            ScriptTypeRearRight = ScriptType.NCalc;

            SourceMode = SourceMode.Viper;
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
            else if (SourceMode == SourceMode.Viper) ApplyViperDefaults(isLockChannel);
            else ResetSourcesToDefault(isLockChannel);
        }

        /// <summary>
        /// Rebuild a PRESET channel's four sources when the stored text is not what that preset says it
        /// should be - returns whether anything was repaired.
        /// <para/>
        /// SELF-REPAIR FOR A CONFIG CORRUPTED BY v1.1.0's SHADOW BUG (owner-reported, 2026-10-01: "ADD
        /// TO VIPER SUPPORT ALSO WILL NOT UPDATE THE SOURCE AND KEYPOINTS, STILL"). While a Viper
        /// channel was blocked on an unsupported title the settings page filled its source boxes with
        /// RAW's property names to show what was actually being read, and the save path wrote those
        /// boxes straight back - so the file ended up saying "SourceMode = Viper" over four Raw
        /// property names. The save path is fixed, but that only stops it happening again; a file
        /// already in that state stays broken forever, because the page faithfully restores exactly the
        /// text it was given. Re-picking the mode by hand was the only cure.
        /// <para/>
        /// SAFE BECAUSE A PRESET'S SOURCES ARE OURS, NOT THE DRIVER'S. Raw, ShakeIt and Viper each have
        /// one correct set of four strings that this plugin generates; editing them is DEFINED to move
        /// the channel to Custom (see the settings page's MoveToCustomIfPresetEdited). So a preset
        /// channel whose text does not match its own preset cannot be a deliberate configuration - it
        /// can only be damage. Custom is excluded outright: there is no canonical text to compare
        /// against, and that text IS the driver's work.
        /// </summary>
        public bool RepairPresetSourcesIfStale(bool isLockChannel)
        {
            if (SourceMode == SourceMode.Custom) return false;

            var expected = new WheelChannelSettings { SourceMode = SourceMode };
            expected.ResetSourcesForCurrentMode(isLockChannel);

            if (string.Equals(SourceFrontLeft, expected.SourceFrontLeft, System.StringComparison.Ordinal)
                && string.Equals(SourceFrontRight, expected.SourceFrontRight, System.StringComparison.Ordinal)
                && string.Equals(SourceRearLeft, expected.SourceRearLeft, System.StringComparison.Ordinal)
                && string.Equals(SourceRearRight, expected.SourceRearRight, System.StringComparison.Ordinal)
                && ScriptTypeFrontLeft == expected.ScriptTypeFrontLeft
                && ScriptTypeFrontRight == expected.ScriptTypeFrontRight
                && ScriptTypeRearLeft == expected.ScriptTypeRearLeft
                && ScriptTypeRearRight == expected.ScriptTypeRearRight)
                return false;

            SourceFrontLeft = expected.SourceFrontLeft;
            SourceFrontRight = expected.SourceFrontRight;
            SourceRearLeft = expected.SourceRearLeft;
            SourceRearRight = expected.SourceRearRight;
            ScriptTypeFrontLeft = expected.ScriptTypeFrontLeft;
            ScriptTypeFrontRight = expected.ScriptTypeFrontRight;
            ScriptTypeRearLeft = expected.ScriptTypeRearLeft;
            ScriptTypeRearRight = expected.ScriptTypeRearRight;
            return true;
        }
    }
}

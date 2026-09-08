using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;

namespace QAdvanceFeedback.Settings
{
    /// <summary>How the max-G reference used to normalise a G-force axis (acceleration or
    /// deceleration) is obtained.</summary>
    public enum GMaxMode
    {
        /// <summary>Always use the configured Fixed*MaxG value, regardless of anything observed at
        /// runtime.</summary>
        Fixed,

        /// <summary>Learn the maximum actually observed, per game+car (see
        /// <see cref="GForceMaxLearner"/>), with single-frame-spike outlier rejection.</summary>
        Auto
    }

    /// <summary>
    /// Model/algorithm settings for the G-force feedback channels (Core/GForce) - composed into
    /// <see cref="QAdvanceFeedbackSettings.GForce"/>, persisted through <c>ConfigStore</c>, wired into
    /// <c>QAdvanceFeedback.cs</c>'s Init/DataUpdate (AttachDelegate the 8 GForce properties,
    /// <see cref="SetCurrentGameAndCar"/> and Observe*G called once per frame, <see cref="GForceEngine"/>
    /// fed the result of <see cref="EffectiveAccelMaxG"/>/<see cref="EffectiveDecelMaxG"/>), and exposed
    /// on the settings UI's G-Force tab - see docs\wiring-ui-report.md for the wiring task's own writeup.
    /// The AUTO-learned maxima additionally round-trip through <c>RuntimeStore</c> via
    /// <see cref="ExportLearnedMaxima"/>/<see cref="ImportLearnedMaxima"/> so they survive a restart.
    /// <para/>
    /// Maxima bind per game AND per car under AUTO (a Formula car and a road car in the same game
    /// learn different maxima) - see <see cref="GForceMaxLearner"/> for the (gameId, carId) key and
    /// its mandatory single-frame-spike outlier rejection, added specifically because an
    /// outlier-unaware learned reference (or normalising against a fixed constant regardless of
    /// context) is exactly what corrupted the sibling ReliableWheelLockSlip project's signal - a
    /// 19.9g collision spike became "the" reference forever after. FIXED mode ignores learned values
    /// entirely, by construction (see <see cref="EffectiveAccelMaxG"/>/<see cref="EffectiveDecelMaxG"/>).
    /// </summary>
    public sealed class GForceSettings
    {
        private const double MinAllowedMaxG = 0.05;

        private double _fixedAccelMaxG = 0.75;
        private double _fixedDecelMaxG = 1.5;

        /// <summary>
        /// AUTO/FIXED mode for the acceleration axis. Default <see cref="GMaxMode.Auto"/>
        /// (docs\robust-auto-gforce-report.md - CHANGED from the original <see cref="GMaxMode.Fixed"/>
        /// default): with NO evidence at all, AUTO's own effective value IS the FIXED default (see
        /// <see cref="EffectiveAccelMaxG"/>) - so a freshly installed plugin's WORST case under this new
        /// default is bit-for-bit identical to shipping FIXED, and it can only ever improve on that once
        /// real evidence accumulates.
        /// <para/>
        /// CORRECTED: this used to cite a <c>GForceMaxLearner.DefaultMinSamples</c> threshold, which DOES
        /// NOT EXIST - a dangling cref describing a minimum-sample gate that was never implemented. The
        /// real rule is "no evidence at all", and <see cref="RobustBandEstimator.TryEstimate"/> succeeds
        /// from a SINGLE sample. That matters after a gap longer than the estimator's own window: the
        /// first sample back evicts the whole window, so one frame can redefine the maximum. See
        /// <c>GForceWindowGapReportTests</c>, which measures exactly that.
        /// </summary>
        public GMaxMode AccelMaxMode { get; set; } = GMaxMode.Auto;

        /// <summary>AUTO/FIXED mode for the deceleration/braking axis. See
        /// <see cref="AccelMaxMode"/>'s remarks.</summary>
        public GMaxMode DecelMaxMode { get; set; } = GMaxMode.Auto;

        /// <summary>AUTO/FIXED mode for the LATERAL axis (v1.0.8). Everything about it mirrors
        /// <see cref="AccelMaxMode"/> - same learner, same per-(game,car) key, same ramp, same
        /// persistence - and it ships AUTO for the same reason.
        /// <para/>
        /// Before this existed, lateral normalised against a HARD-CODED 1.6 g that no setting could
        /// reach, so a 1 g car only ever saw 62% of the available split and a 3 g car saturated early.</summary>
        public GMaxMode LatMaxMode { get; set; } = GMaxMode.Auto;

        private double _fixedLatMaxG = 1.5;

        /// <summary>
        /// The lateral fallback, used in FIXED mode and as AUTO's own floor before evidence exists.
        /// Default **1.5 g** - between the braking default (1.5) and the acceleration default (0.75),
        /// because sustained cornering-g sits closer to braking-g than to acceleration-g: both are
        /// grip-limited and helped by downforce, whereas acceleration is power-limited. It also replaces
        /// the old hard-coded 1.6 g lateral reference, slightly lower so a typical GT car actually
        /// reaches full split rather than falling just short of it.
        /// </summary>
        public double FixedLatMaxG
        {
            get => _fixedLatMaxG;
            set => _fixedLatMaxG = value > MinAllowedMaxG ? value : MinAllowedMaxG;
        }

        /// <summary>
        /// Default rationale (feel over physical realism, per the brief's own instruction): **1.5g**
        /// for braking (REVISED DOWN from an original 2.0g - docs\gforce-transition-scale-report.md -
        /// a legitimate default change, not a weakened assertion: the owner wants the meter to read as
        /// full sooner). The brief's own example is that even an F1 car's hardest braking should hit
        /// the top of the scale quickly - 1.5g comfortably covers hard-braking road/GT content in
        /// typical sim titles while an F1 car's own genuine braking now saturates the meter promptly
        /// rather than needing to reach all the way to 2.0g first.
        /// </summary>
        public double FixedDecelMaxG
        {
            get => _fixedDecelMaxG;
            set => _fixedDecelMaxG = value > MinAllowedMaxG ? value : MinAllowedMaxG;
        }

        /// <summary>
        /// Default rationale: **0.75g** for acceleration (REVISED DOWN from an original 0.9g -
        /// docs\gforce-transition-scale-report.md, alongside <see cref="FixedDecelMaxG"/>'s own
        /// revision) - deliberately lower than the braking default because sustained acceleration-g is
        /// physically smaller than braking-g for almost all vehicles in typical sim content
        /// (acceleration is power/traction-limited; braking gets the combined benefit of tyre grip plus
        /// aerodynamic downforce at speed). Using the same ceiling for both axes would make acceleration
        /// feel permanently numb by comparison; 0.75g lets a strong (not necessarily record) launch or
        /// mid-corner power-down clearly reach toward the top of the scale, sooner than the original
        /// 0.9g did.
        /// </summary>
        public double FixedAccelMaxG
        {
            get => _fixedAccelMaxG;
            set => _fixedAccelMaxG = value > MinAllowedMaxG ? value : MinAllowedMaxG;
        }

        // ---- OUTPUT SCALES (v1.0.8) - a per-axis attenuator on the FINAL channel output. -------------
        // Deliberately NOT applied inside the friction circle: `combined` stays an honest physics
        // quantity, and these only decide how loudly each component is published. Turning the G-force
        // axes down while leaving the wheel lock/slip shake alone is what makes the plugin behave as a
        // lock/slip NOTIFIER rather than a full motion simulation - the owner's own stated use.

        private double _accelOutputScalePercent = 100.0;
        private double _brakeOutputScalePercent = 100.0;
        private double _lateralOutputScalePercent = 100.0;

        /// <summary>0-100%, clamped. Scales the ACCELERATION chain's own level term. Default 100.</summary>
        public double AccelOutputScalePercent
        {
            get => _accelOutputScalePercent;
            set => _accelOutputScalePercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Scales the BRAKING chain's own level term. Default 100.</summary>
        public double BrakeOutputScalePercent
        {
            get => _brakeOutputScalePercent;
            set => _brakeOutputScalePercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Scales the LATERAL boost term. Default 100.</summary>
        public double LateralOutputScalePercent
        {
            get => _lateralOutputScalePercent;
            set => _lateralOutputScalePercent = ClampMath.To0100(value);
        }

        // ---- LATERAL SPLIT PER CHANNEL (v1.0.8) ------------------------------------------------------
        // How much of the lateral headroom - (combined - rLongitudinal) - each channel puts into its
        // left/right split. Deliberately INVERTED against the longitudinal emphasis: under braking the
        // terminal pad (Bottom Front) is already loudest, so it takes the LEAST lateral (50%), while the
        // far pad (Back Low) is quietest and takes the MOST (100%). That is what makes a trail brake read
        // as the cue travelling BACK and to one side as the corner loads up, instead of everything simply
        // getting louder in place. Acceleration mirrors it around its own terminal pad.

        private double _brakeBottomFrontLatSplitPercent = 50.0;
        private double _brakeBottomRearLatSplitPercent = 75.0;
        private double _brakeBackLowLatSplitPercent = 100.0;
        private double _accelBottomRearLatSplitPercent = 100.0;
        private double _accelBackLowLatSplitPercent = 75.0;
        private double _accelBackTopLatSplitPercent = 50.0;

        /// <summary>0-100%, clamped. Braking chain's TERMINAL pad. Default 50.</summary>
        public double BrakeBottomFrontLatSplitPercent
        {
            get => _brakeBottomFrontLatSplitPercent;
            set => _brakeBottomFrontLatSplitPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Braking chain's MIDDLE pad. Default 75.</summary>
        public double BrakeBottomRearLatSplitPercent
        {
            get => _brakeBottomRearLatSplitPercent;
            set => _brakeBottomRearLatSplitPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Braking chain's FAR pad. Default 100.</summary>
        public double BrakeBackLowLatSplitPercent
        {
            get => _brakeBackLowLatSplitPercent;
            set => _brakeBackLowLatSplitPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Acceleration chain's FAR pad. Default 100.</summary>
        public double AccelBottomRearLatSplitPercent
        {
            get => _accelBottomRearLatSplitPercent;
            set => _accelBottomRearLatSplitPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Acceleration chain's MIDDLE pad. Default 75.</summary>
        public double AccelBackLowLatSplitPercent
        {
            get => _accelBackLowLatSplitPercent;
            set => _accelBackLowLatSplitPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100%, clamped. Acceleration chain's TERMINAL pad. Default 50.</summary>
        public double AccelBackTopLatSplitPercent
        {
            get => _accelBackTopLatSplitPercent;
            set => _accelBackTopLatSplitPercent = ClampMath.To0100(value);
        }

        private double _brakeBottomRearSustainPercent = 50.0;
        private double _brakeBackLowSustainPercent = 25.0;
        private double _accelBottomRearSustainPercent = 25.0;
        private double _accelBackLowSustainPercent = 50.0;

        /// <summary>0-100, clamped. Default 50 - the MIDDLE zone of the braking chain (distance 1 from
        /// the terminal Bottom Front), re-derived from this model's own chain topology (halving per
        /// hop from the terminal zone) - see
        /// <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s remarks for the full derivation
        /// and why the previous flat-50%-everywhere defaults were wrong. 0 reproduces the
        /// pre-this-feature fade-to-nothing behaviour exactly.</summary>
        public double BrakeBottomRearSustainPercent
        {
            get => _brakeBottomRearSustainPercent;
            set => _brakeBottomRearSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100, clamped. Default 25 - the FAR zone of the braking chain (distance 2 from
        /// the terminal Bottom Front - half of <see cref="BrakeBottomRearSustainPercent"/>'s own 50%).
        /// See <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s remarks for the full
        /// derivation.</summary>
        public double BrakeBackLowSustainPercent
        {
            get => _brakeBackLowSustainPercent;
            set => _brakeBackLowSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100, clamped. Default 25 - the FAR zone of the acceleration chain (distance 2
        /// from the terminal Back Top - half of <see cref="AccelBackLowSustainPercent"/>'s own 50%).
        /// See <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s remarks for the full
        /// derivation.</summary>
        public double AccelBottomRearSustainPercent
        {
            get => _accelBottomRearSustainPercent;
            set => _accelBottomRearSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100, clamped. Default 50 - the MIDDLE zone of the acceleration chain (distance 1
        /// from the terminal Back Top), per the brief's explicit "Back Low should keep vibrating (just
        /// less strongly)" requirement - see <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s
        /// remarks for the full derivation.</summary>
        public double AccelBackLowSustainPercent
        {
            get => _accelBackLowSustainPercent;
            set => _accelBackLowSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>The owner's driver-facing lateral direction toggle - Normal (default, unchanged
        /// pre-existing behaviour) or Reversed. See <see cref="GForceEngine.LateralDirection"/>'s
        /// remarks for exactly what each mode means physically.</summary>
        public LateralDirectionMode LateralDirection { get; set; } = LateralDirectionMode.Normal;

        /// <summary>
        /// The owner-requested "Integrate Wheel Lock and Slip" G-force shake (see
        /// <see cref="GForceEngine.ShakeFrequencyHz"/>/<c>Core.GForce.GForceShake</c> for the mechanics).
        /// Default ON (changed from an original OFF - docs\integrate-default-report.md): the owner
        /// decided a fresh install should feel this without hunting for the toggle, rather than treating
        /// it as an opt-in change to the existing G-force feel (contrast
        /// <see cref="Core.Projection.PulseSettings.Enabled"/>, the OTHER "changes the feel" toggle,
        /// which still ships OFF - that decision is untouched by this one). Turning this on by itself is
        /// still behaviourally inert for anyone who has not wired up the Wheel Lock/Wheel Slip channels:
        /// the shake amplitude is <c>gForceValue * (wheelValue/100) * scale</c>, so a wheel value of 0
        /// (the default when nothing publishes a lock/slip signal) always contributes a zero-width band -
        /// see <see cref="GForceEngine.Compute"/>.
        /// <para/>
        /// This is a SETTINGS-layer default only - <see cref="GForceEngine"/>'s own bare-constructor
        /// default (<see cref="GForceEngine.IntegrateWheelLockAndSlip"/>) deliberately stays OFF as a
        /// library-level "inert unless configured" baseline for anyone constructing the engine directly
        /// (every <c>GForceEngineShakeTests</c> "disabled" fixture relies on exactly that). The two never
        /// actually disagree for a real user: <see cref="ApplyTo"/> pushes THIS property onto the engine
        /// at Init and on every settings Apply, so what ships here is what every fresh install experiences.
        /// </summary>
        public bool IntegrateWheelLockAndSlip { get; set; } = true;

        /// <summary>The feeling a fresh install ships with. Named rather than repeated, because
        /// <see cref="_shakeFrequencyHz"/>'s own default is derived from it - see there.</summary>
        public const ShakeFeeling DefaultShakeFeeling = Core.GForce.ShakeFeeling.OppositePhase;

        private double _shakeFrequencyHz = DefaultShakeFrequencyFor(DefaultShakeFeeling);

        /// <summary>Hz, clamped to [<see cref="Core.GForce.GForceShake.MinFrequencyHz"/> (1),
        /// <see cref="Core.GForce.GForceShake.MaxFrequencyHz"/> (20)] in the setter itself - see
        /// <see cref="GForceEngine.ShakeFrequencyHz"/>'s own remarks.
        /// <para/>
        /// **THE SHIPPED DEFAULT IS DERIVED FROM <see cref="DefaultShakeFeeling"/>, NOT WRITTEN OUT**
        /// (owner, 2026-09-06), so a fresh install and the Restore-defaults button both land on the
        /// frequency the shipped feeling would itself select. It was a literal 5.0 while
        /// <see cref="DefaultShakeFrequencyFor"/> gave OppositePhase 10 Hz, which meant a fresh install
        /// opened at 5 Hz showing "Opposite phase" and jumped to 10 the moment the driver touched the
        /// dropdown - the same class of drift the two shake scales had. Today this resolves to **10 Hz**.
        /// <para/>
        /// History: 3 Hz originally, then 10, then briefly 5
        /// (docs\shake-frequency-default-report.md) - the 5 was chosen when the wheel-driven modes still
        /// shook around a centre at roughly half their nominal band and the zero-floor travel made the
        /// same setting read busier. The per-feeling reset supersedes it as the single source of the
        /// number. The 1-20 Hz bounds are UNCHANGED throughout.
        /// <para/>
        /// One Hz means ONE FULL Max-Min-Max travel of a single pad per second, not one left-right-left
        /// pan - see <c>ShakeFrequencyDefinitionTests</c>, which pins that for every mode and blend. NOT the Layer 5 pulse's own separate, UNCHANGED 200 ms
        /// (5 Hz) gap floor (<see cref="Core.Projection.PulseSettings.MinGapMs"/>) on the Wheel
        /// Lock/Slip tabs - this property only ever affects the G-Force "Integrate Wheel Lock and Slip"
        /// shake.</summary>
        public double ShakeFrequencyHz
        {
            get => _shakeFrequencyHz;
            set => _shakeFrequencyHz = ClampMath.Clamp(value, Core.GForce.GForceShake.MinFrequencyHz, Core.GForce.GForceShake.MaxFrequencyHz);
        }

        /// <summary>
        /// How the shake is spread across the eight pads (v1.0.8) - see
        /// <see cref="Core.GForce.ShakeApplyMode"/>. Ships
        /// <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/> so a fresh install feels both cues
        /// rather than only whichever the active chain happens to carry.
        /// <para/>
        /// CHANGING THIS DOES NOT TOUCH THE TWO SHAKE SCALES (owner's decision, 2026-09-06) - see
        /// <see cref="WheelLockShakeScale"/>.
        /// </summary>
        public ShakeApplyMode ShakeApplyMode { get; set; } = DefaultShakeApplyMode;

        /// <summary>The mode a fresh install ships with, and the row the shipped scale and trigger
        /// defaults are read from. Named rather than repeated so those three can never drift apart -
        /// which they did once before, see <see cref="WheelLockShakeScale"/>.</summary>
        public const ShakeApplyMode DefaultShakeApplyMode = Core.GForce.ShakeApplyMode.HigherOfGForceOrLockSlip;

        /// <summary>
        /// THE LOCK/SLIP SCALE EACH MODE STARTS FROM (owner's own numbers, 2026-09-07, after seat time):
        /// 1.3 for <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/>, 1.5 for
        /// <see cref="ShakeApplyMode.PerChannel"/>, 1.3 for <see cref="ShakeApplyMode.AllChannelsGForce"/>,
        /// 1.0 for <see cref="ShakeApplyMode.AllChannelsLockSlip"/>.
        /// <para/>
        /// NOTE THE REVERSAL. On 2026-09-06 the owner had this reset REMOVED ("changing the mode will not
        /// impact the scale"), on the grounds that the scale's definition is identical in every mode. It
        /// is back on 2026-09-07 because seat time produced a different number for each mode, and a table
        /// of per-mode values is only reachable if switching the mode applies it. Switching therefore
        /// overwrites a hand-tuned scale again - deliberately.
        /// </summary>
        public static double DefaultShakeScaleFor(ShakeApplyMode mode)
        {
            switch (mode)
            {
                case Core.GForce.ShakeApplyMode.HigherOfGForceOrLockSlip: return 1.2;   // 1.3 -> 1.2, v1.0.9
                case Core.GForce.ShakeApplyMode.PerChannel: return 1.5;
                case Core.GForce.ShakeApplyMode.AllChannelsLockSlip: return 1.0;
                default: return 1.3;   // AllChannelsGForce
            }
        }

        /// <summary>
        /// THE "START SHAKING ABOVE" EACH MODE STARTS FROM. Revised in v1.0.9 after seat time:
        /// <list type="bullet">
        /// <item><see cref="ShakeApplyMode.PerChannel"/> - <b>5</b>, unchanged.</item>
        /// <item><see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/> - <b>60</b> (was 5).</item>
        /// <item><see cref="ShakeApplyMode.AllChannelsGForce"/> - <b>60</b> (was 30).</item>
        /// <item><see cref="ShakeApplyMode.AllChannelsLockSlip"/> - <b>30</b>, unchanged.</item>
        /// </list>
        /// The original split reasoned only about how BROADLY a mode spreads the band: a per-channel band
        /// adds width to an animation that was already there, so a trace of lock can be admitted early,
        /// while an all-channels band is a floor under all eight pads and reads as a permanent buzz.
        /// <para/>
        /// WHAT SEAT TIME ADDED: the combined mode raises each channel's CEILING by the wheel value, so a
        /// low threshold there lets ordinary background lock/slip lift every ceiling continuously - the
        /// same permanent-buzz failure, arriving by a different route. 60 keeps it silent until the wheel
        /// is genuinely working, which is also where its raised ceiling starts to say something.
        /// </summary>
        public static double DefaultShakeTriggerFor(ShakeApplyMode mode)
        {
            switch (mode)
            {
                case Core.GForce.ShakeApplyMode.PerChannel:
                    return 5.0;
                case Core.GForce.ShakeApplyMode.HigherOfGForceOrLockSlip:
                case Core.GForce.ShakeApplyMode.AllChannelsGForce:
                    return 60.0;   // RAISED from 5 / 30 in v1.0.9 - see the remarks above.
                default:
                    return 30.0;   // AllChannelsLockSlip
            }
        }

        /// <summary>
        /// How the two pads of a pair relate while shaking (v1.0.8) - see
        /// <see cref="Core.GForce.ShakeFeeling"/>. Replaces the old "Both-sides blend (%)" spinner:
        /// only three points on that slider were distinct to feel, and the hold setting silently
        /// cancelled itself towards the middle of it.
        /// <para/>
        /// Picking a feeling from the dropdown OVERWRITES <see cref="ShakeFrequencyHz"/> with
        /// <see cref="DefaultShakeFrequencyFor"/> - owner's explicit instruction, 2026-09-06: "set the
        /// frequency as 10HZ (Even the user override to their own frequency value); if enabled
        /// 'Blending' ... set the Shake Frequency as 5HZ instead". A hand-tuned value is deliberately
        /// discarded, because Blending's fixed 50% hold makes the same rate read considerably busier
        /// than the two phase-locked feelings do.
        /// <para/>
        /// This is NOT in tension with the shipped 5 Hz default (<see cref="ShakeFrequencyHz"/>): a
        /// fresh install has never touched the dropdown, so it keeps 5 Hz until the driver picks a
        /// feeling. Contrast <see cref="ShakeApplyMode"/>, which does NOT rewrite the scales.
        /// </summary>
        public ShakeFeeling ShakeFeeling { get; set; } = DefaultShakeFeeling;

        /// <summary>The shake frequency each feeling is SET TO when the driver picks it from the dropdown
        /// - **5 Hz for <see cref="ShakeFeeling.OppositePhase"/>, 10 Hz for the other two** (owner,
        /// 2026-09-06, after seat time; the first cut had the split the other way round). Because
        /// OppositePhase is also <see cref="DefaultShakeFeeling"/>, this is what a fresh install and
        /// Restore-defaults both land on, so the shipped frequency is 5 Hz again.
        /// <para/>
        /// The HOLD is not defaulted per feeling in the same way: the two phase-locked feelings share the
        /// one configured value, and <see cref="ShakeFeeling.Blending"/> does not take a default at all -
        /// it PINS its hold at <see cref="Core.GForce.GForceShake.BlendingHoldFraction"/> (50%, the
        /// owner's "equivalent to set 'Hold on min/max' as 50%") and the UI hides the control, so there
        /// is no per-feeling hold value for the driver to be handed.</summary>
        public static double DefaultShakeFrequencyFor(ShakeFeeling feeling)
            => feeling == Core.GForce.ShakeFeeling.OppositePhase ? 5.0 : 10.0;

        private double _shakeSustainPercent = 30.0;

        /// <summary>
        /// PERCENT (0-90) of the CYCLE the shake spends held at its extremes - the driver-facing form of
        /// <see cref="GForceEngine.ShakeSustainFraction"/>, converted in <see cref="ApplyTo"/>.
        /// Default **30** (owner, 2026-09-06; it was 40 under the old trapezoid) - the same number the
        /// owner's own worked example of the sine+hold shape used.
        /// <para/>
        /// Higher reads as SHARPER, not faster: the period is <see cref="ShakeFrequencyHz"/> alone and
        /// does not move with this. See <see cref="Core.GForce.GForceShake.SineHoldWave"/> for the shape,
        /// including why 0 is exactly a cosine. IGNORED by
        /// <see cref="Core.GForce.ShakeFeeling.Blending"/>, which pins its own 50%.
        /// </summary>
        public double ShakeSustainPercent
        {
            get => _shakeSustainPercent;
            set => _shakeSustainPercent = ClampMath.Clamp(value, 0.0, Core.GForce.GForceShake.MaxSustainFraction * 100.0);
        }

        // NO ShakeBlendPercent ANY MORE (v1.0.8). The "Both-sides blend (%)" spinner became
        // ShakeFeeling's three named choices; the setting outlived it as write-only state that ApplyTo
        // pushed into an engine property nothing read. Old config files simply carry an ignored key.

        private double _shakeTriggerThresholdPercent = DefaultShakeTriggerFor(DefaultShakeApplyMode);

        /// <summary>
        /// The wheel lock/slip value (0-100, UNSCALED) at or above which a shake may start. Default 5
        /// (v1.0.8, lowered from 20).
        /// <para/>
        /// A SOFT SWITCH FOR "INTEGRATE WHEEL LOCK AND SLIP" (owner's own framing). Below it the wheel
        /// signal is treated as absent, so EVERY mode falls back to exactly what it would publish with
        /// the integration turned off - plain G-force, no shake, on all eight pads. That is true of the
        /// wheel-driven modes too: with no wheel cue there is nothing for them to be louder than.
        /// <para/>
        /// THE SCALES ARE NOT APPLIED TO THIS TEST. It is compared against the raw <c>Max(lock, slip)</c>
        /// as the source reports it, NOT against the scaled contribution that drives the band - so
        /// raising a scale makes the shake stronger without making it start any earlier. A lock of 18
        /// with a 1.5 scale is still below a threshold of 20. See
        /// <see cref="GForceEngine.ShakeTriggerThreshold"/> and <c>GForceEngine.AdvanceShake</c> for the
        /// finish-the-cycle and keep-the-rhythm behaviour built around it.
        /// </summary>
        public double ShakeTriggerThresholdPercent
        {
            get => _shakeTriggerThresholdPercent;
            set => _shakeTriggerThresholdPercent = ClampMath.To0100(value);
        }

        private double _wheelLockShakeScale = DefaultShakeScaleFor(DefaultShakeApplyMode);

        /// <summary>Non-negative, clamped in the setter. Default **1.5** (150%) - RAISED from an
        /// original 1.0 (docs\shake-tuning-report.md), per driver feedback asking for a more obvious
        /// shake by default.
        /// <para/>
        /// **THE SAME NUMBER IN EVERY MODE, AND A MODE SWITCH NEVER REWRITES IT** (owner's decision,
        /// 2026-09-06). A previous revision reset both scales to a per-mode default on the reasoning
        /// that the setting meant a different thing in each mode. It does not:
        /// <c>contribution = scale x wheel/100</c> is computed identically in all four modes, and the
        /// Lock and Slip scales are applied to their own channels individually before the engine takes
        /// the larger. What differs is only what that contribution MULTIPLIES - the pad's own G-force
        /// level in the two G-force modes, the full 0-100 range in the two wheel-driven ones - so the
        /// band saturates at a different wheel value per mode (at 1.5, a wheel-driven mode reaches a
        /// full-width band from wheel 67 up). That is a consequence worth knowing, not a change of
        /// meaning, and it is not grounds for overwriting a hand-tuned value.
        /// <para/>
        /// Displayed in the UI as "1.0 = 100%" so the multiplier reads intuitively;
        /// deliberately NOT re-expressed as a separately-stored percentage field (which would create a
        /// second control scaling the same amplitude term as this one and risk contradicting it) - see
        /// <see cref="GForceEngine.ShakeFrequencyHz"/>'s sibling remarks and the report for the full
        /// reconciliation of the driver's "shaking percentage" request against this pre-existing
        /// setting. Concretely: at a pad level of 100 and a wheel value of 60, the old default produced
        /// a shake band of 60 (out of 100); the new default produces a band of 90 - 50% wider, i.e.
        /// audibly/physically more obvious, exactly as requested.</summary>
        public double WheelLockShakeScale
        {
            get => _wheelLockShakeScale;
            set => _wheelLockShakeScale = value >= 0.0 ? value : 0.0;
        }

        private double _wheelSlipShakeScale = DefaultShakeScaleFor(DefaultShakeApplyMode);

        /// <summary>Non-negative, clamped in the setter. Default **1.5** (150%) - see
        /// <see cref="WheelLockShakeScale"/>'s remarks for the full rationale (identical, mirrored for
        /// the Slip channel).</summary>
        public double WheelSlipShakeScale
        {
            get => _wheelSlipShakeScale;
            set => _wheelSlipShakeScale = value >= 0.0 ? value : 0.0;
        }

        private double _sustainTimeConstantSeconds = 0.15;
        private double _transientTimeConstantSeconds = 0.08;
        // SWEEP SPEED. Default 1.2 -> **1.0** (owner, 2026-09-06, after seat time: a slower, smoother
        // transition reads better). Lower = the sweep travels more gradually across the pads for the
        // same pedal input; higher = it snaps through the three stages sooner.
        private double _transientGain = 1.0;

        /// <summary>Seconds, clamped positive. See <see cref="GForceEngine.SustainTimeConstantSeconds"/>'s
        /// remarks for the default's reasoning.</summary>
        public double SustainTimeConstantSeconds
        {
            get => _sustainTimeConstantSeconds;
            set => _sustainTimeConstantSeconds = value > 1e-3 ? value : 1e-3;
        }

        /// <summary>Seconds, clamped positive. See <see cref="GForceEngine.TransientTimeConstantSeconds"/>'s
        /// remarks for the default's reasoning.</summary>
        public double TransientTimeConstantSeconds
        {
            get => _transientTimeConstantSeconds;
            set => _transientTimeConstantSeconds = value > 1e-3 ? value : 1e-3;
        }

        /// <summary>Clamped non-negative. See <see cref="GForceEngine.TransientGain"/>'s remarks for
        /// the default's reasoning.</summary>
        public double TransientGain
        {
            get => _transientGain;
            set => _transientGain = value >= 0.0 ? value : 0.0;
        }

        // 1.2 -> 0.5 in v1.0.9 (owner, after seat time): re-arm more readily, so a re-application
        // mid-corner is felt rather than missed.
        private double _retriggerStrictness = 0.5;

        /// <summary>How hard a pedal stab has to be to restart the travel animation mid-corner. Default
        /// **1.2**. Clamped to the engine's own bounds in the setter, so a hand-edited config cannot
        /// smuggle in a zero threshold - see <see cref="GForceEngine.RetriggerStrictness"/> for the full
        /// derivation and what higher/lower actually feels like.</summary>
        public double RetriggerStrictness
        {
            get => _retriggerStrictness;
            set => _retriggerStrictness = ClampMath.IsFinite(value)
                ? ClampMath.Clamp(value, GForceEngine.MinRetriggerStrictness, GForceEngine.MaxRetriggerStrictness)
                : 0.5;
        }

        private double _autoTransitionAnimationScale = 1.2;
        private double _fixedTransitionAnimationScale = 1.5;

        /// <summary>
        /// MODE-DEPENDENT TRANSITION SCALING (docs\robust-auto-gforce-report.md - REPLACES the single
        /// <c>TransitionAnimationScale</c> setting this class previously carried): the transition scale
        /// used while the relevant axis is in AUTO mode. Default **1.2** (owner-specified) - a smaller
        /// amplification than <see cref="FixedTransitionAnimationScale"/>'s 1.5, since an AUTO-learned
        /// max is already, by construction, closer to what this car/session genuinely achieves (less
        /// "low-G car needs help reaching a full-feeling transition" headroom to make up than a
        /// one-size-fits-all FIXED default has). Clamped to [0, <see cref="GForceEngine.MaxTransitionAnimationScale"/>].
        /// See <see cref="EffectiveAccelTransitionScale"/>/<see cref="EffectiveDecelTransitionScale"/>
        /// for how this and <see cref="FixedTransitionAnimationScale"/> are combined without a step at
        /// the sample threshold.
        /// </summary>
        public double AutoTransitionAnimationScale
        {
            get => _autoTransitionAnimationScale;
            set => _autoTransitionAnimationScale = ClampMath.Clamp(value, 0.0, GForceEngine.MaxTransitionAnimationScale);
        }

        /// <summary>The transition scale used while the relevant axis is in FIXED mode, and also the
        /// value AUTO mode itself uses whenever its own effective max IS the fixed default (below the
        /// evidence threshold - see <see cref="EffectiveAccelTransitionScale"/>'s own remarks). Default
        /// **1.5** - unchanged from this class's previous single-setting default
        /// (docs\gforce-transition-scale-report.md), so a FIXED-mode driver's feel is completely
        /// unaffected by this change. Clamped to [0, <see cref="GForceEngine.MaxTransitionAnimationScale"/>].</summary>
        public double FixedTransitionAnimationScale
        {
            get => _fixedTransitionAnimationScale;
            set => _fixedTransitionAnimationScale = ClampMath.Clamp(value, 0.0, GForceEngine.MaxTransitionAnimationScale);
        }

        /// <summary>
        /// Applies every model/algorithm setting on this object to <paramref name="engine"/> - the one
        /// place that keeps the settings POCO and the live engine's tunable properties from drifting
        /// apart. Called once at Init and again whenever the settings UI's global Apply button saves.
        /// </summary>
        public void ApplyTo(GForceEngine engine)
        {
            if (engine == null) return;
            engine.BrakeBottomRearSustainFraction = BrakeBottomRearSustainPercent / 100.0;
            engine.BrakeBackLowSustainFraction = BrakeBackLowSustainPercent / 100.0;
            engine.AccelBottomRearSustainFraction = AccelBottomRearSustainPercent / 100.0;
            engine.AccelBackLowSustainFraction = AccelBackLowSustainPercent / 100.0;
            engine.LateralDirection = LateralDirection;
            engine.SustainTimeConstantSeconds = SustainTimeConstantSeconds;
            engine.TransientTimeConstantSeconds = TransientTimeConstantSeconds;
            engine.TransientGain = TransientGain;
            engine.RetriggerStrictness = RetriggerStrictness;
            // NOT a single TransitionAnimationScale push any more (docs\robust-auto-gforce-report.md) -
            // the engine's own per-frame Compute call now always receives the two MODE-DEPENDENT,
            // per-key blended scales explicitly (see EffectiveAccelTransitionScale/
            // EffectiveDecelTransitionScale) from the composition root, so engine.TransitionAnimationScale
            // is left at its own bare-constructor default here, only ever mattering as a defensive
            // fallback for a caller that invokes Compute without either override.
            engine.TransitionAnimationScale = FixedTransitionAnimationScale;
            engine.IntegrateWheelLockAndSlip = IntegrateWheelLockAndSlip;
            engine.ShakeApplyMode = ShakeApplyMode;
            engine.ShakeFeeling = ShakeFeeling;

            engine.AccelOutputScale = AccelOutputScalePercent / 100.0;
            engine.BrakeOutputScale = BrakeOutputScalePercent / 100.0;
            engine.LateralOutputScale = LateralOutputScalePercent / 100.0;

            engine.BrakeBottomFrontLatSplit = BrakeBottomFrontLatSplitPercent / 100.0;
            engine.BrakeBottomRearLatSplit = BrakeBottomRearLatSplitPercent / 100.0;
            engine.BrakeBackLowLatSplit = BrakeBackLowLatSplitPercent / 100.0;
            engine.AccelBottomRearLatSplit = AccelBottomRearLatSplitPercent / 100.0;
            engine.AccelBackLowLatSplit = AccelBackLowLatSplitPercent / 100.0;
            engine.AccelBackTopLatSplit = AccelBackTopLatSplitPercent / 100.0;
            engine.ShakeFrequencyHz = ShakeFrequencyHz;
            // THE ONLY PERCENT->FRACTION CONVERSION for this setting; see ShakeSustainPercent's remarks.
            engine.ShakeSustainFraction = ShakeSustainPercent / 100.0;
            engine.ShakeTriggerThreshold = ShakeTriggerThresholdPercent;
            engine.WheelLockShakeScale = WheelLockShakeScale;
            engine.WheelSlipShakeScale = WheelSlipShakeScale;
        }

        /// <summary>
        /// Acceleration axis's learning-path reject ceiling (docs\gforce-direction-fix-report.md -
        /// derived, not copied from the owner's own rougher 10g/20g proposal). Real-world acceleration
        /// peaks: F1 launch ~1.5-2g, a top-fuel drag-launch (the most extreme acceleration event in
        /// any wheeled motorsport) ~4-5g. 6g leaves comfortable margin above even that extreme while
        /// still decisively excluding a wall-impact-scale (15-20g+) spike - the exact failure mode that
        /// let a captured session's own Diag.GForce.LearnedAccelMaxG reach 179.8. See
        /// <see cref="GForceEngine.LiveMagnitudeClampG"/> for why the LIVE path uses a separate, higher
        /// bound instead of rejecting.
        /// </summary>
        public const double AccelLearnMaxPlausibleG = 6.0;

        /// <summary>
        /// Deceleration axis's learning-path reject ceiling - real-world braking peaks: road car
        /// ~1.0-1.2g, GT3 ~1.5-2.0g, F1 braking ~5-6g (braking is consistently harder than
        /// accelerating: tyre grip is helped by aerodynamic downforce at speed, and there is no
        /// traction-limited driven-axle ceiling the way there is under power). 8g leaves comfortable
        /// margin above even F1's own extreme while still decisively excluding a wall-impact-scale
        /// spike. See <see cref="AccelLearnMaxPlausibleG"/>'s own remarks for the full reasoning this
        /// mirrors.
        /// </summary>
        public const double DecelLearnMaxPlausibleG = 8.0;

        /// <summary>
        /// FLOOR ON EVERY LEARNED MAXIMUM - 0.5 g on all three axes (owner's decision).
        /// <para/>
        /// WHAT IT IS FOR. Applies ONLY to the learned value, never to the Fixed*MaxG a driver typed;
        /// and with no evidence at all the fixed default already governs. So it covers exactly one case:
        /// the learner HAS evidence but it is implausibly low - a session with almost no cornering, or
        /// almost no braking. Without it that learns ~0.1 g and the cue then saturates on the slightest
        /// input.
        /// <para/>
        /// WHY AN ABSOLUTE FLOOR IS THE RIGHT ANSWER, not a defect. It was objected that a floor
        /// OVERESTIMATES genuinely low-grip content - a car on snow pulls ~0.4 g, a truck perhaps 0.2 g,
        /// and normalising those against 0.5 makes flat-out effort read below full scale. The owner's
        /// answer settles it: THAT IS CORRECT. A real truck does not produce a strong G-force transition,
        /// and a seat pad should not pretend otherwise. Below roughly half a g there is no forceful event
        /// to report, so reporting proportionally less is honest rather than broken.
        /// <para/>
        /// This is a deliberate trade against pure per-vehicle normalisation: above the floor the cue
        /// still means "at THIS car's limit", while below it the cue means "not much force here" - which
        /// is what a driver of that vehicle actually experiences.
        /// <para/>
        /// THREE SEPARATE CONSTANTS, equal today. The axes have genuinely different physics (braking and
        /// cornering are grip-limited; acceleration is power-limited and spans ~0.2 g for a laden truck
        /// to ~1.5 g for an F1 launch), so they are kept independently adjustable rather than collapsed
        /// into one shared value that would have to be re-reasoned for all three at once.
        /// <para/>
        /// THE EFFECTIVE FLOOR IS <c>Min(this constant, the driver's own Fixed*MaxG)</c> - see
        /// <see cref="FloorFor"/>. A driver who typed something BELOW 0.5 has said, explicitly, that
        /// values that low are wanted on this axis, so the floor steps out of the way rather than
        /// overriding them. Typing 1.5 leaves the floor at 0.5; typing 0.2 lowers it to 0.2, and a
        /// learner converging on 0.3 is then allowed all the way down to 0.3.
        /// </summary>
        public const double MinLearnedLatMaxG = 0.5;

        /// <summary>See <see cref="MinLearnedLatMaxG"/> - the acceleration axis's own floor.</summary>
        public const double MinLearnedAccelMaxG = 0.5;

        /// <summary>See <see cref="MinLearnedLatMaxG"/> - the deceleration axis's own floor.</summary>
        public const double MinLearnedDecelMaxG = 0.5;

        /// <summary>
        /// The floor actually applied to a learned value: the axis's own constant, but never higher than
        /// what the driver typed for that axis. See <see cref="MinLearnedLatMaxG"/>'s remarks - a typed
        /// value below 0.5 is an explicit statement that low readings are wanted here, and the floor
        /// defers to it instead of overriding it.
        /// </summary>
        private static double FloorFor(double axisFloor, double typedValue) => Math.Min(axisFloor, typedValue);

        /// <summary>
        /// Lateral axis's learning-path reject ceiling. Real cornering peaks: road car ~0.9 g, GT3
        /// ~1.5-2.0 g, F1 ~5-6 g. 8 g mirrors <see cref="DecelLearnMaxPlausibleG"/> - cornering and
        /// braking are both grip-limited and reach similar magnitudes - and still decisively excludes a
        /// wall-impact-scale spike.
        /// </summary>
        public const double LatLearnMaxPlausibleG = 8.0;

        private readonly GForceMaxLearner _accelLearner = new GForceMaxLearner(AccelLearnMaxPlausibleG);
        private readonly GForceMaxLearner _decelLearner = new GForceMaxLearner(DecelLearnMaxPlausibleG);
        private readonly GForceMaxLearner _latLearner = new GForceMaxLearner(LatLearnMaxPlausibleG);
        private readonly TelemetryLearningGate _learningGate = new TelemetryLearningGate();

        // ---- RAMP-IN WHEN AUTO ENGAGES (docs\robust-auto-gforce-report.md, owner's explicit spec) -
        // one MaxRamp per (gameId,carId) key, per axis, so switching cars/games gets its own
        // independent ramp (a brand-new key starts a Dictionary lookup miss -> a fresh MaxRamp -> weight
        // 0 -> effective value exactly the FIXED default, confirmed by MaxRampTests). See MaxRamp's own
        // remarks for the full mechanism.
        private readonly Dictionary<string, MaxRamp> _accelRamps = new Dictionary<string, MaxRamp>(StringComparer.Ordinal);
        private readonly Dictionary<string, MaxRamp> _decelRamps = new Dictionary<string, MaxRamp>(StringComparer.Ordinal);
        private readonly Dictionary<string, MaxRamp> _latRamps = new Dictionary<string, MaxRamp>(StringComparer.Ordinal);

        private static MaxRamp RampFor(Dictionary<string, MaxRamp> ramps, string gameId, string carId)
        {
            string key = GForceMaxLearner.MakeKey(gameId, carId);
            if (!ramps.TryGetValue(key, out MaxRamp ramp))
            {
                ramp = new MaxRamp();
                ramps[key] = ramp;
            }
            return ramp;
        }

        /// <summary>
        /// STEP-SIZE-TRIGGERED RAMP (owner's revised spec, docs\robust-auto-gforce-report.md -
        /// SUPERSEDES an earlier "always ramp on the sample-threshold crossing" design, now that there is
        /// no sample threshold at all): every frame produces a RAW target (the learner's own current best
        /// estimate, or the FIXED default on the one genuine no-evidence case - see
        /// <see cref="EffectiveAccelMaxG"/>'s own remarks). If that target differs from the LAST PUBLISHED
        /// effective value by MORE than <see cref="StepTriggerFraction"/> (25%) of
        /// <c>Max(lastPublished, 1.0)</c>, the change is smoothed over <see cref="RampSeconds"/> (2) of
        /// real time rather than applied immediately; a smaller change is applied immediately (no ramp
        /// state at all). The target is RE-READ every frame during a ramp (so a still-rising detected
        /// value is absorbed naturally, per the owner's own explicit requirement, rather than freezing
        /// whatever it was when the ramp started).
        /// <para/>
        /// ONE INSTANCE PER (gameId,carId) KEY (see <see cref="RampFor"/>): a brand-new key has never
        /// published anything, so its very first call seeds its own "last published" to whatever the
        /// FIXED default is (see <see cref="Effective"/>'s own <c>!_initialized</c> branch) - this is what
        /// makes the owner's own worked ramp-in example (fixed=1.5 -&gt; current 5.5g/6.0g) hold for a
        /// brand-new key, and confirms "the ramp restarts for a new car/game" by construction, not a
        /// special case.
        /// <para/>
        /// SYMMETRIC IN EITHER DIRECTION: the SAME mechanism smooths a big jump AWAY from a value that was
        /// previously trusted just as it smooths one INTO a newly-detected value - e.g. if the live
        /// estimate later swings (a genuine large excursion) or a persisted seed differs greatly from the
        /// very first fresh sample of a new session, both are ramped the same way. There is no separate
        /// "ramping down" case to special-case, unlike the sample-threshold-triggered design this
        /// replaces.
        /// </summary>
        private sealed class MaxRamp
        {
            private const double RampSeconds = 2.0;

            /// <summary>25% (owner's own figure) - a change smaller than this fraction of
            /// <c>Max(lastPublished, 1.0)</c> is applied immediately; a larger one is ramped. The
            /// <c>Max(..., 1.0)</c> floor keeps the trigger meaningful even when the last published value
            /// is very small (otherwise a tiny reference would make even a modest ABSOLUTE change look
            /// like a huge relative jump).</summary>
            private const double StepTriggerFraction = 0.25;

            private bool _initialized;
            private double _lastPublished;
            private DateTime? _rampStartUtc;
            private double _rampStartValue;

            /// <summary>Computes this frame's published effective value from <paramref name="rawTarget"/>
            /// (the learner's own current best estimate, or the fixed default with no evidence at all).
            /// <paramref name="fixedDefault"/> seeds the very first call's own "last published" baseline
            /// only - it plays no role afterward.</summary>
            public double Effective(double rawTarget, double fixedDefault, DateTime nowUtc)
            {
                if (!_initialized)
                {
                    _lastPublished = fixedDefault;
                    _initialized = true;
                }

                double effective;
                if (_rampStartUtc.HasValue)
                {
                    // ALREADY ramping - continue by ELAPSED TIME alone, regardless of how close the
                    // residual gap to rawTarget has narrowed. Re-checking the big-jump threshold against
                    // _lastPublished on every frame (as an earlier revision of this method did) would let
                    // a CONVERGING ramp "snap" the instant its own remaining gap dips under the trigger
                    // threshold - exactly the discontinuity this mechanism exists to prevent, and the
                    // opposite of the owner's own "continuous, no step anywhere" requirement.
                    double elapsedSeconds = Math.Max(0.0, (nowUtc - _rampStartUtc.Value).TotalSeconds);
                    double progress = ClampMath.To01(elapsedSeconds / RampSeconds);
                    effective = _rampStartValue + progress * (rawTarget - _rampStartValue);
                    if (progress >= 1.0) _rampStartUtc = null;
                }
                else
                {
                    double changeThreshold = StepTriggerFraction * Math.Max(_lastPublished, 1.0);
                    bool bigJump = Math.Abs(rawTarget - _lastPublished) > changeThreshold;
                    if (!bigJump)
                    {
                        effective = rawTarget;
                    }
                    else
                    {
                        _rampStartUtc = nowUtc;
                        _rampStartValue = _lastPublished;
                        effective = _rampStartValue; // progress = 0 at the exact instant the ramp starts
                    }
                }

                _lastPublished = effective;
                return effective;
            }
        }

        private string _currentGameId = string.Empty;
        private string _currentCarId = string.Empty;

        /// <summary>
        /// Owner-requested learning validity gate (docs\gforce-direction-fix-report.md): the caller
        /// (<c>QAdvanceFeedback.cs</c>) must check this ONCE per frame, BEFORE calling
        /// <see cref="ObserveAccelG"/>/<see cref="ObserveDecelG"/>, so a menu/loading screen, a pit
        /// stop, a session restart, a paused/alt-tabbed game, or a teleport-sized speed discontinuity
        /// cannot be folded into the AUTO-mode learned maxima - see
        /// <see cref="Core.TelemetryLearningGate"/>'s own remarks for the full reasoning and the exact
        /// evidence (a captured session's own Diag.GForce.LearnedAccelMaxG reaching 179.8). Stateful -
        /// call exactly once per frame (see that class's own remarks); <see cref="ResetLearning"/>
        /// clears it alongside both magnitude learners.
        /// </summary>
        public bool IsFrameValidForLearning(ITelemetrySample sample) => _learningGate.IsValid(sample);

        /// <summary>
        /// Records which game/car is currently active, so the no-arg <see cref="CurrentLearnedAccelMaxG"/>/
        /// <see cref="CurrentLearnedDecelMaxG"/> properties below reflect the right (gameId, carId)
        /// without every caller having to thread both strings through. Intended to be called once per
        /// frame by the plugin integration (not yet wired - see this class's own remarks).
        /// </summary>
        public void SetCurrentGameAndCar(string gameId, string carId)
        {
            _currentGameId = gameId ?? string.Empty;
            _currentCarId = carId ?? string.Empty;
        }

        /// <summary>Feeds one frame's acceleration-G magnitude (non-negative) into the AUTO learner
        /// for this (gameId, carId), at <paramref name="timestampUtc"/> (defaults to
        /// <see cref="DateTime.UtcNow"/> when null - every pre-existing 3-arg call/test keeps
        /// compiling and behaving equivalently). Safe to call even when <see cref="AccelMaxMode"/> is
        /// FIXED - the learner keeps learning in the background so switching to AUTO later has data to
        /// use; FIXED mode simply never reads it back (see <see cref="EffectiveAccelMaxG"/>).</summary>
        public void ObserveAccelG(string gameId, string carId, double magnitude, DateTime? timestampUtc = null)
            => _accelLearner.Observe(gameId, carId, magnitude, timestampUtc ?? DateTime.UtcNow);

        /// <summary>Feeds one frame's deceleration/braking-G magnitude (non-negative) into the AUTO
        /// learner for this (gameId, carId). See <see cref="ObserveAccelG"/>'s remarks.</summary>
        public void ObserveDecelG(string gameId, string carId, double magnitude, DateTime? timestampUtc = null)
            => _decelLearner.Observe(gameId, carId, magnitude, timestampUtc ?? DateTime.UtcNow);

        /// <summary>The learned acceleration max for a specific (gameId, carId) - present mainly for
        /// direct testability of the per-game/per-car keying; see <see cref="CurrentLearnedAccelMaxG"/>
        /// for the UI-facing no-arg equivalent.</summary>
        /// <summary>
        /// The lateral equivalent of <see cref="ObserveAccelG"/>. Fed the ABSOLUTE lateral magnitude:
        /// cornering direction is irrelevant to how much grip the car has, and - UNLIKE the longitudinal
        /// pair - there is no accelerating/braking distinction to make either. Lateral is one axis with
        /// one maximum, so whichever reading is highest wins regardless of what the car was doing
        /// longitudinally at the time.
        /// <para/>
        /// UNGATED BY MAGNITUDE, deliberately. A minimum-magnitude threshold was tried and removed: a
        /// sweep of it on a real log was monotonic with no knee (the estimate climbs all the way as the
        /// threshold rises, because trimming the bottom always slides the estimator's pool window up), so
        /// it was a feel knob with no derivable correct value rather than a noise filter. The floor
        /// (<see cref="MinLearnedLatMaxG"/>) handles the case that actually mattered, and is a far more
        /// predictable thing to reason about. <see cref="GForceMaxLearner.Observe"/> still rejects NaN
        /// and non-positive magnitudes on its own.
        /// </summary>
        public void ObserveLatG(string gameId, string carId, double magnitude, DateTime? timestampUtc = null)
            => _latLearner.Observe(gameId, carId, magnitude, timestampUtc ?? DateTime.UtcNow);

        /// <summary>The lateral equivalent of <see cref="GetLearnedAccelMaxG"/>.</summary>
        public double GetLearnedLatMaxG(string gameId, string carId) => _latLearner.GetLearnedMax(gameId, carId);

        /// <summary>The lateral equivalent of <see cref="CurrentLearnedAccelMaxG"/>.</summary>
        public double CurrentLearnedLatMaxG => _latLearner.GetLearnedMax(_currentGameId, _currentCarId);

        /// <summary>The lateral equivalent of <see cref="TryGetCurrentAccelAutoDetected"/>.</summary>
        public bool TryGetCurrentLatAutoDetected(out double detectedG)
        {
            detectedG = _latLearner.GetLearnedMax(_currentGameId, _currentCarId);
            if (detectedG > MinAllowedMaxG) return true;
            detectedG = 0.0;
            return false;
        }

        /// <summary>The lateral equivalent of <see cref="EffectiveAccelMaxG"/>.</summary>
        public double EffectiveLatMaxG(string gameId, string carId, DateTime? timestampUtc = null)
        {
            if (LatMaxMode == GMaxMode.Fixed) return FixedLatMaxG;
            double learned = _latLearner.GetLearnedMax(gameId, carId);
            // The floor applies to the LEARNED value only - see MinLearnedLatMaxG's own remarks for why
            // the fixed value is left exactly as the driver typed it.
            double rawTarget = learned > MinAllowedMaxG
                ? Math.Max(learned, FloorFor(MinLearnedLatMaxG, FixedLatMaxG))
                : FixedLatMaxG;
            return RampFor(_latRamps, gameId, carId).Effective(rawTarget, FixedLatMaxG, timestampUtc ?? DateTime.UtcNow);
        }

        public double GetLearnedAccelMaxG(string gameId, string carId) => _accelLearner.GetLearnedMax(gameId, carId);

        /// <summary>The learned deceleration max for a specific (gameId, carId). See
        /// <see cref="GetLearnedAccelMaxG"/>'s remarks.</summary>
        public double GetLearnedDecelMaxG(string gameId, string carId) => _decelLearner.GetLearnedMax(gameId, carId);

        /// <summary>
        /// Read-only - what the (not-yet-built) settings UI shows in its learned-acceleration-value
        /// textbox for whichever game/car <see cref="SetCurrentGameAndCar"/> was last told is active.
        /// 0.0 before anything has been confirmed for that key.
        /// </summary>
        public double CurrentLearnedAccelMaxG => _accelLearner.GetLearnedMax(_currentGameId, _currentCarId);

        /// <summary>Read-only - the deceleration equivalent of <see cref="CurrentLearnedAccelMaxG"/>.</summary>
        public double CurrentLearnedDecelMaxG => _decelLearner.GetLearnedMax(_currentGameId, _currentCarId);

        /// <summary>UI-facing no-arg equivalent of <see cref="TryGetAccelAutoDetected"/>, for whichever
        /// game/car <see cref="SetCurrentGameAndCar"/> was last told is active.</summary>
        public bool TryGetCurrentAccelAutoDetected(out double detectedG) => TryGetAccelAutoDetected(_currentGameId, _currentCarId, out detectedG);

        /// <summary>UI-facing no-arg equivalent of <see cref="TryGetDecelAutoDetected"/>.</summary>
        public bool TryGetCurrentDecelAutoDetected(out double detectedG) => TryGetDecelAutoDetected(_currentGameId, _currentCarId, out detectedG);

        private readonly Dictionary<string, MaxRamp> _accelScaleRamps = new Dictionary<string, MaxRamp>(StringComparer.Ordinal);
        private readonly Dictionary<string, MaxRamp> _decelScaleRamps = new Dictionary<string, MaxRamp>(StringComparer.Ordinal);

        /// <summary>
        /// The max-G value <see cref="GForceEngine.Compute"/> should actually normalise acceleration
        /// against for this (gameId, carId) this frame. FIXED always returns
        /// <see cref="FixedAccelMaxG"/>, ignoring anything learned (by construction - this branch never
        /// reads the learner). AUTO returns the FIXED default ONLY when there is truly NO evidence at all
        /// (<see cref="GetLearnedAccelMaxG"/> returns exactly 0.0 - no live sample ever observed AND no
        /// persisted seed) - otherwise the learner's own current best estimate, RAMPED via
        /// <see cref="MaxRamp"/> whenever it differs from the last published value by more than the
        /// step-trigger fraction (25%), rather than stepping. So AUTO's WORST case (truly zero evidence)
        /// is bit-for-bit identical to FIXED, and it can only ever improve on that once real evidence
        /// exists - there is no path here where AUTO returns something worse than FIXED.
        /// </summary>
        public double EffectiveAccelMaxG(string gameId, string carId, DateTime? timestampUtc = null)
        {
            if (AccelMaxMode == GMaxMode.Fixed) return FixedAccelMaxG;
            double learned = _accelLearner.GetLearnedMax(gameId, carId);
            double rawTarget = learned > MinAllowedMaxG
                ? Math.Max(learned, FloorFor(MinLearnedAccelMaxG, FixedAccelMaxG))
                : FixedAccelMaxG;
            return RampFor(_accelRamps, gameId, carId).Effective(rawTarget, FixedAccelMaxG, timestampUtc ?? DateTime.UtcNow);
        }

        /// <summary>The deceleration equivalent of <see cref="EffectiveAccelMaxG"/>.</summary>
        public double EffectiveDecelMaxG(string gameId, string carId, DateTime? timestampUtc = null)
        {
            if (DecelMaxMode == GMaxMode.Fixed) return FixedDecelMaxG;
            double learned = _decelLearner.GetLearnedMax(gameId, carId);
            double rawTarget = learned > MinAllowedMaxG
                ? Math.Max(learned, FloorFor(MinLearnedDecelMaxG, FixedDecelMaxG))
                : FixedDecelMaxG;
            return RampFor(_decelRamps, gameId, carId).Effective(rawTarget, FixedDecelMaxG, timestampUtc ?? DateTime.UtcNow);
        }

        /// <summary>
        /// The transition-animation scale <see cref="GForceEngine.Compute"/> should use for the
        /// ACCELERATION chain this frame. The raw target is <see cref="FixedTransitionAnimationScale"/>
        /// with truly no evidence at all, else <see cref="AutoTransitionAnimationScale"/> - put through
        /// the SAME <see cref="MaxRamp"/> mechanism (its own independent instance, keyed the same way) as
        /// the max value itself, so a jump between the two scale constants is smoothed exactly like any
        /// other big jump (or, since 1.2 vs 1.5 is only a 20% relative change - under the 25% trigger -
        /// applied immediately, by the SAME rule the owner specified; either way, never a raw, unrelated
        /// step). FIXED mode simply returns <see cref="FixedTransitionAnimationScale"/> outright.
        /// </summary>
        public double EffectiveAccelTransitionScale(string gameId, string carId, DateTime? timestampUtc = null)
        {
            if (AccelMaxMode == GMaxMode.Fixed) return FixedTransitionAnimationScale;
            double learned = _accelLearner.GetLearnedMax(gameId, carId);
            double rawTarget = learned > MinAllowedMaxG ? AutoTransitionAnimationScale : FixedTransitionAnimationScale;
            return RampFor(_accelScaleRamps, gameId, carId).Effective(rawTarget, FixedTransitionAnimationScale, timestampUtc ?? DateTime.UtcNow);
        }

        /// <summary>The deceleration equivalent of <see cref="EffectiveAccelTransitionScale"/>.</summary>
        public double EffectiveDecelTransitionScale(string gameId, string carId, DateTime? timestampUtc = null)
        {
            if (DecelMaxMode == GMaxMode.Fixed) return FixedTransitionAnimationScale;
            double learned = _decelLearner.GetLearnedMax(gameId, carId);
            double rawTarget = learned > MinAllowedMaxG ? AutoTransitionAnimationScale : FixedTransitionAnimationScale;
            return RampFor(_decelScaleRamps, gameId, carId).Effective(rawTarget, FixedTransitionAnimationScale, timestampUtc ?? DateTime.UtcNow);
        }

        /// <summary>
        /// UI READOUT (docs\robust-auto-gforce-report.md): the RAW auto-detected acceleration value for
        /// this (gameId, carId), independent of the ramp - the settings UI shows this directly ("Auto
        /// detected: 2.3G") rather than the ramped/blended value actually fed to the engine, since what
        /// the driver wants to see is "what has AUTO learned", not an internal smoothing detail. False
        /// (with <paramref name="detectedG"/> 0.0) means AUTO has NO evidence at all yet for this key -
        /// the UI shows "still using default" in that case (see <see cref="SettingsControl"/>'s own
        /// readout wiring).
        /// </summary>
        public bool TryGetAccelAutoDetected(string gameId, string carId, out double detectedG)
        {
            detectedG = _accelLearner.GetLearnedMax(gameId, carId);
            return detectedG > MinAllowedMaxG;
        }

        /// <summary>The deceleration equivalent of <see cref="TryGetAccelAutoDetected"/>.</summary>
        public bool TryGetDecelAutoDetected(string gameId, string carId, out double detectedG)
        {
            detectedG = _decelLearner.GetLearnedMax(gameId, carId);
            return detectedG > MinAllowedMaxG;
        }

        /// <summary>Clears all learned state for both axes - for a full session reset (analogous to
        /// SimHubTelemetryAdapter.Reset), not called automatically by this class. Also clears the
        /// learning validity gate's own remembered last-good-speed baseline (see
        /// <see cref="IsFrameValidForLearning"/>) so a fresh game/session's first frame is not rejected
        /// as a "discontinuity" against whatever the previous game/car was doing.</summary>
        public void ResetLearning()
        {
            _accelLearner.Reset();
            _decelLearner.Reset();
            _learningGate.Reset();
            // Ramp state restarts too (docs\robust-auto-gforce-report.md's own "does the ramp restart on
            // a session restart" question) - a full forget must not leave a stale ramp mid-flight for a
            // key whose underlying learner just got wiped.
            _accelRamps.Clear();
            _decelRamps.Clear();
            _accelScaleRamps.Clear();
            _decelScaleRamps.Clear();
        }

        /// <summary>Snapshots both learners' confirmed maxima for <c>RuntimeStore</c> to persist to
        /// <c>plugin.QAdvanceFeedback.runtime.json</c> - the wiring task's addition so AUTO mode's
        /// learning survives a SimHub restart, matching how the Lock/Slip <see cref="Core.Normalized.GripLearner"/>
        /// states already do.</summary>
        public void ExportLearnedMaxima(out Dictionary<string, double> accel, out Dictionary<string, double> decel)
            => ExportLearnedMaxima(out accel, out decel, out _);

        /// <summary>Three-axis overload (v1.0.8). The two-argument version is kept so a caller that does
        /// not persist lateral still compiles and behaves exactly as before.</summary>
        public void ExportLearnedMaxima(out Dictionary<string, double> accel, out Dictionary<string, double> decel,
            out Dictionary<string, double> lateral)
        {
            accel = _accelLearner.ExportLearnedMaxima();
            decel = _decelLearner.ExportLearnedMaxima();
            lateral = _latLearner.ExportLearnedMaxima();
        }

        /// <summary>Restores both learners from a previously persisted snapshot - called once at
        /// Init. See <see cref="GForceMaxLearner.ImportLearnedMaxima"/>'s remarks.</summary>
        public void ImportLearnedMaxima(Dictionary<string, double> accel, Dictionary<string, double> decel)
            => ImportLearnedMaxima(accel, decel, null);

        /// <summary>Three-axis overload (v1.0.8) - see <see cref="ExportLearnedMaxima(out Dictionary{string, double},
        /// out Dictionary{string, double}, out Dictionary{string, double})"/>. A null lateral snapshot is
        /// exactly what a pre-1.0.8 runtime file yields, and simply leaves that learner cold.</summary>
        public void ImportLearnedMaxima(Dictionary<string, double> accel, Dictionary<string, double> decel,
            Dictionary<string, double> lateral)
        {
            _accelLearner.ImportLearnedMaxima(accel);
            _decelLearner.ImportLearnedMaxima(decel);
            if (lateral != null) _latLearner.ImportLearnedMaxima(lateral);
        }

        // ---- Recommended shaker frequency range (data only - the settings UI displays this, it does
        // ---- not drive anything in Core/GForce itself; SimHub's own ShakeIt "From Hz"/"To Hz" effect
        // ---- fields are outside this task's file ownership).

        /// <summary>The rumble pads' own hardware capability, per the brief: 10-300 Hz.</summary>
        public const double DeviceMinHz = 10.0;

        /// <summary>See <see cref="DeviceMinHz"/>.</summary>
        public const double DeviceMaxHz = 300.0;

        private double _recommendedFromHz = 100.0;
        private double _recommendedToHz = 50.0;

        /// <summary>
        /// Recommended "From" Hz - the frequency a channel plays at when its published value is near
        /// 0 (the high/subtle end of the convention: value near 0 -&gt; high Hz, value 100 -&gt; low
        /// Hz). UPDATED TWICE (docs\raw-gap-and-pad-balance-report.md), both times per the owner's own
        /// real seat-time feel, not theory: first from the original 300 Hz down to 50 Hz (300 Hz at the
        /// low end of the value range read too harsh/thin), then - after further seat time found 20 Hz
        /// too weak to shake strongly enough - to the current **100 Hz -&gt; 50 Hz** range. This is
        /// DATA/GUIDANCE TEXT ONLY (see this class's own remarks above <see cref="DeviceMinHz"/>) - it
        /// does not feed any computed output, so this change cannot move any behavioural test. Still
        /// clamped to the device's actual 10-300 Hz capability so a differently-capable device still
        /// gets a sane recommendation instead of an out-of-range one.
        /// </summary>
        public double RecommendedFromHz
        {
            get => ClampMath.Clamp(_recommendedFromHz, DeviceMinHz, DeviceMaxHz);
            set => _recommendedFromHz = value;
        }

        /// <summary>
        /// Recommended "To" Hz - the frequency a channel plays at when its published value is 100
        /// (the low/punchy end). See <see cref="RecommendedFromHz"/>'s own remarks for the revision
        /// history - now 50 Hz (raised from an original 20 Hz, which real seat time found did not
        /// shake strongly enough), clamped to the device range AND kept at or below
        /// <see cref="RecommendedFromHz"/> - the whole point of the convention is value 0 -&gt; high Hz,
        /// value 100 -&gt; low Hz, so a "to" above "from" would silently invert it.
        /// </summary>
        public double RecommendedToHz
        {
            get => ClampMath.Clamp(_recommendedToHz, DeviceMinHz, RecommendedFromHz);
            set => _recommendedToHz = value;
        }
    }
}

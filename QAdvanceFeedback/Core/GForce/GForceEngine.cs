using System;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>Normal: positive LateralG biases the Right pads (see
    /// <see cref="GForceEngine.LateralDirection"/>'s remarks for the full physical convention this
    /// corresponds to). Reversed: the driver's own preference to feel the mirror image.</summary>
    public enum LateralDirectionMode { Normal, Reversed }

    /// <summary>
    /// The G-force STAGED TRAVEL model (docs\lock-and-animation-report.md - this REPLACES the previous
    /// pass's washout model, "sustained low-pass + transient high-pass", per the driver's own explicit
    /// specification: "the chains are now correct but the driver does not FEEL the travel"). Two
    /// independent signals drive every chain (braking/accelerating), exactly as the driver specified:
    /// <list type="bullet">
    /// <item>a low-pass ("sustain") LEVEL that tracks the steady-state G ratio (unchanged mechanism from
    /// the previous pass, <see cref="SustainTimeConstantSeconds"/>) - this is what "the SUSTAIN level is
    /// driven by the G VALUE itself" means, and it is what makes a falling G (same direction) scale the
    /// whole distribution down proportionally while preserving the sustain ratios (the driver's own
    /// 90/45/22.5 -&gt; 60/30/15 worked example - see <see cref="StagedShape"/>'s remarks for why this
    /// falls out automatically).</item>
    /// <item>an explicit, three-keyframe STAGE PROGRESS (0-&gt;1) that sweeps the pad distribution from
    /// the far pad (fully lit) through the middle pad to the terminal pad (the "sustain" shape) - this
    /// is the actual TRAVEL the driver asked to feel. Its own SPEED (not level) is driven by the DELTA
    /// in the sustain ratio, not its absolute value - "stamping the throttle from rest is a large delta
    /// -&gt; a quick, strong sweep; a gentle change -&gt; a small, slow sweep" - see
    /// <see cref="AdvanceStageProgress"/>.</item>
    /// </list>
    /// <para/>
    /// PAD GEOMETRY (owner-confirmed): Bottom Front = far-leg side (braking's own TERMINAL pad); Bottom
    /// Rear = leg-root side (braking's MIDDLE pad / acceleration's FAR pad); Low Back (<c>BackLow</c>) =
    /// waist (braking's FAR pad / acceleration's MIDDLE pad); Top Back (<c>BackTop</c>) = upper back
    /// (acceleration's own TERMINAL pad).
    /// <para/>
    /// THE THREE STAGES (owner's own specification, verbatim):
    /// <code>
    /// ACCELERATION: BottomRear HIGH/LowBack LOW/TopBack LOW
    ///            -&gt; BottomRear MID/LowBack HIGH/TopBack LOW
    ///            -&gt; BottomRear LOW/LowBack MID/TopBack HIGH (= sustain)
    /// DECELERATION (mirrored): LowBack HIGH/BottomRear LOW/BottomFront LOW
    ///                       -&gt; LowBack MID/BottomRear HIGH/BottomFront LOW
    ///                       -&gt; LowBack LOW/BottomRear MID/BottomFront HIGH (= sustain)
    /// </code>
    /// HIGH is always 1.0 (the terminal pad's own hat, not a setting). MID/LOW reuse the EXISTING,
    /// already-configurable sustain-fraction settings (<see cref="BrakeBottomRearSustainFraction"/> etc)
    /// - the middle zone's own fraction is used as MID, the far zone's own fraction as LOW, WHICHEVER
    /// pad happens to occupy that qualitative slot at a given stage - this is a deliberate reuse (not a
    /// new setting) so a driver's already-tuned sustain fractions carry over unchanged.
    /// <para/>
    /// DIRECTION SELECTION FOR THE ANIMATION ITSELF (owner's own rules, distinct from - and layered on
    /// top of - the established magnitude/direction split below): accelerating requires BOTH measured
    /// SpeedingUp direction AND the throttle pedal actually applied; braking requires the brake pedal
    /// applied (direction continues to gate the underlying magnitude split as before, so a brake press
    /// while genuinely SpeedingUp still contributes nothing - see <see cref="_direction"/>'s own
    /// remarks); coasting (neither pedal) instead watches the DECELERATION-direction chain's own DELTA -
    /// a large one (engine braking / a forced downshift) still runs the deceleration animation, a small
    /// one (<see cref="CoastingDeltaDeadBandPerSecond"/> - ordinary rolling resistance) produces NO cue
    /// at all.
    /// <para/>
    /// DIRECTION STILL COMES FROM DIFFERENTIATED SPEED, NEVER THE REPORTED G SIGN (established fix,
    /// unchanged by this restructure - docs\gforce-direction-fix-report.md): this class still owns its
    /// own <see cref="LongitudinalDirectionResolver"/>, and <see cref="ITelemetryFrame.LongitudinalG"/>'s
    /// sign is still never read for chain selection, only its magnitude.
    /// <para/>
    /// SUPERSEDED FROM THE PREVIOUS PASS'S SIX ACCEPTANCE SCENARIOS (S1-S6, docs\wiring-ui-report.md):
    /// S1/S3/S6 are re-verified under new, direct measurements (delta-driven sweep speed) rather than
    /// the old "gap against a TransientGain=0 twin" technique, since the additive transient concept the
    /// twin isolated no longer exists. S5 ("a transient while already saturated spends the headroom
    /// above the sustain floors") is EXPLICITLY SUPERSEDED: once the stage progress has fully swept
    /// (reached the terminal/sustain shape) and the sustain level is itself already saturated at 1.0,
    /// there is no further "travel" left to show - the owner's own specification calls only for
    /// delta-driven TRAVEL and G-driven SUSTAIN SCALING, neither of which describes a residual bump
    /// while both are already at their own ceiling. This is a deliberate departure, not an oversight -
    /// see docs\lock-and-animation-report.md for the full reasoning.
    /// </summary>
    public sealed class GForceEngine
    {
        /// <summary>
        /// How quickly the SUSTAIN level's low-pass filter tracks a new steady-state G ratio, in
        /// seconds - UNCHANGED role from the previous pass. This is what makes "G falling while still in
        /// the same direction" scale the whole distribution down smoothly (see
        /// <see cref="StagedShape"/>'s remarks) rather than snapping.
        /// </summary>
        public double SustainTimeConstantSeconds { get; set; } = 0.15;

        /// <summary>
        /// REPURPOSED from the previous pass's "transient smoothing time constant" (same property name
        /// and default, kept to avoid a settings-schema/persistence break) - now the DECAY time constant
        /// of the LATCHED stage-travel rate (see <see cref="AdvanceStageProgress"/>): a single fast
        /// onset (e.g. a hard stamp on the brake) latches a high travel rate that then decays over this
        /// many seconds, so the sweep continues for a few frames after the initiating delta itself has
        /// already settled, instead of producing a one-frame flicker.
        /// </summary>
        public double TransientTimeConstantSeconds { get; set; } = 0.08;

        /// <summary>
        /// REPURPOSED from the previous pass's "transient gain" (same property name, default now
        /// re-tuned) - now the gain converting the observed/latched delta-driven rate into
        /// stage-progress advancement per second, capped at <see cref="MaxStageProgressPerSecond"/> -
        /// see <see cref="AdvanceStageProgress"/>'s own remarks for the full derivation and the mutation
        /// this constant is specifically evidenced against (driving the animation from magnitude instead
        /// of delta must fail the large-vs-small-delta test). DEFAULT changed from 1.5 to 1.2
        /// (owner's own hardware testing: the animation reads more clearly at 1.2). Raising this gain
        /// LOWERS the observed rate at which the sweep saturates against
        /// <see cref="MaxStageProgressPerSecond"/> (at 1.2, that's ~4.2/s), shrinking the felt distinction
        /// between a gentle and a violent input beyond that point.
        /// </summary>
        public double TransientGain { get; set; } = 1.0;

        /// <summary>See <see cref="GForceEngine"/>'s class remarks (braking's MIDDLE pad, Bottom Rear) -
        /// UNCHANGED meaning/default from the previous pass, now doubling as the staged model's own MID
        /// level.</summary>
        public double BrakeBottomRearSustainFraction { get; set; } = 0.5;

        /// <summary>Braking's FAR pad (Back Low) - UNCHANGED meaning/default, now the staged model's LOW
        /// level for the braking chain.</summary>
        public double BrakeBackLowSustainFraction { get; set; } = 0.25;

        /// <summary>Acceleration's FAR pad (Bottom Rear) - UNCHANGED meaning/default, now the staged
        /// model's LOW level for the acceleration chain.</summary>
        public double AccelBottomRearSustainFraction { get; set; } = 0.25;

        /// <summary>Acceleration's MIDDLE pad (Back Low) - UNCHANGED meaning/default, now the staged
        /// model's MID level for the acceleration chain.</summary>
        public double AccelBackLowSustainFraction { get; set; } = 0.5;

        /// <summary>The lateral-G magnitude treated as "full scale" for the left/right bias. 1.6g is a
        /// reasonable fixed reference covering everything from road cars to GT3-class content.</summary>
        public double LateralReferenceG { get; set; } = 1.6;

        /// <summary>
        /// LIVE-PATH-ONLY plausibility clamp on LongitudinalG's own magnitude (UNCHANGED from the
        /// previous pass - docs\gforce-direction-fix-report.md): the LEARNING path REJECTS an
        /// impact-magnitude reading outright; this LIVE path CLAMPS instead, so an impact frame still
        /// produces a real, finite, saturated cue rather than freezing or dropping.
        /// </summary>
        public const double LiveMagnitudeClampG = 15.0;

        // ---- OUTPUT SCALES (v1.0.8). Attenuate the published output per axis; see
        // ---- Settings.GForceSettings.AccelOutputScalePercent for why they are not applied inside the
        // ---- friction circle.
        public double AccelOutputScale { get; set; } = 1.0;
        public double BrakeOutputScale { get; set; } = 1.0;
        public double LateralOutputScale { get; set; } = 1.0;

        // ---- LATERAL SPLIT PER CHANNEL (v1.0.8) - the share of the lateral headroom each pad turns into
        // ---- a left/right split. Defaults are deliberately inverted against the longitudinal emphasis;
        // ---- see Settings.GForceSettings' own remarks.
        public double BrakeBottomFrontLatSplit { get; set; } = 0.50;
        public double BrakeBottomRearLatSplit { get; set; } = 0.75;
        public double BrakeBackLowLatSplit { get; set; } = 1.00;
        public double AccelBottomRearLatSplit { get; set; } = 1.00;
        public double AccelBackLowLatSplit { get; set; } = 0.75;
        public double AccelBackTopLatSplit { get; set; } = 0.50;

        /// <summary>
        /// Which chain's lateral splits to use when there is no longitudinal G at all - a steady-state
        /// mid-corner. Owner's decision: KEEP THE LAST ACTIVE CHAIN, so the lateral cue stays where the
        /// animation already was instead of jumping to a fixed set when longitudinal G fades away.
        /// </summary>
        private bool _lastChainWasBraking = true;

        /// <summary>The owner's driver-facing lateral direction toggle - unchanged from the previous
        /// pass, unaffected by this restructure (lateral bias is independent of the longitudinal
        /// chain-selection/travel logic).</summary>
        public LateralDirectionMode LateralDirection { get; set; } = LateralDirectionMode.Normal;

        // ---- Owner-requested "Integrate Wheel Lock and Slip" shake (see GForceShake) - unaffected by
        // this restructure.
        //
        // Bare-constructor default stays OFF deliberately (docs\integrate-default-report.md) - this is a
        // library-level "inert unless configured" baseline for anyone constructing GForceEngine directly
        // (every GForceEngineShakeTests "disabled"/"baseline" fixture relies on exactly this), NOT the
        // same thing as what a real, fully-wired install experiences. The SETTINGS-layer default
        // (Settings.GForceSettings.IntegrateWheelLockAndSlip) is now ON, and Settings.GForceSettings.ApplyTo
        // pushes that value onto this property at Init and on every settings Apply - so the two defaults
        // disagreeing here is intentional, not a drift bug: this property alone is only ever the
        // pre-settings-applied value.
        public bool IntegrateWheelLockAndSlip { get; set; } = false;

        private double _shakeFrequencyHz = 10.0;

        /// <summary>Hz, clamped to [<see cref="GForceShake.MinFrequencyHz"/> (1),
        /// <see cref="GForceShake.MaxFrequencyHz"/> (20)]. Default 10 Hz (raised from an earlier 3 Hz -
        /// see <see cref="Settings.GForceSettings.ShakeFrequencyHz"/>'s remarks for the full rationale).
        /// UNLIKE <see cref="IntegrateWheelLockAndSlip"/>'s bare-constructor default (deliberately kept
        /// OFF while the settings-layer default is ON - see that property's own remarks), this
        /// bare-engine default is kept IN SYNC with the settings-layer default rather than split from
        /// it - the two have always carried the same numeric value here (there is no "inert unless
        /// configured" reason for a frequency to differ the way there is for the on/off switch), and
        /// <see cref="Settings.GForceSettings.ApplyTo"/> pushes the settings value onto this property at
        /// Init and on every settings Apply regardless. NOT the Layer 5 pulse's own separate, UNCHANGED
        /// 200 ms (5 Hz) floor (<see cref="Projection.PulseSettings.MinGapMs"/>).</summary>
        public double ShakeFrequencyHz
        {
            get => _shakeFrequencyHz;
            set => _shakeFrequencyHz = ClampMath.Clamp(value, GForceShake.MinFrequencyHz, GForceShake.MaxFrequencyHz);
        }

        /// <summary>How the shake is spread across the eight pads (v1.0.8) - see
        /// <see cref="Core.GForce.ShakeApplyMode"/> for what each mode does. Defaults to
        /// <see cref="ShakeApplyMode.PerChannel"/>, the only behaviour that existed before, so an engine
        /// constructed directly behaves exactly as it did.</summary>
        public ShakeApplyMode ShakeApplyMode { get; set; } = ShakeApplyMode.PerChannel;

        /// <summary>How the two pads of a pair relate while shaking - see <see cref="ShakeFeeling"/>.
        /// Replaces the old free blend number; <see cref="ShakeBlend"/> is retained only as a
        /// persistence/compatibility shim and no longer reaches the wave.</summary>
        public ShakeFeeling ShakeFeeling { get; set; } = ShakeFeeling.OppositePhase;

        /// <summary>
        /// True for the two modes whose oscillation comes from the WHEEL value rather than from a
        /// G-force level - <see cref="ShakeApplyMode.AllChannelsLockSlip"/> and
        /// <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/>.
        /// <para/>
        /// Two places have to know this and would otherwise silently mis-handle the newer mode: the
        /// shake-start corner scoring (which must score against the band actually about to be used) and
        /// the no-G-force fallback (where a G-force-derived band does not exist at all).
        /// </summary>
        /// <summary>
        /// One pad's <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/> output: the shared zero-floor
        /// wave scaled to THIS pad's own band, which is the greater of the wheel band and this pad's
        /// G-force value (owner's specification, v1.0.8).
        /// <para/>
        /// The floor is ALWAYS zero and the peak is always <c>Max(wheelBand, thisPad'sGForce)</c>, so the
        /// pad travels the full height of whichever cue is louder. One oscillator drives every pad - only
        /// the amplitude differs - which is why this takes a normalised 0..1 multiplier rather than a
        /// finished value.
        /// </summary>
        /// <param name="normalized">This frame's 0..1 wave position for this side.</param>
        /// <param name="wheelBand">The wheel-driven band, shared by every pad.</param>
        /// <param name="padBase">This pad's post-lateral G-force value, still on the 0-1 base scale.</param>
        private static double ScaleToPadBand(double normalized, double wheelBand, double padBase)
        {
            double gForce = ClampMath.To0100(padBase * 100.0);
            return ClampMath.To0100(Math.Max(wheelBand, gForce) * normalized);
        }

        /// <summary>Whether this mode's pads travel from a ZERO FLOOR on one shared, wheel-derived band.
        /// <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/> LEFT this group on 2026-09-07: it now
        /// keeps PerChannel's own centred range and merely raises the ceiling, so only
        /// <see cref="ShakeApplyMode.AllChannelsLockSlip"/> is left with a true zero floor.</summary>
        private bool UsesLockSlipWave => ShakeApplyMode == ShakeApplyMode.AllChannelsLockSlip;

        private double _shakeSustainFraction = 0.40;

        /// <summary>
        /// Share of each half-period the wave holds at an extreme, 0 to
        /// <see cref="GForceShake.MaxSustainFraction"/> (0.90), clamped in the setter. Default 0.40 -
        /// see <see cref="GForceShake.Wave"/> for the shape and why 0 is a triangle rather than the
        /// pre-1.0.8 sine.
        /// <para/>
        /// Stored as a FRACTION here while the settings layer and UI carry a PERCENT
        /// (<see cref="Settings.GForceSettings.ShakeSustainPercent"/>); the conversion lives in
        /// <see cref="Settings.GForceSettings.ApplyTo"/>, so the driver-facing "40" and this 0.40 can
        /// never drift into two independently-edited numbers.
        /// </summary>
        public double ShakeSustainFraction
        {
            get => _shakeSustainFraction;
            set => _shakeSustainFraction = ClampMath.IsFinite(value)
                ? ClampMath.Clamp(value, 0.0, GForceShake.MaxSustainFraction)
                : 0.0;
        }

        // NO ShakeBlend ANY MORE (v1.0.8). The "Both-sides blend (%)" share became ShakeFeeling's three
        // named choices, and the property outlived the mechanism as write-only state: ApplyTo kept
        // setting it and nothing ever read it back, so a persisted blend silently did nothing.

        private double _shakeTriggerThreshold = 5.0;

        /// <summary>
        /// The wheel lock/slip value (0-100, UNSCALED) at or above which a shake may start. Default 20.
        /// Below it the shake stays silent, so the small amounts of lock/slip present during ordinary
        /// driving no longer produce a permanent background buzz. Compared against
        /// <c>Max(lock, slip)</c>, matching the drive itself.
        /// </summary>
        public double ShakeTriggerThreshold
        {
            get => _shakeTriggerThreshold;
            set => _shakeTriggerThreshold = ClampMath.IsFinite(value) ? ClampMath.To0100(value) : 5.0;
        }

        /// <summary>The sustain actually reaching the wave, after the blend's own dwell is accounted for -
        /// see <see cref="GForceShake.EffectiveSustain"/>.</summary>
        private double EffectiveShakeHold => GForceShake.EffectiveHold(ShakeSustainFraction, ShakeFeeling);

        private double _wheelLockShakeScale = 1.5;

        /// <summary>Default 1.5 (150%) - see <see cref="Settings.GForceSettings.WheelLockShakeScale"/>'s
        /// remarks for the full rationale.</summary>
        public double WheelLockShakeScale
        {
            get => _wheelLockShakeScale;
            set => _wheelLockShakeScale = value >= 0.0 ? value : 0.0;
        }

        private double _wheelSlipShakeScale = 1.5;

        /// <summary>Default 1.5 (150%) - see <see cref="Settings.GForceSettings.WheelLockShakeScale"/>'s
        /// remarks for the full rationale (mirrored for Slip).</summary>
        public double WheelSlipShakeScale
        {
            get => _wheelSlipShakeScale;
            set => _wheelSlipShakeScale = value >= 0.0 ? value : 0.0;
        }

        /// <summary>Upper bound enforced by <see cref="TransitionAnimationScale"/>'s own setter -
        /// docs\gforce-transition-scale-report.md. JUDGMENT CALL: even a very placid low-G car whose
        /// sustained ratio bottoms out around 0.15-0.2 (against the new, lower 0.75g/1.5g fixed maxima)
        /// only needs a scale of ~5 to push its own transition PEAK all the way to a full-feeling 100%;
        /// beyond that the aggregate pad level is already saturated and clamped (see
        /// <see cref="Compute"/>'s own <c>ClampMath.To01</c> calls), so a higher ceiling would only
        /// invite nonsense values (e.g. a typo) in the settings UI without ever producing a stronger
        /// felt result.</summary>
        public const double MaxTransitionAnimationScale = 5.0;

        private double _transitionAnimationScale = 1.5;

        /// <summary>
        /// THE TRANSITION ANIMATION SCALE (docs\gforce-transition-scale-report.md - the owner's own
        /// request: "a low-G car should still produce a full-feeling transition sweep"). Amplifies ONLY
        /// the staged sweep's own PEAK reach (see <see cref="StagedShape"/>'s <c>peak</c> parameter,
        /// replacing what used to be a hardcoded HIGH=1.0 ceiling for the far/mid pads' own transit-only
        /// keyframes) - it never touches <see cref="AdvanceSustainLevel"/> or the settled (progress=1)
        /// distribution, which is why a driver's real sense of "how hard am I actually
        /// braking/accelerating relative to the car's own capability" is completely unaffected by this
        /// setting at ANY value (see <see cref="StagedShape"/>'s remarks for the exact proof: every one
        /// of the three pads reaches its own TRUE, scale-independent value at progress=1, regardless of
        /// <paramref name="peak"/> along the way). Clamped to [0, <see cref="MaxTransitionAnimationScale"/>]
        /// in the setter itself. Default 1.5, matching the owner's own worked example (a 0.3g/0.9g road
        /// car against the new 0.75g/1.5g maxima: 0.3x1.5=0.45 -&gt; 60% of the accel transition ceiling,
        /// 0.9x1.5=1.35 -&gt; 90% of the decel one) - see the report for why 1.5 still ships even though
        /// the maxima in the SAME change are being lowered (the two changes turn out not to compound
        /// harmfully for a high-G car, which is already saturated under the new maxima with or without
        /// this scale).
        /// </summary>
        public double TransitionAnimationScale
        {
            get => _transitionAnimationScale;
            set => _transitionAnimationScale = ClampMath.Clamp(value, 0.0, MaxTransitionAnimationScale);
        }

        private bool _shakeActive;
        private double _shakePhaseSeconds;
        private bool _shakeReleasing;
        private bool _shakeReversed;              // flips the cycle's traversal order; alternates per shake
        private bool _shakeHasRunBefore;          // false until the first shake of the session has started
        private double _lastLeftOut, _lastRightOut;   // last PUBLISHED pair, for the start-corner choice
        private double _shakeCycleStartSeconds;
        private double _shakeReleaseEndSeconds;
        private double _shakeHeldContribution;

        // ------------------------------------------------------------------------------------
        // STAGED TRAVEL - new state (docs\lock-and-animation-report.md). Two independent tracks per
        // chain: the sustain LEVEL (low-pass, unchanged mechanism) and the STAGE PROGRESS (0-1, new).
        // ------------------------------------------------------------------------------------

        private double _brakeSustainLevel;
        private double _brakeStageProgress;
        private double _brakeTravelRate;

        /// <summary>The HIGHEST raw brake ratio seen since this chain's current sweep began - see
        /// <see cref="AnimationLevel"/> for what it is for and why the low-passed level alone was not
        /// enough. Reset with the sweep whenever the chain goes inactive.</summary>
        private double _brakeAnimationPeak;

        /// <summary>The previous frame's ratio for delta purposes - deliberately ALWAYS starts at 0.0
        /// (not "no previous value yet"), so a telemetry stream that starts already at a sustained,
        /// nonzero ratio (a cold start mid-event, with no observed ramp-up at all) still gets a
        /// legitimate initial "delta from zero" kick and plays the sweep, rather than getting
        /// permanently stuck at stage 0 forever for lack of any observed change.</summary>
        private double _brakePreviousRatio;

        /// <summary>COASTING GATE state (docs\lock-and-animation-report.md) - a SEPARATE, always-running
        /// (never gated by chain activity) latched+decaying delta-rate tracker, used ONLY to decide
        /// whether a coasting frame counts as "a large delta" (see <see cref="AdvanceCoastingDeltaRate"/>).
        /// Deliberately independent of <see cref="_brakeTravelRate"/> (which only updates while the
        /// chain is already active - a chicken-and-egg problem, since deciding activity is what THIS
        /// tracker is for) and of <see cref="_brakePreviousRatio"/> (which resets to 0 whenever the
        /// chain is inactive): without its own always-on memory, a sudden coasting-deceleration kick
        /// would only ever be detected for the single frame the value actually changed, then
        /// immediately flip back to "small delta" the instant the new (elevated) value holds steady for
        /// even one more frame - producing a one-frame flicker instead of a felt, sustained cue for the
        /// engine-braking event's own duration.</summary>
        private double _brakeCoastingPreviousRatio;

        /// <summary>See <see cref="_brakeCoastingPreviousRatio"/>'s remarks.</summary>
        private double _brakeCoastingDeltaRate;

        private double _accelSustainLevel;
        private double _accelStageProgress;
        private double _accelTravelRate;

        /// <summary>Acceleration's mirror of <see cref="_brakeAnimationPeak"/>.</summary>
        private double _accelAnimationPeak;

        /// <summary>See <see cref="_brakePreviousRatio"/>'s remarks.</summary>
        private double _accelPreviousRatio;

        /// <summary>
        /// The absolute FASTEST the stage progress is ever allowed to advance, in "sweeps per second" -
        /// e.g. 5.0 means a full 0-&gt;1 sweep can never complete in under 0.2s, regardless of how large
        /// or sudden the driving delta is. Without this cap, an instantaneous step (a single-frame
        /// onset, the extreme case of "stamping the throttle from rest") would complete the ENTIRE
        /// three-stage sweep within one or two frames - not felt as travel at all, defeating the whole
        /// point of this restructure. JUDGMENT CALL (no rig to time this against): 5.0 was chosen as a
        /// duration (~0.2s) fast enough to still read as "quick, strong" relative to a multi-second
        /// gentle onset, while remaining long enough (a handful of frames at any realistic sim frame
        /// rate) to be felt as genuine, directional travel rather than a snap.
        /// </summary>
        private const double MaxStageProgressPerSecond = 5.0;

        /// <summary>
        /// A GUARANTEED MINIMUM sweep speed, applied whenever the chain is active, regardless of how
        /// small the observed delta is - see <see cref="AdvanceStageProgress"/>'s own remarks for why
        /// this is necessary: the delta-driven rate decays geometrically once the input stops changing,
        /// and for a small enough initial delta the resulting infinite geometric series sums to LESS
        /// than a full sweep - i.e. without this floor, a genuinely gentle onset could asymptotically
        /// approach, but mathematically never reach, the stage-3/sustain shape, no matter how long the
        /// chain stayed active. "Sustain the final distribution while acceleration/braking continues"
        /// (the owner's own wording) requires the sweep to eventually, reliably complete for EVERY
        /// sustained event, not just large ones - only the SPEED of getting there should vary with delta
        /// size, per this constant's own name. JUDGMENT CALL: 1.0 (a 1-second guaranteed-worst-case
        /// completion) is comfortably slower than any delta-driven rate a real onset - even a gentle,
        /// multi-second ramp - already produces well before its own delta decays away, so it only ever
        /// matters as a true floor for a near-instant, tiny-delta cold start, not as the dominant driver
        /// of ordinary sweeps.
        /// </summary>
        private const double MinStageProgressPerSecond = 1.0;

        /// <summary>Bounds for <see cref="RetriggerStrictness"/>. The floor is not 0: a strictness of
        /// zero would make the threshold zero, re-arming the sweep on ANY rise at all, which is exactly
        /// the "keeps triggering during continuous braking" behaviour the setting exists to avoid.</summary>
        public const double MinRetriggerStrictness = 0.1;

        /// <summary>See <see cref="MinRetriggerStrictness"/>. At 5.0 the pedal would have to cover the
        /// car's whole range in 40 ms, which is effectively "never re-arm".</summary>
        public const double MaxRetriggerStrictness = 5.0;

        private double _retriggerStrictness = 1.2;

        /// <summary>
        /// HOW STRICT THE MID-BRAKE RE-TRIGGER IS, as a multiple of "fast enough to cross the whole
        /// range inside one sweep" (owner's own derivation, 2026-09-06). **Driver-configurable since
        /// 2026-09-07** - it is the one number here that only seat time can settle, so it is a setting
        /// rather than a constant. Default **1.2**, the bottom of the owner's own suggested 1.2-1.5.
        /// <para/>
        /// The threshold itself is <see cref="MaxStageProgressPerSecond"/> x this, in RATIO per second -
        /// ratio, not raw g, which is what makes it self-scaling exactly as the owner wanted: the
        /// threshold is <c>maxG / sweepDuration x strictness</c> in absolute terms, so a low-max-G car
        /// (slower stops, smaller absolute deltas) needs a proportionally smaller delta to earn its
        /// animation, and a high-max-G car is not retriggered by every small stab.
        /// <para/>
        /// At the shipped 0.2 s fastest sweep, 1.2 means "the pedal moved far enough to cover the car's
        /// whole braking range in 167 ms" - a deliberate stab, not the continuous modulation of a long
        /// corner entry. HIGHER is stricter (a harder stab is needed); LOWER re-arms more readily and
        /// eventually feels busy. Clamped in the setter, so neither a hand-edited config nor a bad
        /// spinner value can produce a zero or negative threshold.
        /// </summary>
        public double RetriggerStrictness
        {
            get => _retriggerStrictness;
            set => _retriggerStrictness = ClampMath.IsFinite(value)
                ? ClampMath.Clamp(value, MinRetriggerStrictness, MaxRetriggerStrictness)
                : 1.2;
        }

        /// <summary>Rising ratio-per-second that re-arms a COMPLETED sweep - see
        /// <see cref="RetriggerStrictness"/>. Computed rather than stored so a mid-session Apply takes
        /// effect on the very next frame, like every other setting this engine reads.</summary>
        private double RetriggerRatioRatePerSecond => MaxStageProgressPerSecond * RetriggerStrictness;

        /// <summary>
        /// Fraction of the sweep the FAR pad holds at its peak before the travel starts (owner,
        /// 2026-09-06: "hold on start channel maximum a little bit longer ... shift the animation a few
        /// milliseconds").
        /// <para/>
        /// WHY IT WAS NEEDED. The owner's reading of the old shape was right: the far pad was at its
        /// peak for a single instant at p=0 and only ever fell from there, while the MIDDLE pad rises
        /// into its peak and then falls away from it - so the middle pad spent roughly twice as long
        /// near maximum as the pad that opens the animation. Freezing the opening keyframe for the
        /// first quarter of the sweep gives the far pad a real plateau, making its time at maximum
        /// comparable to the middle pad's, and delays the whole travel slightly as asked.
        /// </summary>
        private const double StartHoldFraction = 0.25;

        /// <summary>
        /// Named, justified dead band for the COASTING case (owner's own requirement - "a small, steady
        /// deceleration is just rolling resistance -&gt; NO vibration at all"): the deceleration-ratio's
        /// own rate of change (per second) must exceed this before a coasting-only (no pedal) event is
        /// treated as "engine braking / a forced downshift" rather than ordinary drag. JUDGMENT CALL:
        /// ordinary rolling/aero resistance decelerates a coasting car smoothly over several seconds
        /// (a small, steady fraction of the deceleration reference per second); a forced downshift or
        /// engine-braking transition is a comparatively abrupt kick reaching a meaningful fraction of the
        /// deceleration reference within a fraction of a second. 0.5 (ratio-units per second, i.e. half
        /// of the configured deceleration maximum per second) sits well above typical steady drag decay
        /// rates while still well below the near-instant rate a genuine kick produces - not
        /// independently rig-tuned, flagged as such like this codebase's other similar constants (e.g.
        /// <see cref="Normalized.NormalizedWheelLockSlipEngine"/>'s own <c>RawActiveThreshold</c>).
        /// </summary>
        private const double CoastingDeltaDeadBandPerSecond = 0.5;

        /// <summary>"Is the pedal meaningfully pressed at all" - deliberately a tiny epsilon (not a
        /// driver-configurable threshold like Wheel Lock/Slip's own Trigger Threshold, which is a
        /// different, independently-configured concept for a different channel) so ordinary sensor
        /// noise on an unpressed pedal cannot be read as "applied".</summary>
        private const double PedalAppliedThresholdPercent = 1.0;

        private readonly LongitudinalDirectionResolver _direction;

        public GForceEngine() : this(null) { }

        public GForceEngine(LongitudinalDirectionResolver directionResolver)
        {
            _direction = directionResolver ?? new LongitudinalDirectionResolver();
        }

        /// <summary>The most recently resolved direction - exposed for diagnostics and so the plugin
        /// composition root can attribute an AUTO-mode learner observation to the SAME axis this frame's
        /// chain selection used.</summary>
        public LongitudinalMotionState CurrentDirection => _direction.State;

        /// <summary>Clears all staged-travel state back to zero - call on a session/game/car switch.</summary>
        public void Reset()
        {
            _brakeSustainLevel = 0.0;
            _brakeStageProgress = 0.0;
            _brakeTravelRate = 0.0;
            _brakePreviousRatio = 0.0;
            _brakeCoastingPreviousRatio = 0.0;
            _brakeCoastingDeltaRate = 0.0;

            _accelSustainLevel = 0.0;
            _accelStageProgress = 0.0;
            _accelTravelRate = 0.0;
            _accelPreviousRatio = 0.0;

            _shakeActive = false;
            _shakePhaseSeconds = 0.0;
            // The v1.0.8 shake state belongs to the session, not to whatever came before it: a new session
            // must not inherit the previous one's release timer, alternation parity, or remembered pair.
            _shakeReleasing = false;
            _shakeHeldContribution = 0.0;
            _shakeCycleStartSeconds = 0.0;
            _shakeReleaseEndSeconds = 0.0;
            _shakeReversed = false;
            _shakeHasRunBefore = false;
            _lastLeftOut = 0.0;
            _lastRightOut = 0.0;
            _direction.Reset();
        }

        /// <param name="accelTransitionScale">Per-frame override for the ACCELERATION chain's own
        /// transition-animation scale (docs\robust-auto-gforce-report.md, mode-dependent transition
        /// scaling) - null uses this instance's own <see cref="TransitionAnimationScale"/> property
        /// (every pre-existing caller/test that never passes this keeps its exact prior behaviour).
        /// <see cref="Settings.GForceSettings"/> computes this per (gameId,carId) frame, continuously
        /// blending its configured Auto/Fixed transition scales by AUTO's own confidence - see that
        /// class's own remarks for why this avoids a step in animation magnitude at the 200-sample
        /// threshold.</param>
        /// <param name="decelTransitionScale">The deceleration/braking chain's own equivalent of
        /// <paramref name="accelTransitionScale"/>.</param>
        public GForceOutput Compute(
            ITelemetrySample sample, double accelMaxG, double decelMaxG,
            double wheelLockAll0100 = 0.0, double wheelSlipAll0100 = 0.0,
            double? accelTransitionScale = null, double? decelTransitionScale = null,
            double latMaxG = 1.5)
        {
            if (sample == null) return GForceOutput.Empty;

            double effectiveAccelTransitionScale = accelTransitionScale ?? TransitionAnimationScale;
            double effectiveDecelTransitionScale = decelTransitionScale ?? TransitionAnimationScale;

            double dtSeconds = sample.Dt.HasValue && sample.Dt.Value.TotalSeconds > 0.0 ? sample.Dt.Value.TotalSeconds : 0.0;

            LongitudinalMotionState direction = _direction.Resolve(sample);

            double lockContribution = WheelLockShakeScale * (ClampMath.To0100(wheelLockAll0100) / 100.0);
            double slipContribution = WheelSlipShakeScale * (ClampMath.To0100(wheelSlipAll0100) / 100.0);

            // THE TRIGGER GATE (v1.0.8) reads the UNSCALED wheel values, not the scaled contributions, so
            // "shake above 20" means the same 20 the driver sees on the Wheel Lock/Slip tabs however the
            // scales are set. Still Max(lock, slip), like the drive itself.
            double gateValue = Math.Max(ClampMath.To0100(wheelLockAll0100), ClampMath.To0100(wheelSlipAll0100));
            bool aboveThreshold = IntegrateWheelLockAndSlip && gateValue >= ShakeTriggerThreshold;

            double shakeContribution = AdvanceShake(
                aboveThreshold, Math.Max(lockContribution, slipContribution), dtSeconds);

            double? longG = sample.New?.LongitudinalG;
            double? lateralGForFallback = sample.New?.LateralG;
            if (!longG.HasValue)
            {
                return lateralGForFallback.HasValue
                    ? ComputeLateralOnlyFallback(lateralGForFallback.Value, shakeContribution, latMaxG)
                    : GForceOutput.Empty;
            }

            // Direction and magnitude are two independent signals (established fix, unchanged): the
            // resolver above supplies direction; LongitudinalG supplies only its Math.Abs magnitude,
            // never its sign.
            double magnitude = Math.Min(Math.Abs(longG.Value), LiveMagnitudeClampG);

            double safeDecelMax = decelMaxG > 1e-6 ? decelMaxG : 1e-6;
            double safeAccelMax = accelMaxG > 1e-6 ? accelMaxG : 1e-6;

            double brakeG = direction == LongitudinalMotionState.Slowing ? magnitude : 0.0;
            double accelG = direction == LongitudinalMotionState.SpeedingUp ? magnitude : 0.0;

            double rBrake = brakeG / safeDecelMax;
            double rAccel = accelG / safeAccelMax;

            // ---- ANIMATION DIRECTION SELECTION (owner's own rules - see class remarks). Layered ON
            // TOP of the direction-gated magnitude split above, not a replacement for it.
            double? brakePercent = sample.New?.BrakePercent;
            double? throttlePercent = sample.New?.ThrottlePercent;
            bool brakeApplied = brakePercent > PedalAppliedThresholdPercent;
            bool throttleApplied = throttlePercent > PedalAppliedThresholdPercent;
            bool coasting = !brakeApplied && !throttleApplied;

            bool accelChainActive = direction == LongitudinalMotionState.SpeedingUp && throttleApplied;

            // The coasting gate's own latched delta-rate runs UNCONDITIONALLY, every frame, regardless
            // of whether the chain ends up active - see _brakeCoastingPreviousRatio's own remarks for
            // why (a one-off instantaneous delta must not decide activity for only a single frame).
            double coastingDeltaRatePerSecond = AdvanceCoastingDeltaRate(
                dtSeconds, rBrake, ref _brakeCoastingPreviousRatio, ref _brakeCoastingDeltaRate);

            bool decelChainActive;
            if (brakeApplied)
            {
                decelChainActive = true;
            }
            else if (coasting)
            {
                // MUTATION (c) target (see class remarks): removing this dead-band check (always
                // treating a coasting frame as "large delta") must fail the "no cue while rolling" test.
                decelChainActive = coastingDeltaRatePerSecond > CoastingDeltaDeadBandPerSecond;
            }
            else
            {
                decelChainActive = false;
            }

            double brakeSustained = AdvanceSustainLevel(dtSeconds, rBrake, decelChainActive, ref _brakeSustainLevel);
            double accelSustained = AdvanceSustainLevel(dtSeconds, rAccel, accelChainActive, ref _accelSustainLevel);

            double brakeProgress = AdvanceStageProgress(
                dtSeconds, rBrake, decelChainActive, ref _brakePreviousRatio, ref _brakeTravelRate,
                ref _brakeStageProgress, out bool brakeRestarted);
            double accelProgress = AdvanceStageProgress(
                dtSeconds, rAccel, accelChainActive, ref _accelPreviousRatio, ref _accelTravelRate,
                ref _accelStageProgress, out bool accelRestarted);

            // A RE-ARMED SWEEP GETS A FRESH PEAK. Without this the new animation would be scaled by the
            // old event's high-water mark, so a second, gentler stab would read as loud as the first.
            if (brakeRestarted) _brakeAnimationPeak = 0.0;
            if (accelRestarted) _accelAnimationPeak = 0.0;

            // THE SWEEP IS SCALED BY THE PEAK OF THE TARGET, NOT BY THE LOW-PASSED LEVEL - see
            // AnimationLevel. Tracked on the RAW ratio so a hard stamp registers on the frame it happens.
            AdvanceAnimationPeak(dtSeconds, rBrake, decelChainActive, ref _brakeAnimationPeak);
            AdvanceAnimationPeak(dtSeconds, rAccel, accelChainActive, ref _accelAnimationPeak);

            double brakeAnimated = AnimationLevel(brakeSustained, _brakeAnimationPeak, brakeProgress);
            double accelAnimated = AnimationLevel(accelSustained, _accelAnimationPeak, accelProgress);

            // ---- Braking chain: far=BackLow, mid=BottomRear, terminal=BottomFront.
            StagedShape(brakeProgress, ClampMath.To01(BrakeBottomRearSustainFraction), ClampMath.To01(BrakeBackLowSustainFraction),
                effectiveDecelTransitionScale,
                out double brakeFarShape, out double brakeMidShape, out double brakeTerminalShape);
            double brakeBackLowSustained = brakeAnimated * brakeFarShape;
            double brakeBottomRearSustained = brakeAnimated * brakeMidShape;
            double brakeBottomFrontSustained = brakeAnimated * brakeTerminalShape;

            // ---- Acceleration chain: far=BottomRear, mid=BackLow, terminal=BackTop.
            StagedShape(accelProgress, ClampMath.To01(AccelBackLowSustainFraction), ClampMath.To01(AccelBottomRearSustainFraction),
                effectiveAccelTransitionScale,
                out double accelFarShape, out double accelMidShape, out double accelTerminalShape);
            double accelBottomRearSustained = accelAnimated * accelFarShape;
            double accelBackLowSustained = accelAnimated * accelMidShape;
            double accelBackTopSustained = accelAnimated * accelTerminalShape;

            // Bottom Rear and Back Low are shared between the two chains; brake and accel energy can
            // never both be non-zero for the same frame (mutually exclusive by direction), so a plain
            // sum is safe.
            double bottomFrontLevel = ClampMath.To01(brakeBottomFrontSustained);
            double bottomRearLevel = ClampMath.To01(brakeBottomRearSustained + accelBottomRearSustained);
            double backLowLevel = ClampMath.To01(brakeBackLowSustained + accelBackLowSustained);
            double backTopLevel = ClampMath.To01(accelBackTopSustained);

            // ---- LATERAL, AS A FRICTION CIRCLE (v1.0.8, owner's own model and worked examples).
            //
            // Lateral is no longer a MULTIPLIER on each pad. It used to be
            // `pad x (1 +/- gain*bias)`, which had two fatal problems: under hard braking the strong
            // side was already at 100 so there was no headroom left to lean into (all the asymmetry had
            // to come from the weak side dropping), and while shaking the two pads' ranges stopped
            // overlapping so the shake never alternated at all.
            //
            // It is now ADDITIVE HEADROOM taken from the friction circle. Longitudinal and lateral grip
            // cannot both be at maximum at once, so their combined magnitude is the hypotenuse:
            //
            //     combined = sqrt(rLong^2 + rLat^2)
            //     level_ch = rLong x that channel's own staged shape      (terminal shape = 1.0)
            //     boost_ch = (combined - rLong) x that channel's own lateral split
            //     L/R      = clamp(level_ch +/- boost_ch)
            //
            // The owner's worked example: 72% brake with 70% lateral gives combined 100.4%, so 28.4
            // points of headroom. Bottom Front (split 50%) reads 86.2/57.8; Bottom Rear (level 36,
            // split 75%) reads 57.3/14.7; Back Low (level 18, split 100%) reads 46.4/0. The splits are
            // inverted against the longitudinal emphasis on purpose, which is what makes a trail brake
            // read as the cue travelling BACK and to one side rather than everything just getting louder.
            double? lateralG = sample.New?.LateralG;
            double safeLatMax = latMaxG > 1e-6 ? latMaxG : 1e-6;
            double signedLatRatio = lateralG.HasValue
                ? ApplyLateralDirection(ClampMath.Clamp(lateralG.Value / safeLatMax, -1.0, 1.0))
                : 0.0;
            double latRatio = Math.Abs(signedLatRatio);

            // WHICH CHAIN OWNS THE SPLITS. Normally the one with energy; with neither active (a
            // steady-state corner) the LAST one, so the cue does not jump as longitudinal G fades.
            const double chainEnergyEpsilon = 1e-6;
            if (brakeSustained > chainEnergyEpsilon || accelSustained > chainEnergyEpsilon)
                _lastChainWasBraking = brakeSustained >= accelSustained;
            bool brakingChain = _lastChainWasBraking;

            double longRatio = brakingChain ? brakeSustained : accelSustained;
            double combined = Math.Sqrt(longRatio * longRatio + latRatio * latRatio);
            double lateralHeadroom = Math.Max(0.0, combined - longRatio) * ClampMath.To01(LateralOutputScale);

            double levelScale = ClampMath.To01(brakingChain ? BrakeOutputScale : AccelOutputScale);

            // Each channel's lateral split - 0 for a pad the active chain does not drive (Back Top under
            // braking, Bottom Front under power), which the owner's six-value spec leaves out by design.
            double bottomFrontSplit = brakingChain ? ClampMath.To01(BrakeBottomFrontLatSplit) : 0.0;
            double bottomRearSplit = ClampMath.To01(brakingChain ? BrakeBottomRearLatSplit : AccelBottomRearLatSplit);
            double backLowSplit = ClampMath.To01(brakingChain ? BrakeBackLowLatSplit : AccelBackLowLatSplit);
            double backTopSplit = brakingChain ? 0.0 : ClampMath.To01(AccelBackTopLatSplit);

            // Positive lateral biases the RIGHT pads (see LateralDirection's own remarks), so the sign
            // decides which side receives the boost and which gives it up.
            double lateralSign = signedLatRatio >= 0.0 ? 1.0 : -1.0;

            PairFromLevelAndBoost(bottomFrontLevel * levelScale, lateralHeadroom * bottomFrontSplit, lateralSign,
                out double bottomFrontBaseL, out double bottomFrontBaseR);
            PairFromLevelAndBoost(bottomRearLevel * levelScale, lateralHeadroom * bottomRearSplit, lateralSign,
                out double bottomRearBaseL, out double bottomRearBaseR);
            PairFromLevelAndBoost(backLowLevel * levelScale, lateralHeadroom * backLowSplit, lateralSign,
                out double backLowBaseL, out double backLowBaseR);
            PairFromLevelAndBoost(backTopLevel * levelScale, lateralHeadroom * backTopSplit, lateralSign,
                out double backTopBaseL, out double backTopBaseR);

            double bottomFrontLeft, bottomFrontRight, bottomRearLeft, bottomRearRight;
            double backLowLeft, backLowRight, backTopLeft, backTopRight;

            if (shakeContribution > 0.0 && ShakeApplyMode == ShakeApplyMode.AllChannelsLockSlip)
            {
                // LOCK/SLIP ONLY - one wave from the wheel value alone, on all eight pads, with NO
                // lateral involvement at all (owner's decision: this mode must depend on nothing else).
                GForceShake.ApplyLockSlipOnly(
                    shakeContribution, ShakeFrequencyHz, _shakePhaseSeconds, EffectiveShakeHold,
                    ShakeFeeling, out double lockSlipLeft, out double lockSlipRight, _shakeReversed);

                bottomFrontLeft = bottomRearLeft = backLowLeft = backTopLeft = ClampMath.To0100(lockSlipLeft);
                bottomFrontRight = bottomRearRight = backLowRight = backTopRight = ClampMath.To0100(lockSlipRight);
            }
            else if (shakeContribution > 0.0 && ShakeApplyMode == ShakeApplyMode.HigherOfGForceOrLockSlip)
            {
                // HIGHER OF THE TWO, PER CHANNEL, ON PER-CHANNEL'S OWN SHAPE (owner's REDEFINITION,
                // 2026-09-07). It used to travel from a zero floor on a band shared by all eight pads,
                // which threw away the per-channel G-force shape entirely. It now runs EXACTLY the
                // PerChannel path - same centre, same band, same lateral "both pads follow the stronger
                // side" rule - and then does one thing differently:
                //
                //     low  = PerChannel's low, UNCHANGED
                //     high = Min(100, Max(PerChannel's high, 100 x contribution))
                //
                // so the minimum is always identical to PerChannel's and only the ceiling is lifted by
                // the wheel. A quiet wheel leaves the G-force animation intact and barely audible on top;
                // a wheel past its own limit pushes every channel's ceiling to 100 and the shake takes
                // over regardless of G-force.
                double wheelCeiling = ClampMath.To0100(100.0 * shakeContribution);

                ShakePairRaisedCeiling(bottomFrontBaseL, bottomFrontBaseR, shakeContribution, wheelCeiling,
                    out bottomFrontLeft, out bottomFrontRight);
                ShakePairRaisedCeiling(bottomRearBaseL, bottomRearBaseR, shakeContribution, wheelCeiling,
                    out bottomRearLeft, out bottomRearRight);
                ShakePairRaisedCeiling(backLowBaseL, backLowBaseR, shakeContribution, wheelCeiling,
                    out backLowLeft, out backLowRight);
                ShakePairRaisedCeiling(backTopBaseL, backTopBaseR, shakeContribution, wheelCeiling,
                    out backTopLeft, out backTopRight);
            }
            else if (shakeContribution > 0.0 && ShakeApplyMode == ShakeApplyMode.AllChannelsGForce)
            {
                // ALL CHANNELS, G-FORCE - the ACTIVE CHAIN'S TERMINAL pad sets one band for everybody,
                // read from its POST-LATERAL output (owner: "the shaking value reference is based on the
                // ACTUAL final output"). Terminal picked by direction, not by which wheel signal is
                // larger - see ShakeApplyMode's own remarks.
                double terminalBase = direction == LongitudinalMotionState.SpeedingUp
                    ? Math.Max(backTopBaseL, backTopBaseR)
                    : Math.Max(bottomFrontBaseL, bottomFrontBaseR);

                GForceShake.Apply(
                    terminalBase * 100.0, shakeContribution,
                    ShakeFrequencyHz, _shakePhaseSeconds, EffectiveShakeHold, ShakeFeeling,
                    out double sharedLeft, out double sharedRight, _shakeReversed);

                bottomFrontLeft = bottomRearLeft = backLowLeft = backTopLeft = ClampMath.To0100(sharedLeft);
                bottomFrontRight = bottomRearRight = backLowRight = backTopRight = ClampMath.To0100(sharedRight);
            }
            else if (shakeContribution > 0.0)
            {
                // PER-CHANNEL - each pair shakes around ITS OWN post-lateral output, and both pads of a
                // pair follow the STRONGER side while shaking. That equalisation is what keeps the shake
                // genuinely alternating under a cornering load; without it the two ranges stop
                // overlapping and the same side stays louder at every instant. The lateral cue returns
                // the moment the shake stops.
                ShakePair(bottomFrontBaseL, bottomFrontBaseR, shakeContribution, out bottomFrontLeft, out bottomFrontRight);
                ShakePair(bottomRearBaseL, bottomRearBaseR, shakeContribution, out bottomRearLeft, out bottomRearRight);
                ShakePair(backLowBaseL, backLowBaseR, shakeContribution, out backLowLeft, out backLowRight);
                ShakePair(backTopBaseL, backTopBaseR, shakeContribution, out backTopLeft, out backTopRight);
            }
            else
            {
                // SILENT - publish the friction-circle pair as-is, lateral split and all.
                bottomFrontLeft = ClampMath.To0100(bottomFrontBaseL * 100.0);
                bottomFrontRight = ClampMath.To0100(bottomFrontBaseR * 100.0);
                bottomRearLeft = ClampMath.To0100(bottomRearBaseL * 100.0);
                bottomRearRight = ClampMath.To0100(bottomRearBaseR * 100.0);
                backLowLeft = ClampMath.To0100(backLowBaseL * 100.0);
                backLowRight = ClampMath.To0100(backLowBaseR * 100.0);
                backTopLeft = ClampMath.To0100(backTopBaseL * 100.0);
                backTopRight = ClampMath.To0100(backTopBaseR * 100.0);
            }

            // REMEMBER THE TERMINAL PAIR for the next shake's start-corner choice. The terminal channel is
            // the one the driver's attention is on (and the one AllChannelsGForce already keys off), and
            // there is only ONE oscillator, so one channel has to decide where a shake opens.
            _lastLeftOut = direction == LongitudinalMotionState.SpeedingUp ? backTopLeft : bottomFrontLeft;
            _lastRightOut = direction == LongitudinalMotionState.SpeedingUp ? backTopRight : bottomFrontRight;

            return new GForceOutput(
                bottomFrontLeft: bottomFrontLeft,
                bottomFrontRight: bottomFrontRight,
                bottomRearLeft: bottomRearLeft,
                bottomRearRight: bottomRearRight,
                backLowLeft: backLowLeft,
                backLowRight: backLowRight,
                backTopLeft: backTopLeft,
                backTopRight: backTopRight);
        }

        /// <summary>
        /// The SUSTAIN level - a plain, dt-correct low-pass filter of the current ratio toward the
        /// configured max, UNCHANGED mechanism from the previous pass. When the chain is not active this
        /// frame (<paramref name="active"/> false), the target is 0 (not <paramref name="rawRatio"/>,
        /// which the caller already zeroes in that case) so the level decays away rather than holding a
        /// stale value forever.
        /// </summary>
        private double AdvanceSustainLevel(double dtSeconds, double rawRatio, bool active, ref double sustainLevel)
        {
            double target = active ? rawRatio : 0.0;
            sustainLevel = ExponentialSmooth(sustainLevel, target, dtSeconds, SustainTimeConstantSeconds);
            return ClampMath.To01(sustainLevel);
        }

        /// <summary>
        /// THE TRAVEL (docs\lock-and-animation-report.md): advances <paramref name="stageProgress"/>
        /// (0-&gt;1, three keyframes at 0/0.5/1.0 - see <see cref="StagedShape"/>) at a rate driven by
        /// the OBSERVED DELTA in <paramref name="rawRatio"/> since the previous frame, NOT its absolute
        /// value - "stamping the throttle from rest is a large delta -&gt; a quick, strong sweep; a
        /// gentle change -&gt; a small, slow sweep" (the owner's own wording, verbatim).
        /// <para/>
        /// MUTATION (a) in the report: replace <c>deltaRatio</c> below with <c>rawRatio</c> itself
        /// (driving the sweep from the G MAGNITUDE instead of its delta) - the large-vs-small-delta test
        /// must fail, since a SUSTAINED large magnitude (no further change) would then keep advancing
        /// indefinitely instead of a genuinely small, slow delta producing a genuinely slower sweep.
        /// <para/>
        /// A single large one-frame delta LATCHES a high travel rate that then decays over
        /// <see cref="TransientTimeConstantSeconds"/> (repurposed - see that property's own remarks),
        /// so the sweep continues across several subsequent frames rather than a one-frame flicker - the
        /// classical "peak-follow then decay" shape this plugin family already uses elsewhere (e.g.
        /// <see cref="Normalized.NormalizedWheelLockSlipEngine"/>'s own release envelope). The rate is
        /// capped at <see cref="MaxStageProgressPerSecond"/> so even an instantaneous, unbounded delta
        /// cannot complete the sweep in a single frame (see that constant's own remarks for why a
        /// felt-but-quick minimum duration matters).
        /// <para/>
        /// When the chain is not active this frame, progress (and the latched rate/previous-ratio
        /// bookkeeping) resets to zero - the NEXT genuine onset for this chain always starts a fresh
        /// three-stage sweep rather than resuming from a stale mid-sweep position.
        /// </summary>
        private double AdvanceStageProgress(
            double dtSeconds, double rawRatio, bool active,
            ref double previousRatio, ref double travelRate, ref double stageProgress,
            out bool restarted)
        {
            restarted = false;

            // "Hold rather than guess" (this plugin family's own standing convention for a missing/
            // invalid dt - e.g. the very first sample of a session): a frame with no usable dt cannot
            // be timed, so EVERYTHING here (including whether to reset an inactive chain) is held
            // exactly as-is, regardless of <paramref name="active"/> - only once dt is valid again does
            // this method decide to reset or advance.
            bool dtValid = ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0;
            if (!dtValid) return ClampMath.To01(stageProgress);

            if (!active)
            {
                stageProgress = 0.0;
                travelRate = 0.0;
                previousRatio = 0.0;
                return 0.0;
            }

            // previousRatio always starts at 0.0 (see its own field remarks) - a cold start already at
            // a sustained, nonzero ratio still gets a legitimate initial delta-from-zero kick.
            double clampedRatio = ClampMath.To01(rawRatio);
            double signedDelta = clampedRatio - previousRatio;
            double deltaRatio = Math.Abs(signedDelta);
            previousRatio = clampedRatio;

            // ---- RE-ARM A FINISHED SWEEP ON A FAST RISE (owner, 2026-09-06). --------------------
            // The defect: progress only ever reset when the CHAIN went inactive, so once the sweep had
            // completed, a driver already on the brake got no animation at all from a later hard stab -
            // "even with a small value for a while, a quick dec later will NOT be triggered".
            //
            // Three rules, all the owner's:
            //  - ONLY WHEN NOTHING IS RUNNING. A sweep in progress is never cut short and restarted;
            //    that would read as a stutter rather than a new event.
            //  - ONLY ON A RISE. The delta is SIGNED here on purpose: dec-G falling away fast - lifting
            //    off the brake - must not trigger anything. (The travel RATE below still uses the
            //    magnitude, so a release still sweeps out at a matching speed.)
            //  - THE THRESHOLD SELF-SCALES with the car - see RetriggerRatioRatePerSecond.
            if (stageProgress >= 1.0 && signedDelta / dtSeconds >= RetriggerRatioRatePerSecond)
            {
                stageProgress = 0.0;
                travelRate = 0.0;
                restarted = true;
            }

            double observedRatePerSecond = deltaRatio / dtSeconds;
            double decayedRate = ExponentialDecayToZero(travelRate, dtSeconds, TransientTimeConstantSeconds);
            travelRate = Math.Max(observedRatePerSecond, decayedRate);

            double advancePerSecond = Math.Min(Math.Max(travelRate * TransientGain, MinStageProgressPerSecond), MaxStageProgressPerSecond);
            stageProgress = ClampMath.To01(stageProgress + advancePerSecond * dtSeconds);
            return stageProgress;
        }

        /// <summary>
        /// THE COASTING GATE'S OWN DELTA RATE (docs\lock-and-animation-report.md) - runs
        /// UNCONDITIONALLY every frame (never gated by chain activity, unlike
        /// <see cref="AdvanceStageProgress"/>'s own rate), so a sudden coasting-deceleration kick (a
        /// forced downshift, engine braking) is remembered (latched, then decaying over
        /// <see cref="TransientTimeConstantSeconds"/>) for a few frames after the initiating delta
        /// itself, rather than being detected for only the single frame the value actually changed and
        /// then immediately reading as "small" again the instant a held sample repeats it.
        /// </summary>
        private double AdvanceCoastingDeltaRate(double dtSeconds, double rawRatio, ref double previousRatio, ref double deltaRate)
        {
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0) return deltaRate;

            double clampedRatio = ClampMath.To01(rawRatio);
            double delta = Math.Abs(clampedRatio - previousRatio);
            previousRatio = clampedRatio;

            double observedRatePerSecond = delta / dtSeconds;
            double decayedRate = ExponentialDecayToZero(deltaRate, dtSeconds, TransientTimeConstantSeconds);
            deltaRate = Math.Max(observedRatePerSecond, decayedRate);
            return deltaRate;
        }

        /// <summary>
        /// THE THREE KEYFRAMES (owner's own specification, verbatim - see class remarks), piecewise
        /// linear between them at <paramref name="progress"/>=0/0.5/1.0 (continuous by construction - no
        /// discontinuous jump anywhere, satisfying the owner's own "a jump is felt as a click"
        /// requirement).
        /// <para/>
        /// MUTATION (b) in the report: collapse this to a single stage (e.g. return the stage-2/sustain
        /// shape unconditionally regardless of <paramref name="progress"/>) - an ordering test (checking
        /// that <paramref name="farValue"/> leads at low progress, <paramref name="midValue"/> leads at
        /// mid progress, <paramref name="terminalValue"/> leads at high progress) must fail.
        /// <para/>
        /// WHY THE OWNER'S WORKED EXAMPLE FALLS OUT AUTOMATICALLY: at progress=1.0 (fully staged, the
        /// "sustain" keyframe), this returns exactly (LOW, MID, HIGH=1.0) for (far, mid, terminal) - the
        /// caller then multiplies by the SAME <c>sustainLevel</c> for all three, so a falling
        /// sustainLevel (G decreasing, same direction) scales all three proportionally, preserving
        /// exactly the MID/LOW ratios relative to the terminal's own 1.0 - e.g. sustainLevel 0.9-&gt;0.6
        /// with MID=0.5/LOW=0.25 gives terminal 90-&gt;60 (100% of the change), mid 45-&gt;30 (50%), far
        /// 22.5-&gt;15 (25%), the owner's own example, verbatim.
        /// <para/>
        /// TRANSITION ANIMATION SCALE (docs\gforce-transition-scale-report.md - <paramref name="peak"/>,
        /// <see cref="TransitionAnimationScale"/>): the ORIGINAL hardcoded HIGH=1.0 constant appeared in
        /// exactly three places above - the far pad's own p=0 keyframe, the mid pad's own p=0.5
        /// keyframe, and the terminal pad's own p=1 keyframe. Of those three, only the FIRST TWO are
        /// ever a pad's OWN transit peak (a value it passes through on its way to a DIFFERENT final
        /// resting fraction - far ends at LOW, mid ends at MID); the terminal's own p=1 keyframe is its
        /// TRUE, settled sustain value, never a transit peak. This split is why <paramref name="peak"/>
        /// (replacing HIGH in only the first two instances - see the branches below) can amplify the
        /// SWEEP without ever moving the terminal's own p=1 reading, or far/mid's own p=1 readings
        /// (LOW/MID respectively) - EVERY branch below still resolves to exactly (LOW, MID, HIGH) at
        /// p=1 for ANY value of <paramref name="peak"/>, by construction (each keyframe's own two
        /// defining branches meet at that keyframe's TRUE value, never at <paramref name="peak"/>) -
        /// this is what keeps the settled/sustain distribution bit-for-bit identical across every scale
        /// value (the dedicated test asserts exactly this). <paramref name="peak"/> itself is the RAW
        /// <see cref="TransitionAnimationScale"/> value (not pre-multiplied by anything) since the
        /// caller already multiplies this method's whole output by <c>sustainLevel</c> - so a peak of
        /// 1.0 (this method's own prior hardcoded HIGH) reproduces the pre-existing behaviour exactly,
        /// and a peak of, say, 1.5 makes a far/mid pad's own transit peak reach 1.5x what the current
        /// sustain level alone would have given it (still clamped 0-100 downstream, same as any other
        /// saturation in this engine).
        /// <para/>
        /// MUTATION target (sustain-path leak): multiplying <paramref name="peak"/> - rather than the
        /// unchanged constant <c>high</c> - into the TERMINAL's own p=1 branch (or into either pad's own
        /// p=1 resting fraction) would leak this scale into the settled/sustain reading - the dedicated
        /// "sustain unchanged at every scale value" test is what catches that.
        /// </summary>
        /// <summary>The travelling keyframes' own FIXED shoulder values (owner, 2026-09-06): the pad one
        /// step behind or ahead of the lit one sits at half, the pad two steps away at a quarter. These
        /// are deliberately NOT the configured sustain fractions - only the FINAL, resting keyframe reads
        /// those, so retuning a sustain changes where the animation comes to rest without flattening the
        /// travel on the way there.</summary>
        private const double TravelShoulderNear = 0.50;
        private const double TravelShoulderFar = 0.25;

        private static void StagedShape(double progress, double midFraction, double lowFraction, double peak, out double farValue, out double midValue, out double terminalValue)
        {
            const double high = 1.0; // TRUE, scale-independent terminal ceiling - NEVER replaced by peak.

            // OWNER'S OWN SPECIFICATION, 2026-09-06, for a target output of 100:
            //   start        Low/Rear/Front = 100 / 50 / 25   <- FIXED shoulders
            //   a few ms on                 =  50 / 100 / 50  <- FIXED shoulders
            //   at rest                     =  25 / 50 / 100  <- the CONFIGURED sustains
            //
            // WHAT CHANGED AND WHY. The shoulders used to read the configured sustain fractions at every
            // keyframe, so the far pad's opening value and the terminal's opening value were the same
            // number that also describes where things settle. That conflated two different ideas and, at
            // the shipped sustains, left the terminal opening at 0.25 while the far pad's own later
            // resting value was also 0.25 - the travel had nothing to say. The two leading slots still
            // take `peak` (the driver's Transition scale), which is what amplifies the travel itself.
            double raw = ClampMath.To01(progress);

            // THE OPENING KEYFRAME IS HELD for StartHoldFraction of the sweep, then the three-keyframe
            // travel plays out across whatever is left - see StartHoldFraction for why.
            if (raw < StartHoldFraction)
            {
                farValue = peak;
                midValue = TravelShoulderNear;
                terminalValue = TravelShoulderFar;
                return;
            }

            double p = (raw - StartHoldFraction) / (1.0 - StartHoldFraction);

            if (p <= 0.5)
            {
                double t = p / 0.5;
                // far: PEAK -> near shoulder. mid: near shoulder -> PEAK. terminal: far shoulder -> near.
                farValue = peak + (TravelShoulderNear - peak) * t;
                midValue = TravelShoulderNear + (peak - TravelShoulderNear) * t;
                terminalValue = TravelShoulderFar + (TravelShoulderNear - TravelShoulderFar) * t;
            }
            else
            {
                double t = (p - 0.5) / 0.5;
                // Only this half lands on the CONFIGURED sustains - the resting shape.
                farValue = TravelShoulderNear + (lowFraction - TravelShoulderNear) * t;
                midValue = peak + (midFraction - peak) * t;
                terminalValue = TravelShoulderNear + (high - TravelShoulderNear) * t;
            }
        }

        /// <summary>
        /// Tracks the HIGHEST raw ratio this chain has seen since its current sweep began, so the sweep
        /// can be scaled by what the driver actually asked for rather than by how far a low-pass filter
        /// happened to have got. Resets with the sweep when the chain goes inactive.
        /// </summary>
        private static void AdvanceAnimationPeak(double dtSeconds, double rawRatio, bool active, ref double peak)
        {
            // Same "hold rather than guess" rule AdvanceStageProgress uses for an unusable dt.
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0) return;

            if (!active) { peak = 0.0; return; }

            double clamped = ClampMath.To01(rawRatio);
            if (clamped > peak) peak = clamped;
        }

        /// <summary>
        /// The level the staged shape is multiplied by: THE PEAK OF THE TARGET EARLY IN THE SWEEP,
        /// handing over to the plain low-passed level as the sweep completes.
        /// <para/>
        /// THE BUG THIS FIXES (owner, 2026-09-06): "drag the slide bar quickly from 0 to dec 100 and Low
        /// is almost never over 25, Rear almost never over 50; only Front animates from ~70 to 100". Both
        /// mechanisms were racing. The sustain level rises with its own 0.15 s time constant while the
        /// stage progress sweeps in as little as 200 ms - so at the instant the FAR pad's keyframe is at
        /// its peak, the level multiplying it is still near zero, and by the time the level has arrived
        /// the sweep has already moved on and collapsed that pad's shape. The leading pads could
        /// therefore never show the travel at all; only the terminal, which peaks last, ever looked right.
        /// <para/>
        /// Scaling the sweep by the peak of the RAW ratio instead removes the race: a hard stamp puts the
        /// full value under the far pad on the very frame it happens ("try to catch up with the maximum
        /// of the target G-Force as much as possible"), while a gentle build has a correspondingly low
        /// peak and stays soft - which is exactly the difference in feel the owner asked for. The
        /// <c>(1 - progress)</c> handover means the resting shape is still driven by the live level, so a
        /// long brake settles wherever the pedal actually is rather than at a stale peak.
        /// <para/>
        /// It can only ever RAISE the early level (<c>lead >= sustained</c> by construction), never lower
        /// it, so no previously-correct steady state moves.
        /// </summary>
        private static double AnimationLevel(double sustained, double animationPeak, double progress)
        {
            double lead = Math.Max(animationPeak, sustained);
            double p = ClampMath.To01(progress);
            return sustained + (lead - sustained) * (1.0 - p);
        }

        /// <summary>
        /// THE SHAKE GATE AND ITS RHYTHM (v1.0.8). Returns the contribution to use this frame - 0 when the
        /// shake is silent - and owns every piece of shake timing state.
        /// <para/>
        /// Four owner requirements, all about the shake feeling like one continuous rhythm rather than a
        /// signal that restarts whenever the wheel value wobbles:
        /// <list type="number">
        /// <item>THRESHOLD. Below <see cref="ShakeTriggerThreshold"/> the shake does not start. Small
        /// amounts of lock/slip are constant during normal driving and produced a permanent low buzz that
        /// masked the real events.</item>
        /// <item>ONE RHYTHM. Once running, the phase only ever advances. A changing wheel value moves the
        /// band's WIDTH and nothing else, so the beat stays where the driver's body has locked onto it.</item>
        /// <item>FINISH THE CYCLE. When the value drops away the shake does not cut off mid-swing; it runs
        /// to the end of the cycle it had already begun, so every gesture is a whole one.</item>
        /// <item>RE-ARM INSIDE THAT TAIL KEEPS THE RHYTHM. A wheel value that crosses back over the
        /// threshold during the trailing cycle rejoins the beat already in progress - it does NOT restart
        /// the phase, which would read as a stutter. Only a shake that starts from true silence realigns.</item>
        /// </list>
        /// During the trailing cycle the LAST above-threshold contribution is held rather than the current
        /// (sub-threshold) one: the point is to complete the gesture at the size it was already being felt
        /// at, not to collapse it as it finishes.
        /// </summary>
        private double AdvanceShake(bool aboveThreshold, double liveContribution, double dtSeconds)
        {
            if (!IntegrateWheelLockAndSlip)
            {
                _shakeActive = false;
                _shakeReleasing = false;
                _shakePhaseSeconds = 0.0;
                _shakeHeldContribution = 0.0;
                return 0.0;
            }

            double dt = ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0 ? dtSeconds : 0.0;
            double period = ShakeFrequencyHz > 0.0 ? 1.0 / ShakeFrequencyHz : 0.0;

            if (aboveThreshold)
            {
                if (!_shakeActive)
                {
                    // A GENUINELY NEW SHAKE - open at whichever corner moves the pads FURTHEST from where
                    // they currently sit, so the first frame is the biggest jolt available rather than a
                    // fixed pose that might happen to be where the pads already are.
                    //
                    // The direction alternates every time: if a shake stops and restarts quickly, the
                    // opposite side leads, which reads as one continuing rhythm rather than two separate
                    // events. On the very first shake of a session there is nothing to alternate from, so
                    // the side with the SHORTER travel leads (owner's rule).
                    _shakeActive = true;

                    // THE BAND THE SHAKE IS ABOUT TO USE - and it is MODE-DEPENDENT, so the corner choice
                    // is scored against the real thing. AllChannelsLockSlip's band does not come from the
                    // G-force level at all, so using the pads' current level there (0 after a silence with
                    // no G-force) would collapse every corner onto the same point and make the choice
                    // meaningless. The other two modes do shake around the level, and use the last
                    // published terminal pair for it.
                    double referenceCentre, band;
                    if (UsesLockSlipWave)
                    {
                        band = ClampMath.To0100(100.0 * liveContribution);
                        referenceCentre = band / 2.0;
                    }
                    else
                    {
                        referenceCentre = (_lastLeftOut + _lastRightOut) / 2.0;
                        band = referenceCentre * liveContribution;
                    }

                    double low, high;
                    if (UsesLockSlipWave)
                    {
                        // ZERO FLOOR - these modes span exactly 0..band (see GForceShake.NormalizedPair),
                        // so scoring the start corner against PadRange's centred, PeakExcursionFactor-
                        // shrunk range would score against a range the shake never actually uses.
                        low = 0.0;
                        high = band;
                    }
                    else
                    {
                        GForceShake.PadRange(referenceCentre, band, out low, out high);
                    }

                    bool leadLeft = _shakeHasRunBefore
                        ? !_shakeReversed
                        : GForceShake.FirstEverLeadIsLeft(_lastLeftOut, _lastRightOut, low, high);

                    GForceShake.ShakeStartCorner corner = GForceShake.ChooseStartCorner(
                        _lastLeftOut, _lastRightOut, referenceCentre, band,
                        leadLeft);

                    _shakeReversed = _shakeHasRunBefore ? !_shakeReversed : !leadLeft;
                    _shakeHasRunBefore = true;

                    _shakePhaseSeconds = GForceShake.PhaseForCorner(
                        corner, ShakeFrequencyHz, EffectiveShakeHold, ShakeFeeling, _shakeReversed);
                    _shakeCycleStartSeconds = _shakePhaseSeconds;
                }
                else
                {
                    // Either still running, or re-armed inside the trailing cycle: keep the beat.
                    _shakePhaseSeconds += dt;
                }
                _shakeReleasing = false;
                _shakeHeldContribution = liveContribution;
                return liveContribution;
            }

            if (!_shakeActive) return 0.0;   // silent, and staying silent

            if (!_shakeReleasing)
            {
                // Just dropped below the threshold - mark where the current cycle ends and run to it.
                //
                // COMPUTED AS AN EXPLICIT REMAINDER, not Ceiling(elapsed)*period. The phase is built by
                // repeatedly adding dt, so after a few seconds it drifts a few ULPs; when a dropout lands
                // ON a cycle boundary the division returns 5.99999... instead of 6.0 and Ceiling then
                // picks the boundary the shake is ALREADY standing on - ending the release instantly and
                // cutting off exactly the gesture this is here to complete. Observed at frame 30 of a
                // 40-frame run. Taking the fractional position and treating "no meaningful remainder" as
                // a whole cycle is stable from either side of the boundary.
                _shakeReleasing = true;
                if (period > 0.0)
                {
                    double elapsedCycles = (_shakePhaseSeconds - _shakeCycleStartSeconds) / period;
                    double positionInCycle = elapsedCycles - Math.Floor(elapsedCycles);
                    double remaining = (1.0 - positionInCycle) * period;
                    if (remaining <= period * 1e-6) remaining = period;   // sitting on a boundary
                    _shakeReleaseEndSeconds = _shakePhaseSeconds + remaining;
                }
                else
                {
                    _shakeReleaseEndSeconds = _shakePhaseSeconds;
                }
            }

            _shakePhaseSeconds += dt;
            if (_shakePhaseSeconds >= _shakeReleaseEndSeconds)
            {
                _shakeActive = false;
                _shakeReleasing = false;
                _shakePhaseSeconds = 0.0;
                _shakeHeldContribution = 0.0;
                return 0.0;
            }

            return _shakeHeldContribution;
        }

        /// <summary>
        /// One pad pair's shaken output, taken from its own post-lateral values.
        /// <para/>
        /// BOTH PADS FOLLOW THE STRONGER ONE (owner's decision). The lateral split has already pushed the
        /// two sides apart by this point; shaking each around its own value leaves their ranges
        /// non-overlapping under any real cornering load, so the loud side never changes and the shake
        /// stops reading as an alternation at all (measured: 75.9-100 against 32.5-51.4, zero swaps per
        /// cycle). Taking the stronger side for both restores it.
        /// </summary>
        private void ShakePair(double baseLeft01, double baseRight01, double shakeContribution,
            out double left, out double right)
        {
            double stronger = Math.Max(baseLeft01, baseRight01) * 100.0;

            GForceShake.Apply(stronger, shakeContribution, ShakeFrequencyHz, _shakePhaseSeconds,
                EffectiveShakeHold, ShakeFeeling, out double shakenL, out double shakenR, _shakeReversed);

            left = ClampMath.To0100(shakenL);
            right = ClampMath.To0100(shakenR);
        }

        /// <summary>
        /// <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/>'s pair: PerChannel's own range with the
        /// ceiling raised to the wheel's, the floor untouched. See the call site for the owner's rule.
        /// </summary>
        private void ShakePairRaisedCeiling(double baseLeft01, double baseRight01,
            double shakeContribution, double wheelCeiling, out double left, out double right)
        {
            // IDENTICAL to ShakePair up to here - the same "both pads follow the stronger side while
            // shaking" equalisation, so the two modes cannot drift apart.
            double stronger = ClampMath.To0100(Math.Max(baseLeft01, baseRight01) * 100.0);
            double band = stronger * shakeContribution;

            GForceShake.PadRange(stronger, band, out double low, out double high);
            double raised = Math.Min(100.0, Math.Max(high, wheelCeiling));

            GForceShake.ApplyRange(low, raised, ShakeFrequencyHz, _shakePhaseSeconds,
                EffectiveShakeHold, ShakeFeeling, _shakeReversed, out double l, out double r);

            left = ClampMath.To0100(l);
            right = ClampMath.To0100(r);
        }

        /// <summary>
        /// Splits one channel's level into a left/right pair using its lateral boost. Additive, so the
        /// boost is genuine extra headroom on the loaded side rather than a multiplier that has nowhere
        /// to go once the level is already at full scale.
        /// </summary>
        private static void PairFromLevelAndBoost(double level01, double boost01, double lateralSign,
            out double left01, out double right01)
        {
            double signed = boost01 * lateralSign;
            left01 = level01 - signed;
            right01 = level01 + signed;
        }

        /// <summary>Standard, frame-rate-independent exponential smoothing (unchanged from the previous
        /// pass).</summary>
        private static double ExponentialSmooth(double previous, double target, double dtSeconds, double tauSeconds)
        {
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0 || !ClampMath.IsFinite(target)) return previous;
            if (!(tauSeconds > 1e-6)) return target;

            double alpha = 1.0 - Math.Exp(-dtSeconds / tauSeconds);
            return previous + alpha * (target - previous);
        }

        /// <summary>Standard dt-correct exponential decay of <paramref name="previous"/> toward zero -
        /// mirrors <see cref="Normalized.NormalizedWheelLockSlipEngine"/>'s own identically-named
        /// helper.</summary>
        private static double ExponentialDecayToZero(double previous, double dtSeconds, double tauSeconds)
        {
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0) return previous;
            double alpha = 1.0 - Math.Exp(-dtSeconds / tauSeconds);
            return previous - alpha * previous;
        }

        /// <summary>
        /// Degraded fallback for when <see cref="ITelemetryFrame.LongitudinalG"/> is unavailable but
        /// <see cref="ITelemetryFrame.LateralG"/> is not - unchanged from the previous pass.
        /// </summary>
        private GForceOutput ComputeLateralOnlyFallback(double lateralG, double shakeContribution = 0.0,
            double latMaxG = 1.5)
        {
            // With no longitudinal G at all, the friction circle degenerates to `combined == rLat` and
            // every channel's level is 0 - so the whole output IS the lateral split. The active chain's
            // splits still decide the shape (owner's decision: keep the LAST chain), and this path writes
            // one pair to all eight pads, so the terminal split is the representative one.
            double safeLatMax = latMaxG > 1e-6 ? latMaxG : 1e-6;
            double signedLatRatio = ApplyLateralDirection(ClampMath.Clamp(lateralG / safeLatMax, -1.0, 1.0));
            double headroom = Math.Abs(signedLatRatio) * ClampMath.To01(LateralOutputScale);
            double split = ClampMath.To01(_lastChainWasBraking ? BrakeBottomFrontLatSplit : AccelBackTopLatSplit);

            PairFromLevelAndBoost(0.0, headroom * split, signedLatRatio >= 0.0 ? 1.0 : -1.0,
                out double baseL, out double baseR);

            double left, right;
            if (shakeContribution > 0.0 && UsesLockSlipWave)
            {
                // The wheel-driven wave ignores G-force by definition, so it still applies on this
                // degraded path - and still without lateral bias. HigherOfGForceOrLockSlip reduces to
                // exactly this here too: with no usable G-force there is no other half to be higher
                // than. The remaining modes coincide anyway, since this fallback already writes one
                // value to all eight pads.
                GForceShake.ApplyLockSlipOnly(
                    shakeContribution, ShakeFrequencyHz, _shakePhaseSeconds, EffectiveShakeHold,
                    ShakeFeeling, out left, out right, _shakeReversed);
                left = ClampMath.To0100(left);
                right = ClampMath.To0100(right);
            }
            else if (shakeContribution > 0.0)
            {
                ShakePair(baseL, baseR, shakeContribution, out left, out right);
            }
            else
            {
                // Silent: publish the split as-is. ShakePair would collapse it, since it deliberately
                // takes the STRONGER side for both pads.
                left = ClampMath.To0100(baseL * 100.0);
                right = ClampMath.To0100(baseR * 100.0);
            }

            _lastLeftOut = left;
            _lastRightOut = right;

            return new GForceOutput(
                bottomFrontLeft: left, bottomFrontRight: right,
                bottomRearLeft: left, bottomRearRight: right,
                backLowLeft: left, backLowRight: right,
                backTopLeft: left, backTopRight: right);
        }

        private double ApplyLateralDirection(double signedBias)
            => LateralDirection == LateralDirectionMode.Reversed ? -signedBias : signedBias;
    }
}

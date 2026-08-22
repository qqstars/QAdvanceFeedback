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
        public double TransientGain { get; set; } = 1.2;

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

        /// <summary>How far a fully-saturated lateral bias pushes the left/right split apart.</summary>
        public double LateralBiasGain { get; set; } = 0.5;

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

        // ------------------------------------------------------------------------------------
        // STAGED TRAVEL - new state (docs\lock-and-animation-report.md). Two independent tracks per
        // chain: the sustain LEVEL (low-pass, unchanged mechanism) and the STAGE PROGRESS (0-1, new).
        // ------------------------------------------------------------------------------------

        private double _brakeSustainLevel;
        private double _brakeStageProgress;
        private double _brakeTravelRate;

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
            double? accelTransitionScale = null, double? decelTransitionScale = null)
        {
            if (sample == null) return GForceOutput.Empty;

            double effectiveAccelTransitionScale = accelTransitionScale ?? TransitionAnimationScale;
            double effectiveDecelTransitionScale = decelTransitionScale ?? TransitionAnimationScale;

            double dtSeconds = sample.Dt.HasValue && sample.Dt.Value.TotalSeconds > 0.0 ? sample.Dt.Value.TotalSeconds : 0.0;

            LongitudinalMotionState direction = _direction.Resolve(sample);

            if (IntegrateWheelLockAndSlip)
            {
                if (!_shakeActive) { _shakeActive = true; _shakePhaseSeconds = 0.0; }
                else if (ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0) { _shakePhaseSeconds += dtSeconds; }
            }
            else
            {
                _shakeActive = false;
                _shakePhaseSeconds = 0.0;
            }

            double lockContribution = WheelLockShakeScale * (ClampMath.To0100(wheelLockAll0100) / 100.0);
            double slipContribution = WheelSlipShakeScale * (ClampMath.To0100(wheelSlipAll0100) / 100.0);
            double shakeContribution = IntegrateWheelLockAndSlip ? Math.Max(lockContribution, slipContribution) : 0.0;

            double? longG = sample.New?.LongitudinalG;
            double? lateralGForFallback = sample.New?.LateralG;
            if (!longG.HasValue)
            {
                return lateralGForFallback.HasValue
                    ? ComputeLateralOnlyFallback(lateralGForFallback.Value, shakeContribution)
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
                dtSeconds, rBrake, decelChainActive, ref _brakePreviousRatio, ref _brakeTravelRate, ref _brakeStageProgress);
            double accelProgress = AdvanceStageProgress(
                dtSeconds, rAccel, accelChainActive, ref _accelPreviousRatio, ref _accelTravelRate, ref _accelStageProgress);

            // ---- Braking chain: far=BackLow, mid=BottomRear, terminal=BottomFront.
            StagedShape(brakeProgress, ClampMath.To01(BrakeBottomRearSustainFraction), ClampMath.To01(BrakeBackLowSustainFraction),
                effectiveDecelTransitionScale,
                out double brakeFarShape, out double brakeMidShape, out double brakeTerminalShape);
            double brakeBackLowSustained = brakeSustained * brakeFarShape;
            double brakeBottomRearSustained = brakeSustained * brakeMidShape;
            double brakeBottomFrontSustained = brakeSustained * brakeTerminalShape;

            // ---- Acceleration chain: far=BottomRear, mid=BackLow, terminal=BackTop.
            StagedShape(accelProgress, ClampMath.To01(AccelBackLowSustainFraction), ClampMath.To01(AccelBottomRearSustainFraction),
                effectiveAccelTransitionScale,
                out double accelFarShape, out double accelMidShape, out double accelTerminalShape);
            double accelBottomRearSustained = accelSustained * accelFarShape;
            double accelBackLowSustained = accelSustained * accelMidShape;
            double accelBackTopSustained = accelSustained * accelTerminalShape;

            // Bottom Rear and Back Low are shared between the two chains; brake and accel energy can
            // never both be non-zero for the same frame (mutually exclusive by direction), so a plain
            // sum is safe.
            double bottomFrontLevel = ClampMath.To01(brakeBottomFrontSustained);
            double bottomRearLevel = ClampMath.To01(brakeBottomRearSustained + accelBottomRearSustained);
            double backLowLevel = ClampMath.To01(brakeBackLowSustained + accelBackLowSustained);
            double backTopLevel = ClampMath.To01(accelBackTopSustained);

            // ---- Lateral left/right bias - unchanged, independent of the longitudinal logic above.
            double? lateralG = sample.New?.LateralG;
            double lateralBias = 0.0;
            if (lateralG.HasValue)
            {
                double safeLatMax = LateralReferenceG > 1e-6 ? LateralReferenceG : 1e-6;
                lateralBias = ApplyLateralDirection(ClampMath.Clamp(lateralG.Value / safeLatMax, -1.0, 1.0));
            }

            double leftFactor = 1.0 - LateralBiasGain * lateralBias;
            double rightFactor = 1.0 + LateralBiasGain * lateralBias;

            ShakePadPair(bottomFrontLevel * 100.0, leftFactor, rightFactor, shakeContribution, out double bottomFrontLeft, out double bottomFrontRight);
            ShakePadPair(bottomRearLevel * 100.0, leftFactor, rightFactor, shakeContribution, out double bottomRearLeft, out double bottomRearRight);
            ShakePadPair(backLowLevel * 100.0, leftFactor, rightFactor, shakeContribution, out double backLowLeft, out double backLowRight);
            ShakePadPair(backTopLevel * 100.0, leftFactor, rightFactor, shakeContribution, out double backTopLeft, out double backTopRight);

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
            ref double previousRatio, ref double travelRate, ref double stageProgress)
        {
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
            double deltaRatio = Math.Abs(clampedRatio - previousRatio);
            previousRatio = clampedRatio;

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
        private static void StagedShape(double progress, double midFraction, double lowFraction, double peak, out double farValue, out double midValue, out double terminalValue)
        {
            const double high = 1.0; // TRUE, scale-independent terminal ceiling - NEVER replaced by peak.
            double mid = midFraction;
            double low = lowFraction;

            // Keyframe 0 (stage 1): far=PEAK, mid=LOW, terminal=LOW.
            // Keyframe 1 (stage 2): far=MID,  mid=PEAK, terminal=LOW.
            // Keyframe 2 (stage 3 = sustain): far=LOW, mid=MID, terminal=HIGH (unscaled).
            double p = ClampMath.To01(progress);

            if (p <= 0.5)
            {
                double t = p / 0.5;
                farValue = peak + (mid - peak) * t;
                midValue = low + (peak - low) * t;
                terminalValue = low; // unchanged across stage 0->1 (LOW at both keyframes)
            }
            else
            {
                double t = (p - 0.5) / 0.5;
                farValue = mid + (low - mid) * t;   // unaffected by peak - far's own transit peak already passed at p<=0.5.
                midValue = peak + (mid - peak) * t;
                terminalValue = low + (high - low) * t; // always the TRUE high=1.0 - never peak.
            }
        }

        private void ShakePadPair(
            double baseLevel0100, double leftFactor, double rightFactor, double shakeContribution,
            out double left, out double right)
        {
            double centreL, centreR;
            if (shakeContribution > 0.0)
            {
                GForceShake.Apply(baseLevel0100, shakeContribution, ShakeFrequencyHz, _shakePhaseSeconds, out centreL, out centreR);
            }
            else
            {
                centreL = baseLevel0100;
                centreR = baseLevel0100;
            }

            left = ClampMath.To0100(centreL * leftFactor);
            right = ClampMath.To0100(centreR * rightFactor);
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
        private GForceOutput ComputeLateralOnlyFallback(double lateralG, double shakeContribution = 0.0)
        {
            double safeLatMax = LateralReferenceG > 1e-6 ? LateralReferenceG : 1e-6;
            double magnitudeRatio = ClampMath.To01(Math.Abs(lateralG) / safeLatMax);

            double lateralBias = ApplyLateralDirection(ClampMath.Clamp(lateralG / safeLatMax, -1.0, 1.0));

            double leftFactor = 1.0 - LateralBiasGain * lateralBias;
            double rightFactor = 1.0 + LateralBiasGain * lateralBias;

            ShakePadPair(magnitudeRatio * 100.0, leftFactor, rightFactor, shakeContribution, out double left, out double right);

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

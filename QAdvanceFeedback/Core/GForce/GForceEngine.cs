using System;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>Normal: positive LateralG biases the Right pads (see
    /// <see cref="GForceEngine.LateralDirection"/>'s remarks for the full physical convention this
    /// corresponds to). Reversed: the driver's own preference to feel the mirror image.</summary>
    public enum LateralDirectionMode { Normal, Reversed }

    /// <summary>
    /// The G-force "washout" model - a classical motion-cueing structure (as used by SimTools/FlyPT
    /// Mover/SFX-100-style 4-axis rigs, adapted here for a fixed set of 8 per-pad VOLUME channels
    /// rather than actuator position) that separates LEVEL from MOTION:
    /// <list type="bullet">
    /// <item>a low-pass ("sustained") path that tracks the steady-state G level and drives the
    /// existing hat+sustain-floor spatial distribution - this REPLACES the old single "travel"
    /// parameter, which conflated level and motion into one number.</item>
    /// <item>a high-pass ("transient") path - the classical washout complement of the same low-pass
    /// filter (transient = raw - lowpass(raw)) - which responds to the RATE at which G is changing and
    /// decays ("washes out") back to zero once the input holds steady, returning every pad to its
    /// sustained distribution and freeing up headroom for the next onset.</item>
    /// </list>
    /// See <see cref="Compute"/>'s own remarks for the full per-frame sequence, and
    /// docs\wiring-ui-report.md for the six acceptance scenarios this structure was built against and
    /// the reasoning behind the chosen time constants/gain.
    /// <para/>
    /// DIRECTION FIX (docs\gforce-direction-fix-report.md - read this before touching the brake/accel
    /// split again): this class used to derive WHICH chain is active from
    /// <see cref="ITelemetryFrame.LongitudinalG"/>'s own sign (positive = accelerating, negative =
    /// braking) - flagged, at the time, as an unverified assumption. It was backwards for at least one
    /// title: this exact codebase's <see cref="NormalizedWheelLockSlipEngine"/> already documents, as
    /// an established finding, that Forza Horizon 6 reports the OPPOSITE convention (positive while
    /// genuinely slowing, in 95.8% of qualifying frames) - which is why the Lock/Slip engine moved
    /// direction off <see cref="ITelemetryFrame.LongitudinalG"/>'s sign entirely, years before this
    /// class was. Direct log evidence (2,612-frame Forza Horizon 6 session) confirmed the same
    /// inversion here: under measured <see cref="LongitudinalMotionState.SpeedingUp"/> (real,
    /// ground-speed-confirmed acceleration), the OLD sign-based code drove Bottom Front (the BRAKING
    /// chain's own terminal pad) to a mean of 76/100 while Back Top (the accelerating chain's
    /// terminal) sat at 0.55/100 - and the mirror-image swap under measured Slowing - exactly matching
    /// the driver's own complaint ("Bottom Front shaking under acceleration").
    /// <para/>
    /// THE FIX: DIRECTION and MAGNITUDE are now two separate signals, exactly like
    /// <see cref="NormalizedWheelLockSlipEngine"/> already treats them. This class now owns its own
    /// <see cref="LongitudinalDirectionResolver"/> (constructor-injectable, mirroring that engine's own
    /// DI pattern) and asks it, EVERY frame, which way the car is measurably going (from differentiated
    /// ground speed - sign-agnostic by construction, needing no per-game telemetry convention to be
    /// trusted). <see cref="ITelemetryFrame.LongitudinalG"/>'s own sign is NEVER read for this decision
    /// any more - only its MAGNITUDE (<c>Math.Abs</c>) is used, attributed to whichever axis
    /// <see cref="LongitudinalDirectionResolver"/> measured this frame. When direction is
    /// <see cref="LongitudinalMotionState.Unknown"/> (no derivative yet, or speed changing by less than
    /// the resolver's own dead band - i.e. a genuine standstill or steady cruise) NEITHER axis gets a
    /// non-zero reading - this is also the direct fix for the driver's second complaint ("Bottom Rear
    /// shaking slightly while stopped"): a stationary car has nothing honest to attribute
    /// <see cref="ITelemetryFrame.LongitudinalG"/>'s own sensor noise to, so both chains now correctly
    /// read (or decay toward) zero instead of the old sign-flicker occasionally lighting up whichever
    /// axis the noise happened to point at.
    /// <para/>
    /// <see cref="ITelemetryFrame.LateralG"/> (SimHub AccelerationSway) is still assumed positive =
    /// biases toward the Right pads under <see cref="LateralDirectionMode.Normal"/> (see
    /// <see cref="LateralDirection"/> for the owner's driver-facing toggle over this) - this sign is
    /// NOT part of the fix above (lateral bias is independent of the longitudinal chain-selection logic
    /// that was actually wrong), and remains driver-adjustable without a code change.
    /// <para/>
    /// STATEFUL, UNLIKE THE OLD MODEL: this class now holds per-chain filter state (the low-pass
    /// level and the resulting transient) across calls, since a washout structure is inherently a
    /// function of TIME, not just the current instant. <see cref="Reset"/> clears it - callers should
    /// call this on a session/game/car switch, mirroring every other stateful engine in this plugin
    /// (see <c>QAdvanceFeedback.cs</c>'s own reset hooks).
    /// </summary>
    public sealed class GForceEngine
    {
        /// <summary>
        /// How quickly the SUSTAINED path's low-pass filter tracks a new steady-state G level, in
        /// seconds. 0.15s was chosen (and verified numerically - see docs\wiring-ui-report.md for the
        /// simulation) so that the S2 acceptance scenario's own stated hold duration (~0.5s, more than
        /// 3 time constants) is long enough for the sustained level to fully settle and the transient
        /// to wash out to a negligible residual, while still being slow enough, relative to a single
        /// frame, for a fast step (S3/S5) to produce a clearly visible transient in the frame or two
        /// immediately following it.
        /// </summary>
        public double SustainTimeConstantSeconds { get; set; } = 0.15;

        /// <summary>
        /// An additional smoothing pass applied to the raw transient residual (raw ratio minus the
        /// sustained low-pass), in seconds - shorter than <see cref="SustainTimeConstantSeconds"/> so
        /// the transient itself still feels snappy/immediate rather than doubly-lagged, while
        /// rejecting frame-to-frame telemetry jitter that would otherwise make the transient feel
        /// twitchy. 0.08s (roughly half of the sustain constant, and - like that constant - verified
        /// numerically against the S2 scenario's 0.5s hold) keeps the transient itself decaying
        /// comfortably faster than the sustained path settles.
        /// </summary>
        public double TransientTimeConstantSeconds { get; set; } = 0.08;

        /// <summary>
        /// Scales the (already filtered) transient residual before it is applied, headroom-scaled, to
        /// every pad - see <see cref="Compute"/>'s remarks. 1.5 was chosen, together with the two time
        /// constants above, so that a fast, large step (S3/S5) produces a clearly visible, obviously
        /// larger transient than a gentle multi-second onset (S1) at the same underlying magnitude
        /// change - verified numerically (see docs\wiring-ui-report.md): a 1g-to-2g step gives roughly
        /// double the peak transient of the same 1g change spread gently over 3 seconds.
        /// </summary>
        public double TransientGain { get; set; } = 1.5;

        /// <summary>
        /// RE-DERIVED (this fraction and the three siblings below - read this once, it applies to all
        /// four; SUPERSEDES an earlier, flawed derivation that produced a flat 50% for every zone
        /// regardless of chain position - see "why the old derivation was wrong" at the end of this
        /// remark).
        /// <para/>
        /// THE MODEL'S ACTUAL TERMINAL WEIGHTS: at full saturation (the sustained ratio at or beyond
        /// 1.0), the terminal (leading) zone's own hat is exactly 1.0 by construction - it needs no
        /// floor at all. Each TRAILING zone's sustain floor is derived from the chain's own topology by
        /// halving per hop of "distance" from that terminal zone - the natural, simplest decay
        /// consistent with a 3-zone chain that has exactly one terminal and two trailing zones:
        /// <list type="bullet">
        /// <item>distance 0 (the terminal zone itself, e.g. Bottom Front for braking): 100% - not a
        /// setting, just the hat's own value.</item>
        /// <item>distance 1 (the MIDDLE zone, e.g. Bottom Rear for braking): 50% of the terminal's
        /// weight.</item>
        /// <item>distance 2 (the FAR zone, e.g. Back Low for braking): 50% of the middle zone's weight
        /// = 25% of the terminal's.</item>
        /// </list>
        /// This produces a genuine SPATIAL GRADIENT the driver can feel WHERE the sensation is
        /// (leading pad near full, middle pad noticeably lower, far pad lower still - the owner's own
        /// suggested 25%/50% shape) - and, for a middle-zone floor of 50%, still leaves 50% headroom
        /// for the transient/washout path to express change, exactly as before.
        /// <para/>
        /// WHY THE OLD DERIVATION WAS WRONG: it computed "the value of a trailing zone's OWN hat at
        /// the midpoint of its own decay ramp", which is a SELF-referential calculation that returns
        /// exactly 0.5 for EVERY zone in this symmetric triangular family regardless of which zone it
        /// is or how far from the terminal it sits - it was never actually a function of chain
        /// position at all, which is why every one of the four sustain floors ended up identical (the
        /// flat-50%-everywhere symptom the owner flagged from the screenshots). The corrected
        /// derivation above is instead a function of DISTANCE FROM THE TERMINAL ZONE, which is what
        /// "derive from the model's own terminal weights" actually requires.
        /// <para/>
        /// Braking chain: Back Low (distance 2, far) -&gt; Bottom Rear (distance 1, middle) -&gt;
        /// Bottom Front (distance 0, terminal). This property is Bottom Rear's (the MIDDLE zone) -
        /// default 0.5 (unchanged from before, since it was already the correct distance-1 value).
        /// </summary>
        public double BrakeBottomRearSustainFraction { get; set; } = 0.5;

        /// <summary>Braking's Back Low sustain - the FAR zone (distance 2 from the terminal Bottom
        /// Front) - default 0.25 (corrected from the old, flawed 0.5 - see
        /// <see cref="BrakeBottomRearSustainFraction"/>'s remarks for the full derivation).</summary>
        public double BrakeBackLowSustainFraction { get; set; } = 0.25;

        /// <summary>Acceleration chain: Bottom Rear (distance 2, far) -&gt; Back Low (distance 1,
        /// middle) -&gt; Back Top (distance 0, terminal). This property is Bottom Rear's (the FAR
        /// zone) - default 0.25 (corrected from the old, flawed 0.5 - see
        /// <see cref="BrakeBottomRearSustainFraction"/>'s remarks for the full derivation).</summary>
        public double AccelBottomRearSustainFraction { get; set; } = 0.25;

        /// <summary>Acceleration's Back Low sustain - the MIDDLE zone (distance 1 from the terminal
        /// Back Top), per the brief's explicit "Back Low should keep vibrating (just less strongly)"
        /// requirement - default 0.5 (unchanged from before, since it was already the correct
        /// distance-1 value - see <see cref="BrakeBottomRearSustainFraction"/>'s remarks).</summary>
        public double AccelBackLowSustainFraction { get; set; } = 0.5;

        /// <summary>The lateral-G magnitude treated as "full scale" for the left/right bias. 1.6g is a
        /// reasonable fixed reference covering everything from road cars to GT3-class content.</summary>
        public double LateralReferenceG { get; set; } = 1.6;

        /// <summary>
        /// LIVE-PATH-ONLY plausibility clamp on LongitudinalG's own magnitude, applied BEFORE the
        /// direction-based brake/accel split (docs\gforce-direction-fix-report.md - REJECT and CLAMP
        /// are different needs, per the owner's own explicit ask): the LEARNING path (see
        /// <see cref="GForceMaxLearner.LearnCapG"/>/<see cref="Settings.GForceSettings.AccelLearnMaxPlausibleG"/>/
        /// <see cref="Settings.GForceSettings.DecelLearnMaxPlausibleG"/>) REJECTS an impact-magnitude
        /// reading outright, since one bad sample would otherwise become the persistent normalisation
        /// reference for every subsequent frame. This LIVE path must NOT do the same thing - dropping
        /// the frame (or freezing the output) during a crash would feel exactly like the plugin
        /// hanging; instead the magnitude is CLAMPED here to a large-but-finite value, producing a
        /// real, saturated, in-range cue for the impact frame that recovers immediately once ordinary
        /// readings resume. 15g is deliberately HIGHER than either learning cap (6g/8g - see those
        /// constants' own remarks for the real-world peak data this is derived from) so it never
        /// clips a genuinely extreme but real event, while remaining far below a genuine wall-impact
        /// spike (this plugin's own captured session showed a ~19.8g-equivalent collision reading) -
        /// high enough to be a true "this is not real driving" backstop, not a everyday ceiling.
        /// </summary>
        public const double LiveMagnitudeClampG = 15.0;

        /// <summary>How far a fully-saturated lateral bias pushes the left/right split apart. 0.5
        /// means the "loaded" side gets up to 1.5x its unbiased value and the "unloaded" side down to
        /// 0.5x.</summary>
        public double LateralBiasGain { get; set; } = 0.5;

        /// <summary>
        /// The owner's driver-facing lateral direction toggle. <see cref="LateralDirectionMode.Normal"/>
        /// (the default, unchanged pre-existing behaviour) corresponds to: turning LEFT produces a
        /// positive <see cref="ITelemetryFrame.LateralG"/> reading (the car's own reaction to cornering
        /// loads the RIGHT side), so the vibration travels to, and settles on, the RIGHT pads while
        /// turning left. <see cref="LateralDirectionMode.Reversed"/> mirrors BOTH the travelling
        /// transition and the settled steady-state bias (there is only ONE bias computation, applied
        /// uniformly every frame, so negating it mirrors both by construction).
        /// </summary>
        public LateralDirectionMode LateralDirection { get; set; } = LateralDirectionMode.Normal;

        // ---- Owner-requested "Integrate Wheel Lock and Slip" shake (see GForceShake) - OFF by
        // default (see GForceSettings.IntegrateWheelLockAndSlip's own remarks on why).
        public bool IntegrateWheelLockAndSlip { get; set; } = false;

        private double _shakeFrequencyHz = GForceShake.MinFrequencyHz;

        /// <summary>Hz, clamped to [<see cref="GForceShake.MinFrequencyHz"/>, <see cref="GForceShake.MaxFrequencyHz"/>]
        /// in the setter itself (not only a UI spinner range) - a hand-edited config file cannot smuggle
        /// in a frequency outside the owner's stated 5-20 Hz band.</summary>
        public double ShakeFrequencyHz
        {
            get => _shakeFrequencyHz;
            set => _shakeFrequencyHz = ClampMath.Clamp(value, GForceShake.MinFrequencyHz, GForceShake.MaxFrequencyHz);
        }

        private double _wheelLockShakeScale = 1.0;

        /// <summary>Non-negative, clamped in the setter.</summary>
        public double WheelLockShakeScale
        {
            get => _wheelLockShakeScale;
            set => _wheelLockShakeScale = value >= 0.0 ? value : 0.0;
        }

        private double _wheelSlipShakeScale = 1.0;

        /// <summary>Non-negative, clamped in the setter.</summary>
        public double WheelSlipShakeScale
        {
            get => _wheelSlipShakeScale;
            set => _wheelSlipShakeScale = value >= 0.0 ? value : 0.0;
        }

        // The shake's own "clock" - advanced from frame dt (never wall-clock, see GForceShake's own
        // remarks), so it is unit-testable with synthetic dt and survives no faster/slower than the
        // rest of this engine's per-frame maths. Mirrors PulseGenerator's own "freshly active starts at
        // t=0, THEN advances by dt on every subsequent frame" convention exactly (see Compute's own
        // remarks) - this is what guarantees the very first frame after (re)activation always has
        // sin(0)==0, i.e. output==centre, so the transition into shaking is continuous.
        private bool _shakeActive;
        private double _shakePhaseSeconds;

        // ---- Washout filter state - see this class's own remarks on why it is now stateful.
        private double _brakeSustainRatio;
        private double _brakeTransient;
        private double _accelSustainRatio;
        private double _accelTransient;

        // ---- DIRECTION FIX - see this class's own remarks. Owns its own resolver instance, exactly
        // like NormalizedWheelLockSlipEngine does, so it needs no shared/singleton state with that
        // engine - both resolvers, fed the same per-frame sample sequence, converge on identical
        // answers by construction (the same deterministic maths over the same inputs).
        private readonly LongitudinalDirectionResolver _direction;

        public GForceEngine() : this(null) { }

        /// <param name="directionResolver">Constructor-injectable for tests that need to observe/
        /// control the resolver directly; defaults to a fresh instance, mirroring
        /// <see cref="NormalizedWheelLockSlipEngine"/>'s own DI pattern.</param>
        public GForceEngine(LongitudinalDirectionResolver directionResolver)
        {
            _direction = directionResolver ?? new LongitudinalDirectionResolver();
        }

        /// <summary>The most recently resolved direction - exposed for diagnostics and so the plugin
        /// composition root can attribute an AUTO-mode learner observation to the SAME axis this
        /// frame's chain selection used, rather than re-deriving (or mis-deriving) it separately - see
        /// <c>QAdvanceFeedback.cs</c>'s own remarks. Mirrors
        /// <see cref="NormalizedWheelLockSlipEngine.CurrentDirection"/>.</summary>
        public LongitudinalMotionState CurrentDirection => _direction.State;

        /// <summary>Clears all washout filter state back to zero - call on a session/game/car switch
        /// so a fresh session does not inherit a stale sustained level or an in-flight transient from
        /// whatever the car was doing a moment before the switch. Also clears the direction resolver's
        /// own filter (see this class's own remarks) so a fresh session's first frame is not compared
        /// against stale speed history from whatever the previous game/car was doing.</summary>
        public void Reset()
        {
            _brakeSustainRatio = 0.0;
            _brakeTransient = 0.0;
            _accelSustainRatio = 0.0;
            _accelTransient = 0.0;
            _shakeActive = false;
            _shakePhaseSeconds = 0.0;
            _direction.Reset();
        }

        /// <summary>
        /// Computes this frame's 8 pad values.
        /// <para/>
        /// PER-FRAME SEQUENCE (per chain - braking and accelerating are independent, non-negative
        /// signals, since a single frame's LongitudinalG can only be braking OR accelerating, never
        /// both):
        /// <list type="number">
        /// <item>Compute the raw, UNCLAMPED ratio r = magnitude / maxG (can exceed 1 when over the
        /// configured maximum - deliberately not clamped yet, so the transient below still sees the
        /// true excursion even while already saturated - see the S5 acceptance scenario).</item>
        /// <item>Update the SUSTAINED low-pass filter toward r, with time constant
        /// <see cref="SustainTimeConstantSeconds"/>. The published "level" for this chain is
        /// clamp01(this filter's value).</item>
        /// <item>The raw transient residual = r - (the just-updated, UNCLAMPED sustain filter value) -
        /// the classical washout high-pass, which is exactly zero once the sustain filter has caught
        /// up to a steady r (see the S2 acceptance scenario), and large immediately after a sudden
        /// change since the low-pass necessarily lags a step (S3/S5).</item>
        /// <item>That residual is itself smoothed by a second low-pass,
        /// <see cref="TransientTimeConstantSeconds"/>, then scaled by <see cref="TransientGain"/> - the
        /// signed "transient drive" for this chain this frame. Positive = onset (G increasing toward
        /// this chain's own maximum); negative = easing off.</item>
        /// <item>The SUSTAINED spatial distribution is computed with the existing hat+sustain-floor
        /// logic (unchanged mechanism from before - see the sustain-fraction properties' own remarks),
        /// using the clamped sustain ratio as the position parameter directly (no separate rate-boosted
        /// "travel" any more - that concept is retired; motion is now the transient path's job
        /// entirely).</item>
        /// <item>The transient drive is applied to EVERY pad in the chain (leading AND trailing alike -
        /// deliberately uniform, not a leading-vs-trailing redistribution, per the S5 acceptance
        /// scenario's own worked numbers) via <see cref="ApplyTransient"/>: HEADROOM-scaled so it can
        /// never push a pad outside [0,1], and so it still has somewhere to go even when the sustained
        /// path is already fully saturated (S4/S5) - "spending the headroom above the sustain floors".</item>
        /// </list>
        /// Bottom Rear and Back Low are shared between the two chains; their final value is the SUM of
        /// both chains' (already headroom-applied) contributions, safe because brake and accel energy
        /// can never both be non-zero for the same frame. Lateral G is applied last, as before.
        /// </summary>
        /// <param name="sample">The two-frame telemetry sample - <see cref="ITelemetrySample.Dt"/>
        /// drives the washout filters' time-correct exponential smoothing; if it is unavailable or
        /// non-positive (e.g. the very first sample of a session), the filters simply hold their
        /// current state for that one frame rather than advancing incorrectly.</param>
        /// <param name="accelMaxG">The max-G reference used to normalise acceleration - the caller
        /// (GForceSettings.EffectiveAccelMaxG) resolves FIXED vs AUTO before calling in.</param>
        /// <param name="decelMaxG">The max-G reference used to normalise braking/deceleration.</param>
        /// <param name="wheelLockAll0100">This frame's published <c>WheelLock.Projected.All</c> value
        /// (0-100) - defaults to 0 (no contribution) so every pre-existing call site (and all 19
        /// pre-existing tests) compiles and behaves identically without passing it. See
        /// <see cref="IntegrateWheelLockAndSlip"/>'s own remarks for how this and
        /// <paramref name="wheelSlipAll0100"/> combine into the shake's single "contribution" value.</param>
        /// <param name="wheelSlipAll0100">This frame's published <c>WheelSlip.Projected.All</c> value
        /// (0-100). See <paramref name="wheelLockAll0100"/>'s remarks.</param>
        public GForceOutput Compute(
            ITelemetrySample sample, double accelMaxG, double decelMaxG,
            double wheelLockAll0100 = 0.0, double wheelSlipAll0100 = 0.0)
        {
            if (sample == null) return GForceOutput.Empty;

            double dtSeconds = sample.Dt.HasValue && sample.Dt.Value.TotalSeconds > 0.0 ? sample.Dt.Value.TotalSeconds : 0.0;

            // DIRECTION FIX (see this class's own remarks) - resolved UNCONDITIONALLY, every frame,
            // exactly like NormalizedWheelLockSlipEngine.Compute's own unconditional call, so the
            // resolver's internal smoothing filter stays continuously up to date regardless of whether
            // LongitudinalG itself happens to be available this particular frame.
            LongitudinalMotionState direction = _direction.Resolve(sample);

            // The shake's own clock only advances while the feature is actually enabled - re-entering
            // it always starts THIS frame at t=0 (sin(0)=0, output==centre) and only advances by dt
            // from the SECOND active frame onward - the same "freshly active starts at t=0, then
            // advances" convention PulseGenerator already uses (see PulseGenerator.Advance). This is
            // what guarantees switching the checkbox on mid-session begins smoothly from the current
            // G-force value (no jump into the middle of an already-running wave) rather than merely
            // "eventually" being continuous because band happens to be 0.
            if (IntegrateWheelLockAndSlip)
            {
                if (!_shakeActive)
                {
                    _shakeActive = true;
                    _shakePhaseSeconds = 0.0;
                }
                else if (ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0)
                {
                    _shakePhaseSeconds += dtSeconds;
                }
            }
            else
            {
                _shakeActive = false;
                _shakePhaseSeconds = 0.0;
            }

            // Lock/Slip combine into ONE non-negative "contribution" per the owner's explicit choice -
            // the LARGER of the two scaled contributions, not their sum, so a brief overlap (both
            // channels non-zero for a frame or two) cannot double the shake amplitude. This is a
            // judgment call on an otherwise-unspecified interaction - flagged in
            // docs\shake-and-toggle-report.md for the owner to revisit if summing is actually wanted.
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

            // DIRECTION FIX (see this class's own remarks): magnitude comes from LongitudinalG
            // (Math.Abs - sign-agnostic), direction comes ONLY from the resolver above, NEVER from
            // LongitudinalG's own sign. Unknown (standstill / within the dead band / no derivative
            // yet) -> both zero - "the standstill gate" (see MUTATION (b) below).
            //
            // MUTATION (a) in the report: replace `direction`-based attribution with the OLD
            // `Math.Max(0.0, -longG.Value)` / `Math.Max(0.0, longG.Value)` sign-based split - a
            // dedicated test using the INVERTED longitudinal convention (ground speed says one thing,
            // LongitudinalG's sign says the opposite) must fail, since the sign-based split would then
            // drive the wrong chain.
            // CLAMP, not reject (see LiveMagnitudeClampG's own remarks) - MUTATION (a) in the report:
            // reject/hold the previous output instead of clamping and proceeding - a dedicated test
            // (an impact-magnitude frame must still produce a real, saturated, in-range, DIFFERENT
            // reading from whatever preceded it, and recover the following frame) must fail.
            double magnitude = Math.Min(Math.Abs(longG.Value), LiveMagnitudeClampG);

            // MUTATION (b) in the report: change the Unknown branch below from 0.0 to a raw-sign
            // fallback (e.g. `Math.Max(0.0, -longG.Value)` for brake) - a dedicated genuine-standstill
            // test (direction Unknown, small sensor-noise-scale LongitudinalG) must then fail, since
            // the mutated code would let a nonzero reading leak through instead of gating to zero.
            double brakeG = direction == LongitudinalMotionState.Slowing ? magnitude : 0.0;
            double accelG = direction == LongitudinalMotionState.SpeedingUp ? magnitude : 0.0;

            double safeDecelMax = decelMaxG > 1e-6 ? decelMaxG : 1e-6;
            double safeAccelMax = accelMaxG > 1e-6 ? accelMaxG : 1e-6;

            double rBrake = brakeG / safeDecelMax;   // unclamped - see this method's remarks
            double rAccel = accelG / safeAccelMax;

            double brakeTransientDrive = AdvanceWashout(dtSeconds, rBrake, ref _brakeSustainRatio, ref _brakeTransient);
            double accelTransientDrive = AdvanceWashout(dtSeconds, rAccel, ref _accelSustainRatio, ref _accelTransient);

            double brakeSustained = ClampMath.To01(_brakeSustainRatio);
            double accelSustained = ClampMath.To01(_accelSustainRatio);

            // ---- Sustained spatial distribution: partition-of-unity "hat" weights (continuous by
            // construction) plus each non-terminal zone's configurable sustain floor (Max of two
            // continuous functions is itself always continuous).
            // Braking axis: Back Low (r=0) -> Bottom Rear (r=0.5) -> Bottom Front (r=1).
            double brakeBackLowHat = ClampMath.To01(1.0 - 2.0 * brakeSustained);
            double brakeBottomRearHat = 1.0 - Math.Abs(2.0 * brakeSustained - 1.0);
            double brakeBottomFrontHat = ClampMath.To01(2.0 * brakeSustained - 1.0);

            // Weight (position, 0-1) combined with the sustain floor FIRST, then scaled by energy
            // (brakeSustained itself) - energy is what keeps a near-zero G reading published near-zero
            // regardless of which zone's hat happens to be large there (e.g. Back Low's hat is ~1.0
            // near r=0, but the actual felt magnitude at r=0.05 must still read as barely-there, not
            // nearly full strength).
            double brakeBackLowWeight = Math.Max(brakeBackLowHat, ClampMath.To01(BrakeBackLowSustainFraction) * brakeBottomFrontHat);
            double brakeBottomRearWeight = Math.Max(brakeBottomRearHat, ClampMath.To01(BrakeBottomRearSustainFraction) * brakeBottomFrontHat);

            double brakeBackLowSustained = brakeSustained * brakeBackLowWeight;
            double brakeBottomRearSustained = brakeSustained * brakeBottomRearWeight;
            double brakeBottomFrontSustained = brakeSustained * brakeBottomFrontHat;

            // Acceleration axis: Bottom Rear (r=0) -> Back Low (r=0.5) -> Back Top (r=1).
            double accelBottomRearHat = ClampMath.To01(1.0 - 2.0 * accelSustained);
            double accelBackTopHat = ClampMath.To01(2.0 * accelSustained - 1.0);
            double accelBackLowHat = 1.0 - Math.Abs(2.0 * accelSustained - 1.0);

            double accelBottomRearWeight = Math.Max(accelBottomRearHat, ClampMath.To01(AccelBottomRearSustainFraction) * accelBackTopHat);
            double accelBackLowWeight = Math.Max(accelBackLowHat, ClampMath.To01(AccelBackLowSustainFraction) * accelBackTopHat);

            double accelBottomRearSustained = accelSustained * accelBottomRearWeight;
            double accelBackLowSustained = accelSustained * accelBackLowWeight;
            double accelBackTopSustained = accelSustained * accelBackTopHat;

            // ---- Transient: applied UNIFORMLY (leading and trailing pads alike) via headroom scaling
            // - see this method's own remarks and the S5 acceptance scenario.
            double bottomFrontLevel = ApplyTransient(brakeBottomFrontSustained, brakeTransientDrive);
            double bottomRearLevel = ApplyTransient(brakeBottomRearSustained, brakeTransientDrive)
                                    + ApplyTransient(accelBottomRearSustained, accelTransientDrive);
            double backLowLevel = ApplyTransient(brakeBackLowSustained, brakeTransientDrive)
                                 + ApplyTransient(accelBackLowSustained, accelTransientDrive);
            double backTopLevel = ApplyTransient(accelBackTopSustained, accelTransientDrive);

            // ---- Lateral left/right bias - independent of the longitudinal logic above.
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
        /// One pad PAIR's final left/right output: when <paramref name="shakeContribution"/> is 0 (the
        /// checkbox is off, either scale is 0, or the wheel value itself is 0 this frame) this reduces
        /// to EXACTLY <c>ClampMath.To0100(baseLevel0100 * leftFactor/rightFactor)</c> - the same
        /// expression, in the same order, this method replaced - so output is byte-identical to before
        /// this feature existed whenever it is inactive. Otherwise <see cref="GForceShake.Apply"/>
        /// computes the shaken left/right centres (already continuous with, and starting from,
        /// <paramref name="baseLevel0100"/> - see that method's own remarks) BEFORE the existing lateral
        /// bias factor is applied, so the lateral bias and the shake compose exactly as they did
        /// separately before, and the final <see cref="ClampMath.To0100"/> still guarantees the
        /// published 0-100 bound even if the lateral factor (up to 1.5x) pushes an already-shaken value
        /// back toward the edge.
        /// </summary>
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

        /// <summary>
        /// One chain's washout step: advances the sustained low-pass filter toward <paramref name="rawRatio"/>,
        /// derives the raw transient residual, smooths THAT through its own low-pass, and returns the
        /// final signed transient drive (already scaled by <see cref="TransientGain"/>). Both filters
        /// are simple, dt-correct exponential smoothing (see <see cref="ExponentialSmooth"/>) - the
        /// standard, frame-rate-independent way to implement a low-pass filter when frames do not
        /// arrive at a fixed interval.
        /// <para/>
        /// MUTATION (a) in the report: skip updating <paramref name="sustainRatio"/> from
        /// <paramref name="rawRatio"/> via a real low-pass and instead set it directly to
        /// <paramref name="rawRatio"/> (i.e. no lag at all) - the residual (and therefore every
        /// transient) becomes permanently ~0, collapsing this method to a magnitude-only model. A
        /// dedicated test catches this (S1/S3/S6 all depend on a non-trivial transient).
        /// </summary>
        private double AdvanceWashout(double dtSeconds, double rawRatio, ref double sustainRatio, ref double transient)
        {
            sustainRatio = ExponentialSmooth(sustainRatio, rawRatio, dtSeconds, SustainTimeConstantSeconds);

            double rawTransient = rawRatio - sustainRatio;

            // MUTATION (c) in the report: this is the washout itself - if the transient were instead
            // simply set to rawTransient every frame (or, worse, latched at its own running maximum and
            // never allowed to decay), holding steady input (S2) would never let it settle back toward
            // 0. Smoothing it through its own low-pass IS what makes it decay ("wash out") once the raw
            // residual itself shrinks.
            transient = ExponentialSmooth(transient, rawTransient, dtSeconds, TransientTimeConstantSeconds);

            return TransientGain * transient;
        }

        /// <summary>
        /// Applies <paramref name="transientDrive"/> to one pad's already-computed sustained level,
        /// HEADROOM-scaled so the result can never leave [0,1] and so a pad already at (or near) its
        /// sustained ceiling/floor still has somewhere to go: for a positive (onset) drive, headroom is
        /// how far the pad is BELOW 1 (room to rise); for a negative (easing-off) drive, headroom is
        /// the pad's own current level (room to fall, never below 0). Applied identically to every pad
        /// in a chain - deliberately NOT a leading-vs-trailing redistribution - see the S5 acceptance
        /// scenario's own worked numbers (Back Low at 10% sustained has ~90% headroom to ALSO jump
        /// during a big onset even while the leading pad is already fully saturated and has none left).
        /// <para/>
        /// MUTATION (b) in the report: apply <paramref name="transientDrive"/> directly, unscaled by
        /// headroom - a dedicated test (S5: a large transient while already saturated) catches the
        /// resulting out-of-range/incorrectly-sized contribution.
        /// </summary>
        private static double ApplyTransient(double sustainedLevel01, double transientDrive)
        {
            double headroom = transientDrive >= 0.0 ? (1.0 - sustainedLevel01) : sustainedLevel01;
            return ClampMath.To01(sustainedLevel01 + transientDrive * headroom);
        }

        /// <summary>
        /// Standard, frame-rate-independent exponential smoothing: after <paramref name="dtSeconds"/>
        /// elapsed, the filter has closed <c>1 - exp(-dt/tau)</c> of the remaining gap toward
        /// <paramref name="target"/> - equivalent to a continuous-time first-order low-pass filter with
        /// time constant <paramref name="tauSeconds"/>, evaluated exactly (not a fixed-per-frame-alpha
        /// approximation that would behave differently at 60 FPS vs 20 FPS). A non-positive or
        /// non-finite dt (missing Dt, or a non-positive tau) holds <paramref name="previous"/> unchanged
        /// rather than advancing incorrectly - the same "hold rather than guess" philosophy every other
        /// per-frame filter in this plugin follows.
        /// </summary>
        private static double ExponentialSmooth(double previous, double target, double dtSeconds, double tauSeconds)
        {
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0 || !ClampMath.IsFinite(target)) return previous;
            if (!(tauSeconds > 1e-6)) return target;

            double alpha = 1.0 - Math.Exp(-dtSeconds / tauSeconds);
            return previous + alpha * (target - previous);
        }

        /// <summary>
        /// Degraded fallback for when <see cref="ITelemetryFrame.LongitudinalG"/> is unavailable but
        /// <see cref="ITelemetryFrame.LateralG"/> is not - the owner's explicit decision: a left-to-right
        /// transitioning vibration driven by lateral G alone is still useful feedback. There is no
        /// fore/aft signal to build a level from, so this deliberately does NOT invent one; instead it
        /// publishes the SAME, undiminished, lateral-driven intensity on all four zone-pairs - a
        /// "neutral" fore/aft distribution favouring neither chain. Continuous and bounded exactly like
        /// the main path.
        /// </summary>
        private GForceOutput ComputeLateralOnlyFallback(double lateralG, double shakeContribution = 0.0)
        {
            double safeLatMax = LateralReferenceG > 1e-6 ? LateralReferenceG : 1e-6;
            double magnitudeRatio = ClampMath.To01(Math.Abs(lateralG) / safeLatMax);

            double lateralBias = ApplyLateralDirection(ClampMath.Clamp(lateralG / safeLatMax, -1.0, 1.0));

            double leftFactor = 1.0 - LateralBiasGain * lateralBias;
            double rightFactor = 1.0 + LateralBiasGain * lateralBias;

            // All four pairs share the SAME base level here (this fallback has no fore/aft
            // distribution - see this method's own remarks), so they also share the same shaken
            // left/right centres - the four pairs stay identical to each other exactly as before this
            // feature existed.
            ShakePadPair(magnitudeRatio * 100.0, leftFactor, rightFactor, shakeContribution, out double left, out double right);

            return new GForceOutput(
                bottomFrontLeft: left, bottomFrontRight: right,
                bottomRearLeft: left, bottomRearRight: right,
                backLowLeft: left, backLowRight: right,
                backTopLeft: left, backTopRight: right);
        }

        /// <summary>The ONE place <see cref="LateralDirection"/> is applied - both <see cref="Compute"/>
        /// and <see cref="ComputeLateralOnlyFallback"/> route their raw, signed bias through this.</summary>
        private double ApplyLateralDirection(double signedBias)
            => LateralDirection == LateralDirectionMode.Reversed ? -signedBias : signedBias;
    }
}

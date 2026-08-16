using System;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Layer 4's orchestrator: "Normalized" projection. Combines Layer 3's Raw per-wheel value (the
    /// only thing that differentiates one wheel from another - see below) with a car-level
    /// "how close to MY OWN limit is this" ratio built ONLY from speed/throttle/brake/G (the brief's
    /// hard input restriction - no viper4gh, no wheel-speed-derived slip, nothing exotic), so the
    /// published bands (0-30 light/margin, 30-60 the ideal working range, 60-80 starting to
    /// lock-or-spin (very close but not yet), 100 fully locked-or-spinning - see
    /// docs\refinements-report.md for the numeric verification) mean
    /// the same thing in an arcade car routinely pulling 4g as in a sim car routinely pulling 1.2g -
    /// see <see cref="GripLearner"/> for how that calibration is learned rather than assumed.
    /// <para/>
    /// WHY RAW IS STILL AN INPUT: none of speed/throttle/brake/G differentiate one wheel from
    /// another - g-force is a whole-car scalar, and per-wheel slip data is explicitly banned. Layer
    /// 3's Raw per-wheel values are the ONLY per-wheel shape available under that restriction, so
    /// this engine uses Raw's own four-way PROPORTIONS (which wheel looks worse relative to the
    /// others) to redistribute a physically-grounded, car-calibrated car-level severity across the
    /// four wheels, rather than trusting Raw's own ABSOLUTE scale (which is exactly what Layer 4
    /// exists to stop trusting blindly).
    /// <para/>
    /// THE DEGRADATION LADDER (see <see cref="AchievedMotion"/> for levels 1-2):
    /// <list type="number">
    /// <item>Direct <see cref="ITelemetryFrame.LongitudinalG"/>.</item>
    /// <item>Derived from differentiated ground speed + frame dt.</item>
    /// <item>Neither available -&gt; Raw is passed through unmodified (clamped 0-100), per wheel and
    /// per aggregate - "something sane", not zero or garbage.</item>
    /// </list>
    /// <para/>
    /// WHICH CHANNEL IS ACTIVE - PEDAL STATE IS NEVER THE ANSWER (fixed; read before changing this
    /// again): this engine used to gate Lock on <c>BrakePercent</c> and Slip on
    /// <c>ThrottlePercent</c>. The owner has stated, more than once, that pedal state must NOT decide
    /// whether the car is slowing or speeding up - brake and throttle overlap constantly (left-foot
    /// braking, trail braking, a downshift blip), so a "committed" pedal reading is not proof of which
    /// way the car is actually going. This is now decided ONLY by
    /// <see cref="LongitudinalDirectionResolver"/>, from differentiated ground speed - see that
    /// class's own remarks for the full reasoning, including why it also side-steps the unverified
    /// <see cref="ITelemetryFrame.LongitudinalG"/> sign convention (evidenced inverted on at least one
    /// title: Forza Horizon 6, longitudinal acceleration reads positive while slowing in 95.8% of
    /// qualifying frames) for free, by construction, never by verifying that convention per game.
    /// When direction is <see cref="LongitudinalMotionState.Unknown"/> (no derivative available yet,
    /// or speed changing by less than the dead band), NEITHER channel gets a non-zero reading - there
    /// is nothing honest to attribute the magnitude to, exactly the same reasoning the sibling
    /// project's own Task 32 fix applies.
    /// <para/>
    /// NOTE: Layer 3's OWN internal <c>Brake &gt; 20</c> / <c>Throttle &gt; 40</c> gates (inside
    /// <see cref="LegacySlipAlgorithm"/>) are SimHub's own decompiled design and are UNCHANGED by this
    /// - this fix is about Layer 4's direction decision only.
    /// </summary>
    public sealed class NormalizedWheelLockSlipEngine
    {
        /// <summary>~0.5g lateral - above this, a sample is excluded from the LEARNER (not from the
        /// live output) so heavy cornering (which can itself produce a large combined-G excursion
        /// with little genuine longitudinal braking/traction behind it) cannot teach the learner a
        /// false peak - "isolate longitudinal behaviour", the same reasoning the sibling project's
        /// own learner gate uses, applied here to the exact lesson the brief calls out (cornering
        /// scrub must not read as lockup).</summary>
        private const double LateralIsolationGateG = 0.5;

        private const double NoRawSignalEpsilon = 1e-6;

        /// <summary>
        /// FIELD FIXES (docs\field-fixes-report.md, defects B and D) - both share one mechanism,
        /// documented together here:
        /// <para/>
        /// DEFECT B (slip normalisation inverted - low Raw reads high, full Raw reads near zero):
        /// the severity this engine publishes used to be <c>gripUtilization</c> alone (the learned
        /// G-based ratio), with Raw's absolute level discarded entirely (only its four-way
        /// PROPORTIONS were used - see <see cref="ComputeChannel"/>'s remarks). That is correct for
        /// BRAKING (higher achieved deceleration genuinely means closer to lockup) but backwards for
        /// genuine WHEELSPIN under power: achieved chassis acceleration typically DROPS once a driven
        /// wheel starts spinning (torque is spent spinning the tyre, not moving the car), so a G-only
        /// severity model reads a full-blown spin as LOW severity - confirmed against this session's
        /// captured log (SpeedingUp-only frames binned by <c>WheelSlip.Raw.All</c> show achieved-G
        /// falling, not rising, in the 60-101 bins). THE FIX: <see cref="RawActiveThreshold"/> below
        /// floors the published severity at Raw's own instantaneous mean, so the output can never
        /// read BELOW what Layer 3's own per-wheel measurement already says - this also directly
        /// fixes defect C's non-monotonicity (Lock reading 100 while Raw reads ~0 is the mirror-image
        /// symptom of the same root cause: G, not Raw, was the only thing that mattered).
        /// <para/>
        /// DEFECT D (release lag - Projected stays elevated for up to ~1.4s+ after Raw drops below
        /// 1): traced to the SAME G-only model from the other direction - a car keeps decelerating
        /// (engine braking/drag) for a second or more after a wheel stops actually locking, and
        /// gripUtilization faithfully (if uselessly, for THIS purpose) keeps tracking that real but
        /// no-longer-relevant chassis deceleration. Confirmed in the captured log: a traced release
        /// event shows <c>Diag.Direction</c> staying "Slowing" (engaged) for 200+ frames after
        /// <c>WheelLock.Raw.All</c> drops to exactly 0, with <c>Diag.MotionMagnitudeG</c> decaying
        /// only gradually across that whole stretch - i.e. this is genuinely still-decelerating
        /// physics, not a smoothing artefact, so units alone (defect A) cannot fix it; gripUtilization
        /// must stop being trusted once Raw itself has nothing to say. THE FIX: <see cref="_lockRawPresence"/>/
        /// <see cref="_slipRawPresence"/> track, per channel, an instant-attack/fast-release envelope
        /// of "is Raw currently indicating anything" and gate gripUtilization by it - once Raw drops
        /// below <see cref="RawActiveThreshold"/> the envelope (and therefore the published severity)
        /// releases to zero within about <see cref="ReleaseTauSeconds"/>*3 seconds REGARDLESS of how
        /// elevated gripUtilization still reads, while never introducing any lag while Raw stays
        /// active (the envelope is already 1.0 and stays there, so the floor/gate above is applied
        /// INSTANTLY in the common case - existing calibration tests that hold Raw at a constant
        /// active level across a changing G magnitude are therefore unaffected).
        /// </summary>
        private const double RawActiveThreshold = 1.0;

        /// <summary>Release time constant for <see cref="_lockRawPresence"/>/<see cref="_slipRawPresence"/>
        /// - chosen so the envelope (and therefore the published severity gated by it) reaches within
        /// about 1% of zero after 5 time constants (~0.15s), comfortably inside the brief's own "under
        /// ~0.15s" release acceptance bar for defect D.</summary>
        private const double ReleaseTauSeconds = 0.03;

        /// <summary>
        /// Lock channel's learning-path reject ceiling (<see cref="GripLearner.LearnCapG"/>) -
        /// Lock is the BRAKING-referenced channel, so this mirrors
        /// <see cref="GForce.GForceMaxLearner"/>'s own deceleration cap (see
        /// <see cref="Settings.GForceSettings.DecelLearnMaxPlausibleG"/> for the shared derivation):
        /// real-world braking peaks top out around F1's ~5-6g, so 8g leaves comfortable margin above
        /// the most extreme REAL event while still decisively excluding a wall-impact-scale (15-20g+)
        /// spike from ever becoming the learned reference - docs\gforce-direction-fix-report.md.
        /// </summary>
        public const double LockLearnMaxPlausibleG = 8.0;

        /// <summary>
        /// Slip channel's learning-path reject ceiling - Slip is the ACCELERATING/traction-referenced
        /// channel, so this mirrors <see cref="GForce.GForceMaxLearner"/>'s own acceleration cap (see
        /// <see cref="Settings.GForceSettings.AccelLearnMaxPlausibleG"/>): real-world acceleration
        /// peaks top out around a top-fuel dragster launch's ~4-5g (well above anything a road/GT/F1
        /// car's own driven wheels achieve), so 6g leaves margin above the most extreme REAL event
        /// while still excluding a wall-impact-scale spike.
        /// </summary>
        public const double SlipLearnMaxPlausibleG = 6.0;

        // NOTE: an earlier revision of this file added a "low-speed lock compensation" here, based on
        // the hypothesis that Layer 3's own RPM/speed brake term is proportional to ground speed.
        // SUPERSEDED (docs\lock-and-animation-report.md): the owner confirmed switching the Wheel Lock
        // SOURCE to SimHub's own ShakeIt export resolves the driver's complaint entirely, proving THIS
        // layer (Normalized/Projected) was never the defect - it was Layer 3's own reproduction of
        // SimHub's algorithm using the WRONG branch. Applying a Layer-4 compensation here would have
        // incorrectly altered the ALREADY-CORRECT ShakeIt-sourced values too (this layer cannot know
        // where rawLockWheels came from) - removed entirely; the real fix now lives in
        // Private\QAdvanceFeedback\SimpleBrakingLockAlgorithm.cs / LegacyWheelLockSlipEngine.cs.

        private readonly KeyedGripLearner _lockLearners;
        private readonly KeyedGripLearner _slipLearners;
        private readonly LongitudinalDirectionResolver _direction;
        private readonly TelemetryLearningGate _learningGate;

        // ---- Per-channel release-envelope state (defect D) - see the remarks above. Reset alongside
        // the direction filter (ResetDirection) so a game/session switch does not inherit a stale
        // "Raw was recently active" envelope from whatever the previous game was doing.
        private double _lockRawPresence;
        private double _slipRawPresence;

        public NormalizedWheelLockSlipEngine(
            KeyedGripLearner lockLearners = null, KeyedGripLearner slipLearners = null,
            LongitudinalDirectionResolver directionResolver = null, TelemetryLearningGate learningGate = null)
        {
            _lockLearners = lockLearners ?? new KeyedGripLearner(LockLearnMaxPlausibleG);
            _slipLearners = slipLearners ?? new KeyedGripLearner(SlipLearnMaxPlausibleG);
            _direction = directionResolver ?? new LongitudinalDirectionResolver();
            _learningGate = learningGate ?? new TelemetryLearningGate();
        }

        /// <summary>The full per-(game,car) keyed Lock learner store - exposed so the plugin
        /// composition root can Import/Export it through <c>RuntimeStore</c> at Init/every frame,
        /// and so a settings UI/diagnostics readout can query a specific (or the current) key
        /// without this engine needing to track "current" itself (the plugin already does, exactly
        /// like <c>GForceSettings.SetCurrentGameAndCar</c>).</summary>
        public KeyedGripLearner LockLearners => _lockLearners;

        /// <summary>The Slip channel's equivalent of <see cref="LockLearners"/>.</summary>
        public KeyedGripLearner SlipLearners => _slipLearners;

        /// <summary>The most recently resolved direction - exposed for diagnostics (e.g. a settings
        /// UI/dashboard readout of "why is the lock channel silent right now").</summary>
        public LongitudinalMotionState CurrentDirection => _direction.State;

        /// <summary>Clears the learned direction filter - call on a game/session switch, mirroring
        /// <c>SimHubTelemetryAdapter.Reset</c>. Does NOT reset the learners (see
        /// <see cref="GripLearner"/>'s own remarks on why they persist across a game switch via
        /// RuntimeStore). Also clears the defect-D release envelopes (see this class's own remarks)
        /// so a fresh game/session does not inherit a stale "Raw was recently active" state.</summary>
        public void ResetDirection()
        {
            _direction.Reset();
            _learningGate.Reset();
            _lockRawPresence = 0.0;
            _slipRawPresence = 0.0;
        }

        /// <param name="sample">This frame's telemetry.</param>
        /// <param name="rawLockWheels">Layer 4's resolved Lock-channel per-wheel sources.</param>
        /// <param name="rawSlipWheels">Layer 4's resolved Slip-channel per-wheel sources.</param>
        /// <param name="gameId">The current SimHub game name - which learned peak-g reference this
        /// frame reads/updates (see <see cref="KeyedGripLearner"/>). Defaults to empty so every
        /// existing single-game caller/test keeps compiling and behaving exactly as before (an empty
        /// gameId/carId is still a perfectly valid, isolated key of its own).</param>
        /// <param name="carId">The current SimHub car id. See <paramref name="gameId"/>.</param>
        /// <param name="thresholds">
        /// TRIGGER THRESHOLD (owner-requested restructure - see <see cref="LegacyThresholds"/>'s own
        /// remarks and docs\lock-and-animation-report.md). Null (the default) uses
        /// <see cref="LegacyThresholds.Defaults"/>, mirroring every other threshold-consuming method in
        /// this plugin family (<c>LegacySlipAlgorithm.Compute</c>, <c>LegacyWheelLockSlipEngine.Compute</c>)
        /// - every pre-existing caller/test keeps compiling and behaving exactly as before. Below the
        /// channel's own pedal threshold, THIS layer's own output (in addition to Layer 3's Raw - see
        /// <c>LegacyWheelLockSlipEngine</c>) reads an unconditional zero for the whole channel - see
        /// <see cref="ComputeChannel"/>'s own remarks for exactly where this is applied relative to the
        /// pre-existing "no signal at all" fallback and the direction-based "engaged" gate.
        /// </param>
        /// <param name="lockAggregation">Wheel Lock's own <see cref="Aggregator"/> weights
        /// (docs\aggregation-report.md), applied to THIS layer's own per-wheel output - see this
        /// class's own remarks on why Raw's aggregation is not simply reused (each tier aggregates its
        /// OWN per-wheel values with the SAME formula/weights, "inheriting" the scheme rather than the
        /// numbers). Read fresh every call, exactly like <paramref name="thresholds"/> - no engine
        /// rebuild needed when a driver edits these weights. Null (the default) means
        /// <see cref="AggregationWeights.LockDefaults"/>.</param>
        /// <param name="slipAggregation">Wheel Slip's own <see cref="Aggregator"/> weights - see
        /// <paramref name="lockAggregation"/>'s own remarks. Null (the default) means
        /// <see cref="AggregationWeights.SlipDefaults"/>.</param>
        public NormalizedWheelLockSlipResult Compute(
            ITelemetrySample sample, Corners rawLockWheels, Corners rawSlipWheels,
            string gameId = "", string carId = "", LegacyThresholds? thresholds = null,
            AggregationWeights? lockAggregation = null, AggregationWeights? slipAggregation = null)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));

            LegacyThresholds t = thresholds ?? LegacyThresholds.Defaults;
            AggregationWeights lockWeights = lockAggregation ?? AggregationWeights.LockDefaults;
            AggregationWeights slipWeights = slipAggregation ?? AggregationWeights.SlipDefaults;
            double? brakePercent = sample.New?.BrakePercent;
            double? throttlePercent = sample.New?.ThrottlePercent;

            // "At/above the threshold, normal behaviour" - car-level, >= (see this parameter's own
            // remarks on the fail-closed treatment of a missing/null pedal reading).
            bool lockTriggered = brakePercent >= t.LockBrakeThresholdPercent;
            bool slipTriggered = brakePercent >= t.SlipBrakeThresholdPercent || throttlePercent >= t.SlipThrottleThresholdPercent;

            AchievedMotion.Result motion = AchievedMotion.Resolve(sample);
            LongitudinalMotionState direction = _direction.Resolve(sample);

            // ---- Owner-requested learning validity gate (docs\gforce-direction-fix-report.md): one
            // shared, general check per frame (pit/replay/session-restart/dt/speed/teleport - see
            // TelemetryLearningGate's own remarks), PLUS a channel-specific pedal-commitment minimum
            // (Lock needs meaningful brake, Slip needs meaningful throttle - mirroring the sibling
            // project's own LearnMinBrake/LearnMinSpeedMs) - this gates ONLY whether a fresh
            // observation is folded into the learner below; it never gates the live 0-100 severity
            // output itself, which remains decided purely by measured "engaged" direction (see this
            // class's own standing rule at the top of this file).
            bool frameValidForLearning = _learningGate.IsValid(sample);
            bool lockObserveAllowed = frameValidForLearning && (sample.New?.BrakePercent ?? 0.0) >= TelemetryLearningGate.LearnMinBrakePercent;
            bool slipObserveAllowed = frameValidForLearning && (sample.New?.ThrottlePercent ?? 0.0) >= TelemetryLearningGate.LearnMinThrottlePercent;

            double dtSeconds = sample.Dt.HasValue && sample.Dt.Value.TotalSeconds > 0.0 ? sample.Dt.Value.TotalSeconds : 0.0;

            Corners lockWheels = ComputeChannel(sample.New, rawLockWheels, motion, _lockLearners, gameId, carId,
                direction == LongitudinalMotionState.Slowing, lockTriggered, lockObserveAllowed, dtSeconds,
                ref _lockRawPresence);
            Corners slipWheels = ComputeChannel(sample.New, rawSlipWheels, motion, _slipLearners, gameId, carId,
                direction == LongitudinalMotionState.SpeedingUp, slipTriggered, slipObserveAllowed, dtSeconds,
                ref _slipRawPresence);

            WheelAggregate lockAggregate = Aggregator.Compute(lockWheels, lockWeights);
            WheelAggregate slipAggregate = Aggregator.Compute(slipWheels, slipWeights);

            return new NormalizedWheelLockSlipResult(
                lockWheels,
                lockAggregate.Front, lockAggregate.Rear, lockAggregate.Left, lockAggregate.Right, lockAggregate.All,
                slipWheels,
                slipAggregate.Front, slipAggregate.Rear, slipAggregate.Left, slipAggregate.Right, slipAggregate.All);
        }

        private static Corners ComputeChannel(
            ITelemetryFrame frame, Corners rawWheels, AchievedMotion.Result motion,
            KeyedGripLearner learners, string gameId, string carId, bool engaged, bool triggered,
            bool observeAllowed, double dtSeconds, ref double rawPresence)
        {
            double w0 = ClampMath.To0100(rawWheels.FrontLeft);
            double w1 = ClampMath.To0100(rawWheels.FrontRight);
            double w2 = ClampMath.To0100(rawWheels.RearLeft);
            double w3 = ClampMath.To0100(rawWheels.RearRight);

            // TRIGGER THRESHOLD (owner-requested restructure, and the owner's OWN clarification: this
            // gate applies at the SOURCE BOUNDARY, unconditionally, whatever the configured source is
            // (our own Raw, a ShakeIt export, or a Manual property/expression) and regardless of
            // anything else this method might otherwise fall back to - see
            // NormalizedWheelLockSlipEngine.Compute's own remarks). Deliberately checked BEFORE the
            // "no G/speed signal at all" degradation floor below (NOT after, as an earlier revision of
            // this method had it) - the owner was explicit that "below threshold the channel is
            // inactive" has no carve-out: a title with no G/speed telemetry is not exempt just because
            // it also happens to hit the level-3 fallback.
            if (!triggered)
                return Corners.Zero;

            // Degradation floor (ladder level 3): no g signal at all, direct or derived - Raw is the
            // only available basis, so it is passed through rather than reading zero or garbage (but
            // only once the trigger threshold above has already been cleared).
            if (motion.Level == AchievedMotion.SignalLevel.Unavailable)
                return new Corners(w0, w1, w2, w3);

            // "engaged" = this channel's own direction (Slowing for Lock, SpeedingUp for Slip) is what
            // LongitudinalDirectionResolver measured THIS frame - see this class's own remarks on why
            // pedal state is never consulted here. Not engaged -> nothing to attribute the magnitude
            // to at all (unchanged from before this task's fix) - the release envelope is simply held
            // rather than advanced, so the NEXT genuinely-engaged frame does not inherit a jump.
            if (!engaged)
                return Corners.Zero;

            if (observeAllowed && IsLongitudinallyIsolated(frame))
                learners.Observe(gameId, carId, motion.MagnitudeG);

            double gripUtilization = ClampMath.To0100(learners.Ratio(gameId, carId, motion.MagnitudeG) * 100.0);

            double mean = (w0 + w1 + w2 + w3) / 4.0;

            // ---- DEFECTS B/D fix - see this class's own remarks on RawActiveThreshold/ReleaseTauSeconds.
            // Instant attack (Raw active this frame -> envelope snaps to 1.0, no lag), fast release
            // (Raw inactive -> envelope decays toward 0 with ReleaseTauSeconds) - the classical
            // asymmetric attack/release shape, deliberately mirroring GForceEngine's own washout
            // filter convention elsewhere in this plugin.
            bool rawActiveNow = mean >= RawActiveThreshold;
            rawPresence = rawActiveNow ? 1.0 : ExponentialDecayToZero(rawPresence, dtSeconds, ReleaseTauSeconds);

            double effectiveGripUtilization = gripUtilization * rawPresence;

            // The floor: severity can never read BELOW Raw's own instantaneous mean - this is what
            // guarantees monotonicity in Raw (defects B/C) without needing to lag anything (the floor
            // itself is instantaneous), while effectiveGripUtilization above is what makes the CEILING
            // release quickly once Raw stops supporting it (defect D).
            double severity = Math.Max(effectiveGripUtilization, mean);

            double s0, s1, s2, s3;
            if (mean <= NoRawSignalEpsilon)
            {
                // No per-wheel differentiation available from Raw at all - distribute the
                // car-level severity evenly rather than favouring an arbitrary wheel.
                s0 = s1 = s2 = s3 = 1.0;
            }
            else
            {
                s0 = w0 / mean; s1 = w1 / mean; s2 = w2 / mean; s3 = w3 / mean;
            }

            return new Corners(
                ClampMath.To0100(severity * s0),
                ClampMath.To0100(severity * s1),
                ClampMath.To0100(severity * s2),
                ClampMath.To0100(severity * s3));
        }

        /// <summary>Standard dt-correct exponential decay of <paramref name="previous"/> toward zero -
        /// mirrors <c>GForceEngine</c>'s own <c>ExponentialSmooth</c> (kept as a separate, "toward
        /// zero only" helper here since that is the only target this class's release envelope ever
        /// needs). A non-positive/non-finite dt holds <paramref name="previous"/> unchanged (missing
        /// Dt, e.g. the first sample of a session) rather than releasing incorrectly.</summary>
        private static double ExponentialDecayToZero(double previous, double dtSeconds, double tauSeconds)
        {
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0) return previous;
            double alpha = 1.0 - Math.Exp(-dtSeconds / tauSeconds);
            return previous - alpha * previous;
        }

        private static bool IsLongitudinallyIsolated(ITelemetryFrame frame)
        {
            double? lateral = frame?.LateralG;
            return !lateral.HasValue || Math.Abs(lateral.Value) <= LateralIsolationGateG;
        }
    }
}

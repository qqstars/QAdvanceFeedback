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

        private readonly Aggregator _aggregator;
        private readonly KeyedGripLearner _lockLearners;
        private readonly KeyedGripLearner _slipLearners;
        private readonly LongitudinalDirectionResolver _direction;

        public NormalizedWheelLockSlipEngine(
            KeyedGripLearner lockLearners = null, KeyedGripLearner slipLearners = null, Aggregator aggregator = null,
            LongitudinalDirectionResolver directionResolver = null)
        {
            _lockLearners = lockLearners ?? new KeyedGripLearner();
            _slipLearners = slipLearners ?? new KeyedGripLearner();
            _aggregator = aggregator ?? new Aggregator(GroupMode.PNorm, 2.0, Corners.Uniform(1.0));
            _direction = directionResolver ?? new LongitudinalDirectionResolver();
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
        /// RuntimeStore).</summary>
        public void ResetDirection() => _direction.Reset();

        /// <param name="sample">This frame's telemetry.</param>
        /// <param name="rawLockWheels">Layer 4's resolved Lock-channel per-wheel sources.</param>
        /// <param name="rawSlipWheels">Layer 4's resolved Slip-channel per-wheel sources.</param>
        /// <param name="gameId">The current SimHub game name - which learned peak-g reference this
        /// frame reads/updates (see <see cref="KeyedGripLearner"/>). Defaults to empty so every
        /// existing single-game caller/test keeps compiling and behaving exactly as before (an empty
        /// gameId/carId is still a perfectly valid, isolated key of its own).</param>
        /// <param name="carId">The current SimHub car id. See <paramref name="gameId"/>.</param>
        public NormalizedWheelLockSlipResult Compute(
            ITelemetrySample sample, Corners rawLockWheels, Corners rawSlipWheels,
            string gameId = "", string carId = "")
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));

            AchievedMotion.Result motion = AchievedMotion.Resolve(sample);
            LongitudinalMotionState direction = _direction.Resolve(sample);

            Corners lockWheels = ComputeChannel(sample.New, rawLockWheels, motion, _lockLearners, gameId, carId,
                direction == LongitudinalMotionState.Slowing);
            Corners slipWheels = ComputeChannel(sample.New, rawSlipWheels, motion, _slipLearners, gameId, carId,
                direction == LongitudinalMotionState.SpeedingUp);

            return new NormalizedWheelLockSlipResult(
                lockWheels,
                _aggregator.Pair(Corners.FL, Corners.FR, lockWheels),
                _aggregator.Pair(Corners.RL, Corners.RR, lockWheels),
                _aggregator.Pair(Corners.FL, Corners.RL, lockWheels),
                _aggregator.Pair(Corners.FR, Corners.RR, lockWheels),
                _aggregator.Quad(lockWheels),
                slipWheels,
                _aggregator.Pair(Corners.FL, Corners.FR, slipWheels),
                _aggregator.Pair(Corners.RL, Corners.RR, slipWheels),
                _aggregator.Pair(Corners.FL, Corners.RL, slipWheels),
                _aggregator.Pair(Corners.FR, Corners.RR, slipWheels),
                _aggregator.Quad(slipWheels));
        }

        private static Corners ComputeChannel(
            ITelemetryFrame frame, Corners rawWheels, AchievedMotion.Result motion,
            KeyedGripLearner learners, string gameId, string carId, bool engaged)
        {
            double w0 = ClampMath.To0100(rawWheels.FrontLeft);
            double w1 = ClampMath.To0100(rawWheels.FrontRight);
            double w2 = ClampMath.To0100(rawWheels.RearLeft);
            double w3 = ClampMath.To0100(rawWheels.RearRight);

            // Degradation floor (ladder level 3): no g signal at all, direct or derived - Raw is the
            // only available basis, so it is passed through rather than reading zero or garbage.
            if (motion.Level == AchievedMotion.SignalLevel.Unavailable)
                return new Corners(w0, w1, w2, w3);

            // "engaged" = this channel's own direction (Slowing for Lock, SpeedingUp for Slip) is what
            // LongitudinalDirectionResolver measured THIS frame - see this class's own remarks on why
            // pedal state is never consulted here.
            if (!engaged)
                return Corners.Zero;

            if (IsLongitudinallyIsolated(frame))
                learners.Observe(gameId, carId, motion.MagnitudeG);

            double gripUtilization = ClampMath.To0100(learners.Ratio(gameId, carId, motion.MagnitudeG) * 100.0);

            double mean = (w0 + w1 + w2 + w3) / 4.0;
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
                ClampMath.To0100(gripUtilization * s0),
                ClampMath.To0100(gripUtilization * s1),
                ClampMath.To0100(gripUtilization * s2),
                ClampMath.To0100(gripUtilization * s3));
        }

        private static bool IsLongitudinallyIsolated(ITelemetryFrame frame)
        {
            double? lateral = frame?.LateralG;
            return !lateral.HasValue || Math.Abs(lateral.Value) <= LateralIsolationGateG;
        }
    }
}

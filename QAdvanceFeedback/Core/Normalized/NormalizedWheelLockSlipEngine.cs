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
    /// <c>BrakeSpeedSlipModel</c>) are SimHub's own decompiled design and are UNCHANGED by this
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

        // FIELD FIXES HISTORY, superseded below (docs\field-fixes-report.md defects B/D, then
        // docs\f1-normalization-fix-report.md) - kept because the ORIGINAL defects are still fixed,
        // just by a more direct mechanism now:
        //
        // DEFECT B (slip normalisation inverted) / DEFECT C (lock non-monotone in Raw): the severity
        // this engine published used to be gripUtilization alone (a learned G-based ratio), with
        // Raw's absolute level discarded entirely. That reads backwards during genuine WHEELSPIN
        // under power (achieved chassis acceleration DROPS once a driven wheel spins, so a G-only
        // model reads a full-blown spin as LOW severity). FIRST FIX: floor severity at Raw's own
        // instantaneous (calibrated) mean via Math.Max(effectiveGripUtilization, calibratedMean).
        //
        // DEFECT D (release lag): the same G-only model, from the other direction - a car keeps
        // decelerating (engine braking/drag) after a wheel stops actually locking, so gripUtilization
        // kept the published severity elevated long after Raw itself dropped to 0. FIRST FIX: an
        // instant-attack/fast-release presence envelope (RawActiveThreshold/ReleaseTauSeconds,
        // _lockRawPresence/_slipRawPresence) gated gripUtilization's contribution off once Raw went
        // quiet.
        //
        // THE F1 25 FIX (docs\f1-normalization-fix-report.md) SUPERSEDES BOTH MECHANISMS ABOVE, not
        // just tunes them: the owner's own controlled F1 25 comparison (four matched wet/dry,
        // Raw/ShakeIt logs) established that the configured SOURCE (our own Raw, or a ShakeIt export)
        // already measures wheel lock/spin proximity directly and CONDITION-INDEPENDENTLY - driving
        // ShakeIt's export directly (no plugin, a fixed gamma curve) read consistently in both wet and
        // dry, while THIS engine's own published severity did not. The culprit was exactly the
        // Math.Max above: it let a too-low, session-immature learned G reference (an F1 car brakes at
        // 5-6g; a single session's own learner had matured to only 3.0-4.1g from 112-253 qualifying
        // samples against a 200-sample maturity bar) push severity to "starting to lock"/"critical"
        // well before the wheel was anywhere near its limit - and since wet braking achieves LOWER g
        // than dry at the SAME lock proximity, a shared g-based ceiling necessarily read the two
        // conditions differently even when Raw/ShakeIt agreed. THE FIX: severity is now simply
        // calibratedMean - the source's own reading, rescaled onto the canonical 0-100 band by
        // KeyedScaleLearner (unchanged) - with NOTHING allowed to push it higher. This keeps both
        // original defects fixed AS A CONSEQUENCE, more directly than before: severity can never read
        // below Raw (it simply IS Raw, calibrated) and it releases with ZERO added lag when Raw drops
        // (no envelope needed - there is nothing left to gate off, so the OLD presence envelope
        // described above is removed entirely, not merely bypassed). GripUtilization keeps exactly one
        // remaining, demoted role: it is still what KeyedScaleLearner's physically-anchored tier uses
        // (via the SHARED, per-car physicalReference learner below) to detect "physically at this
        // car's own limit right now" and teach the scale learner what THIS source's own raw reading
        // looks like at that moment - a CALIBRATOR, never again a live ceiling-raiser. It is
        // deliberately NOT reintroduced as a "floor when the source is quiet" either: that would
        // reproduce defect D from the opposite direction (a car that genuinely never locks a wheel -
        // perfect ABS, high-downforce grip margin - would have G-based severity take back over exactly
        // when Raw's own zero is the correct, honest answer). A title with no per-wheel source data at
        // all is handled by the separate, pre-existing degradation ladder (motion.Level == Unavailable
        // below and Raw pass-through) - this method is only reached once real per-wheel Raw/ShakeIt
        // data exists.
        //
        // The two source-keyed learners (_lockLearners/_slipLearners) are still fed via
        // KeyedGripLearner.Observe every qualifying frame (unchanged) - purely for the
        // Diag.Lock/Slip.LearnedPeakG/LearnerConfidence diagnostics and RuntimeStore persistence
        // continuity; their KeyedGripLearner.Ratio is simply never queried for the live severity
        // anymore.

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

        /// <summary>
        /// PER-SOURCE INPUT CALIBRATION (docs\branch-dispatch-and-source-keyed-learning-report.md,
        /// the owner's own follow-up on top of source-KEYED learning): the fraction of THIS car's own
        /// learned physical grip-limit reference (<see cref="_lockPhysicalReference"/>/
        /// <see cref="_slipPhysicalReference"/>'s own <see cref="GripLearner.Ratio"/>) at/above which a
        /// frame is treated as "physically at the limit right now" - the moment
        /// <see cref="KeyedScaleLearner"/>'s primary tier learns each source's own characteristic
        /// near-limit reading from. 0.85, not 1.0: the physical reference's own learned peak is itself a
        /// DECAYING maximum (see <see cref="GripLearner.Observe"/>'s own remarks), so requiring the
        /// live ratio to reach the exact historical peak on every calibrating frame would make this
        /// trigger condition needlessly rare; 85% of the car's own learned peak is still unambiguously
        /// "close to the limit", not merely "committed".
        /// </summary>
        public const double PhysicalLimitRatioThreshold = 0.85;

        /// <summary>
        /// Minimum raw MEAN before a frame is even eligible to teach <see cref="KeyedScaleLearner"/>
        /// anything (either tier) - guards against mistaking a merely "technically active" but
        /// otherwise negligible raw reading (e.g. a small placeholder value several of this engine's
        /// own calibration tests use, chosen only to be nonzero, not to represent a genuine near-limit
        /// reading) for "this is what the source reads at its own ceiling". A REAL source's own
        /// near-limit reading (per the owner's own worked examples: 30 at minimum, for the smallest of
        /// the three sample sources) sits comfortably above this bar; only a deliberately tiny
        /// placeholder does not.
        /// </summary>
        public const double MinRawForCalibrationObservation = 10.0;

        /// <summary>
        /// SURFACE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md - the
        /// owner's own follow-up: a learned grip reference dominated by tarmac silently reads a genuine
        /// loose-surface limit as low severity). Time constant for smoothing the raw, potentially noisy
        /// per-frame <see cref="SurfaceLooseFraction"/> before it is used to blend between the "Sealed"
        /// and "Loose" learned references - THE mechanism that keeps a tarmac/grass boundary crossing
        /// from producing a step change in output (this task's own explicit continuity requirement).
        /// 0.25s - fast enough that a genuine, sustained surface change (a corner exit onto full grass)
        /// is reflected within a fraction of a second, slow enough that brief, single-frame noise in the
        /// underlying per-wheel reading does not visibly wobble the blend.
        /// </summary>
        private const double SurfaceFractionSmoothingTauSeconds = 0.25;

        /// <summary>How close the SMOOTHED loose fraction must be to 0 or 1 before a frame is trusted
        /// enough to teach either the "Sealed" or "Loose" bucket anything - "excluding [ambiguous/mixed]
        /// frames [from learning] is safest for learning integrity" (this task's own explicit
        /// instruction). A frame whose smoothed fraction sits strictly between these two bounds (i.e.
        /// wheels currently disagree, or a transition is still in progress) teaches NEITHER bucket -
        /// only the LIVE blended read still uses it.</summary>
        private const double SurfaceLearningPurityThreshold = 0.05;

        // NOTE: an earlier revision of this file added a "low-speed lock compensation" here, based on
        // the hypothesis that Layer 3's own RPM/speed brake term is proportional to ground speed.
        // SUPERSEDED (docs\lock-and-animation-report.md): the owner confirmed switching the Wheel Lock
        // SOURCE to SimHub's own ShakeIt export resolves the driver's complaint entirely, proving THIS
        // layer (Normalized/Projected) was never the defect - it was Layer 3's own reproduction of
        // SimHub's algorithm using the WRONG branch. Applying a Layer-4 compensation here would have
        // incorrectly altered the ALREADY-CORRECT ShakeIt-sourced values too (this layer cannot know
        // where rawLockWheels came from) - removed entirely; the real fix now lives in Layer 3
        // (QAdvanceFeedback.Core.RawCalculator.BrakingVsSpeedModel/RawCalculatorEngine).

        private readonly KeyedGripLearner _lockLearners;
        private readonly KeyedGripLearner _slipLearners;
        private readonly LongitudinalDirectionResolver _direction;
        private readonly TelemetryLearningGate _learningGate;

        // ---- PER-SOURCE INPUT CALIBRATION state (docs\branch-dispatch-and-source-keyed-learning-report.md).
        // _lockPhysicalReference/_slipPhysicalReference are DEDICATED, (game,car)-ONLY KeyedGripLearner
        // instances (always queried with an empty sourceIdentity - see ComputeChannel) used SOLELY to
        // detect "is this frame physically at this car's own learned grip limit", SHARED across every
        // configured source for the SAME reason _lockLearners/_slipLearners above are source-keyed:
        // the physics (grip limit, achieved deceleration) is identical regardless of which source is
        // configured, so the detector must not need to re-warm-up every time the driver switches
        // sources - only the RAW READING recorded at that shared physical moment (in
        // _lockScaleLearner/_slipScaleLearner, which ARE keyed by source) differs per source.
        private readonly KeyedGripLearner _lockPhysicalReference;
        private readonly KeyedGripLearner _slipPhysicalReference;
        private readonly KeyedScaleLearner _lockScaleLearner = new KeyedScaleLearner();
        private readonly KeyedScaleLearner _slipScaleLearner = new KeyedScaleLearner();

        // Last-computed per-source scale ceiling (native units) + which tier produced it - exposed for
        // diagnostics (Diag.Lock.SourceScaleCeiling/Diag.Slip.SourceScaleCeiling), mirroring how
        // CurrentDirection below exposes _direction's own last-resolved state.
        private double? _lockScaleCeiling;
        private bool _lockScaleCeilingIsPrimaryTier;
        private double? _slipScaleCeiling;
        private bool _slipScaleCeilingIsPrimaryTier;

        // NOTE: this used to be where the per-channel "is Raw currently active" release-envelope state
        // lived (_lockRawPresence/_slipRawPresence, defect D) - removed entirely by the F1 25 fix (see
        // the class-level history note above): severity is calibratedMean directly now, so it already
        // releases with zero added lag the instant Raw itself drops, with no envelope needed to gate
        // anything off.

        // ---- Surface-keyed learning smoothing state (see SurfaceFractionSmoothingTauSeconds' own
        // remarks) - one smoothed loose-fraction per channel, reset alongside the release envelopes
        // above (a fresh game/session should not inherit a stale "was on grass" blend).
        private double _lockLooseFraction;
        private double _slipLooseFraction;

        // PER-GAME TELEMETRY SUPPORT DETECTION (telemetry-integrity pass, item 2) - see
        // KeyedTelemetrySupport's own remarks. Deliberately NOT reset on a game switch (unlike the
        // per-channel presence/loose-fraction state below) - support is a property of the TITLE, keyed
        // and persisted per game, so it must survive exactly the game switch a plain session-scoped latch
        // would have thrown away.
        private readonly KeyedTelemetrySupport _surfaceSupport = new KeyedTelemetrySupport();
        private string _lastGameId = string.Empty;

        public NormalizedWheelLockSlipEngine(
            KeyedGripLearner lockLearners = null, KeyedGripLearner slipLearners = null,
            LongitudinalDirectionResolver directionResolver = null, TelemetryLearningGate learningGate = null)
        {
            _lockLearners = lockLearners ?? new KeyedGripLearner(LockLearnMaxPlausibleG);
            _slipLearners = slipLearners ?? new KeyedGripLearner(SlipLearnMaxPlausibleG);
            _lockPhysicalReference = new KeyedGripLearner(LockLearnMaxPlausibleG);
            _slipPhysicalReference = new KeyedGripLearner(SlipLearnMaxPlausibleG);
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

        /// <summary>The Lock channel's own per-source calibration learner (COLD/WARM persisted, item 3)
        /// - exposed so the composition root can Import/Export its cold ceilings through
        /// <c>RuntimeStore</c> at Init/every frame, mirroring <see cref="LockLearners"/>'s own exposure.</summary>
        public KeyedScaleLearner LockScaleLearner => _lockScaleLearner;

        /// <summary>The Slip channel's equivalent of <see cref="LockScaleLearner"/>.</summary>
        public KeyedScaleLearner SlipScaleLearner => _slipScaleLearner;

        /// <summary>The Lock channel's currently-configured source's own learned near-the-limit ceiling
        /// (native units, this source's own scale) - null while not yet calibrated (cold start). See
        /// <see cref="KeyedScaleLearner"/>'s own remarks.</summary>
        public double? LockScaleCeiling => _lockScaleCeiling;

        /// <summary>Whether <see cref="LockScaleCeiling"/> came from the PRIMARY (physically-anchored)
        /// tier (true) or the SECONDARY (percentile fallback) tier (false) - meaningless while
        /// <see cref="LockScaleCeiling"/> itself is null.</summary>
        public bool LockScaleCeilingIsPrimaryTier => _lockScaleCeilingIsPrimaryTier;

        /// <summary>The Slip channel's equivalent of <see cref="LockScaleCeiling"/>.</summary>
        public double? SlipScaleCeiling => _slipScaleCeiling;

        /// <summary>The Slip channel's equivalent of <see cref="LockScaleCeilingIsPrimaryTier"/>.</summary>
        public bool SlipScaleCeilingIsPrimaryTier => _slipScaleCeilingIsPrimaryTier;

        /// <summary>Whether the CURRENT game (the last one <see cref="Compute"/> was called with) is
        /// known to genuinely support loose-surface reporting - see
        /// <see cref="KeyedTelemetrySupport"/>'s own remarks (sustained evidence required, promotion
        /// instant, demotion never, persisted per game). False on a title that never populates the field
        /// meaningfully at all (degrading, by construction, to the single-reference behaviour this plugin
        /// had before surface-keying existed).</summary>
        public bool SurfaceEverReportedLoose => _surfaceSupport.IsSupported(_lastGameId);

        /// <summary>The full per-GAME support detector - exposed so the plugin composition root can
        /// Import/Export it through <c>RuntimeStore</c> at Init/every frame, mirroring
        /// <see cref="LockLearners"/>/<see cref="SlipLearners"/>'s own exposure pattern.</summary>
        public KeyedTelemetrySupport SurfaceSupport => _surfaceSupport;

        /// <summary>The Lock channel's current smoothed loose-surface fraction (0.0 = purely sealed,
        /// 1.0 = purely loose) - exposed for diagnostics/troubleshooting.</summary>
        public double LockLooseFraction => _lockLooseFraction;

        /// <summary>The Slip channel's equivalent of <see cref="LockLooseFraction"/>.</summary>
        public double SlipLooseFraction => _slipLooseFraction;

        /// <summary>The most recently resolved direction - exposed for diagnostics (e.g. a settings
        /// UI/dashboard readout of "why is the lock channel silent right now").</summary>
        public LongitudinalMotionState CurrentDirection => _direction.State;

        /// <summary>Clears the learned direction filter - call on a game/session switch, mirroring
        /// <c>SimHubTelemetryAdapter.Reset</c>. Does NOT reset the learners (see
        /// <see cref="GripLearner"/>'s own remarks on why they persist across a game switch via
        /// RuntimeStore). Also clears the surface-loose-fraction smoothing state so a fresh
        /// game/session does not inherit a stale "was on grass" blend (the release envelope this used
        /// to also clear was removed by the F1 25 fix - see this class's own history note).</summary>
        public void ResetDirection()
        {
            _direction.Reset();
            _learningGate.Reset();
            _lockLooseFraction = 0.0;
            _slipLooseFraction = 0.0;
            // _surfaceSupport is DELIBERATELY not touched here - see its own field remarks.
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
        /// this plugin family (<c>BrakeSpeedSlipModel.Compute</c>, <c>RawCalculatorEngine.Compute</c>)
        /// - every pre-existing caller/test keeps compiling and behaving exactly as before. Below the
        /// channel's own pedal threshold, THIS layer's own output (in addition to Layer 3's Raw - see
        /// <c>RawCalculatorEngine</c>) reads an unconditional zero for the whole channel - see
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
        /// <param name="lockSourceIdentity">
        /// SOURCE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md, "Part 2") -
        /// the Lock channel's current <see cref="SourceIdentity"/> (all four of its per-wheel source
        /// configurations, combined). Extends <see cref="KeyedGripLearner"/>'s key from (game,car) to
        /// (game,car,source) so switching the configured Lock source (ShakeIt export, this plugin's own
        /// Raw, a third-party property, a custom expression) gets its OWN isolated learned reference,
        /// never silently reusing a DIFFERENT source's calibration. Defaults to empty - every
        /// pre-existing caller/test that predates source-keying keeps compiling and behaving exactly as
        /// before (still a real, stable, per-(game,car) key of its own; only a caller that ALSO varies
        /// this parameter observes the new per-source isolation).</param>
        /// <param name="slipSourceIdentity">The Slip channel's equivalent of
        /// <paramref name="lockSourceIdentity"/> - independent (Slip's own four wheels may be configured
        /// to a completely different source than Lock's).</param>
        public NormalizedWheelLockSlipResult Compute(
            ITelemetrySample sample, Corners rawLockWheels, Corners rawSlipWheels,
            string gameId = "", string carId = "", LegacyThresholds? thresholds = null,
            AggregationWeights? lockAggregation = null, AggregationWeights? slipAggregation = null,
            string lockSourceIdentity = "", string slipSourceIdentity = "")
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

            // SURFACE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md) - the
            // instantaneous per-frame loose fraction (0 when the title reports no surface data at all,
            // by construction - see SurfaceLooseFraction's own remarks); smoothing happens per-channel
            // inside ComputeChannel (each channel's own release-envelope-style state).
            ITelemetryFrame frame = sample.New;
            double instantLooseFraction = SurfaceLooseFraction.Compute(
                frame?.WheelOnLooseSurfaceFrontLeft, frame?.WheelOnLooseSurfaceFrontRight,
                frame?.WheelOnLooseSurfaceRearLeft, frame?.WheelOnLooseSurfaceRearRight);

            // PER-GAME TELEMETRY SUPPORT DETECTION (item 2) - null when the field itself was not
            // reachable this frame (no evidence either way); otherwise the real true/false reading -
            // KeyedTelemetrySupport itself only ever promotes on SUSTAINED true evidence (see its own
            // remarks), so a single frame's "true" is not enough on its own.
            _lastGameId = gameId ?? string.Empty;
            bool surfaceFieldReachable = SurfaceLooseFraction.AnyWheelReported(
                frame?.WheelOnLooseSurfaceFrontLeft, frame?.WheelOnLooseSurfaceFrontRight,
                frame?.WheelOnLooseSurfaceRearLeft, frame?.WheelOnLooseSurfaceRearRight);
            _surfaceSupport.Observe(gameId, surfaceFieldReachable ? (bool?)(instantLooseFraction > 0.0) : null);

            Corners lockWheels = ComputeChannel(sample.New, rawLockWheels, motion, _lockLearners, _lockPhysicalReference, _lockScaleLearner,
                gameId, carId, lockSourceIdentity, instantLooseFraction,
                direction == LongitudinalMotionState.Slowing, lockTriggered, lockObserveAllowed, dtSeconds,
                ref _lockLooseFraction, out _lockScaleCeiling, out _lockScaleCeilingIsPrimaryTier);
            Corners slipWheels = ComputeChannel(sample.New, rawSlipWheels, motion, _slipLearners, _slipPhysicalReference, _slipScaleLearner,
                gameId, carId, slipSourceIdentity, instantLooseFraction,
                direction == LongitudinalMotionState.SpeedingUp, slipTriggered, slipObserveAllowed, dtSeconds,
                ref _slipLooseFraction, out _slipScaleCeiling, out _slipScaleCeilingIsPrimaryTier);

            WheelAggregate lockAggregate = Aggregator.Compute(lockWheels, lockWeights);
            WheelAggregate slipAggregate = Aggregator.Compute(slipWheels, slipWeights);

            return new NormalizedWheelLockSlipResult(
                lockWheels,
                lockAggregate.Front, lockAggregate.Rear, lockAggregate.Left, lockAggregate.Right, lockAggregate.All,
                slipWheels,
                slipAggregate.Front, slipAggregate.Rear, slipAggregate.Left, slipAggregate.Right, slipAggregate.All);
        }

        /// <summary>See <see cref="KeyedScaleLearner"/>'s own physical-anchor sentinel - the fixed,
        /// empty source identity <see cref="_lockPhysicalReference"/>/<see cref="_slipPhysicalReference"/>
        /// are ALWAYS queried/observed with, regardless of the real, per-channel <paramref name="sourceIdentity"/>
        /// the rest of this method uses - see this class's own remarks on why the physical detector is
        /// deliberately NOT source-keyed.</summary>
        private const string PhysicalReferenceSourceIdentity = "";

        /// <summary>See <see cref="KeyedGripLearner.MakeKey"/>'s own <c>surfaceBucket</c> parameter -
        /// the two REAL buckets this engine blends between. Public so a test (or a diagnostics readout)
        /// can query <see cref="LockLearners"/>/<see cref="SlipLearners"/> for the EXACT bucket this
        /// engine itself writes to (a frame with no surface data reported - the overwhelmingly common
        /// case absent this feature - always resolves to <see cref="SealedSurfaceBucket"/>).</summary>
        public const string SealedSurfaceBucket = "Sealed";

        /// <summary>See <see cref="SealedSurfaceBucket"/>.</summary>
        public const string LooseSurfaceBucket = "Loose";

        private static Corners ComputeChannel(
            ITelemetryFrame frame, Corners rawWheels, AchievedMotion.Result motion,
            KeyedGripLearner learners, KeyedGripLearner physicalReference, KeyedScaleLearner scaleLearner,
            string gameId, string carId, string sourceIdentity, double instantLooseFraction, bool engaged, bool triggered,
            bool observeAllowed, double dtSeconds, ref double smoothedLooseFraction,
            out double? scaleCeiling, out bool scaleCeilingIsPrimaryTier)
        {
            double w0 = ClampMath.To0100(rawWheels.FrontLeft);
            double w1 = ClampMath.To0100(rawWheels.FrontRight);
            double w2 = ClampMath.To0100(rawWheels.RearLeft);
            double w3 = ClampMath.To0100(rawWheels.RearRight);

            scaleCeiling = scaleLearner.LearnedCeiling(gameId, carId, sourceIdentity, out scaleCeilingIsPrimaryTier);

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
            // only available basis, so it is passed through (but PER-SOURCE CALIBRATED - see
            // KeyedScaleLearner's own remarks - using whatever has already been learned; there is no G
            // signal this frame to detect a fresh physical-limit moment, so nothing new is OBSERVED
            // here, only whatever calibration already exists is APPLIED) rather than reading zero,
            // garbage, or the source's own unrescaled native magnitude.
            if (motion.Level == AchievedMotion.SignalLevel.Unavailable)
            {
                return new Corners(
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w0),
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w1),
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w2),
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w3));
            }

            // "engaged" = this channel's own direction (Slowing for Lock, SpeedingUp for Slip) is what
            // LongitudinalDirectionResolver measured THIS frame - see this class's own remarks on why
            // pedal state is never consulted here. Not engaged -> nothing to attribute the magnitude
            // to at all.
            if (!engaged)
                return Corners.Zero;

            // SURFACE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md) - smooth
            // the raw per-frame loose fraction (continuity: see SurfaceFractionSmoothingTauSeconds' own
            // remarks for why this is a continuous blend, not a discrete Sealed/Loose/Mixed switch).
            smoothedLooseFraction = ExponentialSmoothTowardTarget(
                smoothedLooseFraction, instantLooseFraction, dtSeconds, SurfaceFractionSmoothingTauSeconds);

            // "Purely" sealed/loose (within SurfaceLearningPurityThreshold of 0/1) is confident enough to
            // teach that ONE bucket; anything ambiguous in between teaches NEITHER (excluded from
            // learning entirely - the safest choice for learning integrity) but still gets a LIVE
            // blended read below.
            bool confidentlySealed = smoothedLooseFraction <= SurfaceLearningPurityThreshold;
            bool confidentlyLoose = smoothedLooseFraction >= 1.0 - SurfaceLearningPurityThreshold;
            string observeBucket = confidentlySealed ? SealedSurfaceBucket : (confidentlyLoose ? LooseSurfaceBucket : null);

            if (observeAllowed && IsLongitudinallyIsolated(frame) && observeBucket != null)
            {
                // Kept purely for the Diag.Lock/Slip.LearnedPeakG/LearnerConfidence diagnostics and
                // RuntimeStore persistence continuity (docs\f1-normalization-fix-report.md) - its
                // Ratio() is no longer queried for the live severity below (see this class's own
                // history note), only Observe()'s side effect of keeping the learner itself alive.
                learners.Observe(gameId, carId, motion.MagnitudeG, sourceIdentity, observeBucket);
                // SHARED physical-limit reference (docs\branch-dispatch-and-source-keyed-learning-report.md)
                // - always the (game,car)-only source key, regardless of which source is actually
                // configured, but STILL surface-keyed (the physics genuinely differs by surface too).
                // This ONE remains live-relevant: it is what teaches KeyedScaleLearner below.
                physicalReference.Observe(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, observeBucket);
            }

            double mean = (w0 + w1 + w2 + w3) / 4.0;

            // PER-SOURCE INPUT CALIBRATION (docs\branch-dispatch-and-source-keyed-learning-report.md):
            // detect whether THIS frame is physically at this car's own learned grip limit - using the
            // SHARED, (game,car)-only physical reference, never the source-keyed one above - and, if so,
            // teach the scale learner what THIS source's own raw reading looks like at that moment.
            // Blended the same way as gripUtilization, for the same continuity reason.
            double physicalConfidenceSealed = physicalReference.Confidence(gameId, carId, PhysicalReferenceSourceIdentity, SealedSurfaceBucket);
            double physicalConfidenceLoose = physicalReference.Confidence(gameId, carId, PhysicalReferenceSourceIdentity, LooseSurfaceBucket);
            double physicalConfidence = Blend(physicalConfidenceSealed, physicalConfidenceLoose, smoothedLooseFraction);

            double physicalRatioSealed = physicalReference.Ratio(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, SealedSurfaceBucket);
            double physicalRatioLoose = physicalReference.Ratio(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, LooseSurfaceBucket);
            double physicalRatioNow = Blend(physicalRatioSealed, physicalRatioLoose, smoothedLooseFraction);

            bool physicallyAtLimit = physicalConfidence >= 1.0 && physicalRatioNow >= PhysicalLimitRatioThreshold;

            if (mean >= MinRawForCalibrationObservation)
            {
                if (physicallyAtLimit) scaleLearner.ObserveAtPhysicalLimit(gameId, carId, sourceIdentity, mean);
                scaleLearner.ObserveGeneral(gameId, carId, sourceIdentity, mean);
            }

            double calibratedMean = scaleLearner.Rescale(gameId, carId, sourceIdentity, mean);
            scaleCeiling = scaleLearner.LearnedCeiling(gameId, carId, sourceIdentity, out scaleCeilingIsPrimaryTier);

            // ---- F1 25 FIX (docs\f1-normalization-fix-report.md) - see this class's own history note
            // at the top of the file for the full derivation. Severity IS the source, calibrated: the
            // configured source (Raw or a ShakeIt export) already measures wheel lock/spin proximity
            // directly and condition-independently, so nothing (in particular, no G-force-derived
            // ratio) is allowed to push the published severity ABOVE what this frame's own calibrated
            // reading says. This is simultaneously the raw floor (severity can never read BELOW Raw
            // either - it simply IS Raw - so defects B/C stay fixed) and the release (it tracks Raw's
            // own current value every frame with no added lag - so defect D stays fixed too), with no
            // Max()/envelope needed for either property anymore.
            double severity = calibratedMean;

            double s0, s1, s2, s3;
            if (mean <= NoRawSignalEpsilon)
            {
                // No per-wheel differentiation available from Raw at all - distribute the
                // car-level severity evenly rather than favouring an arbitrary wheel.
                s0 = s1 = s2 = s3 = 1.0;
            }
            else
            {
                // Scale-invariant proportions (a uniform linear rescale of all four wheels leaves
                // w_i/mean unchanged) - deliberately built from the RAW w0..w3/mean, not the calibrated
                // ones, since calibration is a single shared scalar for this frame and therefore cancels
                // out of the ratio exactly; using raw values here avoids a redundant Rescale call per
                // wheel for a quantity that would come out identical either way.
                s0 = w0 / mean; s1 = w1 / mean; s2 = w2 / mean; s3 = w3 / mean;
            }

            return new Corners(
                ClampMath.To0100(severity * s0),
                ClampMath.To0100(severity * s1),
                ClampMath.To0100(severity * s2),
                ClampMath.To0100(severity * s3));
        }

        private static bool IsLongitudinallyIsolated(ITelemetryFrame frame)
        {
            double? lateral = frame?.LateralG;
            return !lateral.HasValue || Math.Abs(lateral.Value) <= LateralIsolationGateG;
        }

        /// <summary>Standard dt-correct exponential smoothing of <paramref name="previous"/> TOWARD
        /// <paramref name="target"/> - used for <see cref="SurfaceFractionSmoothingTauSeconds"/>'s own
        /// continuity requirement (docs\branch-dispatch-and-source-keyed-learning-report.md). A
        /// non-positive/non-finite dt holds <paramref name="previous"/> unchanged.</summary>
        private static double ExponentialSmoothTowardTarget(double previous, double target, double dtSeconds, double tauSeconds)
        {
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0) return previous;
            double alpha = 1.0 - Math.Exp(-dtSeconds / tauSeconds);
            return previous + alpha * (target - previous);
        }

        /// <summary>Linear blend between <paramref name="sealedValue"/> (weight <c>1-fraction</c>) and
        /// <paramref name="looseValue"/> (weight <paramref name="fraction"/>) - the mechanism that keeps
        /// a surface transition continuous rather than a discrete bucket switch (see
        /// <see cref="SurfaceLooseFraction"/>'s own remarks).</summary>
        private static double Blend(double sealedValue, double looseValue, double fraction)
            => sealedValue * (1.0 - fraction) + looseValue * fraction;
    }
}

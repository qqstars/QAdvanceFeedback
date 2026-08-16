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
        /// otherwise negligible raw reading (e.g. a value chosen only to clear
        /// <see cref="RawActiveThreshold"/> so the release envelope does not decay, not to represent a
        /// genuine near-limit reading - several of this engine's own calibration tests use exactly such
        /// a placeholder) for "this is what the source reads at its own ceiling". A REAL source's own
        /// near-limit reading (per the owner's own worked examples: 30 at minimum, for the smallest of
        /// the three sample sources) sits comfortably above this bar; only a deliberately tiny
        /// placeholder does not. Chosen well above <see cref="RawActiveThreshold"/> (1.0) precisely so
        /// the two thresholds serve different purposes and do not have to agree.
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

        // ---- Per-channel release-envelope state (defect D) - see the remarks above. Reset alongside
        // the direction filter (ResetDirection) so a game/session switch does not inherit a stale
        // "Raw was recently active" envelope from whatever the previous game was doing.
        private double _lockRawPresence;
        private double _slipRawPresence;

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
        /// RuntimeStore). Also clears the defect-D release envelopes (see this class's own remarks)
        /// so a fresh game/session does not inherit a stale "Raw was recently active" state.</summary>
        public void ResetDirection()
        {
            _direction.Reset();
            _learningGate.Reset();
            _lockRawPresence = 0.0;
            _slipRawPresence = 0.0;
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
                ref _lockRawPresence, ref _lockLooseFraction, out _lockScaleCeiling, out _lockScaleCeilingIsPrimaryTier);
            Corners slipWheels = ComputeChannel(sample.New, rawSlipWheels, motion, _slipLearners, _slipPhysicalReference, _slipScaleLearner,
                gameId, carId, slipSourceIdentity, instantLooseFraction,
                direction == LongitudinalMotionState.SpeedingUp, slipTriggered, slipObserveAllowed, dtSeconds,
                ref _slipRawPresence, ref _slipLooseFraction, out _slipScaleCeiling, out _slipScaleCeilingIsPrimaryTier);

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
            bool observeAllowed, double dtSeconds, ref double rawPresence, ref double smoothedLooseFraction,
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
            // to at all (unchanged from before this task's fix) - the release envelope is simply held
            // rather than advanced, so the NEXT genuinely-engaged frame does not inherit a jump.
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
                learners.Observe(gameId, carId, motion.MagnitudeG, sourceIdentity, observeBucket);
                // SHARED physical-limit reference (docs\branch-dispatch-and-source-keyed-learning-report.md)
                // - always the (game,car)-only source key, regardless of which source is actually
                // configured, but STILL surface-keyed (the physics genuinely differs by surface too).
                physicalReference.Observe(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, observeBucket);
            }

            // LIVE read: blend the Sealed-bucket and Loose-bucket ratios by the SAME smoothed fraction -
            // continuous in both the underlying ratios and the blend weight, so a surface transition
            // produces no step change in the published severity.
            double gripUtilizationSealed = learners.Ratio(gameId, carId, motion.MagnitudeG, sourceIdentity, SealedSurfaceBucket);
            double gripUtilizationLoose = learners.Ratio(gameId, carId, motion.MagnitudeG, sourceIdentity, LooseSurfaceBucket);
            double gripUtilization = ClampMath.To0100(Blend(gripUtilizationSealed, gripUtilizationLoose, smoothedLooseFraction) * 100.0);

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

            // ---- DEFECTS B/D fix - see this class's own remarks on RawActiveThreshold/ReleaseTauSeconds.
            // Instant attack (Raw active this frame -> envelope snaps to 1.0, no lag), fast release
            // (Raw inactive -> envelope decays toward 0 with ReleaseTauSeconds) - the classical
            // asymmetric attack/release shape, deliberately mirroring GForceEngine's own washout
            // filter convention elsewhere in this plugin. Uses the CALIBRATED mean (identical to the
            // raw mean during cold start - see KeyedScaleLearner's own remarks) so this threshold means
            // the same thing regardless of the configured source's own native scale.
            bool rawActiveNow = calibratedMean >= RawActiveThreshold;
            rawPresence = rawActiveNow ? 1.0 : ExponentialDecayToZero(rawPresence, dtSeconds, ReleaseTauSeconds);

            double effectiveGripUtilization = gripUtilization * rawPresence;

            // The floor: severity can never read BELOW Raw's own (calibrated) instantaneous mean - this
            // is what guarantees monotonicity in Raw (defects B/C) without needing to lag anything (the
            // floor itself is instantaneous), while effectiveGripUtilization above is what makes the
            // CEILING release quickly once Raw stops supporting it (defect D). Calibrated, not raw, so
            // the floor means the same thing ("roughly at the limit") for every configured source.
            double severity = Math.Max(effectiveGripUtilization, calibratedMean);

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

        /// <summary>Standard dt-correct exponential smoothing of <paramref name="previous"/> TOWARD
        /// <paramref name="target"/> (as opposed to <see cref="ExponentialDecayToZero"/>'s fixed
        /// zero target) - used for <see cref="SurfaceFractionSmoothingTauSeconds"/>'s own continuity
        /// requirement (docs\branch-dispatch-and-source-keyed-learning-report.md). A non-positive/
        /// non-finite dt holds <paramref name="previous"/> unchanged, same convention as
        /// <see cref="ExponentialDecayToZero"/>.</summary>
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

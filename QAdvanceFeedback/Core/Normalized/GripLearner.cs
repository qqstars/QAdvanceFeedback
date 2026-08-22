using System;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Learns the largest longitudinal motion (deceleration under braking, acceleration under
    /// throttle) THIS car has actually demonstrated, in units of g, and uses it - not a fixed
    /// physical constant - as the reference a raw g reading is compared against. One instance per
    /// channel (Lock, Slip); never shared between them, since a car's braking and driven axles can
    /// have very different peak capability.
    /// <para/>
    /// THE LESSON THIS CLASS EXISTS TO APPLY: the sibling ReliableWheelLockSlip project's equivalent
    /// (<c>GripBudgetEstimator</c>) normalised achieved deceleration against a FIXED ~1g budget
    /// whenever no per-wheel telemetry was available to drive its own learner - an arcade title
    /// pulling 4g under routine braking then divided every frame's deceleration by 1g, read
    /// ratio&gt;=4 on the very first brake stab, and clamped straight to "fully locked" for the rest
    /// of the session; a sim car's genuine 1.2g stop, by contrast, would have read only 120% under
    /// that same fixed reference - survivable, but only by luck of matching the guess. This class
    /// instead RAISES <see cref="LearnedPeakG"/> toward whatever the car actually achieves (a decaying
    /// maximum, not a ratchet: a single sensor glitch must not raise it permanently, and a car that
    /// genuinely brakes harder later must be allowed to raise it again), so both an arcade car's 4g
    /// and a sim car's 1.2g settle into "this IS roughly my peak" and produce a comparable 0-100
    /// reading for a comparable fraction of THEIR OWN peak - see
    /// <see cref="NormalizedWheelLockSlipEngineTests"/>'s arcade-vs-sim test for the acceptance case
    /// this class must pass.
    /// <para/>
    /// COLD START: before enough evidence has accumulated, a freshly-learned (or freshly-seeded)
    /// peak is not yet trustworthy - the very first hard brake of a session could BE the highest g
    /// this car will ever produce, in which case trusting it immediately would read 100 ("fully
    /// locked") for what might only be a firm, ordinary stop. <see cref="Ratio"/> ceilings its
    /// result at <see cref="ColdStartCeilingRatio"/> until <see cref="Confidence"/> (evidence count
    /// over <see cref="MaturitySamples"/>) reaches 1.0, then removes the ceiling entirely - mirroring
    /// the sibling project's own <c>ColdStartMaxRatio</c> guard, applied to a learner that (unlike
    /// the sibling's) never depends on per-wheel telemetry to mature, since Layer 4 has none to work
    /// with (see the brief's explicit ban on wheel-speed-derived slip).
    /// <para/>
    /// OUTLIER REJECTION: <see cref="Observe"/> discards non-finite, non-positive, or implausibly
    /// large (&gt; <see cref="MaxPlausibleG"/> - a session-reset teleport or a one-frame telemetry
    /// glitch, not a real tyre) readings before they can corrupt the learned peak.
    /// </summary>
    public sealed class GripLearner
    {
        /// <summary>Seed value before anything has been observed - a plausible, unremarkable
        /// starting guess, not a permanent reference: <see cref="Observe"/> moves away from it as
        /// soon as real evidence arrives, and <see cref="ColdStartCeilingRatio"/> (not this seed)
        /// is what protects the FIRST few readings from over-trusting it.</summary>
        public const double SeedPeakG = 1.0;

        /// <summary>DEFAULT learning-path reject ceiling - rejected as a sensor glitch/teleport
        /// rather than folded into the learned peak. Kept as the DEFAULT for
        /// <see cref="LearnCapG"/> (and for a plain <c>new GripLearner()</c>, e.g. every pre-existing
        /// test in this file) - a channel-specific instance may override it with a tighter, asymmetric
        /// value instead (see <see cref="NormalizedWheelLockSlipEngine"/>'s own Lock/Slip constants
        /// and docs\gforce-direction-fix-report.md for the derivation).</summary>
        public const double MaxPlausibleG = 8.0;

        /// <summary>
        /// LIVE-PATH-ONLY clamp ceiling for <see cref="Ratio"/> - deliberately HIGHER than any
        /// learning-path cap (REJECT and CLAMP are different needs, per
        /// docs\gforce-direction-fix-report.md: the learning path must REJECT an impact-magnitude
        /// reading outright, since one bad sample would otherwise become the persistent normalisation
        /// reference for every subsequent frame - see <see cref="LearnCapG"/>/<see cref="Observe"/>;
        /// the LIVE path must instead CLAMP, never reject/drop the frame, since dropping it would
        /// freeze the published severity at its last value, which feels like the plugin hanging
        /// through a crash rather than recovering immediately after it). 15g comfortably exceeds
        /// every realistic figure this fix's own brief cited (F1 braking ~5-6g, drag-launch ~4-5g)
        /// with margin, while staying well below a genuine wall-impact spike (this plugin's own
        /// captured session showed a ~19.8g-equivalent collision reading) - high enough to never be
        /// mistaken for ordinary driving, low enough to still mean something as a live ceiling.
        /// </summary>
        public const double LiveClampG = 15.0;

        /// <summary>The ratio ceiling while <see cref="Confidence"/> is still 0 - see this class's
        /// own remarks.</summary>
        public const double ColdStartCeilingRatio = 0.75;

        /// <summary>Qualifying samples for full confidence/maturity. At a typical 60fps with the
        /// engine's own pedal-committed gate, a few seconds of real braking/throttle reaches this.</summary>
        public const int MaturitySamples = 200;

        /// <summary>
        /// Minimum qualifying samples before a (game,car) key is included in
        /// <see cref="KeyedGripLearner.ExportAll"/>'s persisted snapshot at all - deliberately far
        /// below <see cref="MaturitySamples"/> (persistence across a restart should not require FULL
        /// confidence, or a merely-early session would never survive a restart at all), but high
        /// enough that a handful of frames from a five-second menu-only session cannot alone create a
        /// persisted entry for a brand-new key. Now that <see cref="TelemetryLearningGate"/> already
        /// excludes menu/pit/replay/paused/teleport frames from ever reaching <see cref="Observe"/> at
        /// all, "samples" here already means qualifying, gated observations, not raw frames - this is
        /// a second, independent line of defence specifically for persistence, not a substitute for
        /// that gate.
        /// </summary>
        public const int MinPersistSamples = 30;

        /// <summary>
        /// DECAY RATE - SPED UP (docs\branch-dispatch-and-source-keyed-learning-report.md, "settling
        /// speed" check the owner asked for): a decaying maximum is asymmetric by construction - a
        /// harder event raises it INSTANTLY (see <see cref="Observe"/>'s own <c>RaiseAlpha</c> term),
        /// but a genuinely lower new condition (the owner's own example: a dry-established reference
        /// carried into a wet session) only falls at this rate, sample by sample. A too-high carried-
        /// over reference UNDER-reports severity - exactly backwards, and quietest exactly when a wet
        /// track most needs the channel to speak up.
        /// <para/>
        /// MEASURED (not assumed): the OLD rate (0.9995) needed ~861 QUALIFYING samples (this class's
        /// own <see cref="Observe"/> calls, already gated by the engine's pedal-commitment/learning-
        /// validity filters upstream - roughly 15-25 per ordinary braking zone) to fall 35% from a
        /// carried-over peak - 30-plus braking zones, unreasonably slow for the owner's own stated bar
        /// ("settle within the first few braking zones of a session"). This rate (0.997) reaches the
        /// SAME 35% reduction in ~143 qualifying samples (roughly 3-7 braking zones) - see
        /// <c>GripLearnerTests.Learned_peak_settles_to_a_lower_condition_within_a_few_braking_zones</c>
        /// for the pinned regression check.
        /// <para/>
        /// NOT FASTER STILL, DELIBERATELY: a single ordinarily-softer braking zone (~40 qualifying
        /// samples, comfortably within realistic lap-to-lap variance, not a real condition change) drops
        /// the peak by only ~11% at this rate - self-corrected on the very next hard zone by the
        /// instant-rise mechanism. A materially faster decay would start to make the reference visibly
        /// WANDER between ordinary zones (exactly the inconsistency the owner does NOT want) rather than
        /// settling only for a genuine, sustained condition change.
        /// <para/>
        /// NOT MIGRATED TO THE SHARED ROBUST-BAND ESTIMATOR (docs\robust-auto-gforce-report.md -
        /// evaluated, PROTOTYPED, and reverted): a windowed-trimmed-band swap was implemented and
        /// measurably reduced sensitivity to a handful of high-but-plausible outliers on this plugin's
        /// own captured logs (a single injected 7.5g reading among 150 genuine ~2.7-3.4g samples moved
        /// this EMA by +0.42g but the trimmed-band prototype by only +0.05g). However, wiring it in
        /// surfaced a genuine regression in a DIFFERENT, already-pinned invariant this class's own
        /// consumer depends on - <c>RegressionFixTests.A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event</c>
        /// - because the windowed estimator converges to a low, constant input (this test's own synthetic
        /// 0.2g throughout) far FASTER and more precisely than this EMA's own slow, seed-anchored decay
        /// does, which pushed <see cref="Ratio"/> fractionally above 1.0 earlier in the test than the
        /// existing "must never publish higher than the source" guarantee tolerates.
        /// <para/>
        /// SUPERSEDED (docs\adaptive-peak-learner-report.md): the fixed-gain decaying maximum this note
        /// used to describe (unconditional <c>*= ForgetPerSample</c> decay every call, plus a fixed
        /// <c>RaiseAlpha</c>=15% raise toward any single higher reading) is REPLACED below by
        /// <see cref="AdaptivePeakState"/>/<see cref="ApplyEvidenceWeightedObservation"/> - an
        /// EVIDENCE-WEIGHTED estimator whose gain grows with CORROBORATION (repeated, consistent
        /// evidence) rather than being fixed, and whose downward movement requires evidence that looks
        /// like a genuine, repeated limit-seeking shortfall, not merely the ABSENCE of a recent high
        /// reading - see that report for the full worked derivation, tolerance-band/discriminator
        /// justification, and real-log measurement. The two BUGS this fixed-gain mechanism had (an
        /// unconditional decay that could not tell "genuinely lower now" from "didn't try this time",
        /// and a single spurious spike moving the estimate a fixed 15% regardless of whether anything
        /// ever corroborated it) are exactly what the new estimator was built to fix.
        /// </summary>
        private const double MinPeakFloor = 0.1;

        // ---- EVIDENCE-WEIGHTED ADAPTIVE PEAK ESTIMATOR (docs\adaptive-peak-learner-report.md) ----
        // Replaces the old fixed-gain decaying maximum (unconditional ForgetPerSample decay + a fixed
        // RaiseAlpha raise) for BOTH the flat scalar (_flatPeak below) and each SpeedBucket's own peak
        // (SpeedBucket.Peak) - the SAME mechanism, reused for both, exactly as the old ForgetPerSample/
        // RaiseAlpha pair used to be.
        //
        // THE SHAPE: a new observation only moves the estimate by CONFIDENCE * (observation - estimate),
        // where CONFIDENCE grows with CORROBORATION - repeated, consistent evidence at approximately the
        // SAME new level - rather than being a fixed gain applied to every single reading regardless of
        // whether anything else ever agreed with it. RAISING starts moving on the very FIRST sighting of
        // a new higher level (10%), doubling with each further corroborating sighting (20%/40%/80%/100%)
        // - matching the owner's own worked example (from 1.2g, repeated 1.5g readings: ~1.23 -> 1.28 ->
        // 1.36/1.37 -> 1.47, full trust by the 5th). LOWERING is deliberately MORE conservative: the
        // FIRST sighting of a new lower level is recorded as a candidate but moves nothing (confidence
        // 0) - only once a SECOND, separately-arriving observation corroborates the SAME lower level does
        // it start to move, then on the SAME doubling schedule (10%/20%/40%/80%/100% from the 2nd
        // sighting) - see ApplyEvidenceWeightedObservation's own remarks for why this asymmetry is the
        // load-bearing fix for "must not drift down merely because the driver stopped trying".
        //
        // "CORROBORATING" = within a TOLERANCE BAND of the level currently being corroborated (8% of that
        // level, floored at 0.05g so a low-g car is not held to an unrealistically tight absolute band) -
        // close enough to be the SAME physical claim (ordinary frame-to-frame G noise/measurement
        // variation), not so wide that a genuinely different grip level (e.g. a car braking 20-40% harder
        // or softer) could be mistaken for a repeat of the same one.
        //
        // "SEPARATELY-ARRIVING" (not just "another qualifying frame"): a COOLDOWN
        // (RaiseCooldownQualifyingSamples/LowerCooldownQualifyingSamples, in units of qualifying Observe
        // calls - this class has no timestamp/dt to work with, so a call count is the only available
        // proxy for "some real time/effort has passed") gates how often a NEW corroborating hit can be
        // credited. Without this, a single continuous braking zone spanning dozens of
        // frames-all-agreeing-with-each-other would itself look like dozens of independently
        // corroborating "sightings" and reach full trust within one push - exactly the kind of
        // single-event over-trust this task explicitly warns against ("repeated ACROSS separate braking
        // zones", not repeated frames of the same one). Tuned to ordinary-zone-scale lengths (~15-25
        // qualifying frames per zone, per ColdWarmBlend's own remarks) so a typical zone contributes
        // roughly ONE credited hit, not many - LOWER uses a wider cooldown than RAISE (see
        // LowerCooldownQualifyingSamples's own remarks for the measured reason).
        //
        // DATA QUALITY: a frame whose magnitude jumps implausibly far from the immediately preceding
        // quality-passing reading (MaxPlausibleJerkG) is treated as a collision/telemetry-glitch
        // signature and excluded from candidate/confidence bookkeeping entirely (no hit, no candidate
        // change, no Level movement) - "no weird g-force detected" earns a real confidence point; a weird
        // one earns none, regardless of how large the raw magnitude itself is (Observe's own LearnCapG
        // reject already screens the absolute magnitude; this screens the FRAME-TO-FRAME JUMP, a
        // different, complementary signal).
        private const double AdaptiveConfidenceBase = 0.10;

        /// <summary>RAISE-side tolerance band (fraction of the candidate level) - deliberately WIDER than
        /// the lower side. MEASURED (not guessed) on real logs (docs\adaptive-peak-learner-report.md): a
        /// tight (8-15%) band let genuinely repeated, genuinely-near-the-limit real braking zones keep
        /// missing each other's tolerance band (real zone-to-zone peak G varies more than a synthetic
        /// "repeat the identical value" test does - different entry speed, load transfer, exact braking
        /// point), so corroboration kept resetting to hit=1 and the fitted `g_mech` stayed chronically
        /// far below the car's own independently-measured P99 achieved G (e.g. one real log: fitted 1.20
        /// vs a P99 of 2.44 for the SAME qualifying samples) - a genuine under-convergence bug, not a
        /// tuning nicety, confirmed by an independent parallel analysis replaying all real logs. 30%
        /// resolves this (confirmed) while the LOWER side (see <see cref="LowerToleranceFraction"/>) is
        /// kept narrow, because widening THAT side instead breaks the ramp-down discriminator's entire
        /// purpose - see that constant's own remarks.</summary>
        private const double ToleranceFraction = 0.30;

        /// <summary>LOWER-side tolerance band - deliberately NARROWER than the raise side. MEASURED: at
        /// 30% (matching the raise side), varied CASUAL light driving (e.g. 0.25-0.55g, no two readings
        /// identical, none of them a genuine repeated shortfall) started corroborating ITSELF - the whole
        /// spread landed inside one 30%-wide band around its own mean - eroding an established peak from
        /// 1.5g down to 0.52g with no genuine surface change at all, exactly the failure mode this task's
        /// ramp-down discriminator exists to prevent. 15% keeps genuinely-consistent lower evidence (a
        /// real surface change, which - like hard braking - clusters near ONE new level) corroborating
        /// correctly while casual, varied-intensity driving (which does NOT cluster near one level) still
        /// fails to repeatedly match itself.</summary>
        private const double LowerToleranceFraction = 0.15;

        private const double ToleranceFloorG = 0.05;

        /// <summary>RAISE-side cooldown - a single sustained hard push should be trusted about as
        /// promptly as the owner's own worked example implies (full trust by the ~5th corroborating
        /// sighting). 12 qualifying calls keeps one ordinary zone (~15-25 qualifying frames, per
        /// ColdWarmBlend's own remarks) to roughly one credited hit.</summary>
        public const int RaiseCooldownQualifyingSamples = 3;

        /// <summary>LOWER-side cooldown - deliberately WIDER than the raise side. MEASURED (not
        /// guessed): a cooldown of 12 let a single, unbroken, ~100-frame synthetic constant-LOW-input run
        /// (no release in between - see
        /// <c>RegressionFixTests.A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event</c>)
        /// fully converge to exact-1.0 lower-confidence WITHIN that one run, tripping the physical-limit
        /// detector and producing a measurable (~0.016%) cold-start overshoot against raw - a real,
        /// caught regression, not a false alarm. This class has no timestamp/dt to distinguish "one very
        /// long continuous push" from "several genuinely separate ones", so lowering - the direction this
        /// whole task's "did-not-try vs tried-and-could-not" discriminator exists to protect - needs a
        /// wider margin than raising before an unusually long single push can look like several
        /// independently-corroborating events. 40 resolves the regression (confirmed) while still
        /// converging within the owner's own ~4-6-EVENT bar once genuinely SEPARATE events provide the
        /// evidence (confirmed against the real captured logs, docs\adaptive-peak-learner-report.md).
        /// </summary>
        public const int LowerCooldownQualifyingSamples = 40;

        private const double MaxPlausibleJerkG = 3.0;

        /// <summary>SATURATION CAP (docs\adaptive-peak-learner-report.md, Part 1 - the Int32 overflow
        /// fix) shared by every persisted/accumulating sample counter this class owns
        /// (<see cref="_samples"/>, <see cref="SpeedBucket.Count"/>) - see this report's own remarks for
        /// why 1,000,000 is ample headroom for every real consumer (a ratio clamped at a scale of 200, or
        /// a boolean threshold at 20/30) while staying far below <see cref="int.MaxValue"/>.</summary>
        public const int SampleCountSaturationCap = 1_000_000;

        // ---- STABILITY-GATED MATURITY (docs\stability-confidence-fix-report.md) ----
        // THE BUG THIS FIXES: Confidence/MaturitySamples answers "have we seen 200 qualifying samples",
        // NOT "has the reference actually stopped moving" - measured directly against all nine real logs
        // (docs\band-accuracy-simulation-report.md, Table 3): 13,708 of ~73,000 qualifying frames
        // published a severity that a cold-start ceiling fully believed (confidence already 1.0) even
        // though the SAME session's own later evidence showed the reference was still climbing -
        // including 3,858 frames reaching the literal maximum (100, "fully locked/spun") while the same
        // session's hindsight value was as low as 3.0. "Enough evidence to trust the CURRENT estimate"
        // is not "the estimate has reached its true asymptote".
        //
        // THE FIX: MaturityConfidence (what Ratio's cold-start ceiling actually gates on now, replacing
        // the old sample-count-only Confidence for that ONE purpose - Confidence itself is UNCHANGED,
        // still the linear sample-count ramp, kept for its own pre-existing diagnostic contract) is the
        // OLD linear sample-count term MULTIPLIED by a continuous STABILITY term reflecting how long
        // it has been since the flat peak's own Level last moved meaningfully. A RAISE or a genuinely
        // corroborated LOWER hit that actually shifts Level resets/reduces this "quiet streak"
        // proportionally to how big the move was (a tiny move barely dents it; a big one resets it
        // close to zero) - continuous, never a step. Once the streak has run long enough with no
        // meaningful movement (StabilityScaleSamples, a SOFT scale reference exactly like
        // ColdWarmBlend's own ConcaveCountConfidence/CalibrationConfidenceScaleSamples precedent - NOT a
        // requirement, and NOT a second hard sample gate: it engages from the very first quiet call and
        // simply keeps approaching, then EXACTLY reaching, 1.0 the longer the reference stays put), the
        // stability term reaches exactly 1.0 - REACHABLE, unlike KeyedScaleLearner's own dispersion
        // term (which is a property of inherently-noisy raw DATA and can never truly hit zero for real
        // driving); here the quantity being measured is the ESTIMATE's own trajectory, which genuinely
        // does stop moving once the underlying evidence has actually converged, so no artificial floor
        // (like KeyedScaleLearner's "regardless of dispersion" full-trust override at 200 samples,
        // docs\anchor-rescale-report.md) is needed or wanted here - see this class's own report for the
        // full derivation, real-log measurement, and why that floor mechanism was deliberately NOT
        // reused as-is.
        private const double StabilityMoveResetFraction = 0.01;

        /// <summary>SOFT scale reference (see this section's own remarks) for how many consecutive
        /// "quiet" (no meaningful Level movement) qualifying observations are needed before the
        /// stability term reaches full trust - deliberately far smaller than
        /// <see cref="MaturitySamples"/> (200), since this measures SETTLING time after the reference
        /// stops moving, not total evidence volume; a genuinely stable reference settles within a
        /// fraction of a second of further qualifying frames, not another 200-sample wait.</summary>
        public const double StabilityScaleSamples = 120.0;

        /// <summary>0..1, EXACTLY 1.0 once <paramref name="quietStreak"/> reaches
        /// <see cref="StabilityScaleSamples"/> (a genuinely reachable value, not an asymptote - see this
        /// section's own remarks) - the same concave (front-loaded) shape
        /// <see cref="ColdWarmBlend.ConcaveCountConfidence"/> already uses elsewhere in this codebase
        /// (gamma=2, <c>1-(1-x)^2</c>), reproduced directly here (rather than calling that helper) since
        /// <paramref name="quietStreak"/> is a continuous double (partial resets), not an integer
        /// count.</summary>
        private static double StabilityWeight(double quietStreak)
        {
            double x = ClampMath.Clamp(quietStreak / StabilityScaleSamples, 0.0, 1.0);
            double oneMinusX = 1.0 - x;
            return 1.0 - oneMinusX * oneMinusX;
        }

        /// <summary>
        /// One channel's (flat-scalar OR one speed bucket's) own evidence-weighted peak-estimator state -
        /// see this class's own remarks above <see cref="AdaptiveConfidenceBase"/> for the full mechanism.
        /// A plain mutable struct (not a class) so both <see cref="_flatPeak"/> and each
        /// <see cref="SpeedBucket"/>'s own <see cref="SpeedBucket.Peak"/> can hold one inline, with no
        /// extra allocation, mirroring how <see cref="SpeedBucket"/> itself is already a plain struct.
        /// </summary>
        private struct AdaptivePeakState
        {
            public double Level;

            public double RaiseCandidateG;
            public int RaiseHits;

            public double LowerCandidateG;
            public int LowerHits;

            /// <summary>Qualifying-call countdown before the NEXT hit may be credited in either
            /// direction - see this class's own "SEPARATELY-ARRIVING" remarks above.</summary>
            public int Cooldown;

            public double LastRawG;
            public bool HasLastRaw;

            /// <summary>STABILITY-GATED MATURITY (see the remarks above <see cref="StabilityWeight"/>) -
            /// consecutive qualifying observations since <see cref="Level"/> last moved meaningfully,
            /// reduced (never below 0) proportionally to how big the most recent move was, rather than
            /// hard-reset to exactly 0 - continuous, no step. Starts at 0 (maximally unstable/no
            /// evidence yet) for a brand-new state, matching this whole class's own "identity/no trust
            /// until evidence says otherwise" convention.</summary>
            public double QuietStreak;

            public static AdaptivePeakState Seeded(double level) => new AdaptivePeakState { Level = level };
        }

        /// <summary>0..1 confidence for the Nth RAISING hit - <c>min(1, base * 2^(hits-1))</c>: 10%, 20%,
        /// 40%, 80%, then capped at 100% from the 5th corroborating sighting onward. See this class's own
        /// remarks for the worked example this reproduces.</summary>
        private static double RaiseConfidenceForHits(int hits)
        {
            if (hits <= 0) return 0.0;
            double confidence = AdaptiveConfidenceBase * Math.Pow(2.0, hits - 1);
            return confidence >= 1.0 ? 1.0 : confidence;
        }

        /// <summary>0..1 confidence for the Nth LOWERING hit - deliberately ONE SIGHTING MORE CONSERVATIVE
        /// than <see cref="RaiseConfidenceForHits"/>: the very first sighting of a new, lower level
        /// contributes ZERO confidence (a candidate is recorded, but nothing moves) - only the SECOND,
        /// separately-arriving corroborating sighting starts to move the estimate, then on the identical
        /// doubling schedule from there (10%, 20%, 40%, 80%, 100%). This is the load-bearing half of the
        /// ramp-down discriminator: an isolated single low reading (a driver momentarily not pushing as
        /// hard) can never, by itself, move the estimate down at all - only a REPEATED, consistent
        /// shortfall can, exactly implementing "tried and could not" (repeats) versus "did not try" (an
        /// isolated one-off that is never corroborated again before something else supersedes it).</summary>
        private static double LowerConfidenceForHits(int hits) => hits < 2 ? 0.0 : RaiseConfidenceForHits(hits - 1);

        /// <summary>
        /// Applies one qualifying observation to <paramref name="state"/> - the shared mechanism behind
        /// BOTH <see cref="_flatPeak"/> and every <see cref="SpeedBucket.Peak"/>. See this class's own
        /// remarks above <see cref="AdaptiveConfidenceBase"/> for the full derivation.
        /// </summary>
        private static void ApplyEvidenceWeightedObservation(ref AdaptivePeakState state, double magnitudeG)
        {
            // DATA QUALITY GATE: an implausible frame-to-frame jump (collision/telemetry-glitch
            // signature) earns no confidence at all this frame - excluded before it can ever start or
            // corroborate any candidate, in either direction.
            if (state.HasLastRaw && Math.Abs(magnitudeG - state.LastRawG) > MaxPlausibleJerkG)
                return;

            state.LastRawG = magnitudeG;
            state.HasLastRaw = true;

            if (state.Cooldown > 0)
            {
                state.Cooldown--;
                // STABILITY (see the remarks above StabilityWeight): a cooldown call is, by
                // construction, a call where Level cannot possibly move this frame - count it as one
                // more "quiet" observation toward the settling streak.
                state.QuietStreak += 1.0;
                return;
            }

            double levelBeforeThisObservation = state.Level;

            if (magnitudeG > state.Level)
            {
                double bandReference = state.RaiseHits > 0 ? state.RaiseCandidateG : magnitudeG;
                double band = Math.Max(ToleranceFraction * bandReference, ToleranceFloorG);
                bool matches = state.RaiseHits > 0 && Math.Abs(magnitudeG - state.RaiseCandidateG) <= band;

                // INT32 OVERFLOW GUARD (docs\release-1060-report.md, Part 5 overflow audit) - saturate
                // rather than let this wrap negative after enough cumulative real-world corroborating
                // hits. Harmless functionally (RaiseConfidenceForHits already clamps to 1.0 by the 5th
                // hit, so hits beyond that change nothing), but the counter itself must still not
                // overflow over a genuinely multi-year, never-restarted session.
                state.RaiseHits = matches ? (state.RaiseHits < SampleCountSaturationCap ? state.RaiseHits + 1 : state.RaiseHits) : 1;
                state.RaiseCandidateG = magnitudeG;
                // Fresh, confirmed-higher evidence supersedes whatever lower candidate was in progress -
                // it is no longer informative about where the CURRENT level actually sits.
                state.LowerHits = 0;

                double confidence = RaiseConfidenceForHits(state.RaiseHits);
                state.Level += confidence * (magnitudeG - state.Level);
                state.Cooldown = RaiseCooldownQualifyingSamples;
            }
            else if (magnitudeG < state.Level)
            {
                double bandReference = state.LowerHits > 0 ? state.LowerCandidateG : magnitudeG;
                double band = Math.Max(LowerToleranceFraction * bandReference, ToleranceFloorG);
                bool matches = state.LowerHits > 0 && Math.Abs(magnitudeG - state.LowerCandidateG) <= band;

                // INT32 OVERFLOW GUARD - see the RaiseHits guard's own remarks just above; identical
                // reasoning applies to the lowering side.
                state.LowerHits = matches ? (state.LowerHits < SampleCountSaturationCap ? state.LowerHits + 1 : state.LowerHits) : 1;
                state.LowerCandidateG = magnitudeG;

                double confidence = LowerConfidenceForHits(state.LowerHits);
                if (confidence > 0.0)
                {
                    state.Level += confidence * (magnitudeG - state.Level);
                    state.Cooldown = LowerCooldownQualifyingSamples;
                }
                // else: an uncorroborated first sighting of a new lower level - recorded as a candidate
                // for a FUTURE corroborating sighting to confirm, but moves nothing yet, and spends no
                // cooldown (so it does not delay a genuinely corroborating sighting arriving right
                // after it).
            }

            // STABILITY (see the remarks above StabilityWeight): how much THIS call actually moved
            // Level, as a fraction of where it started - a proportional, continuous reduction of the
            // quiet streak (never a hard reset to a fixed value), so a tiny nudge barely dents
            // settledness while a large revision resets it close to zero. A call that moved nothing
            // (magnitude equalled Level, or a candidate was recorded with zero confidence) simply adds
            // to the streak with no reduction at all.
            double relativeMove = Math.Abs(state.Level - levelBeforeThisObservation)
                / Math.Max(levelBeforeThisObservation, MinPeakFloor);
            if (relativeMove > 0.0)
                state.QuietStreak *= ClampMath.Clamp(1.0 - relativeMove / StabilityMoveResetFraction, 0.0, 1.0);
            state.QuietStreak += 1.0;

            if (state.Level < MinPeakFloor) state.Level = MinPeakFloor;
        }

        // ---- SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md, "Stage A") ----
        // THE BUG: LearnedPeakG above is a single scalar mixing every speed this car has ever
        // braked/accelerated at, so a flat peak learned mostly from high-speed evidence makes a
        // genuine LOW-speed lockup permanently unrecognisable as "at the limit" (measured directly on
        // 7 real captured logs - see the report's Part 1 - a 0-1.5% "physically at limit" hit rate
        // below 50 km/h climbing to 70-97% above 200 km/h, in EVERY session). THE FIX: peak achievable
        // g is instead modelled as peakG(v) = g_mech + k * v_mps^2 (an aero car's downforce/drag both
        // rise with the square of speed), fitted per-instance from a small, fixed set of speed
        // buckets - see <see cref="FitSpeedModel"/> for the guarded weighted least-squares fit, and
        // <see cref="SpeedAwarePeakG"/> for the model actually queried by <see cref="Ratio"/>.
        // Deliberately BUCKETED, not a per-frame regression: Slip's own real data has a genuinely
        // different (rise-then-fall, traction-then-power-limited) shape that a naive global
        // regression would fight against, while feeding bucket MAXIMA into the SAME guarded fit lets
        // one mechanism correctly accept a rising shape for Lock and reject it for Slip (the
        // falling-tail guard below), without two different fitting algorithms.
        /// <summary>
        /// SPEED-AWARE REFERENCE FIX (docs\speed-aware-reference-fix-report.md) - REPLACES the old
        /// AdaptivePeakState-based decaying maximum for EACH speed bucket's own <see cref="Peak"/>-style
        /// field (the FLAT scalar, <see cref="_flatPeak"/>, is UNCHANGED - the collapse-detector/
        /// cold-start-ceiling/at-limit-plausibility machinery all read the FLAT peak, and must stay
        /// independent of the very reference this fix corrects, or the fix would be circular).
        /// <para/>
        /// TWO DISTINCT, MEASURED DEFECTS this replaces the old mechanism to fix:
        /// <list type="number">
        /// <item>DECAY EROSION - a corroboration-gated estimator (the flat scalar's own
        /// <see cref="AdaptivePeakState"/>) under-converges badly at a single bucket's own sparse sample
        /// rate (MEASURED, docs\two-signal-band-mapping-report.md: "Lock under-converges - 90-96% of
        /// frames read &gt;=100% of ref" with the old per-bucket mechanism, vs a per-bucket P99-style
        /// estimator recovering physically-plausible aero fits in all 7 dry logs checked).</item>
        /// <item>UNREACHED LIMITS - even a perfect estimator of "the highest G achieved in this bucket" is
        /// not the same thing as "the grip available in this bucket" whenever the driver's own achieved G
        /// there was never actually limit-bound (an F1 car's high-speed braking is brake-torque-limited,
        /// not grip-limited - downforce leaves real margin in reserve). See <see cref="Observe"/>'s own
        /// <c>atLimitWeight</c> parameter for the fix.</item>
        /// </list>
        /// <see cref="RobustBandEstimator"/> - already shipped and trusted for exactly this
        /// "representative upper value from a noisy stream, NO minimum-sample gate" job elsewhere in this
        /// codebase (<see cref="GForce.GForceMaxLearner"/>) - is reused here rather than reinvented for
        /// defect 1. Defect 2 is fixed upstream of this class entirely (which frames are even OFFERED to
        /// <see cref="Estimator"/> - see <see cref="Observe"/>), so the aero fit
        /// (<see cref="FitSpeedModel"/>) is built ONLY from buckets with genuine at-limit evidence, then
        /// EXTRAPOLATES to speeds where the limit was never credibly reached - exactly the shape
        /// <c>g_mech + k*v^2</c> exists for.
        /// <para/>
        /// RAMP-DOWN (the owner's own explicit requirement a plain percentile cannot satisfy alone,
        /// docs\adaptive-peak-learner-report.md's own original objection to P99): <see cref="Estimator"/>
        /// is WINDOWED (see <see cref="SpeedBucketWindowQualifyingTicks"/>), so once a genuine, sustained
        /// lower-grip condition (wet track, worn tyres) replaces a previously dry/fresh one, the stale HIGH
        /// samples age out of the window on their own and the pool-based estimate follows the new, lower
        /// evidence down - a trailing window, not a one-way ratchet.
        /// </summary>
        private struct SpeedBucket
        {
            /// <summary>This bucket's own windowed, at-limit-gated robust estimator - see this struct's
            /// own remarks. Constructed fresh (never null) by <see cref="NewBuckets"/> for every instance
            /// (including a re-<see cref="Load"/>) - never left at its type default.</summary>
            public RobustBandEstimator Estimator;

            /// <summary>All-time (un-windowed, saturating - see <see cref="Observe"/>'s own overflow-guard
            /// remarks), the SAME pairing convention as before this fix: the bucket's own mean speed is
            /// deliberately NOT re-derived from <see cref="Estimator"/>'s own current window (a fixed
            /// 0/50/100/150/200/250 km/h band's own true mean speed barely moves over a session, so the
            /// simpler all-time approximation this class already used pre-fix is kept rather than adding a
            /// second windowed accumulator for negligible benefit).</summary>
            public double SpeedSumMps;
            public int Count;
        }

        /// <summary>Fixed speed-bucket edges (km/h) <see cref="Observe"/> sorts an observation into and
        /// <see cref="FitSpeedModel"/> fits across - 0/50/100/150/200/250, mirroring the bands the real
        /// captured logs were measured against in the report. A small, FIXED set (not adaptive/learned)
        /// so the fit is cheap to recompute from scratch on every query (see <see cref="FitSpeedModel"/>'s
        /// own remarks on why there is no caching).</summary>
        private static readonly double[] SpeedBucketEdgesKmh = { 0.0, 50.0, 100.0, 150.0, 200.0, 250.0 };

        /// <summary>A speed reading outside 0..this is treated as absent for the speed dimension (never
        /// folded into a bucket, never used to look up <see cref="SpeedAwarePeakG"/>) - guards a garbage
        /// or teleport-scale speed value from corrupting the speed-bucketed model, mirroring
        /// <see cref="MaxPlausibleG"/>'s own role for the magnitude dimension.</summary>
        public const double MaxPlausibleSpeedKmh = 500.0;

        /// <summary>Bucket resolution for each bucket's own <see cref="RobustBandEstimator"/> - mirrors
        /// <see cref="GForce.GForceMaxLearner"/>'s own <c>BucketsPerG</c> convention (fine enough that the
        /// pool's own pro-rated-bucket approximation, see that class's own remarks, is never visible at
        /// G-force scale).</summary>
        private const int RobustEstimatorBucketsPerG = 100;

        /// <summary>Per-bucket window width for <see cref="RobustBandEstimator"/>'s own real-time-window
        /// eviction, expressed in units of "qualifying <see cref="Observe"/> calls" rather than literal
        /// seconds (this class has no wall-clock/dt available - see <see cref="_syntheticTicks"/>'s own
        /// remarks). Sized well above the flat scalar's own settling target (~143 qualifying samples, see
        /// the DECAY RATE history note above) because a SINGLE bucket's own admission rate is far sparser
        /// than the flat scalar's (gated by BOTH the speed match AND the at-limit weight - see
        /// <see cref="Observe"/>) - too short a window would starve an infrequently-visited (e.g.
        /// high-speed) bucket of its own history long before any genuine condition change, which would be
        /// indistinguishable from ramp-down. MEASURED against the real captured logs
        /// (docs\speed-aware-reference-fix-report.md) to still let the Sauber/wet vs Red Bull/dry
        /// difference show up within a single session while leaving enough per-bucket history for the
        /// aero fit to stay populated.</summary>
        private const int SpeedBucketWindowQualifyingTicks = 4000;

        /// <summary>Monotonically increasing per-instance "clock" - one tick per qualifying
        /// <see cref="Observe"/> call, regardless of which bucket (if any) the observation lands in - the
        /// only notion of elapsed time this class has ever had (no real timestamp/dt is available here).
        /// Converted to a synthetic <see cref="DateTime"/> (<see cref="SyntheticTimestamp"/>) purely so
        /// <see cref="RobustBandEstimator"/>'s own real-time-window eviction has something monotonic to
        /// compare against; the actual VALUE has no wall-clock meaning. Advancing this on EVERY qualifying
        /// call (not only ones a bucket accepts) mirrors real elapsed "braking/accelerating effort" far
        /// more closely than wall-clock time would - idle, non-qualifying time between corners does not
        /// erode a bucket's own window just because real time passed.</summary>
        private long _syntheticTicks;

        private DateTime SyntheticTimestamp() => DateTime.MinValue.AddSeconds(_syntheticTicks);

        /// <summary>Per-bucket pool tuning (docs\speed-aware-reference-fix-report.md), DELIBERATELY
        /// TIGHTER than <see cref="RobustBandEstimator"/>'s own shared defaults
        /// (<see cref="RobustBandEstimator.DefaultTopTrimFraction"/> 5% / <see cref="RobustBandEstimator.DefaultPoolBandFraction"/>
        /// 10% / <see cref="RobustBandEstimator.MaxMeanBlendWeight"/> 75% max). Those defaults were tuned
        /// for <see cref="GForce.GForceMaxLearner"/>'s own job - "a representative UPPER value, damped
        /// against a single outlier" - which deliberately sits WELL below the population's true maximum
        /// (a P90-ish figure). THIS job needs the estimate to behave like a genuine P99: "the ratio must
        /// not systematically exceed 1.0" (the owner's own headline acceptance bar, order 1% of frames,
        /// not the 5-15%+ a P90-ish pool would leave). MEASURED (docs\speed-aware-reference-fix-report.md):
        /// the shared defaults left a pooled 30.9% of braking frames still reading ratio&gt;1.0 across the
        /// 14 real logs (down from 51.5% pre-fix, but far short of the "small tail" bar) - this tighter
        /// pool (exclude only the top 1%, pool the next 5%, weight 90% toward the pool's own max) trades
        /// some of the outlier-damping <see cref="GForce.GForceMaxLearner"/> wants for the much higher
        /// percentile THIS job needs, while still never trusting the single raw highest sample alone
        /// (n&gt;1 still excludes and pools, exactly like the shared class always has).</summary>
        private const double SpeedBucketTopTrimFraction = 0.01;
        private const double SpeedBucketPoolBandFraction = 0.05;
        private const int SpeedBucketMinPoolSize = 5;
        private const double SpeedBucketMaxMeanBlendWeight = 0.9;

        private SpeedBucket[] NewBuckets()
        {
            int bucketCount = Math.Max(64, (int)Math.Ceiling(LearnCapG * RobustEstimatorBucketsPerG));
            var buckets = new SpeedBucket[SpeedBucketEdgesKmh.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                buckets[i] = new SpeedBucket
                {
                    Estimator = new RobustBandEstimator(
                        0.0, LearnCapG, bucketCount, TimeSpan.FromSeconds(SpeedBucketWindowQualifyingTicks),
                        SpeedBucketTopTrimFraction, SpeedBucketPoolBandFraction, SpeedBucketMinPoolSize, SpeedBucketMaxMeanBlendWeight)
                };
            }
            return buckets;
        }

        /// <summary>Guard 1 (docs\speed-aware-grip-report.md, Part 3.5): fewer than this many populated
        /// speed buckets cannot support a genuine two-parameter fit - falls back to the flat, universal
        /// (<see cref="LearnedPeakG"/>, k=0) model instead of extrapolating from a single data point.</summary>
        private const int MinPopulatedBucketsForFit = 2;

        /// <summary>Guard 2: the populated buckets' own mean-speed spread (km/h) must be at least this
        /// wide before a fit is trusted - a session that only ever braked/accelerated in one narrow band
        /// (e.g. a single repeated corner) cannot support extrapolating an aero curve from it.
        /// <para/>
        /// RE-TUNED DOWN from 80 to 30 (docs\speed-aware-reference-fix-report.md, defect 2's own fix):
        /// this guard was tuned against the OLD, ungated population (every achieved-G reading in a
        /// bucket, so a session that braked anywhere near the limit at all naturally populated a WIDE
        /// spread of buckets). Once <see cref="Observe"/>'s own <c>atLimitWeight</c> gate restricts
        /// admission to genuinely at-limit frames only, a real, otherwise-healthy session can legitimately
        /// have its at-limit evidence concentrated in a NARROW speed band (the owner's own example: an F1
        /// car only ever reaches the true limit in one speed range within a given braking zone) - MEASURED
        /// directly (one real Red Bull/dry log, I_1_6_5/Raw): at-limit evidence populated ONLY the 200 and
        /// 250 km/h buckets (spread 33 km/h), which the OLD 80 km/h bar rejected outright, falling back to
        /// the FLAT reference (3.87g) for those same high-speed frames even though the bucket's own
        /// directly observed peak there was 3.99-4.12g - reproducing a ratio&gt;1.0 in 67% of that log's own
        /// braking frames, unmoved by any other tuning. 30 km/h still requires genuinely DIFFERENT speeds
        /// (not a single repeated corner, which the guard's own original purpose still guards against) while
        /// no longer rejecting the SMALLEST spread this task's own at-limit-gated evidence can realistically
        /// produce from two adjacent populated buckets (50 km/h apart by construction - see
        /// <see cref="SpeedBucketEdgesKmh"/> - whose own MEAN speeds, inside a 50 km/h-wide band each,
        /// necessarily sit closer together than the band edges themselves).</summary>
        private const double MinAeroFitSpeedSpreadKmh = 30.0;

        /// <summary>Guard 7 (extrapolation-overshoot): the fitted g_mech must not exceed the single
        /// highest g this instance has EVER recorded this session (<see cref="_flatPeak"/>.<see cref="AdaptivePeakState.Level"/>) by more
        /// than this fraction - since g_mech is the model's own smallest predicted value (v=0), it cannot
        /// legitimately exceed the largest g ever demonstrated at ANY speed. Found and tuned by observing
        /// exactly this failure live on one real log (session-230140) - see the report's own Part 3.5.</summary>
        private const double MaxGMechOvershootOfLearnedPeak = 1.3;

        /// <summary>Guard 4 (the "falling tail" signature, added in response to the mid-task physics
        /// correction - docs\speed-aware-grip-report.md, Part 0/3.5): if any higher-speed bucket's own
        /// decaying max sits more than 15% below the running maximum any lower-speed bucket already
        /// demonstrated, the rising (k&gt;=0) fit is rejected outright regardless of the overall weighted
        /// slope - a rising model is the wrong SHAPE for data with that signature (traction-limited at
        /// low speed, power/drag-limited at high speed, e.g. Slip). This is what makes k settle to 0 for
        /// Slip on every one of the 7 real logs checked.</summary>
        private const double FallingTailRejectFraction = 0.85;

        // NOTE: NOT initialized inline (unlike before this fix) - NewBuckets() depends on LearnCapG,
        // which is only assigned inside the constructor body (field initializers run before it) - see the
        // constructor's own final line.
        private SpeedBucket[] _buckets;

        /// <summary>The flat scalar's own evidence-weighted peak-estimator state - see
        /// <see cref="AdaptivePeakState"/>. <see cref="LearnedPeakG"/> reads
        /// <see cref="AdaptivePeakState.Level"/> directly - same public meaning as always ("the highest g
        /// this car has demonstrated, regardless of speed"), new mechanism underneath.</summary>
        private AdaptivePeakState _flatPeak = AdaptivePeakState.Seeded(SeedPeakG);

        private int _samples;

        /// <summary>THIS SESSION's own corroboration-in-progress for the flat scalar - exposed read-only
        /// so <see cref="KeyedGripLearner.ExportAll"/> can persist it (docs\adaptive-peak-learner-report.md
        /// - "a returning car resumes warm rather than relearning its confidence from scratch") and
        /// <see cref="Load(double,int,double,double,double,int,double,int)"/> can restore it. Deliberately
        /// NOT cold/warm blended like <see cref="PublishedPeakG"/> - a corroboration count is discrete
        /// bookkeeping about evidence gathered so far, not a physical quantity meaningful to interpolate
        /// between a cold and a hot value.</summary>
        public double RaiseCandidateG => _flatPeak.RaiseCandidateG;
        public int RaiseCandidateHits => _flatPeak.RaiseHits;
        public double LowerCandidateG => _flatPeak.LowerCandidateG;
        public int LowerCandidateHits => _flatPeak.LowerHits;

        // ---- COLD/WARM PERSISTENCE (telemetry-integrity pass, item 3). _flatPeak/_samples above
        // remain exactly what they always were - THIS session's own decaying-maximum "hot" state,
        // unconditionally updated by Observe, unconditionally used by Confidence/the cold-start ceiling.
        // _coldPeakG is a SEPARATE, persisted-from-a-PREVIOUS-session reference, set only by Load - see
        // that method's own remarks. _hasCold distinguishes "no persisted value was ever loaded" (every
        // pre-existing caller/test, and a genuinely brand-new key) from "a real cold reference exists" -
        // only in the latter case does PublishedPeakG/Ratio blend at all; otherwise this class behaves
        // EXACTLY as it always has (this task's own explicit "cold start with no persisted value behaves
        // as today" requirement, satisfied by construction rather than by a special-cased branch).
        // _coldGMech/_coldK are the SAME cold/warm treatment applied to the speed model's own two
        // parameters (see PublishedGMech/PublishedK/PersistedGMech/PersistedK below) - seeded only by
        // Load, alongside _coldPeakG.
        private double _coldPeakG;
        private double _coldGMech;
        private double _coldK;
        private bool _hasCold;
        private WelfordAccumulator _hotSession;

        public double LearnedPeakG => _flatPeak.Level;
        public int Samples => _samples;

        /// <summary>
        /// THE PUBLISHED REFERENCE <see cref="Ratio"/> actually divides by: a continuous blend of the
        /// persisted COLD reference and this session's own HOT decaying maximum, weighted by HOT's own
        /// dispersion (see <see cref="ColdWarmBlend"/>) - NOT sample count alone, per the owner's own
        /// "if the hot data will cause more noise, we'd prefer cold data only" constraint. Identical to
        /// <see cref="LearnedPeakG"/> whenever no cold reference was ever loaded (see this class's own
        /// remarks on <see cref="_hasCold"/>).
        /// </summary>
        public double PublishedPeakG
        {
            get
            {
                if (!_hasCold) return _flatPeak.Level;
                return ColdWarmBlend.Blend(_coldPeakG, _flatPeak.Level, HotWeightForBlend());
            }
        }

        /// <summary>The cold/warm blend weight shared by <see cref="PublishedPeakG"/>,
        /// <see cref="PublishedGMech"/>, and <see cref="PublishedK"/> - factored out once these three
        /// grew to share the identical "blend this session's HOT value with the persisted COLD one,
        /// weighted by HOT's own dispersion" shape (see <see cref="ColdWarmBlend"/>'s own remarks).</summary>
        private double HotWeightForBlend() => ColdWarmBlend.HotWeight(_hotSession.Count, _hotSession.CoefficientOfVariation);

        /// <summary>
        /// What SHOULD be written back to persistence for this key right now: the persisted COLD value,
        /// UNCHANGED, unless this session's own HOT evidence clears BOTH bars of
        /// <see cref="ColdWarmBlend.ShouldPersist"/> (minimum samples AND low dispersion) - "HOT must not
        /// corrupt COLD" (the owner's own requirement): a noisy session, however long, never overwrites a
        /// good persisted profile; a short session, however clean, does not either. When no cold
        /// reference was ever loaded at all (a brand-new key, or any pre-existing caller that never calls
        /// <see cref="Load"/>), there is nothing to protect yet - HOT itself becomes the value persisted,
        /// exactly as this class always did before this task.
        /// </summary>
        public double PersistedPeakG
        {
            get
            {
                if (!_hasCold) return _flatPeak.Level;
                return ColdWarmBlend.ShouldPersist(_hotSession.Count, _hotSession.CoefficientOfVariation) ? PublishedPeakG : _coldPeakG;
            }
        }

        /// <summary>THIS SESSION's own fitted mechanical (v=0) grip term - see <see cref="FitSpeedModel"/>
        /// for the guarded weighted least-squares fit this is computed fresh from on every read (see that
        /// method's own remarks on why there is no caching). Falls back to <see cref="LearnedPeakG"/>
        /// (the flat scalar) whenever any guard rejects the fit - so "no genuine speed-dependence taught
        /// yet" reads identically to the pre-speed-model behaviour, not a second, differently-computed
        /// flat value.</summary>
        public double GMech
        {
            get
            {
                FitSpeedModel(out double gMech, out _);
                return gMech;
            }
        }

        /// <summary>THIS SESSION's own fitted aero/drag term (peakG per (m/s)^2) - see
        /// <see cref="FitSpeedModel"/>. 0.0 whenever any guard rejects the fit (narrow speed band, too few
        /// populated buckets, a falling-tail signature, a negative slope, or an implausible result) -
        /// <see cref="SpeedAwarePeakG"/> then degenerates to the flat <see cref="GMech"/> for every
        /// speed, bit-identical to today's shipped behaviour.</summary>
        public double K
        {
            get
            {
                FitSpeedModel(out _, out double k);
                return k;
            }
        }

        /// <summary>The cold/warm blend of <see cref="GMech"/> with the persisted COLD mechanical term -
        /// mirrors <see cref="PublishedPeakG"/>'s own blend exactly (same weight, same reasoning).
        /// Identical to <see cref="GMech"/> whenever no cold reference was ever loaded.</summary>
        public double PublishedGMech
        {
            get
            {
                if (!_hasCold) return GMech;
                return ColdWarmBlend.Blend(_coldGMech, GMech, HotWeightForBlend());
            }
        }

        /// <summary>The cold/warm blend of <see cref="K"/> with the persisted COLD aero term - mirrors
        /// <see cref="PublishedGMech"/>'s own remarks exactly.</summary>
        public double PublishedK
        {
            get
            {
                if (!_hasCold) return K;
                return ColdWarmBlend.Blend(_coldK, K, HotWeightForBlend());
            }
        }

        /// <summary>DIAGNOSTIC/MEASUREMENT ONLY (docs\speed-aware-reference-fix-report.md) - the fixed
        /// number of speed buckets this instance sorts observations into. Exposed purely so an offline
        /// harness can report, per bucket, whether the physical limit was ever credibly reached in a
        /// captured log, and how the fitted curve compares to each bucket's own directly-observed
        /// estimate - never consumed by any live severity/reference path.</summary>
        public static int SpeedBucketCount => SpeedBucketEdgesKmh.Length;

        /// <summary>DIAGNOSTIC/MEASUREMENT ONLY - the lower edge (km/h) of speed bucket <paramref name="index"/>,
        /// see <see cref="SpeedBucketEdgesKmh"/>.</summary>
        public static double SpeedBucketEdgeKmh(int index) => SpeedBucketEdgesKmh[index];

        /// <summary>DIAGNOSTIC/MEASUREMENT ONLY - snapshots speed bucket <paramref name="index"/>'s own
        /// CURRENT state: whether it currently holds live (windowed, at-limit-gated) evidence
        /// (<paramref name="hasLiveEstimate"/>), that evidence's own robust peak estimate
        /// (<paramref name="peakG"/>, meaningless when <paramref name="hasLiveEstimate"/> is false), its
        /// own all-time mean admitted speed (<paramref name="meanSpeedKmh"/>, 0 when the bucket has never
        /// been fed at all), and its live (windowed) sample count (<paramref name="liveSamples"/>). Never
        /// creates or mutates any state - a pure, read-only snapshot for an offline measurement harness.</summary>
        public void SnapshotSpeedBucket(int index, out bool hasLiveEstimate, out double peakG, out double meanSpeedKmh, out int liveSamples)
        {
            hasLiveEstimate = _buckets[index].Estimator.TryEstimate(out peakG);
            meanSpeedKmh = _buckets[index].Count > 0 ? (_buckets[index].SpeedSumMps / _buckets[index].Count) * 3.6 : 0.0;
            liveSamples = _buckets[index].Estimator.CurrentValidSampleCount;
        }

        /// <summary>What SHOULD be written back to persistence for <see cref="GMech"/> right now - mirrors
        /// <see cref="PersistedPeakG"/>'s own "HOT must not corrupt COLD" gate exactly (the SAME
        /// <see cref="ColdWarmBlend.ShouldPersist"/> bar, evaluated once for the whole key - a session
        /// trusted enough to overwrite the persisted flat peak is trusted enough to overwrite the
        /// persisted speed model too).</summary>
        public double PersistedGMech
        {
            get
            {
                if (!_hasCold) return GMech;
                return ColdWarmBlend.ShouldPersist(_hotSession.Count, _hotSession.CoefficientOfVariation) ? PublishedGMech : _coldGMech;
            }
        }

        /// <summary>What SHOULD be written back to persistence for <see cref="K"/> right now - mirrors
        /// <see cref="PersistedGMech"/>'s own remarks exactly.</summary>
        public double PersistedK
        {
            get
            {
                if (!_hasCold) return K;
                return ColdWarmBlend.ShouldPersist(_hotSession.Count, _hotSession.CoefficientOfVariation) ? PublishedK : _coldK;
            }
        }

        /// <summary>The LEARNING-path reject ceiling actually used by THIS instance - defaults to
        /// <see cref="MaxPlausibleG"/>, but a channel-specific instance (see the constructor) may use
        /// a tighter, asymmetric value instead. <see cref="Observe"/> rejects a magnitude above this
        /// outright.</summary>
        public double LearnCapG { get; }

        /// <param name="learnCapG">The learning-path reject ceiling for THIS instance - see
        /// <see cref="LearnCapG"/>. Defaults to <see cref="MaxPlausibleG"/> so every pre-existing
        /// <c>new GripLearner()</c> call site (including this file's own tests) is unaffected. A
        /// non-positive or non-finite value falls back to the default rather than disabling
        /// rejection entirely.</param>
        public GripLearner(double learnCapG = MaxPlausibleG)
        {
            LearnCapG = learnCapG > 0.0 && ClampMath.IsFinite(learnCapG) ? learnCapG : MaxPlausibleG;
            _buckets = NewBuckets();
        }

        /// <summary>0..1 maturity of the learned peak - 1.0 once <see cref="MaturitySamples"/> qualifying
        /// observations have been folded in. NOTE (docs\regression-fix-report.md - the sample-threshold
        /// follow-up): this LINEAR, absolute-count-based notion is kept for the diagnostic-only readout
        /// (<c>Diag.Lock/Slip.LearnerConfidence</c>) and is deliberately NOT what gates the
        /// physical-limit-detector TEACHING path's own ratio comparison (the one call site that passes
        /// <c>applyColdStartCeiling: false</c> - see <see cref="Ratio"/>'s own remarks; that path relies
        /// entirely on <see cref="Normalized.KeyedScaleLearner"/>'s own ramp instead, precisely so there
        /// is a single confidence notion there, not two). It IS still one INPUT (alongside stability -
        /// see <see cref="MaturityConfidence"/>) to the cold-start ceiling every OTHER
        /// <c>applyColdStartCeiling: true</c> caller uses (most notably the severity-facing physical
        /// ratio <c>u</c> itself) - see <see cref="MaturityConfidence"/>'s own remarks for why sample
        /// count ALONE stopped being sufficient there.</summary>
        public double Confidence => ClampMath.To01(ClampMath.SafeDiv(_samples, MaturitySamples, 0.0));

        /// <summary>THE ACTUAL confidence <see cref="Ratio"/> gates its cold-start ceiling on (docs\
        /// stability-confidence-fix-report.md) - <see cref="Confidence"/> (the existing linear
        /// sample-count ramp, UNCHANGED, still 1.0 at exactly <see cref="MaturitySamples"/>) MULTIPLIED
        /// by a continuous STABILITY term (<see cref="StabilityWeight"/> of the flat peak's own current
        /// "quiet streak" - see the remarks above that method) reflecting whether the reference has
        /// actually settled, not merely whether enough samples have been seen. Both factors are
        /// continuous and gated by NO hard sample count anywhere; the product only reaches 1.0 once
        /// BOTH enough evidence exists AND the reference has genuinely stopped moving - exactly closing
        /// the gap the safety report diagnosed (a reference that keeps climbing well past 200 samples
        /// used to be granted full, unceilinged trust regardless).</summary>
        public double MaturityConfidence
            => ClampMath.Clamp(Confidence * StabilityWeight(_flatPeak.QuietStreak), 0.0, 1.0);

        /// <summary>Diagnostic-only readout of the flat scalar's own current "quiet streak" (see
        /// <see cref="StabilityWeight"/>'s own remarks) - how many consecutive qualifying observations
        /// have passed since <see cref="LearnedPeakG"/> last moved meaningfully. Not itself clamped to
        /// <see cref="StabilityScaleSamples"/> (it may run arbitrarily far past the soft scale
        /// reference once genuinely settled).</summary>
        public double QuietStreak => _flatPeak.QuietStreak;

        /// <summary>
        /// Folds one qualifying observation (already gated by the engine on pedal commitment and the
        /// lateral-isolation check - see <see cref="NormalizedWheelLockSlipEngine"/>, plus the
        /// per-frame <see cref="TelemetryLearningGate"/>/pedal-minimum check - see that engine's own
        /// remarks) into the learned peak. A decaying maximum: every call decays the current estimate
        /// slightly, then raises it toward <paramref name="magnitudeG"/> if that observation exceeds
        /// it - so the learner keeps tracking a car that gets faster tyres or a different setup
        /// mid-session, rather than freezing at whatever it first learned.
        /// <para/>
        /// REJECT, not clamp (see <see cref="LearnCapG"/>'s own remarks): a magnitude above
        /// <see cref="LearnCapG"/> is discarded OUTRIGHT here, never folded in even partially - the
        /// live-path equivalent, <see cref="Ratio"/>, CLAMPS instead, against the separate, higher
        /// <see cref="LiveClampG"/>.
        /// </summary>
        /// <param name="speedKmh">SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md) - this
        /// observation's own ground speed, folded into the matching entry of <see cref="_buckets"/> (see
        /// that struct's own remarks for the windowed, robust-estimator mechanism this now uses) so
        /// <see cref="FitSpeedModel"/> has bucketed evidence to fit from. Defaults to <c>null</c> so every
        /// pre-existing caller/test (including this file's own) is unaffected - the speed dimension simply
        /// never accumulates evidence for them, and <see cref="K"/> stays exactly 0 forever (see
        /// <see cref="FitSpeedModel"/>'s own guards). Out of range (non-finite, negative, or above
        /// <see cref="MaxPlausibleSpeedKmh"/>) is treated the same as absent - a garbage/teleport speed
        /// reading must not corrupt a bucket, but the flat scalar update above still applies.</param>
        /// <param name="atLimitWeight">SPEED-AWARE REFERENCE FIX, defect 2 ("unreached limits" -
        /// docs\speed-aware-reference-fix-report.md) - a continuous 0..1 confidence that THIS frame is
        /// genuinely at the physical grip limit, computed by the caller INDEPENDENTLY of this class's own
        /// reference (see <see cref="NormalizedWheelLockSlipEngine"/>'s <c>ComputeCornerAtLimitConfidence</c>
        /// - a corner-local, shape-based detector that never divides by <see cref="SpeedAwarePeakG"/> or
        /// anything derived from it, avoiding circularity by construction). Gates ONLY the SPEED-BUCKET
        /// admission below (never the flat scalar above, which stays fed unconditionally exactly as
        /// before) - an observation with zero at-limit evidence is real achieved-G data, but is NOT
        /// evidence of "the grip available at this speed" (the textbook case: an F1 car's high-speed
        /// braking is brake-torque-limited, well short of what its downforce actually allows). Defaults to
        /// 1.0 (unconditional admission, byte-identical to this method's own behaviour before this fix)
        /// so every pre-existing caller/test that never passes it is unaffected.</param>
        public void Observe(double magnitudeG, double? speedKmh = null, double atLimitWeight = 1.0)
        {
            if (!ClampMath.IsFinite(magnitudeG) || magnitudeG <= 0.0 || magnitudeG > LearnCapG) return;

            ApplyEvidenceWeightedObservation(ref _flatPeak, magnitudeG);

            // INT32 OVERFLOW GUARD (adaptive-peak-learner-report.md, Part 1): _samples PERSISTS and
            // accumulates across every session (see GripLearnerState.Samples/KeyedGripLearner.ExportAll),
            // so an unguarded increment could in principle wrap negative after enough cumulative real-world
            // use - saturate it well below int.MaxValue instead. The peak estimator above is completely
            // independent of this counter and keeps updating regardless of whether the cap has been
            // reached - only the COUNTER stops rising, never the learning.
            if (_samples < SampleCountSaturationCap) _samples++;
            _hotSession.Observe(magnitudeG);

            // SYNTHETIC CLOCK (see _syntheticTicks' own remarks) - advances on EVERY qualifying call,
            // regardless of whether this observation ends up admitted to a speed bucket below, so a
            // bucket's own window ages out on elapsed qualifying effort, not on how often THIS ONE bucket
            // specifically got fed.
            if (_syntheticTicks < long.MaxValue) _syntheticTicks++;

            if (!speedKmh.HasValue || !ClampMath.IsFinite(speedKmh.Value)
                || speedKmh.Value < 0.0 || speedKmh.Value > MaxPlausibleSpeedKmh)
                return;

            // AT-LIMIT GATE (see this method's own atLimitWeight remarks) - a frame with no credible
            // at-limit evidence teaches this bucket NOTHING (not even partially): the bucket's own
            // reference must reflect "the grip available here", not "the hardest braking merely attempted
            // here". This is a per-frame QUALIFICATION filter (the same kind this codebase already uses
            // everywhere - triggered/engaged/isolation/MinRawForCalibrationObservation), never a minimum
            // accumulated-SAMPLE-COUNT gate on the bucket itself (no such gate exists anywhere below).
            if (atLimitWeight <= 0.0) return;

            int index = BucketIndex(speedKmh.Value);
            SpeedBucket bucket = _buckets[index];
            bucket.Estimator.Observe(SyntheticTimestamp(), magnitudeG);

            // INT32 OVERFLOW GUARD, continued - bucket.Count and bucket.SpeedSumMps MUST freeze TOGETHER
            // (the bucket's own mean speed, SpeedSumMps/Count, would otherwise silently corrupt toward the
            // sum's own growth if only one of the pair kept moving) - see this class's own remarks.
            // Estimator (the actual learned reference) is, again, entirely unaffected by this cap - it has
            // its own, independent saturation guard.
            if (bucket.Count < SampleCountSaturationCap)
            {
                bucket.SpeedSumMps += speedKmh.Value / 3.6;
                bucket.Count++;
            }
            _buckets[index] = bucket;
        }

        /// <summary>The fixed speed bucket <paramref name="speedKmh"/> falls into, per
        /// <see cref="SpeedBucketEdgesKmh"/> - the highest edge THIS speed is at or above.</summary>
        private static int BucketIndex(double speedKmh)
        {
            int index = 0;
            for (int i = 1; i < SpeedBucketEdgesKmh.Length && speedKmh >= SpeedBucketEdgesKmh[i]; i++)
                index = i;
            return index;
        }

        /// <summary>
        /// <paramref name="speedKmh"/>'s own predicted peak achievable g, per the fitted
        /// <c>g_mech + k * v_mps^2</c> model (<see cref="PublishedGMech"/>/<see cref="PublishedK"/>) -
        /// what <see cref="Ratio"/> actually divides by when a speed is supplied. Floored at
        /// <see cref="MinPeakFloor"/> for the same reason <see cref="Observe"/>'s own decaying maximum
        /// is - a near-zero (or, if ever extrapolated far past this car's own observed range, negative)
        /// denominator must never be handed to <see cref="ClampMath.SafeDiv"/>. Degenerates to a plain
        /// constant (<see cref="PublishedGMech"/>, independent of <paramref name="speedKmh"/>) whenever
        /// <see cref="PublishedK"/> is 0 - i.e. bit-identical to <see cref="PublishedPeakG"/> for every
        /// caller that never taught the speed dimension.
        /// </summary>
        public double SpeedAwarePeakG(double speedKmh)
        {
            double vMps = speedKmh / 3.6;
            double predictedPeakG = PublishedGMech + PublishedK * vMps * vMps;
            return predictedPeakG < MinPeakFloor ? MinPeakFloor : predictedPeakG;
        }

        /// <summary>
        /// Weighted least-squares fit of <c>peakG = gMech + k * v_mps^2</c> across whichever of
        /// <see cref="_buckets"/> have evidence, each bucket weighted by its own qualifying sample count.
        /// Recomputed fresh from the raw bucket state on EVERY call (no caching/hysteresis across
        /// frames) - simple and correct at convergence, at the cost of a few transient frames of
        /// k-nonzero-then-rejected behaviour for a brand-new key before enough evidence accumulates to
        /// settle (see docs\speed-aware-grip-report.md's own Concerns).
        /// <para/>
        /// Falls back to <c>(gMech: <see cref="_flatPeak"/>.<see cref="AdaptivePeakState.Level"/>, k: 0.0)</c> - EXACTLY today's flat
        /// value, not a second, differently-computed one - unless every one of the following holds (see
        /// the report's own Part 3.5 for how each guard was found and tuned against the real captured
        /// logs):
        /// <list type="number">
        /// <item>At least <see cref="MinPopulatedBucketsForFit"/> buckets have evidence.</item>
        /// <item>No populated bucket shows a "falling tail" (a later, higher-speed bucket's own decaying
        /// max sitting more than <see cref="FallingTailRejectFraction"/> below the running maximum any
        /// earlier bucket already demonstrated) - the traction-limited/power-limited signature that a
        /// rising fit must never be applied to (Slip, on every real log checked).</item>
        /// <item>The populated buckets' own mean-speed spread is at least
        /// <see cref="MinAeroFitSpeedSpreadKmh"/>.</item>
        /// <item>The regression denominator is non-degenerate (guards a near-singular fit).</item>
        /// <item>The fitted <c>k</c> is not negative - aero cannot REDUCE available grip; a negative fit
        /// indicates the wrong functional shape, not a smaller aero effect.</item>
        /// <item>The fitted <c>gMech</c> is not below <see cref="MinPeakFloor"/>.</item>
        /// <item>The fitted <c>gMech</c> does not exceed <see cref="_flatPeak"/>.<see cref="AdaptivePeakState.Level"/> (the single highest g
        /// this instance has EVER recorded) by more than <see cref="MaxGMechOvershootOfLearnedPeak"/> -
        /// since <c>gMech</c> is the model's own smallest predicted value (v=0), it cannot legitimately
        /// exceed the largest g ever demonstrated at any speed.</item>
        /// </list>
        /// </summary>
        private void FitSpeedModel(out double gMech, out double k)
        {
            gMech = _flatPeak.Level;
            k = 0.0;

            int populatedBuckets = 0;
            double sumWeight = 0.0;
            double sumWeightedV2 = 0.0;
            double sumWeightedPeak = 0.0;
            double sumWeightedV4 = 0.0;
            double sumWeightedV2Peak = 0.0;
            double minMeanSpeedMps = double.MaxValue;
            double maxMeanSpeedMps = double.MinValue;
            double runningMaxPeakG = 0.0;
            bool fallingTailDetected = false;

            // GUARD 8 - ENVELOPE CORRECTION (docs\speed-aware-reference-fix-report.md): a plain weighted
            // least-squares fit MINIMISES overall error, which by construction can (and, measured on real
            // logs, regularly does) sit BELOW one or more individual populated buckets' own directly
            // observed robust peak - reproducing the exact "ratio > 1.0" defect this whole fix exists to
            // close, just via the regression instead of via decay. Populated-bucket (v^2, peakG) pairs are
            // kept here (at most SpeedBucketEdgesKmh.Length of them - a handful) so the fit can be
            // envelope-corrected AFTER solving it, below.
            var populatedV2 = new double[_buckets.Length];
            var populatedPeakG = new double[_buckets.Length];

            for (int i = 0; i < _buckets.Length; i++)
            {
                // "Populated" means the bucket's OWN windowed estimator currently holds LIVE evidence
                // (see SpeedBucket's own remarks) - NOT merely "has ever been admitted" (bucket.Count is
                // an all-time, un-windowed counter kept only for the mean-speed denominator below). A
                // bucket whose only admitted evidence has since aged out of the window (ramp-down - see
                // SpeedBucketWindowQualifyingTicks' own remarks) reverts to "unpopulated" here, exactly
                // like a bucket that was never fed at all - the fit then naturally falls back toward
                // fewer/lower-speed buckets (or the flat model, if too few remain) instead of trusting a
                // stale reading a genuinely lower-grip condition has already superseded.
                if (_buckets[i].Count <= 0) continue;
                if (!_buckets[i].Estimator.TryEstimate(out double peakG)) continue;

                double meanSpeedMps = _buckets[i].SpeedSumMps / _buckets[i].Count;
                double v2 = meanSpeedMps * meanSpeedMps;
                populatedV2[populatedBuckets] = v2;
                populatedPeakG[populatedBuckets] = peakG;
                populatedBuckets++;
                // LIVE (windowed) sample count, not the all-time bucket.Count - a bucket whose recent
                // evidence is thin (even with a long all-time history) is correctly down-weighted in the
                // regression relative to one with abundant CURRENT evidence.
                double weight = _buckets[i].Estimator.CurrentValidSampleCount;

                sumWeight += weight;
                sumWeightedV2 += weight * v2;
                sumWeightedPeak += weight * peakG;
                sumWeightedV4 += weight * v2 * v2;
                sumWeightedV2Peak += weight * v2 * peakG;

                if (meanSpeedMps < minMeanSpeedMps) minMeanSpeedMps = meanSpeedMps;
                if (meanSpeedMps > maxMeanSpeedMps) maxMeanSpeedMps = meanSpeedMps;

                // Guard: falling-tail signature - see this method's own remarks.
                if (peakG < runningMaxPeakG * FallingTailRejectFraction) fallingTailDetected = true;
                if (peakG > runningMaxPeakG) runningMaxPeakG = peakG;
            }

            if (populatedBuckets < MinPopulatedBucketsForFit || fallingTailDetected) return;

            double speedSpreadKmh = (maxMeanSpeedMps - minMeanSpeedMps) * 3.6;
            if (!ClampMath.IsFinite(speedSpreadKmh) || speedSpreadKmh < MinAeroFitSpeedSpreadKmh) return;

            double denominator = sumWeight * sumWeightedV4 - sumWeightedV2 * sumWeightedV2;
            if (!ClampMath.IsFinite(denominator) || Math.Abs(denominator) < 1e-9) return;

            double fittedK = (sumWeight * sumWeightedV2Peak - sumWeightedV2 * sumWeightedPeak) / denominator;
            double fittedGMech = (sumWeightedPeak - fittedK * sumWeightedV2) / sumWeight;

            if (!ClampMath.IsFinite(fittedK) || !ClampMath.IsFinite(fittedGMech)) return;
            if (fittedK < 0.0) return;
            if (fittedGMech < MinPeakFloor) return;
            if (fittedGMech > _flatPeak.Level * MaxGMechOvershootOfLearnedPeak) return;

            // GUARD 8 - ENVELOPE CORRECTION (see this method's own remarks above populatedV2) - shift the
            // curve's BASE (gMech) up by however much the raw least-squares fit under-predicts its own
            // WORST populated bucket, so the published reference sits AT OR ABOVE every bucket it was
            // built from - k (the fitted SHAPE/slope) is left untouched. Re-clamped against the SAME
            // overshoot guard just above (never let the correction push gMech further than a fit could
            // ever have legitimately reached on its own) - a correction large enough to hit that ceiling
            // is capped there rather than rejecting the whole fit outright, since even a partially
            // envelope-corrected fit is still strictly better than reverting to the flat model entirely.
            double maxDeficit = 0.0;
            for (int i = 0; i < populatedBuckets; i++)
            {
                double predicted = fittedGMech + fittedK * populatedV2[i];
                double deficit = populatedPeakG[i] - predicted;
                if (deficit > maxDeficit) maxDeficit = deficit;
            }
            double envelopedGMech = fittedGMech + maxDeficit;
            double gMechCeiling = _flatPeak.Level * MaxGMechOvershootOfLearnedPeak;
            if (envelopedGMech > gMechCeiling) envelopedGMech = gMechCeiling;

            gMech = envelopedGMech;
            k = fittedK;
        }

        /// <summary>
        /// <paramref name="magnitudeG"/> as a fraction of the learned peak, ceilinged per
        /// <see cref="ColdStartCeilingRatio"/> while <see cref="MaturityConfidence"/> is below 1.0 (see
        /// that property's own remarks - sample count alone stopped being sufficient once measured
        /// against real logs: docs\stability-confidence-fix-report.md).
        /// Deliberately NOT itself clamped to [0,1] once mature - a genuine full lock/spin can
        /// exceed the learned peak (the peak is a decaying maximum of ordinary driving, not a hard
        /// physical ceiling), and the caller (<see cref="NormalizedWheelLockSlipEngine"/>) clamps the
        /// final published value to 0-100 regardless.
        /// <para/>
        /// CLAMP, not reject (see <see cref="LiveClampG"/>'s own remarks): this is the LIVE path, so
        /// an impact-magnitude reading is clamped down to a finite, sane input rather than the frame
        /// being dropped - dropping it would freeze the published severity at its last value instead
        /// of producing a real (saturated) reading for that frame and recovering immediately after.
        /// </summary>
        /// <param name="applyColdStartCeiling">Defaults to <c>true</c> (every pre-existing caller/test
        /// keeps its exact behaviour). The ONE caller that now passes <c>false</c>
        /// (<see cref="NormalizedWheelLockSlipEngine"/>'s shared physical-limit detector) relies entirely
        /// on <see cref="Normalized.KeyedScaleLearner"/>'s OWN continuous, dispersion-weighted confidence
        /// (see that class's own remarks) to decide how much a taught observation ultimately matters -
        /// keeping THIS ceiling active too would be exactly the "second confidence notion" the owner's
        /// own follow-up asked to avoid (docs\regression-fix-report.md).</param>
        /// <param name="speedKmh">SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md) - when
        /// supplied (and in range), <paramref name="magnitudeG"/> is divided by
        /// <see cref="SpeedAwarePeakG"/> at THIS speed instead of the flat <see cref="PublishedPeakG"/> -
        /// THE fix itself, letting a genuine low-speed lockup read against this car's own, much lower,
        /// low-speed limit rather than a reference dominated by high-speed evidence. Defaults to
        /// <c>null</c> so every pre-existing caller/test keeps its exact prior behaviour. Out of range is
        /// treated the same as absent, mirroring <see cref="Observe"/>'s own guard.</param>
        /// <param name="useStabilityGatedCeiling">
        /// v1.0.6.9 REWORK (docs\v1068-rework-report.md, Goal 2) - which confidence notion the cold-start
        /// ceiling gates on. <c>true</c> (the default - every pre-existing caller/test keeps its exact
        /// prior behaviour) uses <see cref="MaturityConfidence"/> (sample count AND settledness -
        /// docs\stability-confidence-fix-report.md). <c>false</c> uses the plain, sample-count-only
        /// <see cref="Confidence"/> instead - reachable within a realistic session's ~200 qualifying
        /// samples regardless of whether the reference has fully "settled" per
        /// <see cref="QuietStreak"/>/<see cref="StabilityWeight"/>.
        /// <para/>
        /// WHY THIS EXISTS: measured directly (real logs, all nine 1.0.6_logs captures) that
        /// <see cref="MaturityConfidence"/>'s own <c>QuietStreak</c> requirement almost never reaches
        /// <see cref="StabilityScaleSamples"/> (120) within a realistic single-session capture, because
        /// ordinary continuous, noisy real G telemetry keeps nudging <see cref="LearnedPeakG"/> by small
        /// amounts throughout a session (each nudge resets/reduces the streak) - a genuinely reachable-
        /// in-principle value that, in practice, almost never arrives in time. The live, published
        /// severity that reads <c>u</c> WITH the ceiling therefore stayed pinned near
        /// <see cref="ColdStartCeilingRatio"/> for the ENTIRE session in these captures (confirmed:
        /// <c>MaturityConfidence</c> reached only ~0.27 by the end of a full lap on the owner's own
        /// 1.0.6.8 log) - "a clamp that may never lift", exactly the failure mode
        /// <c>Core\Projection\ColdStartScale.cs</c>'s own history note (ported from
        /// <c>QAdvanceFeedback_1.0.6.2_prerelease</c> for this same rework) already diagnoses and the
        /// owner explicitly asked to move away from. THE FIX, applied ONLY at the ONE call site that
        /// feeds the live car-level severity (<see cref="NormalizedWheelLockSlipEngine"/>'s
        /// <c>ComputeDeltaGCollapseSeverity</c>): pass <c>false</c> here so Normalized/severity itself
        /// can reach its true, best-estimate value within a realistic session (matching the owner's own
        /// "full-lock feedback must be essentially equivalent" requirement), while a SEPARATE,
        /// device-feel-only protection (<c>ColdStartScale</c>, applied at Layer 5/Projected, NEVER at
        /// this Normalized layer - see that class's own remarks for why) keeps the FELT shake strength
        /// damped while the reference is still building trust. This is a deliberate, disclosed trade-off,
        /// not an oversight: Normalized's own "false maximum" rate (a cold reading later contradicted by
        /// this session's own matured reference) is measurably HIGHER again under this mode (reported in
        /// docs\v1068-rework-report.md) - the safety-relevant mitigation moved to where the driver
        /// actually feels output, per the owner's own explicit preference for a Layer-5 multiplicative
        /// scale over an under-converging Layer-4 clamp.
        /// </param>
        public double Ratio(double magnitudeG, bool applyColdStartCeiling = true, double? speedKmh = null, bool useStabilityGatedCeiling = true)
        {
            bool speedSupplied = speedKmh.HasValue && ClampMath.IsFinite(speedKmh.Value)
                && speedKmh.Value >= 0.0 && speedKmh.Value <= MaxPlausibleSpeedKmh;
            double referencePeakG = speedSupplied ? SpeedAwarePeakG(speedKmh.Value) : PublishedPeakG;

            double clamped = ClampMath.Clamp(magnitudeG, 0.0, LiveClampG);
            double raw = ClampMath.SafeDiv(clamped, referencePeakG, 0.0);
            if (!applyColdStartCeiling) return raw;

            double confidence = useStabilityGatedCeiling ? MaturityConfidence : Confidence;
            if (confidence >= 1.0) return raw;

            double ceiling = ColdStartCeilingRatio + confidence * (1.0 - ColdStartCeilingRatio);
            return raw < ceiling ? raw : ceiling;
        }

        /// <summary>
        /// Seeds this learner from previously persisted state (<c>RuntimeStore</c>) - called once at
        /// Init. Atomic: a non-positive/non-finite peak OR a non-positive sample count means "nothing
        /// usable was stored", and BOTH fields are left at their fresh-seed values - adopting one half of
        /// a corrupt pair (e.g. a valid sample count paired with a NaN peak) would leave the learner in a
        /// state it could never reach through <see cref="Observe"/> alone.
        /// <para/>
        /// COLD/WARM (item 3): also seeds <see cref="_hasCold"/>/the persisted COLD reference itself -
        /// from this point on, <see cref="PublishedPeakG"/>/<see cref="Ratio"/> blend COLD with however
        /// this SESSION's own HOT evidence (reset fresh here, this call marking "a new session started
        /// from this cold reference") develops, rather than trusting the seeded hot state outright from
        /// the first frame - see <see cref="PublishedPeakG"/>'s own remarks. At zero new observations the
        /// blend is EXACTLY cold (weight 0), so "restart with no new driving reproduces the previous
        /// mapping" holds immediately after this call, before <see cref="Observe"/> is ever invoked again.
        /// </summary>
        public void Load(double learnedPeakG, int samples) => Load(learnedPeakG, samples, learnedPeakG, 0.0, 0.0, 0, 0.0, 0);

        /// <summary>
        /// SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md) - the SAME seeding as the 2-arg
        /// <see cref="Load(double,int)"/> overload (called by it, with <paramref name="gMech"/> defaulted
        /// to <paramref name="learnedPeakG"/> and <paramref name="k"/> to 0.0), extended with the
        /// persisted speed model's own two parameters. A document written before this feature existed
        /// simply has no <c>g_mech</c>/<c>k</c> to pass - <see cref="GripLearnerState"/>'s own field
        /// defaults (both 0.0) flow straight through here: a non-positive <paramref name="gMech"/> falls
        /// back to <paramref name="learnedPeakG"/> (the flat scalar) and any <paramref name="k"/> below 0
        /// falls back to 0.0 - together reproducing today's flat, k=0 behaviour bit-identically for
        /// every pre-existing persisted profile, with no explicit one-time-import code needed (same
        /// reasoning as the 4-&gt;5 <c>RuntimeDocument.Version</c> bump).
        /// <para/>
        /// Also resets <see cref="_buckets"/> to empty - this SESSION's own speed-bucketed evidence
        /// starts fresh from this cold reference, exactly like <see cref="_hotSession"/> above.
        /// </summary>
        public void Load(double learnedPeakG, int samples, double gMech, double k)
            => Load(learnedPeakG, samples, gMech, k, 0.0, 0, 0.0, 0);

        /// <summary>
        /// EVIDENCE-WEIGHTED PEAK ESTIMATOR CONFIDENCE STATE (docs\adaptive-peak-learner-report.md) - the
        /// SAME seeding as the 4-arg <see cref="Load(double,int,double,double)"/> overload (called by it,
        /// with every confidence-state argument defaulted to 0/none), extended with the flat scalar's own
        /// in-progress corroboration - see <see cref="RaiseCandidateG"/>/<see cref="RaiseCandidateHits"/>/
        /// <see cref="LowerCandidateG"/>/<see cref="LowerCandidateHits"/>. A document written before this
        /// feature existed simply has no candidate/hit fields to pass - <see cref="GripLearnerState"/>'s
        /// own field defaults (0.0/0) flow straight through here, reproducing "no corroboration in
        /// progress yet" - the EXACT state a freshly-constructed learner already starts in - bit-
        /// identically for every pre-existing persisted profile, with no explicit one-time-import code
        /// needed (same reasoning as every earlier additive <c>RuntimeDocument.Version</c> bump). A
        /// negative hit count, or a non-finite/non-positive candidate paired with a positive hit count,
        /// is treated as "nothing usable was stored" for that one candidate (falls back to 0/none)
        /// rather than adopting a state <see cref="Observe"/> could never itself produce.
        /// </summary>
        public void Load(double learnedPeakG, int samples, double gMech, double k,
            double raiseCandidateG, int raiseCandidateHits, double lowerCandidateG, int lowerCandidateHits)
            => Load(learnedPeakG, samples, gMech, k, raiseCandidateG, raiseCandidateHits, lowerCandidateG, lowerCandidateHits, 0.0);

        /// <summary>
        /// STABILITY-GATED MATURITY (docs\stability-confidence-fix-report.md) - the SAME seeding as the
        /// 8-arg <see cref="Load(double,int,double,double,double,int,double,int)"/> overload (called by
        /// it, with <paramref name="quietStreak"/> defaulted to 0.0), extended with the flat scalar's
        /// own in-progress "settling streak" (see <see cref="QuietStreak"/>) - persisted so a RETURNING
        /// car, whose reference has already been genuinely stable across many past sessions, does not
        /// have its stability clock reset to zero on every single restart (deliberately DIFFERENT from
        /// how a genuinely brand-new key starts - see <see cref="AdaptivePeakState.QuietStreak"/>'s own
        /// remarks - this only warms a RETURNING key's own prior settledness, never fabricates one). A
        /// document written before this feature existed simply has no <c>quietStreak</c> to pass -
        /// <see cref="GripLearnerState"/>'s own field default (0.0) flows straight through here,
        /// reproducing "no settling evidence yet this session" - bit-identical to a freshly-constructed
        /// learner's own starting stability state, with no explicit one-time-import code needed (same
        /// reasoning as every earlier additive <c>RuntimeDocument.Version</c> bump). A negative or
        /// non-finite value is treated as "nothing usable was stored" (falls back to 0.0).
        /// </summary>
        public void Load(double learnedPeakG, int samples, double gMech, double k,
            double raiseCandidateG, int raiseCandidateHits, double lowerCandidateG, int lowerCandidateHits,
            double quietStreak)
        {
            if (!ClampMath.IsFinite(learnedPeakG) || learnedPeakG <= 0.0 || samples <= 0) return;
            _flatPeak = AdaptivePeakState.Seeded(learnedPeakG);
            _samples = samples;
            _coldPeakG = learnedPeakG;
            _hasCold = true;
            _hotSession = WelfordAccumulator.Empty;
            _buckets = NewBuckets();
            _syntheticTicks = 0;
            _coldGMech = ClampMath.IsFinite(gMech) && gMech > 0.0 ? gMech : learnedPeakG;
            _coldK = ClampMath.IsFinite(k) && k >= 0.0 ? k : 0.0;

            if (raiseCandidateHits > 0 && ClampMath.IsFinite(raiseCandidateG) && raiseCandidateG > 0.0)
            {
                _flatPeak.RaiseCandidateG = raiseCandidateG;
                _flatPeak.RaiseHits = raiseCandidateHits;
            }
            if (lowerCandidateHits > 0 && ClampMath.IsFinite(lowerCandidateG) && lowerCandidateG > 0.0)
            {
                _flatPeak.LowerCandidateG = lowerCandidateG;
                _flatPeak.LowerHits = lowerCandidateHits;
            }
            if (ClampMath.IsFinite(quietStreak) && quietStreak > 0.0)
            {
                _flatPeak.QuietStreak = quietStreak;
            }
        }
    }
}

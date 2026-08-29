using System;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// NORMALIZE PATTERN (docs\release-1060-report.md, Part 2) - WHEELLOCK ONLY, selectable in the
    /// settings UI's "OUTPUT DATA AND SHAPING" section. Slip has no equivalent selector - it always
    /// behaves like <see cref="MaxGripOnly"/> (only global SMax, 1.0.6.3 parity, no S75/S90 concept).
    /// </summary>
    public enum NormalizePattern
    {
        /// <summary>1.0.6.3's own logic: only the SMax parameter is applied to the Normalized output,
        /// globally. S75/S90 are still LEARNED and PERSISTED by <see cref="LockAnchorLearner"/> in this
        /// mode too - they are simply never read for output.</summary>
        MaxGripOnly,

        /// <summary>DEFAULT. 1.0.6.8's four-range logic: the 100%/90%/75% points of Max-Grip each scale
        /// their own range individually.</summary>
        Mapping
    }

    /// <summary>
    /// Layer 4's orchestrator: "Normalized" projection. Combines Layer 3's Raw per-wheel value (the
    /// only thing that differentiates one wheel from another - see below) with a car-level
    /// "how close to MY OWN limit is this" ratio built ONLY from speed/throttle/brake/G (the brief's
    /// hard input restriction - no viper4gh, no wheel-speed-derived slip, nothing exotic), so the
    /// published bands (0-30 light/margin, 30-60 power braking/acceleration working up toward ideal,
    /// 60-80 the ideal band up to the measured grip limit - higher is faster but progressively
    /// riskier, 80-100 past the limit - locking/spinning, release the pedal immediately - see
    /// docs\anchor-rescale-report.md for the rescale that moved the grip-limit anchor to exactly 80,
    /// and docs\refinements-report.md for the original numeric verification) mean
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

        // ---- SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md - the field report of
        // a car+weather switch producing NO FEEDBACK AT ALL on the ShakeIt source). Diagnosis: decompiled
        // SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.CalibrationData requires 7000 samples before
        // its own per-car calibration is "ready" (IsReady => Count >= 7000); before that, its own
        // GetPercentile pre-maturity fallback is Math.Max(1.0, Max*0.9)*percentile/100.0 - on a car
        // ShakeIt has genuinely never seen (a custom-built car is exactly that), or even on a
        // well-established car mid-session (measured directly, see below), this can publish literal
        // zero while the wheel is genuinely near its limit. MEASURED, not assumed: replaying the
        // owner's own four F1 25 logs, at frames where Layer 3's OWN Raw (WheelLock.Raw.All - computed
        // independently of whatever source is actually configured, ALWAYS available) reads > 50 (a
        // strong, independent signal that the wheel genuinely is near its limit), the CONFIGURED
        // ShakeIt source itself (Diag.Source.Lock.All) reads under 5 in 21.6%-22.7% of those frames
        // (min 0.00 in every threshold band checked, up to Raw>70), even for this SAME, previously-
        // driven, already-calibrated car. WheelLock.Normalized.All tracks that same near-zero almost
        // exactly (matching percentages) - proof this engine is faithfully PASSING THROUGH what ShakeIt
        // reports, not independently suppressing a healthy source (a from-scratch custom car, with zero
        // calibration history instead of an established one, would be expected to hit this far more
        // severely and for far longer - see the report for the full derivation and what capture would
        // settle the custom-car case directly).
        //
        // THE FIX (v1, ABSOLUTE - superseded by the RELATIVE redesign below): rather than silently
        // publishing nothing (indistinguishable from "no lockup is happening" - the worst failure mode
        // per this task's own brief), detect the specific, narrow condition "the configured source reads
        // near-zero WHILE Layer 3's own, independently-computed Raw reads a genuine, well-above-noise-
        // floor value" and fall back to Layer 3's Raw for BOTH the severity and the per-wheel proportions
        // for that frame - a real, already-computed alternate measurement (never a fabricated one),
        // rescaled via this SAME KeyedScaleLearner under a DEDICATED, always-fed (game,car) fallback
        // identity kept warm every qualifying frame regardless of what source is actually configured (so
        // the fallback, when it engages, is not itself cold). Made VISIBLE via
        // Diag.Lock/Slip.SourceFallbackActive - so the owner can tell "the configured source went quiet
        // and we substituted Raw" from "genuinely no lockup", per this task's explicit requirement that a
        // degraded state must never be silently indistinguishable from "nothing is happening".
        //
        // ---- THE RELATIVE-FALLBACK REDESIGN (docs\relative-fallback-and-raw-default-report.md) ----
        // MEASURED, ABOVE, on the owner's own F1 25 ShakeIt log: the v1 trigger (source mean < 2.0)
        // fired for only 2/9706 Sauber frames and 7/6703 F1 Generic frames, yet the F1 Generic section's
        // configured ShakeIt source PEAKS AT 31.0 (Diag.Source.Lock.All) while Layer 3's own,
        // independently-computed Raw reaches 90.4 on the SAME frames - published Normalized capped at
        // 44.0. The v1 trigger is the wrong SHAPE for this: ShakeIt's own documented pre-maturity
        // fallback (`Math.Max(1.0, Max*0.9) * percentile/100.0`, see this class's own history above) does
        // not publish literal zero on an immature per-car reference, it publishes a small-but-nonzero
        // value proportional to whatever `Max` the effect has observed SO FAR for that (car, wheel) -
        // which is exactly why it sails straight past a fixed "< 2.0" gate while still under-reporting by
        // roughly a factor of 3.
        //
        // THE FIX: compare the CONFIGURED source's own CALIBRATED severity against Layer 3's Raw
        // CALIBRATED-THE-SAME-WAY (both projected onto the shared 0-100 canonical band via THIS SAME
        // KeyedScaleLearner, under each source's own key - never comparing native, un-rescaled units,
        // which would not be fair between two sources with different native ranges) - not against a
        // fixed absolute floor. A per-frame RATIO comparison alone would still be a hard, single-frame
        // switch (the task's own explicit concern: "must not fire spuriously... require a sustained,
        // substantial divergence, not a single frame" and "prefer a graceful transition over a hard
        // switch"), so the actual mechanism is:
        // <list type="number">
        // <item>Only ever EVALUATE the disagreement on a frame where Layer 3's Raw itself is a genuine
        // reading (>= <see cref="MinRawForCalibrationObservation"/> - the SAME bar this class already
        // trusts as "real, not noise/placeholder"). On every other frame (nothing happening, or Raw
        // itself is too small to judge from), the divergence estimate is HELD unchanged - a quiet period
        // between braking zones neither builds trust in, nor erodes trust out of, the fallback; there is
        // simply no evidence either way.</item>
        // <item>Exponentially smooth that per-frame divergence estimate over
        // <see cref="FallbackDivergenceSmoothingTauSeconds"/> (mirroring
        // <see cref="SurfaceFractionSmoothingTauSeconds"/>'s own dt-correct mechanism) - a single
        // divergent frame (the ordinary algorithm-vs-algorithm noise the task itself warns against
        // over-reacting to) barely moves the smoothed value; a SUSTAINED divergence across many
        // consecutive genuine-Raw frames (exactly what the F1 Generic measurement shows - the ratio sits
        // at ~0.24-0.33 across essentially every qualifying frame in that section, not an occasional dip)
        // saturates it.</item>
        // <item>Map the SMOOTHED divergence to a continuous BLEND WEIGHT
        // (<see cref="FallbackDivergenceEngageThreshold"/> to <see cref="FallbackDivergenceFullThreshold"/>,
        // linearly ramped) rather than a binary switch - "blend toward Raw proportionally to the
        // divergence" was chosen over a binary substitution specifically so the published severity (and
        // per-wheel proportions - see below) move continuously as the blend weight itself ramps, instead
        // of jumping the instant a threshold is crossed. The driver never feels a step either engaging or
        // disengaging.</item>
        // </list>
        // MEASURED ON BOTH LOGS with this design (tau 1.0s, engage 0.3, full 0.6): F1 Generic's blend
        // weight saturates (>0.5) for 98.5% of its own Raw&gt;50 frames, and published severity there
        // reaches p50 92.6 / p90 100.0 / max 100.0 (up from the old 44.0 cap) - while Sauber's blend
        // weight NEVER exceeds 0.5 at any Raw&gt;50 frame (0.0%), leaving its already-working ShakeIt
        // severity (itself reaching up to 100 there) untouched. See the report for the full derivation,
        // both logs' before/after numbers, and the FH6 guardrail re-check.
        private const double FallbackDivergenceSmoothingTauSeconds = 1.0;

        /// <summary>Smoothed divergence at/below this: the configured source is trusted fully (blend
        /// weight 0, unchanged from before this source ever looked suspect) - see this class's own
        /// history note above.</summary>
        private const double FallbackDivergenceEngageThreshold = 0.3;

        /// <summary>Smoothed divergence at/above this: the configured source is fully replaced by Layer
        /// 3's own calibrated Raw (blend weight 1) - see this class's own history note above. Kept
        /// meaningfully above <see cref="FallbackDivergenceEngageThreshold"/> (not equal to it) so the
        /// 0.3-0.6 band is a genuine ramp, not a second disguised hard switch.</summary>
        private const double FallbackDivergenceFullThreshold = 0.6;

        /// <summary>NATIVE-AGREEMENT GUARD (see this class's own history note) - absolute floor (native
        /// units, the same 0-100 scale every raw wheel value is already clamped to) below which the
        /// configured source's own mean and Layer 3's Raw mean are treated as "the same reading" for
        /// this frame, bypassing the calibrated comparison entirely. Small enough to only catch genuine
        /// same-value agreement (e.g. the configured source IS Raw), never a real, meaningfully different
        /// pair of readings that happen to be in the same rough neighbourhood.</summary>
        private const double NativeAgreementAbsoluteTolerance = 1.0;

        /// <summary>The proportional counterpart to <see cref="NativeAgreementAbsoluteTolerance"/> - a
        /// fraction of Layer 3's own Raw reading, so "the same reading" is judged relatively at higher
        /// magnitudes too (1.0 native unit of tolerance is generous near 0 but overly strict near 100).</summary>
        private const double NativeAgreementRelativeTolerance = 0.02;

        /// <summary>The fixed sourceIdentity segment <see cref="KeyedScaleLearner"/> keys the
        /// always-fed Layer-3-Raw fallback calibration under - deliberately never collides with a real
        /// <see cref="SourceIdentity.Compute"/> output (those are either a verbatim property name or an
        /// 8-hex-digit FNV-1a hash, never this literal).</summary>
        private const string RawFallbackSourceIdentity = "__layer3_raw_fallback__";

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

        // ---- CORNER-LOCAL AT-LIMIT GATE (docs\speed-aware-reference-fix-report.md) ----
        // A SECOND, DISTINCT defect from decay erosion: even a perfectly-estimated "highest G achieved in
        // this speed bucket" is not the same thing as "the grip available at this speed" unless the tyre
        // was actually pushed to its limit while at that speed - an F1 car's own high-speed braking is
        // frequently brake-torque-limited (downforce leaves real grip margin in reserve), so folding an
        // ordinary, comfortably-within-margin high-speed stop into the speed-bucketed reference would
        // teach "the hardest braking attempted here" as if it were "the physical ceiling here". THE FIX -
        // ported from QAdvanceFeedback_1.0.6.9_anchor_sampling's own ComputeCornerAtLimitConfidence (built
        // there for a different call site - gating KeyedScaleLearner's SMax teaching - but the underlying
        // detector is exactly what this task needs too, and is reused/adapted here rather than
        // reinvented): a continuous 0..1 confidence that THIS frame is genuinely at the limit, built from
        // two shape-based signals (multiplied, the same "one confidence, multiplied" idiom
        // ComputeDeltaGCollapseSeverity's own gate*collapse already uses) plus a coarse plausibility
        // floor - see ComputeCornerAtLimitConfidence's own remarks for the full derivation. Deliberately
        // built entirely from quantities INDEPENDENT of SpeedAwarePeakG/the speed-bucketed model itself
        // (the flat, non-speed-aware physical reference; the raw calibration basis; frame-to-frame G) -
        // avoiding the circularity of judging "did we reach the limit" using the very reference being
        // corrected. Gates ONLY GripLearner.Observe's speed-bucket admission (see that method's own
        // atLimitWeight remarks) - never severity, never the SMax/ObserveAtPhysicalLimit teaching, which
        // keeps using the SAME session-wide physicallyAtLimit boolean it always has.
        /// <summary>Scale for the "effort sustained" term of <see cref="ComputeCornerAtLimitConfidence"/> -
        /// the fraction of THIS frame's own calibration basis (never a stored/global ceiling) that counts
        /// as a genuine frame-to-frame drop in braking/throttle effort, as opposed to ordinary telemetry
        /// ripple.</summary>
        private const double AtLimitEffortDropToleranceFraction = 0.15;

        /// <summary>Absolute floor (native units) for the effort-drop scale above - guards a near-zero
        /// calibration basis from making this term hypersensitive to ordinary single-frame noise.</summary>
        private const double AtLimitEffortDropFloorNative = 2.0;

        /// <summary>Coarse plausibility-floor band (see <see cref="ComputeCornerAtLimitConfidence"/>'s own
        /// remarks) - below this fraction of the session-wide (flat, non-speed-aware) reference, a frame's
        /// own G is too small in absolute terms to plausibly be near ANY corner's physical limit,
        /// regardless of its trend. Deliberately far below <see cref="PhysicalLimitRatioThreshold"/>
        /// (0.85) - this is a sanity rejection of the implausible, not a return to "reaching X% of history
        /// is proof of THIS corner's own limit".</summary>
        private const double AtLimitPlausibilityRatioFloor = 0.10;

        /// <summary>Above this fraction of the session-wide reference, the plausibility term is fully
        /// satisfied (1.0) - see <see cref="AtLimitPlausibilityRatioFloor"/>'s own remarks.</summary>
        private const double AtLimitPlausibilityRatioCeiling = 0.40;

        // ---- DELTA-G COLLAPSE BAND MAPPING (docs\delta-g-band-mapping-report.md) ----
        // THE OWNER'S FINAL SPECIFICATION FOR "Normalized": 30 = consistently achieving 75% of the
        // maximum braking/accel effort available; 60 = 90%; 80 = the maximum effort available; 100 =
        // fully locked/spun, delivering LESS effort than the maximum. This must mean the SAME physical
        // thing regardless of game/car/surface/source - so severity below is now derived from the
        // physically-anchored utilization ratio u = g / (this car's own achievable peak at this speed),
        // NOT from the configured source's own native reading (see this class's own F1-25-FIX history
        // note above for what this supersedes and why - Raw/ShakeIt's own native scale is exactly what
        // this design stops trusting as the car-level number; it is retained ONLY to redistribute the
        // resulting car-level level across the four wheels, below).
        //
        // TWO PRIOR ATTEMPTS AT THIS EXACT BAND (docs\grip-utilization-band-report.md,
        // docs\stable-denominator-report.md) BOTH FAILED because they blended toward the curve with a
        // Math.Max floor - a floor can only ever RAISE output, so "80-100 means it is getting WORSE, and
        // output must be able to fall out of it" is structurally unreachable. THIS design uses a plain
        // convex combination (see ComputeDeltaGCollapseSeverity below) - both terms can fall, so there is
        // no floor anywhere in this formula.
        //
        // THE BRANCH DISCRIMINATOR (docs\two-signal-band-mapping-report.md, Part 2/3): the configured
        // SOURCE's own native reading was measured, on all nine real logs, to be an UNRELIABLE branch cue
        // - it is the wrong sign for Slip almost everywhere, and reverses sign on wet surfaces / some
        // car-source pairs for Lock. What DOES hold cleanly on every log, for BOTH channels: is G ITSELF
        // currently COLLAPSING (falling), gated to only matter once the driver is already close to the
        // physical limit. That is exactly the owner's own 80-100 definition ("deceleration/acceleration
        // REDUCING"), detected directly rather than inferred from the source.
        //
        // gate(u)   = clamp((u - BandGateStart) / BandGateWidth, 0, 1)         - smooth ramp 0.80..0.95
        // collapse  = clamp(-(g[t] - g[t-1]) / dGScale, 0, 1)                   - only within one qualifying run
        // b         = gate(u) * collapse                                       - one confidence notion, both channels
        // R(u)      = BandCurve.Evaluate(clamp(u,0,1))                          - rising branch, PHYSICAL meaning
        // F(b)      = FullLockOutputBase + FullLockOutputSpan * b               - falling branch
        // Normalized = (1 - b) * R(u) + b * F(b)                                - plain convex combination, no floor
        private const double BandGateStart = 0.80;
        private const double BandGateWidth = 0.15;

        /// <summary>Fraction of the flat, surface-blended learned peak used as the collapse detector's
        /// own scale (docs\two-signal-band-mapping-report.md, Part 3/5) - a ΔG more negative than this
        /// fraction of the car's own peak, this frame, reads as a genuine collapse (b's collapse term
        /// saturates at 1.0).</summary>
        private const double CollapseDGScaleFraction = 0.12;

        /// <summary>Fallback ΔG scale (g) while this (game,car)'s own physical reference is still cold
        /// (fewer than <see cref="CollapseDGScaleMinSamples"/> ever-qualifying samples) - a plausible
        /// minimum genuine-collapse magnitude, not zero/undefined.</summary>
        private const double CollapseDGScaleColdFallbackG = 0.15;

        private const int CollapseDGScaleMinSamples = 5;

        // COLLAPSE HYSTERESIS (docs\delta-g-band-mapping-report.md) - a short EMA on the raw collapse
        // term was tried live here (a CollapseHysteresisTauSeconds constant, an
        // ExponentialSmoothTowardTarget call, and a per-channel _lockSmoothedCollapse/_slipSmoothedCollapse
        // session-state field), measured directly against the real logs, and REJECTED: it made the
        // frame-to-frame flicker at the b=0<->b>0 boundary measurably WORSE, not better (Slip's fraction
        // of boundary crossings jumping >15 published points rose from 16.8-37.5% to 49.1-84.3%, and
        // car-level time-above-80 roughly doubled) - see ComputeDeltaGCollapseSeverity's own remarks for
        // the diagnosed reason (EMA-smoothing a mostly-zero, occasionally-spiking signal spreads each
        // spike's decay tail across many more frames, creating MORE opportunities for a jump, not fewer).
        // No hysteresis ships; see the class-level report reference for the full measured evidence.

        /// <summary>The falling branch's own floor (b=0) - "80" itself, the owner's own "maximum grip"
        /// anchor, unchanged from every pre-existing anchor-rescale/curve-defaults precedent in this
        /// codebase.</summary>
        private const double FullLockOutputBase = 80.0;

        /// <summary>The falling branch's own span (b=1 -> 100, "fully locked, delivering LESS effort
        /// than the maximum").</summary>
        private const double FullLockOutputSpan = 20.0;

        /// <summary>The rising branch R(u) - IDENTICAL for both channels, per the owner's own explicit
        /// "same rule for the Slip/acceleration channel" instruction: 75% of available grip -> 30, 90% ->
        /// 60, 100% (the physical max) -> 80. A single shared, allocation-free instance (Fritsch-Carlson
        /// monotone cubic, already used by Layer 5's OutputProjector - reused here rather than
        /// reimplemented, per this codebase's own standing precedent).</summary>
        private static readonly MonotoneCubicCurve BandCurve =
            new MonotoneCubicCurve(new[] { 0.0, 0.75, 0.90, 1.00 }, new[] { 0.0, 30.0, 60.0, 80.0 });

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
        // isLockChannel is passed ONLY so each learner can look up its own side of
        // KnownSourceColdStartReference during a Tier-1 cold start - see KeyedScaleLearner's own
        // constructor remarks. Nothing else in either learner is channel-aware.
        private readonly KeyedScaleLearner _lockScaleLearner = new KeyedScaleLearner(isLockChannel: true);
        private readonly KeyedScaleLearner _slipScaleLearner = new KeyedScaleLearner(isLockChannel: false);

        /// <summary>MANUAL KEY DATA POINTS (v1.0.7.2) - decides WHEN a configured value may replace the
        /// learned one; see <see cref="ManualOverrideGate"/> for the rule. Shared by both channels, keyed
        /// per (game, car, source) so each context earns its own readiness.</summary>
        private readonly ManualOverrideGate _manualGate = new ManualOverrideGate();

        /// <summary>Lock's manually-configured anchors, or <see cref="ManualAnchors.None"/> for Auto.
        /// Set by the plugin layer from the persisted settings; the engine never reads Settings itself.
        /// Learning continues regardless of what is set here - this only selects what is PUBLISHED.</summary>
        public ManualAnchors LockManualAnchors { get; set; } = ManualAnchors.None;

        /// <summary>Slip's counterpart to <see cref="LockManualAnchors"/>.</summary>
        public ManualAnchors SlipManualAnchors { get; set; } = ManualAnchors.None;

        private bool _lockManualAnchorsApplied;
        private bool _slipManualAnchorsApplied;

        /// <summary>Whether Lock's configured anchors are actually driving the output right now - false
        /// while the gate is still withholding them. Diagnostic, and what the UI uses to decide whether
        /// the value boxes are live yet.</summary>
        public bool LockManualAnchorsApplied => _lockManualAnchorsApplied;

        /// <summary>Slip's counterpart to <see cref="LockManualAnchorsApplied"/>.</summary>
        public bool SlipManualAnchorsApplied => _slipManualAnchorsApplied;

        /// <summary>
        /// Whether the manual-override gate is OPEN for this context - cold start finished AND the
        /// driving-time bar cleared.
        /// <para/>
        /// Deliberately independent of whether a manual value actually exists. <see cref="LockManualAnchorsApplied"/>
        /// can only be true once something was configured to apply, so it cannot answer "may we now write
        /// the first value in?" for an unknown source that has nothing configured yet - which is exactly
        /// the case the one-time seed exists to handle.
        /// </summary>
        public bool ManualGateReady(string gameId, string carId, string sourceIdentity, bool isLockChannel)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, string.Empty);
            KeyedScaleLearner learner = isLockChannel ? _lockScaleLearner : _slipScaleLearner;
            return _manualGate.IsReady(key, learner.CeilingHandoverConfidence(gameId, carId, sourceIdentity));
        }

        /// <summary>Driving seconds accumulated toward the manual-override gate for this context - lets
        /// the UI show progress rather than an unexplained disabled box.</summary>
        public double ManualGateElapsedSeconds(string gameId, string carId, string sourceIdentity)
            => _manualGate.ElapsedSeconds(KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, string.Empty));

        /// <summary>FEATURE C (docs\v1068-four-range-report.md) - WHEELLOCK ONLY, learns S75/S90 (see
        /// <see cref="LockAnchorLearner"/>'s own remarks). There is deliberately no Slip equivalent -
        /// the owner was explicit that the 30/60 anchors, and this entire four-range mapping, apply to
        /// WheelLock only.</summary>
        private readonly LockAnchorLearner _lockAnchors = new LockAnchorLearner();

        // Last-computed per-source scale ceiling (native units) + which tier produced it - exposed for
        // diagnostics (Diag.Lock.SourceScaleCeiling/Diag.Slip.SourceScaleCeiling), mirroring how
        // CurrentDirection below exposes _direction's own last-resolved state.
        private double? _lockScaleCeiling;
        private bool _lockScaleCeilingIsPrimaryTier;
        private double? _slipScaleCeiling;
        private bool _slipScaleCeilingIsPrimaryTier;

        // SHAKEIT-SILENCE FALLBACK (see this class's own history note above) - whether THIS frame's
        // published severity/proportions came from the Layer-3-Raw fallback rather than the configured
        // source, exposed the same way LockScaleCeilingIsPrimaryTier is.
        private bool _lockSourceFallbackActive;
        private bool _slipSourceFallbackActive;

        // RELATIVE-FALLBACK smoothed divergence state (see the class-level history note above) - one
        // smoothed "how implausibly low is the configured source vs. Layer 3's own calibrated Raw" value
        // per channel, session-scoped exactly like _lockLooseFraction/_slipLooseFraction below (reset
        // alongside them - a fresh game/session should not inherit a stale "the source looked bad"
        // verdict from a previous car/session).
        private double _lockFallbackDivergence;
        private double _slipFallbackDivergence;

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

        // ---- DELTA-G COLLAPSE BAND MAPPING state (see the class-level history note above) - the
        // previous QUALIFYING frame's own achieved |g| per channel, used purely to detect a genuine
        // frame-to-frame collapse (ΔG). Session-scoped, like every other per-channel state above -
        // deliberately NOT persisted (a previous-frame value is meaningless across a restart/gap, unlike
        // the learned peak references it is compared against). Null means "no qualifying previous frame
        // to compare against yet" (a fresh session, right after a quiet stretch, or right after a car
        // switch) - the collapse term reads 0 in that case (see ComputeDeltaGCollapseSeverity), never a
        // spurious comparison against a stale value from a different run.
        private double? _lockLastG;
        private double? _slipLastG;

        // ---- CORNER-LOCAL AT-LIMIT GATE state (see the class-level history note above) - the previous
        // QUALIFYING frame's own achieved |g| AND calibration basis per channel, used purely by
        // ComputeCornerAtLimitConfidence to judge "is G plateauing/falling while effort is sustained".
        // Session-scoped and reset alongside _lockLastG/_slipLastG at the SAME three sites (not
        // triggered/motion unavailable/not engaged) - a fresh qualifying run must not diff its first frame
        // against a stale reading from before the gap, exactly like _lockLastG/_slipLastG's own remarks.
        private double? _lockAtLimitLastG;
        private double? _lockAtLimitLastBasis;
        private double? _slipAtLimitLastG;
        private double? _slipAtLimitLastBasis;

        // Last-computed u (utilization)/b (branch confidence) per channel - diagnostics only, mirroring
        // LockScaleCeiling's own exposure pattern; used by the real-log validation harness
        // (scratchpad\narrative-validation-harness) to bin/verify band correspondence and falling-branch
        // validity directly against what the engine itself computed, rather than a second, parallel
        // (and possibly drifting) reimplementation of the formula.
        private double _lockUtilization;
        private double _lockBranchConfidence;
        private double _slipUtilization;
        private double _slipBranchConfidence;

        // Last-computed CAR-LEVEL severity (before per-wheel redistribution) per channel - diagnostic
        // only. NOTE: this is deliberately NOT the same as LockAll/SlipAll whenever the configured
        // source's own per-wheel readings are non-uniform (LockAll is Aggregator.Compute's OWN weighted
        // combination of the four PER-WHEEL values, each severity*s_i - see ComputeChannel's own remarks)
        // - band-correspondence validation against the owner's 30/60/80 specification must read THIS
        // value (the mapping's own direct output), not the post-aggregation LockAll, since per-wheel
        // aggregation is an orthogonal, pre-existing mechanism this task does not change.
        private double _lockCarLevelSeverity;
        private double _slipCarLevelSeverity;

        // ---- AGGREGATED-ALL SCALE state (docs\all-channel-scale-and-surface-gap-report.md) - the most
        // recently computed uniform per-wheel multiplier per channel (diagnostic only, mirrors
        // _lockUtilization's own exposure). 1.0 (the neutral, no-op value) until the channel first
        // engages, exactly like _lockScaleCeiling defaults to null/_lockUtilization defaults to 0.0 -
        // this one defaults to 1.0 specifically because it is a MULTIPLIER, not an additive quantity.
        private double _lockAllScale = 1.0;
        private double _slipAllScale = 1.0;

        // ---- MID-CHAIN CLAMP FIX (docs\clamp-chain-fix-report.md) - the most recently computed NATIVE
        // (pre-scale, pre-final-clamp) Front/Rear/Left/Right/All aggregate per channel. Compute() builds
        // the PUBLISHED aggregate as ClampMath.To0100(nativeAggregate.X * allScale) for each of the five
        // fields, instead of re-aggregating the already-scaled-and-clamped per-wheel Corners - see
        // ComputeChannel's own remarks for how this is populated on every return path (including the
        // early-exit/degraded ones, where it is defined so that nativeAggregate.X * allScale reproduces
        // EXACTLY the per-wheel Corners that path already returns, i.e. a no-op for paths this fix does
        // not target). Defaults to all-zero, matching a channel that has never engaged.
        private WheelAggregate _lockNativeAggregate;
        private WheelAggregate _slipNativeAggregate;

        // ---- LAYER 5 COLD-START DEVICE-FEEL SCALE state (v1.0.6.9 rework, Goal 2) - the most recently
        // computed plain, sample-count-only Confidence per channel (diagnostic + Layer-5 feed, mirrors
        // _lockUtilization's own exposure pattern). 0.0 while the channel's own physical reference has
        // never been observed - the correct "fully cold" starting value.
        private double _lockColdStartConfidence;
        private double _slipColdStartConfidence;

        // TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md) - which tier
        // each channel's own KeyedScaleLearner most recently resolved to, exposed as Diag.Lock/Slip.
        // ColdStartTier so the owner can see which tier engaged on a rig. Tier1 (no reference exists) is
        // the correct "never queried yet" starting value.
        private ColdStartTier _lockColdStartTier = ColdStartTier.Tier1;
        private ColdStartTier _slipColdStartTier = ColdStartTier.Tier1;

        // FEATURE C diagnostic (docs\v1068-four-range-report.md) - whether the LAST Lock ComputeChannel
        // call actually used the new source-space four-range curve (all three anchors valid) rather than
        // falling back to the pre-existing G-ratio BandCurve. Always false for Slip (no anchor learner is
        // ever passed for that channel).
        private bool _lockFourRangeCurveActive;
        private bool _slipFourRangeCurveActive;

        /// <summary>
        /// OBJECTIVE A (docs\all-channel-scale-and-surface-gap-report.md) - the owner's own proposal:
        /// per-wheel/per-axle wheel-lock behaviour depends on too many individual factors to calibrate
        /// per wheel, and per-wheel physical truth is not obtainable at all (no per-wheel G signal
        /// exists - see this class's own remarks at the top of the file). Rather than trying anyway, the
        /// owner's fix computes the SAME physically-anchored car-level <c>severity</c>
        /// (<see cref="ComputeDeltaGCollapseSeverity"/>, UNCHANGED) as the one and only accurate number,
        /// then derives ONE uniform multiplier - <c>allScale = severity / Aggregator.Compute(blended
        /// source wheels, weights).All</c> - and applies THAT SAME multiplier to every one of the four
        /// blended per-wheel native readings, rather than the OLD mean-relative proportion
        /// (<c>s_i = bw_i / flatMean(bw)</c>). Because <see cref="Aggregator.Compute"/>'s own two blend
        /// stages (and, for Slip, its floor stage) are all homogeneous of degree 1 in their four inputs
        /// for any non-negative scalar (<c>Max(a*k,b*k) = k*Max(a,b)</c>, <c>a*k*wFront + b*k*wRear =
        /// k*(a*wFront+b*wRear)</c>), re-aggregating these four uniformly-scaled per-wheel values with
        /// the SAME weights reproduces <c>severity</c> EXACTLY at the All level (mod the 0-100 input
        /// clamp at extreme scales) - closing, BY CONSTRUCTION, the exact discrepancy
        /// docs\delta-g-band-mapping-report.md Part 3 diagnosed (front-axle-biased per-wheel proportions
        /// interacting with front-axle-biased aggregation weights to inflate published `.All` well above
        /// the mapping's own car-level severity). Front/Rear/Left/Right fall out of the SAME
        /// <see cref="Aggregator.Compute"/> call downstream in <see cref="Compute"/> - no separate
        /// "apply to the groups too" step is needed, they are already computed from these same four
        /// scaled per-wheel values.
        /// <para/>
        /// RANKING IS PRESERVED (measured, see the report): scaling all four wheels by the SAME positive
        /// constant this frame can never change which wheel is largest - the per-wheel discrimination
        /// this class's own <c>A_single_locking_wheel_still_reads_distinctly_higher...</c> test and the
        /// real-log PER_WHEEL harness check both depend on is unaffected by this change in principle;
        /// what changes is only the ABSOLUTE per-wheel/group magnitude (now anchored to physical truth at
        /// the All level, previously anchored to the flat four-wheel mean).
        /// <para/>
        /// PER-CHANNEL, INDEPENDENTLY SHIPPED (the owner's own explicit instruction - Lock and Slip may
        /// not both win): true only for the channel(s) this measured to actually improve the published
        /// `.All` discrepancy without an unacceptable cost to Slip's own per-wheel/per-group fidelity -
        /// see the report for the measured decision. <see cref="ComputeChannel"/> falls back to the OLD
        /// mean-relative proportion for a channel where this is false, UNCHANGED from every previous
        /// report.
        /// </summary>
        public const bool LockUsesAggregatedAllScale = true;

        /// <summary>NORMALIZE PATTERN (docs\release-1060-report.md, Part 2) - which of
        /// <see cref="Normalized.NormalizePattern"/> WheelLock's own published severity currently uses.
        /// Defaults to <see cref="NormalizePattern.Mapping"/> (1.0.6.8's four-range behaviour, the
        /// shipped default) - settable so the settings UI can apply the driver's own selection. Slip has
        /// no equivalent (see that enum's own remarks) - this property affects ONLY the Lock channel's
        /// <see cref="ComputeChannel"/> call (see <see cref="Compute"/>'s own `useFourRangeForSeverity`
        /// argument).</summary>
        public NormalizePattern LockNormalizePattern { get; set; } = NormalizePattern.Mapping;

        /// <summary>
        /// Slip's own pattern selector (v1.0.7.2). Ships <see cref="NormalizePattern.MaxGripOnly"/> - its
        /// "Best Point Only" - because Slip has no native 90%/75% grip measurement to place two lower
        /// anchors from. When the driver opts into the Perfect/Great/Good mapping, those two anchors are
        /// DERIVED from the Perfect point rather than learned, which is why this only has an effect while
        /// manual anchors are active: there is nothing measured for it to map otherwise.
        /// </summary>
        public NormalizePattern SlipNormalizePattern { get; set; } = NormalizePattern.MaxGripOnly;

        /// <summary>The Slip channel's equivalent of <see cref="LockUsesAggregatedAllScale"/> - see that
        /// constant's own remarks for why these may differ. MEASURED FALSE
        /// (docs\all-channel-scale-and-surface-gap-report.md): Slip ships
        /// <see cref="AggregationWeights.SlipDefaults"/>' own <see cref="AggregationWeights.SlipFloorFactor"/>
        /// (0.70, an owner-tested default this task must not change) - a Math.Max floor stage INSIDE
        /// <see cref="Aggregator.Compute"/> itself (`result = Max(result, Max(participating wheels) *
        /// 0.70)`), pre-existing and unrelated to this task. Measured directly, live, on all seven
        /// replayable logs: whenever <c>allScale</c> is large enough to clamp even ONE wheel to its own
        /// 100 ceiling (common for Slip specifically - its native per-wheel readings run far smaller than
        /// the G-derived severity far more often than Lock's do, so the uniform multiplier needed to
        /// reconcile them is frequently large, sometimes by orders of magnitude), that wheel's clamped
        /// 100 feeds the floor's own `Max(participating wheels)`, pinning the published All at EXACTLY
        /// 70.0 regardless of what severity actually is - even at severity=100. This is not a corner
        /// case: on these logs, 839-1200+ of the several-hundred-to-thousand frames where Slip severity
        /// exceeded 80 published an All of exactly 70.0 (see the report's own ALLGAP_SLIP measurement) -
        /// net effect, published Slip time-above-80 stayed at 1.02% (vs the 8.74% car-level target this
        /// task exists to close the gap toward) - ZERO net improvement over the 1.27% baseline, plus new,
        /// large-magnitude per-wheel scale volatility (allScale measured up to ~8865x on these same logs)
        /// that would visibly look like flicker in the per-wheel Slip readout - directly the "do NOT
        /// degrade Slip's per-wheel discrimination for a small All gain" the owner's brief explicitly
        /// warned against, for a channel this task's own measurement shows gains NOTHING from the
        /// trade. Slip therefore keeps the pre-existing mean-relative proportion unchanged.</summary>
        public const bool SlipUsesAggregatedAllScale = false;

        /// <summary>
        /// Source value (on the enforced 0-100 source scale) at and above which a wheel counts as FULLY
        /// contributing to the flat-mean calibration basis; below it a wheel's contribution ramps
        /// linearly to zero. See <see cref="ContributionWeightedMean"/>.
        /// </summary>
        /// <remarks>
        /// 5.0 is 5% of the enforced source scale - the band in which a per-wheel reading is sensor
        /// jitter and rounding rather than genuine slip. Deliberately a RAMP rather than a cutoff: a
        /// hard "average the wheels above X" test moves the basis DISCONTINUOUSLY as a wheel crosses X
        /// (three wheels at 60 plus a fourth at X-epsilon averages 60; the same frame one tick later,
        /// with the fourth at X+epsilon, averages 45) - the identical class of single-sample step the
        /// readiness ramp in <see cref="KeyedScaleLearner"/> exists to prevent.
        /// </remarks>
        public const double CalibrationContributionRampTop = 5.0;

        /// <summary>
        /// The flat calibration basis for a channel that does NOT use the aggregated-All scale (Slip -
        /// see <see cref="SlipUsesAggregatedAllScale"/>): the mean of the four per-wheel readings,
        /// weighting each wheel by how much it is CONTRIBUTING rather than counting all four slots
        /// unconditionally.
        /// <para/>
        /// WHY. The plain four-slot mean divides by four whether or not four wheels are doing anything,
        /// so it silently measures "how many wheels are involved" alongside "how hard they are
        /// slipping". That is not symmetric between the two channels, because the underlying event is
        /// not: braking loads all four wheels, while accelerating spins only the driven pair. Measured
        /// on the owner's own four-session capture (docs\cross-channel-smax-report.md), contributing
        /// wheels per frame ran 2.7-3.5 while braking against 2.0-2.3 while accelerating, which put a
        /// 1.22-1.74x direction-dependent bias into a basis that is supposed to carry severity alone.
        /// <para/>
        /// The bias itself was self-cancelling in the steady state - <see cref="KeyedScaleLearner"/>
        /// learns SMax from this same basis and then divides by it, so a uniformly diluted basis with a
        /// uniformly diluted SMax republishes the same number. What did NOT cancel is the VARIANCE: the
        /// contributing-wheel count changes from frame to frame, so the dilution changes from frame to
        /// frame, widening this channel's own p95-to-p99 spread far beyond anything physical. On the
        /// same capture that inflated spread to 0.12-0.51, against the 0.15
        /// <see cref="KeyedScaleLearner.MaxTrustedRelativeSpread"/> budget - which drove the agreement
        /// term to exactly zero in three of the four sessions and pinned Slip's learned ceiling to its
        /// cautious percentile permanently, so the confidence blend never functioned on this channel at
        /// all.
        /// <para/>
        /// EXACT REDUCTION TO THE PREVIOUS BASIS. Every weight saturates at 1.0 at and above
        /// <see cref="CalibrationContributionRampTop"/>, so when all four wheels are contributing this
        /// returns <c>(a+b+c+d)/4</c> - the same operations in the same order, bit for bit. It differs
        /// ONLY in frames that genuinely have an idle wheel, which is precisely the case it exists to
        /// correct.
        /// <para/>
        /// NOT REACHED BY LOCK. Lock sets <see cref="LockUsesAggregatedAllScale"/>, so its basis is the
        /// weighted aggregate and this method is never called on its path.
        /// </summary>
        internal static double ContributionWeightedMean(double a, double b, double c, double d)
        {
            double wa = ClampMath.To01(a / CalibrationContributionRampTop);
            double wb = ClampMath.To01(b / CalibrationContributionRampTop);
            double wc = ClampMath.To01(c / CalibrationContributionRampTop);
            double wd = ClampMath.To01(d / CalibrationContributionRampTop);

            double weight = wa + wb + wc + wd;
            // Nothing is contributing at all - every reading is inside the noise band, so there is no
            // "active wheel" subset to average. Fall back to the plain four-slot mean, which in this
            // situation is itself a near-zero number; this keeps the transition into and out of a fully
            // idle frame continuous rather than stepping between two different definitions.
            if (weight <= NoRawSignalEpsilon) return (a + b + c + d) / 4.0;

            return (wa * a + wb * b + wc * c + wd * d) / weight;
        }

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

        /// <summary>FEATURE C - WheelLock's own learned S75/S90 anchors (see
        /// <see cref="LockAnchorLearner"/>). Exposed so the composition root can Import/Export it through
        /// <c>RuntimeStore</c> at Init/every frame, mirroring <see cref="LockScaleLearner"/>'s own
        /// exposure. No Slip equivalent - see <see cref="_lockAnchors"/>'s own remarks.</summary>
        public LockAnchorLearner LockAnchors => _lockAnchors;

        /// <summary>The Lock channel's shared, (game,car)-only physical-limit detector (RuntimeDocument
        /// Version 4, docs\cold-start-and-timing-fix-report.md) - exposed so the composition root can
        /// Import/Export it through <c>RuntimeStore</c> at Init/every frame, mirroring
        /// <see cref="LockLearners"/>'s own exposure. Previously session-scoped only (flagged, not fixed,
        /// in docs\f1-normalization-fix-report.md's own Concerns): with severity now driven solely by
        /// <see cref="KeyedScaleLearner"/>'s PRIMARY (physically-anchored) tier, this detector is what
        /// gates that tier's own calibration - a driver who restarts SimHub mid-session re-cold-starts it
        /// (and therefore calibration) every time unless it survives the restart like every other learner
        /// here does.</summary>
        public KeyedGripLearner LockPhysicalReference => _lockPhysicalReference;

        /// <summary>The Slip channel's equivalent of <see cref="LockPhysicalReference"/>.</summary>
        public KeyedGripLearner SlipPhysicalReference => _slipPhysicalReference;

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

        /// <summary>SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - whether the
        /// MOST RECENT frame's Lock severity/proportions were substituted from Layer 3's own Raw because
        /// the configured source read near-zero while Raw independently read a genuine, well-above-floor
        /// value. Published as <c>Diag.Lock.SourceFallbackActive</c> so a driver/rig can tell "the
        /// configured source went quiet and we substituted Raw" from "genuinely no lockup" - see this
        /// class's own history note.</summary>
        public bool LockSourceFallbackActive => _lockSourceFallbackActive;

        /// <summary>The Slip channel's equivalent of <see cref="LockSourceFallbackActive"/>.</summary>
        public bool SlipSourceFallbackActive => _slipSourceFallbackActive;

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

        /// <summary>SHAKEIT-SILENCE FALLBACK diagnostic - the current continuous 0..1 blend weight
        /// (<see cref="ComputeBlendWeight"/> applied to the most recently smoothed divergence) toward
        /// Layer 3's own Raw for the PER-WHEEL proportions (see the DELTA-G COLLAPSE BAND MAPPING note:
        /// this weight no longer also decides the car-level severity, only which per-wheel values
        /// distribute it). Exposed purely for diagnostics/tests - mirrors
        /// <see cref="LockSourceFallbackActive"/>'s own boolean, at full resolution.</summary>
        public double LockFallbackWeight => ComputeBlendWeight(_lockFallbackDivergence);

        /// <summary>The Slip channel's equivalent of <see cref="LockFallbackWeight"/>.</summary>
        public double SlipFallbackWeight => ComputeBlendWeight(_slipFallbackDivergence);

        /// <summary>DELTA-G COLLAPSE BAND MAPPING diagnostic - the most recently computed u = g /
        /// SpeedAwarePeakG(v) (surface-blended, cold-start-ceilinged) that fed <see cref="LockAll"/>'s own
        /// rising branch this frame. 0.0 before the channel has ever been engaged.</summary>
        public double LockUtilization => _lockUtilization;

        /// <summary>The most recently computed branch confidence b (gate(u)*collapse) for Lock.</summary>
        public double LockBranchConfidence => _lockBranchConfidence;

        /// <summary>The Slip channel's equivalent of <see cref="LockUtilization"/>.</summary>
        public double SlipUtilization => _slipUtilization;

        /// <summary>The Slip channel's equivalent of <see cref="LockBranchConfidence"/>.</summary>
        public double SlipBranchConfidence => _slipBranchConfidence;

        /// <summary>The most recently computed CAR-LEVEL severity (before per-wheel redistribution) for
        /// Lock - see <see cref="_lockCarLevelSeverity"/>'s own remarks for why this differs from
        /// <see cref="NormalizedWheelLockSlipResult.LockAll"/> whenever the configured source's own
        /// per-wheel readings are non-uniform.</summary>
        public double LockCarLevelSeverity => _lockCarLevelSeverity;

        /// <summary>The Slip channel's equivalent of <see cref="LockCarLevelSeverity"/>.</summary>
        public double SlipCarLevelSeverity => _slipCarLevelSeverity;

        /// <summary>
        /// AGGREGATED-ALL SCALE (docs\all-channel-scale-and-surface-gap-report.md) - the most recently
        /// computed uniform multiplier applied to every one of the Lock channel's four blended per-wheel
        /// native readings (<c>severity / Aggregator.Compute(blendedSourceWheels, weights).All</c>) - see
        /// <see cref="ComputeChannel"/>'s own remarks. 1.0 while the channel has never been engaged (the
        /// neutral, no-op scale) or while on a path the mid-chain clamp fix does not target (see
        /// <see cref="ComputeChannel"/>'s own per-branch remarks - docs\clamp-chain-fix-report.md). Also
        /// the multiplier <see cref="Compute"/> applies to <see cref="_lockNativeAggregate"/> to build the
        /// published Front/Rear/Left/Right/All. Exposed purely for diagnostics/validation, mirroring
        /// <see cref="LockUtilization"/>'s own exposure pattern.
        /// </summary>
        public double LockAllScale => _lockAllScale;

        /// <summary>
        /// The Slip channel's equivalent of <see cref="LockAllScale"/> - CHANGED (docs\clamp-chain-fix-
        /// report.md): no longer pinned to 1.0 just because <see cref="SlipUsesAggregatedAllScale"/> is
        /// false. Slip's own pre-existing scaling path (the flat-mean-relative proportion,
        /// <c>severity * (bw_i / flatMean(bw))</c>, UNCHANGED - see <see cref="ComputeChannel"/>'s own
        /// remarks) already applies ONE uniform multiplier to all four wheels
        /// (<c>severity / flatMean(bw)</c>); this property now surfaces that multiplier instead of always
        /// reading 1.0, so <see cref="Compute"/> can apply it to <see cref="_slipNativeAggregate"/> the
        /// same way Lock's own multiplier is applied to <see cref="_lockNativeAggregate"/>.
        /// </summary>
        public double SlipAllScale => _slipAllScale;

        /// <summary>
        /// LAYER 5 COLD-START DEVICE-FEEL SCALE (v1.0.6.9 rework, Goal 2 - docs\v1068-rework-report.md) -
        /// the Lock channel's own physical reference's most recently computed PLAIN, sample-count-only
        /// <see cref="GripLearner.Confidence"/> (surface-blended exactly like <see cref="LockUtilization"/>'s
        /// own denominator) - fed to <see cref="Projection.ColdStartScale"/> at Layer 5 to damp the FELT
        /// output while this channel's own reference is still building trust, WITHOUT clamping
        /// Normalized itself (see <see cref="GripLearner.Ratio"/>'s own <c>useStabilityGatedCeiling</c>
        /// remarks for why Normalized's own cold-start ceiling deliberately no longer also requires
        /// settledness). 0.0 before the channel has ever been observed.
        /// </summary>
        public double LockColdStartConfidence => _lockColdStartConfidence;

        /// <summary>The Slip channel's equivalent of <see cref="LockColdStartConfidence"/>.</summary>
        public double SlipColdStartConfidence => _slipColdStartConfidence;

        /// <summary>TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md) -
        /// which tier the Lock channel's own <see cref="KeyedScaleLearner"/> most recently resolved to for
        /// the currently-configured (gameId, carId, sourceIdentity, surface) key - see
        /// <see cref="KeyedScaleLearner.ResolveTier"/>. Published as <c>Diag.Lock.ColdStartTier</c> so the
        /// owner can see which tier engaged on a rig.</summary>
        public ColdStartTier LockColdStartTier => _lockColdStartTier;

        /// <summary>The Slip channel's equivalent of <see cref="LockColdStartTier"/>.</summary>
        public ColdStartTier SlipColdStartTier => _slipColdStartTier;

        /// <summary>The Layer 5 <see cref="Projection.ColdStartScale"/> Stage-1 floor implied by
        /// <see cref="LockColdStartTier"/> - see <see cref="ColdStartTierFloors"/>.</summary>
        public double LockColdStartFloor => ColdStartTierFloors.FloorFor(_lockColdStartTier);

        /// <summary>The Slip channel's equivalent of <see cref="LockColdStartFloor"/>.</summary>
        public double SlipColdStartFloor => ColdStartTierFloors.FloorFor(_slipColdStartTier);

        /// <summary>FEATURE C diagnostic - see <see cref="_lockFourRangeCurveActive"/>'s own remarks.</summary>
        public bool LockFourRangeCurveActive => _lockFourRangeCurveActive;

        /// <summary>Always false - Feature C is WheelLock ONLY.</summary>
        public bool SlipFourRangeCurveActive => _slipFourRangeCurveActive;

        /// <summary>
        /// BUG FIX (docs\pipeline-exception-safety-report.md, Part B): the exact surface bucket
        /// (<see cref="SealedSurfaceBucket"/>/<see cref="LooseSurfaceBucket"/>) <see cref="ComputeChannel"/>
        /// itself is CURRENTLY observing the Lock channel's learner under - resolved from
        /// <see cref="LockLooseFraction"/> with the SAME <c>SurfaceLearningPurityThreshold</c> test
        /// <see cref="ComputeChannel"/> uses internally to compute its own (private, not otherwise
        /// exposed) <c>observeBucket</c>. Null while the surface reading is ambiguous (neither
        /// confidently sealed nor confidently loose) - mirrors <c>observeBucket</c>'s own "teach
        /// neither" null case exactly, so a caller never guesses a bucket this frame did not actually
        /// teach.
        /// <para/>
        /// WHY THIS EXISTS: <c>QAdvanceFeedback.cs</c>'s own <c>DataUpdate</c> used to query
        /// <see cref="LockLearners"/>'s <c>PublishedPeakG</c>/<c>Confidence</c> with NO surface-bucket
        /// argument at all (defaulting to the empty-string bucket) for the <c>Diag.Lock.LearnedPeakG</c>/
        /// <c>Diag.Lock.LearnerConfidence</c> readout, while <see cref="ComputeChannel"/> only ever
        /// <c>Observe</c>s under the REAL "Sealed"/"Loose" bucket above - two different dictionary keys
        /// (see <see cref="KeyedGripLearner.MakeKey"/>), so that diagnostic read NEVER found the entry
        /// real accumulation was actually writing to, and permanently read back
        /// <see cref="GripLearner.SeedPeakG"/>/0 confidence regardless of how much real learning had
        /// happened - exactly the "accumulation looks stuck" symptom the owner reported, confirmed by
        /// replaying this project's own captured F1 25 logs (every row showed
        /// <c>Diag.Lock.LearnedPeakG</c>==1/<c>LearnerConfidence</c>==0 even while
        /// <c>QAdvanceFeedback.Parameters.json</c> demonstrably persisted a real, mature learned peak for
        /// the same session). The actual accumulation/persistence path (<see cref="KeyedGripLearner.ExportAll"/>)
        /// was never affected - this was a read-side key mismatch, not an accumulation bug.
        /// </summary>
        public string LockCurrentSurfaceBucket => ResolveCurrentSurfaceBucket(_lockLooseFraction);

        /// <summary>The Slip channel's equivalent of <see cref="LockCurrentSurfaceBucket"/>.</summary>
        public string SlipCurrentSurfaceBucket => ResolveCurrentSurfaceBucket(_slipLooseFraction);

        private static string ResolveCurrentSurfaceBucket(double smoothedLooseFraction)
        {
            if (smoothedLooseFraction <= SurfaceLearningPurityThreshold) return SealedSurfaceBucket;
            if (smoothedLooseFraction >= 1.0 - SurfaceLearningPurityThreshold) return LooseSurfaceBucket;
            return null;
        }

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
            _lockFallbackDivergence = 0.0;
            _slipFallbackDivergence = 0.0;
            _lockLastG = null;
            _slipLastG = null;
            _lockAtLimitLastG = null;
            _lockAtLimitLastBasis = null;
            _slipAtLimitLastG = null;
            _slipAtLimitLastBasis = null;
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
        /// <param name="layer3RawLockWheels">
        /// SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - Layer 3's OWN Lock
        /// per-wheel Raw values, ALWAYS computed independently of whatever source is actually configured
        /// (see <c>QAdvanceFeedback.cs</c>'s own <c>legacy.LockWheels</c>, published verbatim as
        /// <c>WheelLock.Raw.*</c> regardless of this engine's source). Used ONLY as a fallback detector +
        /// fallback value for the narrow case where the CONFIGURED source (<paramref name="rawLockWheels"/>)
        /// reads near-zero while this independent measurement reads a genuine, well-above-floor value -
        /// see this class's own history note. Defaults to <c>Corners.Zero</c> so every existing
        /// caller/test (which never varies this) keeps compiling and behaving exactly as before - a
        /// permanently-zero fallback input can never look like a genuine disagreement, so the fallback
        /// simply never engages for them.</param>
        /// <param name="layer3RawSlipWheels">The Slip channel's equivalent of
        /// <paramref name="layer3RawLockWheels"/>.</param>
        public NormalizedWheelLockSlipResult Compute(
            ITelemetrySample sample, Corners rawLockWheels, Corners rawSlipWheels,
            string gameId = "", string carId = "", LegacyThresholds? thresholds = null,
            AggregationWeights? lockAggregation = null, AggregationWeights? slipAggregation = null,
            string lockSourceIdentity = "", string slipSourceIdentity = "",
            Corners layer3RawLockWheels = default(Corners), Corners layer3RawSlipWheels = default(Corners))
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
                ref _lockLooseFraction, layer3RawLockWheels, lockWeights, LockUsesAggregatedAllScale,
                out _lockScaleCeiling, out _lockScaleCeilingIsPrimaryTier,
                out _lockSourceFallbackActive, ref _lockFallbackDivergence, ref _lockLastG, out _lockUtilization, out _lockBranchConfidence,
                out _lockCarLevelSeverity, out _lockAllScale, out _lockColdStartConfidence, _lockAnchors, out _lockFourRangeCurveActive,
                ref _lockAtLimitLastG, ref _lockAtLimitLastBasis, out _lockNativeAggregate,
                useFourRangeForSeverity: LockNormalizePattern == NormalizePattern.Mapping,
                manualAnchors: LockManualAnchors,
                reportManualApplied: applied => _lockManualAnchorsApplied = applied,
                manualGate: _manualGate);
            Corners slipWheels = ComputeChannel(sample.New, rawSlipWheels, motion, _slipLearners, _slipPhysicalReference, _slipScaleLearner,
                gameId, carId, slipSourceIdentity, instantLooseFraction,
                direction == LongitudinalMotionState.SpeedingUp, slipTriggered, slipObserveAllowed, dtSeconds,
                ref _slipLooseFraction, layer3RawSlipWheels, slipWeights, SlipUsesAggregatedAllScale,
                out _slipScaleCeiling, out _slipScaleCeilingIsPrimaryTier,
                out _slipSourceFallbackActive, ref _slipFallbackDivergence, ref _slipLastG, out _slipUtilization, out _slipBranchConfidence,
                out _slipCarLevelSeverity, out _slipAllScale, out _slipColdStartConfidence, lockAnchorLearner: null, out _slipFourRangeCurveActive,
                ref _slipAtLimitLastG, ref _slipAtLimitLastBasis, out _slipNativeAggregate,
                // SLIP ONLY - see the floor's own remarks at the severity assignment. The Lock call above
                // deliberately does not pass this, so Lock keeps the F1 25 fix's behaviour exactly.
                floorSeverityAtRawBasis: true,
                useFourRangeForSeverity: SlipNormalizePattern == NormalizePattern.Mapping,
                manualAnchors: SlipManualAnchors,
                reportManualApplied: applied => _slipManualAnchorsApplied = applied,
                manualGate: _manualGate);

            // TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md) -
            // diagnostic-only readout of which tier THIS key most recently resolved to (the actual
            // blend/anchor computation already happened inside ComputeChannel's own Rescale/LearnedCeiling
            // calls, using the per-frame surface classification directly - see those call sites' own
            // remarks). Recomputed here from the SAME smoothed loose fraction ComputeChannel just updated
            // (ref _lockLooseFraction/_slipLooseFraction above), via the SAME Sealed/Loose/ambiguous
            // classification LockCurrentSurfaceBucket/SlipCurrentSurfaceBucket already expose - "ambiguous"
            // (null) is treated as Sealed here too, mirroring "no surface data reported always resolves to
            // Sealed" elsewhere in this class.
            _lockColdStartTier = _lockScaleLearner.ResolveTier(gameId, carId, lockSourceIdentity, LockCurrentSurfaceBucket ?? SealedSurfaceBucket);
            _slipColdStartTier = _slipScaleLearner.ResolveTier(gameId, carId, slipSourceIdentity, SlipCurrentSurfaceBucket ?? SealedSurfaceBucket);

            // ---- MID-CHAIN CLAMP FIX (docs\clamp-chain-fix-report.md) - THE ALL-CHANNEL
            // CLAMP-THEN-REAGGREGATE BUG, now closed for EVERY published group channel on BOTH channels
            // (superseding the narrower "DEFECT A FIX" this comment used to describe, which covered only
            // Lock's own `All`). The published Front/Rear/Left/Right/All are no longer built by
            // re-aggregating the already-scaled-AND-CLAMPED per-wheel Corners
            // (`Aggregator.Compute(lockWheels/slipWheels, weights)` - the old code, which silently caps a
            // single fully-locked/fully-spinning wheel's contribution at 100 BEFORE the axle/side blend
            // even sees it, exactly the "AxleBlend(100,0)*0.75+0*0.25=75, All=75*0.90+0*0.10=67.5
            // REGARDLESS of how large allScale is" scenario this fix eliminates). Instead, each channel's
            // own <see cref="ComputeChannel"/> call now ALSO returns the NATIVE (pre-scale, pre-clamp)
            // aggregate (<see cref="_lockNativeAggregate"/>/<see cref="_slipNativeAggregate"/>) alongside
            // the scale it actually applied this frame (<see cref="_lockAllScale"/>/
            // <see cref="_slipAllScale"/> - Lock's own `severity/blendedNativeAggregate`; Slip's own
            // `severity/proportionMean`, the flat-mean-relative formula's OWN uniform multiplier, now
            // surfaced through this same field for the first time - see ComputeChannel's own remarks). The
            // published aggregate is then `ClampMath.To0100(nativeAggregate.X * allScale)` for each of the
            // five fields - ONE clamp, at the very end, applied to a value that already reflects the FULL
            // scale - never an intermediate one. Aggregator's own blend stages are all weighted sums of
            // Max/Min/plain values (homogeneous of degree 1, non-negative weights - see Aggregator's own
            // remarks), so for any path that does NOT need this fix (not triggered/not engaged/no G
            // signal/no raw differentiation - see ComputeChannel's own per-branch remarks),
            // <c>_lockNativeAggregate</c>/<c>_slipNativeAggregate</c> is defined to exactly equal
            // `Aggregator.Compute` of that path's own (unscaled-by-anything-further) per-wheel Corners
            // with `allScale` left at its neutral 1.0 - reproducing today's numbers there BIT-FOR-BIT, a
            // deliberate no-op. This is purely a REORDERING (aggregate-then-scale-then-clamp-once, instead
            // of scale-then-clamp-then-reaggregate) - no aggregation weight, curve anchor, output, or
            // severity formula changes.
            WheelAggregate lockAggregate = new WheelAggregate(
                ClampMath.To0100(_lockNativeAggregate.Front * _lockAllScale),
                ClampMath.To0100(_lockNativeAggregate.Rear * _lockAllScale),
                ClampMath.To0100(_lockNativeAggregate.Left * _lockAllScale),
                ClampMath.To0100(_lockNativeAggregate.Right * _lockAllScale),
                ClampMath.To0100(_lockNativeAggregate.All * _lockAllScale));
            WheelAggregate slipAggregate = new WheelAggregate(
                ClampMath.To0100(_slipNativeAggregate.Front * _slipAllScale),
                ClampMath.To0100(_slipNativeAggregate.Rear * _slipAllScale),
                ClampMath.To0100(_slipNativeAggregate.Left * _slipAllScale),
                ClampMath.To0100(_slipNativeAggregate.Right * _slipAllScale),
                ClampMath.To0100(_slipNativeAggregate.All * _slipAllScale));

            // ---- DEFECT A OVERRIDE, KEPT (docs\v1068-four-range-report.md originally; re-evaluated for
            // docs\clamp-chain-fix-report.md). Lock's own `_lockNativeAggregate.All` is now DEFINED (see
            // ComputeChannel's own remarks on the `useAggregatedAllScale` branch) to be exactly
            // `blendedNativeAggregate` - the SAME quantity `_lockAllScale` was divided FROM
            // (`allScale = severity / blendedNativeAggregate`) - so `_lockNativeAggregate.All * _lockAllScale`
            // is now the ALGEBRAIC IDENTITY `blendedNativeAggregate * (severity / blendedNativeAggregate)`,
            // which equals `severity` exactly whenever `blendedNativeAggregate != 0`. This override is
            // THEREFORE NOT STRICTLY NEEDED for the common case any more - but it is deliberately KEPT
            // (not removed) for two reasons this task's own brief says to weigh: (1) it is not PROVABLY
            // redundant in the one degenerate edge case `ClampMath.SafeDiv`'s own fallback covers -
            // `blendedNativeAggregate == 0` exactly, where `allScale` falls back to its neutral 1.0 rather
            // than being derived from `severity` at all, so `nativeAggregate.All * allScale` would read 0
            // instead of `severity`; and (2) `x * (y / x) == y` is a real-number identity, not a
            // floating-point one - IEEE754 rounding can very rarely leave the two sides a few ULPs apart.
            // The override makes the "`Lock.All == carLevelSeverity`, always, exactly" guarantee hold by
            // CONSTRUCTION rather than by (overwhelmingly likely, but not perfectly guaranteed) arithmetic
            // coincidence - cheap insurance the brief explicitly sanctions keeping "if in doubt."
            // NOTE: SlipUsesAggregatedAllScale is a compile-time `const false` (unchanged by this task -
            // see its own remarks) - an equivalent `if (SlipUsesAggregatedAllScale) ...` branch here would
            // be permanently dead code (CS0162), so it is deliberately omitted rather than written and
            // disabled. Slip's own All no longer needs (and never had) an equivalent override - it is now
            // computed by the SAME native-aggregate-times-scale-then-clamp-once path as every other Slip
            // channel, closing exactly the gap Slip previously had on EVERY published field including All.
            if (LockUsesAggregatedAllScale)
                lockAggregate = new WheelAggregate(lockAggregate.Front, lockAggregate.Rear, lockAggregate.Left, lockAggregate.Right, ClampMath.To0100(_lockCarLevelSeverity));

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
            bool observeAllowed, double dtSeconds, ref double smoothedLooseFraction, Corners layer3RawWheels,
            AggregationWeights weights, bool useAggregatedAllScale,
            out double? scaleCeiling, out bool scaleCeilingIsPrimaryTier, out bool sourceFallbackActive,
            ref double smoothedFallbackDivergence, ref double? lastG, out double utilization, out double branchConfidence,
            out double carLevelSeverity, out double allScale, out double coldStartConfidence,
            LockAnchorLearner lockAnchorLearner, out bool fourRangeCurveActive,
            ref double? atLimitLastG, ref double? atLimitLastBasis, out WheelAggregate nativeAggregate,
            bool useFourRangeForSeverity = true, bool floorSeverityAtRawBasis = false,
            ManualAnchors manualAnchors = default(ManualAnchors), Action<bool> reportManualApplied = null,
            ManualOverrideGate manualGate = null)
        {
            // MID-CHAIN CLAMP FIX (docs\clamp-chain-fix-report.md) - default, no-op value for every early
            // return below that this fix does not target (not triggered/not engaged/no G signal at all):
            // Aggregator.Compute of an all-zero Corners is itself all-zero, so `nativeAggregate.X * 1.0`
            // (allScale's own default, set right below) reproduces that path's own all-zero Corners
            // exactly. Branches that DO need a real value (the no-raw-differentiation branch, and both
            // channels' own real scaling paths) overwrite this before returning - see each branch's own
            // remarks.
            nativeAggregate = default(WheelAggregate);
            fourRangeCurveActive = false;
            utilization = 0.0;
            branchConfidence = 0.0;
            carLevelSeverity = 0.0;
            allScale = 1.0;
            // LAYER 5 COLD-START DEVICE-FEEL SCALE (v1.0.6.9 rework, Goal 2 - Core\Projection\ColdStartScale.cs,
            // ported from QAdvanceFeedback_1.0.6.2_prerelease). Deliberately the PLAIN, sample-count-only
            // Confidence (NOT MaturityConfidence - see GripLearner.Ratio's own useStabilityGatedCeiling
            // remarks), blended Sealed/Loose exactly like utilization/dGScale below, and computed
            // regardless of engagement/trigger state - a channel's own "how much do we trust this
            // reference yet" is a property of the LEARNER, not of whether THIS frame happens to be
            // actively braking/accelerating, so Layer 5 sees a stable, non-flickering confidence across
            // brief gaps between braking zones rather than it dropping to 0 between corners.
            double coldStartConfidenceSealed = physicalReference.Confidence(gameId, carId, PhysicalReferenceSourceIdentity, SealedSurfaceBucket);
            double coldStartConfidenceLoose = physicalReference.Confidence(gameId, carId, PhysicalReferenceSourceIdentity, LooseSurfaceBucket);
            coldStartConfidence = Blend(coldStartConfidenceSealed, coldStartConfidenceLoose, smoothedLooseFraction);
            double w0 = ClampMath.To0100(rawWheels.FrontLeft);
            double w1 = ClampMath.To0100(rawWheels.FrontRight);
            double w2 = ClampMath.To0100(rawWheels.RearLeft);
            double w3 = ClampMath.To0100(rawWheels.RearRight);

            double lw0 = ClampMath.To0100(layer3RawWheels.FrontLeft);
            double lw1 = ClampMath.To0100(layer3RawWheels.FrontRight);
            double lw2 = ClampMath.To0100(layer3RawWheels.RearLeft);
            double lw3 = ClampMath.To0100(layer3RawWheels.RearRight);

            // HOISTED (speed-aware-reference-fix-report.md) - originally computed further down, just
            // before the old physicalRatioSealed/Loose block. Moved up here (a pure, side-effect-free
            // computation depending only on w0-w3/lw0-lw3/weights/useAggregatedAllScale, all already
            // available) so the CORNER-LOCAL AT-LIMIT GATE below - which needs "this frame's own raw
            // calibration basis" as its "effort" signal - can read it BEFORE the speed-bucket-teaching
            // call it gates. See DEFECT B FIX below (docs\v1068-four-range-report.md) for the full
            // rationale of what these represent; unchanged in every other respect.
            // NOTE: the flat four-slot mean that used to be computed here fed exactly ONE consumer, the
            // non-aggregated calibration basis below, and is now folded into
            // ContributionWeightedMean (which still reduces to it exactly whenever all four wheels
            // contribute - see that method's own remarks). layer3RawMeanEarly is a DIFFERENT quantity
            // with other consumers (the Slip raw floor and the divergence math further down) and stays
            // the plain four-slot mean, deliberately untouched.
            double layer3RawMeanEarly = (lw0 + lw1 + lw2 + lw3) / 4.0;
            // MID-CHAIN CLAMP FIX (docs\clamp-chain-fix-report.md): these two now capture the FULL
            // WheelAggregate (Front/Rear/Left/Right/All), not just .All as before - the extra four fields
            // are used below (the `useAggregatedAllScale` branch) to build the NATIVE Front/Rear/Left/
            // Right aggregate the SAME way `blendedNativeAggregate`/`.All` already was, closing the same
            // clamp-then-reaggregate gap for those four fields that the pre-existing fix already closed
            // for All alone. `aggregatedNativeConfigured`/`aggregatedNativeFallback` (the `.All` scalars)
            // are UNCHANGED, byte-identical to before.
            WheelAggregate nativeAggConfiguredFull = useAggregatedAllScale ? Aggregator.Compute(new Corners(w0, w1, w2, w3), weights) : default(WheelAggregate);
            WheelAggregate nativeAggFallbackFull = useAggregatedAllScale ? Aggregator.Compute(new Corners(lw0, lw1, lw2, lw3), weights) : default(WheelAggregate);
            double aggregatedNativeConfigured = useAggregatedAllScale ? nativeAggConfiguredFull.All : 0.0;
            double aggregatedNativeFallback = useAggregatedAllScale ? nativeAggFallbackFull.All : 0.0;
            // CONTRIBUTION-WEIGHTED FLAT BASIS (docs\cross-channel-smax-report.md) - the non-aggregated
            // branch (Slip) no longer divides by four slots regardless of how many wheels are actually
            // involved; see ContributionWeightedMean's own remarks for the measurement and for why this
            // is bit-for-bit the previous flat mean whenever all four wheels contribute. BOTH bases move
            // together, deliberately: each is fed to the scale learner AND divided by it again on the way
            // out (ObserveGeneral/ObserveAtPhysicalLimit and Rescale below both take these SAME values),
            // so the units stay consistent end to end and the configured-vs-fallback divergence test
            // further down keeps comparing two quantities on one scale. The aggregated branch (Lock) is
            // untouched and never evaluates this.
            double calibrationBasisConfigured = useAggregatedAllScale
                ? aggregatedNativeConfigured
                : ContributionWeightedMean(w0, w1, w2, w3);
            double calibrationBasisFallback = useAggregatedAllScale
                ? aggregatedNativeFallback
                : ContributionWeightedMean(lw0, lw1, lw2, lw3);

            // SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md) - the SAME
            // GroundSpeedKmh-falling-back-to-SpeedKmh convention every other speed-consuming class in
            // this layer already uses (AchievedMotion, LongitudinalDirectionResolver,
            // TelemetryLearningGate), so a title that only ever populates the fallback field still
            // teaches/queries the speed-bucketed model correctly. Threaded through to both
            // GripLearner.Observe/Ratio calls below - null (or out of GripLearner's own plausible range)
            // simply never accumulates/queries speed-bucketed evidence, degrading to today's flat
            // behaviour for every title/test that never populates either field.
            double? speedKmh = frame?.GroundSpeedKmh ?? frame?.SpeedKmh;

            sourceFallbackActive = false;
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
            {
                // DELTA-G COLLAPSE state reset: a channel that is not even triggered is not a
                // "qualifying run" - the next triggered frame must not compare its own g against a
                // stale value from before this gap (see _lockLastG/_slipLastG's own remarks).
                lastG = null;
                // CORNER-LOCAL AT-LIMIT GATE state reset - same reasoning, same gap.
                atLimitLastG = null;
                atLimitLastBasis = null;
                // FEATURE C - the anchor learner's own run-bracket tracking breaks for the same reason.
                lockAnchorLearner?.ResetRun(gameId, carId, sourceIdentity);
                return Corners.Zero;
            }

            // Degradation floor (ladder level 3): no g signal at all, direct or derived - Raw is the
            // only available basis, so it is passed through (but PER-SOURCE CALIBRATED - see
            // KeyedScaleLearner's own remarks - using whatever has already been learned; there is no G
            // signal this frame to detect a fresh physical-limit moment, so nothing new is OBSERVED
            // here, only whatever calibration already exists is APPLIED) rather than reading zero,
            // garbage, or the source's own unrescaled native magnitude.
            if (motion.Level == AchievedMotion.SignalLevel.Unavailable)
            {
                // No G channel this frame (direct or derived) - see _lockLastG/_slipLastG's own
                // remarks: the next frame that DOES have a genuine g reading must not diff against
                // whatever g happened to be current before this gap.
                lastG = null;
                atLimitLastG = null;
                atLimitLastBasis = null;
                lockAnchorLearner?.ResetRun(gameId, carId, sourceIdentity);
                // OUT OF SCOPE for the mid-chain clamp fix (this is KeyedScaleLearner.Rescale's own
                // independent per-wheel calibration, not the severity-driven allScale/mean-relative-scale
                // path the fix targets - see this class's own remarks). `allScale` stays at its neutral
                // 1.0 default; `nativeAggregate` is set to Aggregator.Compute of these SAME (already
                // per-wheel-rescaled) Corners, so `nativeAggregate.X * 1.0` reproduces this branch's own
                // published Front/Rear/Left/Right/All bit-for-bit, unchanged from before this fix.
                Corners rescaled = new Corners(
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w0),
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w1),
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w2),
                    scaleLearner.Rescale(gameId, carId, sourceIdentity, w3));
                nativeAggregate = Aggregator.Compute(rescaled, weights);
                return rescaled;
            }

            // "engaged" = this channel's own direction (Slowing for Lock, SpeedingUp for Slip) is what
            // LongitudinalDirectionResolver measured THIS frame - see this class's own remarks on why
            // pedal state is never consulted here. Not engaged -> nothing to attribute the magnitude
            // to at all.
            if (!engaged)
            {
                // Direction Unknown (or the wrong direction for this channel) - not a qualifying run
                // either; see the reset above.
                lastG = null;
                atLimitLastG = null;
                atLimitLastBasis = null;
                lockAnchorLearner?.ResetRun(gameId, carId, sourceIdentity);
                return Corners.Zero;
            }

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

            // HOISTED (speed-aware-reference-fix-report.md) - originally computed further down (see the
            // v1.0.6.9 REWORK history note just below, unchanged). Moved up here, before the
            // speed-bucket-teaching call below, purely so the CORNER-LOCAL AT-LIMIT GATE (right after)
            // can use physicalRatioNow as its own coarse plausibility floor - identical formula/values to
            // before, just computed one step earlier. calibrationBasisConfigured (this frame's own raw
            // "effort") is likewise now available from the earlier hoist above.
            double physicalRatioSealed = physicalReference.Ratio(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, SealedSurfaceBucket, applyColdStartCeiling: false);
            double physicalRatioLoose = physicalReference.Ratio(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, LooseSurfaceBucket, applyColdStartCeiling: false);
            double physicalRatioNow = Blend(physicalRatioSealed, physicalRatioLoose, smoothedLooseFraction);
            bool physicallyAtLimit = physicalRatioNow >= PhysicalLimitRatioThreshold;

            // CORNER-LOCAL AT-LIMIT GATE (docs\speed-aware-reference-fix-report.md - see the class-level
            // history note above AtLimitEffortDropToleranceFraction). Cold start: atLimitLastG has no
            // value on the first qualifying frame of a run (or right after any of the resets above) -
            // atLimitWeight is then 0.0 (identity - "under-report rather than over-report while cold",
            // satisfied structurally rather than by a special-cased branch), and this frame simply teaches
            // the speed bucket nothing yet, exactly like a genuinely ambiguous frame would.
            double atLimitWeight = atLimitLastG.HasValue
                ? ComputeCornerAtLimitConfidence(
                    physicalReference, gameId, carId, motion.MagnitudeG, calibrationBasisConfigured, smoothedLooseFraction,
                    atLimitLastG.Value, atLimitLastBasis ?? 0.0, physicalRatioNow)
                : 0.0;
            atLimitLastG = motion.MagnitudeG;
            atLimitLastBasis = calibrationBasisConfigured;

            if (observeAllowed && IsLongitudinallyIsolated(frame) && observeBucket != null)
            {
                // Kept purely for the Diag.Lock/Slip.LearnedPeakG/LearnerConfidence diagnostics and
                // RuntimeStore persistence continuity (docs\f1-normalization-fix-report.md) - its
                // Ratio() is no longer queried for the live severity below (see this class's own
                // history note), only Observe()'s side effect of keeping the learner itself alive.
                learners.Observe(gameId, carId, motion.MagnitudeG, sourceIdentity, observeBucket, speedKmh);
                // SHARED physical-limit reference (docs\branch-dispatch-and-source-keyed-learning-report.md)
                // - always the (game,car)-only source key, regardless of which source is actually
                // configured, but STILL surface-keyed (the physics genuinely differs by surface too).
                // This ONE remains live-relevant: it is what teaches KeyedScaleLearner below. atLimitWeight
                // gates ONLY the speed-bucket admission inside GripLearner.Observe (see that method's own
                // remarks) - the flat, non-speed-aware peak this same call also feeds keeps updating
                // unconditionally, exactly as before this fix.
                physicalReference.Observe(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, observeBucket, speedKmh, atLimitWeight);
            }

            // DEFECT B FIX (docs\v1068-four-range-report.md) - MAX-GRIP CALIBRATION BASIS, RECONCILED
            // WITH 1.0.6.3 (QAdvanceFeedback_1.0.6.3_altprerelease's own ComputeChannel): when
            // useAggregatedAllScale is set (Lock), the basis ObserveAtPhysicalLimit/ObserveGeneral/
            // Rescale/the divergence math below all calibrate against is the SAME weighted aggregate
            // that will re-combine the published output (Aggregator.Compute(...).All), NOT the flat
            // mean - so a scale learned here reproduces itself exactly at the All level once applied
            // uniformly (see LockUsesAggregatedAllScale's own remarks). 1.0.6.8 had DRIFTED from this -
            // this ComputeChannel fed the flat mean into the scale learner unconditionally, so Lock's own
            // learned ceiling (Smax) no longer matched what 1.0.6.3 would learn from the SAME telemetry.
            // Byte-identical to the pre-existing flat-mean basis when the flag is false (Slip - every
            // pre-existing behaviour is preserved for the channel that does not ship this). NOTE: the
            // actual mean/aggregatedNative*/calibrationBasis* ASSIGNMENTS now live in the earlier hoist
            // above (speed-aware-reference-fix-report.md) - this comment block is kept here, in its
            // original place, purely as the rationale for what those hoisted values represent.
            //
            // PER-SOURCE INPUT CALIBRATION (docs\branch-dispatch-and-source-keyed-learning-report.md):
            // detect whether THIS frame is physically at this car's own learned grip limit - using the
            // SHARED, (game,car)-only physical reference, never the source-keyed one above - and, if so,
            // teach the scale learner what THIS source's own raw reading looks like at that moment.
            // Blended the same way as gripUtilization, for the same continuity reason.
            //
            // CONTINUOUS CONFIDENCE, NO ABSOLUTE-COUNT GATE, ONE SINGLE CONFIDENCE NOTION
            // (docs\regression-fix-report.md - the owner's own "why not just lower the sample bar, and
            // why not for every title" follow-up, then their own concrete blend specification): an
            // EARLIER pass here required physicalReference's OWN confidence to reach a fixed sample count
            // (first 200, then 60) before ANY frame could ever be treated as "at the limit" - still a
            // hard cliff, and worse, an ABSOLUTE COUNT tuned against exactly one captured title's own
            // qualifying-frame rate. A LATER pass then tried a second, separate continuous weight here
            // (how much THIS shared detector's own G-evidence should be trusted) on top of
            // <see cref="KeyedScaleLearner"/>'s own - exactly the "second confidence notion" the owner
            // explicitly said to avoid. Both are gone: `Ratio` is called WITHOUT its own confidence-based
            // ceiling (`applyColdStartCeiling: false` - see that method's own remarks) so "is this moment
            // near what we've learned so far" is answered honestly at any sample count, and every
            // qualifying "at the limit" frame teaches <see cref="KeyedScaleLearner"/> at full weight (1.0)
            // - the ONE place a continuous, dispersion-weighted confidence is computed is
            // <see cref="KeyedScaleLearner"/>'s OWN ramp (see its own remarks), which already answers
            // "how much do we trust the ACCUMULATED calibration evidence for this exact key" - a single,
            // sufficient confidence notion, not two. A virgin physical reference's own inflated ratio
            // (dividing by an unlearned seed peak) can therefore look "at the limit" readily in the very
            // first few frames of a session, but this is harmless by construction: KeyedScaleLearner's
            // own concave, dispersion-weighted ramp starts at 0 regardless (its OWN count is what is
            // low, not this one), so a handful of early, possibly-unrepresentative teachings cannot move
            // the published ceiling meaningfully until real, dispersion-confirmed evidence accumulates
            // FOR THIS EXACT KEY.
            // v1.0.6.9 REWORK (docs\v1068-rework-report.md, Goal 3) - speedKmh deliberately NOT passed
            // here any more. This detects "physically at THIS car's own learned max-grip limit" (the
            // 80-anchor calibration trigger) - the owner's own precise scoping: "NOT for the max-grip (80)
            // point - max grip is the moment the dec G-Force starts REDUCING, which needs no speed model."
            // Speed-aware modelling is confined to a narrow role (identifying/learning the 30/60 reference
            // points for VALIDATION, and G-force validation itself - see ComputeDeltaGCollapseSeverity's
            // own remarks) - it must never leak into what decides "at the limit" or what gets published.
            // NOTE: physicalRatioSealed/Loose/Now and physicallyAtLimit are now computed in the EARLIER
            // hoist above (speed-aware-reference-fix-report.md) - unchanged formula, just computed one
            // step earlier so the corner-local at-limit gate can also read physicalRatioNow.
            // WHICH FRAMES MAY TEACH SMax (docs\cross-channel-smax-report.md - RESTORED from
            // QAdvanceFeedback_1.0.6.9_anchor_sampling, whose own
            // docs\atlimit-gate-and-per-anchor-lag-report.md measured the defect and built the fix).
            //
            // `physicallyAtLimit` is `G >= PhysicalLimitRatioThreshold` of this key's SESSION-WIDE
            // best-ever G - flat, no speed term, nothing about THIS corner's own lock dynamics. Measured
            // across that report's own 14-log corpus, it fires EARLY relative to the corner's real onset
            // in 87.7% of events, median 45.4% low: it trips in the high-speed, high-downforce part of
            // the braking zone, where the tyre is gripping hardest and a lock/slip source therefore reads
            // near its MINIMUM. Teaching SMax from those frames is why the learned ceiling read 26-42 on
            // the owner's own 1.7.1 capture against a true ~75, and why the rescale factor was ~2.7x too
            // large (the over-shake this project has chased since 1.0.6).
            //
            // The corner-local detector is the owner's own stated rule made operational - "max grip is
            // the moment the dec G-Force starts REDUCING, which needs no speed model" - i.e. G plateauing
            // or falling WHILE the source's own effort is sustained or still rising. Measured on the same
            // capture, weighting the at-limit distribution by it moves the learned ceiling to 72-78
            // across two cars AND two sources (1.08x cross-session spread, tighter than anything else
            // tried, including the general-distribution percentile it replaces).
            //
            // WHEELLOCK ONLY - the same non-negotiable scoping 1.0.6.9_anchor_sampling shipped, for the
            // same reason: ComputeChannel is shared, Slip's own collapse numbers are far noisier on this
            // capture (P90 spanning 33-70), and Slip must not move as a side effect of a Lock fix.
            // lockAnchorLearner is the SAME null-for-Slip discriminator the four-range curve already uses.
            bool isLockChannel = lockAnchorLearner != null;
            double smaxTeachingWeight = isLockChannel ? atLimitWeight : (physicallyAtLimit ? 1.0 : 0.0);

            if (calibrationBasisConfigured >= MinRawForCalibrationObservation)
            {
                if (smaxTeachingWeight > 0.0)
                {
                    // The DEFAULT (unsurfaced) key - still the one and only key the live severity reads.
                    scaleLearner.ObserveAtPhysicalLimit(gameId, carId, sourceIdentity, calibrationBasisConfigured, smaxTeachingWeight);
                    // TIERED COLD-START REFERENCE SYSTEM (v1.0.7) - ADDITIVE surface-specific teaching
                    // (see KeyedScaleLearner.ObserveAtPhysicalLimit's own remarks): only when the surface
                    // is confidently classified (observeBucket != null), mirroring the SAME "ambiguous ->
                    // teach neither" convention this method already applies to the GripLearner surface
                    // split just above. Purely gives ResolveReference genuine Tier-4 candidates to find;
                    // never read by the live Rescale/LearnedCeiling calls for the default key.
                    if (observeBucket != null)
                        scaleLearner.ObserveAtPhysicalLimit(gameId, carId, sourceIdentity, calibrationBasisConfigured, smaxTeachingWeight, observeBucket);
                }
                scaleLearner.ObserveGeneral(gameId, carId, sourceIdentity, calibrationBasisConfigured);
            }

            // SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - keep the fallback's
            // OWN calibration warm every qualifying frame, regardless of what source is actually
            // configured (mirroring how physicalReference above is always fed regardless of source), so
            // that WHEN the fallback below actually engages, Rescale is not itself starting cold.
            double layer3RawMean = layer3RawMeanEarly;
            if (calibrationBasisFallback >= MinRawForCalibrationObservation)
            {
                // SAME GATE AS THE CONFIGURED KEY ABOVE - the fallback is this same channel's own Raw
                // reading, so it must be taught at the same moments and with the same confidence, or the
                // two keys would learn ceilings on different definitions and the divergence test between
                // them (further down) would compare quantities that are not comparable.
                if (smaxTeachingWeight > 0.0)
                    scaleLearner.ObserveAtPhysicalLimit(gameId, carId, RawFallbackSourceIdentity, calibrationBasisFallback, smaxTeachingWeight);
                scaleLearner.ObserveGeneral(gameId, carId, RawFallbackSourceIdentity, calibrationBasisFallback);
            }

            // TIERED COLD-START REFERENCE SYSTEM (v1.0.7) - observeBucket (null when the surface is
            // ambiguous this frame) falls back to SealedSurfaceBucket here too, matching "no surface data
            // reported always resolves to Sealed" elsewhere in this class. This is ONLY a hint for
            // KeyedScaleLearner's own tier resolver (see LearnedCeilingForKey's own remarks) - it never
            // changes WHICH key calibratedMean/scaleCeiling are actually read from (still the default,
            // unsurfaced key, unchanged).
            string tierSurfaceHint = observeBucket ?? SealedSurfaceBucket;
            double calibratedMean = scaleLearner.Rescale(gameId, carId, sourceIdentity, calibrationBasisConfigured, tierSurfaceHint);
            scaleCeiling = scaleLearner.LearnedCeiling(gameId, carId, sourceIdentity, out scaleCeilingIsPrimaryTier, tierSurfaceHint);

            // ---- MANUAL KEY DATA POINTS (v1.0.7.2) ----
            //
            // LEARNING IS UNAFFECTED. Everything above this point - ObserveAtPhysicalLimit, ObserveGeneral,
            // the anchor learner's own corner buffering - has already run, unconditionally. This block
            // only decides which SMax is APPLIED to the published output, which is why the learned value
            // stays live and correct for the UI to display, and why toggling back to Auto is instant.
            //
            // THE GATE. A manual value is withheld until this exact context has BOTH finished cold start
            // AND accumulated ManualOverrideGate.MinimumInGameSeconds of real driving (the owner's
            // "whichever is longer"). Before that the learned value is published - a manual number
            // configured elsewhere is not necessarily meaningful the instant a new session starts, and
            // the driver has had no chance to see what this one reads.
            bool manualAnchorsApplied = false;
            string manualGateKey = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, string.Empty);
            if (manualGate != null)
                manualGate.Observe(manualGateKey, dtSeconds, advancing: engaged && speedKmh.HasValue && speedKmh.Value > 0.0);
            bool manualReady = manualAnchors.Active && manualGate != null
                && manualGate.IsReady(manualGateKey, scaleLearner.CeilingHandoverConfidence(gameId, carId, sourceIdentity));
            manualAnchorsApplied = manualReady;

            if (reportManualApplied != null) reportManualApplied(manualAnchorsApplied);

            if (manualReady && manualAnchors.SMax > 0.0)
            {
                scaleCeiling = manualAnchors.SMax;
                // Rescale by hand rather than through the learner: the learner divides by what it has
                // LEARNED, and the whole point here is to divide by what the driver CONFIGURED. Same
                // formula it uses (basis * CanonicalAtLimitAnchor / ceiling), same clamp.
                calibratedMean = ClampMath.To0100(
                    ClampMath.SafeDiv(calibrationBasisConfigured * KeyedScaleLearner.CanonicalAtLimitAnchor,
                        manualAnchors.SMax, calibratedMean));
            }

            // ---- v1068 CORRECTION (docs\v1068-four-range-report.md, "A REAL DEFECT in how Feature C was
            // wired") - THE FOUR-RANGE CURVE IS NOW THE MAPPING FOR LOCK, NOT AN OVERRIDE ON TOP OF THE
            // ΔG-COLLAPSE FORMULA'S OWN RISING TERM. An earlier pass of this task applied the curve only to
            // `rising`, leaving the 80-100 region entirely G-derived (`falling = 80 + 20*b`, requiring
            // gate(u)*collapse(ΔG) to be large) - measured directly against 1.0.6.3 to under-report a
            // genuine full lock by 13-14 points and to shrink the near-80 dwell by 66-72% on the two Raw
            // logs checked, because 1.0.6.3 reaches 80 whenever the CALIBRATED SOURCE is ~80 (a plain
            // linear Rescale), not only when u is pinned near 1.0 with active collapse. THE FIX: for Lock,
            // severity is now the calibrated SOURCE reading end to end (0-100), mapped through the
            // four-range curve when the three anchors are valid, exactly like 1.0.6.3's own single-anchor
            // Rescale otherwise (`calibratedMean`/`calibratedRawFallback`, unchanged formula) - which by
            // CONSTRUCTION reproduces 1.0.6.3's own two defining properties: source==Smax maps to exactly
            // 80 (a literal knot of the curve, or `Smax*(80/Smax)=80` in the Rescale fallback), and
            // source==100 maps to (at least) 100, clamped, exactly like 1.0.6.3's own severity=calibratedMean
            // does. The ΔG-collapse mechanism (u/gate/collapse) is NOT deleted - see
            // ComputeDeltaGCollapseSeverity's own remarks below for what it still does and why.
            double? lockFourRangeSeverityConfigured = null;
            double? lockFourRangeSeverityFallback = null;
            double? twoRangeSeverityConfigured = null;
            double? twoRangeSeverityFallback = null;

            // MANUAL THREE-POINT MAPPING, EITHER CHANNEL (v1.0.7.2). Deliberately OUTSIDE the
            // lockAnchorLearner guard below: Slip passes a null anchor learner (it has no measured
            // S90/S75), so a branch inside that guard could never fire for it - and Slip's
            // Perfect/Great/Good mapping is exactly the case where all three knots are configured rather
            // than measured. Learning is unaffected either way; this only selects what is published.
            if (manualReady && useFourRangeForSeverity && manualAnchors.SMax > 0.0
                && TryBuildLockRangeCurve(manualAnchors.S75, manualAnchors.S90, manualAnchors.SMax,
                    out MonotoneCubicCurve manualRangeCurve))
            {
                lockFourRangeSeverityConfigured = manualRangeCurve.Evaluate(calibrationBasisConfigured);
                lockFourRangeSeverityFallback = manualRangeCurve.Evaluate(calibrationBasisFallback);
                fourRangeCurveActive = true;
            }
            // AUTO + THREE-POINT MAPPING. Slip passes no anchor learner, and Lock's own learned ratios
            // are not always available, so without this a channel under Auto+Mapping had no curve to
            // publish and silently fell back to the plain rescale - the mode looked selected but did
            // nothing. The two lower anchors are derived from the learned ceiling by the SAME fixed
            // percentages the settings page shows, so what the driver reads in the boxes is what the
            // channel actually applies.
            else if (useFourRangeForSeverity && lockAnchorLearner == null && scaleCeiling.HasValue
                && TryBuildDerivedRangeCurve(scaleCeiling.Value, out MonotoneCubicCurve derivedCurve))
            {
                lockFourRangeSeverityConfigured = derivedCurve.Evaluate(calibrationBasisConfigured);
                lockFourRangeSeverityFallback = derivedCurve.Evaluate(calibrationBasisFallback);
                fourRangeCurveActive = true;
            }
            // MAX-GRIP-ONLY IS A CURVE TOO, just a two-segment one - see TryBuildTwoRangeCurve for why a
            // plain rescale got both ends of the range wrong. Applies to BOTH channels (Slip ships this
            // mode), and uses the manual SMax when one is live, otherwise the learned ceiling.
            else if (!useFourRangeForSeverity)
            {
                double? twoRangeSMax = manualReady && manualAnchors.SMax > 0.0
                    ? manualAnchors.SMax
                    : scaleCeiling;
                if (twoRangeSMax.HasValue
                    && TryBuildTwoRangeCurve(twoRangeSMax.Value, out MonotoneCubicCurve twoRangeCurve))
                {
                    twoRangeSeverityConfigured = twoRangeCurve.Evaluate(calibrationBasisConfigured);
                    twoRangeSeverityFallback = twoRangeCurve.Evaluate(calibrationBasisFallback);
                }
            }
            else if (lockAnchorLearner != null)
            {
                double? smaxConfigured = scaleCeiling;
                // S90-FALLBACK-RATIO FIX (docs\release-1060-report.md, Part 3 - "S90 feels a little
                // early"): TryBuildLockRangeCurveWithFallback tries the REAL learned S75/S90 first, and
                // consults the fallback ratios ONLY when that fails to produce a valid, buildable curve -
                // whether because no real anchor has been learned yet at all (null), OR because a real
                // anchor exists but is not (yet) usable (e.g. a single early bracket-crossing frame can
                // register a real-but-degenerate anchor that fails TryBuildLockRangeCurve's own ordering/
                // spacing guards). Consulting the fallback on EITHER failure mode (not just null) keeps
                // this deterministic: the SAME key, queried twice with no intervening real evidence, must
                // resolve to the SAME curve both times, never silently degrading to plain calibratedMean
                // on one visit and the four-range curve on another. The moment a real anchor is BOTH
                // present AND valid, it is used and this fallback is never consulted - the learned-anchor
                // path is completely undisturbed. See S90FallbackRatioOfSmax/S75FallbackRatioOfSmax's own
                // remarks for the measured derivation.
                // NORMALIZE PATTERN (docs\release-1060-report.md, Part 2) - the curve is still built and
                // S75/S90/Smax still LEARN normally regardless of `useFourRangeForSeverity` (the "Max-Grip
                // Only" pattern's own explicit contract: anchors keep learning and persisting even though
                // they are not applied to output in that mode); `fourRangeCurveActive` only reports true
                // when the curve is ALSO actually driving the published severity below.
                if (TryBuildLockRangeCurveWithFallback(lockAnchorLearner, gameId, carId, sourceIdentity, smaxConfigured, scaleLearner, out MonotoneCubicCurve rangeCurveConfigured))
                {
                    lockFourRangeSeverityConfigured = rangeCurveConfigured.Evaluate(calibrationBasisConfigured);
                    fourRangeCurveActive = useFourRangeForSeverity;
                }

                // The SAME curve mechanism, kept warm for the SHAKEIT-SILENCE FALLBACK identity too (see
                // RawFallbackSourceIdentity's own remarks) - mirrors how scaleLearner/physicalReference are
                // always fed both identities, so the fallback blend below is never comparing a curve-mapped
                // value against a Rescale-mapped one on the SAME frame for no reason other than which
                // identity happened to warm up first.
                double? smaxFallback = scaleLearner.LearnedCeiling(gameId, carId, RawFallbackSourceIdentity, out _);
                if (TryBuildLockRangeCurveWithFallback(lockAnchorLearner, gameId, carId, RawFallbackSourceIdentity, smaxFallback, scaleLearner, out MonotoneCubicCurve rangeCurveFallback))
                {
                    lockFourRangeSeverityFallback = rangeCurveFallback.Evaluate(calibrationBasisFallback);
                }

                if (observeAllowed && IsLongitudinallyIsolated(frame))
                {
                    // SPEED-AWARE, NARROWLY (owner's explicit scoping - see LockAnchorLearner's own
                    // remarks): "that corner's own max-grip G" needs speedKmh; this ratio is used SOLELY
                    // to identify/validate the 30/60 anchors, never to decide what gets published.
                    double uSpeedAwareSealed = physicalReference.Ratio(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, SealedSurfaceBucket, applyColdStartCeiling: false, speedKmh: speedKmh, useStabilityGatedCeiling: false);
                    double uSpeedAwareLoose = physicalReference.Ratio(gameId, carId, motion.MagnitudeG, PhysicalReferenceSourceIdentity, LooseSurfaceBucket, applyColdStartCeiling: false, speedKmh: speedKmh, useStabilityGatedCeiling: false);
                    double uSpeedAware = Blend(uSpeedAwareSealed, uSpeedAwareLoose, smoothedLooseFraction);
                    lockAnchorLearner.Observe(gameId, carId, sourceIdentity, uSpeedAware, calibrationBasisConfigured, smaxConfigured);
                    lockAnchorLearner.Observe(gameId, carId, RawFallbackSourceIdentity, uSpeedAware, calibrationBasisFallback, smaxFallback);

                    // PHYSICALLY-DERIVED ANCHORS (docs\cross-channel-smax-report.md) - buffer this frame
                    // for retrospective extraction when the braking event ends. Deliberately fed the SAME
                    // atLimitWeight that teaches SMax, so S75/S90 and SMax are all derived from one
                    // detector on one physical event. Nothing is learned on this call; see
                    // LockAnchorLearner.ObserveCornerFrame for why it cannot be.
                    lockAnchorLearner.ObserveCornerFrame(gameId, carId, sourceIdentity,
                        motion.MagnitudeG, calibrationBasisConfigured, atLimitWeight, smaxConfigured);
                    lockAnchorLearner.ObserveCornerFrame(gameId, carId, RawFallbackSourceIdentity,
                        motion.MagnitudeG, calibrationBasisFallback, atLimitWeight, smaxFallback);
                }
            }

            // ---- DELTA-G COLLAPSE (docs\delta-g-band-mapping-report.md) - DIAGNOSTIC ONLY, for BOTH
            // channels, as of the 1.0.6.0 Slip-regression fix (docs\release-1060-report.md, Part 1). This
            // mechanism was ORIGINALLY built for Lock's own four-range work, but Slip was left calling it
            // for its published severity too and never reverted - the owner reported this explicitly
            // ("TOTALLY messed up... shaked too early, and the output is high... Totally different with
            // 1.0.6.3") after comparing against 1.0.6.3, which has NO ΔG-collapse/band-mapping concept for
            // Slip at all (confirmed by diff: 1.0.6.3's Slip severity is exactly `calibratedMean`, its own
            // single-anchor Rescale, blended toward the Raw fallback). ComputeDeltaGCollapseSeverity is
            // still called, unconditionally, for BOTH channels, ONLY because: (1) it advances `lastG` (via
            // the `ref` parameter) so the collapse detector's own state stays coherent frame to frame, and
            // (2) it still populates `utilization`/`branchConfidence` - "is this car currently at its own
            // physical limit" and "is achieved deceleration currently falling" remain genuinely useful,
            // correct, STANDALONE diagnostics - even though NEITHER channel's own published severity is
            // decided by its return value any more (see the severity assignment just below).
            // Return value deliberately discarded (see the remarks above) - only the `ref`/`out` side
            // effects (lastG advancement, utilization/branchConfidence diagnostics) are still wanted.
            _ = ComputeDeltaGCollapseSeverity(
                physicalReference, gameId, carId, motion.MagnitudeG, smoothedLooseFraction, ref lastG,
                out utilization, out branchConfidence);

            // RELATIVE FALLBACK (docs\relative-fallback-and-raw-default-report.md - superseding the
            // absolute "source < 2.0" trigger, see this class's own history note at the top of the file
            // for the full derivation and measured before/after numbers). Layer 3's Raw, calibrated onto
            // the SAME canonical 0-100 band via the SAME KeyedScaleLearner (under the dedicated,
            // always-warm RawFallbackSourceIdentity key) - a fair, like-for-like comparison against
            // calibratedMean above, never a native-units one.
            double calibratedRawFallback = scaleLearner.Rescale(gameId, carId, RawFallbackSourceIdentity, calibrationBasisFallback);

            // Only ever a genuine signal to evaluate the disagreement against when Raw itself clears the
            // SAME "real, not noise/placeholder" bar this class already trusts elsewhere
            // (MinRawForCalibrationObservation) - otherwise there is nothing to compare the configured
            // source against this frame, so the smoothed divergence estimate is simply HELD (neither
            // built up nor decayed) rather than pulled toward a meaningless "no evidence" reading.
            bool rawSignalPresent = calibrationBasisFallback >= MinRawForCalibrationObservation;
            double instantDivergence = smoothedFallbackDivergence;
            if (rawSignalPresent)
            {
                // NATIVE-AGREEMENT GUARD (FH6 guardrail finding, docs\relative-fallback-and-raw-default-report.md):
                // when the configured source's OWN native reading already equals Layer 3's Raw this
                // frame (the common case when the driver's configured source genuinely IS Raw, or any
                // other source that happens to momentarily read the same), there is no real disagreement
                // to measure, full stop - regardless of what the CALIBRATED comparison below would say.
                // Without this guard, two independently-converging calibration ceilings (the configured
                // source's own vs the dedicated raw-fallback one) can drift apart by a small amount
                // purely from differing sample histories (the raw-fallback identity is fed every
                // qualifying frame regardless of configured source, while a real source's own key is only
                // fed while THAT source is actually configured) - measured directly against the FH6 log
                // referenced in the report: 1.39% of frames where the configured source was demonstrably
                // Raw itself still crossed the engagement threshold before this guard, purely from that
                // ceiling noise. A same-frame native match is a strictly stronger, always-correct signal
                // that no substitution is warranted, checked BEFORE trusting the calibrated ceilings.
                bool nativelyAgrees = Math.Abs(calibrationBasisConfigured - calibrationBasisFallback) <= Math.Max(NativeAgreementAbsoluteTolerance, calibrationBasisFallback * NativeAgreementRelativeTolerance);
                if (nativelyAgrees)
                {
                    instantDivergence = 0.0;
                }
                else
                {
                    double divergenceDenominator = Math.Max(calibratedRawFallback, MinRawForCalibrationObservation);
                    instantDivergence = ClampMath.To01((calibratedRawFallback - calibratedMean) / divergenceDenominator);
                }
            }

            // Sustained-evidence smoothing (same dt-correct mechanism as the surface-fraction blend
            // above) - a single divergent frame barely moves this; a sustained divergence across many
            // consecutive genuine-Raw frames (the measured F1 Generic signature) saturates it.
            smoothedFallbackDivergence = ExponentialSmoothTowardTarget(
                smoothedFallbackDivergence, instantDivergence, dtSeconds, FallbackDivergenceSmoothingTauSeconds);

            // Continuous blend weight, ramped between the two thresholds - "blend toward Raw
            // proportionally to the divergence" rather than a binary substitution, so severity and the
            // per-wheel proportions below move smoothly as the blend weight itself ramps, never stepping.
            double fallbackWeight = ComputeBlendWeight(smoothedFallbackDivergence);
            sourceFallbackActive = fallbackWeight > 0.0;

            // ---- THE ACTUAL SEVERITY ASSIGNMENT (docs\release-1060-report.md, Part 1).
            // LOCK: the calibrated SOURCE end to end - the four-range curve when its own three anchors are
            // valid (`lockAnchorLearner != null`, so `lockFourRangeSeverityConfigured`/`...Fallback` are
            // populated), else exactly 1.0.6.3's own single-anchor Rescale (`calibratedMean`/
            // `calibratedRawFallback`) - blended toward Layer 3's own Raw fallback by the SAME continuous
            // ShakeIt-silence weight as the per-wheel proportions below (never a native-units mismatch:
            // both sides are already on the canonical 0-100 scale, either via the curve or via Rescale).
            // SLIP: `lockAnchorLearner` is always null for Slip (Slip has NO S75/S90/four-range concept -
            // "only global SMax solution supported", per the owner), so `lockFourRangeSeverityConfigured`/
            // `...Fallback` are always null for Slip and this SAME expression reduces to exactly
            // `calibratedMean * (1-fallbackWeight) + calibratedRawFallback * fallbackWeight` - 1.0.6.3's
            // own Slip severity formula, verbatim, with no ΔG-collapse/band-mapping involved at all. One
            // expression, no channel branch, because the `??` operator already does the right thing for
            // both channels once Lock's own fields are the only ones ever populated.
            // NORMALIZE PATTERN (docs\release-1060-report.md, Part 2) - "Max-Grip Only" mode
            // (`useFourRangeForSeverity: false`) discards the four-range value here even when a valid
            // curve exists, falling through to `calibratedMean`/`calibratedRawFallback` (1.0.6.3's own
            // single-anchor Rescale) - the SAME formula Slip always uses, applied to Lock only in this
            // mode. S75/S90 keep learning/persisting either way (see the fallback/curve-building block
            // above) - only THIS read is gated.
            // Under Mapping the four-range curve drives severity; under Max-Grip-Only the two-range one
            // does. Both are the same kind of piecewise mapping, so neither mode falls back to the plain
            // rescale for its published severity any more.
            double? configuredForSeverity = useFourRangeForSeverity
                ? lockFourRangeSeverityConfigured
                : twoRangeSeverityConfigured;
            double? fallbackForSeverity = useFourRangeForSeverity ? lockFourRangeSeverityFallback : null;
            double severity = (configuredForSeverity ?? calibratedMean) * (1.0 - fallbackWeight)
                + (fallbackForSeverity ?? calibratedRawFallback) * fallbackWeight;

            // SLIP-ONLY RAW FLOOR (restores 1.0.3's own `Math.Max(..., mean)` guarantee for this channel).
            //
            // WHY SLIP NEEDS IT AND LOCK DOES NOT. The F1 25 fix removed the old
            // `Math.Max(effectiveGripUtilization, calibratedMean)` outright, and for LOCK that was
            // exactly right: the max let a session-immature learned G reference push severity to
            // "critical" well before the wheel was near its limit, and since wet braking achieves lower
            // g than dry at the same lock proximity, a shared g-based ceiling read the two conditions
            // differently. Every log behind that decision was a BRAKING log.
            //
            // Slip was collateral damage. The defect the max originally fixed (docs\field-fixes-report.md
            // defect B) is a WHEELSPIN phenomenon: achieved chassis acceleration DROPS once a driven
            // wheel spins up, so a model that reads severity from anything other than the source itself
            // reads a full-blown spin as LOW. Measured on the owner's own Raw-source log, Lock passes
            // through this layer intact (Raw p99 83.5 -> Normalized p99 83.6) while Slip loses nearly
            // half its magnitude (71.2 -> 40.0). This floor restores the one guarantee that prevents
            // that - the published value can never read BELOW the source's own calibration basis - and
            // does so WITHOUT reintroducing the g-based ceiling that caused the wet/dry problem, since
            // the floor is the source's own reading, not a learned G ratio.
            //
            // Deliberately named for the BEHAVIOUR, not the channel, and defaulted false: it is reachable
            // only from the Slip call site. ComputeChannel is `private static` and every learner arrives
            // as a per-channel instance, so Lock cannot observe this flag by any path.
            if (floorSeverityAtRawBasis) severity = Math.Max(severity, calibrationBasisConfigured);

            carLevelSeverity = severity;

            // Per-wheel proportions blended the SAME continuous weight, so a wheel's published share of
            // the car-level severity moves smoothly too, never jumping the instant the blend engages.
            double bw0 = w0 * (1.0 - fallbackWeight) + lw0 * fallbackWeight;
            double bw1 = w1 * (1.0 - fallbackWeight) + lw1 * fallbackWeight;
            double bw2 = w2 * (1.0 - fallbackWeight) + lw2 * fallbackWeight;
            double bw3 = w3 * (1.0 - fallbackWeight) + lw3 * fallbackWeight;
            double proportionMean = (bw0 + bw1 + bw2 + bw3) / 4.0;
            if (proportionMean <= NoRawSignalEpsilon)
            {
                // No per-wheel differentiation available from Raw at all - distribute the
                // car-level severity evenly rather than favouring an arbitrary wheel. allScale stays at
                // its neutral 1.0 default (there is nothing native to scale - severity IS the published
                // value here, uniformly, exactly as before this task). nativeAggregate is Aggregator.Compute
                // of four EQUAL (severity) inputs - since every weight pair in Aggregator sums to 1 (see
                // its own remarks), this reproduces `severity` for every one of Front/Rear/Left/Right/All,
                // so `nativeAggregate.X * 1.0` matches this branch's own uniform-severity Corners exactly.
                nativeAggregate = Aggregator.Compute(new Corners(severity, severity, severity, severity), weights);
                return new Corners(
                    ClampMath.To0100(severity), ClampMath.To0100(severity),
                    ClampMath.To0100(severity), ClampMath.To0100(severity));
            }

            if (!useAggregatedAllScale)
            {
                // PRE-OBJECTIVE-A BEHAVIOUR (see LockUsesAggregatedAllScale/SlipUsesAggregatedAllScale's
                // own remarks) - the flat-mean-relative proportion, UNCHANGED: severity * (bw_i /
                // flatMean(bw)). Per-wheel formula kept VERBATIM (same operations, same order) so no
                // floating-point behaviour changes for this channel's own per-wheel output.
                //
                // MID-CHAIN CLAMP FIX (docs\clamp-chain-fix-report.md) - THIS is Slip's own actual scaling
                // path (SlipUsesAggregatedAllScale stays a compile-time `const false`, unchanged - see
                // that constant's own remarks; this fix does not touch it or Slip's severity formula
                // above). Algebraically, `severity * s_i == severity * (bw_i / proportionMean) ==
                // bw_i * (severity / proportionMean)` - i.e. this branch DOES apply one uniform multiplier
                // to all four wheels, it was just never surfaced or reused for the group channels before.
                // `allScale` now carries that SAME multiplier (`severity / proportionMean`) so
                // NormalizedWheelLockSlipEngine.Compute can build Front/Rear/Left/Right/All the identical
                // native-aggregate-times-scale-then-clamp-once way Lock's own path does below - closing,
                // for Slip, the gap the brief's own analysis found on EVERY published Slip field (Slip
                // never had Lock's `:1041`-style bypass, so this was previously unprotected everywhere,
                // not just on Front/Rear/Left/Right). `nativeAggregate` is Aggregator.Compute of the
                // NATIVE (pre-scale) bw0..bw3 - the same per-wheel values this formula already scales -
                // so `nativeAggregate.X * allScale` is the exact aggregate-then-scale reordering of what
                // re-aggregating the (old, clamped) per-wheel output used to approximate lossily.
                double s0 = bw0 / proportionMean, s1 = bw1 / proportionMean, s2 = bw2 / proportionMean, s3 = bw3 / proportionMean;
                allScale = ClampMath.SafeDiv(severity, proportionMean, 1.0);
                nativeAggregate = Aggregator.Compute(new Corners(bw0, bw1, bw2, bw3), weights);
                return new Corners(
                    ClampMath.To0100(severity * s0), ClampMath.To0100(severity * s1),
                    ClampMath.To0100(severity * s2), ClampMath.To0100(severity * s3));
            }

            // ---- OBJECTIVE A - AGGREGATED-ALL SCALE (see LockUsesAggregatedAllScale's own remarks for
            // the full derivation) - ONE uniform multiplier, derived from the SAME aggregation weights
            // that will re-combine the published per-wheel output, applied identically to all four
            // blended native readings so that re-aggregating reproduces `severity` exactly at the All
            // level (mod the 0-100 clamp at extreme scales - see the MID-CHAIN CLAMP FIX in Compute(),
            // which now removes that clamp for every one of Front/Rear/Left/Right/All, not All alone).
            // DEFECT B PARITY (docs\v1068-four-range-report.md): blended EXACTLY like 1.0.6.3's own
            // ComputeChannel - aggregatedNativeConfigured/aggregatedNativeFallback computed separately
            // (from the RAW, un-blended w/lw wheels, before the per-wheel fallback blend), THEN blended
            // by fallbackWeight - not Aggregator.Compute(bw0..bw3) computed AFTER the per-wheel blend
            // (1.0.6.8's own prior structure), since Aggregator's Max/Min stages are non-linear and the
            // two orders are not generally equal once fallbackWeight is strictly between 0 and 1.
            double blendedNativeAggregate = aggregatedNativeConfigured * (1.0 - fallbackWeight) + aggregatedNativeFallback * fallbackWeight;
            allScale = ClampMath.SafeDiv(severity, blendedNativeAggregate, 1.0);

            // MID-CHAIN CLAMP FIX (docs\clamp-chain-fix-report.md) - Front/Rear/Left/Right computed the
            // SAME "aggregate configured/fallback separately, THEN blend by fallbackWeight" way as All
            // (`blendedNativeAggregate` above) - see Fix 1's own blueprint
            // (QAdvanceFeedback_analysis_shakeit_overshake\shakeit-overshake-analysis.md, Section 7) for
            // why this order (not aggregating the already-per-wheel-blended bw0..bw3) is the one that
            // stays consistent with how `blendedNativeAggregate`/`allScale`'s own denominator is derived.
            // These are NEVER clamped here - only once, in Compute(), after being multiplied by allScale.
            double blendedNativeFront = nativeAggConfiguredFull.Front * (1.0 - fallbackWeight) + nativeAggFallbackFull.Front * fallbackWeight;
            double blendedNativeRear = nativeAggConfiguredFull.Rear * (1.0 - fallbackWeight) + nativeAggFallbackFull.Rear * fallbackWeight;
            double blendedNativeLeft = nativeAggConfiguredFull.Left * (1.0 - fallbackWeight) + nativeAggFallbackFull.Left * fallbackWeight;
            double blendedNativeRight = nativeAggConfiguredFull.Right * (1.0 - fallbackWeight) + nativeAggFallbackFull.Right * fallbackWeight;
            nativeAggregate = new WheelAggregate(blendedNativeFront, blendedNativeRear, blendedNativeLeft, blendedNativeRight, blendedNativeAggregate);

            return new Corners(
                ClampMath.To0100(bw0 * allScale),
                ClampMath.To0100(bw1 * allScale),
                ClampMath.To0100(bw2 * allScale),
                ClampMath.To0100(bw3 * allScale));
        }

        /// <summary>
        /// FEATURE C (docs\v1068-four-range-report.md) - WHEELLOCK ONLY. Builds the four-range,
        /// source-space mapping (0,0)-&gt;(s75,30)-&gt;(s90,60)-&gt;(smax,80)-&gt;(100,100) via the SAME
        /// monotone-cubic machinery <see cref="BandCurve"/> already uses (mainly linear within each
        /// range, smoothed at the joins - the owner's own "wheel slip ratio should be close to linear
        /// within each range" rationale - WITHOUT the settings UI's separate flatten-range feature,
        /// which this class never wires in). Returns false (leaving the caller on its existing
        /// `calibratedMean`/`calibratedRawFallback` fallback - 1.0.6.3's OWN single-anchor Rescale formula,
        /// which already guarantees the two endpoints that matter, source==Smax -&gt; 80 and source==100 -&gt;
        /// (at least) 100 clamped) whenever the three learned anchors cannot form a sane, strictly-
        /// increasing knot sequence - non-finite, non-positive, out of order, or too close together (closer
        /// than <see cref="MinRangeGapNative"/>) to leave <see cref="MonotoneCubicCurve"/> (which REQUIRES
        /// strictly increasing x - see its own constructor) a numerically sane interval to interpolate
        /// across. This is a deliberate ABSTAIN, not a guess - exactly this codebase's
        /// standing "prefer under-reporting/no-op to a wild guess" convention (mirrors
        /// <see cref="KeyedScaleLearner.Rescale"/>'s own cold-start identity return).
        /// </summary>
        private const double MinRangeGapNative = 0.5;

        /// <summary>
        /// S90/S75 FALLBACK RATIOS (docs\release-1060-report.md, Part 3 - the owner's measured "S90 feels
        /// a little early" fix). Before <see cref="LockAnchorLearner"/> has learned a REAL S75/S90
        /// crossing for a key, this codebase used to fall all the way back to <c>calibratedMean</c> -
        /// 1.0.6.3's plain single-anchor Rescale, which maps source==Smax to exactly 80 linearly. That
        /// IMPLIES an S90 sitting at exactly <c>60/80*Smax = 0.750*Smax</c> and an S75 at
        /// <c>30/80*Smax = 0.375*Smax</c>, even though neither was ever actually learned - a silent,
        /// un-inspectable default the owner measured to read early: output 60 landed at only ~80.6% of
        /// achieved deceleration against a 90% target, and output 30 at ~74.4% against a 75% target.
        /// <para/>
        /// THE FIX: while no real anchor is learned yet, build the four-range curve anyway using these
        /// EXPLICIT fallback ratios instead of silently degrading to the plain linear Rescale. Per
        /// slip-ratio reasoning (docs\s75-s90-slipratio-and-fit-report.md's own measured curve), roughly
        /// 40% of Smax yields 75-80% of deceleration capability and roughly 84% yields 93-97% - so
        /// <see cref="S75FallbackRatioOfSmax"/> moves from the implied 0.375 up to 0.40, and
        /// <see cref="S90FallbackRatioOfSmax"/> moves from the implied 0.750 up by the owner's own
        /// explicit 1.125 factor to <c>0.750*1.125 = 0.84375</c>. Erring slightly HIGH is deliberate, not
        /// a rounding artifact: S75/S90 are BOUNDARIES ("powerful braking starts here", "best braking
        /// starts here"), so reading slightly ABOVE a boundary is better feedback for the driver than
        /// reading slightly below it.
        /// <para/>
        /// SCOPE: consulted ONLY at the two read sites in <see cref="ComputeChannel"/> that ask
        /// <see cref="LockAnchorLearner.LearnedS75"/>/<see cref="LockAnchorLearner.LearnedS90"/> for a
        /// value to build a curve from THIS frame - via `?? RatioOfSmaxFallback(...)`, so the moment a
        /// real learned anchor exists (non-null), it is used and this fallback is never reached again for
        /// that key. Nothing is written back into <see cref="LockAnchorLearner"/>'s own persisted state -
        /// SMax/S75/S90 keep learning exactly as before, completely independent of whether this fallback
        /// is currently substituting for a not-yet-learned S75/S90.
        /// </summary>
        private const double S90FallbackRatioOfSmax = 0.750 * 1.125; // = 0.84375
        private const double S75FallbackRatioOfSmax = 0.40;

        /// <summary>
        /// S75 and S90 as fractions of SMax, MEASURED from the deceleration curve using the same
        /// corner-local detector that defines SMax itself (docs\cross-channel-smax-report.md).
        /// <para/>
        /// HOW THEY WERE OBTAINED. For each braking event the detector identifies the limit frame - the
        /// frame of peak collapse confidence - giving that corner its own <c>G_limit</c> and
        /// <c>S_limit</c>. Walking BACK through the same corner for the last rising crossing of
        /// 0.75 x G_limit and 0.90 x G_limit and reading the source there gives, on the owner's own
        /// four-session capture:
        /// <code>
        ///   S75/S_limit:  0.50  0.49  0.44  0.51     -> 0.49
        ///   S90/S_limit:  0.70  0.78  0.61  0.80     -> 0.72
        /// </code>
        /// across two cars and two sources. The underlying relationship is genuinely monotone - median
        /// source per grip band, measured against each corner's OWN detector-identified limit rather than
        /// a session-wide or speed-binned peak, rises in every one of the four sessions. (An earlier pass
        /// measured this against a speed-binned peak, which mixes corners together and made the
        /// relationship look flat; that was a measurement error, not a property of the data.)
        /// <para/>
        /// WHY RATIOS AND NOT ABSOLUTE VALUES. The ratios are dimensionless and stable; the absolute
        /// crossings are not, because <c>S_limit</c> is a single frame per corner while SMax is a high
        /// percentile of the pooled confidence-weighted distribution - a deliberately more conservative
        /// statistic of the same physical event. Applying the measured ratios to SMax keeps all three
        /// knots on ONE basis and preserves the SHAPE the deceleration curve actually has. It follows
        /// that S90 here is not literally "the source at 90% grip in this corner" - it is that
        /// relationship expressed against the ceiling the channel actually publishes against.
        /// </summary>
        /// <para/>
        /// SEED ONLY, NOT THE ANSWER. These are where a cold key STARTS; once a key has seen enough
        /// corroborating corners it publishes its OWN learned ratios instead
        /// (LockAnchorLearner.LearnedPhysicalS75Ratio/S90Ratio). Rounded to 0.75/0.50 at the owner's
        /// request - both sit inside the measured spread (S90 0.61-0.80, S75 0.44-0.51) and a rounder
        /// reference is easier to reason about when a key is still cold.
        private const double PhysicalS90SeedRatio = 0.75;

        /// <summary>S75's counterpart to <see cref="PhysicalS90SeedRatio"/> - see its remarks.</summary>
        private const double PhysicalS75SeedRatio = 0.50;

        /// <summary>
        /// Percentile used ONLY to ask "does this key have usable physically-anchored evidence yet".
        /// <see cref="KeyedScaleLearner.PhysicalAnchorLevel"/> returns null below the minimum sample bar,
        /// which is the actual question here; the percentile chosen does not affect the answer, and the
        /// VALUE is deliberately unused - the anchors come from the measured ratios, not from this read.
        /// </summary>
        private const double PhysicalAnchorReadyProbePercentile = 50.0;

        private static double? RatioOfSmaxFallback(double? smax, double ratio)
            => smax.HasValue && ClampMath.IsFinite(smax.Value) && smax.Value > 0.0 ? smax.Value * ratio : (double?)null;

        /// <summary>
        /// Tries <paramref name="learner"/>'s own REAL learned S75/S90 for this key first; if that fails
        /// to produce a valid, buildable curve for ANY reason (no anchor learned yet, or a real anchor
        /// that exists but is not yet usable), retries with the S75/S90 FALLBACK RATIOS instead (see
        /// S90FallbackRatioOfSmax/S75FallbackRatioOfSmax's own remarks) - so a given key's curve never
        /// silently oscillates between "the four-range curve" and "plain calibratedMean" from one query
        /// to the next with no intervening real evidence (see docs\release-1060-report.md, Part 3).
        /// </summary>
        private static bool TryBuildLockRangeCurveWithFallback(
            LockAnchorLearner learner, string gameId, string carId, string sourceIdentity, double? smax,
            KeyedScaleLearner scaleLearner, out MonotoneCubicCurve curve)
        {
            curve = null;
            if (!smax.HasValue) return false;

            // PHYSICALLY-ANCHORED ANCHORS, PREFERRED (docs\cross-channel-smax-report.md).
            //
            // WHY THE LEGACY ANCHORS BELOW ARE NO LONGER FIRST. LockAnchorLearner measures "the source
            // value where deceleration reaches 75%/90% of this corner's own max-grip G" - a crossing on
            // the RISING branch of the slip curve, i.e. BEFORE the tyre reaches its limit. SMax now sits
            // at the limit itself (the corner-local collapse detector). Those are opposite sides of the
            // curve's peak, so no amount of tuning reconciles them: measured on the owner's own 1.7.1
            // capture the legacy anchors land at 17-36 against an SMax of 72-77, which builds a curve
            // whose first knot is at a quarter of the range and makes ordinary braking feel like a lock.
            //
            // Both anchors are now MEASURED OFF THE DECELERATION CURVE, using the same corner-local
            // detector that defines SMax - see PhysicalS90RatioOfSmax's own remarks for the derivation
            // and the measured per-session numbers. All three knots therefore describe one physical
            // event on one basis, which is the property that was missing.
            //
            // An earlier revision of this placed S90 at a percentile of the at-limit distribution and S75
            // at the collapse onset. Both were stable, but neither was obtained from the physics the way
            // SMax is - one was a statistic of the distribution, the other a fixed onset fraction - and
            // together they built a badly shaped curve (knots at 15 / 70 / 75, so output reached 60 while
            // the source was still in the teens and then crawled).
            // THIS KEY'S OWN LEARNED RATIOS FIRST, the shipped ones only as the seed. The learned pair
            // comes from real crossings of 0.75x/0.90x each completed corner's own limit G
            // (LockAnchorLearner.ObserveCornerFrame/ResetRun); the constants below are the measured
            // cross-session medians, used so a cold key starts from a good reference rather than from
            // nothing and so a key that has not yet seen enough corroborating corners never publishes a
            // one-corner outlier.
            if (scaleLearner != null && scaleLearner.PhysicalAnchorLevel(gameId, carId, sourceIdentity, PhysicalAnchorReadyProbePercentile).HasValue)
            {
                double s75Ratio = learner.PhysicalS75Ratio(gameId, carId, sourceIdentity, PhysicalS75SeedRatio);
                double s90Ratio = learner.PhysicalS90Ratio(gameId, carId, sourceIdentity, PhysicalS90SeedRatio);
                if (TryBuildLockRangeCurve(smax.Value * s75Ratio, smax.Value * s90Ratio, smax.Value, out curve))
                    return true;
            }

            // LEGACY CROSSING ANCHORS - retained so a key restored from persisted state, or one whose
            // at-limit distribution is not yet ready, still produces a curve rather than dropping to
            // plain Rescale. Superseded as the primary source, not deleted: LockAnchorLearner keeps
            // learning and persisting exactly as before (see its own remarks and the Max-Grip Only
            // pattern's contract, both of which are unchanged by this).
            double? s75 = learner.LearnedS75(gameId, carId, sourceIdentity);
            double? s90 = learner.LearnedS90(gameId, carId, sourceIdentity);
            if (s75.HasValue && s90.HasValue && TryBuildLockRangeCurve(s75.Value, s90.Value, smax.Value, out curve))
                return true;

            double? s75Fallback = RatioOfSmaxFallback(smax, S75FallbackRatioOfSmax);
            double? s90Fallback = RatioOfSmaxFallback(smax, S90FallbackRatioOfSmax);
            return s75Fallback.HasValue && s90Fallback.HasValue
                && TryBuildLockRangeCurve(s75Fallback.Value, s90Fallback.Value, smax.Value, out curve);
        }

        /// <summary>
        /// Max-Grip-Only's own mapping: the SAME piecewise construction the four-range curve uses, with
        /// two segments instead of four - <c>{0, SMax, 100} -> {0, 80, 100}</c>.
        /// <para/>
        /// WHY NOT A PLAIN RESCALE. Max-Grip-Only used to publish <c>basis * 80 / SMax</c>, a single
        /// straight line through the SMax anchor. That anchors the middle correctly but gets both ends
        /// wrong, because nothing pins the top: with SMax 85 a full-scale source reads 94 and can never
        /// reach 100, and with SMax 70 everything above 87.5 saturates at 100 and the top eighth of the
        /// source range is indistinguishable. Pinning 100 -> 100 as a second segment fixes both ends
        /// while leaving the SMax anchor exactly where it was, and makes this mode consistent with the
        /// mapping mode rather than a different kind of thing.
        /// </summary>
        /// <summary>Fractions of SMax the two lower anchors sit at when they are DERIVED rather than
        /// measured. Deliberately the same numbers the settings page derives with, so the boxes and the
        /// published mapping never disagree.</summary>
        private const double DerivedS90FractionOfSMax = 0.90;
        private const double DerivedS75FractionOfSMax = 0.70;

        /// <summary>The four-range curve with its two lower anchors derived from SMax - see
        /// <see cref="DerivedS90FractionOfSMax"/>.</summary>
        private static bool TryBuildDerivedRangeCurve(double smax, out MonotoneCubicCurve curve)
            => TryBuildLockRangeCurve(smax * DerivedS75FractionOfSMax, smax * DerivedS90FractionOfSMax, smax, out curve);

        private static bool TryBuildTwoRangeCurve(double smax, out MonotoneCubicCurve curve)
        {
            curve = null;
            if (!ClampMath.IsFinite(smax)) return false;
            if (smax < MinRangeGapNative) return false;
            if (smax > 100.0 - MinRangeGapNative) return false;

            curve = new MonotoneCubicCurve(
                new[] { 0.0, smax, 100.0 },
                new[] { 0.0, CanonicalNormalizedAtMaxGrip, 100.0 });
            return true;
        }

        /// <summary>The normalized value the max-grip anchor maps to - the shared 80 both range curves
        /// are built around, and the same number <see cref="KeyedScaleLearner.CanonicalAtLimitAnchor"/>
        /// rescales toward.</summary>
        private const double CanonicalNormalizedAtMaxGrip = 80.0;

        private static bool TryBuildLockRangeCurve(double s75, double s90, double smax, out MonotoneCubicCurve curve)
        {
            curve = null;
            if (!ClampMath.IsFinite(s75) || !ClampMath.IsFinite(s90) || !ClampMath.IsFinite(smax)) return false;
            if (s75 < MinRangeGapNative) return false;
            if (s90 < s75 + MinRangeGapNative) return false;
            if (smax < s90 + MinRangeGapNative) return false;
            if (smax > 100.0 - MinRangeGapNative) return false;

            curve = new MonotoneCubicCurve(new[] { 0.0, s75, s90, smax, 100.0 }, new[] { 0.0, 30.0, 60.0, 80.0, 100.0 });
            return true;
        }

        /// <summary>
        /// DELTA-G COLLAPSE BAND MAPPING (docs\delta-g-band-mapping-report.md) - the car-level "Normalized"
        /// level for THIS frame, per the owner's own final specification:
        /// <list type="bullet">
        /// <item>30 = consistently achieving 75% of the maximum effort available.</item>
        /// <item>60 = consistently achieving 90%.</item>
        /// <item>80 = the maximum effort available (physical grip limit).</item>
        /// <item>100 = fully locked/spun, delivering LESS effort than the maximum (ΔG genuinely falling).</item>
        /// </list>
        /// <paramref name="physicalReference"/> is the SAME shared, (game,car)-only, surface-blended
        /// physical-limit detector <c>physicalRatioNow</c> above already reads - re-used here rather than
        /// adding a second reference, per this class's own standing "one physical-limit notion" precedent.
        /// Uses the SHIPPED evidence-weighted adaptive peak estimator (<see cref="GripLearner"/>) as the
        /// denominator, NOT a P99/percentile reference - see docs\adaptive-peak-learner-report.md for why
        /// P99 was evaluated and rejected (cannot ramp down; a windowed one reintroduces the "did not try
        /// vs tried and could not" ambiguity this project's own estimator already solves).
        /// </summary>
        /// <param name="lastG">The previous QUALIFYING frame's own achieved |g| for THIS channel - null
        /// when there is no genuine previous frame to diff against (see <see cref="_lockLastG"/>'s own
        /// remarks). Updated to <paramref name="magnitudeG"/> on every call - a plain "session-scoped
        /// previous value" ref, exactly like <paramref name="smoothedLooseFraction"/>/
        /// <paramref name="smoothedFallbackDivergence"/> elsewhere in this method.</param>
        private static double ComputeDeltaGCollapseSeverity(
            KeyedGripLearner physicalReference, string gameId, string carId,
            double magnitudeG, double smoothedLooseFraction, ref double? lastG,
            out double utilization, out double branchConfidence)
        {
            // u = g / SpeedAwarePeakG(v), surface-blended exactly like physicalRatioNow above, but WITH
            // the cold-start ceiling applied (applyColdStartCeiling: true) - the owner's own "under-report
            // rather than over-report while cold" requirement, satisfied by reusing GripLearner.Ratio's
            // OWN existing ColdStartCeilingRatio(0.75)/MaturitySamples(200) contract verbatim (continuous,
            // no step at any sample count - see that method's own remarks) rather than adding a second,
            // differently-shaped confidence gate.
            // v1.0.6.9 REWORK (docs\v1068-rework-report.md, Goal 2) - useStabilityGatedCeiling: false. See
            // GripLearner.Ratio's own remarks for the full derivation: gating THIS live-severity ceiling on
            // MaturityConfidence's own settledness requirement measurably never lifts within a realistic
            // session on real logs ("the Lock motor not shaking at all"). The plain, sample-count-only
            // Confidence still protects the very first few observations from over-trusting a brand-new
            // seed (unchanged - see the "never over-reporting while cold" tests); it simply does not ALSO
            // require the reference to have stopped moving before granting full trust. Device-feel
            // protection against a still-uncertain reference now lives at Layer 5 (ColdStartScale) - see
            // LockColdStartConfidence/SlipColdStartConfidence below.
            //
            // v1.0.6.9 REWORK (docs\v1068-rework-report.md, Goal 3) - speedKmh deliberately NOT passed
            // here any more (was `speedKmh: speedKmh`). The owner's own precise, narrow scoping: speed-
            // aware modelling IS required (elsewhere, offline/validation-only - see
            // docs\v1068-rework-report.md's own G-ratio verification) to identify/learn what fraction of
            // available grip 30/60 represent AT A GIVEN SPEED (available grip varies strongly with speed
            // in F1), but it must NEVER leak into what gets PUBLISHED - "projecting source data into
            // Normalized output... must depend ONLY on the learned scale information, with no speed
            // term." `u` here therefore always divides by the FLAT, surface-blended
            // <see cref="GripLearner.PublishedPeakG"/> (via `Ratio`'s own speedKmh-omitted overload),
            // never <see cref="GripLearner.SpeedAwarePeakG"/>. The speed-bucketed model itself keeps
            // learning regardless (<see cref="GripLearner.Observe"/> above is UNCHANGED, still fed
            // speedKmh every qualifying frame) - only the LIVE query stopped reading it.
            double uSealed = physicalReference.Ratio(gameId, carId, magnitudeG, PhysicalReferenceSourceIdentity, SealedSurfaceBucket, applyColdStartCeiling: true, useStabilityGatedCeiling: false);
            double uLoose = physicalReference.Ratio(gameId, carId, magnitudeG, PhysicalReferenceSourceIdentity, LooseSurfaceBucket, applyColdStartCeiling: true, useStabilityGatedCeiling: false);
            double u = Blend(uSealed, uLoose, smoothedLooseFraction);

            double dGScale = ResolveCollapseDGScale(physicalReference, gameId, carId, smoothedLooseFraction);

            // COLLAPSE: only meaningful within the SAME qualifying run - a null lastG (fresh run, or right
            // after a quiet/reset gap) means "nothing to compare against yet", so collapse reads 0 (never a
            // spurious comparison across a gap - see _lockLastG/_slipLastG's own remarks).
            // COLLAPSE HYSTERESIS - INVESTIGATED AND REJECTED (docs\delta-g-band-mapping-report.md): a
            // short EMA on this raw collapse term was tried live, measured directly against the real
            // logs, and made the frame-to-frame flicker WORSE, not better (e.g. Slip's fraction of
            // boundary crossings jumping >15 published points rose from 16.8-37.5% to 49.1-84.3%, and
            // car-level time-above-80 roughly doubled) - because EMA-smoothing a signal that is mostly
            // zero with occasional brief spikes does not smooth the TRANSITION cleanly, it SPREADS each
            // spike's decay tail across many more subsequent frames, each of which can independently
            // land near the u=0.80 gate boundary while u itself is still moving - more opportunities for
            // a visible jump, not fewer. Reverted; the raw, unsmoothed collapse below is what ships - see
            // Concerns for the honest disposition of this investigation (median jump size is already
            // small; the residual tail traces to genuinely fast per-frame ΔG in the real telemetry, not a
            // discontinuity in this formula, which is provably continuous in u for any fixed collapse).
            double collapse = 0.0;
            if (lastG.HasValue)
            {
                double deltaG = magnitudeG - lastG.Value;
                collapse = ClampMath.To01(ClampMath.SafeDiv(-deltaG, dGScale, 0.0));
            }
            lastG = magnitudeG;

            double gate = ClampMath.To01(ClampMath.SafeDiv(u - BandGateStart, BandGateWidth, 0.0));
            double b = gate * collapse;
            utilization = u;
            branchConfidence = b;

            // v1068 CORRECTION (docs\v1068-four-range-report.md) - this method's own RETURN VALUE
            // ("severity") is now used verbatim only for SLIP (untouched by this task) - Lock's own live
            // severity is computed separately, from the calibrated SOURCE end to end (see ComputeChannel's
            // own "THE ACTUAL SEVERITY ASSIGNMENT" remarks), specifically because gating the 80-100 region
            // on this method's own `b` (collapse confidence) under-reported a genuine full lock whenever
            // the car's own G telemetry did not show a sharp, sustained collapse - measured directly against
            // 1.0.6.3 to fall 13-14 points short of a genuine full-lock reading. `utilization`/
            // `branchConfidence` (u/b, below) remain genuinely useful, correct diagnostics for Lock too
            // (exposed as `LockUtilization`/`LockBranchConfidence`) - only the ability to GATE Lock's own
            // published severity was removed.
            double rising = BandCurve.Evaluate(ClampMath.To01(u));
            double falling = FullLockOutputBase + FullLockOutputSpan * b;

            // Plain convex combination - NOT Math.Max(rising, falling) or any other floor. Both terms can
            // independently fall (rising falls whenever u falls; falling falls whenever b eases, i.e.
            // whenever the collapse itself abates or utilization drops back out of the gate) - see the
            // class-level DELTA-G COLLAPSE BAND MAPPING note for why this is the load-bearing property
            // that let this design succeed where the two prior Math.Max-floored attempts did not.
            return (1.0 - b) * rising + b * falling;
        }

        /// <summary>The ΔG collapse detector's own scale (g) - <see cref="CollapseDGScaleFraction"/> of
        /// this (game,car)'s own flat, surface-blended published peak (the SAME shipped adaptive
        /// estimator <see cref="ComputeDeltaGCollapseSeverity"/>'s own denominator already reads), falling
        /// back to <see cref="CollapseDGScaleColdFallbackG"/> while this key is still too cold (fewer than
        /// <see cref="CollapseDGScaleMinSamples"/> ever-qualifying samples) for that peak to mean
        /// anything yet.</summary>
        private static double ResolveCollapseDGScale(
            KeyedGripLearner physicalReference, string gameId, string carId, double smoothedLooseFraction)
        {
            int sealedSamples = physicalReference.Samples(gameId, carId, PhysicalReferenceSourceIdentity, SealedSurfaceBucket);
            int looseSamples = physicalReference.Samples(gameId, carId, PhysicalReferenceSourceIdentity, LooseSurfaceBucket);
            double blendedSamples = Blend(sealedSamples, looseSamples, smoothedLooseFraction);
            if (blendedSamples < CollapseDGScaleMinSamples) return CollapseDGScaleColdFallbackG;

            double sealedPeak = physicalReference.PublishedPeakG(gameId, carId, PhysicalReferenceSourceIdentity, SealedSurfaceBucket);
            double loosePeak = physicalReference.PublishedPeakG(gameId, carId, PhysicalReferenceSourceIdentity, LooseSurfaceBucket);
            double peak = Blend(sealedPeak, loosePeak, smoothedLooseFraction);

            double scale = CollapseDGScaleFraction * peak;
            return scale > 0.0 && ClampMath.IsFinite(scale) ? scale : CollapseDGScaleColdFallbackG;
        }

        /// <summary>
        /// CORNER-LOCAL AT-LIMIT GATE (docs\speed-aware-reference-fix-report.md) - a continuous 0..1
        /// confidence that THIS frame is physically at the grip limit RIGHT NOW, used ONLY to decide
        /// whether this frame is genuine evidence for the speed-bucketed reference model (see
        /// <see cref="GripLearner.Observe"/>'s own <c>atLimitWeight</c> remarks) - ported/adapted from
        /// <c>QAdvanceFeedback_1.0.6.9_anchor_sampling</c>'s own method of the same name (built there for a
        /// different call site, gating <see cref="KeyedScaleLearner.ObserveAtPhysicalLimit"/> - the
        /// underlying detector is reused here for a new purpose, not reinvented). Two continuous terms,
        /// multiplied (the same "one confidence, multiplied" idiom <see cref="ComputeDeltaGCollapseSeverity"/>'s
        /// own <c>gate*collapse</c> already uses), plus a coarse plausibility floor:
        /// <list type="number">
        /// <item><b>G is plateauing or falling</b> - reuses the EXACT <c>collapse</c> shape
        /// (<c>clamp(-ΔG/dGScale, 0, 1)</c>) <see cref="ComputeDeltaGCollapseSeverity"/> already computes,
        /// complemented so a flat-or-falling ΔG (a genuine plateau, not merely already falling) reads FULL
        /// confidence, ramping down only as G is still climbing meaningfully.</item>
        /// <item><b>Effort is sustained or increasing</b> - THIS frame's own calibration basis (the raw
        /// source reading itself, never pedal state - see this class's own standing "pedal state is never
        /// the answer" rule) is not falling. Without this term, a driver simply EASING off the brake (G
        /// falls because effort dropped, not because the tyre hit its limit) would look identical to a
        /// genuine lockup to term 1 alone, and would teach the speed-bucketed reference from a source
        /// reading that is itself falling away from the true limit.</item>
        /// <item><b>G's own magnitude is at least PLAUSIBLE</b> - a coarse sanity floor, reusing the
        /// ALREADY-COMPUTED <paramref name="physicalRatioNowForPlausibility"/> (the FLAT, non-speed-aware
        /// session-wide ratio - never <see cref="GripLearner.SpeedAwarePeakG"/>, avoiding circularity with
        /// the very reference this gate feeds) at a MUCH LOWER, purely-implausibility-rejecting band
        /// (<see cref="AtLimitPlausibilityRatioFloor"/>-<see cref="AtLimitPlausibilityRatioCeiling"/>, ~10%
        /// to 40% of the flat reference) - this term exists only to stop an isolated, large ΔG drop from an
        /// ARTIFACT/transition frame (e.g. G falling because the braking zone simply ENDED) from
        /// registering full confidence purely because it happens to follow a much higher previous reading.</item>
        /// </list>
        /// <b>Cold start:</b> the CALLER returns 0.0 (identity - no teaching this frame) without even
        /// calling this method whenever this channel has no immediately preceding qualifying frame to diff
        /// against yet - "under-report rather than over-report while cold", satisfied structurally rather
        /// than by a special-cased branch inside the formula itself.
        /// </summary>
        private static double ComputeCornerAtLimitConfidence(
            KeyedGripLearner physicalReference, string gameId, string carId,
            double magnitudeG, double calibrationBasisConfigured, double smoothedLooseFraction,
            double lastG, double lastBasis, double physicalRatioNowForPlausibility)
        {
            double dGScale = ResolveCollapseDGScale(physicalReference, gameId, carId, smoothedLooseFraction);
            double deltaG = magnitudeG - lastG;
            double gStillRising = ClampMath.To01(ClampMath.SafeDiv(deltaG, dGScale, 0.0));
            double gPlateauOrFalling = 1.0 - gStillRising;

            double basisScale = Math.Max(AtLimitEffortDropFloorNative, AtLimitEffortDropToleranceFraction * calibrationBasisConfigured);
            double deltaBasis = calibrationBasisConfigured - lastBasis;
            double effortDropping = ClampMath.To01(ClampMath.SafeDiv(-deltaBasis, basisScale, 0.0));
            double effortSustained = 1.0 - effortDropping;

            double gPlausibleMagnitude = ClampMath.To01(ClampMath.SafeDiv(
                physicalRatioNowForPlausibility - AtLimitPlausibilityRatioFloor,
                AtLimitPlausibilityRatioCeiling - AtLimitPlausibilityRatioFloor, 0.0));

            return gPlateauOrFalling * effortSustained * gPlausibleMagnitude;
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

        /// <summary>RELATIVE FALLBACK (see this class's own history note) - maps a smoothed divergence
        /// reading to a continuous 0-1 blend weight: 0 at/below <see cref="FallbackDivergenceEngageThreshold"/>
        /// (the configured source is trusted fully), linearly ramping to 1 at/above
        /// <see cref="FallbackDivergenceFullThreshold"/> (Layer 3's own calibrated Raw fully replaces it) -
        /// the graceful-transition mechanism the task's own brief asked for, in place of a hard
        /// threshold switch.</summary>
        private static double ComputeBlendWeight(double smoothedDivergence)
        {
            if (smoothedDivergence <= FallbackDivergenceEngageThreshold) return 0.0;
            if (smoothedDivergence >= FallbackDivergenceFullThreshold) return 1.0;
            return (smoothedDivergence - FallbackDivergenceEngageThreshold)
                / (FallbackDivergenceFullThreshold - FallbackDivergenceEngageThreshold);
        }
    }
}

using System;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// LAYER 5 COLD-START DEVICE-FEEL SCALE (v1.0.6.9 rework, Goal 2 - docs\v1068-rework-report.md,
    /// ported verbatim from <c>QAdvanceFeedback_1.0.6.2_prerelease\QAdvanceFeedback\Core\Projection\ColdStartScale.cs</c>,
    /// the owner's own design, their own name "isStarting"): while a channel's own max-grip learner is
    /// still cold, the FINAL PROJECTED output (never Normalized - Normalized must keep its physical
    /// meaning; this is a device-feel concern, which per this project's own layer rule belongs here, in
    /// Layer 5) is scaled down by a continuous, monotone factor that starts at <see cref="MinSafeFloor"/>
    /// (50%) at zero evidence and rises to EXACTLY 100% once the channel's own learner reaches
    /// <see cref="Normalized.GripLearner.MaturitySamples"/> (200) qualifying observations.
    /// <para/>
    /// WHY THIS, NOT A CLAMP/GATE: the 1.0.6.8 "stability-confidence gate" tried to solve this SAME
    /// cold-start-over-trigger problem by CLAMPING the live Normalized/severity output itself (gating its
    /// cold-start ceiling on <see cref="Normalized.GripLearner.MaturityConfidence"/>, which requires the
    /// reference to have gone quiet for <see cref="Normalized.GripLearner.StabilityScaleSamples"/>
    /// consecutive qualifying observations) - and it failed badly, measured directly on the owner's own
    /// real logs for THIS rework: real, continuous, noisy braking telemetry keeps nudging the learned
    /// peak by small amounts throughout an entire realistic session, so that settledness requirement
    /// almost never arrived (confidence measured stuck at ~0.27 by the end of a full lap on one real
    /// log) - a clamp that may never lift. Corner-aligned analysis of the shipped 1.0.6.8 build separately
    /// found its own output pinned at the identical value at four physically different corners ("the Lock
    /// motor not shaking at all"). A smooth MULTIPLICATIVE scale that always converges to exactly 100%
    /// (driven by the PLAIN, sample-count-only <see cref="Normalized.GripLearner.Confidence"/> - see
    /// <see cref="Normalized.GripLearner.Ratio"/>'s own <c>useStabilityGatedCeiling</c> remarks) cannot get
    /// stuck the way that clamp could, and it introduces no hard step anywhere. This task therefore moved
    /// the ONLY remaining cold-start protection for the LIVE severity number to the PLAIN confidence
    /// (unceilinged by settledness), and added THIS mechanism at Layer 5 to keep the FELT output safely
    /// damped while that plain confidence is still building - the safety-relevant mitigation now lives
    /// where the driver actually feels output, not where the diagnostic number is computed.
    /// <para/>
    /// "USABLE SAMPLES", DEFINED: <see cref="Normalized.GripLearner.Confidence"/> - the qualifying
    /// (already gated upstream by pedal-commitment/lateral-isolation and <see cref="TelemetryLearningGate"/>)
    /// observations folded into THIS channel's own max-grip learner via
    /// <see cref="Normalized.GripLearner.Observe(double)"/>, over its own
    /// <see cref="Normalized.GripLearner.MaturitySamples"/> (200), clamped to [0,1] - reused directly
    /// rather than duplicated. Critically, it is seeded from PERSISTED sample counts by
    /// <see cref="Normalized.GripLearner.Load"/> - a returning, already-warm (game,car) key therefore
    /// starts this SAME session at whatever confidence its persisted sample count already implies (at or
    /// near 1.0 for a genuinely warm car), never re-scaled down to the floor just because a new SimHub
    /// session started. No new persisted state is introduced by this mechanism at all.
    /// <para/>
    /// PART 1 - THE CURVE SHAPE (FINAL clarification): the owner's own two-stage design is
    /// <c>scale = 0.5 + 0.5*curve(x)</c>, <c>x = confidence</c>, with an indicative
    /// <c>curve(x)</c> in [0,1] - explicitly "just an example, for the reference, not the source of
    /// truth". Implemented directly as <c>scale%</c> anchors (so <c>curve(x)</c> is never a separate
    /// step): (0, 50), (0.10, 52.5), (0.50, 57.5), (0.75, 75.0), (0.80, 87.5), (0.90, 93.5), (1.00, 100) -
    /// markedly non-linear and front-loaded-SLOW (only ~2.5 points of recovery over the first 10% of
    /// samples), then accelerating. AN AMBIGUITY IN THE OWNER'S OWN TWO WORKED EXAMPLES WAS RESOLVED IN
    /// FAVOUR OF <c>scale = 0.5 + 0.5*curve</c> (halving the curve value), NOT
    /// <c>scale = 0.5 + curve</c> directly (their first, less complete example) - the halved form is the
    /// one with COMPLETE arithmetic in their own fully-worked example (x=0.75 -&gt; curve 0.50 -&gt;
    /// "actual scaling rate 100%-50%+25%=75%") and is self-consistent with the Stage 2 worked example
    /// below; the anchors above already bake this halving in, so <see cref="Compute"/> itself is a plain
    /// percent-to-fraction conversion, nothing more. TAKEN LITERALLY AS PIECEWISE-LINEAR, the
    /// 0.75-&gt;0.80 segment (+12.5 points over 5% of the domain) versus the neighbouring 0.10-&gt;0.50
    /// segment (+5 points over 40%) is a near-discontinuity a driver would feel as an abrupt strength
    /// change mid-lap. Fitted here instead through <see cref="MonotoneCubicCurve"/> (this project's own
    /// existing, already-tested Fritsch-Carlson monotone Hermite spline - the SAME machinery
    /// <see cref="OutputProjector"/> uses for the driver-facing curve) rather than piecewise-linear
    /// interpolation: C1-smooth (no angular kink anywhere, including through the steep 0.75-0.80 region),
    /// provably monotone non-decreasing, and passes EXACTLY through every one of the owner's own seven
    /// anchors (a Hermite interpolant, unlike a fitted sigmoid, reproduces its control points exactly -
    /// see <c>ColdStartScaleTests</c>' own pinned anchor-point regression for the measured values).
    /// Smoothness through the steep region was prioritised over hitting 87.5% at EXACTLY x=0.80 on the
    /// nose, per the owner's own explicit, twice-repeated instruction that these numbers are illustrative.
    /// <para/>
    /// PART 2 - AMPLITUDE-DEPENDENT SCALING (<see cref="ApplyAmplitudeScale"/>): the SAME cold-start
    /// scale must not attenuate a SMALL shake into nothing (swallowed by a motor's own low-cut/dead
    /// zone) the way it attenuates a large one - "a small shake carries information and must survive; a
    /// large shake is the aggressive one worth taming while cold" (the owner's own reasoning). Formula:
    /// <c>output = p * (1 - (1-s) * (p/100))</c>, where <c>p</c> is the projected output (0-100) and
    /// <c>s</c> is <see cref="Compute"/>'s own scale (0..1). Verified against the owner's own worked
    /// examples: p=100, s=0.6 -&gt; 60 (full attenuation at full amplitude); p=50, s=0.6 -&gt; 40 (HALF
    /// the amplitude gets only HALF the attenuation - effective scale 80%, not 60%).
    /// <para/>
    /// THE SAFETY CONSTRAINT ON THE FLOOR (do not lower <see cref="MinSafeFloor"/> without re-deriving
    /// this): the amplitude transform's own derivative w.r.t. <c>p</c> is <c>1 - 2*(1-s)*p/100</c>. At
    /// <c>s = 0.5</c> this reaches exactly zero at <c>p = 100</c> (flat at the top - still monotone
    /// non-decreasing, the boundary case). For ANY <c>s &lt; 0.5</c>, the derivative goes NEGATIVE above
    /// <c>p = 100/(2*(1-s))</c> - a HARDER brake would then produce a WEAKER shake, inverting the entire
    /// cue. <see cref="MinSafeFloor"/> (0.5) is therefore not a taste choice, it is the mathematical
    /// boundary this formula tolerates - <see cref="Compute"/> and <see cref="ApplyAmplitudeScale"/> both
    /// clamp defensively to this floor regardless of what the curve/caller produces, so a mis-tuned
    /// anchor set can never silently invert the cue.
    /// </summary>
    public static class ColdStartScale
    {
        /// <summary>
        /// HARD MATHEMATICAL FLOOR for the cold-start scale - see this class's own remarks ("THE SAFETY
        /// CONSTRAINT ON THE FLOOR") for the full derivation. Below this, <see cref="ApplyAmplitudeScale"/>'s
        /// own derivative goes negative for a large enough projected output, inverting the cue (a harder
        /// brake producing a WEAKER shake). Never lower this without re-deriving that boundary.
        /// </summary>
        public const double MinSafeFloor = 0.5;

        /// <summary>
        /// The owner's own seven indicative anchor points (confidence, scale%) - see this class's own
        /// remarks. Deliberately exposed as simple, isolated, retunable arrays so the owner can adjust
        /// the shape by feel; <see cref="Curve"/> is rebuilt from these once, statically.
        /// </summary>
        private static readonly double[] AnchorConfidence = { 0.0, 0.10, 0.50, 0.75, 0.80, 0.90, 1.00 };
        private static readonly double[] AnchorScalePercent = { 50.0, 52.5, 57.5, 75.0, 87.5, 93.5, 100.0 };

        private static readonly MonotoneCubicCurve Curve = new MonotoneCubicCurve(AnchorConfidence, AnchorScalePercent);

        /// <summary>
        /// The multiplicative scale (<paramref name="floor"/>..1.0) for one channel, given that
        /// channel's own current <see cref="Normalized.GripLearner.Confidence"/>. Continuous, monotone
        /// non-decreasing, genuinely reaches exactly 1.0 at confidence 1 (not an asymptote). Non-finite/
        /// out-of-range input is clamped defensively.
        /// <para/>
        /// TIERED COLD-START FLOOR (v1.0.7, docs\v107-tiered-coldstart-report.md): <paramref name="floor"/>
        /// defaults to <see cref="MinSafeFloor"/> (0.5) - Tier 1's own floor, and BIT-IDENTICAL to every
        /// caller/test that predates the tiered reference system. A HIGHER floor (0.6/0.7/0.8 for Tiers
        /// 2/3/4 - see <see cref="Normalized.ColdStartTierFloors"/>) is PERMITTED and simply raises where
        /// the ramp starts; anything BELOW <see cref="MinSafeFloor"/> is defensively clamped UP to it,
        /// never down - the hard mathematical floor this class's own remarks derive is never relaxed in
        /// the other direction. The owner's own seven anchors (fitted at the Tier-1/0.5 floor) are
        /// reproduced EXACTLY by <see cref="Curve"/> as before; a higher floor is applied as a plain
        /// AFFINE remap of the curve's own 50..100 output range onto floor*100..100 - an affine transform
        /// of a monotone Hermite spline's control points preserves both its monotonicity and its C1
        /// smoothness, so every property <see cref="Curve"/> already guarantees at floor 0.5 still holds
        /// at any higher floor.
        /// </summary>
        public static double Compute(double confidence, double floor = MinSafeFloor)
        {
            double x = ClampMath.To01(ClampMath.IsFinite(confidence) ? confidence : 0.0);
            double effectiveFloor = ClampMath.IsFinite(floor) ? Math.Max(MinSafeFloor, floor) : MinSafeFloor;
            double baseScalePercent = Curve.Evaluate(x); // in [50, 100], anchored to the Tier-1/0.5 floor.

            double floorPercent = effectiveFloor * 100.0;
            double stretchedPercent = floorPercent + (100.0 - floorPercent) * (baseScalePercent - 50.0) / 50.0;

            double scale = stretchedPercent / 100.0;
            return Math.Max(effectiveFloor, Math.Min(1.0, scale));
        }

        /// <summary>
        /// PART 2 - the amplitude-dependent transform - see this class's own remarks for the formula,
        /// worked examples, and the safety-critical floor clamp. <paramref name="scale"/> is clamped to
        /// [<see cref="MinSafeFloor"/>, 1.0] defensively before use, regardless of what
        /// <see cref="Compute"/> (or any other caller) produces - this is what makes the "never invert
        /// the cue" guarantee unconditional rather than dependent on every caller behaving.
        /// <para/>
        /// DELIBERATE DESIGN DECISION - DO NOT "FIX" THIS LATER: the <c>p/100</c> divisor below is the
        /// ABSOLUTE scale 0-100, NOT any driver-configured output ceiling - even a driver-shaped curve
        /// that caps below 100, this transform still measures "how large is this shake" against the
        /// absolute 0-100 scale. The amplitude term exists to protect a SMALL shake from a motor's own
        /// low-cut/dead zone, which is an absolute property of the DEVICE, not a proportion of whatever
        /// ceiling the driver happens to have configured.
        /// </summary>
        public static double ApplyAmplitudeScale(double projectedOutput, double scale)
        {
            double s = ClampMath.IsFinite(scale) ? Math.Max(MinSafeFloor, Math.Min(1.0, scale)) : MinSafeFloor;
            double p = ClampMath.To0100(projectedOutput);
            double output = p * (1.0 - (1.0 - s) * (p / 100.0));
            return ClampMath.To0100(output);
        }
    }
}

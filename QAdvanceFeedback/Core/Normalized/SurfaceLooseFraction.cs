namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// SURFACE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md - the owner's
    /// own follow-up: a learned grip reference dominated by tarmac's higher achievable deceleration
    /// silently reads a genuine loose-surface (grass/gravel) limit as low severity - 0.4g against a
    /// 1.5g tarmac-dominated reference reads ~27%, near-silent at exactly the moment grip is lowest).
    /// <para/>
    /// A CONTINUOUS FRACTION, NOT A DISCRETE "SEALED/LOOSE/MIXED/UNKNOWN" KEY SEGMENT - a deliberate
    /// departure from a literal four-way surface-class key, chosen for two reasons:
    /// <list type="number">
    /// <item><b>Continuity.</b> A discrete class key read directly would make
    /// <see cref="NormalizedWheelLockSlipEngine"/>'s severity computation SWITCH which learned peak it
    /// reads from the instant classification flips - exactly the "step change felt as a click" this
    /// task's own brief warns against, even though the underlying G magnitude itself never jumps. A
    /// continuous fraction, SMOOTHED over time (see <see cref="NormalizedWheelLockSlipEngine"/>'s own
    /// per-channel smoothing state), lets the reference blend gradually across a tarmac/grass boundary
    /// instead.</item>
    /// <item><b>No combinatorial fragmentation.</b> Only TWO learner buckets ever exist per
    /// (game,car,source) - "Sealed" and "Loose" - not a third/fourth "Mixed"/"Unknown" bucket that
    /// would need its own maturity threshold. A title that never reports a loose surface fraction above
    /// 0 keeps feeding the SAME "Sealed" bucket it always did (identical to today's undifferentiated
    /// behaviour - see <see cref="Compute"/>'s own remarks on why null defaults to sealed).</item>
    /// </list>
    /// </summary>
    public static class SurfaceLooseFraction
    {
        /// <summary>
        /// The fraction (0.0-1.0) of this frame's four wheels reporting a loose surface. A null reading
        /// (title does not populate this field, OR the specific wheel's own array slot was
        /// unreachable - see <see cref="ITelemetryFrame.WheelOnLooseSurfaceFrontLeft"/>'s own remarks on
        /// why null-vs-false cannot be resolved here) counts as SEALED (0), not excluded - this is what
        /// makes a title with NO surface signal at all degrade, by construction, to the exact
        /// single-reference behaviour this plugin had before surface-keying existed (every frame
        /// contributes 0.0, so the smoothed fraction settles at 0.0 and stays there, and the blend in
        /// <see cref="NormalizedWheelLockSlipEngine"/> reduces to reading the "Sealed" bucket alone,
        /// forever) - "degrade to today's behaviour" falls out of this rule for free, without a special
        /// case anywhere else.
        /// </summary>
        public static double Compute(bool? frontLeft, bool? frontRight, bool? rearLeft, bool? rearRight)
        {
            int loose = 0;
            if (frontLeft == true) loose++;
            if (frontRight == true) loose++;
            if (rearLeft == true) loose++;
            if (rearRight == true) loose++;
            return loose / 4.0;
        }

        /// <summary>True if ANY of the four wheels reports a KNOWN (non-null) reading this frame - used
        /// to feed <c>Diag.Capabilities.SurfaceEverReportedLoose</c>'s own latch (see
        /// <see cref="NormalizedWheelLockSlipEngine"/>'s own remarks) independently of whether that
        /// reading happens to be true or false.</summary>
        public static bool AnyWheelReported(bool? frontLeft, bool? frontRight, bool? rearLeft, bool? rearRight)
            => frontLeft.HasValue || frontRight.HasValue || rearLeft.HasValue || rearRight.HasValue;
    }
}

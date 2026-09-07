using System;

namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// The owner-requested "Integrate Wheel Lock and Slip" G-force shake: turns one pad PAIR's
    /// (left/right of the same zone) current G-force value into a left/right ALTERNATING oscillation
    /// superimposed on that value, driven by how hard the wheel is currently locking/slipping.
    /// <para/>
    /// MECHANICS (per pad pair, per the owner's own worked examples - see
    /// <c>docs\shake-and-toggle-report.md</c>):
    /// <code>
    /// band   = gForceValue * wheelContribution     // wheelContribution already folds in the scale -
    ///                                               // see GForceEngine.Compute's own remarks on how
    ///                                               // Lock/Slip combine into this one number
    /// half   = band / 2
    /// centre = gForceValue                          // continuous: at wheelContribution == 0, half ==
    ///                                                // 0 and the output is exactly centre - inert.
    /// output_L = effectiveCentre + half * sin(2*pi*f*t)
    /// output_R = effectiveCentre - half * sin(2*pi*f*t)
    /// </code>
    /// <para/>
    /// CLAMPING BY SHIFTING, NOT SQUASHING (the owner's explicit requirement - the band WIDTH must be
    /// preserved, only its POSITION moves): when <c>band &lt;= 100</c> (so a shift can always make it
    /// fit), <c>effectiveCentre = Clamp(gForceValue, half, 100 - half)</c> - this single clamp
    /// reproduces all three required cases in one formula:
    /// <list type="bullet">
    /// <item>both ends already in range -&gt; Clamp is a no-op, effectiveCentre == gForceValue.</item>
    /// <item>only the top overflows (gForceValue + half &gt; 100) -&gt; Clamp pins effectiveCentre to
    /// <c>100 - half</c>, so the shifted top sits exactly at 100.</item>
    /// <item>only the bottom underflows (gForceValue - half &lt; 0) -&gt; Clamp pins effectiveCentre to
    /// <c>half</c>, so the shifted bottom sits exactly at 0.</item>
    /// </list>
    /// Only when the band itself is wider than the whole 0-100 range (<c>half &gt; 50</c>, i.e.
    /// <c>band &gt; 100</c>) is a shift alone mathematically impossible (there is no position where a
    /// span wider than 100 fits inside [0,100]) - the owner's own fallback applies exactly here:
    /// effectiveCentre is fixed at 50 and the final output is squashed (clamped) to [0,100] instead of
    /// shifted, per the owner's own worked example 3 (band 162, both ends out).
    /// </summary>
    public static class GForceShake
    {
        /// <summary>1 Hz - the slowest this shake is allowed to run, enforced in
        /// <see cref="Settings.GForceSettings.ShakeFrequencyHz"/>'s own setter (not merely a UI
        /// spinner minimum). LOWERED from an original 5 Hz floor (docs\shake-tuning-report.md) per
        /// real seat-time driver feedback: 5 Hz did not read as "obvious" enough, and 1-2 Hz reads
        /// better. NOT to be confused with <see cref="Projection.PulseSettings"/>'s own, separate,
        /// UNCHANGED 200 ms (5 Hz) pulse gap floor on the Wheel Lock/Slip tabs.</summary>
        public const double MinFrequencyHz = 1.0;

        /// <summary>20 Hz - the fastest this shake is allowed to run. See <see cref="MinFrequencyHz"/>.</summary>
        public const double MaxFrequencyHz = 20.0;

        /// <summary>
        /// Upper bound on <see cref="Wave"/>'s sustain fraction: 90%, not 100%. At exactly 100% the ramps
        /// would have zero duration and the wave would become an instantaneous square - a discontinuity
        /// the hardware cannot follow and which reads as a click rather than a shake. 90% already feels
        /// essentially square while leaving a real, if brief, transit.
        /// </summary>
        public const double MaxSustainFraction = 0.90;

        // THE BLEND IS GONE (v1.0.8), and with it DefaultBlend, the quadrature wave, and
        // PeakExcursionFactor. It existed because a 100% differential shake could not be felt: with
        // L = c + h·w and R = c - h·w the pair's TOTAL never moves, and since two transducers are
        // uncorrelated their powers add, so felt magnitude goes as sqrt(L² + R²) - which a pure pan
        // barely disturbs. Measured on a real session log: the pan swung 62 points peak-to-peak while
        // felt magnitude swung 13 (23% depth, against a 29% ceiling even at a saturated 100/0 swing).
        //
        // THE MEASUREMENT STILL STANDS; only the control changed. A share-of-the-band spinner turned out
        // to have just three points that were distinct to feel, so it became ShakeFeeling's three named
        // choices - and OppositePhase is deliberately the subtlest of them for exactly the reason above.

        // ---- THE SINE + HOLD WAVE (v1.0.8, owner's specification). ------------------------------------
        //
        // Replaces the trapezoid. One cycle of a channel that starts at its MAXIMUM is, as a fraction of
        // the period:
        //
        //     [0,          h/4)                 held at MAX
        //     [h/4,        h/4 + (1-h)/2)       half a cosine, MAX -> MIN
        //     [...,        ... + h/2)           held at MIN
        //     [...,        ... + (1-h)/2)       half a cosine, MIN -> MAX
        //     [1 - h/4,    1)                   held at MAX
        //
        // THE PERIOD IS ALWAYS 1/f (owner's decision, 2026-09-06). The holds are a share OF the cycle,
        // not an addition to it, so "10 Hz" keeps meaning ten full Max-Min-Max travels per second at
        // every hold setting - the rule the owner set when the frequency semantics were pinned. The
        // sine portion is therefore (1-h) of the cycle, exactly as specified.
        //
        // THE TWO MAX HOLDS ARE HALF-LENGTH EACH. A cycle starts and ends at the maximum, so the single
        // "hold at max" is split across the two ends - h/4 + h/4 - while the minimum, which is passed
        // through once, gets h/2. Both extremes are therefore held for the same total h/2, which is what
        // makes the shape symmetric.
        //
        // AT h = 0 THIS IS EXACTLY A COSINE: the ramps become (1 +/- cos(2*pi*p))/2 with no flats at all.

        /// <summary>The hold <see cref="ShakeFeeling.Blending"/> always uses - see that member.</summary>
        public const double BlendingHoldFraction = 0.5;

        /// <summary>
        /// One channel's normalised position, 0 at the minimum and 1 at the maximum, for a wave that
        /// starts at its MAXIMUM. See the block comment above for the shape.
        /// </summary>
        public static double SineHoldWave(double frequencyHz, double phaseSeconds, double holdFraction)
        {
            if (!ClampMath.IsFinite(frequencyHz) || frequencyHz <= 0.0
                || !ClampMath.IsFinite(phaseSeconds)) return 1.0;

            double h = ClampMath.IsFinite(holdFraction)
                ? ClampMath.Clamp(holdFraction, 0.0, MaxSustainFraction)
                : 0.0;

            double cycles = frequencyHz * phaseSeconds;
            double p = cycles - Math.Floor(cycles);

            double ramp = (1.0 - h) / 2.0;      // each half-cosine, as a fraction of the period
            double q1 = h / 4.0;                // end of the opening MAX hold
            double q2 = q1 + ramp;              // end of the MAX -> MIN ramp
            double q3 = q2 + h / 2.0;           // end of the MIN hold
            double q4 = q3 + ramp;              // end of the MIN -> MAX ramp (== 1 - h/4)

            if (p < q1) return 1.0;
            if (p < q2) return ramp <= 0.0 ? 0.0 : (1.0 + Math.Cos(Math.PI * ((p - q1) / ramp))) / 2.0;
            if (p < q3) return 0.0;
            if (p < q4) return ramp <= 0.0 ? 1.0 : (1.0 - Math.Cos(Math.PI * ((p - q3) / ramp))) / 2.0;
            return 1.0;
        }

        /// <summary>
        /// Both pads' normalised positions (0..1) for a feeling, a frequency and a hold.
        /// <para/>
        /// The LEADER is the pad that starts at its maximum; <paramref name="reversed"/> swaps which
        /// side that is, exactly as it did before - the start-corner rule that decides it is unchanged.
        /// </summary>
        public static void FeelingPair(
            double frequencyHz, double phaseSeconds, double holdFraction, ShakeFeeling feeling,
            out double left, out double right, bool reversed = false)
        {
            double period = frequencyHz > 0.0 ? 1.0 / frequencyHz : 0.0;
            double leader, follower;

            switch (feeling)
            {
                case ShakeFeeling.SamePhase:
                    // No offset at all - one pulse, both pads.
                    leader = follower = SineHoldWave(frequencyHz, phaseSeconds, holdFraction);
                    break;

                case ShakeFeeling.Blending:
                    // BOTH START AT MAX, one already descending. The offset is the leader's own opening
                    // MAX hold (h/4 of the period), so at phase 0 the follower sits exactly at the top of
                    // its descent while the leader is still holding - the owner's own description. With
                    // the fixed hold of 0.5 that offset is an eighth of the cycle.
                    leader = SineHoldWave(frequencyHz, phaseSeconds, BlendingHoldFraction);
                    follower = SineHoldWave(
                        frequencyHz, phaseSeconds + period * BlendingHoldFraction / 4.0, BlendingHoldFraction);
                    break;

                default:   // OppositePhase
                    leader = SineHoldWave(frequencyHz, phaseSeconds, holdFraction);
                    follower = SineHoldWave(frequencyHz, phaseSeconds + period / 2.0, holdFraction);
                    break;
            }

            left = reversed ? follower : leader;
            right = reversed ? leader : follower;
        }

        /// <summary>The hold actually used for a feeling - <see cref="ShakeFeeling.Blending"/> pins its
        /// own and ignores the setting, which is why the UI hides the control for it.</summary>
        public static double EffectiveHold(double configuredHold, ShakeFeeling feeling)
            => feeling == ShakeFeeling.Blending
                ? BlendingHoldFraction
                : (ClampMath.IsFinite(configuredHold) ? ClampMath.Clamp(configuredHold, 0.0, MaxSustainFraction) : 0.0);

        /// <summary>
        /// The four positions a shake can OPEN from, one per corner of the (left, right) square. A shake
        /// that begins from real silence picks whichever of these moves the pads FURTHEST from where they
        /// currently sit, so the first frame is the biggest jolt available rather than a fade-in;
        /// <see cref="PhaseForCorner"/> then finds the phase that actually lands there for the configured
        /// feeling. Not every corner is reachable by every feeling - <see cref="ShakeFeeling.SamePhase"/>
        /// can only ever sit at <see cref="BothLow"/> or <see cref="BothHigh"/> - which is why the search
        /// returns the closest attainable phase rather than assuming an exact hit.
        /// </summary>
        public enum ShakeStartCorner
        {
            /// <summary>Left at its maximum, right at its minimum.</summary>
            PanLeft,
            /// <summary>Left at its minimum, right at its maximum.</summary>
            PanRight,
            /// <summary>Both pads at their minimum.</summary>
            BothLow,
            /// <summary>Both pads at their maximum.</summary>
            BothHigh,
        }

        /// <summary>
        /// The range a single pad will span for a given centre and band - i.e. the min/max the start
        /// alignment aims at. Applies the same excursion and clamp-by-shift rules
        /// <see cref="Apply"/> does, so the two can never disagree about where the shake will actually go.
        /// </summary>
        public static void PadRange(double centre, double band, out double low, out double high)
        {
            // NO PeakExcursionFactor ANY MORE. The sine+hold wave spans a full 0..1 for every feeling
            // and every hold, so a pad's travel IS the band - there is no blend-dependent shrinkage left
            // to compensate for. This used to divide the half-band by that factor, which is exactly why
            // a nominal band of 70 only ever swung 35 wide at the default blend.
            double excursion = band / 2.0;
            double effectiveCentre = excursion > 50.0
                ? 50.0
                : ClampMath.Clamp(ClampMath.To0100(centre), excursion, 100.0 - excursion);

            low = ClampMath.To0100(effectiveCentre - excursion);
            high = ClampMath.To0100(effectiveCentre + excursion);
        }

        /// <summary>
        /// Which corner a shake should OPEN at, so the very first frame moves the pads as far as they can
        /// go - the owner's "we will always get the maximum shaking at the first frame".
        /// <para/>
        /// Scored as TOTAL ABSOLUTE TRAVEL: <c>|L − targetL| + |R − targetR|</c> for each of the four
        /// corners, highest wins. Absolute distance rather than a signed difference because a pad can sit
        /// OUTSIDE the coming range (a strong cornering bias easily puts it there), where a signed term
        /// goes negative and would rank a corner as worse than doing nothing.
        /// <para/>
        /// TIES prefer a PAN corner - a shake that opens by splitting the pads reads as a shake
        /// immediately, whereas one that opens with both pads together has to wait a quarter cycle before
        /// anything distinguishes it from a plain level change. Which pan corner is then decided by
        /// <paramref name="leadLeft"/>, which the caller alternates between consecutive shakes.
        /// </summary>
        /// <summary>
        /// Which of the four opening poses to jump to when a shake starts - the one FURTHEST from where
        /// the pads currently sit, so the first frame is the biggest jolt available.
        /// </summary>
        public static ShakeStartCorner ChooseStartCorner(
            double currentLeft, double currentRight, double centre, double band, bool leadLeft)
        {
            CornerOutputs(centre, band, out double panLeftLeft, out double panLeftRight,
                out double bothLow, out double bothHigh);

            double panLeft = Travel(currentLeft, panLeftLeft) + Travel(currentRight, panLeftRight);
            double panRight = Travel(currentLeft, panLeftRight) + Travel(currentRight, panLeftLeft);
            double low = Travel(currentLeft, bothLow) + Travel(currentRight, bothLow);
            double high = Travel(currentLeft, bothHigh) + Travel(currentRight, bothHigh);

            // The lead side is decided by the caller's alternation rule; between the two PAN corners it
            // simply picks which one counts as "lead", and the common corners are scored on their own.
            double bestPan = leadLeft ? panLeft : panRight;
            ShakeStartCorner panCorner = leadLeft ? ShakeStartCorner.PanLeft : ShakeStartCorner.PanRight;

            if (bestPan >= low && bestPan >= high) return panCorner;
            return low >= high ? ShakeStartCorner.BothLow : ShakeStartCorner.BothHigh;
        }

        private static double Travel(double from, double to) => Math.Abs(to - from);

        public static void CornerOutputs(double centre, double band,
            out double panLeftLeft, out double panLeftRight, out double bothLow, out double bothHigh)
        {
            PadRange(centre, band, out double low, out double high);

            panLeftLeft = high;
            panLeftRight = low;
            bothLow = low;
            bothHigh = high;
        }

        public static bool FirstEverLeadIsLeft(double currentLeft, double currentRight, double low, double high)
        {
            double leftTravel = Math.Min(Math.Abs(currentLeft - low), Math.Abs(currentLeft - high));
            double rightTravel = Math.Min(Math.Abs(currentRight - low), Math.Abs(currentRight - high));
            return leftTravel <= rightTravel;
        }

        /// <summary>
        /// The phase that puts the wave exactly on <paramref name="corner"/>.
        /// <para/>
        /// <paramref name="reversed"/> flips the quadrature term, which REVERSES THE ORDER the cycle
        /// visits its corners - pan-left, both-low, pan-right, both-high one way; pan-left, both-high,
        /// pan-right, both-low the other. That is what makes a pad "move first" out of a both-together
        /// corner, and alternating it between consecutive shakes is what gives the owner's requested
        /// near-continuous feel when a shake stops and restarts quickly.
        /// </summary>
        /// <summary>
        /// The phase at which a shake should OPEN so that its first frame sits on the requested corner.
        /// <para/>
        /// SEARCHED, NOT DERIVED. The old model had a closed form because pan and common were separable;
        /// the sine+hold model does not - where "both pads at their minimum" occurs depends on the
        /// feeling AND the hold (under Blending it is the overlap of two offset minimum-holds, which
        /// moves as the hold changes). Scanning one cycle is exact for every combination, cannot go
        /// stale when a feeling is added, and runs once per shake start rather than per frame.
        /// <para/>
        /// A corner a feeling cannot reach simply resolves to its closest approach - Opposite Phase can
        /// never put both pads high, and Same Phase can never pan - which is the honest answer and keeps
        /// the caller free of per-feeling special cases.
        /// </summary>
        public static double PhaseForCorner(
            ShakeStartCorner corner, double frequencyHz, double holdFraction, ShakeFeeling feeling, bool reversed)
        {
            if (!ClampMath.IsFinite(frequencyHz) || frequencyHz <= 0.0) return 0.0;

            double targetLeft, targetRight;
            switch (corner)
            {
                case ShakeStartCorner.PanRight: targetLeft = 0.0; targetRight = 1.0; break;
                case ShakeStartCorner.BothLow: targetLeft = 0.0; targetRight = 0.0; break;
                case ShakeStartCorner.BothHigh: targetLeft = 1.0; targetRight = 1.0; break;
                default: targetLeft = 1.0; targetRight = 0.0; break;   // PanLeft
            }

            const int steps = 720;
            double period = 1.0 / frequencyHz;
            double bestPhase = 0.0;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < steps; i++)
            {
                double phase = period * i / steps;
                FeelingPair(frequencyHz, phase, holdFraction, feeling, out double l, out double r, reversed);

                double dl = l - targetLeft;
                double dr = r - targetRight;
                double distance = dl * dl + dr * dr;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestPhase = phase;
                }
            }

            return bestPhase;
        }

        /// <summary>
        /// A shake AROUND a G-force level: the pad swings <c>band/2</c> either side of it.
        /// </summary>
        public static void Apply(
            double gForceValue0100, double wheelContribution, double frequencyHz, double phaseSeconds,
            double holdFraction, ShakeFeeling feeling,
            out double left, out double right, bool reversed = false)
        {
            double centre = ClampMath.To0100(gForceValue0100);
            double contribution = wheelContribution > 0.0 && ClampMath.IsFinite(wheelContribution) ? wheelContribution : 0.0;

            ApplyBand(centre, centre * contribution, frequencyHz, phaseSeconds, holdFraction, feeling, reversed,
                out left, out right);
        }

        /// <summary>
        /// A shake that IS the wheel value: the pad travels from silence to the band. See
        /// <see cref="ShakeApplyMode.AllChannelsLockSlip"/> for why the floor is zero here.
        /// </summary>
        public static void ApplyLockSlipOnly(
            double wheelContribution, double frequencyHz, double phaseSeconds, double holdFraction,
            ShakeFeeling feeling, out double left, out double right, bool reversed = false)
        {
            double contribution = wheelContribution > 0.0 && ClampMath.IsFinite(wheelContribution) ? wheelContribution : 0.0;

            // 100 * contribution == lockOrSlipValue * scale, by construction of the contribution term.
            double band = ClampMath.To0100(100.0 * contribution);

            FeelingPair(frequencyHz, phaseSeconds, holdFraction, feeling, out double nLeft, out double nRight, reversed);
            left = ClampMath.To0100(band * nLeft);
            right = ClampMath.To0100(band * nRight);
        }

        /// <summary>
        /// The wave mapped into an EXPLICIT range rather than a centre and a band.
        /// <para/>
        /// Exists for <see cref="ShakeApplyMode.HigherOfGForceOrLockSlip"/>, which (2026-09-07) keeps
        /// <see cref="ShakeApplyMode.PerChannel"/>'s own low exactly and only RAISES the high - so the
        /// two ends come from different places and can no longer be expressed as one centre plus one
        /// band. <see cref="PadRange"/> produces the per-channel pair this is handed.
        /// </summary>
        public static void ApplyRange(
            double low, double high, double frequencyHz, double phaseSeconds, double holdFraction,
            ShakeFeeling feeling, bool reversed, out double left, out double right)
        {
            FeelingPair(frequencyHz, phaseSeconds, holdFraction, feeling,
                out double nLeft, out double nRight, reversed);

            double span = high - low;
            left = ClampMath.To0100(low + span * nLeft);
            right = ClampMath.To0100(low + span * nRight);
        }

        private static void ApplyBand(
            double centre, double band, double frequencyHz, double phaseSeconds, double holdFraction,
            ShakeFeeling feeling, bool reversed, out double left, out double right)
        {
            double half = band / 2.0;

            // The band cannot be preserved by any shift once it is wider than the whole scale - fix the
            // centre and let the output squash instead (the owner's own explicit exception, unchanged).
            double effectiveCentre = half > 50.0
                ? 50.0
                : ClampMath.Clamp(centre, half, 100.0 - half);

            FeelingPair(frequencyHz, phaseSeconds, holdFraction, feeling, out double nLeft, out double nRight, reversed);

            // n is 0 at the minimum and 1 at the maximum, so (2n - 1) maps it onto -1..+1.
            left = ClampMath.To0100(effectiveCentre + half * (2.0 * nLeft - 1.0));
            right = ClampMath.To0100(effectiveCentre + half * (2.0 * nRight - 1.0));
        }
    }
}

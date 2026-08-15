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
        /// <summary>5 Hz - the slowest this shake is allowed to run, enforced in
        /// <see cref="Settings.GForceSettings.ShakeFrequencyHz"/>'s own setter (not merely a UI
        /// spinner minimum).</summary>
        public const double MinFrequencyHz = 5.0;

        /// <summary>20 Hz - the fastest this shake is allowed to run. See <see cref="MinFrequencyHz"/>.</summary>
        public const double MaxFrequencyHz = 20.0;

        /// <summary>
        /// Computes one pad pair's shaken left/right output for this instant.
        /// </summary>
        /// <param name="gForceValue0100">This pair's own current G-force value (0-100) - the wave's
        /// centre before any clamp-by-shift is applied.</param>
        /// <param name="wheelContribution">Already-scaled, already-combined wheel lock/slip drive -
        /// see <see cref="GForceEngine"/>'s own remarks for how Lock/Slip are combined into this one
        /// non-negative number (0 means no shake at all, by construction: band becomes 0).</param>
        /// <param name="frequencyHz">Oscillation frequency in Hz.</param>
        /// <param name="phaseSeconds">Elapsed seconds fed into <c>sin(2*pi*f*t)</c> - the caller's
        /// injectable "clock", advanced from frame <c>dt</c> (never wall-clock) - see
        /// <see cref="GForceEngine"/>'s own remarks, mirroring <c>PulseGenerator</c>'s identical
        /// convention.</param>
        public static void Apply(
            double gForceValue0100, double wheelContribution, double frequencyHz, double phaseSeconds,
            out double left, out double right)
        {
            double centre = ClampMath.To0100(gForceValue0100);
            double contribution = wheelContribution > 0.0 && ClampMath.IsFinite(wheelContribution) ? wheelContribution : 0.0;

            double band = centre * contribution;
            double half = band / 2.0;

            double effectiveCentre;
            bool bandTooWideToShift = half > 50.0;
            if (bandTooWideToShift)
            {
                // The owner's own explicit exception: a band wider than the whole 0-100 range cannot
                // be preserved by any shift - fix the centre at 50 and squash the final output instead.
                effectiveCentre = 50.0;
            }
            else
            {
                // One clamp reproduces "shift down if the top overflows, shift up if the bottom
                // underflows, otherwise leave it alone" - see this class's own remarks.
                effectiveCentre = ClampMath.Clamp(centre, half, 100.0 - half);
            }

            double wave = Math.Sin(2.0 * Math.PI * frequencyHz * phaseSeconds);
            double l = effectiveCentre + half * wave;
            double r = effectiveCentre - half * wave;

            if (bandTooWideToShift)
            {
                l = ClampMath.To0100(l);
                r = ClampMath.To0100(r);
            }

            left = l;
            right = r;
        }
    }
}

using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// One channel's (Wheel Lock or Wheel Slip, independently - see
    /// <see cref="Settings.WheelChannelSettings"/>) configurable weights for <see cref="Aggregator"/>'s
    /// physically-motivated scheme (docs\aggregation-report.md), supplied and seat-tested by the
    /// plugin's owner. REPLACES the previous generic <c>GroupMode</c>/p-norm scheme, which ignored
    /// weight transfer entirely and weighted every wheel identically regardless of channel or car.
    /// <para/>
    /// THE FIVE NUMBERS:
    /// <list type="bullet">
    /// <item><see cref="WMax"/>/<see cref="WMin"/> - the AXLE (Front/Rear) blend: the stronger and the
    /// weaker of the two wheels on one axle, weighted order-independently (see
    /// <see cref="Aggregator"/>'s own remarks).</item>
    /// <item><see cref="WFront"/>/<see cref="WRear"/> - the SIDE (Left/Right) and CAR (All) blend: the
    /// front-position value against the rear-position value, weighted order-dependently - this is
    /// where weight transfer is actually modelled (front-heavy under braking, rear/driven-heavy under
    /// power, tunable per car/drivetrain).</item>
    /// <item><see cref="SlipFloorFactor"/> - the Slip-only (by shipped default) floor that stops a
    /// single strongly-spinning wheel being diluted away; 0 disables it entirely (Lock's own shipped
    /// default - see <see cref="Aggregator"/>'s own remarks on why Lock does not need it).</item>
    /// </list>
    /// <para/>
    /// BOUNDS: <see cref="WMax"/>/<see cref="WMin"/>/<see cref="WFront"/>/<see cref="WRear"/> are
    /// clamped to &gt;= 0 only - NOT forced to sum to 1 within a pair. The owner's own shipped numbers
    /// happen to sum to exactly 1.0 in both pairs for both channels (0.45+0.55, 0.90+0.10, 0.55+0.45,
    /// 0.65+0.35) - that is what keeps a UNIFORM four-wheel reading passing through the whole
    /// aggregation unchanged (see AggregatorTests' own uniform-input coverage), but this struct
    /// deliberately does not enforce it: a driver who types weights that do not sum to 1 is never
    /// silently rescaled to "fix" what they typed (see <see cref="Settings.WheelChannelSettings"/>'s own
    /// remarks on why auto-normalising would be worse - it would hide what the driver actually
    /// configured). <see cref="Aggregator"/>'s own final 0-100 clamp is what keeps the OUTPUT bounded
    /// regardless of how far from 1 a pair sums to.
    /// <para/>
    /// <see cref="SlipFloorFactor"/> IS clamped to [0,1]: it multiplies the strongest participating
    /// wheel to produce a floor: a factor above 1 would let the floor exceed every wheel's own reading
    /// (an outright boost, not "never diluted away" - not what this mechanism is for).
    /// </summary>
    public readonly struct AggregationWeights : IEquatable<AggregationWeights>
    {
        public readonly double WMax;
        public readonly double WMin;
        public readonly double WFront;
        public readonly double WRear;
        public readonly double SlipFloorFactor;

        public AggregationWeights(double wMax, double wMin, double wFront, double wRear, double slipFloorFactor)
        {
            WMax = ClampNonNegative(wMax);
            WMin = ClampNonNegative(wMin);
            WFront = ClampNonNegative(wFront);
            WRear = ClampNonNegative(wRear);
            SlipFloorFactor = ClampMath.Clamp(ClampMath.IsFinite(slipFloorFactor) ? slipFloorFactor : 0.0, 0.0, 1.0);
        }

        private static double ClampNonNegative(double value)
            => ClampMath.IsFinite(value) && value > 0.0 ? value : 0.0;

        /// <summary>Wheel Lock's owner-tested shipped defaults (docs\aggregation-report.md; REVISED,
        /// docs\slip-source-consistency-report.md - a second round of owner seat-testing): axle blend
        /// 0.75/0.25 (up from 0.45/0.55 - the stronger of the two wheels on an axle now dominates,
        /// rather than being weighted slightly LESS than the weaker one), front/rear blend 0.90/0.10
        /// UNCHANGED (braking is still front-weight-transfer-dominated), no slip floor (0.0 - Lock does
        /// not need one, see <see cref="Aggregator"/>'s own remarks).</summary>
        public static readonly AggregationWeights LockDefaults = new AggregationWeights(0.75, 0.25, 0.90, 0.10, 0.0);

        /// <summary>Wheel Slip's owner-tested shipped defaults (REVISED, docs\slip-source-consistency-report.md):
        /// axle blend 0.85/0.15 (up from 0.55/0.45 - the strongest spinning wheel now dominates the axle
        /// reading far more than before), front/rear blend 0.45/0.55 (FLIPPED from 0.65/0.35 - rear/driven-axle
        /// bias, matching the owner's own seat-tested RWD-leaning default rather than Lock's front bias),
        /// slip floor 0.70 (up from 0.4 - a single strongly-spinning wheel is diluted away even less than
        /// before).</summary>
        public static readonly AggregationWeights SlipDefaults = new AggregationWeights(0.85, 0.15, 0.45, 0.55, 0.70);

        /// <summary>
        /// The NEUTRAL, equal-weight, no-floor fallback (0.5/0.5/0.5/0.5/0.0) - reduces both blend
        /// stages to a plain mean, roughly equivalent in spirit to the RETIRED default (p-norm with
        /// uniform weights). Used ONLY as <see cref="Settings.WheelChannelSettings"/>'s bare field
        /// initialiser (a genuinely bare <c>new WheelChannelSettings()</c>, e.g. a settings-UI scratch
        /// object) - see that class's own remarks on why a shared POCO cannot give two different
        /// field-initialiser defaults, and on why a config file predating this feature does NOT actually
        /// observe this fallback (it reads <see cref="LockDefaults"/>/<see cref="SlipDefaults"/> as
        /// appropriate instead, via Newtonsoft's existing-object reuse - verified in ConfigStoreTests).
        /// </summary>
        public static readonly AggregationWeights Neutral = new AggregationWeights(0.5, 0.5, 0.5, 0.5, 0.0);

        public bool Equals(AggregationWeights other)
            => WMax.Equals(other.WMax) && WMin.Equals(other.WMin) && WFront.Equals(other.WFront)
            && WRear.Equals(other.WRear) && SlipFloorFactor.Equals(other.SlipFloorFactor);

        public override bool Equals(object obj) => obj is AggregationWeights other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = WMax.GetHashCode();
                h = (h * 397) ^ WMin.GetHashCode();
                h = (h * 397) ^ WFront.GetHashCode();
                h = (h * 397) ^ WRear.GetHashCode();
                h = (h * 397) ^ SlipFloorFactor.GetHashCode();
                return h;
            }
        }

        public override string ToString()
            => $"WMax={WMax:F3} WMin={WMin:F3} WFront={WFront:F3} WRear={WRear:F3} SlipFloorFactor={SlipFloorFactor:F3}";
    }
}

using System;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The owner's physically-motivated aggregation scheme (docs\aggregation-report.md) - replaces the
    /// retired p-norm/<c>GroupMode</c> aggregator. Every hand-computed test below reconstructs the
    /// formula from <see cref="AggregationWeights"/>' own five numbers, independently of
    /// <see cref="Aggregator"/>'s own implementation, so a mistake in either the formula OR the
    /// shipped defaults would be caught.
    /// </summary>
    public class AggregatorTests
    {
        // ------------------------------------------------------------------------------------
        // Hand-computed formula checks, both channels - Front/Rear (axle Max/Min blend), Left/Right
        // (raw per-side front/rear blend), All (axle-blended front/rear blend).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Lock_defaults_match_the_owners_tested_values_exactly()
        {
            // MUTATION (a) target: if WMax/WMin (or WFront/WRear) were ever swapped, this test's
            // hand-computed expectations below would fail.
            Assert.Equal(0.45, AggregationWeights.LockDefaults.WMax, 9);
            Assert.Equal(0.55, AggregationWeights.LockDefaults.WMin, 9);
            Assert.Equal(0.90, AggregationWeights.LockDefaults.WFront, 9);
            Assert.Equal(0.10, AggregationWeights.LockDefaults.WRear, 9);
            Assert.Equal(0.0, AggregationWeights.LockDefaults.SlipFloorFactor, 9);
        }

        [Fact]
        public void Slip_defaults_match_the_owners_tested_values_exactly()
        {
            Assert.Equal(0.55, AggregationWeights.SlipDefaults.WMax, 9);
            Assert.Equal(0.45, AggregationWeights.SlipDefaults.WMin, 9);
            Assert.Equal(0.65, AggregationWeights.SlipDefaults.WFront, 9);
            Assert.Equal(0.35, AggregationWeights.SlipDefaults.WRear, 9);
            Assert.Equal(0.4, AggregationWeights.SlipDefaults.SlipFloorFactor, 9);
        }

        [Fact]
        public void Lock_formula_matches_hand_computation_for_a_known_input_set()
        {
            // FL=80, FR=20, RL=10, RR=60 - all four wheels distinct, no floor engaged (Lock ships 0).
            var wheels = new Corners(frontLeft: 80.0, frontRight: 20.0, rearLeft: 10.0, rearRight: 60.0);
            AggregationWeights w = AggregationWeights.LockDefaults; // WMax .45 WMin .55 WFront .90 WRear .10

            double expectedFront = 80.0 * w.WMax + 20.0 * w.WMin; // max=80 min=20
            double expectedRear = 60.0 * w.WMax + 10.0 * w.WMin;  // max=60 min=10
            double expectedLeft = 80.0 * w.WFront + 10.0 * w.WRear;  // FL*WFront + RL*WRear
            double expectedRight = 20.0 * w.WFront + 60.0 * w.WRear; // FR*WFront + RR*WRear
            double expectedAll = expectedFront * w.WFront + expectedRear * w.WRear;

            WheelAggregate result = Aggregator.Compute(wheels, w);

            Assert.Equal(expectedFront, result.Front, 9);
            Assert.Equal(expectedRear, result.Rear, 9);
            Assert.Equal(expectedLeft, result.Left, 9);
            Assert.Equal(expectedRight, result.Right, 9);
            Assert.Equal(expectedAll, result.All, 9);
        }

        [Fact]
        public void Slip_formula_matches_hand_computation_including_the_floor_when_it_does_not_engage()
        {
            // Chosen so the blended values already exceed the floor everywhere (see the dedicated
            // floor-engagement tests below for the case where it DOES change the result) - all four
            // wheels close together, no single dominant spinner.
            var wheels = new Corners(frontLeft: 50.0, frontRight: 55.0, rearLeft: 48.0, rearRight: 52.0);
            AggregationWeights w = AggregationWeights.SlipDefaults; // WMax .55 WMin .45 WFront .65 WRear .35

            double expectedFront = 55.0 * w.WMax + 50.0 * w.WMin; // max=55 min=50
            double expectedRear = 52.0 * w.WMax + 48.0 * w.WMin;  // max=52 min=48
            double expectedLeft = 50.0 * w.WFront + 48.0 * w.WRear;
            double expectedRight = 55.0 * w.WFront + 52.0 * w.WRear;
            double expectedAllPreFloor = expectedFront * w.WFront + expectedRear * w.WRear;

            // Floor check: none of these should be below Max(all four)*0.4 = 55*0.4 = 22 anyway, so the
            // floor is a no-op here - expected values are simply the blended ones.
            WheelAggregate result = Aggregator.Compute(wheels, w);

            Assert.Equal(expectedFront, result.Front, 9);
            Assert.Equal(expectedRear, result.Rear, 9);
            Assert.Equal(expectedLeft, result.Left, 9);
            Assert.Equal(expectedRight, result.Right, 9);
            Assert.Equal(expectedAllPreFloor, result.All, 9);
        }

        // ------------------------------------------------------------------------------------
        // The slip floor - engaging when one wheel dominates, not otherwise.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Slip_floor_engages_for_a_single_strongly_spinning_wheel()
        {
            // A single spinning REAR-LEFT wheel, everything else at 0 - with Slip's own shipped
            // weights (WMax .55/WMin .45, WFront .65/WRear .35, floor .4), the axle blend ALONE never
            // needs the floor (WMax=.55 already exceeds the .4 floor factor for whichever wheel is the
            // axle's own max), but a REAR wheel's contribution to Left/Right/All is weighted by WRear
            // (.35), which IS below the .4 floor factor - so a lone REAR spinner is exactly the case
            // that dilutes below the floor at those two levels.
            //   Rear  = Max(0,100)*.55 + Min(0,100)*.45 = 55.0  (axle blend alone, no floor needed)
            //   Left  = FL*.65 + RL*.35 = 0*.65 + 100*.35 = 35.0 < Max(FL,RL)*.4 = 40.0 -> FLOORED to 40.0
            //   All   = Front*.65 + Rear*.35 = 0*.65 + 55*.35 = 19.25 < Max(all four)*.4 = 40.0 -> FLOORED to 40.0
            var wheels = new Corners(frontLeft: 0.0, frontRight: 0.0, rearLeft: 100.0, rearRight: 0.0);
            AggregationWeights w = AggregationWeights.SlipDefaults;

            double leftBeforeFloor = 0.0 * w.WFront + 100.0 * w.WRear;
            double allBeforeFloor = (100.0 * w.WMax + 0.0 * w.WMin) * w.WRear; // Front=0, Rear=55*wRear
            Assert.True(leftBeforeFloor < 100.0 * w.SlipFloorFactor, "test setup should require the floor for Left");
            Assert.True(allBeforeFloor < 100.0 * w.SlipFloorFactor, "test setup should require the floor for All");

            WheelAggregate result = Aggregator.Compute(wheels, w);

            Assert.Equal(0.0, result.Front, 9); // FL=FR=0 - nothing to floor
            Assert.Equal(100.0 * w.WMax + 0.0 * w.WMin, result.Rear, 9); // = 55.0, floor (40) is not binding
            Assert.Equal(100.0 * w.SlipFloorFactor, result.Left, 9); // floored: 35.0 -> 40.0
            Assert.Equal(0.0, result.Right, 9); // FR=RR=0
            Assert.Equal(100.0 * w.SlipFloorFactor, result.All, 9); // floored: 19.25 -> 40.0
        }

        [Fact]
        public void Slip_floor_does_not_engage_when_the_blend_already_exceeds_it()
        {
            // All four wheels equal and high - blended values equal the input directly (WMax+WMin=1,
            // WFront+WRear=1 for Slip's own shipped pairs), comfortably above Max*0.4.
            var wheels = Corners.Uniform(90.0);
            AggregationWeights w = AggregationWeights.SlipDefaults;

            WheelAggregate result = Aggregator.Compute(wheels, w);

            Assert.Equal(90.0, result.All, 9);
            Assert.Equal(90.0, result.Front, 9);
            Assert.True(result.All > 90.0 * w.SlipFloorFactor, "floor should not be the binding term here");
        }

        [Fact]
        public void Lock_never_applies_a_floor_by_default()
        {
            // A single locked wheel, otherwise free wheels - Lock's shipped SlipFloorFactor is 0, so the
            // axle blend (diluted) is the final answer, unlike Slip's equivalent scenario above.
            var wheels = new Corners(frontLeft: 100.0, frontRight: 0.0, rearLeft: 0.0, rearRight: 0.0);
            AggregationWeights w = AggregationWeights.LockDefaults;

            WheelAggregate result = Aggregator.Compute(wheels, w);

            double expectedFront = 100.0 * w.WMax + 0.0 * w.WMin; // = 45.0, no floor lifts this
            Assert.Equal(expectedFront, result.Front, 9);
            Assert.True(result.Front < 100.0, "Lock must not floor a single locked wheel by default");
        }

        // ------------------------------------------------------------------------------------
        // Continuity across a crossover - a weighted Max/Min blend must not step, unlike a bare Max.
        //
        // Both a weighted Max/Min blend AND a bare Max are continuous in VALUE as two inputs cross (no
        // jump) - Math.Max itself is a continuous, if not smooth, function of its arguments. The
        // difference that actually matters (and that a shaker feels as a "click") is the KINK at the
        // crossover: the SLOPE either side of the crossover changes by |WMax-WMin| for the weighted
        // blend (a small number for the owner's shipped weights, e.g. |0.55-0.45| = 0.10 for Slip) but
        // by a full 1.0 for a bare Max (slope 0 on the losing side, 1 on the winning side) - roughly
        // 10x sharper. This is measured directly below via the discrete second difference
        // f(b-h) + f(b+h) - 2*f(b), which is exactly h*(slope_after - slope_before) at the crossover:
        // small and bounded for the blend, an order of magnitude larger for a bare Max.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Axle_blend_has_a_small_bounded_kink_at_the_crossover()
        {
            // MUTATION (c) target: replace the blend with a bare Math.Max and this test fails - see
            // Bare_Max_has_a_much_larger_kink_at_the_same_crossover below for the direct comparison.
            AggregationWeights w = AggregationWeights.SlipDefaults; // WMax .55 WMin .45
            const double b = 50.0;
            const double h = 1.0;

            double before = Aggregator.Compute(new Corners(b - h, b, 0.0, 0.0), w).Front;
            double at = Aggregator.Compute(new Corners(b, b, 0.0, 0.0), w).Front;
            double after = Aggregator.Compute(new Corners(b + h, b, 0.0, 0.0), w).Front;

            double kink = Math.Abs(before + after - 2.0 * at);

            // Exactly h * |WMax - WMin| for this blend (worked by hand: before=49.55, at=50.0,
            // after=50.55 -> kink = |49.55+50.55-100.0| = 0.10).
            Assert.Equal(h * Math.Abs(w.WMax - w.WMin), kink, 9);
            Assert.True(kink < 0.5, $"axle blend's kink at the crossover must be small, got {kink}");
        }

        [Fact]
        public void Bare_Max_has_a_much_larger_kink_at_the_same_crossover()
        {
            // Direct proof the test above is actually discriminating (mutation (c)'s target): the SAME
            // three points, combined with a bare Math.Max instead of the weighted blend, produce a kink
            // an order of magnitude larger - exactly h * |1 - 0| = h, since Max's own slope is 0 on the
            // losing side and 1 on the winning side.
            const double b = 50.0;
            const double h = 1.0;

            double before = Math.Max(b - h, b);
            double at = Math.Max(b, b);
            double after = Math.Max(b + h, b);

            double bareMaxKink = Math.Abs(before + after - 2.0 * at);
            double blendKink = Math.Abs(
                Aggregator.Compute(new Corners(b - h, b, 0.0, 0.0), AggregationWeights.SlipDefaults).Front
                + Aggregator.Compute(new Corners(b + h, b, 0.0, 0.0), AggregationWeights.SlipDefaults).Front
                - 2.0 * Aggregator.Compute(new Corners(b, b, 0.0, 0.0), AggregationWeights.SlipDefaults).Front);

            Assert.Equal(h, bareMaxKink, 9);
            Assert.True(bareMaxKink > blendKink * 5.0,
                $"a bare Max's kink ({bareMaxKink}) must be much larger than the blend's own ({blendKink}) " +
                "for the continuity test above to be a real, discriminating guard against mutation (c)");
        }

        // ------------------------------------------------------------------------------------
        // Bounds - every output stays within 0-100 for all admissible (non-negative) weights, however
        // extreme, and for out-of-range (150/-20) input.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Output_is_clamped_to_0_100_under_extreme_weights()
        {
            var extreme = new AggregationWeights(wMax: 50.0, wMin: 50.0, wFront: 50.0, wRear: 50.0, slipFloorFactor: 1.0);
            var wheels = Corners.Uniform(100.0);

            WheelAggregate result = Aggregator.Compute(wheels, extreme);

            Assert.Equal(100.0, result.Front, 9);
            Assert.Equal(100.0, result.Rear, 9);
            Assert.Equal(100.0, result.Left, 9);
            Assert.Equal(100.0, result.Right, 9);
            Assert.Equal(100.0, result.All, 9);
        }

        [Fact]
        public void Input_out_of_0_100_range_is_clamped_before_blending()
        {
            var wheels = new Corners(150.0, -20.0, 0.0, 0.0);
            AggregationWeights w = AggregationWeights.LockDefaults;

            // Clamped first: 100 and 0.
            double expectedFront = 100.0 * w.WMax + 0.0 * w.WMin;
            WheelAggregate result = Aggregator.Compute(wheels, w);

            Assert.Equal(expectedFront, result.Front, 9);
            Assert.InRange(result.Front, 0.0, 100.0);
            Assert.InRange(result.All, 0.0, 100.0);
        }

        [Fact]
        public void Zero_weights_produce_a_bounded_zero_result_not_a_crash()
        {
            var zero = new AggregationWeights(0.0, 0.0, 0.0, 0.0, 0.0);
            WheelAggregate result = Aggregator.Compute(Corners.Uniform(80.0), zero);

            Assert.Equal(0.0, result.All, 9);
            Assert.InRange(result.All, 0.0, 100.0);
        }

        // ------------------------------------------------------------------------------------
        // Weights that do not sum to 1 within a pair - not auto-normalised, but still bounded.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Weights_not_summing_to_one_are_not_silently_rescaled()
        {
            // WMax+WMin = 1.5, not 1.0 - the blend is deliberately NOT renormalised back to a convex
            // combination; the raw (larger) result is what a driver typing these numbers gets, only
            // bounded by the final 0-100 clamp.
            var w = new AggregationWeights(wMax: 1.0, wMin: 0.5, wFront: 1.0, wRear: 0.0, slipFloorFactor: 0.0);
            var wheels = new Corners(40.0, 40.0, 40.0, 40.0);

            // Front = Max(40,40)*1.0 + Min(40,40)*0.5 = 40 + 20 = 60 (NOT 40, which a normalised/mean
            // blend would give for two equal inputs).
            WheelAggregate result = Aggregator.Compute(wheels, w);
            Assert.Equal(60.0, result.Front, 9);
        }

        // ------------------------------------------------------------------------------------
        // Uniform input passes through unchanged for both channels' shipped defaults (both weight
        // pairs sum to exactly 1.0 for both channels) - this is what keeps a genuinely car-level signal
        // (e.g. this plugin's own Lock algorithm, which has no per-wheel term at all) from being
        // distorted by the aggregation stage.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(37.5)]
        [InlineData(100.0)]
        public void Uniform_four_wheel_input_passes_through_unchanged_for_lock_defaults(double value)
        {
            WheelAggregate result = Aggregator.Compute(Corners.Uniform(value), AggregationWeights.LockDefaults);
            Assert.Equal(value, result.Front, 9);
            Assert.Equal(value, result.Rear, 9);
            Assert.Equal(value, result.Left, 9);
            Assert.Equal(value, result.Right, 9);
            Assert.Equal(value, result.All, 9);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(37.5)]
        [InlineData(100.0)]
        public void Uniform_four_wheel_input_passes_through_unchanged_for_slip_defaults(double value)
        {
            // Slip's floor cannot change this either: Max(participating)*0.4 <= value whenever value
            // >= 0 and the blend already equals value exactly (0.4 <= 1.0).
            WheelAggregate result = Aggregator.Compute(Corners.Uniform(value), AggregationWeights.SlipDefaults);
            Assert.Equal(value, result.Front, 9);
            Assert.Equal(value, result.Rear, 9);
            Assert.Equal(value, result.Left, 9);
            Assert.Equal(value, result.Right, 9);
            Assert.Equal(value, result.All, 9);
        }

        // ------------------------------------------------------------------------------------
        // AggregationWeights' own bounds enforcement.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void AggregationWeights_clamps_negative_weights_to_zero()
        {
            var w = new AggregationWeights(-5.0, -1.0, -2.0, -3.0, -0.5);
            Assert.Equal(0.0, w.WMax, 9);
            Assert.Equal(0.0, w.WMin, 9);
            Assert.Equal(0.0, w.WFront, 9);
            Assert.Equal(0.0, w.WRear, 9);
            Assert.Equal(0.0, w.SlipFloorFactor, 9);
        }

        [Fact]
        public void AggregationWeights_clamps_slip_floor_factor_to_0_1()
        {
            var w = new AggregationWeights(1.0, 1.0, 1.0, 1.0, 2.5);
            Assert.Equal(1.0, w.SlipFloorFactor, 9);
        }

        [Fact]
        public void AggregationWeights_treats_non_finite_input_as_zero()
        {
            var w = new AggregationWeights(double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.NaN);
            Assert.Equal(0.0, w.WMax, 9);
            Assert.Equal(0.0, w.WMin, 9);
            Assert.Equal(0.0, w.WFront, 9);
            Assert.Equal(0.0, w.WRear, 9);
            Assert.Equal(0.0, w.SlipFloorFactor, 9);
        }
    }
}

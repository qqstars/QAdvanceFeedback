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
            // REVISED (docs\slip-source-consistency-report.md - a second round of owner seat-testing):
            // WMax/WMin 0.45/0.55 -> 0.75/0.25 (the stronger wheel now dominates the axle blend, rather
            // than being weighted slightly LESS than the weaker one); WFront/WRear UNCHANGED at 0.90/0.10.
            // MUTATION (a) target: if WMax/WMin (or WFront/WRear) were ever swapped, this test's
            // hand-computed expectations below would fail.
            Assert.Equal(0.75, AggregationWeights.LockDefaults.WMax, 9);
            Assert.Equal(0.25, AggregationWeights.LockDefaults.WMin, 9);
            Assert.Equal(0.90, AggregationWeights.LockDefaults.WFront, 9);
            Assert.Equal(0.10, AggregationWeights.LockDefaults.WRear, 9);
            Assert.Equal(0.0, AggregationWeights.LockDefaults.SlipFloorFactor, 9);
        }

        [Fact]
        public void Slip_defaults_match_the_owners_tested_values_exactly()
        {
            // REVISED (docs\slip-source-consistency-report.md): WMax/WMin 0.55/0.45 -> 0.85/0.15;
            // WFront/WRear FLIPPED 0.65/0.35 -> 0.45/0.55 (rear/driven-axle bias); floor 0.4 -> 0.70.
            Assert.Equal(0.85, AggregationWeights.SlipDefaults.WMax, 9);
            Assert.Equal(0.15, AggregationWeights.SlipDefaults.WMin, 9);
            Assert.Equal(0.45, AggregationWeights.SlipDefaults.WFront, 9);
            Assert.Equal(0.55, AggregationWeights.SlipDefaults.WRear, 9);
            Assert.Equal(0.70, AggregationWeights.SlipDefaults.SlipFloorFactor, 9);
        }

        [Fact]
        public void Lock_formula_matches_hand_computation_for_a_known_input_set()
        {
            // FL=80, FR=20, RL=10, RR=60 - all four wheels distinct, no floor engaged (Lock ships 0).
            var wheels = new Corners(frontLeft: 80.0, frontRight: 20.0, rearLeft: 10.0, rearRight: 60.0);
            AggregationWeights w = AggregationWeights.LockDefaults; // WMax .75 WMin .25 WFront .90 WRear .10

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
            AggregationWeights w = AggregationWeights.SlipDefaults; // WMax .85 WMin .15 WFront .45 WRear .55

            double expectedFront = 55.0 * w.WMax + 50.0 * w.WMin; // max=55 min=50
            double expectedRear = 52.0 * w.WMax + 48.0 * w.WMin;  // max=52 min=48
            double expectedLeft = 50.0 * w.WFront + 48.0 * w.WRear;
            double expectedRight = 55.0 * w.WFront + 52.0 * w.WRear;
            double expectedAllPreFloor = expectedFront * w.WFront + expectedRear * w.WRear;

            // Floor check (REVISED, docs\slip-source-consistency-report.md - floor now 0.70, not 0.4):
            // none of these should be below Max(all four)*0.70 = 55*0.70 = 38.5 anyway, so the floor is a
            // no-op here - expected values are simply the blended ones.
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
            // A single spinning REAR-LEFT wheel, everything else at 0 - REVISED for the owner's second
            // round of seat-testing (docs\slip-source-consistency-report.md: WMax .85/WMin .15, WFront
            // .45/WRear .55, floor .70). The axle blend ALONE still never needs the floor (WMax=.85
            // already exceeds the .70 floor factor for whichever wheel is the axle's own max), and now
            // even the REAR wheel's contribution to Rear itself clears the floor comfortably - but Left
            // and All (both weighted by WRear=.55, below the .70 floor factor) still dilute below it.
            //   Rear  = Max(0,100)*.85 + Min(0,100)*.15 = 85.0  (axle blend alone, no floor needed: 85 > 70)
            //   Left  = FL*.45 + RL*.55 = 0*.45 + 100*.55 = 55.0 < Max(FL,RL)*.70 = 70.0 -> FLOORED to 70.0
            //   All   = Front*.45 + Rear*.55 = 0*.45 + 85*.55 = 46.75 < Max(all four)*.70 = 70.0 -> FLOORED to 70.0
            var wheels = new Corners(frontLeft: 0.0, frontRight: 0.0, rearLeft: 100.0, rearRight: 0.0);
            AggregationWeights w = AggregationWeights.SlipDefaults;

            double rearBeforeFloor = 100.0 * w.WMax + 0.0 * w.WMin;
            double leftBeforeFloor = 0.0 * w.WFront + 100.0 * w.WRear;
            double allBeforeFloor = rearBeforeFloor * w.WRear; // Front=0, Rear=rearBeforeFloor*wRear
            Assert.True(rearBeforeFloor > 100.0 * w.SlipFloorFactor, "test setup should NOT require the floor for Rear");
            Assert.True(leftBeforeFloor < 100.0 * w.SlipFloorFactor, "test setup should require the floor for Left");
            Assert.True(allBeforeFloor < 100.0 * w.SlipFloorFactor, "test setup should require the floor for All");

            WheelAggregate result = Aggregator.Compute(wheels, w);

            Assert.Equal(0.0, result.Front, 9); // FL=FR=0 - nothing to floor
            Assert.Equal(rearBeforeFloor, result.Rear, 9); // = 85.0, floor (70) is not binding
            Assert.Equal(100.0 * w.SlipFloorFactor, result.Left, 9); // floored: 55.0 -> 70.0
            Assert.Equal(0.0, result.Right, 9); // FR=RR=0
            Assert.Equal(100.0 * w.SlipFloorFactor, result.All, 9); // floored: 46.75 -> 70.0
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

            double expectedFront = 100.0 * w.WMax + 0.0 * w.WMin; // = 75.0, no floor lifts this
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
        // blend but by a full 1.0 for a bare Max (slope 0 on the losing side, 1 on the winning side).
        // REVISED (docs\slip-source-consistency-report.md - a second round of owner seat-testing pushed
        // Slip's own WMax/WMin from 0.55/0.45 to 0.85/0.15): the blend's own kink is now |0.85-0.15| =
        // 0.70 - still STRICTLY smaller than a bare Max's 1.0 (never a full step), but no longer "an
        // order of magnitude" smaller the way the old 0.10 was; the owner's own more max-dominant
        // weights are a deliberate trade-off (favour the strongest wheel far more) that narrows, but does
        // not remove, this margin. This is measured directly below via the discrete second difference
        // f(b-h) + f(b+h) - 2*f(b), which is exactly h*(slope_after - slope_before) at the crossover.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Axle_blend_has_a_bounded_kink_smaller_than_a_bare_max_at_the_crossover()
        {
            // MUTATION (c) target: replace the blend with a bare Math.Max and this test fails - see
            // Bare_Max_has_a_larger_kink_at_the_same_crossover below for the direct comparison.
            AggregationWeights w = AggregationWeights.SlipDefaults; // WMax .85 WMin .15 (REVISED)
            const double b = 50.0;
            const double h = 1.0;

            double before = Aggregator.Compute(new Corners(b - h, b, 0.0, 0.0), w).Front;
            double at = Aggregator.Compute(new Corners(b, b, 0.0, 0.0), w).Front;
            double after = Aggregator.Compute(new Corners(b + h, b, 0.0, 0.0), w).Front;

            double kink = Math.Abs(before + after - 2.0 * at);

            // Exactly h * |WMax - WMin| for this blend (worked by hand: before=49.3, at=50.0, after=50.7
            // -> kink = |49.3+50.7-100.0| = 0.70).
            Assert.Equal(h * Math.Abs(w.WMax - w.WMin), kink, 9);
            Assert.True(kink < h, $"axle blend's kink at the crossover must still be strictly smaller than a bare Max's (h={h}), got {kink}");
        }

        [Fact]
        public void Bare_Max_has_a_larger_kink_at_the_same_crossover()
        {
            // Direct proof the test above is actually discriminating (mutation (c)'s target): the SAME
            // three points, combined with a bare Math.Max instead of the weighted blend, produce a
            // strictly larger kink - exactly h * |1 - 0| = h, since Max's own slope is 0 on the losing
            // side and 1 on the winning side. REVISED (docs\slip-source-consistency-report.md): with the
            // owner's newer, more max-dominant Slip weights the margin is real but modest (1.0 vs 0.70,
            // ~1.43x) rather than the old ~10x - the assertion bound below reflects that honestly.
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
            Assert.True(bareMaxKink > blendKink * 1.2,
                $"a bare Max's kink ({bareMaxKink}) must still be meaningfully larger than the blend's own ({blendKink}) " +
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

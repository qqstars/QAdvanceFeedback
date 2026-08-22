using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// OBJECTIVE A - AGGREGATED-ALL SCALE (docs\all-channel-scale-and-surface-gap-report.md): the
    /// owner's own proposal - compute the All-channel value from the per-wheel source values using the
    /// EXISTING aggregation weights, then apply the ONE resulting scale uniformly to All/Front/Rear/
    /// Left/Right/each wheel, rather than distributing the car-level severity by a flat mean-relative
    /// proportion and then re-aggregating with axle/side weights that are NOT flat (the exact mechanism
    /// docs\delta-g-band-mapping-report.md Part 3 diagnosed as inflating published Lock `.All` to 12.83%
    /// time-above-80 against a car-level severity of only 2.04%). MEASURED (see the report): this wins
    /// for Lock (published time-above-80 falls to ~1.9%, matching car-level severity almost exactly) but
    /// is a wash-to-regression for Slip (its OWN pre-existing, owner-tested SlipFloorFactor=0.70 floor
    /// stage inside <see cref="Aggregator.Compute"/> pins published All at exactly 70.0 whenever the
    /// large scale this mechanism needs for Slip clamps even one wheel to 100, defeating the fix and
    /// adding large-magnitude per-wheel volatility) - so only <see cref="NormalizedWheelLockSlipEngine.LockUsesAggregatedAllScale"/>
    /// ships true; <see cref="NormalizedWheelLockSlipEngine.SlipUsesAggregatedAllScale"/> stays false.
    /// </summary>
    public class AggregatedAllScaleTests
    {
        private static ITelemetrySample BrakingSample(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        [Fact]
        public void LockUsesAggregatedAllScale_is_shipped_true_and_SlipUsesAggregatedAllScale_is_shipped_false()
        {
            // Pins the actual shipped decision (see this class's own remarks) - a future edit that
            // flips either constant without updating this test (and the report) is caught here.
            Assert.True(NormalizedWheelLockSlipEngine.LockUsesAggregatedAllScale);
            Assert.False(NormalizedWheelLockSlipEngine.SlipUsesAggregatedAllScale);
        }

        [Fact]
        public void Front_biased_raw_no_longer_inflates_published_LockAll_far_above_car_level_severity()
        {
            // The exact mechanism docs\delta-g-band-mapping-report.md Part 3 diagnosed: LockDefaults'
            // own front-axle bias (0.75/0.25 axle, 0.90/0.10 front/rear) amplifies a front-loaded raw
            // pattern (real F1 braking's own weight-transfer signature) when the OLD flat-mean-relative
            // proportion is combined with those same weights a SECOND time. A car steadily at its own
            // physical limit (car-level severity ~80) with a front-biased raw pattern used to publish
            // LockAll clamped at 100 (severity * ~1.46, see the report's own worked mutation) - now it
            // must stay close to the car-level severity itself.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            var frontBiasedRaw = new Corners(100.0, 80.0, 40.0, 40.0);
            engine.Compute(BrakingSample(peak), frontBiasedRaw, Corners.Zero);
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(peak), frontBiasedRaw, Corners.Zero);

            Assert.True(result.LockAll >= 70.0, $"expected LockAll near the car-level severity (~80), got {result.LockAll}");
            Assert.True(Math.Abs(result.LockAll - engine.LockCarLevelSeverity) < 1.0,
                $"published LockAll ({result.LockAll}) should now closely track car-level severity ({engine.LockCarLevelSeverity}) - the whole point of this task");
        }

        [Fact]
        public void Aggregating_the_scaled_per_wheel_output_reproduces_car_level_severity_by_construction()
        {
            // Direct algebraic confirmation of Defect A's own fix (docs\v1068-four-range-report.md): All
            // is now PUBLISHED as car-level severity directly (not re-derived from the scaled, possibly
            // CLAMPED per-wheel Corners a second time) - this is the invariant that actually matters, and
            // it holds unconditionally, regardless of whether any individual wheel's own scaled value
            // needed to clamp.
            //
            // RE-EXPRESSED (v1068 correction - "A REAL DEFECT in how Feature C was wired"): this test used
            // to ALSO assert `Aggregator.Compute(result.LockWheels, weights).All == result.LockAll` - a
            // SEPARATE, independent re-aggregation of the PUBLISHED (already scaled-and-clamped) per-wheel
            // values. That assertion is exactly the double-aggregation clamp bug Defect A's own fix
            // deliberately bypasses for the PUBLISHED All (see NormalizedWheelLockSlipEngine.Compute's own
            // remarks) - it is generally FALSE whenever any one wheel's own scaled value needed to clamp
            // (as it now does here, since Lock's own severity is source-driven again, matching 1.0.6.3, and
            // can require a large allScale), and asserting it would re-pin the very bug this task fixes.
            // Removed; the ONE invariant that must hold (and does, by construction) is kept below.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 3.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            var raw = new Corners(60.0, 80.0, 60.0, 40.0); // the owner's own worked-example wheel pattern
            engine.Compute(BrakingSample(peak * 0.9), raw, Corners.Zero);
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(peak * 0.9), raw, Corners.Zero);

            Assert.Equal(engine.LockCarLevelSeverity, result.LockAll, 3);
        }

        [Fact]
        public void Per_wheel_ranking_is_preserved_under_the_uniform_scale_exactly_as_under_the_old_proportion()
        {
            // Scaling all four wheels by the SAME positive constant this frame can never change which
            // wheel is largest - the per-wheel discrimination acceptance bar does not depend on which
            // formula produced the constant.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            var raw = new Corners(100.0, 40.0, 40.0, 40.0);
            engine.Compute(BrakingSample(peak), raw, Corners.Zero);
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(peak), raw, Corners.Zero);

            Assert.True(result.LockWheels.FrontLeft > result.LockWheels.FrontRight);
            Assert.Equal(result.LockWheels.FrontRight, result.LockWheels.RearLeft, 3);
            Assert.Equal(result.LockWheels.RearLeft, result.LockWheels.RearRight, 3);
        }

        [Fact]
        public void Slip_keeps_the_old_mean_relative_proportion_unchanged()
        {
            // SlipUsesAggregatedAllScale ships false (see this class's own remarks) - Slip's per-wheel
            // output for a non-uniform raw pattern must still equal severity * (bw_i / flatMean(bw)),
            // NOT the new aggregated-scale formula.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 3.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Zero, Corners.Uniform(50.0));

            var raw = new Corners(80.0, 20.0, 20.0, 20.0);
            engine.Compute(ThrottleSample(peak), Corners.Zero, raw);
            NormalizedWheelLockSlipResult result = engine.Compute(ThrottleSample(peak), Corners.Zero, raw);

            double flatMean = (80.0 + 20.0 + 20.0 + 20.0) / 4.0;
            double expectedFL = ClampMath.To0100(engine.SlipCarLevelSeverity * (80.0 / flatMean));
            Assert.Equal(expectedFL, result.SlipWheels.FrontLeft, 3);
        }

        private static ITelemetrySample ThrottleSample(double gMagnitude, double throttlePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 101.0, longitudinalG: gMagnitude, throttlePercent: throttlePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        [Fact]
        public void MUTATION_reverting_Lock_to_the_old_proportion_reproduces_the_published_All_discrepancy()
        {
            // MUTATION EVIDENCE (the brief's own requirement): force the OLD mean-relative-proportion
            // formula for Lock (mirroring LockUsesAggregatedAllScale=false) and confirm the exact
            // discrepancy this task closes REAPPEARS on the same front-biased scenario - i.e. this test
            // demonstrates what would happen without this task's fix, pinned as a permanent regression
            // guard even though the real toggle is a compile-time constant, not a runtime one.
            //
            // Warmup raw RAISED to 90.0 (v1068 correction - "A REAL DEFECT in how Feature C was wired"):
            // Lock's own severity is source-driven again (matching 1.0.6.3) - a warmup at 50.0 taught a
            // learned ceiling (Smax~=50) far below the front-biased pattern's own ~89.5 aggregated native
            // reading, saturating severity at the 100 clamp and leaving no headroom for the OLD formula's
            // own inflation to show up against. 90.0 keeps severity comfortably below 100 (~79.6) so the
            // original diagnostic (the OLD formula publishes All well ABOVE car-level severity) is
            // preserved exactly as this test intends.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(90.0), Corners.Zero);

            var frontBiasedRaw = new Corners(100.0, 80.0, 40.0, 40.0);
            engine.Compute(BrakingSample(peak), frontBiasedRaw, Corners.Zero);
            engine.Compute(BrakingSample(peak), frontBiasedRaw, Corners.Zero);

            double severity = engine.LockCarLevelSeverity;
            double flatMean = (100.0 + 80.0 + 40.0 + 40.0) / 4.0;
            var oldWheels = new Corners(
                ClampMath.To0100(severity * (100.0 / flatMean)), ClampMath.To0100(severity * (80.0 / flatMean)),
                ClampMath.To0100(severity * (40.0 / flatMean)), ClampMath.To0100(severity * (40.0 / flatMean)));
            double oldPublishedAll = Aggregator.Compute(oldWheels, AggregationWeights.LockDefaults).All;

            Assert.True(oldPublishedAll - severity > 10.0,
                $"the OLD proportion formula should reproduce a large published-All-over-severity gap on this front-biased pattern (severity={severity}, oldPublishedAll={oldPublishedAll})");
        }
    }
}

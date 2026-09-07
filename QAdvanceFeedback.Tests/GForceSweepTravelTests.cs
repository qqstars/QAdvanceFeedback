using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE SWEEP MUST ACTUALLY TRAVEL (owner, 2026-09-06, from real hardware): "drag the slide bar
    /// quickly from 0 to dec 100 and the Low channel is almost never over 25, Rear almost never over 50.
    /// Only the Front is animated from around 70 into 100. That is INCORRECT."
    /// <para/>
    /// The cause was a race between two mechanisms that had never been considered together: the sustain
    /// LEVEL rises with its own 0.15 s time constant, while the stage progress sweeps in as little as
    /// 200 ms. At the instant the far pad's keyframe is at its peak, the level multiplying it is still
    /// near zero; by the time the level arrives, the sweep has moved on and collapsed that pad's shape.
    /// So the leading pads could never show the travel - only the terminal, which peaks last, ever
    /// looked right. See <c>GForceEngine.AnimationLevel</c>.
    /// </summary>
    public class GForceSweepTravelTests
    {
        private readonly ITestOutputHelper _out;
        public GForceSweepTravelTests(ITestOutputHelper output) { _out = output; }

        private const double AccelMax = 1.0;
        private const double DecelMax = 1.0;
        private const double FrameSeconds = 1.0 / 60.0;

        /// <summary>Braking: speed falling, brake pedal applied. Accelerating: the mirror.</summary>
        private static TelemetrySample Sample(double longG, bool braking)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: braking ? 101.0 : 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: braking ? 100.0 : 101.0,
                longitudinalG: longG,
                brakePercent: braking ? 90.0 : 0.0,
                throttlePercent: braking ? 0.0 : 90.0);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(FrameSeconds));
        }

        private static GForceEngine Engine() => new GForceEngine
        {
            // The shake is a separate concern; keep it out of these numbers entirely.
            IntegrateWheelLockAndSlip = false,
        };

        /// <summary>Peak value each of the braking chain's three pads reaches over a run of frames.</summary>
        private (double low, double rear, double front) BrakePeaks(double targetG, int frames)
        {
            GForceEngine engine = Engine();
            double low = 0, rear = 0, front = 0;

            for (int i = 0; i < frames; i++)
            {
                GForceOutput r = engine.Compute(Sample(-targetG, braking: true), AccelMax, DecelMax);
                low = Math.Max(low, r.BackLowLeft ?? 0.0);
                rear = Math.Max(rear, r.BottomRearLeft ?? 0.0);
                front = Math.Max(front, r.BottomFrontLeft ?? 0.0);
            }

            _out.WriteLine($"target {targetG:F2}g over {frames} frames -> Low {low:F1}, Rear {rear:F1}, Front {front:F1}");
            return (low, rear, front);
        }

        [Fact]
        public void A_hard_stamp_lights_the_far_pad_fully_not_just_a_quarter()
        {
            // THE REGRESSION, stated as the owner measured it. A step straight to a full-strength brake
            // must put real level under BackLow (the braking chain's FAR pad) and BottomRear (its MIDDLE
            // pad), not leave them stranded at their resting shoulders of 25 and 50.
            (double low, double rear, double front) = BrakePeaks(targetG: 1.0, frames: 40);

            Assert.True(low > 90.0, $"the far pad should light up on a hard stamp, peaked at {low:F1}");
            Assert.True(rear > 90.0, $"the middle pad should light up on a hard stamp, peaked at {rear:F1}");
            Assert.True(front > 95.0, $"the terminal pad must still finish at full, peaked at {front:F1}");
        }

        [Fact]
        public void The_opening_pad_now_holds_at_its_peak_instead_of_only_touching_it()
        {
            // OWNER, 2026-09-06: "hold on the start channel maximum a little bit longer ... I assume
            // right now the start channel remaining half of the maximum duration comparing to the
            // mid-channel". THAT READING WAS CORRECT. The far pad was at its peak for a single instant
            // at p=0 and only ever fell from there, while the MIDDLE pad rises INTO its peak and then
            // falls away from it - so the middle pad spent roughly twice as long near maximum as the pad
            // that opens the animation. StartHoldFraction gives the opening keyframe a real plateau.
            //
            // Measured as frames spent within 2 points of that pad's own maximum, on a hard stamp.
            GForceEngine engine = Engine();
            var far = new System.Collections.Generic.List<double>();
            var mid = new System.Collections.Generic.List<double>();

            for (int i = 0; i < 60; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax);
                far.Add(r.BackLowLeft ?? 0.0);      // braking's FAR pad
                mid.Add(r.BottomRearLeft ?? 0.0);   // braking's MIDDLE pad
            }

            double farMax = 0.0, midMax = 0.0;
            foreach (double v in far) farMax = Math.Max(farMax, v);
            foreach (double v in mid) midMax = Math.Max(midMax, v);

            int farAtPeak = 0, midAtPeak = 0;
            foreach (double v in far) if (v >= farMax - 2.0) farAtPeak++;
            foreach (double v in mid) if (v >= midMax - 2.0) midAtPeak++;

            _out.WriteLine($"frames at peak - far {farAtPeak} (max {farMax:F1}), mid {midAtPeak} (max {midMax:F1})");

            Assert.True(farAtPeak >= 3,
                $"the opening pad should HOLD at its peak, not merely touch it ({farAtPeak} frames)");
            Assert.True(farAtPeak >= midAtPeak,
                $"the opening pad's time at maximum should now be at least the middle pad's " +
                $"(far {farAtPeak} vs mid {midAtPeak})");
        }

        [Fact]
        public void The_same_hard_stamp_still_comes_to_rest_on_the_configured_sustains()
        {
            // The travel must not cost the resting shape: after the sweep the pads settle on the
            // driver's own sustain fractions (25 / 50 / 100 at the shipped defaults). This is what stops
            // "make the travel visible" from turning into "everything is loud forever".
            GForceEngine engine = Engine();
            GForceOutput r = GForceOutput.Empty;
            for (int i = 0; i < 200; i++) r = engine.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax);

            _out.WriteLine($"at rest -> Low {r.BackLowLeft:F1}, Rear {r.BottomRearLeft:F1}, Front {r.BottomFrontLeft:F1}");

            Assert.Equal(25.0, r.BackLowLeft ?? 0.0, 0);
            Assert.Equal(50.0, r.BottomRearLeft ?? 0.0, 0);
            Assert.Equal(100.0, r.BottomFrontLeft ?? 0.0, 0);
        }

        [Fact]
        public void A_gentle_build_stays_soft_where_a_stamp_is_loud()
        {
            // The other half of the owner's requirement: "if dec smoothly ... the low and rear should got
            // not too high value so the animation feels softer". Same destination, different journey -
            // which is the whole point of scaling the sweep by the PEAK OF THE TARGET rather than by a
            // fixed shape.
            GForceEngine gentle = Engine();
            double gentleLow = 0.0, gentleRear = 0.0;

            // Ramp 0 -> 0.5g over 2 seconds, well slower than the sweep itself.
            for (int i = 0; i < 120; i++)
            {
                double g = 0.5 * (i / 120.0);
                GForceOutput r = gentle.Compute(Sample(-g, braking: true), AccelMax, DecelMax);
                gentleLow = Math.Max(gentleLow, r.BackLowLeft ?? 0.0);
                gentleRear = Math.Max(gentleRear, r.BottomRearLeft ?? 0.0);
            }
            _out.WriteLine($"gentle build -> Low {gentleLow:F1}, Rear {gentleRear:F1}");

            (double stampLow, double stampRear, _) = BrakePeaks(targetG: 0.5, frames: 40);

            Assert.True(gentleLow < stampLow,
                $"a gentle build must read softer on the far pad than a stamp to the same g " +
                $"(gentle {gentleLow:F1} vs stamp {stampLow:F1})");
            Assert.True(gentleRear < stampRear,
                $"...and on the middle pad too (gentle {gentleRear:F1} vs stamp {stampRear:F1})");
        }

        [Fact]
        public void Acceleration_travels_the_same_way_through_its_own_chain()
        {
            // "Do the same thing for the acc-G as well." The accelerating chain is BottomRear (far) ->
            // BackLow (middle) -> BackTop (terminal), and it shares the mechanism outright - this guards
            // that it really is shared rather than braking-only.
            GForceEngine engine = Engine();
            double rear = 0, low = 0, top = 0;

            for (int i = 0; i < 40; i++)
            {
                GForceOutput r = engine.Compute(Sample(1.0, braking: false), AccelMax, DecelMax);
                rear = Math.Max(rear, r.BottomRearLeft ?? 0.0);
                low = Math.Max(low, r.BackLowLeft ?? 0.0);
                top = Math.Max(top, r.BackTopLeft ?? 0.0);
            }
            _out.WriteLine($"accel stamp -> Rear {rear:F1}, Low {low:F1}, Top {top:F1}");

            Assert.True(rear > 90.0, $"acceleration's far pad should light up too, peaked at {rear:F1}");
            Assert.True(low > 90.0, $"acceleration's middle pad should light up too, peaked at {low:F1}");
            Assert.True(top > 95.0, $"acceleration's terminal must still finish at full, peaked at {top:F1}");
        }

        [Fact]
        public void The_travelling_keyframes_use_fixed_shoulders_not_the_configured_sustains()
        {
            // Owner's specification: 100/50/25 -> 50/100/50 -> 25/50/100, where only the LAST triple
            // reads the configured sustains. Retuning a sustain must therefore change where the
            // animation comes to REST without flattening the travel on the way there.
            GForceEngine tuned = Engine();
            tuned.BrakeBackLowSustainFraction = 0.80;     // a far-pad sustain far above the fixed shoulder
            tuned.BrakeBottomRearSustainFraction = 0.90;

            GForceOutput r = GForceOutput.Empty;
            for (int i = 0; i < 200; i++) r = tuned.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax);

            // The RESTING shape follows the retuned sustains...
            Assert.Equal(80.0, r.BackLowLeft ?? 0.0, 0);
            Assert.Equal(90.0, r.BottomRearLeft ?? 0.0, 0);

            // ...while the terminal is still the true, unscaled ceiling.
            Assert.Equal(100.0, r.BottomFrontLeft ?? 0.0, 0);
        }
    }
}

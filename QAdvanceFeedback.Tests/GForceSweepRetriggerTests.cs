using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// RE-ARMING THE SWEEP MID-BRAKE (owner, 2026-09-06, from real driving): "if I get dec G-Force, even
    /// with a small value, for a while, even though I get a quick dec later, the quick dec animation will
    /// NOT be triggered."
    /// <para/>
    /// Cause: <c>AdvanceStageProgress</c> only ever reset progress when the CHAIN went inactive. Trail a
    /// little brake down a straight and the sweep completes; every later stab then found progress already
    /// at 1 and produced no travel at all. The fix re-arms a COMPLETED sweep on a fast RISE, with a
    /// threshold derived from the car's own maximum so it is not sensitive to ordinary modulation.
    /// </summary>
    public class GForceSweepRetriggerTests
    {
        private readonly ITestOutputHelper _out;
        public GForceSweepRetriggerTests(ITestOutputHelper output) { _out = output; }

        private const double AccelMax = 1.0;
        private const double DecelMax = 1.0;
        private const double FrameSeconds = 1.0 / 60.0;

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

        private static GForceEngine Engine() => new GForceEngine { IntegrateWheelLockAndSlip = false };

        /// <summary>Widest travel the braking chain's FAR pad shows over <paramref name="frames"/>.</summary>
        private static double FarPadTravel(GForceEngine engine, double g, bool braking, int frames)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < frames; i++)
            {
                GForceOutput r = engine.Compute(Sample(braking ? -g : g, braking), AccelMax, DecelMax);
                double pad = (braking ? r.BackLowLeft : r.BottomRearLeft) ?? 0.0;
                min = Math.Min(min, pad);
                max = Math.Max(max, pad);
            }
            return max - min;
        }

        [Fact]
        public void A_hard_stab_after_a_long_light_brake_animates_again()
        {
            // THE REPORTED DEFECT, reproduced as driven: trail 8% brake for a second (the sweep finishes
            // and settles), then stamp it. Before the fix the second event produced nothing at all.
            GForceEngine engine = Engine();
            for (int i = 0; i < 90; i++) engine.Compute(Sample(-0.08, braking: true), AccelMax, DecelMax);

            double settled = (engine.Compute(Sample(-0.08, braking: true), AccelMax, DecelMax).BackLowLeft) ?? 0.0;
            double travel = FarPadTravel(engine, g: 1.0, braking: true, frames: 30);

            _out.WriteLine($"settled far pad {settled:F2}, travel after the stab {travel:F2}");
            Assert.True(travel > 20.0, $"the stab should re-arm the sweep, far pad only moved {travel:F2}");
        }

        [Fact]
        public void Ordinary_modulation_does_not_keep_retriggering()
        {
            // The owner's explicit constraint: "NOT that sensitive that keeps triggering the animation
            // with a continuous braking operation". A slow squeeze from 0 to full over a second must
            // produce ONE sweep, not a stutter of restarts.
            GForceEngine engine = Engine();
            int restarts = 0;
            double previous = 0.0;

            for (int i = 0; i < 60; i++)
            {
                double g = i / 60.0;
                GForceOutput r = engine.Compute(Sample(-g, braking: true), AccelMax, DecelMax);
                double far = r.BackLowLeft ?? 0.0;
                // A restart shows up as the far pad jumping back UP to its opening peak after having
                // fallen away from it.
                if (far > previous + 15.0 && i > 5) restarts++;
                previous = far;
            }

            _out.WriteLine($"far-pad jumps during a 1 s squeeze: {restarts}");
            Assert.True(restarts <= 1, $"a gentle squeeze should not restart the sweep repeatedly, saw {restarts}");
        }

        [Fact]
        public void Releasing_the_brake_quickly_never_triggers_an_animation()
        {
            // "NOTICE, if currently still got dec-G, and the dec-G force reduced very fast, we will NOT
            // trigger any animation. Only increasing of dec-G or acc-G will trigger the animation." The
            // delta used for the re-arm is SIGNED for exactly this reason.
            GForceEngine engine = Engine();
            for (int i = 0; i < 120; i++) engine.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax);

            double beforeRelease = (engine.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax).BackLowLeft) ?? 0.0;

            // Drop straight from full to a trace of brake - the fastest possible FALL.
            double afterRelease = (engine.Compute(Sample(-0.05, braking: true), AccelMax, DecelMax).BackLowLeft) ?? 0.0;

            _out.WriteLine($"far pad {beforeRelease:F2} -> {afterRelease:F2} across a hard release");
            Assert.True(afterRelease <= beforeRelease + 1.0,
                $"a fast release must not open a new sweep (far pad went {beforeRelease:F2} -> {afterRelease:F2})");
        }

        [Fact]
        public void A_sweep_already_running_is_never_cut_short_and_restarted()
        {
            // "ONLY if currently no animation is running, then trigger the animation." Two hard stabs
            // three frames apart must read as one gesture; restarting mid-sweep would be a stutter.
            GForceEngine engine = Engine();
            engine.Compute(Sample(-0.5, braking: true), AccelMax, DecelMax);   // opens a sweep
            engine.Compute(Sample(-0.5, braking: true), AccelMax, DecelMax);

            double mid = (engine.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax).BackLowLeft) ?? 0.0;
            double next = (engine.Compute(Sample(-1.0, braking: true), AccelMax, DecelMax).BackLowLeft) ?? 0.0;

            // Within the opening hold the far pad is pinned at its peak, so "no restart" shows up as the
            // value not being re-lifted after it has begun to fall. Assert the sweep is monotone here.
            _out.WriteLine($"far pad mid-sweep {mid:F2} -> {next:F2}");
            Assert.True(next <= mid + 0.01, $"a running sweep must not restart ({mid:F2} -> {next:F2})");
        }

        [Fact]
        public void Acceleration_re_arms_on_the_same_rule()
        {
            // "Do the same thing for the acc-G as well."
            GForceEngine engine = Engine();
            for (int i = 0; i < 90; i++) engine.Compute(Sample(0.08, braking: false), AccelMax, DecelMax);

            double travel = FarPadTravel(engine, g: 1.0, braking: false, frames: 30);
            _out.WriteLine($"accel far pad travel after the stab {travel:F2}");
            Assert.True(travel > 20.0, $"a throttle stab should re-arm the accel sweep, saw {travel:F2}");
        }

        // ---------------- the strictness setting (driver-configurable since 2026-09-07) ----------------

        [Fact]
        public void Retrigger_strictness_ships_at_1_2_and_is_clamped_at_both_ends()
        {
            // It is a SETTING rather than a constant because only seat time can settle it. The floor is
            // deliberately not 0: a strictness of zero means a threshold of zero, which re-arms on ANY
            // rise - exactly the runaway the setting exists to prevent.
            Assert.Equal(1.2, new GForceEngine().RetriggerStrictness, 6);
            Assert.Equal(1.2, new Settings.GForceSettings().RetriggerStrictness, 6);

            var engine = new GForceEngine { RetriggerStrictness = 0.0 };
            Assert.Equal(GForceEngine.MinRetriggerStrictness, engine.RetriggerStrictness, 6);

            engine.RetriggerStrictness = -5.0;
            Assert.Equal(GForceEngine.MinRetriggerStrictness, engine.RetriggerStrictness, 6);

            engine.RetriggerStrictness = 500.0;
            Assert.Equal(GForceEngine.MaxRetriggerStrictness, engine.RetriggerStrictness, 6);

            engine.RetriggerStrictness = double.NaN;
            Assert.Equal(1.2, engine.RetriggerStrictness, 6);

            // A hand-edited config file cannot smuggle a zero threshold in either.
            var settings = new Settings.GForceSettings { RetriggerStrictness = 0.0 };
            Assert.Equal(GForceEngine.MinRetriggerStrictness, settings.RetriggerStrictness, 6);
        }

        [Fact]
        public void Raising_the_strictness_makes_the_same_stab_stop_re_arming()
        {
            // The setting has to actually reach the threshold, not merely persist. The SAME gesture that
            // re-arms at the shipped 1.2 must be ignored once the driver asks for a decisive stab only.
            //
            // THE GESTURE IS RAMPED, NOT STEPPED, and that matters: a single-frame step from 0.08 to 0.9
            // is 0.82 ratio in 16.7 ms = ~49 ratio/s, which clears even the maximum threshold (25), so a
            // stepped stab cannot distinguish the settings at all. Ramped over 5 frames it is
            // 0.82 / (5/60) = ~9.8 ratio/s, which sits between the 1.2 threshold (6.0) and the 2.5 one
            // (12.5) - so the two settings must disagree about it.
            double lenient = TravelAfterRampedStab(strictness: 1.2, stabTo: 0.9, rampFrames: 5);
            double strict = TravelAfterRampedStab(strictness: 2.5, stabTo: 0.9, rampFrames: 5);

            _out.WriteLine($"~9.8 ratio/s stab - travel at 1.2: {lenient:F2}, at 2.5: {strict:F2}");
            Assert.True(lenient > strict + 5.0,
                $"raising strictness should suppress this stab (1.2 gave {lenient:F2}, 2.5 gave {strict:F2})");
        }

        [Fact]
        public void Lowering_the_strictness_lets_a_gentler_stab_through()
        {
            // The other direction, so the setting is shown to be monotone rather than merely different.
            // 0.08 -> 0.4 over 6 frames is 0.32 / (6/60) = 3.2 ratio/s: below the 1.2 threshold (6.0),
            // above the floor's (0.5).
            double atDefault = TravelAfterRampedStab(strictness: 1.2, stabTo: 0.4, rampFrames: 6);
            double atFloor = TravelAfterRampedStab(
                strictness: GForceEngine.MinRetriggerStrictness, stabTo: 0.4, rampFrames: 6);

            _out.WriteLine($"~3.2 ratio/s stab - travel at 1.2: {atDefault:F2}, at floor: {atFloor:F2}");
            Assert.True(atFloor > atDefault + 5.0,
                $"a lower strictness should admit a gentler stab (floor {atFloor:F2} vs 1.2 {atDefault:F2})");
        }

        /// <summary>
        /// Settles a long light brake (so the sweep completes), then RAMPS to <paramref name="stabTo"/>
        /// over <paramref name="rampFrames"/> frames - giving a controlled ratio-per-second rate - and
        /// reports how far the braking chain's FAR pad travels, the signature of a re-armed sweep.
        /// </summary>
        private double TravelAfterRampedStab(double strictness, double stabTo, int rampFrames)
        {
            const double from = 0.08;
            var engine = new GForceEngine
            {
                IntegrateWheelLockAndSlip = false,
                RetriggerStrictness = strictness,
            };
            for (int i = 0; i < 90; i++) engine.Compute(Sample(-from, braking: true), AccelMax, DecelMax);

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 1; i <= rampFrames; i++)
            {
                double g = from + (stabTo - from) * (i / (double)rampFrames);
                GForceOutput r = engine.Compute(Sample(-g, braking: true), AccelMax, DecelMax);
                double pad = r.BackLowLeft ?? 0.0;
                min = Math.Min(min, pad); max = Math.Max(max, pad);
            }
            for (int i = 0; i < 30; i++)
            {
                GForceOutput r = engine.Compute(Sample(-stabTo, braking: true), AccelMax, DecelMax);
                double pad = r.BackLowLeft ?? 0.0;
                min = Math.Min(min, pad); max = Math.Max(max, pad);
            }
            return max - min;
        }

        [Fact]
        public void The_threshold_scales_with_the_cars_own_maximum()
        {
            // The owner's derivation: the threshold is maxG / sweepDuration, so a LOW-max-G car (which
            // slows more gently and therefore produces smaller absolute deltas) still earns its
            // animation. Working in RATIO space is what makes that automatic - so the same pedal gesture,
            // expressed as a fraction of each car's own maximum, must re-arm both.
            foreach (double carMax in new[] { 0.6, 1.5, 3.0 })
            {
                var engine = new GForceEngine { IntegrateWheelLockAndSlip = false };
                for (int i = 0; i < 90; i++)
                    engine.Compute(Sample(-0.08 * carMax, braking: true), carMax, carMax);

                double min = double.MaxValue, max = double.MinValue;
                for (int i = 0; i < 30; i++)
                {
                    GForceOutput r = engine.Compute(Sample(-carMax, braking: true), carMax, carMax);
                    double far = r.BackLowLeft ?? 0.0;
                    min = Math.Min(min, far); max = Math.Max(max, far);
                }

                double travel = max - min;
                _out.WriteLine($"maxG {carMax:F1} -> far pad travel {travel:F2}");
                Assert.True(travel > 20.0, $"a {carMax:F1}g car should still re-arm, travel was {travel:F2}");
            }
        }
    }
}

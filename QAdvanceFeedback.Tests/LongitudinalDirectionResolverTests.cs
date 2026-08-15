using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="LongitudinalDirectionResolver"/> - the fix for the owner's standing rule
    /// that pedal state must never decide whether the car is slowing or speeding up. Every sample
    /// here is built directly (not through <c>SimHubTelemetryAdapter</c>), so these tests exercise the
    /// resolver in isolation from the engine that consumes it - see NormalizedWheelLockSlipEngineTests
    /// for the integration-level assertions.
    /// <para/>
    /// Speed deltas throughout are deliberately modest (1.0 km/h over a 16ms frame, ~-17.4 m/s^2
    /// before smoothing) - large enough to clear the dead band after a single EMA step, but well
    /// under the ~60 m/s^2 (~6g) implausible-jump cap, so ordinary tests are not accidentally
    /// exercising the rejection path (see An_implausible_jump_is_rejected_... for that, deliberately).
    /// </summary>
    public class LongitudinalDirectionResolverTests
    {
        private static ITelemetrySample Sample(double? newSpeedKmh, double? oldSpeedKmh, TimeSpan? dt)
        {
            var newFrame = new TelemetryFrame(groundSpeedKmh: newSpeedKmh);
            var oldFrame = new TelemetryFrame(groundSpeedKmh: oldSpeedKmh);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, dt);
        }

        [Fact]
        public void Falling_speed_resolves_to_Slowing()
        {
            var resolver = new LongitudinalDirectionResolver();
            var result = resolver.Resolve(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 101.0, dt: TimeSpan.FromMilliseconds(16)));

            Assert.Equal(LongitudinalMotionState.Slowing, result);
        }

        [Fact]
        public void Rising_speed_resolves_to_SpeedingUp()
        {
            var resolver = new LongitudinalDirectionResolver();
            var result = resolver.Resolve(Sample(newSpeedKmh: 101.0, oldSpeedKmh: 100.0, dt: TimeSpan.FromMilliseconds(16)));

            Assert.Equal(LongitudinalMotionState.SpeedingUp, result);
        }

        [Fact]
        public void Constant_speed_within_the_dead_band_resolves_to_Unknown()
        {
            var resolver = new LongitudinalDirectionResolver();
            // A tiny drift (0.01 km/h over 16ms) is well inside sampling noise - must not read as a
            // direction.
            var result = resolver.Resolve(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.01, dt: TimeSpan.FromMilliseconds(16)));

            Assert.Equal(LongitudinalMotionState.Unknown, result);
        }

        [Fact]
        public void No_speed_reading_at_all_holds_the_previous_state_rather_than_guessing()
        {
            var resolver = new LongitudinalDirectionResolver();
            resolver.Resolve(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 101.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.Slowing, resolver.State);

            var held = resolver.Resolve(Sample(newSpeedKmh: null, oldSpeedKmh: null, dt: TimeSpan.FromMilliseconds(16)));

            Assert.Equal(LongitudinalMotionState.Slowing, held);
        }

        [Fact]
        public void Missing_dt_holds_the_previous_state()
        {
            var resolver = new LongitudinalDirectionResolver();
            resolver.Resolve(Sample(newSpeedKmh: 101.0, oldSpeedKmh: 100.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.SpeedingUp, resolver.State);

            var held = resolver.Resolve(Sample(newSpeedKmh: 90.0, oldSpeedKmh: 100.0, dt: null));

            Assert.Equal(LongitudinalMotionState.SpeedingUp, held);
        }

        [Fact]
        public void Zero_or_negative_dt_holds_the_previous_state()
        {
            var resolver = new LongitudinalDirectionResolver();
            resolver.Resolve(Sample(newSpeedKmh: 101.0, oldSpeedKmh: 100.0, dt: TimeSpan.FromMilliseconds(16)));

            var held = resolver.Resolve(Sample(newSpeedKmh: 80.0, oldSpeedKmh: 100.0, dt: TimeSpan.Zero));

            Assert.Equal(LongitudinalMotionState.SpeedingUp, held);
        }

        [Fact]
        public void An_implausible_jump_is_rejected_and_does_not_corrupt_the_filter()
        {
            var resolver = new LongitudinalDirectionResolver();
            resolver.Resolve(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 101.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.Slowing, resolver.State);

            // A 300 km/h swing in 16ms - a teleport/session-restart discontinuity, not real driving.
            var duringJump = resolver.Resolve(Sample(newSpeedKmh: 300.0, oldSpeedKmh: 0.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.Slowing, duringJump); // held, not flipped to SpeedingUp

            // The next ordinary, physically plausible frame resumes updating normally.
            var after = resolver.Resolve(Sample(newSpeedKmh: 98.0, oldSpeedKmh: 99.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.Slowing, after);
        }

        [Fact]
        public void Sign_agnostic_by_construction_ignores_longitudinal_g_entirely()
        {
            // LongitudinalG is never read by this class at all - confirmed by never setting it on
            // either frame here and still getting a correct answer from ground speed alone (a
            // sign-convention bug in LongitudinalG cannot possibly leak into this resolver).
            var resolver = new LongitudinalDirectionResolver();
            var result = resolver.Resolve(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 101.0, dt: TimeSpan.FromMilliseconds(16)));

            Assert.Equal(LongitudinalMotionState.Slowing, result);
        }

        [Fact]
        public void Falls_back_to_plain_SpeedKmh_when_GroundSpeedKmh_is_absent()
        {
            var oldFrame = new TelemetryFrame(speedKmh: 101.0);
            var newFrame = new TelemetryFrame(speedKmh: 100.0);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            var resolver = new LongitudinalDirectionResolver();
            Assert.Equal(LongitudinalMotionState.Slowing, resolver.Resolve(sample));
        }

        [Fact]
        public void Reset_clears_state_and_filter_back_to_Unknown()
        {
            var resolver = new LongitudinalDirectionResolver();
            resolver.Resolve(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 101.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.Slowing, resolver.State);

            resolver.Reset();

            Assert.Equal(LongitudinalMotionState.Unknown, resolver.State);
            Assert.Equal(0.0, resolver.FilteredAccelerationMs2, 9);
        }

        [Fact]
        public void Light_smoothing_prevents_chatter_from_a_single_noisy_frame()
        {
            var resolver = new LongitudinalDirectionResolver();

            // Establish a confident Slowing trend over several consistent frames (each ~1 km/h slower
            // than the previous, 16ms apart).
            double speed = 110.0;
            for (int i = 0; i < 10; i++)
            {
                resolver.Resolve(Sample(newSpeedKmh: speed - 1.0, oldSpeedKmh: speed, dt: TimeSpan.FromMilliseconds(16)));
                speed -= 1.0;
            }
            Assert.Equal(LongitudinalMotionState.Slowing, resolver.State);

            // One single noisy frame reporting a small, momentary rise - must not flip the smoothed
            // state on its own (the EMA filter dominates a single outlier).
            var afterNoise = resolver.Resolve(Sample(newSpeedKmh: speed - 0.8, oldSpeedKmh: speed - 1.0, dt: TimeSpan.FromMilliseconds(16)));
            Assert.Equal(LongitudinalMotionState.Slowing, afterNoise);
        }
    }
}

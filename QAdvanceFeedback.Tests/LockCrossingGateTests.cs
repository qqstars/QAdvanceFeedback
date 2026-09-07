using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="LockCrossingGate"/> - the WheelLock twin of <see cref="SlipCrossingGate"/>, and the
    /// INDEPENDENCE of the two channels' switches.
    /// <para/>
    /// The gate ships OFF (see <c>NormalizedWheelLockSlipEngine.LockCrossingGateEnabled</c> for the
    /// measurement behind that), so these tests enable it explicitly. They exist so the mechanism is
    /// verified and ready the moment an EA WRC capture justifies turning it on.
    /// </summary>
    public class LockCrossingGateTests
    {
        private const double Dt = 1.0 / 60.0;
        private const string Game = "G", Car = "C", Source = "Src";

        private static LockCrossingGate Feed(
            LockCrossingGate gate, int frames, double basisStart, double basisStep, double gStart, double gStep)
        {
            for (int i = 0; i < frames; i++)
            {
                double basis = basisStart + basisStep * i;
                gate.Observe(basis, basis, gStart + gStep * i, true, Dt);
            }
            return gate;
        }

        [Fact]
        public void Lock_rising_while_G_falls_is_a_crossing()
        {
            var gate = Feed(new LockCrossingGate(), 12, 10.0, 2.0, 1.8, -0.05);
            Assert.True(gate.Qualified);
        }

        [Fact]
        public void A_sustained_lock_at_constant_G_never_qualifies()
        {
            var gate = Feed(new LockCrossingGate(), 200, 100.0, 0.0, 1.8, 0.0);
            Assert.False(gate.Qualified);
        }

        [Fact]
        public void A_frame_the_corner_detector_has_no_confidence_in_cannot_arm_a_crossing()
        {
            // Lock's arming condition is the corner-local detector, not a G threshold - the difference
            // from Slip's gate. A spin or a kerb strike shows the same rise-while-G-falls signature.
            var gate = new LockCrossingGate();
            for (int i = 0; i < 20; i++) gate.Observe(20.0 + 2.0 * i, 20.0 + 2.0 * i, 1.8 - 0.05 * i, false, Dt);
            Assert.False(gate.Qualified);
        }

        [Fact]
        public void The_hold_window_teaches_the_crossing_value_not_the_lock_up_that_follows()
        {
            var gate = new LockCrossingGate();
            Feed(gate, LockCrossingGate.TrendFrames + 1, 20.0, 1.5, 1.8, -0.05);
            Assert.True(gate.Qualified);

            double onset = gate.TeachingBasis(0.0);
            for (int i = 0; i < 20; i++) gate.Observe(100.0, 100.0, 0.6, true, Dt);

            Assert.True(gate.Qualified);
            Assert.Equal(onset, gate.TeachingBasis(100.0), 6);
            Assert.Equal(onset, gate.TeachingFallbackBasis(100.0), 6);
        }

        [Fact]
        public void A_rise_inside_a_live_window_cannot_re_arm_the_detector()
        {
            var gate = new LockCrossingGate();
            Feed(gate, LockCrossingGate.TrendFrames + 1, 20.0, 1.5, 1.8, -0.05);
            double onset = gate.TeachingBasis(0.0);

            for (int i = 0; i < 10; i++) gate.Observe(40.0 + i * 6.0, 40.0 + i * 6.0, 1.7 - i * 0.08, true, Dt);
            Assert.Equal(onset, gate.TeachingBasis(0.0), 6);
        }

        [Fact]
        public void The_weight_is_a_multiplier_on_the_corner_detectors_own_confidence()
        {
            // The substantive difference from Slip's gate: Lock's existing continuous confidence must
            // survive inside a qualified window rather than being replaced by a flat 1.0.
            var gate = new LockCrossingGate();
            Assert.Equal(0.0, gate.TeachingWeight(0.42));           // not qualified, factor 0

            Feed(gate, LockCrossingGate.TrendFrames + 1, 20.0, 1.5, 1.8, -0.05);
            Assert.Equal(0.42, gate.TeachingWeight(0.42), 9);       // qualified: confidence passes through

            var lenient = new LockCrossingGate { NonCrossingWeightFactor = 0.5 };
            Assert.Equal(0.21, lenient.TeachingWeight(0.42), 9);    // not qualified: scaled, not zeroed
        }

        [Fact]
        public void Nonsense_inputs_are_rejected_the_same_way_slips_are()
        {
            var gate = new LockCrossingGate();
            for (int i = 0; i < 20; i++)
                gate.Observe(double.PositiveInfinity, double.PositiveInfinity, 1.8 - 0.05 * i, true, Dt);
            Assert.False(gate.Qualified);

            gate.NonCrossingWeightFactor = double.NaN;
            Assert.Equal(0.0, gate.NonCrossingWeightFactor);
            Assert.Equal(0.0, gate.TeachingWeight(double.NaN));
            Assert.Equal(0.0, gate.TeachingWeight(double.PositiveInfinity));

            gate.RecordCandidate(null, true);
            Assert.Equal(0L, gate.ValidFrames(null));
        }

        [Fact]
        public void A_zero_frame_time_still_expires_the_window()
        {
            var gate = new LockCrossingGate();
            for (int i = 0; i < LockCrossingGate.TrendFrames + 1; i++)
                gate.Observe(20.0 + 1.5 * i, 20.0 + 1.5 * i, 1.8 - 0.05 * i, true, 0.0);
            Assert.True(gate.Qualified);

            for (int i = 0; i < (int)(LockCrossingGate.HoldSeconds / LockCrossingGate.NominalFrameSeconds) + 5; i++)
                gate.Observe(34.0, 34.0, 1.2, true, 0.0);
            Assert.False(gate.Qualified);
        }

        // ------------------------------------------------------------------------------------
        // INDEPENDENCE - the owner's explicit requirement: either side must be switchable off
        // without touching the other.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Both_gates_ship_on()
        {
            // Lock's was OFF when it landed, pending an EA WRC capture - see the property's own remarks
            // for the trade-off that was measured. The owner enabled it on 2026-09-05 on the strength of
            // the field report; setting it false restores 1.0.6.9's validated Lock behaviour exactly and
            // reaches nothing on the Slip channel.
            var engine = new NormalizedWheelLockSlipEngine();
            Assert.True(engine.LockCrossingGateEnabled);
            Assert.True(engine.SlipCrossingGateEnabled);
        }

        [Fact]
        public void Turning_the_lock_gate_on_does_not_change_what_slip_learns()
        {
            double slipOff = SlipCeilingWith(lockGate: false);
            double slipOn = SlipCeilingWith(lockGate: true);
            Assert.Equal(slipOff, slipOn, 9);
        }

        [Fact]
        public void Turning_the_slip_gate_off_does_not_change_what_lock_learns()
        {
            double lockOn = LockCeilingWith(slipGate: true);
            double lockOff = LockCeilingWith(slipGate: false);
            Assert.Equal(lockOn, lockOff, 9);
        }

        [Fact]
        public void Disabling_the_slip_gate_restores_the_pre_gate_slip_behaviour()
        {
            // The kill switch has to actually kill it: a launch that the gate refuses to learn from must
            // teach again once the switch is off.
            double gated = SlipLaunchCeiling(slipGate: true);
            double ungated = SlipLaunchCeiling(slipGate: false);
            Assert.True(ungated > gated + 10.0, $"gated {gated:F1}, ungated {ungated:F1}");
        }

        private static ITelemetrySample Accel(double g)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: 101.0, longitudinalG: g, throttlePercent: 90.0),
                new TelemetryFrame(groundSpeedKmh: 100.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static ITelemetrySample Brake(double g)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -g, brakePercent: 80.0),
                new TelemetryFrame(groundSpeedKmh: 101.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static double SlipCeilingWith(bool lockGate)
        {
            var e = new NormalizedWheelLockSlipEngine { LockCrossingGateEnabled = lockGate };
            for (int c = 0; c < 30; c++)
            {
                for (int i = 0; i < 20; i++) e.Compute(Accel(1.5), Corners.Uniform(30.0), Corners.Uniform(10.0 + i), Game, Car, lockSourceIdentity: "L", slipSourceIdentity: Source);
                for (int i = 0; i < 20; i++) e.Compute(Accel(1.5 - i * 0.02), Corners.Uniform(30.0), Corners.Uniform(30.0 + i), Game, Car, lockSourceIdentity: "L", slipSourceIdentity: Source);
            }
            return e.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
        }

        private static double LockCeilingWith(bool slipGate)
        {
            var e = new NormalizedWheelLockSlipEngine { SlipCrossingGateEnabled = slipGate };
            for (int c = 0; c < 30; c++)
                for (int i = 0; i < 40; i++)
                    e.Compute(Brake(1.8 - i * 0.01), Corners.Uniform(20.0 + i), Corners.Uniform(15.0), Game, Car, lockSourceIdentity: "L", slipSourceIdentity: Source);
            return e.LockScaleLearner.LearnedCeiling(Game, Car, "L", out _) ?? 0.0;
        }

        private static double SlipLaunchCeiling(bool slipGate)
        {
            var e = new NormalizedWheelLockSlipEngine { SlipCrossingGateEnabled = slipGate };
            for (int i = 0; i < 900; i++)
                e.Compute(Accel(Math.Min(1.5, 0.2 + 1.3 * i / 300.0)), Corners.Zero, Corners.Uniform(100.0),
                          Game, Car, lockSourceIdentity: "L", slipSourceIdentity: Source);
            return e.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
        }
    }
}

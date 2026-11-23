using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The SMax 100/90/70 fix (docs\slip-smax-crossing-gate-design.md, "Walk-back stays inside the rise",
    /// "The window teaches only the snapshot", "Learn ceiling"). The frame sequences are the two real
    /// crossings that taught 100 in the field logs (FH6 frame 5300, Viper frame 2955).
    /// </summary>
    public class CrossingGateSmaxFixTests
    {
        private const double Dt = 1.0 / 60.0;

        private static void Feed(LockCrossingGate gate, double[] bases, double[] gs)
        {
            for (int i = 0; i < bases.Length; i++)
                gate.Observe(bases[i], bases[i], gs[i], true, Dt, 0);
        }

        [Fact]
        public void Fh6_crossing_snapshots_the_first_rising_frame_not_the_flat_frame_before_it()
        {
            // FH6 frames 5295-5300: flat 0 while G eases, then FL starts to lock (7.2) as G dips.
            var gate = new LockCrossingGate();
            Feed(gate, new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 7.2 }, new[] { 2.15, 1.80, 1.72, 1.83, 1.90, 1.87 });

            Assert.True(gate.Qualified);
            // The window teaches the snapshot even though the live value has run away to 100.
            Assert.Equal(7.2, gate.TeachingBasis(100.0), 6);
            Assert.Equal(7.2, gate.TeachingFallbackBasis(100.0), 6);
        }

        [Fact]
        public void Viper_one_frame_jump_snapshots_the_jump_frame()
        {
            // Viper frames 2950-2955: flat 0, then 0 -> 67.5 in one frame.
            var gate = new LockCrossingGate();
            Feed(gate, new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 67.5 }, new[] { 3.00, 2.96, 2.92, 2.92, 2.82, 2.75 });

            Assert.True(gate.Qualified);
            Assert.Equal(67.5, gate.TeachingBasis(100.0), 6);
        }

        [Fact]
        public void A_progressive_rise_still_walks_back_to_its_grip_peak()
        {
            var gate = new LockCrossingGate();
            Feed(gate, new[] { 0.0, 0.0, 20.0, 45.0, 70.0, 79.0 }, new[] { 1.70, 1.70, 1.68, 1.70, 1.66, 1.60 });

            Assert.True(gate.Qualified);
            Assert.Equal(45.0, gate.TeachingBasis(100.0), 6);
        }

        [Fact]
        public void Every_window_frame_teaches_the_snapshot_never_the_live_value()
        {
            var gate = new LockCrossingGate();
            Feed(gate, new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 7.2 }, new[] { 2.15, 1.80, 1.72, 1.83, 1.90, 1.87 });
            double[] live = { 67.5, 90.0, 75.0, 97.5, 100.0, 100.0 };
            foreach (double v in live)
            {
                gate.Observe(v, v, 1.4, true, Dt, 2);
                Assert.True(gate.Qualified);
                Assert.Equal(7.2, gate.TeachingBasis(v), 6);
            }
        }

        [Fact]
        public void Lock_and_Slip_never_teach_above_the_learn_ceiling_95()
        {
            Assert.Equal(95.0, LockCrossingGate.LearnCeiling);
            Assert.Equal(95.0, SlipCrossingGate.LearnCeiling);

            var lockGate = new LockCrossingGate();
            Feed(lockGate, new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 97.5 }, new[] { 2.0, 2.0, 2.0, 2.0, 2.0, 1.8 });
            Assert.True(lockGate.Qualified);
            Assert.Equal(95.0, lockGate.TeachingBasis(100.0), 6);
            Assert.Equal(95.0, lockGate.TeachingFallbackBasis(100.0), 6);

            var slipGate = new SlipCrossingGate();
            double[] b = { 0.0, 0.0, 0.0, 0.0, 0.0, 99.0 }, g = { 2.0, 2.0, 2.0, 2.0, 2.0, 1.8 };
            for (int i = 0; i < b.Length; i++) slipGate.Observe(b[i], b[i], g[i], true, Dt, 0);
            Assert.True(slipGate.Qualified);
            Assert.Equal(95.0, slipGate.TeachingBasis(100.0), 6);

            // Outside a window the live value is capped too (it only teaches when NonCrossingWeight > 0).
            Assert.Equal(95.0, new LockCrossingGate().TeachingBasis(100.0), 6);
        }

        [Fact]
        public void SlipAngle_gate_keeps_its_45_degree_ceiling_the_learner_caps_Max_itself()
        {
            CrossingGateParameters p = CrossingGateParameters.SlipAngle;
            Assert.Equal(45.0, p.SourceCeiling);
            Assert.Equal(45.0, p.LearnCeiling);
        }
    }
}

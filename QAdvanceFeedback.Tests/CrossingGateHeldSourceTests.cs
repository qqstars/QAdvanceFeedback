using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE HELD-SOURCE LEAK (owner, 2026-09-07, from real driving): "we will NOT accept if the source
    /// value is HOLD but Acc/Dec-G is decreasing for the SMax. Instead, the source value MUST INCREASING."
    /// <para/>
    /// The trend test differences against <c>TrendFrames</c> (5) frames back, so a source that shoots
    /// 10 -> 100 in two frames and then SITS at 100 still reported a rise of +90 for the next five
    /// frames. Across those frames it was not rising at all - it was pegged - yet G was falling (the
    /// wheel had already let go), so the frame qualified and taught 100. Rules 1 and 2 close it, and
    /// together they make SMax = 100 STRUCTURALLY unreachable: teaching requires a strictly increasing
    /// source, and a value clamped to 100 cannot increase.
    /// </summary>
    public class CrossingGateHeldSourceTests
    {
        private readonly ITestOutputHelper _out;
        public CrossingGateHeldSourceTests(ITestOutputHelper output) { _out = output; }

        private const double Dt = 1.0 / 60.0;

        /// <summary>Feeds a run of frames and reports whether any of them qualified a crossing.</summary>
        private static bool AnySlipCrossing(SlipCrossingGate gate, double[] basis, double[] g)
        {
            bool any = false;
            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame) any = true;
            }
            return any;
        }

        private static bool AnyLockCrossing(LockCrossingGate gate, double[] basis, double[] g)
        {
            bool any = false;
            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame) any = true;
            }
            return any;
        }

        [Fact]
        public void A_source_pegged_at_100_never_qualifies_however_far_G_falls()
        {
            // The reported case, as driven: a snappy break-away to full slip, then the source HOLDS at
            // 100 while G collapses. Before rule 1 the five stale trend frames qualified and taught 100.
            var gate = new SlipCrossingGate();

            // Six settling frames, then the break-away, then a long hold at 100 with G falling away.
            var basis = new double[] { 10, 10, 10, 10, 10, 10, 55, 100, 100, 100, 100, 100, 100, 100, 100 };
            var g = new double[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.95, 0.80, 0.70, 0.60, 0.50, 0.40, 0.30, 0.20, 0.10 };

            // The rise frames may legitimately qualify - they ARE rising. What must never happen is a
            // crossing armed on a frame where the source is merely HELD.
            var heldOnly = new SlipCrossingGate();
            for (int i = 0; i < 6; i++) heldOnly.Observe(100.0, 100.0, 1.0, atLimit: true, dtSeconds: Dt);
            bool qualifiedWhileHeld = false;
            for (int i = 0; i < 20; i++)
            {
                heldOnly.Observe(100.0, 100.0, 1.0 - i * 0.04, atLimit: true, dtSeconds: Dt);
                if (heldOnly.CrossedThisFrame) qualifiedWhileHeld = true;
            }

            _out.WriteLine($"held at 100 with G collapsing -> crossing fired: {qualifiedWhileHeld}");
            Assert.False(qualifiedWhileHeld, "a held source must never qualify, however far G falls");

            // And the full gesture: whatever it teaches, it can never be the ceiling itself.
            AnySlipCrossing(gate, basis, g);
            _out.WriteLine($"taught basis after the break-away: {gate.TeachingBasis(100.0):F2}");
            Assert.True(gate.TeachingBasis(100.0) < SlipCrossingGate.SourceCeiling,
                "the taught snapshot must be below the ceiling - a clamped 100 cannot be 'still rising'");
        }

        [Fact]
        public void The_lock_gate_rejects_a_held_source_too()
        {
            // Same rule, Lock's own gate - the owner asked for both, and EA WRC's Lock SMax = 100 is the
            // same shape of failure.
            var gate = new LockCrossingGate();
            for (int i = 0; i < 6; i++) gate.Observe(100.0, 100.0, 1.0, atLimit: true, dtSeconds: Dt);

            bool qualified = false;
            for (int i = 0; i < 20; i++)
            {
                gate.Observe(100.0, 100.0, 1.0 - i * 0.04, atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame) qualified = true;
            }

            _out.WriteLine($"lock held at 100 with G collapsing -> crossing fired: {qualified}");
            Assert.False(qualified, "a held lock source must never qualify either");
        }

        [Fact]
        public void A_source_reading_just_over_100_is_clamped_and_cannot_read_as_rising()
        {
            // Rule 2. Real telemetry and our own arithmetic both produce 100.0000000001; against a
            // previous 100.0 that is a "rise", which is one of the ways a saturated source kept
            // qualifying. Both gates clamp on entry.
            var slip = new SlipCrossingGate();
            for (int i = 0; i < 6; i++) slip.Observe(100.0, 100.0, 1.0, atLimit: true, dtSeconds: Dt);

            bool qualified = false;
            for (int i = 0; i < 10; i++)
            {
                slip.Observe(100.0000000001, 100.0000000001, 0.9 - i * 0.05, atLimit: true, dtSeconds: Dt);
                if (slip.CrossedThisFrame) qualified = true;
            }

            Assert.False(qualified, "100.0000000001 must clamp to 100 and therefore not count as a rise");
            Assert.True(slip.TeachingBasis(100.0000000001) <= SlipCrossingGate.SourceCeiling,
                "no basis may leave the gate above the ceiling");
        }

        [Fact]
        public void A_genuine_mid_range_crossing_still_qualifies_and_teaches_its_own_value()
        {
            // THE PROPERTY THAT MATTERS ALONGSIDE: "no impact on the normal range values". A progressive
            // break-away rising through the 50s, with G turning over, must still qualify and still teach
            // the reading it crossed at.
            var gate = new SlipCrossingGate();
            var basis = new double[] { 30, 32, 34, 36, 38, 40, 44, 48, 52, 56 };
            var g = new double[] { 0.90, 0.92, 0.94, 0.95, 0.96, 0.96, 0.93, 0.89, 0.84, 0.78 };

            bool any = AnySlipCrossing(gate, basis, g);
            double taught = gate.TeachingBasis(56.0);

            _out.WriteLine($"progressive crossing qualified: {any}, taught {taught:F1}");
            Assert.True(any, "a progressive traction crossing must still be detected");
            Assert.InRange(taught, 40.0, 60.0);
        }
    }
}

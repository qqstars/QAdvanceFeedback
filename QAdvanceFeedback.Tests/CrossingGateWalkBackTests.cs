using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE G-BANDED WALK-BACK (owner, 2026-09-25 - v1.1.0). Confirming a crossing costs
    /// <see cref="LockCrossingGate.TrendFrames"/> frames of evidence, so the detection frame is
    /// structurally LATE - G has already fallen off its peak by the time the gate fires. The owner's own
    /// observation on the FH6 capture: detection at frame 7617 (G 0.980, basis 98.2) but the car was
    /// braking hardest at 7613 (G 1.248, basis 92.0), and max grip is where G PEAKED.
    /// <para/>
    /// The snapshot therefore steps back to the grip peak OF THE ONE CONTIGUOUS RISE that produced this
    /// crossing, bounded by <see cref="LockCrossingGate.DefaultWalkBackGBandFraction"/> so it can never
    /// wander into an earlier, unrelated phase.
    /// <para/>
    /// See the constant's own remarks for the corpus measurement behind the 10% band, including why the
    /// unbounded version collapsed a 74.0 snapshot to 11.8 and why 5% was too tight (it declines to act
    /// on a third of hard-braking crossings).
    /// </summary>
    public class CrossingGateWalkBackTests
    {
        private readonly ITestOutputHelper _out;
        public CrossingGateWalkBackTests(ITestOutputHelper output) { _out = output; }

        private const double Dt = 1.0 / 60.0;

        /// <summary>Runs a trace and returns what the gate taught, or null if it never crossed.</summary>
        private static double? RunLock(double[] basis, double[] g, double band = LockCrossingGate.DefaultWalkBackGBandFraction)
        {
            var gate = new LockCrossingGate { WalkBackGBandFraction = band };
            for (int i = 0; i < 6; i++) gate.Observe(1.0, 1.0, 0.50, atLimit: true, dtSeconds: Dt, saturatedWheelCount: 0);
            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt, saturatedWheelCount: 0);
                if (gate.CrossedThisFrame) return gate.TeachingBasis(basis[i]);
            }
            return null;
        }

        private static double? RunSlip(double[] basis, double[] g, double band = SlipCrossingGate.DefaultWalkBackGBandFraction)
        {
            var gate = new SlipCrossingGate { WalkBackGBandFraction = band };
            for (int i = 0; i < 6; i++) gate.Observe(1.0, 1.0, 0.50, atLimit: true, dtSeconds: Dt, saturatedWheelCount: 0);
            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt, saturatedWheelCount: 0);
                if (gate.CrossedThisFrame) return gate.TeachingBasis(basis[i]);
            }
            return null;
        }

        // A textbook crossing. The basis rises monotonically; G peaks at index 2 and falls away, and it
        // must fall by more than GFall (0.05) across the 5-frame trend window or the gate never arms at
        // all. G moves ~2% per frame, well inside the 10% band, so the walk reaches the peak.
        // Detection lands on index 7 (basis 78, G 0.99); the grip peak is index 2 (basis 40, G 1.08).
        private static readonly double[] PeakBasis = { 20, 30, 40, 50, 60, 70, 74, 78 };
        private static readonly double[] PeakG = { 1.00, 1.02, 1.08, 1.06, 1.04, 1.02, 1.01, 0.99 };

        [Fact]
        public void The_snapshot_is_taken_at_the_grip_peak_not_the_detection_frame()
        {
            double? taught = RunLock(PeakBasis, PeakG);
            _out.WriteLine($"taught {taught}");
            Assert.NotNull(taught);
            // G peaks at 1.08 where the basis reads 40; the detection frame is later, at 78.
            Assert.Equal(40.0, taught.Value, 3);
        }

        [Fact]
        public void Disabling_the_band_restores_the_detection_frame_snapshot()
        {
            // The kill switch must reproduce pre-1.1.0 behaviour exactly, so a regression can always be
            // bisected to this feature rather than to the rest of the gate.
            double? off = RunLock(PeakBasis, PeakG, band: 0.0);
            double? on = RunLock(PeakBasis, PeakG);
            _out.WriteLine($"band off -> {off}, on -> {on}");
            Assert.NotNull(off);
            Assert.True(off.Value > on.Value, "disabled walk-back must snapshot the later, higher frame");
        }

        [Fact]
        public void The_band_stops_the_walk_leaving_a_different_phase()
        {
            // THE VIPER FAILURE (session-20260925-114458, frame 293), which is why the band exists.
            // The source jumps 15.3 -> 73.5 in one frame; the highest-G frame in the raw window sits on
            // the far side of that jump at basis 11.8. G there is 3.837 against a detection G of 2.917 -
            // a 31% gap - so a 10% band refuses to step onto it and the snapshot stays on this side.
            double[] basis = { 1.23, 8.33, 13.44, 13.44, 11.81, 15.32, 73.45, 73.98 };
            double[] g = { 0.614, 2.395, 3.780, 3.780, 3.837, 3.596, 2.994, 2.917 };

            double? banded = RunLock(basis, g);
            double? unbanded = RunLock(basis, g, band: 9e9);
            _out.WriteLine($"banded -> {banded}, unbanded -> {unbanded}");

            Assert.NotNull(banded);
            Assert.NotNull(unbanded);
            Assert.True(banded.Value > 70.0, $"banded snapshot collapsed to {banded.Value}");
            Assert.True(unbanded.Value < 20.0, "the unbanded rule is expected to collapse - that is the defect");
        }

        [Fact]
        public void The_walk_never_leaves_the_rise_that_produced_this_crossing()
        {
            // The structural half of the bound, independent of the G band. Here G drifts only 1% per
            // frame so the band admits everything, but the basis DIPS at index 3 - an earlier, separate
            // rise sits behind it. The walk must stop at the dip and never reach the lower values.
            // Index 1 is an EARLIER, separate phase: its basis (60) is higher than index 2's (45), so
            // the rise ending at index 6 does not include it. Its G (1.09) is the highest in the window
            // and sits INSIDE the 10% band, so only the rise rule can keep the walk off it. If the walk
            // escaped, the snapshot would be 60 rather than 45.
            double[] basis = { 30, 60, 45, 55, 62, 70, 74 };
            double[] g = { 1.20, 1.09, 1.06, 1.05, 1.04, 1.02, 1.00 };

            double? taught = RunLock(basis, g);
            _out.WriteLine($"taught {taught}");
            Assert.NotNull(taught);
            Assert.Equal(45.0, taught.Value, 3);
        }

        [Fact]
        public void A_hard_brake_on_a_grip_plateau_still_reaches_its_peak()
        {
            // Measured on the corpus: during genuinely hard braking the car sits on a grip PLATEAU and G
            // moves only about 2% per frame (median, for crossings at 3 g and above). The band must be
            // loose enough to walk across that - at 5% it would decline on a third of hard brakes.
            double[] basis = { 20, 30, 40, 50, 58, 64, 70 };
            double[] g = { 3.00, 3.10, 3.20, 3.24, 3.18, 3.10, 3.02 };

            double? taught = RunLock(basis, g);
            _out.WriteLine($"3g plateau -> {taught}");
            Assert.NotNull(taught);
            Assert.Equal(50.0, taught.Value, 3);   // G peaks at 3.24 where the basis reads 50
        }

        [Fact]
        public void Both_channels_ship_the_band_at_ten_percent()
        {
            // A drift in either default silently re-tunes both the output amplitude AND where the
            // canonical at-limit anchor lands - see DefaultWalkBackGBandFraction's own remarks on the
            // cost that was accepted here. Pinned on both channels.
            Assert.Equal(0.10, LockCrossingGate.DefaultWalkBackGBandFraction, 6);
            Assert.Equal(0.10, SlipCrossingGate.DefaultWalkBackGBandFraction, 6);
            Assert.Equal(0.10, new LockCrossingGate().WalkBackGBandFraction, 6);
            Assert.Equal(0.10, new SlipCrossingGate().WalkBackGBandFraction, 6);
        }

        [Fact]
        public void Turning_the_walk_back_off_restores_the_detection_frame_snapshot()
        {
            // The kill switch must reproduce pre-1.1.0 behaviour exactly, so a regression can always be
            // bisected to this feature rather than to the rest of the gate.
            var gate = new LockCrossingGate { WalkBackGBandFraction = 0.0 };
            for (int i = 0; i < 6; i++) gate.Observe(1.0, 1.0, 0.50, atLimit: true, dtSeconds: Dt);
            double? taught = null;
            for (int i = 0; i < PeakBasis.Length; i++)
            {
                gate.Observe(PeakBasis[i], PeakBasis[i], PeakG[i], atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame) { taught = gate.TeachingBasis(PeakBasis[i]); break; }
            }
            _out.WriteLine($"walk-back off -> {taught}");
            Assert.NotNull(taught);
            Assert.Equal(78.0, taught.Value, 3);
        }

        [Fact]
        public void Slip_walks_back_the_same_way_and_is_independently_switchable()
        {
            double? on = RunSlip(PeakBasis, PeakG);
            double? off = RunSlip(PeakBasis, PeakG, band: 0.0);
            _out.WriteLine($"slip on -> {on}, off -> {off}");
            Assert.Equal(40.0, on.Value, 3);
            Assert.True(off.Value > on.Value);
        }

        [Fact]
        public void A_zero_or_non_finite_detection_G_degrades_to_the_detection_frame()
        {
            // G is the band's denominator. A title reporting zero G while the source still moves must not
            // divide by it, nor throw, nor teach nothing - it simply falls back to the old snapshot.
            double[] basis = { 20, 30, 40, 50, 60, 70, 74, 78 };
            double[] g = { 1.00, 1.02, 1.08, 1.06, 1.04, 1.02, 1.01, 0.0 };

            var gate = new LockCrossingGate { WalkBackGBandFraction = LockCrossingGate.DefaultWalkBackGBandFraction };
            for (int i = 0; i < 6; i++) gate.Observe(1.0, 1.0, 0.50, atLimit: true, dtSeconds: Dt);
            double? taught = null;
            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame) { taught = gate.TeachingBasis(basis[i]); break; }
            }
            _out.WriteLine($"zero-G detection -> {taught}");
            Assert.NotNull(taught);
            Assert.Equal(78.0, taught.Value, 3);
        }

        [Fact]
        public void Both_keys_are_snapshotted_at_the_same_frame()
        {
            // The engine's configured-vs-fallback divergence test compares the two on the assumption that
            // they describe the same moment, so the walk must move them together. Here the fallback runs
            // at exactly half the configured basis, so the relationship must survive the walk.
            var gate = new LockCrossingGate();
            for (int i = 0; i < 6; i++) gate.Observe(1.0, 0.5, 0.50, atLimit: true, dtSeconds: Dt);

            double? configured = null, fallback = null;
            for (int i = 0; i < PeakBasis.Length; i++)
            {
                gate.Observe(PeakBasis[i], PeakBasis[i] / 2.0, PeakG[i], atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame)
                {
                    configured = gate.TeachingBasis(PeakBasis[i]);
                    fallback = gate.TeachingFallbackBasis(PeakBasis[i] / 2.0);
                    break;
                }
            }
            _out.WriteLine($"configured {configured}, fallback {fallback}");
            Assert.NotNull(configured);
            Assert.Equal(configured.Value / 2.0, fallback.Value, 6);
        }
    }
}

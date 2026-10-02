using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE SATURATED-WHEEL GUARD (owner, 2026-09-25 - v1.1.0). Rules 1 and 2 made a rise FROM the
    /// ceiling impossible, but not a rise TO it, and not a "crossing" detected long after half the car
    /// has already let go. The owner reported Lock SMax learning 98.x in Forza Horizon 6 and the corpus
    /// holds two logs that learned exactly 100.0.
    /// <para/>
    /// THE GUARD: a crossing is rejected when
    /// <see cref="LockCrossingGate.DefaultMinSaturatedWheelsForRejection"/> or more of the four
    /// CONFIGURED wheels already read the source's own ceiling. It reads nothing but the source's own
    /// saturation - no G, no grip, no speed, no surface - so it satisfies the owner's standing
    /// constraint that a different car, surface or weather must never be penalised.
    /// <para/>
    /// The per-log corpus measurements live on each channel's own constant. These tests pin the
    /// BEHAVIOUR, using the owner's own captured FH6 frames as the primary case.
    /// </summary>
    public class CrossingGateSaturatedWheelTests
    {
        private readonly ITestOutputHelper _out;
        public CrossingGateSaturatedWheelTests(ITestOutputHelper output) { _out = output; }

        private const double Dt = 1.0 / 60.0;

        // ---- THE OWNER'S OWN CAPTURE -----------------------------------------------------------
        // QAdvanceFeedback.session-20260925-115227.csv (FH6, Car_3980, branch RPS), frames 7605-7622 -
        // the braking zone that taught SMax = 98.249. Basis is WheelLock.Raw.All, G is
        // Diag.MotionMagnitudeG, and Pegged counts how many of WheelLock.Raw.{FL,FR,RL,RR} read 100.
        // Verbatim from the log, not reconstructed: both fronts are pegged from frame 7608 onward while
        // the rears are still climbing, which is exactly why the aggregate is honestly still rising at
        // the detection frame nine frames later.
        private static readonly double[] Fh6Basis =
        {
            66.851, 74.460, 78.611, 90.000, 90.000, 90.000, 90.223, 91.513, 91.956,
            92.904, 93.792, 95.805, 98.249, 98.712, 98.630, 98.956, 99.477, 100.000
        };
        private static readonly double[] Fh6G =
        {
            0.585, 0.686, 0.799, 0.855, 0.849, 0.875, 0.848, 1.072, 1.248,
            1.187, 1.182, 1.023, 0.980, 1.032, 1.126, 1.047, 0.990, 0.944
        };
        private static readonly int[] Fh6Pegged = { 0, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 4 };

        /// <summary>Runs a trace through the Lock gate and returns what it taught, or null if it never
        /// crossed. Primed with six quiet frames so the trend history is full before the trace starts;
        /// the priming values are far below the trace and rising in G, so they can never themselves
        /// arm a crossing.</summary>
        private static double? RunLock(double[] basis, double[] g, int[] pegged, int threshold)
        {
            // WalkBackGBandFraction = 0 isolates THIS feature: the walk-back is a separate v1.1.0
            // change with its own tests, and leaving it on would make every number here depend on both.
            var gate = new LockCrossingGate { MinSaturatedWheelsForRejection = threshold, WalkBackGBandFraction = 0.0 };
            for (int i = 0; i < 6; i++) gate.Observe(10.0, 10.0, 0.50, atLimit: true, dtSeconds: Dt, saturatedWheelCount: 0);

            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt, saturatedWheelCount: pegged[i]);
                if (gate.CrossedThisFrame) return gate.TeachingBasis(basis[i]);
            }
            return null;
        }

        private static double? RunSlip(double[] basis, double[] g, int[] pegged, int threshold)
        {
            var gate = new SlipCrossingGate { MinSaturatedWheelsForRejection = threshold, WalkBackGBandFraction = 0.0 };
            for (int i = 0; i < 6; i++) gate.Observe(10.0, 10.0, 0.50, atLimit: true, dtSeconds: Dt, saturatedWheelCount: 0);

            for (int i = 0; i < basis.Length; i++)
            {
                gate.Observe(basis[i], basis[i], g[i], atLimit: true, dtSeconds: Dt, saturatedWheelCount: pegged[i]);
                if (gate.CrossedThisFrame) return gate.TeachingBasis(basis[i]);
            }
            return null;
        }

        [Fact]
        public void The_reported_FH6_braking_zone_taught_98_before_the_guard()
        {
            // THE DEFECT, reproduced from the owner's own capture. With the guard off the gate fires at
            // frame 7617 and snapshots 98.249 - which is bit-for-bit the SMax the log published.
            double? taught = RunLock(Fh6Basis, Fh6G, Fh6Pegged, threshold: 0);
            _out.WriteLine($"guard off -> {(taught.HasValue ? taught.Value.ToString("F3") : "no crossing")}");
            Assert.NotNull(taught);
            Assert.Equal(98.249, taught.Value, 3);
        }

        [Fact]
        public void The_guard_rejects_that_whole_braking_zone()
        {
            // Every frame in the zone that satisfies the rise/fall test has two or more wheels already
            // pegged, so nothing in it teaches at all. That is the intended outcome: this zone contains
            // no honest onset to learn from - by the time the aggregate crossed, both fronts had been
            // fully locked for nine frames.
            double? taught = RunLock(Fh6Basis, Fh6G, Fh6Pegged, LockCrossingGate.DefaultMinSaturatedWheelsForRejection);
            _out.WriteLine($"guard on  -> {(taught.HasValue ? taught.Value.ToString("F3") : "no crossing")}");
            Assert.Null(taught);
        }

        [Fact]
        public void One_pegged_wheel_is_not_enough_to_reject()
        {
            // The threshold is TWO. A single locked wheel during trail braking is ordinary and its
            // crossing is real evidence; rejecting it would thin the pool for no measured gain.
            double[] basis = { 30, 33, 36, 39, 42, 45, 48, 52, 56, 60, 64 };
            double[] g = { 0.80, 0.85, 0.89, 0.92, 0.94, 0.95, 0.94, 0.91, 0.87, 0.82, 0.76 };
            int[] onePegged = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

            double? taught = RunLock(basis, g, onePegged, LockCrossingGate.DefaultMinSaturatedWheelsForRejection);
            _out.WriteLine($"one pegged wheel -> {(taught.HasValue ? taught.Value.ToString("F1") : "no crossing")}");
            Assert.NotNull(taught);
        }

        [Fact]
        public void A_clean_crossing_with_no_saturation_is_untouched_on_both_channels()
        {
            // The half of the requirement that matters most: correct data must survive. This is the
            // probe's own "progressive crossing" shape, the canonical genuine event.
            double[] basis = { 30, 33, 36, 39, 42, 45, 48, 52, 56, 60, 64 };
            double[] g = { 0.80, 0.85, 0.89, 0.92, 0.94, 0.95, 0.94, 0.91, 0.87, 0.82, 0.76 };
            int[] none = new int[basis.Length];

            double? lockOff = RunLock(basis, g, none, threshold: 0);
            double? lockOn = RunLock(basis, g, none, LockCrossingGate.DefaultMinSaturatedWheelsForRejection);
            double? slipOff = RunSlip(basis, g, none, threshold: 0);
            double? slipOn = RunSlip(basis, g, none, SlipCrossingGate.DefaultMinSaturatedWheelsForRejection);

            _out.WriteLine($"lock {lockOff} -> {lockOn}   slip {slipOff} -> {slipOn}");
            Assert.Equal(lockOff, lockOn);
            Assert.Equal(slipOff, slipOn);
            Assert.NotNull(lockOn);
            Assert.NotNull(slipOn);
        }

        [Fact]
        public void Slip_rejects_a_saturated_crossing_too()
        {
            // Slip gains MORE from the guard than Lock does - three corpus logs sat at exactly 100.0.
            // The mechanism is identical; the gates are separate classes on purpose.
            double? withGuard = RunSlip(Fh6Basis, Fh6G, Fh6Pegged, SlipCrossingGate.DefaultMinSaturatedWheelsForRejection);
            double? without = RunSlip(Fh6Basis, Fh6G, Fh6Pegged, threshold: 0);

            _out.WriteLine($"slip guard off -> {without}, on -> {withGuard}");
            Assert.NotNull(without);
            Assert.Null(withGuard);
        }

        [Fact]
        public void Both_channels_ship_the_guard_on_at_two_wheels()
        {
            // A drift in either default would silently reopen the defect, so both are pinned.
            Assert.Equal(2, LockCrossingGate.DefaultMinSaturatedWheelsForRejection);
            Assert.Equal(2, SlipCrossingGate.DefaultMinSaturatedWheelsForRejection);
            Assert.Equal(2, new LockCrossingGate().MinSaturatedWheelsForRejection);
            Assert.Equal(2, new SlipCrossingGate().MinSaturatedWheelsForRejection);
        }

        [Fact]
        public void A_caller_that_does_not_report_saturation_keeps_the_previous_behaviour()
        {
            // The parameter defaults to 0 - "the caller did not report saturation" - so every
            // pre-existing call site and test behaves exactly as it did before the guard existed. This
            // is what makes the guard's arrival a pure addition rather than a silent retune.
            var gate = new LockCrossingGate { WalkBackGBandFraction = 0.0 };
            for (int i = 0; i < 6; i++) gate.Observe(10.0, 10.0, 0.50, atLimit: true, dtSeconds: Dt);

            double? taught = null;
            for (int i = 0; i < Fh6Basis.Length; i++)
            {
                gate.Observe(Fh6Basis[i], Fh6Basis[i], Fh6G[i], atLimit: true, dtSeconds: Dt);
                if (gate.CrossedThisFrame) { taught = gate.TeachingBasis(Fh6Basis[i]); break; }
            }

            _out.WriteLine($"unreported saturation -> {taught}");
            Assert.NotNull(taught);
            Assert.Equal(98.249, taught.Value, 3);
        }

        [Fact]
        public void Zero_is_the_kill_switch_and_the_channels_are_independent()
        {
            // The owner's standing requirement that either channel can be switched off without touching
            // the other - the two gates are separate objects with separate settings.
            double? lockOff = RunLock(Fh6Basis, Fh6G, Fh6Pegged, threshold: 0);
            double? slipOn = RunSlip(Fh6Basis, Fh6G, Fh6Pegged, SlipCrossingGate.DefaultMinSaturatedWheelsForRejection);

            Assert.NotNull(lockOff);   // Lock's guard disabled - taught as before
            Assert.Null(slipOn);       // Slip's guard still active
        }
    }
}

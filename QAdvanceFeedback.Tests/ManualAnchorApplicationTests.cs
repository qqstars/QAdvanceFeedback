using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A CONFIGURED MANUAL SCALE IS APPLIED UNCONDITIONALLY (owner, 2026-09-25 - v1.1.0).
    /// <para/>
    /// The owner's own statement of the contract: maturity decides WHICH NUMBER THE SETTINGS PAGE SHOWS
    /// when Auto is toggled off - the learned value once learning is mature, that source's shipped
    /// reference for a known source that is not mature yet, "---" for an unknown one. It has nothing to
    /// do with whether an already-configured value reaches the output: "if the applied SMax/S90/S75 is
    /// a valid number, then ALWAYS use the set number".
    /// <para/>
    /// WHAT THIS REPLACED. <c>ManualOverrideGate</c> used to withhold the manual value from the ENGINE
    /// until cold start finished AND 30s of driving had accumulated. On the owner's capture
    /// (session-20260925-214840, Viper source, manual 10/6.5/3) that gate could never open:
    /// <c>CeilingHandoverConfidence</c> early-returns 0.0 on an empty at-limit distribution, and that
    /// session taught ZERO at-limit samples on either channel. The channel silently ran on its decaying
    /// cold-start reference instead, with nothing in the UI or the log saying so.
    /// <para/>
    /// THERE WERE NO TESTS ON THIS PATH BEFORE - which is why the defect survived. These cover it.
    /// </summary>
    public class ManualAnchorApplicationTests
    {
        private readonly ITestOutputHelper _out;
        public ManualAnchorApplicationTests(ITestOutputHelper output) { _out = output; }

        private const string Game = "F12025";
        private const string Car = "Red Bull Racing";
        private const string Source = "NCalc:viperish";

        private static NormalizedWheelLockSlipEngine FreshEngine() => new NormalizedWheelLockSlipEngine();

        private static double LockOut(NormalizedWheelLockSlipEngine e, double wheelValue, double g = 2.0)
            => e.Compute(TestFrames.BrakingSampleFor(g), Corners.Uniform(wheelValue), Corners.Zero,
                         Game, Car, lockSourceIdentity: Source).LockAll;

        [Fact]
        public void THE_OWNERS_CASE_a_manual_SMax_of_ten_drives_a_675_reading_to_full_scale()
        {
            // The exact configuration from the report: manual 10 / 6.5 / 3, against a source whose
            // aggregate reads 67.5 at the limit. 67.5 * 80/10 saturates, so the cue should be at full
            // strength - not the ~80 the learned 67.5 ceiling was producing.
            var e = FreshEngine();
            e.LockManualAnchors = ManualAnchors.Of(10.0, 6.5, 3.0);

            double published = LockOut(e, 67.5);
            _out.WriteLine($"manual 10/6.5/3 with source 67.5 -> LockAll {published:F2}");

            Assert.True(e.LockManualAnchorsApplied, "the configured value must be in force");
            Assert.True(published > 95.0, $"expected full scale, got {published:F2}");
        }

        [Fact]
        public void It_applies_on_the_very_first_frame_with_no_warm_up()
        {
            // No driving time, no learned evidence, nothing. The number in the box IS the scale.
            var e = FreshEngine();
            e.LockManualAnchors = ManualAnchors.Of(10.0, 6.5, 3.0);

            double first = LockOut(e, 67.5);
            _out.WriteLine($"first frame -> {first:F2}, applied={e.LockManualAnchorsApplied}");
            Assert.True(e.LockManualAnchorsApplied);
            Assert.True(first > 95.0);
        }

        [Fact]
        public void It_applies_even_though_the_at_limit_distribution_is_empty()
        {
            // THE EXACT BLOCKER. CeilingHandoverConfidence returns 0.0 for an empty primary distribution,
            // which the old gate required to be >= 0.95 - so this configuration could never take effect.
            var e = FreshEngine();
            e.LockManualAnchors = ManualAnchors.Of(10.0, 6.5, 3.0);

            for (int i = 0; i < 120; i++) LockOut(e, 67.5);

            double confidence = e.LockScaleLearner.CeilingHandoverConfidence(Game, Car, Source);
            _out.WriteLine($"handover confidence {confidence:F3} (the old gate needed >= 0.95)");
            Assert.True(confidence < 0.95, "this scenario must genuinely have an immature learner");
            Assert.True(e.LockManualAnchorsApplied, "and the manual value must apply regardless");
        }

        [Fact]
        public void Auto_still_publishes_the_learned_or_cold_value()
        {
            // The other half of the contract: with Auto on (Active = false) nothing is overridden.
            var e = FreshEngine();
            e.LockManualAnchors = ManualAnchors.None;

            double published = LockOut(e, 67.5);
            _out.WriteLine($"auto -> {published:F2}");
            Assert.False(e.LockManualAnchorsApplied);
            Assert.True(published < 95.0, "auto must not saturate the way the manual 10 does");
        }

        [Fact]
        public void A_blank_or_invalid_manual_value_never_takes_over()
        {
            // "---" reaches the engine as Active = false; a zero or negative ceiling would divide the
            // rescale by nothing, so it is refused even if something marks it Active.
            var blank = FreshEngine();
            blank.LockManualAnchors = ManualAnchors.None;
            LockOut(blank, 67.5);
            Assert.False(blank.LockManualAnchorsApplied);

            var zero = FreshEngine();
            zero.LockManualAnchors = ManualAnchors.Of(0.0, 0.0, 0.0);
            LockOut(zero, 67.5);
            Assert.False(zero.LockManualAnchorsApplied, "a zero ceiling must not be applied");
        }

        [Fact]
        public void Learning_keeps_running_and_stays_readable_while_manual_is_in_force()
        {
            // The owner's own description of the intended behaviour: "the learning will keep doing behind
            // the scene, and saving the learning result parameters into the parameters file, BUT will
            // only apply the manual SMax/S90/S75". So the learner must still accumulate - otherwise
            // toggling back to Auto would start from nothing and the UI would have no value to show.
            var manual = FreshEngine();
            manual.LockManualAnchors = ManualAnchors.Of(10.0, 6.5, 3.0);
            var auto = FreshEngine();

            TestFrames.WarmLockWithCrossings(manual, 4.0, 70.0, gameId: Game, carId: Car, lockSourceIdentity: Source);
            TestFrames.WarmLockWithCrossings(auto, 4.0, 70.0, gameId: Game, carId: Car, lockSourceIdentity: Source);

            double? manualLearned = manual.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            double? autoLearned = auto.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _);

            _out.WriteLine($"learned under manual {manualLearned}, under auto {autoLearned}");
            Assert.NotNull(manualLearned);
            Assert.Equal(autoLearned.Value, manualLearned.Value, 6);
        }

        [Fact]
        public void Toggling_back_to_auto_is_immediate()
        {
            // Nothing latches, so a driver can compare the two without restarting anything.
            var e = FreshEngine();
            e.LockManualAnchors = ManualAnchors.Of(10.0, 6.5, 3.0);
            double withManual = LockOut(e, 67.5);
            Assert.True(e.LockManualAnchorsApplied);

            e.LockManualAnchors = ManualAnchors.None;
            double afterToggle = LockOut(e, 67.5);

            _out.WriteLine($"manual {withManual:F2} -> auto {afterToggle:F2}");
            Assert.False(e.LockManualAnchorsApplied);
            Assert.True(afterToggle < withManual);
        }

        [Fact]
        public void Slip_behaves_the_same_and_the_two_channels_are_independent()
        {
            // SLIP NEEDS THROTTLE, NOT BRAKE. A first version of this drove the Slip channel with
            // BrakingSampleFor and got 0.00 with the override never armed - correctly, since the brake
            // gate suppresses Slip outright (see Core.LegacyThresholds: brake is checked FIRST and takes
            // priority). Slip is exercised on power, so the sample must be one.
            var e = FreshEngine();
            e.SlipManualAnchors = ManualAnchors.Of(10.0, 6.5, 3.0);

            var outp = e.Compute(AcceleratingSample(0.6), Corners.Zero, Corners.Uniform(67.5),
                                 Game, Car, slipSourceIdentity: Source);

            _out.WriteLine($"slip manual -> {outp.SlipAll:F2}, lock applied={e.LockManualAnchorsApplied}, slip applied={e.SlipManualAnchorsApplied}");
            Assert.True(e.SlipManualAnchorsApplied);
            Assert.True(outp.SlipAll > 95.0, $"expected full scale, got {outp.SlipAll:F2}");
            Assert.False(e.LockManualAnchorsApplied, "configuring one channel must not arm the other");
        }

        /// <summary>A frame on POWER - accelerating, throttle open, brake released - which is what the
        /// Slip channel's own pedal gate requires. TestFrames only offers a braking sample.</summary>
        private static ITelemetrySample AcceleratingSample(double gMagnitude)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 101.0, longitudinalG: gMagnitude, brakePercent: 0.0, throttlePercent: 90.0);
            return new TelemetrySample(newFrame, oldFrame, System.DateTime.UtcNow, System.TimeSpan.FromMilliseconds(16));
        }
    }
}

using System.IO;
using QAdvanceFeedback;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE TEACHING FLOOR IS PER SOURCE, SCALED BY THAT SOURCE'S OWN COLD-START REFERENCE.
    /// <para/>
    /// Measured on the owner's 1.1.0 Viper capture: the flat floor of 10 was calibrated for a source
    /// reading ~70-85 at its limit (our Raw), and on Viper's Slip channel - p90 = 4.4 - it discarded
    /// 99.7% of the signal, leaving 20 teachable frames out of 6000. SMax was then computed from a
    /// handful of outliers and gave 18.1 and 11.9 on two runs of the same car and track. With the
    /// per-source floor it reads 1719 frames and settles at 7.1 / 7.3.
    /// <para/>
    /// THE BINDING CONSTRAINT IS THAT RAW AND SHAKEIT DO NOT MOVE (owner: "DONOT break the Raw/ShakeIt
    /// behavior and algorithm"), which the Math.Min shape guarantees arithmetically.
    /// </summary>
    public class CalibrationObservationFloorTests
    {
        private readonly ITestOutputHelper _out;
        public CalibrationObservationFloorTests(ITestOutputHelper output) { _out = output; }

        private const double Absolute = NormalizedWheelLockSlipEngine.MinRawForCalibrationObservation;

        private static KeyedScaleLearner Learner(bool isLock) => new KeyedScaleLearner(isLock);

        [Fact]
        public void Raw_and_ShakeIt_keep_the_absolute_floor_exactly()
        {
            // Their references are 85 (Lock) and 75 (Slip); a quarter of either is ABOVE 10, so Math.Min
            // returns the absolute floor unchanged. This is the no-regression guarantee.
            foreach (bool isLock in new[] { true, false })
            {
                var learner = Learner(isLock);

                // Raw's identity, built the same way the engine builds it.
                string raw = RawSourceFallback.RawIdentity(isLock);
                double rawFloor = learner.CalibrationObservationFloor(raw, Absolute);
                _out.WriteLine($"{(isLock ? "Lock" : "Slip")} Raw: floor={rawFloor}");
                Assert.Equal(Absolute, rawFloor, 6);

                // ShakeIt's, from a channel configured for that mode.
                var shakeIt = new WheelChannelSettings { SourceMode = SourceMode.ShakeIt };
                shakeIt.ResetSourcesForCurrentMode(isLock);
                string shakeItIdentity = SourceIdentity.Compute(
                    shakeIt.SourceFrontLeft, shakeIt.ScriptTypeFrontLeft.ToString(),
                    shakeIt.SourceFrontRight, shakeIt.ScriptTypeFrontRight.ToString(),
                    shakeIt.SourceRearLeft, shakeIt.ScriptTypeRearLeft.ToString(),
                    shakeIt.SourceRearRight, shakeIt.ScriptTypeRearRight.ToString());
                double shakeItFloor = learner.CalibrationObservationFloor(shakeItIdentity, Absolute);
                _out.WriteLine($"{(isLock ? "Lock" : "Slip")} ShakeIt: floor={shakeItFloor}");
                Assert.Equal(Absolute, shakeItFloor, 6);
            }
        }

        [Fact]
        public void An_unknown_source_with_no_reference_keeps_the_absolute_floor()
        {
            // Nothing to scale by, so nothing is assumed.
            Assert.Equal(Absolute, Learner(true).CalibrationObservationFloor("NCalc:deadbeef", Absolute), 6);
            Assert.Equal(Absolute, Learner(true).CalibrationObservationFloor(null, Absolute), 6);
        }

        [Fact]
        public void A_small_scale_source_gets_a_floor_from_its_own_reference()
        {
            // Viper: 15 (Lock) and 10 (Slip) -> 3.75 and 2.5. This is what reopens the Slip signal.
            string lockViper = KnownSourceColdStartReference.ViperSourceIdentity(true);
            string slipViper = KnownSourceColdStartReference.ViperSourceIdentity(false);

            Assert.Equal(15.0 * 0.25, Learner(true).CalibrationObservationFloor(lockViper, Absolute), 6);
            Assert.Equal(10.0 * 0.25, Learner(false).CalibrationObservationFloor(slipViper, Absolute), 6);
        }

        [Fact]
        public void A_CUSTOM_sources_floor_comes_from_the_drivers_own_configured_reference()
        {
            // The owner's explicit requirement: "for the custom type, MAKE SURE respect the cold-start
            // reference points as well". A hand-written source has no shipped entry, so the floor has
            // to come from the number its driver supplied - which ApplyCustomColdStart pushes into the
            // learner every frame.
            const string custom = "NCalc:aaaa~NCalc:bbbb~NCalc:cccc~NCalc:dddd";
            var learner = Learner(true);

            // Before the driver configures one, there is nothing to scale by.
            Assert.Equal(Absolute, learner.CalibrationObservationFloor(custom, Absolute), 6);

            learner.ConfiguredColdStartIdentity = custom;
            learner.ConfiguredColdStartSMax = 12.0;
            Assert.Equal(3.0, learner.CalibrationObservationFloor(custom, Absolute), 6);

            // And it applies ONLY to that source - another source is unaffected by it.
            Assert.Equal(Absolute, learner.CalibrationObservationFloor("NCalc:other", Absolute), 6);

            // A large custom reference still cannot RAISE the floor above the absolute one.
            learner.ConfiguredColdStartSMax = 90.0;
            Assert.Equal(Absolute, learner.CalibrationObservationFloor(custom, Absolute), 6);
        }

        [Fact]
        public void The_floor_can_only_ever_lower_the_bar_never_raise_it()
        {
            // The property that makes this safe to apply everywhere: no source can become HARDER to
            // learn from than it was before.
            var learner = Learner(true);
            learner.ConfiguredColdStartIdentity = "x";
            foreach (double reference in new[] { 1.0, 5.0, 15.0, 40.0, 85.0, 100.0 })
            {
                learner.ConfiguredColdStartSMax = reference;
                Assert.True(learner.CalibrationObservationFloor("x", Absolute) <= Absolute);
            }
        }

        [Fact]
        public void The_engine_uses_the_per_source_floor_for_both_of_its_tests()
        {
            // Both the enclosing eligibility test and the SMax-snapshot test must use it, or a snapshot
            // below the old constant would still be thrown away.
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            string engine = File.ReadAllText(Path.Combine(
                dir.FullName, "QAdvanceFeedback", "Core", "Normalized", "NormalizedWheelLockSlipEngine.cs"));

            Assert.Contains("double observationFloor =", engine);
            Assert.Contains("calibrationBasisConfigured >= observationFloor", engine);
            Assert.Contains("smaxTeachingBasis >= observationFloor", engine);
        }
    }
}

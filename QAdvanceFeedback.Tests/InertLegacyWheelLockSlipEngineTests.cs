using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Proves the stub path for Layer 3 (see <c>..\Private\README.md</c> and <c>AlgorithmFactory</c>'s
    /// own remarks) is genuinely inert and never throws, regardless of what it is given - this is the
    /// exact object <c>AlgorithmFactory.CreateLegacyEngine</c> falls back to when
    /// <c>Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs</c> is absent from a build.
    /// </summary>
    public class InertLegacyWheelLockSlipEngineTests
    {
        private static ITelemetrySample SampleWithReadings()
        {
            var newFrame = new TelemetryFrame(groundSpeedKmh: 120.0, speedKmh: 120.0, rpm: 6000.0, gear: "3",
                brakePercent: 80.0, throttlePercent: 0.0, clutchPercent: 0.0, lateralLocalVelocity: 1.5);
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 130.0, speedKmh: 130.0, rpm: 6500.0, gear: "3");
            return new TelemetrySample(newFrame, oldFrame, System.DateTime.UtcNow, System.TimeSpan.FromMilliseconds(16));
        }

        [Fact]
        public void Compute_never_throws_for_a_sample_with_real_readings()
        {
            var engine = new InertLegacyWheelLockSlipEngine();
            var result = engine.Compute(SampleWithReadings());
            Assert.NotNull(result);
        }

        [Fact]
        public void Compute_never_throws_for_an_empty_sample()
        {
            var engine = new InertLegacyWheelLockSlipEngine();
            var sample = new TelemetrySample(TelemetryFrame.Empty, TelemetryFrame.Empty, System.DateTime.UtcNow, null);
            var result = engine.Compute(sample);
            Assert.NotNull(result);
        }

        [Fact]
        public void Compute_never_throws_for_a_null_sample()
        {
            var engine = new InertLegacyWheelLockSlipEngine();
            var result = engine.Compute(null);
            Assert.NotNull(result);
        }

        [Fact]
        public void Every_published_number_is_exactly_zero_regardless_of_input()
        {
            var engine = new InertLegacyWheelLockSlipEngine();
            var result = engine.Compute(SampleWithReadings());

            Assert.Equal(Corners.Zero, result.LockWheels);
            Assert.Equal(0.0, result.LockFront);
            Assert.Equal(0.0, result.LockRear);
            Assert.Equal(0.0, result.LockLeft);
            Assert.Equal(0.0, result.LockRight);
            Assert.Equal(0.0, result.LockAll);

            Assert.Equal(Corners.Zero, result.SlipWheels);
            Assert.Equal(0.0, result.SlipFront);
            Assert.Equal(0.0, result.SlipRear);
            Assert.Equal(0.0, result.SlipLeft);
            Assert.Equal(0.0, result.SlipRight);
            Assert.Equal(0.0, result.SlipAll);
        }

        [Fact]
        public void Thresholds_parameter_is_accepted_and_ignored_still_reads_zero()
        {
            var engine = new InertLegacyWheelLockSlipEngine();
            var thresholds = new LegacyThresholds { LockBrakeThresholdPercent = 1.0, SlipBrakeThresholdPercent = 1.0, SlipThrottleThresholdPercent = 1.0 };

            var result = engine.Compute(SampleWithReadings(), thresholds);

            Assert.Equal(0.0, result.LockAll);
            Assert.Equal(0.0, result.SlipAll);
        }

        [Fact]
        public void Repeated_calls_are_stable_not_accumulating_any_hidden_state()
        {
            var engine = new InertLegacyWheelLockSlipEngine();
            var sample = SampleWithReadings();

            var first = engine.Compute(sample);
            var second = engine.Compute(sample);
            var third = engine.Compute(sample);

            Assert.Equal(0.0, first.LockAll);
            Assert.Equal(0.0, second.LockAll);
            Assert.Equal(0.0, third.LockAll);
        }
    }
}

using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// FRAME-RATE INDEPENDENCE (telemetry-integrity pass, item 4). <see cref="BrakeSpeedSlipModel"/>'s
    /// brake/throttle terms compare a frame-to-frame ground-speed/RPM ratio without ever having
    /// considered how much wall-clock time separated the two frames - a raw delta that naturally scales
    /// with <c>dt</c> (a longer gap between samples accumulates more real-world change for the SAME
    /// underlying rate). These tests hold the underlying physical rate of change constant (a steady
    /// deceleration, RPM held fixed so only ground speed carries the change - keeping the comparison
    /// exactly linear rather than approximate) and vary only the sampling interval, so the delta at 2x
    /// the nominal interval is EXACTLY 2x the delta at 1x it - precisely the scenario
    /// <see cref="BrakeSpeedSlipModel.NominalDtSeconds"/>'s own remarks describe.
    /// </summary>
    public class DtNormalizationTests
    {
        private const int FL = 0;

        private static TelemetrySample Sample(TimeSpan? dt, double oldSpeedKmh, double newSpeedKmh, double rpm, double brakePercent = 70.0)
        {
            var newFrame = new TelemetryFrame(
                speedKmh: newSpeedKmh, rpm: rpm, gear: "3",
                brakePercent: brakePercent, throttlePercent: 0.0, clutchPercent: 0.0, lateralLocalVelocity: 0.0);
            var oldFrame = new TelemetryFrame(speedKmh: oldSpeedKmh, rpm: rpm, gear: "3");
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, dt);
        }

        /// <summary>THE ACCEPTANCE TEST. RPM is held IDENTICAL old-to-new in both scenarios (removing
        /// any RPM-driven nonlinearity from the comparison), so the brake term's own delta is a plain
        /// linear function of the ground-speed drop alone: a 10 km/h drop sampled across one nominal
        /// frame and a 20 km/h drop sampled across two nominal frames represent the EXACT SAME
        /// deceleration rate (10 km/h per nominal frame either way) - our own dt normalisation must
        /// therefore produce IDENTICAL readings for both, where the un-normalised formula would have
        /// read the second scenario as roughly twice as strong for no physical reason.</summary>
        [Fact]
        public void Identical_deceleration_rate_reads_the_same_regardless_of_sampling_interval()
        {
            double nominalDt = BrakeSpeedSlipModel.NominalDtSeconds;

            var oneNominalFrame = Sample(TimeSpan.FromSeconds(nominalDt), oldSpeedKmh: 110.0, newSpeedKmh: 100.0, rpm: 6000.0);
            var twoNominalFrames = Sample(TimeSpan.FromSeconds(nominalDt * 2.0), oldSpeedKmh: 110.0, newSpeedKmh: 90.0, rpm: 6000.0);

            var fastResult = BrakeSpeedSlipModel.Compute(oneNominalFrame, FL);
            var slowResult = BrakeSpeedSlipModel.Compute(twoNominalFrames, FL);

            Assert.Equal(fastResult.Lock, slowResult.Lock, 9);

            // Pin the actual magnitude too (not just "the two agree") - a future regression that
            // normalises BOTH scenarios identically wrong (e.g. always dividing by a hard-coded 2x)
            // would still pass the equality assertion above but fail this one.
            double delta = Math.Abs(110.0 / 6000.0 - 100.0 / 6000.0); // = 10/6000, the ONE-nominal-frame delta
            double brakeGainRamp = (70.0 - 55.0) / (90.0 - 55.0);
            double expected = delta * 4000.0 * brakeGainRamp; // dt factor is 1.0 at exactly the nominal interval
            Assert.Equal(expected, fastResult.Lock, 9);
            Assert.Equal(expected, slowResult.Lock, 9);
        }

        [Fact]
        public void A_sample_at_exactly_the_nominal_interval_reproduces_the_pre_normalisation_reading()
        {
            var withDt = Sample(TimeSpan.FromSeconds(BrakeSpeedSlipModel.NominalDtSeconds), oldSpeedKmh: 110.0, newSpeedKmh: 90.0, rpm: 6000.0);
            var withoutDt = Sample(null, oldSpeedKmh: 110.0, newSpeedKmh: 90.0, rpm: 6000.0);

            var withDtResult = BrakeSpeedSlipModel.Compute(withDt, FL);
            var withoutDtResult = BrakeSpeedSlipModel.Compute(withoutDt, FL);

            Assert.Equal(withoutDtResult.Lock, withDtResult.Lock, 9);
        }

        /// <summary>Implausible dt (a loading screen/stall, already outside
        /// <see cref="TelemetryLearningGate.MaxPlausibleDtSeconds"/>) must fall back to the un-normalised
        /// reading rather than dividing by a gap that was never a real driving interval - i.e. no spike,
        /// and no silent collapse toward zero either.</summary>
        [Fact]
        public void An_implausible_dt_falls_back_to_the_unnormalised_reading_rather_than_a_spike()
        {
            var implausible = Sample(TimeSpan.FromSeconds(TelemetryLearningGate.MaxPlausibleDtSeconds + 1.0), oldSpeedKmh: 110.0, newSpeedKmh: 90.0, rpm: 6000.0);
            var noDt = Sample(null, oldSpeedKmh: 110.0, newSpeedKmh: 90.0, rpm: 6000.0);

            var implausibleResult = BrakeSpeedSlipModel.Compute(implausible, FL);
            var noDtResult = BrakeSpeedSlipModel.Compute(noDt, FL);

            Assert.Equal(noDtResult.Lock, implausibleResult.Lock, 9);
        }

        /// <summary>WheelRotationLockFilter's own smoothing rate is a per-frame alpha - also a dt
        /// artefact (see that class's own remarks). Confirms the filter settles to (approximately) the
        /// SAME steady-state lock reading whether fed at the nominal interval or at a coarser one, for a
        /// sustained, unchanging input - the wall-clock TIME to settle differs (by design - a coarser
        /// sample rate still needs the same wall-clock time constant), but the eventual reading does not
        /// depend on frame rate.</summary>
        [Fact]
        public void WheelRotationLockFilter_settles_to_the_same_steady_state_regardless_of_frame_rate()
        {
            double nominalDt = BrakeSpeedSlipModel.NominalDtSeconds;

            var fastFilter = new WheelRotationLockFilter();
            var slowFilter = new WheelRotationLockFilter();

            double fastLast = 0.0, slowLast = 0.0;
            for (int i = 0; i < 400; i++)
            {
                fastLast = fastFilter.Compute(2.0, 10.0, dt: TimeSpan.FromSeconds(nominalDt));
            }
            for (int i = 0; i < 200; i++) // half as many calls, but each spans 2x the wall-clock time
            {
                slowLast = slowFilter.Compute(2.0, 10.0, dt: TimeSpan.FromSeconds(nominalDt * 2.0));
            }

            Assert.Equal(fastLast, slowLast, 3);
        }
    }
}

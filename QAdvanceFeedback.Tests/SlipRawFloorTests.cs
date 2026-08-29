using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// SLIP-ONLY RAW FLOOR. The F1 25 fix removed <c>Math.Max(effectiveGripUtilization, calibratedMean)</c>
    /// for BOTH channels, but every log behind that decision was a braking log. The defect the max
    /// originally fixed is a wheelspin phenomenon - achieved chassis acceleration DROPS once a driven
    /// wheel spins, so anything other than the source's own reading understates a full spin - and the
    /// owner's own Raw-source log shows the consequence: Lock passes through this layer intact
    /// (Raw p99 83.5 -&gt; Normalized p99 83.6) while Slip loses nearly half its magnitude
    /// (71.2 -&gt; 40.0). These tests pin the floor that restores Slip, and pin that Lock is untouched.
    /// </summary>
    public class SlipRawFloorTests
    {
        private static ITelemetrySample AcceleratingSample(double gMagnitude, double throttlePercent = 90.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 101.0, longitudinalG: gMagnitude, throttlePercent: throttlePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        private static ITelemetrySample BrakingSample(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        /// <summary>Drives a channel's learned SMax well ABOVE the canonical anchor by feeding sustained
        /// at-limit raw values near 100, so that <c>Rescale</c>'s own <c>80 / SMax</c> factor becomes
        /// less than 1 and the calibrated value sits BELOW the raw reading. That is precisely the regime
        /// where the floor does or does not engage.</summary>
        private const double HighRaw = 100.0;
        private const double ProbeRaw = 50.0;

        [Fact]
        public void SlipSeverityNeverReadsBelowTheSourcesOwnBasis()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 1.2;

            for (int i = 0; i < 400; i++)
                engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(HighRaw));

            engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(ProbeRaw));
            engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(ProbeRaw));

            // The ceiling must actually be above the canonical anchor, otherwise this test would pass
            // trivially with or without the floor and would prove nothing.
            Assert.True(engine.SlipScaleCeiling.HasValue && engine.SlipScaleCeiling.Value > KeyedScaleLearner.CanonicalAtLimitAnchor,
                $"test setup failed to drive SMax above the anchor (ceiling={engine.SlipScaleCeiling}) - "
                + "without that, the unfloored value would not be below Raw and this test is vacuous");

            // With SMax above the anchor, the UNFLOORED value would be ProbeRaw * (80/SMax) < ProbeRaw.
            // Landing exactly on ProbeRaw is therefore only possible because the floor engaged.
            double unflooredWouldBe = ProbeRaw * (KeyedScaleLearner.CanonicalAtLimitAnchor / engine.SlipScaleCeiling.Value);
            Assert.True(unflooredWouldBe < ProbeRaw - 1e-6, $"sanity: unfloored {unflooredWouldBe} should be below {ProbeRaw}");

            Assert.Equal(ProbeRaw, engine.SlipCarLevelSeverity, 6);
        }

        [Fact]
        public void LockIsNotFloored_TheF125FixKeepsItsBehaviourExactly()
        {
            // The mirror image of the test above, on the channel that must NOT change. Lock deliberately
            // still allows a learned SMax above the canonical anchor to pull severity BELOW Raw - that is
            // what the F1 25 wet/dry fix established, and re-flooring Lock would undo it.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;

            for (int i = 0; i < 400; i++)
                engine.Compute(BrakingSample(peak), Corners.Uniform(HighRaw), Corners.Zero);

            engine.Compute(BrakingSample(peak), Corners.Uniform(ProbeRaw), Corners.Zero);
            engine.Compute(BrakingSample(peak), Corners.Uniform(ProbeRaw), Corners.Zero);

            Assert.True(engine.LockCarLevelSeverity < ProbeRaw,
                $"Lock severity ({engine.LockCarLevelSeverity}) must still be free to read below Raw ({ProbeRaw}) - "
                + "the Slip floor must not leak into this channel");
        }

        [Fact]
        public void TheFloorOnlyRaises_ItNeverCapsSlip()
        {
            // A floor must be exactly that. When the calibrated value is ALREADY above the source's own
            // reading (a learned SMax below the anchor, the ordinary cold/early case), the floor must not
            // pull it back down.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 1.2;
            const double lowRaw = 20.0;

            for (int i = 0; i < 400; i++)
                engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(lowRaw));

            engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(lowRaw));
            engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(lowRaw));

            Assert.True(engine.SlipCarLevelSeverity >= lowRaw - 1e-6,
                "the floor holds at the source's own reading");
            Assert.True(engine.SlipCarLevelSeverity > lowRaw,
                $"a learned SMax below the anchor must still AMPLIFY ({engine.SlipCarLevelSeverity} vs raw {lowRaw}) - "
                + "the floor raises, it never caps");
        }

        [Fact]
        public void SlipLearningKeepsRunningWithTheFloorInPlace()
        {
            // The owner's explicit requirement. The floor is applied to `severity`, which is computed
            // AFTER every learner call and from a different variable (the learners are fed
            // calibrationBasisConfigured - the NATIVE source aggregate). So there is no feedback path and
            // Slip's own SMax must keep converging on the evidence it is shown.
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 1.2;

            for (int i = 0; i < 400; i++)
                engine.Compute(AcceleratingSample(peak), Corners.Zero, Corners.Uniform(HighRaw));
            double? afterHighEvidence = engine.SlipScaleCeiling;

            Assert.True(afterHighEvidence.HasValue, "Slip must have learned a ceiling at all");
            Assert.True(afterHighEvidence.Value > KeyedScaleLearner.CanonicalAtLimitAnchor,
                $"Slip's learned ceiling ({afterHighEvidence}) must track the high evidence it was shown, "
                + "proving the floor did not freeze or bias learning");
        }
    }
}

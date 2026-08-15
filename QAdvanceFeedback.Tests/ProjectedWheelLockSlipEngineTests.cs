using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class ProjectedWheelLockSlipEngineTests
    {
        private static NormalizedWheelLockSlipResult Normalized(Corners lockWheels, Corners slipWheels)
            => new NormalizedWheelLockSlipResult(
                lockWheels, lockWheels.FrontLeft, lockWheels.FrontRight, lockWheels.FrontLeft, lockWheels.FrontRight, lockWheels.FrontLeft,
                slipWheels, slipWheels.FrontLeft, slipWheels.FrontRight, slipWheels.FrontLeft, slipWheels.FrontRight, slipWheels.FrontLeft);

        private static ProjectorSettings IdentitySettings()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Linear); // start 0, end 100 by field default -> exact identity
            return s;
        }

        [Fact]
        public void With_identity_curve_and_pulse_disabled_projected_equals_normalized_input()
        {
            var lockProjector = new OutputProjector(IdentitySettings());
            var slipProjector = new OutputProjector(IdentitySettings());
            var lockPulse = new PulseSettings { Enabled = false };
            var slipPulse = new PulseSettings { Enabled = false };
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, lockPulse, slipProjector, slipPulse);

            var lockWheels = new Corners(12.0, 34.0, 56.0, 78.0);
            var slipWheels = new Corners(1.0, 2.0, 3.0, 4.0);
            var normalized = Normalized(lockWheels, slipWheels);

            ProjectedWheelLockSlipResult result = engine.Compute(normalized, dtSeconds: 0.016);

            Assert.Equal(12.0, result.LockWheels.FrontLeft, 6);
            Assert.Equal(34.0, result.LockWheels.FrontRight, 6);
            Assert.Equal(1.0, result.SlipWheels.FrontLeft, 6);
        }

        [Fact]
        public void Each_of_the_nine_targets_pulses_independently()
        {
            var lockProjector = new OutputProjector(IdentitySettings());
            var slipProjector = new OutputProjector(IdentitySettings());
            var lockPulse = new PulseSettings { Enabled = true, GapMs = 400, MinValue = 0 };
            var slipPulse = new PulseSettings { Enabled = true, GapMs = 400, MinValue = 0 };
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, lockPulse, slipProjector, slipPulse);

            // FrontLeft pinned at 100 (will pulse); FrontRight at 50 (will not pulse - below max).
            var lockWheels = new Corners(100.0, 50.0, 50.0, 50.0);
            var normalized = Normalized(lockWheels, Corners.Zero);

            engine.Compute(normalized, 0.0);              // t=0 for FrontLeft's pulse
            ProjectedWheelLockSlipResult afterHalfGap = engine.Compute(normalized, 0.2); // t=200 = gap/2

            // FrontLeft (pulsing) should have moved noticeably away from 100 by half a gap in...
            Assert.True(afterHalfGap.LockWheels.FrontLeft < 90.0,
                $"FrontLeft (pinned at 100) should be mid-pulse, read {afterHalfGap.LockWheels.FrontLeft}");

            // ...while FrontRight (never at 100) is unaffected by any clock at all - always the
            // plain projected value.
            Assert.Equal(50.0, afterHalfGap.LockWheels.FrontRight, 6);
        }

        [Fact]
        public void Lock_and_slip_pulse_settings_are_independent()
        {
            var lockProjector = new OutputProjector(IdentitySettings());
            var slipProjector = new OutputProjector(IdentitySettings());
            var lockPulse = new PulseSettings { Enabled = true, GapMs = 400, MinValue = 0 };
            var slipPulse = new PulseSettings { Enabled = false }; // slip channel pulse OFF
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, lockPulse, slipProjector, slipPulse);

            var lockWheels = Corners.Uniform(100.0);
            var slipWheels = Corners.Uniform(100.0);
            var normalized = Normalized(lockWheels, slipWheels);

            engine.Compute(normalized, 0.0);
            ProjectedWheelLockSlipResult after = engine.Compute(normalized, 0.2);

            Assert.True(after.LockAll < 100.0, "lock channel pulse should be active and mid-cycle");
            Assert.Equal(100.0, after.SlipAll, 6); // slip channel pulse disabled -> stays flat at 100
        }

        [Fact]
        public void Curve_shapes_the_normalized_value_before_pulsing_is_ever_considered()
        {
            var s = new ProjectorSettings(); // field defaults: 30->10, 60->30, 80->80 (Curve's own anchor shape)
            var lockProjector = new OutputProjector(s);
            var slipProjector = new OutputProjector(IdentitySettings());
            var lockPulse = new PulseSettings { Enabled = false };
            var slipPulse = new PulseSettings { Enabled = false };
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, lockPulse, slipProjector, slipPulse);

            var lockWheels = Corners.Uniform(30.0); // curve default: 30 -> ~10
            var normalized = Normalized(lockWheels, Corners.Zero);

            ProjectedWheelLockSlipResult result = engine.Compute(normalized, 0.016);

            Assert.InRange(result.LockAll, 9.0, 11.0);
        }
    }
}

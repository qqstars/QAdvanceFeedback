using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="ProjectorSettings.ApplyPreset"/> handles <see cref="ProjectionChannel.SlipAngle"/>
    /// explicitly for every preset (Task 18 review trap: Linear/Custom used to fall through to Lock).
    /// </summary>
    public class ProjectorSettingsSlipAngleTests
    {
        [Fact]
        public void Linear_for_slip_angle_is_a_straight_line_from_0_to_100()
        {
            ProjectorSettings s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);
            s.StartInput = 0.0;   // the shipped Start is 15 now; Linear keeps the driver's Start, so zero it for the 0-100 line
            s.ApplyPreset(ProjectorPreset.Linear, ProjectionChannel.SlipAngle);

            Assert.Equal(ProjectorPreset.Linear, s.Preset);
            Assert.Equal(0.0, s.StartInput);
            Assert.Equal(100.0, s.EndInput);
            var projector = new OutputProjector(s);
            foreach (double x in new[] { 0.0, 10.0, 30.0, 45.0, 60.0, 80.0, 95.0, 100.0 })
                Assert.Equal(x, projector.Project(x), 6);
        }

        [Fact]
        public void Custom_for_slip_angle_keeps_what_the_driver_entered()
        {
            ProjectorSettings s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);
            s.SlightlyOutput = 17.0;
            s.ApplyPreset(ProjectorPreset.Custom, ProjectionChannel.SlipAngle);

            Assert.Equal(ProjectorPreset.Custom, s.Preset);
            Assert.Equal(17.0, s.SlightlyOutput);
        }

        [Fact]
        public void Curve_for_slip_angle_is_the_shipped_10_20_60_preset()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.SlipAngle);
            Assert.Equal(new[] { 30.0, 60.0, 80.0 }, new[] { s.SlightlyInput, s.ModerateInput, s.CriticalInput });
            Assert.Equal(new[] { 10.0, 20.0, 60.0 }, new[] { s.SlightlyOutput, s.ModerateOutput, s.CriticalOutput });
            Assert.Equal(new[] { 3.0, 6.0, 3.0 }, new[] { s.SlightlyFlattenRange, s.ModerateFlattenRange, s.CriticalFlattenRange });
            Assert.Equal(15.0, s.StartInput);   // owner 2026-11-19: Start input 15 (was 0)
            Assert.Equal(0.0, s.StartOutput);
        }

        [Fact]
        public void Shipped_slip_angle_default_has_start_input_15_and_lock_slip_presets_are_unchanged()
        {
            Assert.Equal(15.0, ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle).StartInput);
            Assert.Equal(0.0, ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle).StartOutput);
            var lockS = new ProjectorSettings(); lockS.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Lock);
            var slipS = new ProjectorSettings(); slipS.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);
            Assert.Equal(20.0, lockS.StartInput);
            Assert.Equal(20.0, slipS.StartInput);
        }
    }
}

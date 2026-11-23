using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Owner, 2026-11-22: the Slip Angle curve (Best 60 -> 20 smooth range 6, Max 80 -> 60 smooth range 3),
    /// restored by "Restore all defaults" and by the Curve preset; and the lifted-wheel rule of the axle angle.</summary>
    public class SlipAngleCurveAndAxleTests
    {
        private static void AssertNewCurve(ProjectorSettings p)
        {
            Assert.Equal(ProjectorPreset.Curve, p.Preset);
            Assert.Equal(15.0, p.StartInput); Assert.Equal(0.0, p.StartOutput);
            Assert.Equal(100.0, p.EndInput); Assert.Equal(100.0, p.EndOutput);
            Assert.Equal(30.0, p.SlightlyInput); Assert.Equal(10.0, p.SlightlyOutput); Assert.Equal(3.0, p.SlightlyFlattenRange);
            Assert.Equal(60.0, p.ModerateInput); Assert.Equal(20.0, p.ModerateOutput); Assert.Equal(6.0, p.ModerateFlattenRange);
            Assert.Equal(80.0, p.CriticalInput); Assert.Equal(60.0, p.CriticalOutput); Assert.Equal(3.0, p.CriticalFlattenRange);
        }

        [Fact]
        public void The_shipped_slip_angle_curve_is_best_60_to_20_range_6_and_max_80_to_60_range_3()
        {
            AssertNewCurve(ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle));
            AssertNewCurve(new SlipAngleChannelSettings().Projector);
        }

        [Fact]
        public void Restore_all_defaults_resets_the_slip_angle_curve_to_the_new_values()
        {
            var s = new QAdvanceFeedbackSettings();
            ProjectorSettings p = s.SlipAngle.Projector;
            p.Preset = ProjectorPreset.CurveCustom;
            p.ModerateInput = 50.0; p.ModerateOutput = 45.0; p.ModerateFlattenRange = 1.0;
            p.CriticalInput = 90.0; p.CriticalOutput = 90.0; p.CriticalFlattenRange = 0.0;

            s.RestoreDefaults();

            AssertNewCurve(s.SlipAngle.Projector);
        }

        [Fact]
        public void Choosing_the_curve_preset_again_gives_the_new_values()
        {
            var p = new ProjectorSettings();
            p.ApplyPreset(ProjectorPreset.Linear, ProjectionChannel.SlipAngle);
            p.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.SlipAngle);
            AssertNewCurve(p);
        }

        [Fact]
        public void A_saved_config_still_on_the_previous_shipped_curve_moves_to_the_new_one_and_an_edited_one_does_not()
        {
            ProjectorSettings Previous() => new ProjectorSettings
            {
                Preset = ProjectorPreset.Curve, StartInput = 15, StartOutput = 0, EndInput = 100, EndOutput = 100,
                SlightlyInput = 30, SlightlyOutput = 10, SlightlyFlattenRange = 3,
                ModerateInput = 62, ModerateOutput = 30, ModerateFlattenRange = 2,
                CriticalInput = 78, CriticalOutput = 60, CriticalFlattenRange = 2,
            };
            var s = new SlipAngleChannelSettings { Projector = Previous() };
            s.Normalise();
            AssertNewCurve(s.Projector);

            ProjectorSettings edited = Previous();
            edited.ModerateOutput = 31.0;   // one value changed by the driver
            var e = new SlipAngleChannelSettings { Projector = edited };
            e.Normalise();
            Assert.Equal(62.0, e.Projector.ModerateInput);
            Assert.Equal(31.0, e.Projector.ModerateOutput);
        }

        private static SlipAngleRawResult Axles(double fl, double fr, double rl, double rr, bool[] kerbs = null) =>
            SlipAngleRawCalculator.Compute(
                new SlipAngleInput(string.Empty, hasNativeSource: true, nativeWheelDeg: new Corners(fl, fr, rl, rr), isNative: true,
                    unit: SlipAngleUnit.Degrees, kerbWheels: kerbs),
                null, true, 1.0, 0.85, 0.15);

        /// <summary>OWNER, 2026-11-22. A game reports 0 for a wheel off the ground: measured on the FH6 log, FL 0 with
        /// FR 12.7 made a 6.3 front axle. The loaded partner is what the axle is doing.</summary>
        [Fact]
        public void A_lifted_wheel_is_left_out_of_its_axle_like_a_kerb_wheel()
        {
            SlipAngleRawResult r = Axles(0.0, 12.7, 3.0, 4.0);
            Assert.Equal(12.7, r.FrontDeg.Value, 9);
            Assert.Equal(3.5, r.RearDeg.Value, 9);

            Assert.Equal(6.0, Axles(5.0, 7.0, 0.0, 6.0).FrontDeg.Value, 9);   // both loaded: the mean
            Assert.Equal(6.0, Axles(5.0, 7.0, 0.0, 6.0).RearDeg.Value, 9);    // rear: the lifted left left out
        }

        [Fact]
        public void An_axle_running_straight_is_still_the_mean()
        {
            // Both wheels near zero (a straight), or the partner below the 2 degree bar: not a lifted wheel.
            Assert.Equal(0.04, Axles(0.03, 0.05, 0.0, 0.0).FrontDeg.Value, 9);
            Assert.Equal(0.95, Axles(0.0, 1.9, 1.0, 1.0).FrontDeg.Value, 9);
        }
    }
}

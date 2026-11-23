using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// The Slip Angle Test Effect calculation (docs\slip-angle-design.md 9.11): the slider's Raw.All in
    /// degrees -> the real normalization with the shown window -> the channel's projector -> the real
    /// drift attenuation with the configured parameters (full confidence, no gate, no landing).
    /// </summary>
    public class SlipAngleTestEngineTests
    {
        private const double Dt = 1.0 / 60.0;
        private static readonly SlipAngleWindow W = new SlipAngleWindow(4.0, 7.0, 10.0);   // End 15

        private static double Curve(SlipAngleChannelSettings ch, double deg) =>
            new OutputProjector(ch.Projector).Project(ClampMath.To0100(SlipAngleNormalizer.Normalize(deg, W)));

        [Fact]
        public void Below_max_the_pulse_free_output_is_the_curve()
        {
            var ch = new SlipAngleChannelSettings();
            for (double deg = 0.0; deg < W.Max; deg += 0.5)
            {
                var engine = new SlipAngleTestEngine(ch);
                SlipAngleProjectedResult r = null;
                for (int i = 0; i < 120; i++) r = engine.Step(deg, W, Dt);   // 2 s held: never a drift below Max
                Assert.Equal(Curve(ch, deg), r.PulseFreeAll, 9);
                Assert.Equal(r.PreDriftAll, r.PulseFreeAll, 9);   // maturity 1: no cold-start damping
                Assert.Equal(DriftState.Normal, r.DriftState);
            }
        }

        [Fact]
        public void Held_past_max_eases_to_ToPercent_of_the_curve()
        {
            var ch = new SlipAngleChannelSettings();
            var engine = new SlipAngleTestEngine(ch);
            double deg = 2.5 * W.Max;   // past End
            SlipAngleProjectedResult r = engine.Step(deg, W, Dt);
            Assert.Equal(Curve(ch, W.End), r.PulseFreeAll, 9);   // first frame: full output at the top

            for (int i = 0; i < 30; i++) r = engine.Step(deg, W, Dt);   // 0.5 s > Hold 0.25 + Duration 0.12
            Assert.Equal(DriftState.Drifting, r.DriftState);
            Assert.Equal(0.40 * Curve(ch, W.End), r.PulseFreeAll, 9);
        }

        [Fact]
        public void Configured_drift_parameters_are_used()
        {
            var ch = new SlipAngleChannelSettings();
            ch.Drift.ToPercent = 70;
            var engine = new SlipAngleTestEngine(ch);
            SlipAngleProjectedResult r = null;
            for (int i = 0; i < 40; i++) r = engine.Step(3.0 * W.Max, W, Dt);
            Assert.Equal(0.70 * Curve(ch, W.End), r.PulseFreeAll, 9);
        }

        [Fact]
        public void Invalid_inputs_never_throw()
        {
            var engine = new SlipAngleTestEngine(null);
            SlipAngleProjectedResult r = engine.Step(double.NaN, new SlipAngleWindow(double.NaN, 1, 2), double.NaN);
            Assert.Equal(0.0, r.PulseFreeAll, 9);
            r = engine.Step(-5, W, -1);
            Assert.Equal(0.0, r.FinalAll, 9);
        }
    }
}

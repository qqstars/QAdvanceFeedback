using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class SlipAngleProjectedEngineTests
    {
        private const double Dt = 1.0 / 60.0;
        private static readonly SlipAngleGateVerdict Open = new SlipAngleGateVerdict(true, 1, false, 1, false, false, true);

        private static OutputProjector Projector() => new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle));
        private static SlipAngleProjectedEngine Engine() => new SlipAngleProjectedEngine(Projector(), new PulseSettings(), new DriftSettings());

        [Theory]
        [InlineData(30.0, 10.0)]
        [InlineData(60.0, 20.0)]
        [InlineData(80.0, 60.0)]
        public void Preset_outputs_10_20_60_at_min_best_max(double input, double expected)
        {
            // Owner, 2026-11-22: Best 60 -> 20 (smooth range 6), Max 80 -> 60 (smooth range 3); was 62 -> 30 / 78 -> 60, 2 / 2.
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);
            Assert.Equal(15.0, s.StartInput); Assert.Equal(0.0, s.StartOutput);   // Start input 15 (owner 2026-11-19; was 0)
            Assert.Equal(100.0, s.EndInput); Assert.Equal(100.0, s.EndOutput);
            Assert.Equal(3.0, s.SlightlyFlattenRange); Assert.Equal(6.0, s.ModerateFlattenRange); Assert.Equal(3.0, s.CriticalFlattenRange);
            Assert.Equal(expected, Projector().Project(input), 6);
            Assert.False(new PulseSettings().Enabled);
        }

        [Fact]
        public void Cold_tier1_caps_full_reading_at_50()
        {
            var r = Engine().Compute(100, 100, 0.0, ColdStartTier.Tier1, Open, Dt);
            Assert.Equal(50.0, r.FinalAll, 6);
            Assert.Equal(50.0, r.PreDriftAll, 6);
        }

        [Fact]
        public void Warm_reaches_100()
        {
            var r = Engine().Compute(100, 100, 1.0, ColdStartTier.Tier1, Open, Dt);
            Assert.Equal(100.0, r.FinalAll, 6);
            Assert.Equal(100.0, r.PulseFreeAll, 6);
        }

        [Fact]
        public void Pedal_closed_outputs_zero_but_drift_tracks()
        {
            var closed = new SlipAngleGateVerdict(false, 1, false, 1, false, false, true);
            var e = Engine();
            SlipAngleProjectedResult r = null;
            for (int i = 0; i < 60; i++) r = e.Compute(100, 100, 1.0, ColdStartTier.Tier1, closed, Dt);
            Assert.Equal(0.0, r.FinalAll);
            Assert.Equal(DriftState.Drifting, r.DriftState);
            // Pedal reopens mid-slide: resumes with the tracked drift state.
            r = e.Compute(100, 100, 1.0, ColdStartTier.Tier1, Open, Dt);
            Assert.Equal(DriftState.Drifting, r.DriftState);
            Assert.True(r.FinalAll < 100.0);
            Assert.True(r.FinalAll > 0.0);
        }

        [Fact]
        public void Landing_fade_multiplies()
        {
            var g = new SlipAngleGateVerdict(true, 0.5, false, 0.5, false, false, true);
            var r = Engine().Compute(100, 100, 1.0, ColdStartTier.Tier1, g, Dt);
            Assert.Equal(25.0, r.FinalAll, 6);
            Assert.Equal(100.0, r.PreDriftAll, 6);
        }

        [Fact]
        public void Airborne_frame_outputs_zero_and_resets_drift()
        {
            var e = Engine();
            for (int i = 0; i < 60; i++) e.Compute(100, 100, 1.0, ColdStartTier.Tier1, Open, Dt);
            var air = new SlipAngleGateVerdict(true, 1, true, 1, false, false, true);
            var r = e.Compute(100, 100, 1.0, ColdStartTier.Tier1, air, Dt);
            Assert.Equal(0.0, r.FinalAll);
            Assert.Equal(DriftState.Normal, r.DriftState);
            Assert.Equal(1.0, r.DriftFactor);
        }

        [Fact]
        public void Wobble_frames_keep_drift_normal()
        {
            var wob = new SlipAngleGateVerdict(true, 1, false, 1, true, false, true);
            var e = Engine();
            SlipAngleProjectedResult r = null;
            for (int i = 0; i < 60; i++) r = e.Compute(100, 100, 1.0, ColdStartTier.Tier1, wob, Dt);
            Assert.Equal(DriftState.Normal, r.DriftState);
            Assert.Equal(1.0, r.DriftFactor);
        }

        [Fact]
        public void Non_finite_inputs_never_produce_nan()
        {
            var r = Engine().Compute(double.NaN, double.NaN, double.NaN, ColdStartTier.Tier1, Open, double.NaN);
            Assert.False(double.IsNaN(r.FinalAll));
            Assert.False(double.IsNaN(r.PulseFreeAll));
            Assert.False(double.IsNaN(r.PreDriftAll));
        }
    }
}

using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// The WheelLock/WheelSlip Test Effect calculation (docs\shakeit-test-effect-mvvm-design.md): slider
    /// source values -> the key data the graph shows -> the engine's aggregation -> the real projection
    /// and pulse, published as Projected.*. Plus the balls drawn on the graph.
    /// </summary>
    public class LockSlipTestEngineTests
    {
        private const double Dt = 1.0 / 60.0;

        private static KeyDataView Triple(double sMax, double s90, double s75) =>
            new KeyDataView { SMax = sMax, S90 = s90, S75 = s75, ShowLowerAnchors = true };

        private static WheelChannelSettings LockChannel()
        {
            var ch = new WheelChannelSettings { NormalizePattern = NormalizePattern.Mapping };
            ch.Projector = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            return ch;
        }

        [Fact]
        public void Normalize_matches_the_graph_samples()
        {
            WheelChannelSettings ch = LockChannel();
            var projector = new OutputProjector(ch.Projector);
            var points = SourceToProjectedGraph.Points(ch.Projector, true, false, 85, 75, 60, 100, 100);

            for (int i = 0; i <= 20; i++)
            {
                double x = i * 5;
                double expected = 100 - projector.Project(ClampMath.To0100(KeyDataNormalization.Normalize(x, true, false, 85, 75, 60)));
                Assert.Equal(expected, points[i].Y, 9);
            }
        }

        [Fact]
        public void Step_projects_each_wheel_through_the_shown_key_data()
        {
            WheelChannelSettings ch = LockChannel();
            var projector = new OutputProjector(ch.Projector);
            var engine = new LockSlipTestEngine(isLock: true, ch);

            ProjectedWheelLockSlipResult r = engine.Step(new Corners(80, 60, 40, 0), Triple(80, 60, 40), Dt);

            Assert.Equal(projector.Project(80), r.LockWheels.FrontLeft, 6);
            Assert.Equal(projector.Project(60), r.LockWheels.FrontRight, 6);
            Assert.Equal(projector.Project(30), r.LockWheels.RearLeft, 6);
            Assert.Equal(0.0, r.LockWheels.RearRight, 6);
        }

        [Fact]
        public void Step_aggregates_groups_with_the_channel_weights()
        {
            WheelChannelSettings ch = LockChannel();
            var projector = new OutputProjector(ch.Projector);
            var engine = new LockSlipTestEngine(true, ch);

            ProjectedWheelLockSlipResult r = engine.Step(new Corners(80, 60, 40, 0), Triple(80, 60, 40), Dt);

            WheelAggregate agg = Aggregator.Compute(new Corners(80, 60, 30, 0), ch.ToAggregationWeights());
            Assert.Equal(projector.Project(agg.Front), r.LockFrontWithoutPulse, 6);
            Assert.Equal(projector.Project(agg.Right), r.LockRightWithoutPulse, 6);
            Assert.Equal(projector.Project(agg.All), r.LockAllWithoutPulse, 6);
        }

        [Fact]
        public void Step_runs_the_pulse()
        {
            WheelChannelSettings ch = LockChannel();
            ch.Pulse.Enabled = true;
            var engine = new LockSlipTestEngine(true, ch);

            bool differed = false;
            for (int i = 0; i < 60; i++)
            {
                ProjectedWheelLockSlipResult r = engine.Step(new Corners(100, 100, 100, 100), Triple(80, 60, 40), Dt);
                if (System.Math.Abs(r.LockAll - r.LockAllWithoutPulse) > 1e-9) differed = true;
            }
            Assert.True(differed);
        }

        [Fact]
        public void Step_leaves_the_other_channel_at_zero()
        {
            var engine = new LockSlipTestEngine(true, LockChannel());
            ProjectedWheelLockSlipResult r = engine.Step(new Corners(90, 90, 90, 90), Triple(80, 60, 40), Dt);
            Assert.Equal(0.0, r.SlipAll);
            Assert.Equal(0.0, r.SlipWheels.FrontLeft);

            var slip = new WheelChannelSettings();
            var slipEngine = new LockSlipTestEngine(false, slip);
            ProjectedWheelLockSlipResult s = slipEngine.Step(new Corners(90, 90, 90, 90), Triple(80, 60, 40), Dt);
            Assert.Equal(0.0, s.LockAll);
            Assert.True(s.SlipAll > 0.0);
        }

        [Fact]
        public void Step_never_touches_the_channel_settings_or_learned_data()
        {
            WheelChannelSettings ch = LockChannel();
            string before = JsonConvert.SerializeObject(ch);
            var engine = new LockSlipTestEngine(true, ch);
            for (int i = 0; i < 100; i++) engine.Step(new Corners(i, 100 - i, 50, 25), Triple(80, 60, 40), Dt);
            Assert.Equal(before, JsonConvert.SerializeObject(ch));
        }

        [Theory]
        [InlineData(EffectRouting.Corners, new[] { BallColour.BrightRed, BallColour.DarkRed, BallColour.BrightBlue, BallColour.DarkBlue })]
        [InlineData(EffectRouting.FrontRear, new[] { BallColour.Red, BallColour.Blue })]
        [InlineData(EffectRouting.LeftRight, new[] { BallColour.BrightGrey, BallColour.DarkGrey })]
        [InlineData(EffectRouting.Mono, new[] { BallColour.Orange })]
        public void Balls_follow_routing_and_colours(EffectRouting routing, BallColour[] colours)
        {
            WheelChannelSettings ch = LockChannel();
            var sources = new Corners(80, 60, 40, 0);
            ProjectedWheelLockSlipResult r = new LockSlipTestEngine(true, ch).Step(sources, Triple(80, 60, 40), Dt);

            var balls = TestBalls.For(routing, true, sources, ch.ToAggregationWeights(), r);

            Assert.Equal(colours, balls.Select(b => b.Colour).ToArray());
            Assert.Equal(EffectRoutingSlots.For(routing).ToArray(), balls.Select(b => b.Slot).ToArray());
        }

        [Fact]
        public void Ball_y_is_pulse_free_and_group_x_is_aggregated_source()
        {
            WheelChannelSettings ch = LockChannel();
            ch.Pulse.Enabled = true;
            var sources = new Corners(80, 60, 40, 0);
            var engine = new LockSlipTestEngine(true, ch);
            ProjectedWheelLockSlipResult r = null;
            for (int i = 0; i < 20; i++) r = engine.Step(sources, Triple(80, 60, 40), Dt);

            var corner = TestBalls.For(EffectRouting.Corners, true, sources, ch.ToAggregationWeights(), r);
            Assert.Equal(80, corner[0].Source0100, 9);
            Assert.Equal(r.LockWheelsWithoutPulse.FrontLeft, corner[0].Projected0100, 9);

            var frontRear = TestBalls.For(EffectRouting.FrontRear, true, sources, ch.ToAggregationWeights(), r);
            Assert.Equal(Aggregator.Compute(sources, ch.ToAggregationWeights()).Front, frontRear[0].Source0100, 9);
            Assert.Equal(r.LockFrontWithoutPulse, frontRear[0].Projected0100, 9);
        }
    }
}

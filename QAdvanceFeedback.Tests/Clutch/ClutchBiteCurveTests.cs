using QAdvanceFeedback.Core.Clutch;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>The Clutch Bite output curve and the "last direction" rule (docs\clutch-bite-design.md).</summary>
    public class ClutchBiteCurveTests
    {
        [Fact]
        public void Peak_is_100_at_the_bite_and_0_at_or_outside_the_edges()
        {
            Assert.Equal(100.0, ClutchBiteCurve.Output(50, 50, 30, 2.5), 9);
            Assert.Equal(0.0, ClutchBiteCurve.Output(20, 50, 30, 2.5));
            Assert.Equal(0.0, ClutchBiteCurve.Output(80, 50, 30, 2.5));
            Assert.Equal(0.0, ClutchBiteCurve.Output(10, 50, 30, 2.5));
            Assert.Equal(0.0, ClutchBiteCurve.Output(95, 50, 30, 2.5));
        }

        [Fact]
        public void S_curve_is_slow_fast_slow()
        {
            double a30 = ClutchBiteCurve.Output(30, 50, 30, 2.5);
            double a35 = ClutchBiteCurve.Output(35, 50, 30, 2.5);
            double a40 = ClutchBiteCurve.Output(40, 50, 30, 2.5);
            double a45 = ClutchBiteCurve.Output(45, 50, 30, 2.5);
            Assert.True(a30 < 16, "outer third rises only a little: " + a30);   // 15.0 at k 2.5
            Assert.True(a40 > 70, "most of the rise is in the middle third: " + a40);
            Assert.True(a45 - a40 < a40 - a35, "the top flattens near the bite");
        }

        [Fact]
        public void Both_sides_mirror_each_other()
        {
            for (int d = 1; d < 30; d++)
                Assert.Equal(ClutchBiteCurve.Output(50 - d, 50, 30, 2.5), ClutchBiteCurve.Output(50 + d, 50, 30, 2.5), 9);
        }

        [Fact]
        public void Gamma_1_is_a_straight_line()
        {
            Assert.Equal(50.0, ClutchBiteCurve.Output(35, 50, 30, 1.0), 9);
            Assert.Equal(50.0, ClutchBiteCurve.Output(65, 50, 30, 1.0), 9);
        }

        [Fact]
        public void Edges_keep_a_5_point_deadzone()
        {
            Assert.Equal((45.0, 95.0), ClutchBiteCurve.Edges(75, 30));
            Assert.Equal((5.0, 50.0), ClutchBiteCurve.Edges(20, 30));
            Assert.Equal((20.0, 80.0), ClutchBiteCurve.Edges(50, 30));
            Assert.Equal(0.0, ClutchBiteCurve.Output(96, 75, 30, 2.5));
            Assert.True(ClutchBiteCurve.Output(94, 75, 30, 2.5) > 0);
        }

        [Fact]
        public void Bite_range_and_gamma_are_clamped()
        {
            Assert.Equal(ClutchBiteCurve.Edges(10, 30), ClutchBiteCurve.Edges(2, 30));
            Assert.Equal(ClutchBiteCurve.Edges(90, 30), ClutchBiteCurve.Edges(99, 30));
            Assert.Equal(100.0, ClutchBiteCurve.Output(10, 2, 30, 2.5), 9);
            Assert.Equal(ClutchBiteCurve.Output(40, 50, 30, 6.0), ClutchBiteCurve.Output(40, 50, 30, 60.0), 9);
            Assert.Equal(ClutchBiteCurve.Edges(50, 50), ClutchBiteCurve.Edges(50, 80));
        }

        [Fact]
        public void Points_trace_the_curve_in_value_coordinates()
        {
            var points = ClutchBiteCurve.Points(50, 30, 2.5);
            Assert.Equal(101, points.Count);
            Assert.Equal(50.0, points[50].X, 9);
            Assert.Equal(100.0, points[50].Y, 9);
            Assert.Equal(0.0, points[10].Y);
        }

        [Fact]
        public void Release_only_follows_last_direction_and_holds()
        {
            var t = new ClutchDirectionTracker();
            for (double c = 100; c >= 60; c -= 2) t.Update(c);
            for (int i = 0; i < 10; i++) t.Update(60);

            Assert.Equal(ClutchMoveDirection.Releasing, t.Last);
            Assert.True(t.Allows(ClutchApplyDirection.ReleaseOnly));
            Assert.False(t.Allows(ClutchApplyDirection.PressOnly));
            Assert.True(t.Allows(ClutchApplyDirection.PressAndRelease));
        }

        [Fact]
        public void Jitter_under_2_points_does_not_flip_direction()
        {
            var t = new ClutchDirectionTracker();
            t.Update(80); t.Update(70); t.Update(60);
            t.Update(61.5); t.Update(60.5); t.Update(61.9);
            Assert.Equal(ClutchMoveDirection.Releasing, t.Last);
            t.Update(62.1);
            Assert.Equal(ClutchMoveDirection.Pressing, t.Last);
        }

        [Fact]
        public void Unknown_direction_allows_only_press_and_release()
        {
            var t = new ClutchDirectionTracker();
            t.Update(40);
            Assert.Equal(ClutchMoveDirection.Unknown, t.Last);
            Assert.True(t.Allows(ClutchApplyDirection.PressAndRelease));
            Assert.False(t.Allows(ClutchApplyDirection.PressOnly));
            Assert.False(t.Allows(ClutchApplyDirection.ReleaseOnly));
            t.Reset();
            Assert.Equal(ClutchMoveDirection.Unknown, t.Last);
        }
    }
}

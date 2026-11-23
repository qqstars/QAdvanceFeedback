using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>The Slip Angle tab's right graph: Raw.All degrees (0 to End) through to Projected.</summary>
    public class SlipAngleSourceToProjectedGraphTests
    {
        private static ProjectorSettings Shipped() => ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);

        private static double At(IReadOnlyList<GraphPoint> points, double deg) =>
            points.Single(p => System.Math.Abs(p.X - deg) < 1e-9).Y;

        [Fact]
        public void Samples_run_from_0_to_End_every_quarter_degree()
        {
            var w = new SlipAngleWindow(5.0, 8.0, 10.0);
            IReadOnlyList<GraphPoint> points = SlipAngleSourceToProjectedGraph.Sample(w, Shipped());

            Assert.Equal(0.0, points[0].X);
            Assert.Equal(15.0, points[points.Count - 1].X, 9);
            Assert.Equal(61, points.Count);   // 0, 0.25, ... 15
            for (int i = 1; i < points.Count; i++) Assert.Equal(0.25, points[i].X - points[i - 1].X, 9);
        }

        [Fact]
        public void The_key_angles_are_sampled_exactly_even_off_the_grid()
        {
            var w = new SlipAngleWindow(4.1, 6.3, 7.9);
            IReadOnlyList<GraphPoint> points = SlipAngleSourceToProjectedGraph.Sample(w, Shipped());

            // Best / Max land exactly on the 60 / 80 anchors (owner, 2026-11-22: 20 / 60).
            Assert.Equal(10.0, At(points, 4.1), 6);
            Assert.Equal(20.0, At(points, 6.3), 6);
            Assert.Equal(60.0, At(points, 7.9), 6);
            Assert.Equal(w.End, points[points.Count - 1].X, 9);
            for (int i = 1; i < points.Count; i++) Assert.True(points[i].X > points[i - 1].X);
        }

        [Fact]
        public void An_invalid_window_falls_back_to_the_shipped_default()
        {
            IReadOnlyList<GraphPoint> points = SlipAngleSourceToProjectedGraph.Sample(new SlipAngleWindow(5, 5, 5), Shipped());
            Assert.Equal(15.0, points[points.Count - 1].X, 9);
        }

        [Fact]
        public void No_projector_gives_no_points()
        {
            Assert.Empty(SlipAngleSourceToProjectedGraph.Sample(SlipAngleWindow.ShippedDefault, null));
        }
    
        [Fact]
        public void The_start_angle_is_sampled_exactly_and_projects_the_15_anchor()
        {
            var w = new SlipAngleWindow(4.1, 6.3, 7.9);   // Start = 2
            IReadOnlyList<GraphPoint> points = SlipAngleSourceToProjectedGraph.Sample(w, Shipped());
            Assert.Equal(new OutputProjector(Shipped()).Project(15.0), At(points, 2.0), 6);

            var w2 = new SlipAngleWindow(4.6, 7.0, 9.0);  // Start = 2 (off-grid Min, on-grid Start)
            Assert.Contains(SlipAngleSourceToProjectedGraph.Sample(w2, Shipped()), p => System.Math.Abs(p.X - 2.0) < 1e-9);
            var w3 = new SlipAngleWindow(1.5, 3.0, 5.0);  // no Start knot, still sampled
            Assert.NotEmpty(SlipAngleSourceToProjectedGraph.Sample(w3, Shipped()));
        }
}
}

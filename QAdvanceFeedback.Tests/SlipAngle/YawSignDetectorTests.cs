using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Per-game yaw-rate sign learning (ruling R12): k in {+1, -1} such that
    /// lateral G ~ k * V * r.</summary>
    public class YawSignDetectorTests
    {
        private const double Dt = 0.1;
        private const double G = 9.80665;
        private const string Game = "RFactor2";

        /// <summary>A steady corner: V = 30 m/s, lateral G = latG, yaw rate chosen so V*r equals
        /// sign * latG (in g).</summary>
        private static void Corner(YawSignDetector d, string game, double seconds, int sign, double latG = 1.0,
            double v = 30.0, bool airborne = false)
        {
            int frames = (int)System.Math.Round(seconds / Dt);
            double r = sign * latG * G / v;
            for (int i = 0; i < frames; i++)
            {
                // alternate left and right corners so the evidence is sign agreement, not a bias
                double s = (i / 10) % 2 == 0 ? 1.0 : -1.0;
                d.Observe(game, s * latG, v, s * r, airborne, Dt);
            }
        }

        [Fact]
        public void Spec_values_are_pinned()
        {
            Assert.Equal(3.0, YawSignDetector.EvidenceWindowSeconds);
            Assert.Equal(0.3, YawSignDetector.MinLateralG);
            Assert.Equal(30.0, YawSignDetector.MinSpeedKmh);
        }

        [Fact]
        public void Undecided_before_3_seconds_of_evidence()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 2.8, +1);
            Assert.False(d.TryGetSign(Game, out int k));
            Assert.Equal(0, k);
        }

        [Fact]
        public void Learns_positive_sign()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 3.1, +1);
            Assert.True(d.TryGetSign(Game, out int k));
            Assert.Equal(1, k);
        }

        [Fact]
        public void Learns_negative_sign()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 3.1, -1);
            Assert.True(d.TryGetSign(Game, out int k));
            Assert.Equal(-1, k);
        }

        [Fact]
        public void Frames_below_0_3_g_slow_airborne_or_invalid_do_not_count()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 20.0, +1, latG: 0.2);
            Corner(d, Game, 20.0, +1, v: 8.0);        // 28.8 km/h < 30
            Corner(d, Game, 20.0, +1, airborne: true);
            for (int i = 0; i < 200; i++) d.Observe(Game, double.NaN, 30, 0.3, false, Dt);
            for (int i = 0; i < 200; i++) d.Observe(Game, 1.0, 30, null, false, Dt);
            for (int i = 0; i < 200; i++) d.Observe(Game, 1.0, null, 0.3, false, Dt);
            for (int i = 0; i < 200; i++) d.Observe(Game, 1.0, 30, 0.3, false, 0.0);
            Assert.False(d.TryGetSign(Game, out _));
        }

        [Fact]
        public void Reverse_driving_keeps_the_same_sign()
        {
            // V negative and r flipped with it: V*r still matches lateral G, so k stays +1.
            var d = new YawSignDetector();
            int frames = 40;
            for (int i = 0; i < frames; i++)
            {
                double s = (i / 10) % 2 == 0 ? 1.0 : -1.0;
                d.Observe(Game, s * 0.8, -20.0, -s * 0.8 * G / 20.0, false, Dt);
            }
            Assert.True(d.TryGetSign(Game, out int k));
            Assert.Equal(1, k);
        }

        [Fact]
        public void Mixed_evidence_stays_undecided()
        {
            var d = new YawSignDetector();
            for (int n = 0; n < 10; n++)
            {
                Corner(d, Game, 0.4, +1);
                Corner(d, Game, 0.4, -1);
            }
            Assert.False(d.TryGetSign(Game, out _));
        }

        [Fact]
        public void Does_not_flip_on_brief_contrary_evidence()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 3.1, +1);
            Corner(d, Game, 2.0, -1);
            Corner(d, Game, 2.0, +1);
            Assert.True(d.TryGetSign(Game, out int k));
            Assert.Equal(1, k);
        }

        [Fact]
        public void Does_not_flip_on_moderately_contrary_window()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 3.0, +1);
            // a 3 s window that is only 70% contrary is not "strong" reversal
            for (int n = 0; n < 3; n++)
            {
                Corner(d, Game, 0.7, -1);
                Corner(d, Game, 0.3, +1);
            }
            Assert.True(d.TryGetSign(Game, out int k));
            Assert.Equal(1, k);
        }

        [Fact]
        public void Flips_only_after_a_further_3_seconds_of_strongly_reversed_evidence()
        {
            var d = new YawSignDetector();
            Corner(d, Game, 3.0, +1);
            Assert.True(d.TryGetSign(Game, out int k));
            Assert.Equal(1, k);
            Corner(d, Game, 2.9, -1);
            Assert.True(d.TryGetSign(Game, out k));
            Assert.Equal(1, k);
            Corner(d, Game, 0.2, -1);
            Assert.True(d.TryGetSign(Game, out k));
            Assert.Equal(-1, k);
        }

        [Fact]
        public void Games_are_isolated()
        {
            var d = new YawSignDetector();
            Corner(d, "A", 3.1, +1);
            Corner(d, "B", 3.1, -1);
            Assert.True(d.TryGetSign("A", out int a));
            Assert.True(d.TryGetSign("B", out int b));
            Assert.Equal(1, a);
            Assert.Equal(-1, b);
            Assert.False(d.TryGetSign("C", out _));
            Assert.False(d.TryGetSign(null, out _));
        }
    }
}

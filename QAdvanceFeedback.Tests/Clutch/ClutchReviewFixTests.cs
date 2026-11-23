using System;
using System.Collections.Generic;
using System.IO;
using QAdvanceFeedback.Core.Clutch;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>Fixes from the final review of the Clutch Bite effect (docs\clutch-bite-report.md).</summary>
    public class ClutchReviewFixTests
    {
        private const double Dt = 1.0 / 60.0;

        /// <summary>Frames for a standing start with a given RPM model; true bite 45 (two-frame delay).</summary>
        private static List<double> Run(ClutchBiteDetector d, IList<double> clutch, Func<int, double, double> rpm, Func<int, double> time = null)
        {
            var samples = new List<double>();
            double speed = 0;
            for (int i = 0; i < clutch.Count; i++)
            {
                double delayed = clutch[Math.Max(0, i - 2)];
                double engaged = Math.Max(0, 45 - delayed);
                speed += Dt * 0.5 * Math.Max(0, engaged - 2);
                double t = time?.Invoke(i) ?? i * Dt;
                var f = new ClutchFrame(t, clutch[i], rpm(i, engaged), speed, 10, true, false, true);
                if (d.Update(f, out double s)) samples.Add(s);
            }
            return samples;
        }

        private static List<double> SlowStart()
        {
            var c = new List<double>();
            for (int i = 0; i < 60; i++) c.Add(100);
            for (int i = 0; i < 60; i++) c.Add(90);
            for (int i = 1; i <= 300; i++) c.Add(90 - 60.0 * i / 300);     // 12 %/s
            for (int i = 0; i < 360; i++) c.Add(30);
            return c;
        }

        // I3 - a settling (falling) idle at release must not become an early sample.

        [Fact]
        public void Revs_still_falling_at_release_learn_nothing()
        {
            List<double> clutch = SlowStart();
            // A blip decaying from 1300 to 900 over ~2 s, still falling when the release starts at frame 120.
            List<double> s = Run(new ClutchBiteDetector(), clutch,
                (i, engaged) => 900 + 400 * Math.Exp(-i / 60.0) - 20 * engaged);
            Assert.DoesNotContain(s, v => v > 60);
        }

        [Fact]
        public void A_steady_idle_still_learns_the_true_bite()
        {
            List<double> s = Run(new ClutchBiteDetector(), SlowStart(), (i, engaged) => 900 - 20 * engaged);
            Assert.InRange(Assert.Single(s), 44, 46);
        }

        // I4 - a pause or menu during a release resets the detector.

        [Fact]
        public void A_time_gap_mid_release_resets_the_detector()
        {
            List<double> clutch = SlowStart();
            // Paused for 5 s at frame 200 (clutch 74); after the pause the pedal is already at 40.
            for (int i = 200; i < 420; i++) clutch[i] = Math.Min(clutch[i], 40);
            List<double> s = Run(new ClutchBiteDetector(), clutch, (i, engaged) => 900 - 20 * engaged,
                time: i => i < 200 ? i * Dt : i * Dt + 5.0);
            Assert.Empty(s);
        }

        [Fact]
        public void Interrupt_resets_the_engine_detector()
        {
            string plugin = File.ReadAllText(PluginFile("QAdvanceFeedback.Clutch.cs"));
            int at = plugin.IndexOf("private void UpdateClutchOutOfSession()", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("_clutchEngine.Interrupt()", plugin.Substring(at, 600));
        }

        // M1 - a game value that never showed a clutch is not a clutch source.

        [Fact]
        public void Unlatched_game_value_is_no_source()
        {
            var r = new ClutchSourceResolver();
            ClutchReading reading = r.Resolve("g", 0, null, null, false);
            Assert.Equal(ClutchSourceKind.None, reading.Kind);
            Assert.Equal(0.0, reading.Clutch0100.Value);
        }

        // I1 / I2 - plugin side (source scans: the plugin class needs SimHub).

        [Fact]
        public void Learning_saves_go_to_the_cache_not_a_synchronous_flush_on_the_data_thread()
        {
            string plugin = File.ReadAllText(PluginFile("QAdvanceFeedback.Clutch.cs"));
            int at = plugin.IndexOf("private void SaveClutchLearningNow()", StringComparison.Ordinal);
            string body = plugin.Substring(at, plugin.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
            Assert.DoesNotContain("Flush()", body);
        }

        [Fact]
        public void Readout_test_bite_and_forget_use_one_key()
        {
            string plugin = File.ReadAllText(PluginFile("QAdvanceFeedback.Clutch.cs"));
            Assert.Contains("_clutchLearner.Forget(_lastClutchKey)", plugin);
            Assert.Contains("_clutchLearner.Status(_lastClutchKey", plugin);
            Assert.DoesNotContain("reading.Kind == ClutchSourceKind.Game) _lastClutchKey", plugin);
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(Path.Combine(dir.FullName, "QAdvanceFeedback"), Path.Combine(parts));
        }
    }
}

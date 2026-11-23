using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Clutch;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>
    /// Standing-start bite point detection and learning (docs\clutch-bite-design.md, "Auto bite
    /// point"), including the owner's cases: holding above the bite, crossing it quickly and holding
    /// below, crossing it slowly and holding below.
    /// </summary>
    public class ClutchBiteLearningTests
    {
        private const double Dt = 1.0 / 60.0;
        private const double TrueBite = 45.0;

        /// <summary>
        /// A standing start at 60 Hz. The engine loads (RPM falls) as the clutch passes the true bite,
        /// two frames late like a real sim; speed builds once the clutch is engaged a little further.
        /// </summary>
        private sealed class Start
        {
            private readonly List<double> _clutch = new List<double>();
            public double Throttle = 10;
            public double? ThrottleAt5s;
            public bool IdleGovernor;      // holds RPM flat: only the speed shows the bite
            public bool Loose;
            public double Seconds = 12;

            public Start Hold(double clutch, double seconds) { for (int i = 0; i < seconds / Dt; i++) _clutch.Add(clutch); return this; }

            public Start Move(double to, double seconds)
            {
                double from = _clutch.Count == 0 ? 100 : _clutch[_clutch.Count - 1];
                int n = Math.Max(1, (int)Math.Round(seconds / Dt));
                for (int i = 1; i <= n; i++) _clutch.Add(from + (to - from) * i / n);
                return this;
            }

            public List<double> Run(ClutchBiteDetector detector, Func<int, bool> inGear = null)
            {
                var samples = new List<double>();
                double speed = 0;
                int frames = (int)(Seconds / Dt);
                for (int i = 0; i < frames; i++)
                {
                    double clutch = i < _clutch.Count ? _clutch[i] : _clutch[_clutch.Count - 1];
                    double delayed = _clutch[Math.Max(0, Math.Min(_clutch.Count - 1, i - 2))];
                    double engaged = Math.Max(0, TrueBite - delayed);
                    double rpm = IdleGovernor ? 900 : 900 - 20 * engaged;   // 4 % (36 rpm) fires ~1.8 points past the bite
                    speed += Dt * 0.5 * Math.Max(0, engaged - 2);
                    double throttle = ThrottleAt5s.HasValue && i * Dt > 5 ? ThrottleAt5s.Value : Throttle;
                    var f = new ClutchFrame(i * Dt, clutch, rpm, speed, throttle, inGear?.Invoke(i) ?? true, Loose, true);
                    if (detector.Update(f, out double s)) samples.Add(s);
                }
                return samples;
            }
        }

        private static Start Begin() => new Start().Hold(100, 1).Move(90, 0.5).Hold(90, 0.5);

        [Fact]
        public void Slow_release_learns_the_true_bite()
        {
            List<double> s = Begin().Move(30, 5).Run(new ClutchBiteDetector());
            Assert.InRange(Assert.Single(s), TrueBite - 1, TrueBite + 1);
        }

        [Fact]
        public void Quicker_release_within_the_limit_is_still_accurate()
        {
            // 25 %/s: the 4 % trigger fires well past the bite, so only the backdated onset plus the
            // one-frame correction keeps the sample within a point.
            List<double> s = Begin().Move(30, 2.4).Run(new ClutchBiteDetector());
            Assert.InRange(Assert.Single(s), TrueBite - 1, TrueBite + 1);
        }

        [Fact]
        public void Hold_above_bite_learns_nothing()
        {
            Assert.Empty(Begin().Move(46, 3.7).Hold(46, 10).Run(new ClutchBiteDetector()));
        }

        [Fact]
        public void Fast_crossing_then_hold_is_rejected()
        {
            Assert.Empty(Begin().Move(46, 3.7).Hold(46, 0.5).Move(40, 0.1).Hold(40, 8).Run(new ClutchBiteDetector()));
        }

        [Fact]
        public void Moderate_crossing_then_hold_learns_the_true_bite()
        {
            List<double> s = Begin().Move(46, 3.7).Hold(46, 0.5).Move(40, 0.5).Hold(40, 8).Run(new ClutchBiteDetector());
            Assert.InRange(Assert.Single(s), TrueBite - 1, TrueBite + 1);
        }

        [Fact]
        public void Fast_dump_55_to_35_is_rejected()
        {
            Assert.Empty(Begin().Move(55, 3).Move(35, 0.15).Hold(35, 8).Run(new ClutchBiteDetector()));
        }

        [Fact]
        public void Throttle_swing_rejects()
        {
            var start = Begin().Move(30, 5);
            start.ThrottleAt5s = 40;      // the throttle comes in mid-release
            Assert.Empty(start.Run(new ClutchBiteDetector()));
        }

        [Fact]
        public void Speed_only_on_loose_surface_rejects_but_on_tarmac_is_kept()
        {
            var loose = Begin().Move(30, 5);
            loose.IdleGovernor = true;
            loose.Loose = true;
            Assert.Empty(loose.Run(new ClutchBiteDetector()));

            var tarmac = Begin().Move(30, 5);
            tarmac.IdleGovernor = true;
            double s = Assert.Single(tarmac.Run(new ClutchBiteDetector()));
            Assert.True(s < TrueBite, "a speed-only sample comes from the later speed onset: " + s);
        }

        [Fact]
        public void Not_armed_unless_stopped_in_gear_with_clutch_in()
        {
            // Never pressed past 90: no arming.
            Assert.Empty(new Start().Hold(85, 1).Move(30, 5).Run(new ClutchBiteDetector()));
            // In neutral the whole time: no arming.
            Assert.Empty(Begin().Move(30, 5).Run(new ClutchBiteDetector(), inGear: _ => false));
        }

        // ---- Learner -------------------------------------------------------------------------------

        private const string Key = "game|car|GameClutch";

        [Fact]
        public void Learner_blends_then_matures_then_stops()
        {
            var learner = new ClutchBiteLearner();
            foreach (double v in new[] { 44.0, 45.0, 46.0 }) Assert.True(learner.AddSample(Key, v));
            ClutchBiteStatus early = learner.Status(Key, configured: 50, auto: true);
            Assert.False(early.IsMatured);
            Assert.True(early.Final > 45 && early.Final < 50, "blends toward the learned median: " + early.Final);
            Assert.Equal(45.0, early.Learned.Value, 9);
            Assert.Equal(3, early.Count);

            foreach (double v in new[] { 44.0, 45.0, 46.0, 45.0, 44.5, 45.5 }) learner.AddSample(Key, v);
            ClutchBiteStatus matured = learner.Status(Key, 50, true);
            Assert.True(matured.IsMatured);
            Assert.Equal(45.0, matured.Final, 9);
            Assert.Empty(learner.ExportAll()[Key].Samples);

            Assert.False(learner.AddSample(Key, 30));           // learning has stopped for this car
            Assert.Equal(45.0, learner.Status(Key, 50, true).Final, 9);
        }

        [Fact]
        public void Learner_does_not_mature_on_spread_samples()
        {
            var learner = new ClutchBiteLearner();
            foreach (double v in new[] { 40.0, 50, 40, 50, 40, 50, 40, 50, 45 }) learner.AddSample(Key, v);
            Assert.False(learner.Status(Key, 50, true).IsMatured);
        }

        [Fact]
        public void Forget_returns_to_configured_and_learning_restarts()
        {
            var learner = new ClutchBiteLearner();
            for (int i = 0; i < 9; i++) learner.AddSample(Key, 45);
            Assert.True(learner.Status(Key, 50, true).IsMatured);

            learner.Forget(Key);
            ClutchBiteStatus s = learner.Status(Key, 50, true);
            Assert.False(s.IsMatured);
            Assert.Equal(50.0, s.Final);
            Assert.Null(s.Learned);
            Assert.True(learner.AddSample(Key, 47));
        }

        [Fact]
        public void Manual_mode_final_is_configured()
        {
            var learner = new ClutchBiteLearner();
            for (int i = 0; i < 9; i++) learner.AddSample(Key, 45);
            Assert.Equal(62.0, learner.Status(Key, 62, auto: false).Final);
        }

        [Fact]
        public void Keeps_the_last_15_samples()
        {
            var learner = new ClutchBiteLearner();
            for (int i = 0; i < 20; i++) learner.AddSample(Key, i % 2 == 0 ? 30 : 60);   // too spread to mature
            ClutchBiteLearnerState state = learner.ExportAll()[Key];
            Assert.Equal(15, state.Samples.Count);
            Assert.Equal(20, state.TotalCount);
        }

        [Fact]
        public void Export_import_round_trip_keeps_learning_and_matured_keys()
        {
            var learner = new ClutchBiteLearner();
            learner.AddSample("a", 44);
            for (int i = 0; i < 9; i++) learner.AddSample("b", 52);
            string json = JsonConvert.SerializeObject(learner.ExportAll());

            var restored = new ClutchBiteLearner();
            restored.Import(JsonConvert.DeserializeObject<Dictionary<string, ClutchBiteLearnerState>>(json));

            Assert.Equal(learner.Status("a", 50, true).Final, restored.Status("a", 50, true).Final, 9);
            Assert.True(restored.Status("b", 50, true).IsMatured);
            Assert.Equal(52.0, restored.Status("b", 50, true).Final, 9);
            Assert.Equal(KeyedKey(), ClutchBiteLearner.Key("g", "c", "GameClutch"));
        }

        private static string KeyedKey() => QAdvanceFeedback.Core.Normalized.KeyedGripLearner.MakeKey("g", "c", "GameClutch");
    }
}

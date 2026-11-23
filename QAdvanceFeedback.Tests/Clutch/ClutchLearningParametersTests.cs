using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Clutch;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Tests.Ui;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>
    /// Owner's follow-up (docs\clutch-bite-design.md, "Learning parameters"):
    /// <list type="bullet">
    /// <item>Restore all defaults resets the Clutch settings too;</item>
    /// <item>the clutch learning parameters are editable on the plugin page's Clutch tab only - never in
    /// the ShakeIt panel.</item>
    /// </list>
    /// </summary>
    public class ClutchLearningParametersTests
    {
        [Fact]
        public void Restore_all_defaults_resets_the_clutch_settings()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Clutch.BitePoint = 63;
            s.Clutch.Mode = ClutchBiteMode.Manual;
            s.Clutch.FallbackAxis = "JoystickPlugin.P_Z";
            s.Clutch.Learning.MatureSamples = 20;

            s.RestoreDefaults();

            Assert.Equal(JsonConvert.SerializeObject(new ClutchBiteSettings()), JsonConvert.SerializeObject(s.Clutch));
        }

        [Fact]
        public void Learning_defaults_are_the_designed_values_and_clamp()
        {
            ClutchLearningSettings l = new ClutchBiteSettings().Learning;
            Assert.Equal(90.0, l.ArmClutchPercent);
            Assert.Equal(4.0, l.RpmDipPercent);
            Assert.Equal(2.0, l.ConfirmSpeedKmh);
            Assert.Equal(30.0, l.MaxReleaseRatePerSec);
            Assert.Equal(15.0, l.MaxThrottleSwing);
            Assert.Equal(2.0, l.SteadyIdlePercent);
            Assert.Equal(9, l.MatureSamples);
            Assert.Equal(3.0, l.MatureSpreadPoints);

            l.ArmClutchPercent = 20; l.MatureSamples = 1; l.MaxReleaseRatePerSec = 9999;
            Assert.Equal(70.0, l.ArmClutchPercent);
            Assert.Equal(3, l.MatureSamples);
            Assert.Equal(200.0, l.MaxReleaseRatePerSec);
        }

        [Fact]
        public void Old_settings_file_without_learning_section_gets_defaults()
        {
            var back = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>("{ \"Clutch\": { \"BitePoint\": 44 } }");
            Assert.Equal(44.0, back.Clutch.BitePoint);
            Assert.Equal(9, back.Clutch.Learning.MatureSamples);
        }

        [Fact]
        public void Hub_writes_the_learning_parameters_as_one_field()
        {
            var s = new QAdvanceFeedbackSettings();
            var hub = new SettingsHub(() => s, _ => { });
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            ClutchLearningParameters p = s.Clutch.Learning.ToParameters().With(matureSamples: 12);
            Assert.True(hub.Set(SettingField.ClutchLearning, p, SettingsOrigin.PluginPage));

            Assert.Equal(12, s.Clutch.Learning.MatureSamples);
            Assert.Equal(new[] { SettingField.ClutchLearning }, got.Fields);
            Assert.False(hub.Set(SettingField.ClutchLearning, p, SettingsOrigin.PluginPage));   // unchanged
        }

        // ---- The parameters reach the detector and the learner -----------------------------------

        private static ClutchFrame Frame(double t, double clutch, double speed, double rpm) =>
            new ClutchFrame(t, clutch, rpm, speed, 10, true, false, true);

        /// <summary>Hold 100, 90, then 90 → 46 slowly, hold, 46 → 40 in 0.1 s, hold (true bite 45).</summary>
        private static List<double> FastCrossing(ClutchBiteDetector d, double top = 100)
        {
            const double dt = 1.0 / 60.0;
            var c = new List<double>();
            for (int i = 0; i < 120; i++) c.Add(top);
            for (int i = 1; i <= 220; i++) c.Add(top - (top - 46) * i / 220);
            for (int i = 0; i < 30; i++) c.Add(46);
            for (int i = 1; i <= 6; i++) c.Add(46 - i);
            for (int i = 0; i < 480; i++) c.Add(40);
            var samples = new List<double>();
            double speed = 0;
            for (int i = 0; i < c.Count; i++)
            {
                double engaged = Math.Max(0, 45 - c[Math.Max(0, i - 2)]);
                speed += dt * 0.5 * Math.Max(0, engaged - 2);
                if (d.Update(Frame(i * dt, c[i], speed, 900 - 20 * engaged), out double s)) samples.Add(s);
            }
            return samples;
        }

        [Fact]
        public void A_looser_release_speed_limit_accepts_a_crossing_the_default_rejects()
        {
            Assert.Empty(FastCrossing(new ClutchBiteDetector()));
            var loose = new ClutchBiteDetector { Parameters = ClutchLearningParameters.Default.With(maxReleaseRatePerSec: 150) };
            Assert.Single(FastCrossing(loose));
        }

        [Fact]
        public void A_higher_arm_level_needs_the_clutch_pressed_further()
        {
            // The pedal is only pressed to 92 before the start (the looser speed limit keeps the crossing).
            var normal = new ClutchBiteDetector { Parameters = ClutchLearningParameters.Default.With(maxReleaseRatePerSec: 150) };
            Assert.Single(FastCrossing(normal, top: 92));
            var strict = new ClutchBiteDetector { Parameters = ClutchLearningParameters.Default.With(maxReleaseRatePerSec: 150, armClutchPercent: 95) };
            Assert.Empty(FastCrossing(strict, top: 92));
        }

        [Fact]
        public void Mature_samples_and_spread_are_honoured()
        {
            var learner = new ClutchBiteLearner();
            var p = ClutchLearningParameters.Default.With(matureSamples: 5, matureSpreadPoints: 1);
            for (int i = 0; i < 5; i++) learner.AddSample("k", 45 + (i % 2) * 0.5, p);
            Assert.True(learner.Status("k", 50, true).IsMatured);

            var wide = new ClutchBiteLearner();
            for (int i = 0; i < 9; i++) wide.AddSample("k", 45 + (i % 2) * 2.5, ClutchLearningParameters.Default.With(matureSpreadPoints: 1));
            Assert.False(wide.Status("k", 50, true).IsMatured);
        }

        // ---- Page only -------------------------------------------------------------------------------

        [Fact]
        public void Page_view_model_shows_and_writes_learning_parameters_the_ShakeIt_one_does_not()
        {
            var host = new FakePanelHost();
            var page = new ClutchBiteViewModel(host, new FakeClutchHost(), new ManualTicker(), new ImmediateDispatcher(), showLearning: true);
            page.Activate();
            Assert.True(page.ShowLearning);
            page.Learning.MatureSamples = 11;
            page.Learning.MaxReleaseRatePerSec = 45;
            Assert.Equal(11, host.Settings.Clutch.Learning.MatureSamples);
            Assert.Equal(45.0, host.Settings.Clutch.Learning.MaxReleaseRatePerSec);

            var panel = new ClutchBiteViewModel(host, new FakeClutchHost(), new ManualTicker(), new ImmediateDispatcher());
            Assert.False(panel.ShowLearning);
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(Path.Combine(dir.FullName, "QAdvanceFeedback"), Path.Combine(parts));
        }
    }
}

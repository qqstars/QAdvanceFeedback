using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class SlipAngleChannelSettingsTests
    {
        [Fact]
        public void Defaults_match_spec()
        {
            var s = new SlipAngleChannelSettings();
            Assert.Equal(SourceMode.Manual, s.SourceMode);
            Assert.Equal(0.85, s.WeightMax);
            Assert.Equal(0.15, s.WeightMin);
            Assert.True(s.CrossingGateEnabled);
            Assert.Equal(TriggerBy.Any, s.Gate.TriggerBy);
            Assert.NotNull(s.KeyData);
            Assert.NotNull(s.Projector);
            Assert.NotNull(s.Pulse);
            Assert.NotNull(s.Drift);
        }

        [Fact]
        public void Old_config_without_slip_angle_loads_defaults()
        {
            string path = Path.Combine(Path.GetTempPath(), "qaf-sa-" + System.Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(path, "{\"Version\":1}");
                var loaded = ConfigStore.Load(path);
                Assert.NotNull(loaded.SlipAngle);
                Assert.Equal(0.85, loaded.SlipAngle.WeightMax);
                Assert.NotNull(loaded.SlipAngle.Gate);
                Assert.NotNull(loaded.SlipAngle.KeyData);
                Assert.NotNull(loaded.SlipAngle.Drift);
                Assert.Equal(SourceMode.Manual, loaded.SlipAngle.SourceMode);

                File.WriteAllText(path, "{\"SlipAngle\":null}");
                Assert.NotNull(ConfigStore.Load(path).SlipAngle);

                File.WriteAllText(path, "{\"SlipAngle\":{\"Gate\":null,\"Drift\":null,\"Pulse\":null,\"Projector\":null,\"KeyData\":null,\"SourceMode\":1}}");
                var n = ConfigStore.Load(path).SlipAngle;
                Assert.NotNull(n.Gate); Assert.NotNull(n.Drift); Assert.NotNull(n.Pulse);
                Assert.NotNull(n.Projector); Assert.NotNull(n.KeyData);
                Assert.Equal(SourceMode.Manual, n.SourceMode);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Restore_defaults_keeps_key_data()
        {
            var s = new QAdvanceFeedbackSettings();
            var kd = s.SlipAngle.KeyData;
            s.SlipAngle.WeightMax = 0.5;
            s.RestoreDefaults();
            Assert.Same(kd, s.SlipAngle.KeyData);
            Assert.Equal(0.85, s.SlipAngle.WeightMax);
        }

        [Fact]
        public void Round_trip_json()
        {
            var s = new QAdvanceFeedbackSettings();
            s.SlipAngle.SourceMode = SourceMode.Custom;
            s.SlipAngle.WeightMax = 0.7;
            s.SlipAngle.WeightMin = 0.3;
            s.SlipAngle.CrossingGateEnabled = false;
            s.SlipAngle.Gate.BrakeThreshold = 12;
            s.SlipAngle.Drift.Gamma = 3.0;
            s.SlipAngle.CustomSources = new WheelSourceSet { FrontLeft = "a" };
            var back = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));
            back.Normalise();
            Assert.Equal(SourceMode.Custom, back.SlipAngle.SourceMode);
            Assert.Equal(0.7, back.SlipAngle.WeightMax);
            Assert.Equal(0.3, back.SlipAngle.WeightMin);
            Assert.False(back.SlipAngle.CrossingGateEnabled);
            Assert.Equal(12, back.SlipAngle.Gate.BrakeThreshold);
            Assert.Equal(3.0, back.SlipAngle.Drift.Gamma);
            Assert.Equal("a", back.SlipAngle.CustomSources.FrontLeft);
        }

        [Fact]
        public void Drift_values_clamped_to_ranges()
        {
            var s = new QAdvanceFeedbackSettings();
            s.SlipAngle.Drift.Gamma = 99;
            s.SlipAngle.Drift.HoldSeconds = -5;
            s.SlipAngle.Drift.ToPercent = double.NaN;
            s.Normalise();
            Assert.Equal(Core.Projection.DriftSettings.MaxGamma, s.SlipAngle.Drift.Gamma);
            Assert.Equal(Core.Projection.DriftSettings.MinHoldSeconds, s.SlipAngle.Drift.HoldSeconds);
            Assert.Equal(Core.Projection.DriftSettings.DefaultToPercent, s.SlipAngle.Drift.ToPercent);
        }

        [Fact]
        public void Weights_are_finite_and_non_negative()
        {
            var s = new SlipAngleChannelSettings { WeightMax = -1, WeightMin = double.PositiveInfinity };
            Assert.Equal(0.85, s.WeightMax);
            Assert.Equal(0.15, s.WeightMin);
        }
    }
}

namespace QAdvanceFeedback.Tests
{
    public class SlipAngleWatcherNormaliseTests : System.IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "qaf-sawatch-" + System.Guid.NewGuid().ToString("N"));

        public SlipAngleWatcherNormaliseTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        [Fact]
        public void Watcher_path_normalises_null_slip_angle()
        {
            string path = Path.Combine(_dir, "c.json");
            File.WriteAllText(path, "{\"Version\":1}");
            QAdvanceFeedbackSettings got = null;
            using (new ConfigFileWatcher(path, () => null, s => got = s, debounceMs: 100))
            {
                File.WriteAllText(path, "{\"SlipAngle\":{\"Gate\":null,\"Drift\":{\"Gamma\":99}}}");
                for (int i = 0; i < 50 && got == null; i++) System.Threading.Thread.Sleep(100);
            }
            Assert.NotNull(got);
            Assert.NotNull(got.SlipAngle.Gate);
            Assert.Equal(Core.Projection.DriftSettings.MaxGamma, got.SlipAngle.Drift.Gamma);

            got = null;
            using (new ConfigFileWatcher(path, () => null, s => got = s, debounceMs: 100))
            {
                File.WriteAllText(path, "{\"SlipAngle\":null,\"Version\":2}");
                for (int i = 0; i < 50 && got == null; i++) System.Threading.Thread.Sleep(100);
            }
            Assert.NotNull(got);
            Assert.NotNull(got.SlipAngle);
        }
    }
}

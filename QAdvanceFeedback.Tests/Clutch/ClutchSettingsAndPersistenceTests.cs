using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Clutch;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>Clutch Bite settings, their hub fields, and the learned bite points in
    /// QAdvanceFeedback.Parameters.json (docs\clutch-bite-design.md).</summary>
    public class ClutchSettingsAndPersistenceTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-clutch-" + Guid.NewGuid() + ".json");
        private RuntimeStore _store;

        public void Dispose()
        {
            _store?.Dispose();
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void Defaults_match_the_design()
        {
            var c = new QAdvanceFeedbackSettings().Clutch;
            Assert.Equal(ClutchApplyDirection.PressAndRelease, c.Apply);
            Assert.Equal(ClutchBiteMode.Auto, c.Mode);
            Assert.Equal(50.0, c.BitePoint);
            Assert.Equal(30.0, c.FadedRange);
            Assert.Equal(2.5, c.Gamma);
            Assert.Null(c.FallbackAxis);
            Assert.False(c.InvertAxis);
        }

        [Fact]
        public void Setters_clamp()
        {
            var c = new ClutchBiteSettings { BitePoint = 3, FadedRange = 99, Gamma = 0.2 };
            Assert.Equal(10.0, c.BitePoint);
            Assert.Equal(50.0, c.FadedRange);
            Assert.Equal(1.0, c.Gamma);
            c.BitePoint = 95; c.FadedRange = 0; c.Gamma = 9;
            Assert.Equal(90.0, c.BitePoint);
            Assert.Equal(1.0, c.FadedRange);
            Assert.Equal(6.0, c.Gamma);
        }

        [Theory]
        [InlineData(SettingField.ClutchApply, ClutchApplyDirection.ReleaseOnly)]
        [InlineData(SettingField.ClutchBiteMode, ClutchBiteMode.Manual)]
        [InlineData(SettingField.ClutchBitePoint, 42.0)]
        [InlineData(SettingField.ClutchFadedRange, 20.0)]
        [InlineData(SettingField.ClutchGamma, 3.0)]
        [InlineData(SettingField.ClutchAxis, "JoystickPlugin.Pedals_Z")]
        [InlineData(SettingField.ClutchAxisInvert, true)]
        public void Hub_sets_each_clutch_field_and_raises_it(SettingField field, object value)
        {
            var s = new QAdvanceFeedbackSettings();
            int saves = 0;
            var hub = new SettingsHub(() => s, _ => saves++);
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            Assert.True(hub.Set(field, value, SettingsOrigin.ShakeItPanel));

            Assert.Equal(value, SettingsHub.Read(s, field));
            Assert.Equal(new[] { field }, got.Fields);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Diff_reports_clutch_fields()
        {
            var a = new QAdvanceFeedbackSettings();
            var b = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(a));
            b.Clutch.BitePoint = 61;
            b.Clutch.InvertAxis = true;
            Assert.Equal(new[] { SettingField.ClutchBitePoint, SettingField.ClutchAxisInvert }, SettingsHub.Diff(a, b));
        }

        [Fact]
        public void Settings_json_round_trip()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Clutch.Apply = ClutchApplyDirection.PressOnly;
            s.Clutch.Mode = ClutchBiteMode.Manual;
            s.Clutch.BitePoint = 37;
            s.Clutch.FallbackAxis = "JoystickPlugin.Pedals_Z";
            var back = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));
            Assert.Equal(JsonConvert.SerializeObject(s.Clutch), JsonConvert.SerializeObject(back.Clutch));
        }

        [Fact]
        public void Old_settings_file_without_clutch_section_gets_defaults()
        {
            var back = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>("{ \"Version\": 1 }");
            Assert.NotNull(back.Clutch);
            Assert.Equal(50.0, back.Clutch.BitePoint);
        }

        [Fact]
        public void Runtime_store_round_trips_clutch_learners_and_last_key()
        {
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            var learners = new Dictionary<string, ClutchBiteLearnerState>
            {
                ["g|#|car|#|GameClutch"] = new ClutchBiteLearnerState { Samples = new List<double> { 44, 46 }, TotalCount = 2 },
                ["g|#|other|#|GameClutch"] = new ClutchBiteLearnerState { TotalCount = 9, Matured = 51 },
            };
            _store.SaveClutchBiteLearners(learners);
            _store.SaveLastClutchKey("g|#|car|#|GameClutch");
            _store.Flush();
            _store.Dispose();

            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            _store.LoadClutchBiteLearners(out var back);
            Assert.Equal(new List<double> { 44, 46 }, back["g|#|car|#|GameClutch"].Samples);
            Assert.Equal(51.0, back["g|#|other|#|GameClutch"].Matured);
            Assert.Equal("g|#|car|#|GameClutch", _store.LoadLastClutchKey());
        }

        [Fact]
        public void Old_runtime_file_without_clutch_section_loads_empty()
        {
            File.WriteAllText(_path, "{ \"Version\": 11 }");
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            _store.LoadClutchBiteLearners(out var data);
            Assert.Empty(data);
            Assert.Null(_store.LoadLastClutchKey());
        }
    }
}

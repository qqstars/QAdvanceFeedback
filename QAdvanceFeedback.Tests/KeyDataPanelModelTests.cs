using System;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The key data points (Auto/Manual, SMax-only vs SMax/S90/S75, the three values) shared by the
    /// plugin settings page and the native WheelLock/WheelSlip ShakeIt panels
    /// (docs\shakeit-native-effects-design.md, "Key data points in the ShakeIt panel").
    /// </summary>
    public class KeyDataPanelModelTests
    {
        private const string Game = "IRacing";

        private static WheelChannelSettings Lock() => WheelChannelSettings.CreateLockDefaults();

        private static readonly KeyDataTriple NoLearned = null;

        /// <summary>A learned triple as <see cref="KeyDataPanelModel.LearnedKeyData"/> forms it for Slip:
        /// SMax rounded to 0.1, S90/S75 derived from it.</summary>
        private static KeyDataTriple Learned(double sMax)
        {
            double s = Math.Round(sMax, 1);
            KeyDataPointSettings.DeriveLowerAnchors(s, out double s90, out double s75);
            return new KeyDataTriple(s, Math.Round(s90, 1), Math.Round(s75, 1));
        }

        private static void Shipped(WheelChannelSettings ch, bool isLock, out double sMax, out double s90, out double s75) =>
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(KeyDataPanelModel.Identity(ch), isLock,
                KeyDataPointDefaults.CreateShipped(), out sMax, out s90, out s75));

        // ---- view -------------------------------------------------------------------------------

        [Fact]
        public void Auto_is_a_read_only_readout_of_the_learned_value()
        {
            var ch = Lock();
            ch.KeyDataPoints.SetAutoGenerate(KeyDataPanelModel.Identity(ch), true);

            KeyDataView v = KeyDataPanelModel.Resolve(ch, true, Game, Learned(42.04), KeyDataPointDefaults.CreateShipped());

            Assert.True(v.Auto);
            Assert.False(v.Editable);
            KeyDataPointSettings.DeriveLowerAnchors(42.0, out double s90, out double s75);
            Assert.Equal(42.0, v.SMax);
            Assert.Equal(Math.Round(s90, 1), v.S90);
            Assert.Equal(Math.Round(s75, 1), v.S75);
        }

        [Fact]
        public void Auto_with_nothing_learned_shows_the_shipped_reference()
        {
            var ch = Lock();
            ch.KeyDataPoints.SetAutoGenerate(KeyDataPanelModel.Identity(ch), true);
            Shipped(ch, true, out double sMax, out double s90, out double s75);

            KeyDataView v = KeyDataPanelModel.Resolve(ch, true, Game, NoLearned, KeyDataPointDefaults.CreateShipped());

            Assert.Equal((sMax, s90, s75), (v.SMax.Value, v.S90.Value, v.S75.Value));
        }

        [Fact]
        public void Manual_is_editable_and_shows_the_stored_slot()
        {
            var ch = Lock();
            string id = KeyDataPanelModel.Identity(ch);
            ch.KeyDataPoints.SetAutoGenerate(id, false);
            ch.KeyDataPoints.SetManual(Game, id, 60, 50, 40, seeded: true);

            KeyDataView v = KeyDataPanelModel.Resolve(ch, true, Game, Learned(95), KeyDataPointDefaults.CreateShipped());

            Assert.False(v.Auto);
            Assert.True(v.Editable);
            Assert.Equal((60.0, 50.0, 40.0), (v.SMax.Value, v.S90.Value, v.S75.Value));
        }

        [Fact]
        public void Manual_with_nothing_stored_prefills_the_shipped_defaults()
        {
            var ch = Lock();
            ch.KeyDataPoints.SetAutoGenerate(KeyDataPanelModel.Identity(ch), false);
            Shipped(ch, true, out double sMax, out _, out _);

            Assert.Equal(sMax, KeyDataPanelModel.Resolve(ch, true, Game, NoLearned, KeyDataPointDefaults.CreateShipped()).SMax);
        }

        [Fact]
        public void Max_grip_only_hides_S90_and_S75()
        {
            var ch = Lock();
            ch.NormalizePattern = NormalizePattern.MaxGripOnly;
            Assert.False(KeyDataPanelModel.Resolve(ch, true, Game, NoLearned, KeyDataPointDefaults.CreateShipped()).ShowLowerAnchors);
            ch.NormalizePattern = NormalizePattern.Mapping;
            Assert.True(KeyDataPanelModel.Resolve(ch, true, Game, NoLearned, KeyDataPointDefaults.CreateShipped()).ShowLowerAnchors);
        }

        [Fact]
        public void Per_game_manual_with_no_game_has_nowhere_to_write()
        {
            var ch = Lock();
            string id = KeyDataPanelModel.Identity(ch);
            ch.KeyDataPoints.SetAutoGenerate(id, false);
            ch.KeyDataPoints.SetPerGame(id, true);

            KeyDataView v = KeyDataPanelModel.Resolve(ch, true, null, NoLearned, KeyDataPointDefaults.CreateShipped());

            Assert.True(v.PerGameWithoutGame);
            Assert.Null(v.SMax);
        }

        // ---- commit (the page's PersistChannelIfManual rule) --------------------------------------

        [Fact]
        public void Commit_writes_a_valid_manual_triple_and_latches_it()
        {
            var ch = Lock();
            string id = KeyDataPanelModel.Identity(ch);
            ch.KeyDataPoints.SetAutoGenerate(id, false);

            Assert.True(KeyDataPanelModel.TryCommitManual(ch.KeyDataPoints, Game, id, 70, 60, 50, maxGripOnly: false));

            Assert.True(ch.KeyDataPoints.TryGetManual(Game, id, out double a, out double b, out double c));
            Assert.Equal((70.0, 60.0, 50.0), (a, b, c));
            Assert.True(ch.KeyDataPoints.IsSeeded(Game, id));
        }

        [Fact]
        public void Commit_under_max_grip_only_derives_S90_and_S75()
        {
            var ch = Lock();
            string id = KeyDataPanelModel.Identity(ch);
            ch.KeyDataPoints.SetAutoGenerate(id, false);

            Assert.True(KeyDataPanelModel.TryCommitManual(ch.KeyDataPoints, Game, id, 70, null, null, maxGripOnly: true));

            KeyDataPointSettings.DeriveLowerAnchors(70, out double s90, out double s75);
            ch.KeyDataPoints.TryGetManual(Game, id, out _, out double b, out double c);
            Assert.Equal((s90, s75), (b, c));
        }

        [Fact]
        public void Commit_is_refused_under_auto_invalid_or_per_game_without_game()
        {
            var ch = Lock();
            string id = KeyDataPanelModel.Identity(ch);
            ch.KeyDataPoints.SetAutoGenerate(id, true);
            Assert.False(KeyDataPanelModel.TryCommitManual(ch.KeyDataPoints, Game, id, 70, 60, 50, false));

            ch.KeyDataPoints.SetAutoGenerate(id, false);
            Assert.False(KeyDataPanelModel.TryCommitManual(ch.KeyDataPoints, Game, id, 50, 60, 70, false));   // out of order

            ch.KeyDataPoints.SetPerGame(id, true);
            Assert.False(KeyDataPanelModel.TryCommitManual(ch.KeyDataPoints, null, id, 70, 60, 50, false));
        }

        // ---- shared through the settings hub ------------------------------------------------------

        private static SettingsHub Hub(QAdvanceFeedbackSettings s) => new SettingsHub(() => s, _ => { }, () => Game);

        [Fact]
        public void Hub_sets_auto_for_the_current_source()
        {
            var s = new QAdvanceFeedbackSettings();
            string id = KeyDataPanelModel.Identity(s.Lock);
            bool before = s.Lock.KeyDataPoints.GetAutoGenerate(id);

            Assert.True(Hub(s).Set(SettingField.LockKeyDataAuto, !before, SettingsOrigin.ShakeItPanel));
            Assert.Equal(!before, s.Lock.KeyDataPoints.GetAutoGenerate(id));
        }

        [Fact]
        public void Hub_sets_the_pattern()
        {
            var s = new QAdvanceFeedbackSettings();
            NormalizePattern other = s.Slip.NormalizePattern == NormalizePattern.Mapping ? NormalizePattern.MaxGripOnly : NormalizePattern.Mapping;
            Assert.True(Hub(s).Set(SettingField.SlipNormalizePattern, other, SettingsOrigin.ShakeItPanel));
            Assert.Equal(other, s.Slip.NormalizePattern);
        }

        [Fact]
        public void Hub_sets_manual_values_and_rejects_invalid_ones()
        {
            var s = new QAdvanceFeedbackSettings();
            string id = KeyDataPanelModel.Identity(s.Lock);
            s.Lock.KeyDataPoints.SetAutoGenerate(id, false);
            SettingsHub hub = Hub(s);

            Assert.True(hub.Set(SettingField.LockKeyDataValues, new KeyDataTriple(72, 61, 49), SettingsOrigin.ShakeItPanel));
            Assert.Equal(new KeyDataTriple(72, 61, 49), SettingsHub.Read(s, SettingField.LockKeyDataValues, Game));

            Assert.False(hub.Set(SettingField.LockKeyDataValues, new KeyDataTriple(40, 61, 49), SettingsOrigin.ShakeItPanel));
            Assert.Equal(new KeyDataTriple(72, 61, 49), SettingsHub.Read(s, SettingField.LockKeyDataValues, Game));
        }

        [Fact]
        public void Diff_reports_key_data_and_pattern_changes()
        {
            var a = new QAdvanceFeedbackSettings();
            var b = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(a));
            string id = KeyDataPanelModel.Identity(b.Lock);
            b.Lock.KeyDataPoints.SetAutoGenerate(id, !a.Lock.KeyDataPoints.GetAutoGenerate(id));
            b.Slip.NormalizePattern = a.Slip.NormalizePattern == NormalizePattern.Mapping ? NormalizePattern.MaxGripOnly : NormalizePattern.Mapping;

            Assert.Equal(new[] { SettingField.LockKeyDataAuto, SettingField.SlipNormalizePattern }, SettingsHub.Diff(a, b, Game));
        }
    }
}

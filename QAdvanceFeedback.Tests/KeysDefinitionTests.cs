using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.KeyRules;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.RawCalculator.Calibration;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Version 21 keys (owner, 2026-11-25): only a learner key's SOURCE part is hashed ("21:&lt;number&gt;", a stable
    /// 64-bit FNV-1a of the full source configuration), game / car / surface stay readable for borrowing; versioned rules
    /// behind a factory; the calibration key starts with the game and leaves SimHub's generated ids out.
    /// </summary>
    public sealed class KeysDefinitionTests
    {
        // ---------------------------------------------------------------- the hash

        [Fact]
        public void The_hash_is_FNV1a_64_over_UTF8_and_depends_only_on_the_string()
        {
            Assert.Equal(14695981039346656037UL, StableHash.Fnv1a64(string.Empty));   // the offset basis
            Assert.Equal(0xaf63dc4c8601ec8cUL, StableHash.Fnv1a64("a"));             // the published test vector
            Assert.Equal(0x85944171f73967e8UL, StableHash.Fnv1a64("foobar"));
            Assert.Equal(StableHash.Fnv1a64("Plain:x~Plain:y"), StableHash.Fnv1a64("Plain:" + "x~Plain:y"));
            Assert.NotEqual(StableHash.Fnv1a64("abc"), StableHash.Fnv1a64("abd"));
            // Non-ASCII is hashed by its UTF-8 bytes (not UTF-16 code units).
            Assert.Equal(StableHash.Fnv1a64("é"), Fnv(new byte[] { 0xc3, 0xa9 }));
        }

        private static ulong Fnv(byte[] bytes)
        {
            ulong h = 14695981039346656037UL;
            unchecked { foreach (byte b in bytes) { h ^= b; h *= 1099511628211UL; } }
            return h;
        }

        // ---------------------------------------------------------------- rules and helpers

        [Fact]
        public void The_factory_builds_every_released_version_and_GetKey_writes_the_latest()
        {
            Assert.Equal("21", KeyRule.LatestVersion);
            Assert.Equal(21, KeyRule.LatestVersionNumber);
            Assert.Equal(21, new Core.Runtime.RuntimeDocument().Version);   // the parameters file's version follows it
            Assert.IsType<KeyRule21>(KeyRuleFactory.CreateKeyRule("21"));
            Assert.Null(KeyRuleFactory.CreateKeyRule("99"));
            Assert.Null(KeyRuleFactory.CreateKeyRule(null));

            string key = Keys.GetKey(new LearnerSourceKeyConfig("Plain:A~Plain:B~Plain:C~Plain:D"));
            Assert.StartsWith("21:", key);
            Assert.True(Keys.IsKey(key));
            Assert.Equal("21", Keys.VersionOf(key));
            Assert.Equal(key, Keys.GetKey(new LearnerSourceKeyConfig("Plain:A~Plain:B~Plain:C~Plain:D")));   // stable
        }

        [Fact]
        public void Matches_uses_the_rule_of_the_version_the_key_names()
        {
            var config = new LearnerSourceKeyConfig("GameClutch");
            string key = Keys.GetKey(config);
            Assert.True(Keys.Matches(key, config));
            Assert.False(Keys.Matches(key, new LearnerSourceKeyConfig("None")));
            Assert.False(Keys.Matches("99:" + key.Substring(3), config));   // no such version
            Assert.False(Keys.Matches("Raw", config));                      // not a versioned key
            // Learner and Slip Angle configurations never share a key, even with the same text.
            Assert.NotEqual(key, Keys.GetKey(new SlipAngleSourceKeyConfig("GameClutch", string.Empty)));
        }

        [Fact]
        public void IsKey_accepts_only_version_colon_number()
        {
            Assert.True(Keys.IsKey("21:123"));
            Assert.False(Keys.IsKey("21:"));
            Assert.False(Keys.IsKey(":123"));
            Assert.False(Keys.IsKey("21:12a"));
            Assert.False(Keys.IsKey("Plain:QAdvanceFeedback.WheelLock.Raw.FrontLeft"));
            Assert.False(Keys.IsKey(null));
        }

        // ---------------------------------------------------------------- learner keys

        private const string LongSource = "Plain:QAdvanceFeedback.WheelLock.Raw.FrontLeft~Plain:QAdvanceFeedback.WheelLock.Raw.FrontRight"
            + "~Plain:QAdvanceFeedback.WheelLock.Raw.RearLeft~Plain:QAdvanceFeedback.WheelLock.Raw.RearRight";

        [Fact]
        public void A_learner_key_hashes_only_its_source_and_parses_back_to_the_identity()
        {
            string key = KeyedGripLearner.MakeKey("AssettoCorsaEvo", "BMW M2", LongSource, "Sealed");
            string[] parts = key.Split(new[] { "|#|" }, StringSplitOptions.None);
            Assert.Equal(new[] { "AssettoCorsaEvo", "BMW M2" }, new[] { parts[0], parts[1] });
            Assert.Equal(Keys.GetKey(new LearnerSourceKeyConfig(LongSource)), parts[2]);
            Assert.Equal("Sealed", parts[3]);

            Assert.True(ColdStartTierResolver.TryParseKey(key, out string g, out string c, out string s, out string b));
            Assert.Equal(("AssettoCorsaEvo", "BMW M2", LongSource, "Sealed"), (g, c, s, b));
            // Rebuilding the parsed key gives the same key; an empty source stays empty (the physical reference).
            Assert.Equal(key, KeyedGripLearner.MakeKey(g, c, s, b));
            Assert.Equal("G|#|C|#||#|", KeyedGripLearner.MakeKey("G", "C", string.Empty, string.Empty));
        }

        [Fact]
        public void A_hash_not_seen_this_session_parses_as_itself_and_rebuilds_to_the_same_key()
        {
            string unknown = "21:12345678901234567890";
            string key = "G|#|C|#|" + unknown + "|#|Sealed";
            Assert.True(ColdStartTierResolver.TryParseKey(key, out string g, out string c, out string s, out string b));
            Assert.Equal(unknown, s);
            Assert.Equal(key, KeyedGripLearner.MakeKey(g, c, s, b));
            Assert.Equal(key, SourceKeys.RekeyLearnerKey(key));
        }

        [Fact]
        public void A_version_20_section_is_rekeyed_in_place_and_rekeying_again_changes_nothing()
        {
            var old = new Dictionary<string, int>
            {
                ["FH6|#|Car_1|#|" + LongSource + "|#|Sealed"] = 1,
                ["FH6|#|Car_1|#||#|"] = 2,                           // no source: untouched
                ["__legacy_import__"] = 3,                             // not a learner key: untouched
                ["FH6|#|Car_1|#|GameClutch|#|"] = 4,                  // the Clutch key shape
            };
            Dictionary<string, int> once = SourceKeys.RekeyLearners(old);
            Assert.Equal(4, once.Count);
            Assert.Contains(KeyedGripLearner.MakeKey("FH6", "Car_1", LongSource, "Sealed"), once.Keys);
            Assert.Contains("FH6|#|Car_1|#||#|", once.Keys);
            Assert.Contains("__legacy_import__", once.Keys);
            Assert.Contains(KeyedGripLearner.MakeKey("FH6", "Car_1", "GameClutch"), once.Keys);
            Assert.Equal(once.Keys, SourceKeys.RekeyLearners(once).Keys);
            Assert.Null(SourceKeys.RekeyLearners<int>(null));
        }

        // ---------------------------------------------------------------- Slip Angle

        [Fact]
        public void A_slip_angle_key_hashes_the_games_definition_and_keeps_the_axle_readable()
        {
            Func<string, string> before = SourceKeys.SlipAngleDefinition;
            try
            {
                string definition = "{\"slipAngle\":[\"a\",\"b\",\"c\",\"d\"]}|unit=Degrees";
                SourceKeys.SlipAngleDefinition = game => game == "F12025" ? definition : string.Empty;
                SourceKeys.SlipAngleDefinitionChanged();

                string key = KeyedSlipAngleLearner.MakeKey("F12025", "Haas", SlipAngleFrameProcessor.NativeIdentity,
                    SlipAngleTarget.Front, SurfaceBucket.Sealed);
                string segment = key.Split(new[] { "|#|" }, StringSplitOptions.None)[2];
                Assert.Equal(Keys.GetKey(new SlipAngleSourceKeyConfig(SlipAngleFrameProcessor.NativeIdentity, definition)) + "/front", segment);

                Assert.True(KeyedSlipAngleLearner.TryParseKey(key, out string g, out string c, out string s, out SlipAngleTarget t, out _));
                Assert.Equal(("F12025", "Haas", SlipAngleFrameProcessor.NativeIdentity, SlipAngleTarget.Front), (g, c, s, t));

                // A changed definition (a source edited, another unit) learns under a new key.
                SourceKeys.SlipAngleDefinition = game => definition.Replace("Degrees", "Radians");
                SourceKeys.SlipAngleDefinitionChanged();
                Assert.NotEqual(key, KeyedSlipAngleLearner.MakeKey("F12025", "Haas", SlipAngleFrameProcessor.NativeIdentity,
                    SlipAngleTarget.Front, SurfaceBucket.Sealed));

                // A Version 20 key ("SlipAngle.PluginInternal.Native/front") is rekeyed with the game's definition.
                string old = "F12025|#|Haas|#|" + SlipAngleFrameProcessor.NativeIdentity + "/front|#|sealed";
                Assert.Equal(KeyedSlipAngleLearner.MakeKey("F12025", "Haas", SlipAngleFrameProcessor.NativeIdentity,
                    SlipAngleTarget.Front, SurfaceBucket.Sealed), SourceKeys.RekeyLearnerKey(old));
            }
            finally
            {
                SourceKeys.SlipAngleDefinition = before;
                SourceKeys.SlipAngleDefinitionChanged();
            }
        }

        // ---------------------------------------------------------------- config key data slots

        [Fact]
        public void A_version_20_configs_key_data_slots_and_flags_are_rekeyed_once()
        {
            var k = new KeyDataPointSettings();
            k.Values = new Dictionary<string, KeyDataPointEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["global|src:" + LongSource] = new KeyDataPointEntry { SMax = 40 },
                ["game:FH6|src:" + LongSource] = new KeyDataPointEntry { SMax = 50 },
            };
            k.SourceFlags = new Dictionary<string, KeyDataSourceFlags>(StringComparer.OrdinalIgnoreCase)
            {
                [LongSource] = new KeyDataSourceFlags { AutoGenerate = false, PerGame = true },
            };
            Assert.True(k.RekeySources());
            Assert.False(k.RekeySources());   // already in the new form

            string hashed = Keys.GetKey(new LearnerSourceKeyConfig(LongSource));
            Assert.Contains("global|src:" + hashed, k.Values.Keys);
            Assert.Contains("game:FH6|src:" + hashed, k.Values.Keys);
            Assert.Contains(hashed, k.SourceFlags.Keys);
            // Callers keep passing the readable identity.
            Assert.False(k.GetAutoGenerate(LongSource));
            Assert.True(k.GetPerGame(LongSource));
            Assert.Equal(KeyDataPointSettings.MakeSlotKey(false, null, LongSource), "global|src:" + hashed);
        }

        // ---------------------------------------------------------------- calibration key

        [Fact]
        public void The_calibration_key_starts_with_the_game_and_leaves_generated_ids_out()
        {
            Assert.Equal("F12025;Melbourne;Haas;Slip", CalibrationDataProvider.BuildKey("F12025", "Melbourne", "Haas", "Slip"));
            Assert.Equal("FH6;Car_3304;Slip", CalibrationDataProvider.BuildKey("FH6", "DT__261125043634", "Car_3304", "Slip"));
            Assert.Equal("FH6;RPSToSpeedFront", CalibrationDataProvider.BuildKey("FH6", "DT__1", "DC__2", "RPSToSpeedFront"));
            Assert.Equal("Melbourne;Haas;Slip", CalibrationDataProvider.BuildKey(null, "Melbourne", "Haas", "Slip"));
        }

        private static void Drive(CalibrationDataProvider p, string game, string track, string car)
        {
            p.GameId = game;
            p.Update(track, car, true, true, 120.0, 0.0, new double?[] { 30, 30, 30, 30 }, new double?[] { 0.1, 0.2, 0.3, 0.4 });
        }

        [Fact]
        public void Driving_learns_under_the_game_key()
        {
            var p = new CalibrationDataProvider();
            Drive(p, "F12025", "Melbourne", "Haas");
            Assert.Contains("F12025;Melbourne;Haas;Slip", p.CalibrationData.Keys);
            Assert.Contains("F12025;Melbourne;Haas;RPSToSpeedFront", p.CalibrationData.Keys);
            Drive(p, "FH6", "DT__260914080134", "Car_1392");
            Drive(p, "FH6", "DT__260916052416", "Car_1392");   // the next session: the same calibration
            Assert.Equal(4, p.CalibrationData["FH6;Car_1392;Slip"].Count / 2);
        }

        [Fact]
        public void An_older_files_entries_drop_generated_ids_and_move_under_the_game_the_first_time_they_are_met()
        {
            CalibrationData Entry()
            {
                var c = new CalibrationData();
                c.AddValue(0.5);
                return c;
            }
            var old = new Dictionary<string, CalibrationData>(StringComparer.Ordinal)
            {
                ["Melbourne;Haas;Slip"] = Entry(),
                ["Melbourne;Haas;RPSToSpeedFront"] = Entry(),
                ["DT__260914080134;Car_1392;Slip"] = Entry(),
            };
            var p = new CalibrationDataProvider();
            p.ImportCalibrations(old, fromOlderVersion: true);
            Assert.Equal(2, p.CalibrationData.Count);   // the generated-id entry is gone
            Assert.Contains(CalibrationDataProvider.LegacyPrefix + "Melbourne;Haas;Slip", p.CalibrationData.Keys);

            p.GameId = "F12025";
            Assert.True(p.HasCalibration("Melbourne", "Haas"));
            ICalibrationData read = p.GetSlipCalibration("Melbourne", "Haas", front: true);   // a read adopts it too
            Assert.Equal(1, ((CalibrationData)read.GetUnderlyingAutocalibration()).Count);
            Assert.Contains("F12025;Melbourne;Haas;Slip", p.CalibrationData.Keys);
            Assert.DoesNotContain(CalibrationDataProvider.LegacyPrefix + "Melbourne;Haas;Slip", p.CalibrationData.Keys);

            // Reset clears the adopted entry AND the legacy one not met yet (so nothing is adopted back).
            Assert.Equal(2, p.ResetCalibration("Melbourne", "Haas"));
            Assert.False(p.HasCalibration("Melbourne", "Haas"));
            Assert.Empty(p.CalibrationData);
        }

        [Fact]
        public void ClearAll_deletes_every_calibration()
        {
            var p = new CalibrationDataProvider();
            Drive(p, "F12025", "Melbourne", "Haas");
            Drive(p, "F12025", "Imola", "Williams");
            Assert.Equal(6, p.ClearAll());
            Assert.Empty(p.CalibrationData);
            Assert.False(p.ExportCalibrations(out bool _).Count > 0);
        }

        // ---------------------------------------------------------------- "Persist Calibration data?"

        private sealed class PersistHost : ICalibrationResetHost
        {
            public bool Persist = true;
            public readonly List<bool> Sets = new List<bool>();
            public bool HasCalibrationForCurrentKey() => Persist;
            public bool ResetCalibrationForCurrentKey() => false;
            public bool PersistCalibration => Persist;
            public void SetPersistCalibration(bool persist) { Sets.Add(persist); Persist = persist; }
        }

        [Fact]
        public void One_switch_instance_is_shared_and_a_change_applies_at_once()
        {
            var host = new PersistHost();
            var shared = new CalibrationPersistViewModel(() => host);
            var seenOnOtherTab = new List<string>();
            shared.PropertyChanged += (s, e) => seenOnOtherTab.Add(e.PropertyName);

            shared.Refresh();
            Assert.True(shared.IsOn);
            shared.IsOn = false;
            Assert.Equal(new[] { false }, host.Sets);   // applied at once, exactly once
            Assert.Contains(nameof(CalibrationPersistViewModel.IsOn), seenOnOtherTab);
            shared.IsOn = false;                          // unchanged: nothing applied again
            Assert.Single(host.Sets);

            host.Persist = true;                          // changed elsewhere (Restore All Defaults)
            shared.Refresh();
            Assert.True(shared.IsOn);
            Assert.Single(host.Sets);                     // a refresh never applies
        }

        [Fact]
        public void The_page_gives_both_tabs_the_same_switch_and_binds_it_two_way()
        {
            string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "QAdvanceFeedback", "Ui", "Views", "WheelChannelPageView.xaml"));
            Assert.Contains("DataContext=\"{Binding CalibrationPersist}\"", xaml);
            Assert.Contains("IsChecked=\"{Binding IsOn, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml);
            Assert.Contains("{v:Loc Calibration.Persist}", xaml);
            string page = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "QAdvanceFeedback", "Ui", "ViewModels", "SettingsPageViewModel.cs"));
            Assert.Contains("SlipTab.CalibrationPersist = LockTab.CalibrationPersist;", page);
            // Not on the ShakeIt panels (owner).
            string panel = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "QAdvanceFeedback", "Ui", "Views", "WheelChannelEffectView.xaml"));
            Assert.DoesNotContain("CalibrationPersist", panel);
            Assert.True(new GeneralSettings().PersistSlipLockCalibration);   // default on
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "QAdvanceFeedback.sln"))) dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repo root not found");
        }
    }
}

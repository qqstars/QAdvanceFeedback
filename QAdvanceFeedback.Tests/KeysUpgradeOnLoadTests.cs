using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core.KeyRules;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.RawCalculator.Calibration;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Owner, 2026-11-25: "the parameters json show version 21, but still using the old key". An older file's keys are
    /// converted IN THE STORE'S DOCUMENT at load (not only in the learners' copies), so the first write - which stamps
    /// Version 21 - carries the new keys; and a file already stamped 21 that still holds readable keys is repaired.
    /// A source part with no version prefix is Version 20 (KeyRule20 in the factory).
    /// </summary>
    public sealed class KeysUpgradeOnLoadTests : IDisposable
    {
        private const string Raw = "Plain:QAdvanceFeedback.WheelLock.Raw.FrontLeft~Plain:QAdvanceFeedback.WheelLock.Raw.FrontRight"
            + "~Plain:QAdvanceFeedback.WheelLock.Raw.RearLeft~Plain:QAdvanceFeedback.WheelLock.Raw.RearRight";

        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-keys-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        [Fact]
        public void A_source_part_without_a_version_is_version_20_and_the_factory_has_its_rule()
        {
            Assert.Equal("20", Keys.VersionOf(Raw));
            Assert.Equal("21", Keys.VersionOf("21:123"));
            Assert.Null(Keys.VersionOf(string.Empty));
            Assert.IsType<KeyRule20>(KeyRuleFactory.CreateKeyRule("20"));
            Assert.True(Keys.Matches(Raw, new LearnerSourceKeyConfig(Raw)));            // the readable identity itself
            Assert.False(Keys.Matches(Raw, new LearnerSourceKeyConfig("GameClutch")));
            Assert.True(Keys.Matches(Keys.GetKey(new LearnerSourceKeyConfig(Raw)), new LearnerSourceKeyConfig(Raw)));
        }

        private static JObject Grip() => new JObject { ["PeakG"] = 1.2, ["Samples"] = 50 };

        private JObject Load(int version, bool withNewCalibration = false)
        {
            var calibration = new JObject
            {
                ["Melbourne;Haas;Slip"] = new JObject { ["s"] = 1.0, ["c"] = 2, ["p"] = 3.0 },
                ["DT__260914080134;Car_1392;Slip"] = new JObject { ["s"] = 1.0, ["c"] = 2, ["p"] = 3.0 },
            };
            if (withNewCalibration) calibration["F12025;Imola;Williams;Slip"] = new JObject { ["s"] = 5.0, ["c"] = 6, ["p"] = 7.0 };
            var doc = new JObject
            {
                ["Version"] = version,
                ["LockLearners"] = new JObject { ["F12025|#|Haas|#|" + Raw + "|#|Sealed"] = Grip() },
                ["ClutchBiteLearners"] = new JObject { ["F12025|#|Haas|#|GameClutch|#|"] = new JObject { ["TotalCount"] = 3 } },
                ["LastClutchKey"] = "F12025|#|Haas|#|GameClutch|#|",
                ["LockScaleCrossCarSeed"] = new JObject { ["F12025|#|" + Raw] = new JObject { ["ColdCeiling"] = 40.0 } },
                ["SlipLockCalibration"] = calibration,
            };
            File.WriteAllText(_path, doc.ToString());

            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
            {
                Assert.Equal(20, store.LoadedVersion);   // a stamped-21 file with old keys reads as what it is
                Assert.True(store.LoadedOlderVersion);
                store.Flush();                            // no learner handed anything over: only the load's own rewrite
            }
            return JObject.Parse(File.ReadAllText(_path));
        }

        private static void AssertWrittenInTheNewForm(JObject written)
        {
            Assert.Equal(21, (int)written["Version"]);
            string hashed = Keys.GetKey(new LearnerSourceKeyConfig(Raw));
            Assert.Equal(new[] { "F12025|#|Haas|#|" + hashed + "|#|Sealed" }, ((JObject)written["LockLearners"]).Properties().Select(p => p.Name));
            string clutch = KeyedGripLearner.MakeKey("F12025", "Haas", "GameClutch");
            Assert.Equal(new[] { clutch }, ((JObject)written["ClutchBiteLearners"]).Properties().Select(p => p.Name));
            Assert.Equal(clutch, (string)written["LastClutchKey"]);
            Assert.DoesNotContain("Plain:", written.ToString());
            Assert.Empty((JObject)written["LockScaleCrossCarSeed"]);   // vestigial since 1.0.7: dropped
            JObject calibration = (JObject)written["SlipLockCalibration"];
            Assert.Contains(CalibrationDataProvider.LegacyPrefix + "Melbourne;Haas;Slip", calibration.Properties().Select(p => p.Name));
            Assert.DoesNotContain(calibration.Properties(), p => p.Name.Contains("DT__"));
        }

        [Fact]
        public void A_version_20_file_is_written_back_with_the_new_keys_before_any_learner_hands_data_over()
        {
            JObject written = Load(20);
            AssertWrittenInTheNewForm(written);
        }

        [Fact]
        public void A_file_stamped_21_with_old_keys_is_repaired_and_its_new_calibration_keys_are_kept()
        {
            JObject written = Load(21, withNewCalibration: true);
            AssertWrittenInTheNewForm(written);
            // A game-first key the faulty build already wrote (its game is one the file has learners for) stays as it is.
            Assert.Contains("F12025;Imola;Williams;Slip", ((JObject)written["SlipLockCalibration"]).Properties().Select(p => p.Name));
        }

        [Theory]
        [InlineData(20, false)]   // an older file: keys and latches readable
        [InlineData(21, true)]    // rewritten by the first fix: keys hashed, the latch reference still readable
        public void An_evidence_latchs_reference_key_is_converted_too(int version, bool sectionKeysAlreadyHashed)
        {
            string own = "F12025|#|Haas|#|" + Raw + "|#|";
            string lender = "F12025|#|Williams|#|" + Raw + "|#|";
            string ownKey = sectionKeysAlreadyHashed ? SourceKeys.RekeyLearnerKey(own) : own;
            var doc = new JObject
            {
                ["Version"] = version,
                ["LockScaleEvidence"] = new JObject
                {
                    [ownKey] = new JObject
                    {
                        ["Latches"] = new JArray(new JObject
                        {
                            ["Surface"] = "Sealed", ["Tier"] = 3, ["ReferenceKey"] = lender, ["ReferenceCeiling"] = 42.0,
                        }),
                    },
                },
            };
            File.WriteAllText(_path, doc.ToString());
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
            {
                Assert.Equal(20, store.LoadedVersion);   // either way the file is in the older form
                store.LoadScaleEvidence(true, out var evidence);
                ScaleTierLatch latch = evidence[SourceKeys.RekeyLearnerKey(own)].Latches[0];
                Assert.Equal(SourceKeys.RekeyLearnerKey(lender), latch.ReferenceKey);
                Assert.Equal(42.0, latch.ReferenceCeiling);
                store.Flush();
            }
            Assert.DoesNotContain("Plain:", File.ReadAllText(_path));
        }

        [Fact]
        public void A_proper_version_21_file_is_left_as_it_is()
        {
            string hashed = Keys.GetKey(new LearnerSourceKeyConfig(Raw));
            var doc = new JObject
            {
                ["Version"] = 21,
                ["LockLearners"] = new JObject { ["F12025|#|Haas|#|" + hashed + "|#|Sealed"] = Grip() },
                ["SlipLockCalibration"] = new JObject { ["F12025;Melbourne;Haas;Slip"] = new JObject { ["s"] = 1.0, ["c"] = 2, ["p"] = 3.0 } },
            };
            File.WriteAllText(_path, doc.ToString());
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
            {
                Assert.Equal(21, store.LoadedVersion);
                Assert.False(store.LoadedOlderVersion);
                store.LoadLockLearners(out var lockLearners);
                Assert.Equal(new[] { "F12025|#|Haas|#|" + hashed + "|#|Sealed" }, lockLearners.Keys);
                store.LoadSlipLockCalibration(out var calibration);
                Assert.Equal(new[] { "F12025;Melbourne;Haas;Slip" }, calibration.Keys);   // not taken for a legacy key
            }
        }
    }
}

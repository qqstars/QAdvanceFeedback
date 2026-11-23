using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>The hard SMax limit applies to TYPED values too (owner, 2026-11-19): a Manual key-data
    /// triple and a Custom cold-start reference are stored at most 95 - from the settings page, the
    /// ShakeIt panel, or a hand-edited settings file.</summary>
    public class KeyDataSMaxLimitTests
    {
        [Fact]
        public void The_typed_limit_is_the_learned_limit()
        {
            Assert.Equal(KeyedScaleLearner.MaxLearnedCeiling, KeyDataPointSettings.MaxSMax);
        }

        [Fact]
        public void A_manual_triple_above_95_is_stored_at_95()
        {
            var k = new KeyDataPointSettings();
            k.SetManual("", "Raw", 100.0, 98.0, 70.0, seeded: true);

            Assert.True(k.TryGetManual("", "Raw", out double sMax, out double s90, out double s75));
            Assert.Equal(95.0, sMax);
            Assert.Equal(95.0, s90);
            Assert.Equal(70.0, s75);
        }

        [Fact]
        public void The_panel_commit_clamps_too()
        {
            var k = new KeyDataPointSettings();
            k.SetAutoGenerate("Raw", false);
            Assert.True(KeyDataPanelModel.TryCommitManual(k, "", "Raw", 99.0, null, null, maxGripOnly: true));

            Assert.True(k.TryGetManual("", "Raw", out double sMax, out double s90, out double s75));
            Assert.Equal(95.0, sMax);
            Assert.True(s90 <= sMax && s75 <= s90);
        }

        [Fact]
        public void A_settings_file_entry_at_100_heals()
        {
            var entry = new KeyDataPointEntry { SMax = 100.0, S90 = 90.0, S75 = 70.0 };
            Assert.Equal(95.0, entry.SMax);
            Assert.Equal(90.0, entry.S90);
        }

        [Fact]
        public void A_custom_reference_above_95_is_stored_at_95()
        {
            var set = new KeyDataPointDefaultSet(100.0, 90.0, 70.0);
            Assert.Equal(95.0, set.SMax);
            Assert.True(set.IsUsable());
        }
    }
}

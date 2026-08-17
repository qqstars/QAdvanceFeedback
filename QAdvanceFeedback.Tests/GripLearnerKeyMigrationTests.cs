using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GripLearnerKeyMigration"/> - upgrading a persisted key up to the current
    /// 4-segment (gameId,carId,sourceIdentity,surfaceBucket) shape without ever discarding a driver's
    /// already-learned profile (docs\branch-dispatch-and-source-keyed-learning-report.md, "Part 2"
    /// MIGRATION requirement, extended for surface-keyed learning).
    /// </summary>
    public class GripLearnerKeyMigrationTests
    {
        [Fact]
        public void Null_source_returns_an_empty_not_null_dictionary()
        {
            var result = GripLearnerKeyMigration.MigrateLegacyKeys(null);
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void A_two_segment_legacy_key_is_upgraded_to_four_segments_with_both_sentinels()
        {
            // An ACTUAL pre-migration 2-segment key (predates source-keying AND surface-keying entirely).
            string trueLegacyKey = "GameA|#|Car1";
            var state = new GripLearnerState { PeakG = 2.5, Samples = 500 };
            var persisted = new Dictionary<string, GripLearnerState> { [trueLegacyKey] = state };

            var migrated = GripLearnerKeyMigration.MigrateLegacyKeys(persisted);

            string expectedKey = KeyedGripLearner.MakeKey(
                "GameA", "Car1", KeyedGripLearner.LegacySourcelessSourceIdentity, KeyedGripLearner.LegacyPreSurfaceSplitBucket);
            Assert.True(migrated.ContainsKey(expectedKey));
            Assert.Equal(2.5, migrated[expectedKey].PeakG, 9);
            Assert.Equal(500, migrated[expectedKey].Samples);
            Assert.DoesNotContain(trueLegacyKey, migrated.Keys);
        }

        [Fact]
        public void A_three_segment_post_part2_key_is_upgraded_to_four_segments_with_only_the_surface_sentinel()
        {
            // An ACTUAL post-Part-2, pre-surface-keying 3-segment key.
            string threeSegmentKey = "GameA|#|Car1|#|Plain:SomeProperty";
            var persisted = new Dictionary<string, GripLearnerState>
            {
                [threeSegmentKey] = new GripLearnerState { PeakG = 1.1, Samples = 40 }
            };

            var migrated = GripLearnerKeyMigration.MigrateLegacyKeys(persisted);

            string expectedKey = KeyedGripLearner.MakeKey("GameA", "Car1", "Plain:SomeProperty", KeyedGripLearner.LegacyPreSurfaceSplitBucket);
            Assert.True(migrated.ContainsKey(expectedKey));
            Assert.Equal(1.1, migrated[expectedKey].PeakG, 9);
            Assert.DoesNotContain(threeSegmentKey, migrated.Keys);
        }

        [Fact]
        public void An_already_four_segment_key_is_left_completely_unchanged()
        {
            string modernKey = KeyedGripLearner.MakeKey("GameA", "Car1", "Plain:SomeProperty", "Sealed");
            var persisted = new Dictionary<string, GripLearnerState> { [modernKey] = new GripLearnerState { PeakG = 1.1, Samples = 40 } };

            var migrated = GripLearnerKeyMigration.MigrateLegacyKeys(persisted);

            Assert.True(migrated.ContainsKey(modernKey));
            Assert.Single(migrated);
        }

        [Fact]
        public void The_flat_LegacyImportKey_pseudo_key_is_left_completely_unchanged()
        {
            var persisted = new Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.LegacyImportKey] = new GripLearnerState { PeakG = 3.0, Samples = 100 }
            };

            var migrated = GripLearnerKeyMigration.MigrateLegacyKeys(persisted);

            Assert.True(migrated.ContainsKey(KeyedGripLearner.LegacyImportKey));
            Assert.Equal(3.0, migrated[KeyedGripLearner.LegacyImportKey].PeakG, 9);
        }

        [Fact]
        public void Null_key_or_null_value_entries_are_skipped_without_throwing()
        {
            var persisted = new Dictionary<string, GripLearnerState>
            {
                ["GameA|#|Car1"] = null,
            };
            var migrated = GripLearnerKeyMigration.MigrateLegacyKeys(persisted);
            Assert.Empty(migrated);
        }

        [Fact]
        public void Multiple_legacy_keys_all_migrate_independently()
        {
            var persisted = new Dictionary<string, GripLearnerState>
            {
                ["GameA|#|Car1"] = new GripLearnerState { PeakG = 1.0, Samples = 50 },
                ["GameA|#|Car2"] = new GripLearnerState { PeakG = 2.0, Samples = 60 },
                ["GameB|#|Car1"] = new GripLearnerState { PeakG = 3.0, Samples = 70 },
            };

            var migrated = GripLearnerKeyMigration.MigrateLegacyKeys(persisted);

            Assert.Equal(3, migrated.Count);
            Assert.True(migrated.ContainsKey(KeyedGripLearner.MakeKey("GameA", "Car1", KeyedGripLearner.LegacySourcelessSourceIdentity, KeyedGripLearner.LegacyPreSurfaceSplitBucket)));
            Assert.True(migrated.ContainsKey(KeyedGripLearner.MakeKey("GameA", "Car2", KeyedGripLearner.LegacySourcelessSourceIdentity, KeyedGripLearner.LegacyPreSurfaceSplitBucket)));
            Assert.True(migrated.ContainsKey(KeyedGripLearner.MakeKey("GameB", "Car1", KeyedGripLearner.LegacySourcelessSourceIdentity, KeyedGripLearner.LegacyPreSurfaceSplitBucket)));
        }
    }
}

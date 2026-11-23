using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The one-time load migration of the retired hidden "__layer3_raw_fallback__" key into each channel's
    /// own internal (Plugin Internal) key - docs\internal-source-learning-design.md 3.1.
    /// </summary>
    public class InternalSourceKeyMigrationTests
    {
        private const string Hidden = InternalSourceKeyMigration.RetiredHiddenSourceIdentity;
        private static string LockRaw => RawSourceFallback.RawIdentity(true);
        private static string SlipRaw => RawSourceFallback.RawIdentity(false);

        private static string Key(string game, string car, string source, string surface = "")
            => KeyedGripLearner.MakeKey(game, car, source, surface);

        private static ScaleLearnerState Scale(double ceiling) => new ScaleLearnerState { ColdCeiling = ceiling, ColdIsPrimaryTier = true };

        [Fact]
        public void Internal_key_with_no_evidence_adopts_the_hidden_state()
        {
            var hiddenState = Scale(71.5);
            var data = new Dictionary<string, ScaleLearnerState> { [Key("G", "C", Hidden)] = hiddenState };

            Dictionary<string, ScaleLearnerState> migrated = InternalSourceKeyMigration.MigrateScale(data, LockRaw);

            Assert.Same(hiddenState, migrated[Key("G", "C", LockRaw)]);
            Assert.False(migrated.ContainsKey(Key("G", "C", Hidden)));
        }

        [Fact]
        public void Internal_key_with_evidence_is_kept_and_the_hidden_one_dropped()
        {
            var internalState = Scale(64.0);
            var data = new Dictionary<string, ScaleLearnerState>
            {
                [Key("G", "C", Hidden)] = Scale(90.0),
                [Key("G", "C", LockRaw)] = internalState,
            };

            Dictionary<string, ScaleLearnerState> migrated = InternalSourceKeyMigration.MigrateScale(data, LockRaw);

            Assert.Same(internalState, migrated[Key("G", "C", LockRaw)]);
            Assert.Single(migrated);
        }

        [Fact]
        public void An_internal_entry_without_evidence_counts_as_empty()
        {
            var data = new Dictionary<string, ScaleLearnerState>
            {
                [Key("G", "C", Hidden)] = Scale(70.0),
                [Key("G", "C", LockRaw)] = Scale(0.0),
            };

            Assert.Equal(70.0, InternalSourceKeyMigration.MigrateScale(data, LockRaw)[Key("G", "C", LockRaw)].ColdCeiling);
        }

        [Fact]
        public void Unrelated_keys_are_untouched_and_game_car_surface_are_preserved()
        {
            var shakeIt = Scale(55.0);
            var otherCarInternal = Scale(60.0);
            var data = new Dictionary<string, ScaleLearnerState>
            {
                [Key("G", "C", "ShakeIt")] = shakeIt,
                [Key("G", "C2", LockRaw)] = otherCarInternal,
                [Key("G", "C", Hidden, "Loose")] = Scale(66.0),
                [Key("H", "C", Hidden)] = Scale(77.0),
            };

            Dictionary<string, ScaleLearnerState> migrated = InternalSourceKeyMigration.MigrateScale(data, LockRaw);

            Assert.Same(shakeIt, migrated[Key("G", "C", "ShakeIt")]);
            Assert.Same(otherCarInternal, migrated[Key("G", "C2", LockRaw)]);
            Assert.Equal(66.0, migrated[Key("G", "C", LockRaw, "Loose")].ColdCeiling);
            Assert.Equal(77.0, migrated[Key("H", "C", LockRaw)].ColdCeiling);
            Assert.DoesNotContain(migrated.Keys, InternalSourceKeyMigration.IsHiddenKey);
            Assert.Equal(4, migrated.Count);
        }

        [Fact]
        public void Lock_and_slip_each_map_to_their_own_internal_identity()
        {
            var data = new Dictionary<string, ScaleLearnerState> { [Key("G", "C", Hidden)] = Scale(70.0) };

            Assert.True(InternalSourceKeyMigration.MigrateScale(data, LockRaw).ContainsKey(Key("G", "C", LockRaw)));
            Assert.True(InternalSourceKeyMigration.MigrateScale(data, SlipRaw).ContainsKey(Key("G", "C", SlipRaw)));
            Assert.NotEqual(LockRaw, SlipRaw);
        }

        [Fact]
        public void Anchor_entries_follow_the_same_rule()
        {
            var hidden = new LockAnchorState { S75 = 30.0, Hits75 = 3, S90 = 50.0, Hits90 = 2 };
            var kept = new LockAnchorState { PhysicalRatioLevel90 = 0.7, PhysicalRatioHits90 = 1 };
            var data = new Dictionary<string, LockAnchorState>
            {
                [Key("G", "C", Hidden)] = hidden,
                [Key("G", "C2", Hidden)] = new LockAnchorState { S90 = 44.0 },
                [Key("G", "C2", LockRaw)] = kept,
            };

            Dictionary<string, LockAnchorState> migrated = InternalSourceKeyMigration.MigrateAnchors(data, LockRaw);

            Assert.Same(hidden, migrated[Key("G", "C", LockRaw)]);
            Assert.Same(kept, migrated[Key("G", "C2", LockRaw)]);
            Assert.Equal(2, migrated.Count);
        }

        [Fact]
        public void Grip_entries_follow_the_same_rule()
        {
            var data = new Dictionary<string, GripLearnerState>
            {
                [Key("G", "C", Hidden, "Sealed")] = new GripLearnerState { PeakG = 1.9, Samples = 40 },
                [Key("G", "C", "Viper", "Sealed")] = new GripLearnerState { PeakG = 1.2, Samples = 10 },
            };

            Dictionary<string, GripLearnerState> migrated = InternalSourceKeyMigration.MigrateGrip(data, SlipRaw);

            Assert.Equal(40, migrated[Key("G", "C", SlipRaw, "Sealed")].Samples);
            Assert.Equal(10, migrated[Key("G", "C", "Viper", "Sealed")].Samples);
            Assert.Equal(2, migrated.Count);
        }

        [Fact]
        public void Null_and_empty_inputs_are_safe_and_the_input_is_not_modified()
        {
            Assert.Null(InternalSourceKeyMigration.MigrateScale(null, LockRaw));

            var data = new Dictionary<string, ScaleLearnerState> { [Key("G", "C", Hidden)] = Scale(70.0) };
            Dictionary<string, ScaleLearnerState> migrated = InternalSourceKeyMigration.MigrateScale(data, null);

            Assert.Empty(migrated);
            Assert.True(data.ContainsKey(Key("G", "C", Hidden)));
        }

        [Fact]
        public void The_engine_no_longer_writes_the_hidden_key()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            TestFrames.WarmLockWithCrossings(engine, 4.0, 70.0, gameId: "G", carId: "C", lockSourceIdentity: "ShakeIt");
            for (int i = 0; i < 200; i++)
                engine.Compute(TestFrames.BrakingSampleFor(3.5), QAdvanceFeedback.Core.Corners.Uniform(40.0),
                    QAdvanceFeedback.Core.Corners.Zero, "G", "C", lockSourceIdentity: "ShakeIt",
                    layer3RawLockWheels: QAdvanceFeedback.Core.Corners.Uniform(60.0));

            Assert.DoesNotContain(engine.LockScaleLearner.ExportAll().Keys, InternalSourceKeyMigration.IsHiddenKey);
            Assert.DoesNotContain(engine.LockAnchors.ExportAll().Keys, InternalSourceKeyMigration.IsHiddenKey);
            Assert.DoesNotContain(engine.LockLearners.ExportAll().Keys, InternalSourceKeyMigration.IsHiddenKey);
        }
    }
}

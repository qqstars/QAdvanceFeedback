using System;
using System.IO;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Runtime;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// The last Slip Angle key-data context (game, car, identity tier), persisted next to Lock/Slip's
    /// <c>LastKeyDataGame/Car</c> so that after a restart - SimHub on the menu, no game running - the Slip
    /// Angle tab and the ShakeIt panel form the EXACT persist key (owner, 2026-11-19; docs\full-mvvm-plan.md
    /// 3.5), and the context the data thread keeps for them.
    /// </summary>
    public class SlipAngleLastContextPersistenceTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-sactx-" + Guid.NewGuid() + ".json");
        private RuntimeStore _store;

        public void Dispose()
        {
            _store?.Dispose();
            if (File.Exists(_path)) File.Delete(_path);
        }

        // ---------------- RuntimeDocument / RuntimeCache / RuntimeStore ----------------

        [Fact]
        public void The_context_survives_a_restart()
        {
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            _store.SaveLastSlipAngleContext("acc", "car1", SourceNativeness.Fallback);
            _store.Flush();
            _store.Dispose();

            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            Assert.True(_store.TryLoadLastSlipAngleContext(out string game, out string car, out SourceNativeness tier));
            Assert.Equal("acc", game);
            Assert.Equal("car1", car);
            Assert.Equal(SourceNativeness.Fallback, tier);
        }

        [Fact]
        public void An_older_file_has_no_context()
        {
            File.WriteAllText(_path, "{ \"Version\": 12 }");
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            Assert.False(_store.TryLoadLastSlipAngleContext(out _, out _, out _));
        }

        [Fact]
        public void The_flush_snapshot_copies_the_context()
        {
            var cache = new RuntimeCache();
            cache.SaveLastSlipAngleContext("acc", "car1", SourceNativeness.Native);
            RuntimeDocument snapshot = cache.SnapshotIfDirty();
            Assert.NotNull(snapshot);
            Assert.Equal("acc", snapshot.LastSlipAngleGame);
            Assert.Equal("car1", snapshot.LastSlipAngleCar);
            Assert.Equal("Native", snapshot.LastSlipAngleTier);
        }

        [Fact]
        public void Saving_the_same_context_does_not_mark_the_cache_dirty()
        {
            var cache = new RuntimeCache();
            cache.SaveLastSlipAngleContext("acc", "car1", SourceNativeness.Native);
            Assert.NotNull(cache.SnapshotIfDirty());
            cache.SaveLastSlipAngleContext("acc", "car1", SourceNativeness.Native);
            Assert.Null(cache.SnapshotIfDirty());
            cache.SaveLastSlipAngleContext("acc", "car1", SourceNativeness.Fallback);
            Assert.NotNull(cache.SnapshotIfDirty());
        }

        [Fact]
        public void An_unreadable_tier_loads_as_unknown_and_a_null_game_is_never_saved()
        {
            var cache = new RuntimeCache();
            cache.SaveLastSlipAngleContext(null, "car1", SourceNativeness.Native);
            Assert.False(cache.TryLoadLastSlipAngleContext(out _, out _, out _));

            cache.Load(new RuntimeDocument { LastSlipAngleGame = "acc", LastSlipAngleCar = null, LastSlipAngleTier = "bogus" });
            Assert.True(cache.TryLoadLastSlipAngleContext(out string game, out string car, out SourceNativeness tier));
            Assert.Equal("acc", game);
            Assert.Equal(string.Empty, car);
            Assert.Equal(SourceNativeness.Unknown, tier);
        }

        // ---------------- SlipAngleKeyDataContext (what the plugin keeps for the UI) ----------------

        [Fact]
        public void First_launch_has_no_context()
        {
            var ctx = new SlipAngleKeyDataContext();
            Assert.Null(ctx.Current);
            Assert.Equal(SourceNativeness.Unknown, ctx.IdentityTier(SourceNativeness.Unknown));
        }

        [Fact]
        public void A_restored_context_reads_sealed_and_picks_the_saved_tier_until_a_game_runs()
        {
            var ctx = new SlipAngleKeyDataContext();
            ctx.Restore("acc", "car1", SourceNativeness.Fallback);

            Assert.Equal("acc", ctx.Current.GameId);
            Assert.Equal("car1", ctx.Current.CarId);
            Assert.Equal(SurfaceBucket.Sealed, ctx.Current.Blend.Active);
            Assert.Equal(1.0, ctx.Current.Blend.WSealed, 9);
            // On the menu the live tier is Unknown: the saved tier picks the identity (Fallback slot).
            Assert.Equal(SourceNativeness.Fallback, ctx.IdentityTier(SourceNativeness.Unknown));

            var blend = new SurfaceBlend(SurfaceBucket.Loose, 0.2, 0.8, 0.0, SurfaceBucketSource.Unknown);
            ctx.Observe("ams2", "car9", SourceNativeness.Native, blend);
            Assert.Equal("ams2", ctx.Current.GameId);
            Assert.Equal("car9", ctx.Current.CarId);
            Assert.Equal(SurfaceBucket.Loose, ctx.Current.Blend.Active);
            // Once a game ran this session, the live (in-session) tier rules - even Unknown.
            Assert.Equal(SourceNativeness.Unknown, ctx.IdentityTier(SourceNativeness.Unknown));
            Assert.Equal(SourceNativeness.Native, ctx.IdentityTier(SourceNativeness.Native));
        }

        [Fact]
        public void Observe_reports_a_change_of_game_car_or_tier_only()
        {
            var ctx = new SlipAngleKeyDataContext();
            SurfaceBlend s = SurfaceBlend.Only(SurfaceBucket.Sealed);
            Assert.True(ctx.Observe("acc", "car1", SourceNativeness.Native, s));
            Assert.False(ctx.Observe("acc", "car1", SourceNativeness.Native, SurfaceBlend.Only(SurfaceBucket.Loose)));
            Assert.True(ctx.Observe("acc", "car1", SourceNativeness.Fallback, s));
            Assert.True(ctx.Observe("acc", "car2", SourceNativeness.Fallback, s));
            Assert.False(ctx.Observe(null, "car2", SourceNativeness.Fallback, s));   // no game: ignored
            Assert.Equal("acc", ctx.Current.GameId);
        }

        [Fact]
        public void A_restored_context_with_an_exact_key_shows_its_window_end_to_end()
        {
            // Session 1: the key is learned and saved, the context saved.
            var learner = new KeyedSlipAngleLearner();
            string key = KeyedSlipAngleLearner.MakeKey("acc", "car1", SlipAngleFrameProcessor.FallbackIdentity, SlipAngleTarget.All, SurfaceBucket.Sealed);
            learner.Import(new System.Collections.Generic.Dictionary<string, SlipAngleKeyState>
            {
                [key] = new SlipAngleKeyState
                {
                    Learner = new SlipAngleLearnerState { PriorMin = 6.0, PriorBest = 9.0, PriorMax = 14.0 },
                    PublishedMin = 6.0, PublishedBest = 9.0, PublishedMax = 14.0, ColdTier = 1,
                },
            });
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            learner.Save(_store.SaveSlipAngleLearners);
            _store.SaveLastSlipAngleContext("acc", "car1", SourceNativeness.Fallback);
            _store.Flush();
            _store.Dispose();

            // Session 2 (restart, menu): learners imported, then the context restored.
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            var restored = new KeyedSlipAngleLearner();
            _store.LoadSlipAngleLearners(out var data);
            restored.Import(data);
            var ctx = new SlipAngleKeyDataContext();
            Assert.True(_store.TryLoadLastSlipAngleContext(out string g, out string c, out SourceNativeness t));
            ctx.Restore(g, c, t);

            string identity = Settings.SettingsHub.SlipAngleIdentity(null, ctx.IdentityTier(SourceNativeness.Unknown));
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, identity);
            SlipAngleWindow? w = SlipAngleKeyDataModel.LearnedWindow(restored, ctx.Current, identity);
            Assert.True(w.HasValue);
            Assert.Equal(6.0, w.Value.Min, 9);
            Assert.Equal(9.0, w.Value.Best, 9);
            Assert.Equal(14.0, w.Value.Max, 9);
            // The Native identity of the same context is another key: unknown.
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(restored, ctx.Current, SlipAngleFrameProcessor.NativeIdentity));
        }
    }
}

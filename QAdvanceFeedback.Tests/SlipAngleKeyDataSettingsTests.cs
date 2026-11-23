using Newtonsoft.Json;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class SlipAngleKeyDataSettingsTests
    {
        private const string Src = "raw-slip-angle";

        private static SlipAngleKeyDataSettings Manual(bool perGame = false)
        {
            var k = new SlipAngleKeyDataSettings();
            k.SetAutoGenerate(Src, false);
            k.SetPerGame(Src, perGame);
            return k;
        }

        [Fact]
        public void Defaults_are_auto_global()
        {
            var k = new SlipAngleKeyDataSettings();
            Assert.True(k.AutoGenerate);
            Assert.False(k.PerGame);
            Assert.False(k.TryGetSlot("g", Src, out _));
        }

        [Fact]
        public void Auto_is_read_only()
        {
            var k = new SlipAngleKeyDataSettings();
            Assert.False(k.TryCommitManual("g", Src, 5, 8, 10));
        }

        [Fact]
        public void Commit_rounds_to_tenth_and_round_trips()
        {
            var k = Manual();
            Assert.True(k.TryCommitManual("g", Src, 4.04, 7.96, 11.0));
            Assert.True(k.TryGetSlot("other", Src, out SlipAngleWindow w));
            Assert.Equal(4.0, w.Min, 6);
            Assert.Equal(8.0, w.Best, 6);
            Assert.Equal(11.0, w.Max, 6);
            Assert.True(k.IsSeeded("g", Src));

            var back = JsonConvert.DeserializeObject<SlipAngleKeyDataSettings>(JsonConvert.SerializeObject(k));
            Assert.True(back.TryGetSlot("g", Src, out SlipAngleWindow w2));
            Assert.Equal(11.0, w2.Max, 6);
            Assert.False(back.GetAutoGenerate(Src));
        }

        [Theory]
        [InlineData(5, 5, 10)]
        [InlineData(0, 8, 10)]
        [InlineData(9, 8, 10)]
        [InlineData(5, 8, 8.1)]
        [InlineData(5, 5.1, 10)]
        [InlineData(0.4, 8, 10)]
        [InlineData(5, 8, 45.5)]
        [InlineData(double.NaN, 8, 10)]
        public void Invalid_triples_rejected(double min, double best, double max)
        {
            var k = Manual();
            Assert.False(k.TryCommitManual("g", Src, min, best, max));
            Assert.False(k.TryGetSlot("g", Src, out _));
        }

        [Fact]
        public void Per_game_without_game_refused()
        {
            var k = Manual(perGame: true);
            Assert.False(k.TryCommitManual(null, Src, 5, 8, 10));
            Assert.False(k.TryCommitManual("", Src, 5, 8, 10));
            Assert.True(k.TryCommitManual("gA", Src, 5, 8, 10));
            Assert.False(k.TryGetSlot("gB", Src, out _));
            Assert.True(k.TryGetSlot("gA", Src, out _));
        }

        [Fact]
        public void Slots_are_keyed_by_source_and_clear_rearms_seed()
        {
            var k = Manual();
            Assert.True(k.TryCommitManual("g", Src, 5, 8, 10));
            Assert.False(k.TryGetSlot("g", "other-src", out _));
            k.ClearSlot("g", Src);
            Assert.False(k.TryGetSlot("g", Src, out _));
            Assert.False(k.IsSeeded("g", Src));
        }

        [Fact]
        public void Seed_writes_once()
        {
            var k = new SlipAngleKeyDataSettings();
            Assert.True(k.TrySeed("g", Src, new SlipAngleWindow(4, 7, 9)));
            Assert.True(k.IsSeeded("g", Src));
            Assert.False(k.TrySeed("g", Src, new SlipAngleWindow(6, 9, 12)));
            k.TryGetSlot("g", Src, out SlipAngleWindow w);
            Assert.Equal(9.0, w.Max, 6);
            Assert.False(new SlipAngleKeyDataSettings().TrySeed("g", Src, new SlipAngleWindow(5, 5, 10)));
        }

        [Fact]
        public void Manual_withheld_until_gate_ready()
        {
            var k = Manual();
            k.TryCommitManual("g", Src, 4, 7, 9);
            var learned = new SlipAngleWindow(6, 9, 12);
            var gate = new ManualOverrideGate();

            SlipAngleWindow r = k.Resolve("g", Src, learned, gate, "key", 1.0);
            Assert.Equal(12.0, r.Max, 6);   // no driving time yet

            for (int i = 0; i < 70; i++) gate.Observe("key", 0.5, true);
            Assert.Equal(12.0, k.Resolve("g", Src, learned, gate, "key", 0.5).Max, 6);   // maturity short
            Assert.Equal(9.0, k.Resolve("g", Src, learned, gate, "key", 0.95).Max, 6);

            k.SetAutoGenerate(Src, true);
            Assert.Equal(12.0, k.Resolve("g", Src, learned, gate, "key", 1.0).Max, 6);
        }

        [Fact]
        public void Manual_without_slot_uses_learned()
        {
            var k = Manual();
            var gate = new ManualOverrideGate();
            for (int i = 0; i < 70; i++) gate.Observe("key", 0.5, true);
            Assert.Equal(12.0, k.Resolve("g", Src, new SlipAngleWindow(6, 9, 12), gate, "key", 1.0).Max, 6);
        }

        [Fact]
        public void Sources_have_independent_flags()
        {
            var k = new SlipAngleKeyDataSettings();
            k.SetAutoGenerate("A", false);
            k.SetPerGame("A", true);
            Assert.False(k.GetAutoGenerate("A"));
            Assert.True(k.GetPerGame("A"));
            Assert.True(k.GetAutoGenerate("B"));
            Assert.False(k.GetPerGame("B"));

            k.SetAutoGenerate("B", false);
            Assert.False(k.TryCommitManual(null, "A", 5, 8, 10));   // per-game, no game
            Assert.True(k.TryCommitManual(null, "B", 5, 8, 10));    // global
            Assert.True(k.TryCommitManual("g", "A", 6, 9, 12));
            Assert.True(k.TryGetSlot("x", "B", out SlipAngleWindow b));
            Assert.Equal(10.0, b.Max, 6);
            Assert.False(k.TryGetSlot("x", "A", out _));
        }

        [Fact]
        public void Missing_source_flag_falls_back_to_global_pair()
        {
            var k = new SlipAngleKeyDataSettings { AutoGenerate = false, PerGame = true };
            Assert.False(k.GetAutoGenerate("never-seen"));
            Assert.True(k.GetPerGame("never-seen"));
            k.SetAutoGenerate("fresh", true);   // seeded from the fallback pair
            Assert.True(k.GetPerGame("fresh"));
        }

        [Fact]
        public void Old_json_without_source_flags_loads()
        {
            var k = JsonConvert.DeserializeObject<SlipAngleKeyDataSettings>("{\"AutoGenerate\":false,\"PerGame\":false}");
            Assert.False(k.GetAutoGenerate(Src));
            Assert.True(k.TryCommitManual("g", Src, 5, 8, 10));
        }
        [Fact]
        public void IsDefault_tracks_flags_and_stored_windows_and_ResetSource_restores_them()
        {
            var k = new SlipAngleKeyDataSettings();
            Assert.True(k.IsDefault(Src));

            k.SetAutoGenerate(Src, false);
            Assert.False(k.IsDefault(Src));
            Assert.True(k.TryCommitManual("g", Src, 5, 8, 10));   // the shipped window counts as default
            k.SetAutoGenerate(Src, true);
            Assert.True(k.IsDefault(Src));

            k.SetAutoGenerate(Src, false);
            k.SetPerGame(Src, true);
            Assert.True(k.TryCommitManual("g", Src, 4, 7, 9));
            k.SetAutoGenerate("other-source", false);
            k.SetPerGame("other-source", true);
            Assert.True(k.TryCommitManual("g", "other-source", 4, 7, 9));
            Assert.False(k.IsDefault(Src));

            k.ResetSource(Src);
            Assert.True(k.IsDefault(Src));
            Assert.True(k.GetAutoGenerate(Src));
            Assert.False(k.GetPerGame(Src));
            Assert.False(k.IsSeeded("g", Src));
            // Another source is untouched.
            Assert.True(k.TryGetSlot("g", "other-source", out SlipAngleWindow w));
            Assert.Equal(9.0, w.Max);
        }
    }
}

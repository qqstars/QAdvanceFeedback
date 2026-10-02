using System;
using System.IO;
using System.Linq;
using QAdvanceFeedback;
using QAdvanceFeedback.Core.Viper;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE VIPER SUPPORTED-GAME LIST and its own file, <c>QAdvanceFeedback.Viper.json</c>.
    /// <para/>
    /// The list is hardcoded because it is not data in the Viper assembly - it is a chain of string
    /// comparisons inside <c>DataUpdate</c>, invisible to reflection - and the file exists so a driver
    /// can correct it without waiting for a build of this plugin. See
    /// <see cref="ViperSupportedGames"/> for the full reasoning, including why IL scraping was checked
    /// and rejected.
    /// </summary>
    public class ViperSupportedGamesTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public ViperSupportedGamesTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "qaf-viper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Path_() => Path.Combine(_dir, ViperStore.FileName);

        [Fact]
        public void The_shipped_list_matches_the_decompiled_plugin()
        {
            // Transcribed from v1.5.2's own guard. Pinned so a careless edit cannot quietly drop a title
            // - a missing entry reads as "unsupported" and silently sends that game to Raw.
            Assert.Equal(23, ViperSupportedGames.Shipped.Count);
            Assert.Equal("1.5.2", ViperSupportedGames.ShippedFromPluginVersion);

            foreach (string expected in new[]
                     {
                         "PCars", "PCars2", "Automobilista2", "RRRE",
                         "RFactor2", "RFactor2Spectator", "LMU",
                         "AssettoCorsa", "AssettoCorsaCompetizione", "AssettoCorsaEVO", "AssettoCorsaRally",
                         "GranTurismo7",
                         "F12018", "F12019", "F12020", "F12021", "F12022", "F12023", "F12024", "F12025",
                         "EAWRC23", "ProjectMotorRacing", "CodemastersDirtRally2",
                     })
                Assert.Contains(expected, ViperSupportedGames.Shipped);

            // No duplicates, nothing blank - this list seeds a file a driver will read.
            Assert.Equal(ViperSupportedGames.Shipped.Count,
                ViperSupportedGames.Shipped.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(ViperSupportedGames.Shipped, string.IsNullOrWhiteSpace);
        }

        [Fact]
        public void The_games_from_the_owners_own_logs_resolve_correctly()
        {
            // F12025 is what every Viper capture in this project ran on, and FH6 is the one the owner
            // tried the Viper source on and got nothing from - the plugin returns before computing, so
            // all four properties sat at their declared 0 all session with no warning.
            Assert.True(ViperSupportedGames.IsShipped("F12025"));
            Assert.False(ViperSupportedGames.IsShipped("FH6"));
        }

        [Fact]
        public void Matching_is_case_insensitive_and_a_missing_game_is_never_supported()
        {
            Assert.True(ViperSupportedGames.IsSupported("f12025", ViperSupportedGames.Shipped));
            Assert.True(ViperSupportedGames.IsSupported("F12025", ViperSupportedGames.Shipped));

            // With no game running there is nothing for the plugin to compute, so "supported" would be a
            // claim nothing has tested.
            Assert.False(ViperSupportedGames.IsSupported(null, ViperSupportedGames.Shipped));
            Assert.False(ViperSupportedGames.IsSupported("   ", ViperSupportedGames.Shipped));

            // A NULL FILE LIST NO LONGER MEANS UNSUPPORTED for a shipped title (owner, 2026-10-01) -
            // the shipped list is OR-ed in, so a missing or emptied document cannot un-support one.
            Assert.True(ViperSupportedGames.IsSupported("F12025", null));
            // An unknown title is still unsupported with no file, which is the half that must not move.
            Assert.False(ViperSupportedGames.IsSupported("FH6", null));
        }

        [Fact]
        public void Adding_and_removing_a_game_leaves_the_original_untouched()
        {
            // The settings page edits a working copy and only commits on Apply, so these must not
            // mutate in place.
            ViperDocument shipped = ViperSupportedGames.CreateShippedDocument();
            int before = shipped.SupportedGames.Count;

            ViperDocument added = ViperSupportedGames.WithGame(shipped, "FH6");
            Assert.Equal(before, shipped.SupportedGames.Count);
            Assert.Equal(before + 1, added.SupportedGames.Count);
            Assert.True(ViperSupportedGames.IsSupported("FH6", added.SupportedGames));

            ViperDocument removed = ViperSupportedGames.WithoutGame(added, "fh6");   // case-insensitive
            Assert.False(ViperSupportedGames.IsSupported("FH6", removed.SupportedGames));
            Assert.Equal(before, removed.SupportedGames.Count);
        }

        [Fact]
        public void A_no_op_edit_returns_the_same_instance_so_nothing_needs_saving()
        {
            // Reference equality is how the caller decides whether Apply has anything to write.
            ViperDocument doc = ViperSupportedGames.CreateShippedDocument();
            Assert.Same(doc, ViperSupportedGames.WithGame(doc, "F12025"));       // already there
            Assert.Same(doc, ViperSupportedGames.WithoutGame(doc, "FH6"));       // was never there
        }

        [Fact]
        public void A_missing_file_yields_the_shipped_list_and_writes_nothing()
        {
            // The normal first run. Writing is reserved for Apply, so merely reading must not create
            // the file - otherwise a driver who never uses Viper still finds one.
            ViperDocument loaded = ViperStore.Load(Path_());

            Assert.Equal(ViperSupportedGames.Shipped.Count, loaded.SupportedGames.Count);
            Assert.False(File.Exists(Path_()), "loading must not create the file");
        }

        [Fact]
        public void A_saved_file_round_trips_and_survives_a_reload()
        {
            ViperDocument doc = ViperSupportedGames.WithGame(ViperSupportedGames.CreateShippedDocument(), "FH6");
            Assert.True(ViperStore.Save(Path_(), doc));

            ViperDocument reloaded = ViperStore.Load(Path_());
            Assert.Equal(ViperDocument.CurrentVersion, reloaded.Version);
            Assert.True(ViperSupportedGames.IsSupported("FH6", reloaded.SupportedGames));
            Assert.True(ViperSupportedGames.IsSupported("F12025", reloaded.SupportedGames));

            _out.WriteLine(File.ReadAllText(Path_()).Substring(0, 80).Replace("\r", "").Replace("\n", " "));
        }

        [Fact]
        public void A_shipped_game_stays_supported_even_if_the_file_drops_it()
        {
            // CONTRACT REVERSED BY THE OWNER, 2026-10-01: "when in the F125 game, which is the internal
            // supported game, it will still shows the Remove from Support List button which is
            // INCORRECT. ONLY the game not in the internal support lists will display the button."
            //
            // This test previously asserted the opposite - that the file wins once written, so a driver
            // could "correct us". In practice that was a trap rather than a correction: removing F12025
            // dropped the channel to Raw, and with the button now hidden for shipped titles there would
            // be no way back. The shipped list is this project's record of what viper4gh's plugin
            // actually handles, so it is OR-ed in and the file only ever ADDS to it. That also repairs a
            // document already written without a shipped title, with no migration.
            ViperDocument doc = ViperSupportedGames.WithoutGame(ViperSupportedGames.CreateShippedDocument(), "F12025");
            ViperStore.Save(Path_(), doc);

            ViperDocument reloaded = ViperStore.Load(Path_());
            Assert.True(ViperSupportedGames.IsSupported("F12025", reloaded.SupportedGames));
            Assert.True(ViperSupportedGames.IsShipped("F12025"), "our own list is unchanged by a file edit");
        }

        [Fact]
        public void A_corrupt_file_degrades_to_the_shipped_list_rather_than_throwing()
        {
            File.WriteAllText(Path_(), "{ this is not json");
            ViperDocument loaded = ViperStore.Load(Path_());
            Assert.Equal(ViperSupportedGames.Shipped.Count, loaded.SupportedGames.Count);
        }

        [Fact]
        public void A_hand_edited_file_is_sanitised_on_read()
        {
            // Blanks, duplicates and a null entry must not reach the caller - and must not be silently
            // rewritten either, since only Apply writes.
            File.WriteAllText(Path_(),
                "{ \"Version\": 1, \"SupportedGames\": [ \"F12025\", \"f12025\", \"\", null, \"  FH6  \" ] }");

            ViperDocument loaded = ViperStore.Load(Path_());
            Assert.Equal(2, loaded.SupportedGames.Count);
            Assert.True(ViperSupportedGames.IsSupported("F12025", loaded.SupportedGames));
            Assert.True(ViperSupportedGames.IsSupported("FH6", loaded.SupportedGames));
        }

        [Fact]
        public void The_file_carries_a_version_and_holds_no_key_data_points()
        {
            // The owner's explicit boundary: this file describes somebody else's plugin. Key data points
            // are ours and belong in the config file.
            ViperStore.Save(Path_(), ViperSupportedGames.CreateShippedDocument());
            string json = File.ReadAllText(Path_());

            Assert.Contains("\"Version\"", json);
            Assert.DoesNotContain("SMax", json);
            Assert.DoesNotContain("S90", json);
            Assert.DoesNotContain("KeyData", json);
        }
    }
}

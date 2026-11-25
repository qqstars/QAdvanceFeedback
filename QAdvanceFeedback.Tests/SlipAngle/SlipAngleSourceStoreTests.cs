using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// QAdvanceFeedback.SlipAngleSource.json (owner, 2026-11-23): the compiled copy, the PluginsData\Common file it
    /// seeds, and the settings page's save. Each test uses its own temp file and keeps the catalogs it loads to itself
    /// (<c>use</c>), so the shared reader table the other tests read is never swapped.
    /// </summary>
    public sealed class SlipAngleSourceStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "qaf-sas-" + Guid.NewGuid().ToString("N"));
        private SlipAngleSourceCatalog _used;

        public SlipAngleSourceStoreTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string FilePath => Path.Combine(_dir, SlipAngleSourceStore.FileName);

        private SlipAngleSourceStore NewStore()
        {
            var store = new SlipAngleSourceStore(FilePath, use: c => _used = c);
            store.Load();
            return store;
        }

        private static string[] Paths(string stem) =>
            new[] { "FL", "FR", "RL", "RR" }.Select(w => "DataCorePlugin.GameRawData." + stem + w).ToArray();

        // ---------------- the compiled copy ----------------

        [Fact]
        public void The_compiled_copy_is_embedded_and_valid()
        {
            Assert.False(string.IsNullOrWhiteSpace(SlipAngleSourceCompiled.Text));
            Assert.NotSame(SlipAngleSourceCatalog.Empty, SlipAngleSourceCompiled.Catalog);
            Assert.Equal(13, SlipAngleSourceCompiled.Catalog.NativeGameIds.Count);   // 25 games, shared rows
            Assert.Equal(19, SlipAngleSourceCompiled.Catalog.Games.Count);           // + surface-only rows and IRacing's flag row
        }

        [Fact]
        public void The_solution_root_json_is_the_one_compiled_in()
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "QAdvanceFeedback.sln"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);
            Assert.Equal(File.ReadAllText(Path.Combine(root, SlipAngleSourceCompiled.ResourceName)), SlipAngleSourceCompiled.Text);
        }

        [Theory]
        [InlineData("LFS", SlipAngleUnit.Radians)]
        [InlineData("Drift21", SlipAngleUnit.Radians)]
        [InlineData("RFactor2", SlipAngleUnit.Radians)]
        [InlineData("LMU", SlipAngleUnit.Radians)]
        [InlineData("NASCARHeat5", SlipAngleUnit.Radians)]
        [InlineData("KartKraft", SlipAngleUnit.Degrees)]
        [InlineData("FH6", SlipAngleUnit.Ratio1)]
        [InlineData("FM8", SlipAngleUnit.Ratio1)]
        [InlineData("AssettoCorsaEVO", SlipAngleUnit.Unverified)]   // no unit: detected while driving
        [InlineData("ACC", SlipAngleUnit.Unverified)]
        [InlineData("F12025", SlipAngleUnit.Unverified)]
        public void Units_are_the_ones_the_code_stated_and_unknown_ones_are_detected(string game, SlipAngleUnit unit)
        {
            Assert.True(SlipAngleSourceCompiled.Catalog.TryGetNative(game, out SlipAngleRawSourceEntry e));
            Assert.Equal(unit, e.Unit);
        }

        // ---------------- the file in PluginsData\Common ----------------

        [Fact]
        public void A_missing_file_is_created_from_the_compiled_copy_and_used()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.True(File.Exists(FilePath));
            Assert.Equal(SlipAngleSourceCompiled.Text, File.ReadAllText(FilePath));
            Assert.False(store.IsBroken);
            Assert.True(_used.TryGetNative("AssettoCorsaEVO", out _));
        }

        [Fact]
        public void The_file_is_the_only_definition_a_game_taken_out_of_it_has_no_source()
        {
            JObject doc = JObject.Parse(SlipAngleSourceCompiled.Text);
            ((JArray)doc["games"]).Where(g => (string)g["id"] == "ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO").ToList().ForEach(g => g["id"] = "ACC|AssettoCorsaCompetizione|AssettoCorsaRally");
            File.WriteAllText(FilePath, doc.ToString());
            NewStore();
            Assert.False(_used.TryGetNative("AssettoCorsaEVO", out _));
            Assert.True(_used.TryGetNative("ACC", out _));
        }

        [Fact]
        public void A_broken_file_is_never_overwritten_the_compiled_copy_is_used_and_saving_is_refused()
        {
            File.WriteAllText(FilePath, "{ \"games\": [ { \"id\": \"X\", \"slipAngle\": [\"a\"] } ] }");
            string before = File.ReadAllText(FilePath);
            SlipAngleSourceStore store = NewStore();
            Assert.True(store.IsBroken);
            Assert.Contains("four property names", store.BrokenReason);
            Assert.Same(SlipAngleSourceCompiled.Catalog, _used);
            Assert.Equal(SlipAngleSourceSaveResult.Unavailable, store.Save("MyGame", Paths("a")));
            Assert.Equal(before, File.ReadAllText(FilePath));
            Assert.False(store.CanRestore("AssettoCorsaEVO"));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{ \"games\": [ { \"slipAngle\": [\"a\",\"b\",\"c\",\"d\"] } ] }")]                          // no id
        [InlineData("{ \"games\": [ { \"id\": \"A\", \"slipAngle\": [\"a\",\"b\",\"c\",\"d\"], \"unit\": \"furlongs\" } ] }")]
        [InlineData("{ \"games\": [ { \"id\": \"A\", \"slipAngle\": [\"a\",\"b\",\"c\",\"d\"], \"transform\": \"atan2PatchVelocity\" } ] }")]   // no longitudinal
        [InlineData("{ \"games\": [ { \"id\": \"A\", \"slipAngle\": [\"a\",\"b\",\"c\",\"d\"] }, { \"id\": \"a\", \"surface\": [\"a\",\"b\",\"c\",\"d\"] } ] }")]   // twice
        [InlineData("{ \"games\": [ { \"id\": \"A*\" } ] }")]                                                            // a pattern with nothing
        public void Invalid_files_are_refused_whole(string json)
        {
            File.WriteAllText(FilePath, json);
            Assert.True(NewStore().IsBroken);
        }

        [Fact]
        public void A_hand_edit_is_picked_up_on_reload_and_a_deleted_file_is_created_again()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.False(store.ReloadIfChanged());
            var doc = SlipAngleSourceDocument.FromJson(File.ReadAllText(FilePath));
            doc.Games.Add(new SlipAngleSourceGame { Id = "NewSim", SlipAngle = Paths("sa").ToList(), Unit = "degrees" });
            File.WriteAllText(FilePath, doc.ToJson() + " ");
            Assert.True(store.ReloadIfChanged());
            Assert.True(_used.TryGetNative("newsim", out SlipAngleRawSourceEntry e));
            Assert.Equal(SlipAngleUnit.Degrees, e.Unit);

            File.Delete(FilePath);
            Assert.True(store.ReloadIfChanged());
            Assert.True(File.Exists(FilePath));
            Assert.False(_used.TryGetNative("NewSim", out _));
        }

        // ---------------- the page's Apply ----------------

        [Fact]
        public void Four_paths_for_a_new_game_add_a_plain_row_with_no_unit()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("NewSim", Paths("sa")));
            Assert.True(_used.TryGetNative("NewSim", out SlipAngleRawSourceEntry e));
            Assert.Equal(Paths("sa"), e.PropertyPaths);
            Assert.Equal(SlipAngleUnit.Unverified, e.Unit);   // Auto-detect
            Assert.Equal(SlipAngleTransform.Direct, e.Transform);
            Assert.Contains("NewSim", File.ReadAllText(FilePath));
            Assert.False(store.CanRestore("NewSim"));          // not a shipped game
            Assert.Equal(SlipAngleSourceSaveResult.Unchanged, store.Save("NewSim", Paths("sa")));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void One_to_three_paths_are_refused(int filled)
        {
            SlipAngleSourceStore store = NewStore();
            string before = File.ReadAllText(FilePath);
            string[] p = Paths("sa").Select((x, i) => i < filled ? x : "").ToArray();
            Assert.Equal(SlipAngleSourceSaveResult.Incomplete, store.Save("NewSim", p));
            Assert.Equal(before, File.ReadAllText(FilePath));
        }

        [Fact]
        public void Clearing_all_four_removes_the_source_and_keeps_surface_paths()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.Equal(SlipAngleSourceSaveResult.Removed, store.Save("AssettoCorsaEVO", new[] { "", "", "", "" }));
            Assert.False(_used.TryGetNative("AssettoCorsaEVO", out _));
            Assert.Null(store.Current("AssettoCorsaEVO"));

            Assert.Equal(SlipAngleSourceSaveResult.Removed, store.Save("F12025", new string[4]));
            Assert.False(_used.TryGetNative("F12025", out _));
            Assert.NotNull(_used.SurfacePaths("F12025"));     // the surface stays
        }

        [Fact]
        public void Restore_default_is_offered_only_for_a_changed_shipped_game_and_puts_the_shipped_row_back()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.False(store.CanRestore("AssettoCorsaEVO"));   // untouched
            Assert.False(store.CanRestore("GranTurismo7"));      // shipped, but surface only
            Assert.False(store.CanRestore("NoSuchGame"));

            store.Save("AssettoCorsaEVO", Paths("mine"));
            Assert.True(store.CanRestore("AssettoCorsaEVO"));
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("AssettoCorsaEVO", (string[])null, restoreCompiled: true));
            Assert.False(store.CanRestore("AssettoCorsaEVO"));
            Assert.True(SlipAngleSourceGame.SameSettings(store.Compiled("AssettoCorsaEVO"), store.Current("AssettoCorsaEVO")));
            Assert.Equal("ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO", store.Current("AssettoCorsaEVO").Id);   // merged back

            store.Save("AssettoCorsaEVO", new string[4]);        // removed is "changed" too
            Assert.True(store.CanRestore("AssettoCorsaEVO"));
        }

        [Fact]
        public void A_save_keeps_hand_edits_and_unknown_fields_and_changes_only_that_game_of_a_shared_row()
        {
            NewStore();
            JObject doc = JObject.Parse(File.ReadAllText(FilePath));
            doc["myNote"] = "kept";
            ((JArray)doc["games"]).First(g => (string)g["id"] == "ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO")["customField"] = 42;
            File.WriteAllText(FilePath, doc.ToString());
            SlipAngleSourceStore store = NewStore();

            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("AssettoCorsaCompetizione", Paths("acc")));
            JObject after = JObject.Parse(File.ReadAllText(FilePath));
            Assert.Equal("kept", (string)after["myNote"]);
            JToken acc = ((JArray)after["games"]).Single(g => (string)g["id"] == "ACC|AssettoCorsaRally|AssettoCorsaEVO");   // the others keep the row
            Assert.Equal(42, (int)acc["customField"]);
            Assert.Equal("DataCorePlugin.GameRawData.Physics.slipAngle01", (string)acc["slipAngle"][0]);
            JToken mine = ((JArray)after["games"]).Single(g => (string)g["id"] == "AssettoCorsaCompetizione");
            Assert.Equal(Paths("acc"), mine["slipAngle"].ToObject<string[]>());
        }

        // ---------------- ids: Game1|Game2, wildcards, regex: ----------------

        [Theory]
        [InlineData("FM?|FH?", "FH6", true)]
        [InlineData("FM?|FH?", "fh7", true)]
        [InlineData("FM?|FH?", "FM8", true)]
        [InlineData("FM?|FH?", "FH10", false)]
        [InlineData("FM?|FH?", "FH", false)]
        [InlineData("PCars*", "PCars3", true)]
        [InlineData("PCars*", "PCars", true)]
        [InlineData(@"regex:^F120(2[3-9]|[3-9]\d)[A-Za-z]*$", "F12023", true)]
        [InlineData(@"regex:^F120(2[3-9]|[3-9]\d)[A-Za-z]*$", "F12026SeasonPack", true)]
        [InlineData(@"regex:^F120(2[3-9]|[3-9]\d)[A-Za-z]*$", "F12022", false)]
        [InlineData(@"regex:^F120(2[3-9]|[3-9]\d)[A-Za-z]*$", "XF12023", false)]
        [InlineData("regex:A|B", "B", true)]                // the regex runs to the end of the id, | included
        [InlineData("regex:A|B", "AB", false)]              // and must match the whole game id
        [InlineData(@"Game1|regex:^Sim\d$", "Game1", true)]
        [InlineData(@"Game1|regex:^Sim\d$", "Sim4", true)]
        [InlineData("Game1|Game2", "game2", true)]
        [InlineData("Game1|Game2", "Game", false)]
        public void An_id_matches_by_name_wildcard_or_regex(string id, string game, bool matches)
            => Assert.Equal(matches, SlipAngleGameIdPattern.Parse(id).Matches(game));

        [Theory]
        [InlineData("A||B")]
        [InlineData("regex:")]
        [InlineData("regex:(unclosed")]
        public void A_bad_id_makes_the_file_invalid(string id)
        {
            var doc = new SlipAngleSourceDocument();
            doc.Games.Add(new SlipAngleSourceGame { Id = id, SlipAngle = Paths("x").ToList() });
            File.WriteAllText(FilePath, doc.ToJson());
            Assert.True(NewStore().IsBroken);
        }

        [Fact]
        public void A_game_named_by_two_rows_makes_the_file_invalid()
        {
            var doc = new SlipAngleSourceDocument();
            doc.Games.Add(new SlipAngleSourceGame { Id = "A|B", SlipAngle = Paths("x").ToList() });
            doc.Games.Add(new SlipAngleSourceGame { Id = "b", SlipAngle = Paths("y").ToList() });
            File.WriteAllText(FilePath, doc.ToJson());
            Assert.True(NewStore().IsBroken);
        }

        [Fact]
        public void A_new_game_with_the_same_source_joins_that_row()
        {
            SlipAngleSourceStore store = NewStore();
            string[] evo = store.Current("AssettoCorsaEVO").SlipAngle.ToArray();
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("NewSim", evo));
            Assert.Equal("ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO|NewSim", store.Current("NewSim").Id);
            Assert.Single(SlipAngleSourceDocument.FromJson(File.ReadAllText(FilePath)).Games, g => g.Id.Contains("NewSim"));
        }

        [Fact]
        public void Changing_a_game_a_wildcard_covers_adds_an_exact_row_and_restore_names_it_in_the_pattern_row()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("FH6", Paths("fh6")));
            Assert.Equal("FH6", store.Current("FH6").Id);
            Assert.Equal("ratio1", store.Current("FH6").Unit);          // keeps the unit of the row it had
            Assert.Equal("FM?|FH?", store.Current("FH5").Id);           // the others keep the pattern
            Assert.True(store.CanRestore("FH6"));

            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("FH6", (string[])null, restoreCompiled: true));
            // THE GAME IS ALWAYS NAMED (owner, 2026-11-24): it joins the row with the same settings, even though the
            // pattern of that row already covers it.
            Assert.Equal("FM?|FH?|FH6", store.Current("FH6").Id);
            Assert.DoesNotContain(SlipAngleSourceDocument.FromJson(File.ReadAllText(FilePath)).Games, g => g.Id == "FH6");
            Assert.False(store.CanRestore("FH6"));
        }

        [Fact]
        public void Clearing_a_game_a_wildcard_covers_writes_an_exact_empty_row_and_a_second_one_joins_it()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.Equal(SlipAngleSourceSaveResult.Removed, store.Save("FH6", new string[4]));
            Assert.False(_used.TryGetNative("FH6", out _));
            Assert.True(_used.TryGetNative("FH5", out _));
            Assert.True(store.Current("FH6").IsEmpty);
            Assert.True(store.CanRestore("FH6"));

            Assert.Equal(SlipAngleSourceSaveResult.Removed, store.Save("FH5", new string[4]));
            Assert.Equal("FH6|FH5", store.Current("FH5").Id);
            Assert.True(_used.TryGetNative("FH4", out _));
        }

        [Fact]
        public void Editing_a_computed_row_on_the_page_makes_it_a_plain_read()
        {
            SlipAngleSourceStore store = NewStore();
            store.Save("RFactor2", Paths("rf"));
            Assert.True(_used.TryGetNative("RFactor2", out SlipAngleRawSourceEntry e));
            Assert.Equal(SlipAngleTransform.Direct, e.Transform);
            Assert.Null(e.LongitudinalPaths);
            Assert.True(store.CanRestore("RFactor2"));
        }

        // ---------------- match levels (owner, 2026-11-24) ----------------

        [Theory]
        [InlineData("F12024", "F12024", SlipAngleGameIdPattern.LevelSingleName)]
        [InlineData("F12019|F12024", "F12024", SlipAngleGameIdPattern.LevelListedName)]
        [InlineData("F12024|regex:x", "F12024", SlipAngleGameIdPattern.LevelListedName)]
        [InlineData("F12024|F12?|F1*", "F12024", SlipAngleGameIdPattern.LevelListedName)]
        [InlineData("F1202?", "F12024", SlipAngleGameIdPattern.LevelQuestionMark)]
        [InlineData("F1*", "F12024", SlipAngleGameIdPattern.LevelStar)]
        [InlineData("F1?0*", "F12024", SlipAngleGameIdPattern.LevelStar)]   // * and ? together: a star wildcard
        [InlineData("F1202?|F1*", "F12024", SlipAngleGameIdPattern.LevelQuestionMark)]   // the best term counts
        [InlineData("regex:F1.*", "F12024", SlipAngleGameIdPattern.LevelRegex)]
        [InlineData("MySim|F1202?", "F12024", SlipAngleGameIdPattern.LevelQuestionMark)]
        [InlineData("MySim|F1202?", "MySim", SlipAngleGameIdPattern.LevelListedName)]
        [InlineData("F1202?", "F12030", 0)]
        [InlineData("F12024", "", 0)]
        public void Each_term_matches_at_its_level(string id, string game, int level)
            => Assert.Equal(level, SlipAngleGameIdPattern.Parse(id).Level(game));

        [Theory]
        [InlineData("F12024|F1202?", "F12024", SlipAngleGameIdPattern.LevelQuestionMark)]   // the exact name is ignored
        [InlineData("F12024|F1*", "F12024", SlipAngleGameIdPattern.LevelStar)]
        [InlineData("F12024|regex:F1.*", "F12024", SlipAngleGameIdPattern.LevelRegex)]
        [InlineData("F12024", "F12024", 0)]
        [InlineData("F1202?", "X", 0)]
        public void The_pattern_level_ignores_exact_names(string id, string game, int level)
            => Assert.Equal(level, SlipAngleGameIdPattern.Parse(id).PatternLevel(game));

        private static SlipAngleSourceDocument Doc(params (string id, string stem)[] rows)
        {
            var doc = new SlipAngleSourceDocument();
            foreach (var (id, stem) in rows) doc.Games.Add(new SlipAngleSourceGame { Id = id, SlipAngle = Paths(stem).ToList() });
            return doc;
        }

        [Theory]
        [InlineData("F1*", "F1202?", "b")]          // ? beats *, even later in the file
        [InlineData("F1202?", "F1*", "a")]
        [InlineData("regex:F1.*", "F1*", "b")]      // * beats a regex
        [InlineData("F1202?", "F120?4", "a")]       // the same level: file order
        [InlineData("F1*", "F12024|F1*X", "b")]     // a name in a list beats any pattern
        public void A_game_gets_the_row_matching_it_at_the_best_level_then_file_order(string first, string second, string winner)
        {
            SlipAngleSourceDocument doc = Doc((first, "a"), (second, "b"));
            Assert.True(SlipAngleSourceCatalog.FromDocument(doc).TryGetNative("F12024", out SlipAngleRawSourceEntry e));
            Assert.Equal(Paths(winner), e.PropertyPaths);
            Assert.Equal(winner == "a" ? 0 : 1, SlipAngleSourceCatalog.ResolveIndex(doc.Games, "F12024"));   // the save path agrees
        }

        [Fact]
        public void A_question_mark_in_a_mixed_id_acts_at_its_own_level()
        {
            SlipAngleSourceDocument doc = Doc(("F1*", "a"), ("MySim|F1202?", "b"));
            SlipAngleSourceCatalog c = SlipAngleSourceCatalog.FromDocument(doc);
            Assert.True(c.TryGetNative("F12024", out SlipAngleRawSourceEntry e));
            Assert.Equal(Paths("b"), e.PropertyPaths);
            Assert.True(c.TryGetNative("MySim", out e));
            Assert.Equal(Paths("b"), e.PropertyPaths);
            Assert.True(c.TryGetNative("F15", out e));
            Assert.Equal(Paths("a"), e.PropertyPaths);
        }

        [Fact]
        public void Saving_follows_the_match_levels()
        {
            File.WriteAllText(FilePath, Doc(("F1*", "a"), ("F1202?", "b")).ToJson());
            SlipAngleSourceStore store = NewStore();
            Assert.Equal("F1202?", store.Current("F12024").Id);
            Assert.Equal(SlipAngleSourceSaveResult.Unchanged, store.Save("F12024", Paths("b")));   // its row has it already
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("F12024", Paths("a")));
            Assert.Equal("F1*|F12024", store.Current("F12024").Id);                                // named in the row with those settings
            Assert.Equal("F1202?", store.Current("F12025").Id);
        }

        // ---------------- saving always names the game (owner, 2026-11-24) ----------------

        [Fact]
        public void A_game_split_out_of_a_shared_pattern_row_and_put_back_is_named_in_it_again()
        {
            File.WriteAllText(FilePath, Doc(("F12024|F1202?|F1203?", "s")).ToJson());
            SlipAngleSourceStore store = NewStore();

            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("F12024", Paths("t")));
            var games = SlipAngleSourceDocument.FromJson(File.ReadAllText(FilePath)).Games;
            Assert.Equal(new[] { "F1202?|F1203?", "F12024" }, games.Select(g => g.Id));
            Assert.Equal(Paths("s"), games[0].SlipAngle);
            Assert.Equal(Paths("t"), games[1].SlipAngle);

            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("F12024", Paths("s")));
            games = SlipAngleSourceDocument.FromJson(File.ReadAllText(FilePath)).Games;
            Assert.Equal(new[] { "F1202?|F1203?|F12024" }, games.Select(g => g.Id));
            Assert.Equal(Paths("s"), games[0].SlipAngle);
        }

        [Fact]
        public void An_empty_source_for_a_game_no_pattern_covers_writes_nothing()
        {
            File.WriteAllText(FilePath, Doc(("MySim", "s")).ToJson());
            SlipAngleSourceStore store = NewStore();
            Assert.Equal(SlipAngleSourceSaveResult.Removed, store.Save("MySim", new string[4]));
            Assert.Empty(SlipAngleSourceDocument.FromJson(File.ReadAllText(FilePath)).Games);   // no row means no source
            Assert.Null(store.Current("MySim"));
        }

        // ---------------- units in the file (owner, 2026-11-24) ----------------

        [Fact]
        public void A_velocity_row_is_always_radians_and_its_unit_field_is_ignored()
        {
            foreach (string game in new[] { "RFactor2", "LMU", "NASCARHeat5" })
            {
                Assert.Null(SlipAngleSourceCompiled.Catalog.Find(game).Unit);
                Assert.True(SlipAngleSourceCompiled.Catalog.TryGetNative(game, out SlipAngleRawSourceEntry e));
                Assert.Equal(SlipAngleUnit.Radians, e.Unit);
            }
            SlipAngleSourceGame Atan2(string unit) => new SlipAngleSourceGame
            {
                SlipAngle = Paths("lat").ToList(), Longitudinal = Paths("lon").ToList(), Transform = "atan2PatchVelocity", Unit = unit,
            };
            Assert.True(SlipAngleSourceGame.SameSettings(Atan2(null), Atan2("radians")));
            Assert.True(SlipAngleSourceGame.SameSettings(Atan2(null), Atan2("degrees")));
            Assert.False(SlipAngleSourceGame.SameSettings(new SlipAngleSourceGame { SlipAngle = Paths("a").ToList() },
                new SlipAngleSourceGame { SlipAngle = Paths("a").ToList(), Unit = "radians" }));   // an angle row's unit counts

            var doc = new SlipAngleSourceDocument();
            SlipAngleSourceGame row = Atan2(null);
            row.Id = "X";
            doc.Games.Add(row);
            Assert.True(SlipAngleSourceCatalog.FromDocument(doc).TryGetNative("X", out SlipAngleRawSourceEntry x));
            Assert.Equal(SlipAngleUnit.Radians, x.Unit);
        }

        [Fact]
        public void Saving_a_velocity_source_writes_no_unit()
        {
            SlipAngleSourceStore store = NewStore();
            var edit = new SlipAngleSourceEdit
            {
                Type = SlipAngleTelemetryType.LateralLongitudinal, Lateral = Paths("lat"), Longitudinal = Paths("lon"), Unit = "degrees",
            };
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("NewSim", edit));
            Assert.Null(store.Current("NewSim").Unit);
            Assert.True(_used.TryGetNative("NewSim", out SlipAngleRawSourceEntry e));
            Assert.Equal(SlipAngleUnit.Radians, e.Unit);
        }

        [Theory]
        [InlineData("Degrees", "degrees")]
        [InlineData(" RATIO100 ", "ratio100")]
        [InlineData("radians", "radians")]
        [InlineData("ratio1", "ratio1")]
        [InlineData("furlongs", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Normal_unit_lower_cases_and_drops_unknown_units(string unit, string normal)
            => Assert.Equal(normal, SlipAngleSourceEdit.NormalUnit(unit));

        [Fact]
        public void The_editor_state_reads_an_angle_rows_unit_only()
        {
            Assert.Equal("radians", SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("LFS")).Unit);
            Assert.Equal("ratio1", SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("FH6")).Unit);
            Assert.Equal("degrees", SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("KartKraft")).Unit);
            Assert.Null(SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("ACC")).Unit);        // Auto Detect
            Assert.Null(SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("RFactor2")).Unit);   // velocities
            Assert.Null(SlipAngleSourceEdit.From(null).Unit);
        }

        [Fact]
        public void Saving_an_angle_source_writes_its_unit_and_auto_detect_removes_it()
        {
            SlipAngleSourceStore store = NewStore();
            SlipAngleSourceEdit edit = SlipAngleSourceEdit.From(store.Current("AssettoCorsaEVO"));
            edit.Unit = "Ratio100";
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("AssettoCorsaEVO", edit));
            Assert.Equal("ratio100", store.Current("AssettoCorsaEVO").Unit);
            Assert.True(_used.TryGetNative("AssettoCorsaEVO", out SlipAngleRawSourceEntry e));
            Assert.Equal(SlipAngleUnit.Ratio100, e.Unit);
            Assert.Equal(SlipAngleUnit.Unverified, _used.TryGetNative("ACC", out e) ? e.Unit : SlipAngleUnit.Degrees);   // the others untouched

            edit.Unit = null;   // Auto Detect: back to the shared row
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("AssettoCorsaEVO", edit));
            Assert.Null(store.Current("AssettoCorsaEVO").Unit);
            Assert.False(store.CanRestore("AssettoCorsaEVO"));

            // The angle-only overload keeps the row's own unit.
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("KartKraft", Paths("kk")));
            Assert.Equal("degrees", store.Current("KartKraft").Unit);
        }

        // ---------------- surfaceValues ----------------

        [Fact]
        public void The_reader_translates_the_surface_by_the_rows_surfaceValues_and_leaves_an_untranslated_game_alone()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            var values = new Dictionary<string, object>();
            string[] wheels = { "01", "02", "03", "04" };
            for (int i = 0; i < 4; i++)
                values[SlipAngleRawSourceTable.FullPropertyName("CurrentPlayerTelemetry.mWheels" + wheels[i] + ".mSurfaceType")] = (byte)(i == 3 ? 1 : 4);
            var frame = new TelemetryFrame(groundSpeedKmh: 108, speedKmh: 108, lateralG: 1.0);
            SlipAngleInput rf2 = reader.Read("RFactor2", n => values.TryGetValue(n, out object v) ? v : null, frame, 1.0 / 60.0);
            Assert.Equal(new SurfaceBucket?[] { SurfaceBucket.Loose, SurfaceBucket.Loose, SurfaceBucket.Loose, null }, rf2.WheelSurface);   // 1 Wet: unknown

            SlipAngleInput f1 = reader.Read("F12025", n => "3", frame, 1.0 / 60.0);
            Assert.NotNull(f1.RawWheelSurface);
            Assert.Null(f1.WheelSurface);   // no surfaceValues: SimHub's grass/gravel flag decides instead
        }

        [Fact]
        public void SurfaceValues_are_part_of_the_merge_key()
        {
            var a = new SlipAngleSourceGame { Surface = Paths("s").ToList() };
            var b = new SlipAngleSourceGame { Surface = Paths("s").ToList(), SurfaceValues = new Dictionary<string, string> { ["1"] = "loose" } };
            var c = new SlipAngleSourceGame { Surface = Paths("s").ToList(), SurfaceValues = new Dictionary<string, string> { ["1"] = "LOOSE" } };
            Assert.False(SlipAngleSourceGame.SameSettings(a, b));
            Assert.True(SlipAngleSourceGame.SameSettings(b, c));
        }

        // ---------------- "Always car angle" ----------------

        [Fact]
        public void Always_car_angle_reads_no_tyre_value_even_for_a_game_with_a_source()
        {
            var always = true;
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { AlwaysCarAngle = () => always };
            var values = new Dictionary<string, object>();
            for (int i = 0; i < 4; i++) values[SlipAngleRawSourceTable.FullPropertyName("Physics.slipAngle" + (i + 1).ToString("00"))] = 0.05f;
            Func<string, object> get = n => values.TryGetValue(n, out object v) ? v : null;
            var frame = new TelemetryFrame(groundSpeedKmh: 108, speedKmh: 108, lateralG: 1.0);

            SlipAngleInput input = reader.Read("ACC", get, frame, 1.0 / 60.0);
            Assert.False(input.HasNativeSource);
            Assert.Null(input.NativeWheelDeg);

            always = false;
            Assert.True(reader.Read("ACC", get, frame, 1.0 / 60.0).HasNativeSource);
        }

        [Fact]
        public void Always_car_angle_keys_the_car_angle_hides_the_unit_dropdown_and_is_a_broadcast_field()
        {
            var ch = new SlipAngleChannelSettings { AngleSource = SlipAngleAngleSource.CarAngle };
            ch.EnsureSourceUnits();
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, SettingsHub.SlipAngleIdentity(ch, SourceNativeness.Native, "ACC"));
            Assert.Equal("SlipAngle.KeyData.Header.CarAngle",
                SettingsHub.SlipAngleKeyDataHeaderKey(SourceNativeness.Native, "ACC", ch.SourceUnits, SlipAngleAngleSource.CarAngle));
            Assert.False(SettingsHub.ShowSlipAngleUnitChoice("ACC", ch.SourceUnits, SlipAngleAngleSource.CarAngle));
            Assert.True(SettingsHub.ShowSlipAngleUnitChoice("ACC", ch.SourceUnits));

            var before = QAdvanceFeedbackSettings.CreateDefault();
            var after = QAdvanceFeedbackSettings.CreateDefault();
            after.SlipAngle.AngleSource = SlipAngleAngleSource.CarAngle;
            Assert.Contains(SettingField.SlipAngleAngleSource, SettingsHub.Diff(before, after, "ACC"));
        }

        // ---------------- verticalAcceleration (owner, 2026-11-24) ----------------

        [Theory]
        [InlineData("Rennsport", true)]
        [InlineData("IRacing", true)]
        [InlineData("iracing", true)]
        [InlineData("AssettoCorsaEVO", false)]
        [InlineData("F12025", false)]
        [InlineData("NoSuchGame", false)]
        public void The_compiled_copy_says_which_games_write_vertical_acceleration(string game, bool reports)
        {
            Assert.Equal(reports, SlipAngleSourceCompiled.Catalog.ReportsVerticalAcceleration(game));
        }

        [Fact]
        public void IRacing_has_a_flag_only_row_and_no_slip_angle_source()
        {
            Assert.False(SlipAngleSourceCompiled.Catalog.TryGetNative("IRacing", out _));
            Assert.Null(SlipAngleSourceCompiled.Catalog.SurfacePaths("IRacing"));
            Assert.Equal(true, SlipAngleSourceCompiled.Catalog.Find("IRacing").VerticalAcceleration);
        }

        [Fact]
        public void The_reader_table_asks_the_catalog_in_use_for_vertical_acceleration()
        {
            // The table is shared static state: the test project runs its tests one at a time
            // (xunit.runner.json), and the compiled copy is put back afterwards.
            var doc = new SlipAngleSourceDocument();
            doc.Games.Add(new SlipAngleSourceGame { Id = "MySim", VerticalAcceleration = true });
            doc.Games.Add(new SlipAngleSourceGame { Id = "Rennsport", SlipAngle = Paths("r").ToList() });
            try
            {
                SlipAngleRawSourceTable.Use(SlipAngleSourceCatalog.FromDocument(doc));
                Assert.True(SlipAngleRawSourceTable.ReportsVerticalAcceleration("MySim"));
                Assert.False(SlipAngleRawSourceTable.ReportsVerticalAcceleration("Rennsport"));   // this file does not say so
                Assert.False(SlipAngleRawSourceTable.ReportsVerticalAcceleration("IRacing"));     // nothing hard-coded
            }
            finally { SlipAngleRawSourceTable.Use(null); }
            Assert.True(SlipAngleRawSourceTable.ReportsVerticalAcceleration("Rennsport"));
            Assert.True(SlipAngleRawSourceTable.ReportsVerticalAcceleration("IRacing"));
            Assert.False(SlipAngleRawSourceTable.ReportsVerticalAcceleration("MySim"));
        }

        [Theory]
        [InlineData("Sim*")]
        [InlineData(@"regex:^Sim\d$")]
        public void A_wildcard_or_regex_row_may_hold_only_vertical_acceleration(string id)
        {
            var doc = new SlipAngleSourceDocument();
            doc.Games.Add(new SlipAngleSourceGame { Id = id, VerticalAcceleration = true });
            File.WriteAllText(FilePath, doc.ToJson());
            SlipAngleSourceStore store = NewStore();
            Assert.False(store.IsBroken, store.BrokenReason);
            Assert.True(_used.ReportsVerticalAcceleration("Sim1"));
            Assert.False(_used.TryGetNative("Sim1", out _));
        }

        [Fact]
        public void Vertical_acceleration_is_part_of_the_merge_key_and_of_the_settings()
        {
            var a = new SlipAngleSourceGame { SlipAngle = Paths("s").ToList() };
            var b = new SlipAngleSourceGame { SlipAngle = Paths("s").ToList(), VerticalAcceleration = true };
            var c = new SlipAngleSourceGame { SlipAngle = Paths("s").ToList(), VerticalAcceleration = false };
            Assert.False(SlipAngleSourceGame.SameSettings(a, b));
            Assert.True(SlipAngleSourceGame.SameSettings(a, c));   // false is the same as absent
            Assert.Equal(true, b.SettingsOnly().VerticalAcceleration);
            Assert.False(new SlipAngleSourceGame { Id = "X", VerticalAcceleration = true }.IsEmpty);
        }

        [Fact]
        public void The_editor_state_reads_the_rows_vertical_acceleration()
        {
            Assert.True(SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("Rennsport")).HasVerticalAcceleration);
            Assert.True(SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("IRacing")).HasVerticalAcceleration);
            Assert.False(SlipAngleSourceEdit.From(SlipAngleSourceCompiled.Catalog.Find("AssettoCorsaEVO")).HasVerticalAcceleration);
            Assert.False(SlipAngleSourceEdit.From(null).HasVerticalAcceleration);
        }

        [Fact]
        public void Saving_the_editor_writes_or_removes_vertical_acceleration()
        {
            SlipAngleSourceStore store = NewStore();
            SlipAngleSourceEdit edit = SlipAngleSourceEdit.From(store.Current("AssettoCorsaEVO"));
            edit.HasVerticalAcceleration = true;
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("AssettoCorsaEVO", edit));
            Assert.Equal(true, store.Current("AssettoCorsaEVO").VerticalAcceleration);
            Assert.True(_used.ReportsVerticalAcceleration("AssettoCorsaEVO"));
            Assert.False(_used.ReportsVerticalAcceleration("ACC"));   // only that game of the shared row
            Assert.Contains("\"verticalAcceleration\": true", File.ReadAllText(FilePath));

            edit = SlipAngleSourceEdit.From(store.Current("Rennsport"));
            Assert.True(edit.HasVerticalAcceleration);
            edit.HasVerticalAcceleration = false;
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("Rennsport", edit));
            Assert.Null(store.Current("Rennsport").VerticalAcceleration);
            Assert.False(_used.ReportsVerticalAcceleration("Rennsport"));
            Assert.True(store.CanRestore("Rennsport"));
        }

        [Fact]
        public void The_angle_only_save_keeps_the_surface_and_vertical_acceleration()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("Rennsport", Paths("mine")));
            SlipAngleSourceGame row = store.Current("Rennsport");
            Assert.Equal(Paths("mine"), row.SlipAngle);
            Assert.Equal(true, row.VerticalAcceleration);
            Assert.Equal(store.Compiled("Rennsport").Surface, row.Surface);
        }

        // ---------------- HasCompiledDefault (owner, 2026-11-24) ----------------

        [Theory]
        [InlineData("AssettoCorsaEVO", true)]
        [InlineData("Automobilista2", true)]   // surface only
        [InlineData("IRacing", true)]          // the vertical-acceleration flag only
        [InlineData("FH6", true)]              // through a wildcard
        [InlineData("NoSuchGame", false)]
        [InlineData("", false)]
        public void Has_compiled_default_is_any_shipped_row_with_a_setting(string game, bool shipped)
        {
            Assert.Equal(shipped, NewStore().HasCompiledDefault(game));
        }

        [Fact]
        public void A_surface_only_shipped_game_can_be_restored_once_changed()
        {
            SlipAngleSourceStore store = NewStore();
            Assert.False(store.CanRestore("Automobilista2"));
            SlipAngleSourceEdit edit = SlipAngleSourceEdit.From(store.Current("Automobilista2"));
            edit.HasSurface = false;
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("Automobilista2", edit));
            Assert.Null(store.Current("Automobilista2"));
            Assert.True(store.CanRestore("Automobilista2"));
            Assert.Equal(SlipAngleSourceSaveResult.Saved, store.Save("Automobilista2", (string[])null, restoreCompiled: true));
            Assert.True(SlipAngleSourceGame.SameSettings(store.Compiled("Automobilista2"), store.Current("Automobilista2")));
            Assert.False(store.CanRestore("Automobilista2"));
        }

        // ---------------- the source state (the orange line's wording, owner 2026-11-24) ----------------

        private static TelemetryFrame Moving => new TelemetryFrame(groundSpeedKmh: 108, speedKmh: 108, lateralG: 1.0);

        [Fact]
        public void A_game_whose_properties_never_arrive_while_moving_is_not_reported()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput first = reader.Read("ACC", n => null, Moving, 1.0 / 60.0);
            Assert.True(first.HasNativeSource);
            Assert.Equal(SlipAngleSourceState.Checking, first.SourceState);

            SlipAngleInput input = first;
            for (int i = 1; i < SlipAngleRawSourceReader.MissingVerdictFrames; i++) input = reader.Read("ACC", n => null, Moving, 1.0 / 60.0);
            Assert.Equal(SlipAngleSourceState.NotReported, input.SourceState);
            Assert.False(input.HasNativeSource);
            Assert.Equal(SlipAngleSourceState.NotReported, reader.Read("ACC", n => null, Moving, 1.0 / 60.0).SourceState);   // held
        }

        [Fact]
        public void Stopped_frames_never_rule_a_game_not_reported()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            var stopped = new TelemetryFrame(groundSpeedKmh: 0, speedKmh: 0, lateralG: 0.0);
            SlipAngleInput input = null;
            for (int i = 0; i < SlipAngleRawSourceReader.MissingVerdictFrames + 10; i++) input = reader.Read("ACC", n => null, stopped, 1.0 / 60.0);
            Assert.Equal(SlipAngleSourceState.Checking, input.SourceState);
        }

        [Fact]
        public void The_source_state_is_the_car_angle_applied_no_source_or_checking()
        {
            var always = true;
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { AlwaysCarAngle = () => always };
            Assert.Equal(SlipAngleSourceState.CarAngleAlways, reader.Read("ACC", n => null, Moving, 1.0 / 60.0).SourceState);
            Assert.Equal(SlipAngleSourceState.CarAngleAlways, reader.Read("NoSuchGame", n => null, Moving, 1.0 / 60.0).SourceState);

            always = false;
            Assert.Equal(SlipAngleSourceState.NoSource, reader.Read("NoSuchGame", n => null, Moving, 1.0 / 60.0).SourceState);

            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            units.Choose("ACC", SlipAngleUnitChoice.CarAngle);
            reader.Units = () => units;
            Assert.Equal(SlipAngleSourceState.CarAngleForGame, reader.Read("ACC", n => null, Moving, 1.0 / 60.0).SourceState);
            // "Always car angle" names itself even when the game's own choice is Car Angle too.
            always = true;
            Assert.Equal(SlipAngleSourceState.CarAngleAlways, reader.Read("ACC", n => null, Moving, 1.0 / 60.0).SourceState);
            always = false;
            units.Choose("NoSuchGame", SlipAngleUnitChoice.CarAngle);   // no row: the car angle is its only source anyway
            Assert.Equal(SlipAngleSourceState.NoSource, reader.Read("NoSuchGame", n => null, Moving, 1.0 / 60.0).SourceState);
        }

        [Fact]
        public void The_source_state_defaults_from_the_input_and_reaches_the_status()
        {
            Assert.Equal(SlipAngleSourceState.NoSource, new SlipAngleInput("G").SourceState);
            Assert.Equal(SlipAngleSourceState.Checking, new SlipAngleInput("G", true, new Corners(1, 1, 1, 1), false, SlipAngleUnit.Degrees).SourceState);
            Assert.Equal(SlipAngleSourceState.Native, new SlipAngleInput("G", true, new Corners(1, 1, 1, 1), true, SlipAngleUnit.Degrees).SourceState);

            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            SlipAngleFrameResult r = p.Process(new SlipAngleFrameRequest
            {
                GameId = "G", CarId = "C", Dt = 1.0 / 60.0, Frame = Moving,
                Input = new SlipAngleInput("G", sourceState: SlipAngleSourceState.NotReported),
            });
            Assert.Equal(SlipAngleSourceState.NotReported, r.SourceState);
            Assert.Equal(SlipAngleSourceState.NotReported, SlipAngleStatus.From(r, SourceNativeness.Fallback, "G").SourceState);
        }

        [Fact]
        public void An_unknown_angle_source_value_normalises_to_per_wheel()
        {
            var ch = new SlipAngleChannelSettings { AngleSource = (SlipAngleAngleSource)7 };
            ch.Normalise();
            Assert.Equal(SlipAngleAngleSource.PerWheelWhenAvailable, ch.AngleSource);
        }
    }
}

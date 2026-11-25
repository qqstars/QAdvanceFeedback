using System;
using System.IO;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The Slip Angle tab's source block (owner, 2026-11-23 / 2026-11-24): General Source Type first (Always car angle
    /// hides the rest), then the running game's row in QAdvanceFeedback.SlipAngleSource.json: Telemetry Type (None /
    /// Per-Wheel Angle / Lateral + Longitudinal Velocity), Has Surface, Has Vertical Acceleration and the boxes each
    /// needs, saved by the page's Apply; "Restore this game's source" writes at once; plus the surface line.
    /// </summary>
    public sealed class SlipAngleTelemetrySourceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "qaf-sat-" + Guid.NewGuid().ToString("N"));
        private readonly FakeSlipAngleHost _host = new FakeSlipAngleHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly SlipAngleSourceStore _store;

        public SlipAngleTelemetrySourceTests()
        {
            Directory.CreateDirectory(_dir);
            _store = new SlipAngleSourceStore(Path.Combine(_dir, SlipAngleSourceStore.FileName), use: _ => { });
            _store.Load();
            _host.NativeSources = _store;
            _host.Settings.SlipAngle.EnsureSourceUnits();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private SlipAngleTabViewModel NewVm(string game)
        {
            _host.GameId = game;
            var vm = new SlipAngleTabViewModel(_host, _ticker);
            vm.Load(_host.Settings.SlipAngle);
            vm.Activate();
            return vm;
        }

        private static string[] Texts(System.Collections.Generic.IReadOnlyList<SlipAngleTelemetryRowViewModel> rows) => rows.Select(r => r.Text).ToArray();

        private static void Type(System.Collections.Generic.IReadOnlyList<SlipAngleTelemetryRowViewModel> rows, params string[] texts)
        {
            for (int i = 0; i < 4; i++) rows[i].Text = texts[i];
        }

        // ---------------- Telemetry Type / Has Surface ----------------

        [Fact]
        public void An_angle_game_shows_Angle_Only_with_its_four_properties_and_restore_disabled_until_changed()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            Assert.True(vm.ShowTelemetry);
            Assert.True(vm.ShowTelemetryRows);
            Assert.Equal(SlipAngleTelemetryType.AngleOnly, vm.TelemetryType);
            Assert.True(vm.ShowAngleRows);
            Assert.False(vm.ShowVelocityRows);
            Assert.False(vm.HasSurface);
            Assert.False(vm.ShowSurfaceRows);
            Assert.False(vm.HasVerticalAcceleration);
            Assert.True(vm.ShowTelemetryRestore);      // a shipped game: shown...
            Assert.False(vm.CanRestoreTelemetry);      // ...but nothing to restore yet
            Assert.False(vm.RestoreTelemetryCommand.CanExecute(null));
            Assert.Equal(Enumerable.Range(1, 4).Select(i => "DataCorePlugin.GameRawData.Physics.slipAngle0" + i), Texts(vm.AngleRows));
            Assert.Equal("Angle Front Left", vm.AngleRows[0].Label);
            Assert.False(vm.HasPendingTelemetry);
        }

        [Fact]
        public void A_velocity_game_shows_both_velocity_groups_and_its_surface()
        {
            SlipAngleTabViewModel vm = NewVm("RFactor2");
            Assert.Equal(SlipAngleTelemetryType.LateralLongitudinal, vm.TelemetryType);
            Assert.False(vm.ShowAngleRows);
            Assert.True(vm.ShowVelocityRows);
            Assert.True(vm.HasSurface);
            Assert.True(vm.ShowSurfaceRows);
            Assert.EndsWith("mLateralPatchVel", vm.LateralRows[0].Text);
            Assert.EndsWith("mLongitudinalGroundVel", vm.LongitudinalRows[3].Text);
            Assert.EndsWith("mSurfaceType", vm.SurfaceRows[2].Text);
            Assert.Equal("Lateral Velocity Component FL", vm.LateralRows[0].Label);
            Assert.Equal("Longitudinal Velocity Component RR", vm.LongitudinalRows[3].Label);
            Assert.Equal("Rear Left Surface", vm.SurfaceRows[2].Label);
            Assert.All(vm.LateralRows, r => Assert.True(r.IsEditable));
        }

        [Fact]
        public void A_game_with_no_row_is_None_with_no_boxes_and_choosing_Angle_Only_saves_four_properties()
        {
            SlipAngleTabViewModel vm = NewVm("BrandNewSim");
            Assert.Equal(SlipAngleTelemetryType.None, vm.TelemetryType);
            Assert.True(vm.ShowTelemetryRows);
            Assert.False(vm.ShowAngleRows);
            Assert.False(vm.ShowVelocityRows);
            Assert.NotEmpty(vm.TelemetryMessage);
            Assert.False(vm.ShowTelemetryRestore);   // not in the compiled JSON

            vm.TelemetryType = SlipAngleTelemetryType.AngleOnly;
            Assert.True(vm.ShowAngleRows);
            Assert.Empty(vm.TelemetryMessage);
            Type(vm.AngleRows, "p.fl", "p.fr", "p.rl", "p.rr");
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.Equal(new[] { "p.fl", "p.fr", "p.rl", "p.rr" }, _store.Current("BrandNewSim").SlipAngle);
            Assert.Null(_store.Current("BrandNewSim").Unit);
        }

        [Fact]
        public void Lateral_Longitudinal_saves_an_atan2_row_in_radians_with_its_surface()
        {
            SlipAngleTabViewModel vm = NewVm("BrandNewSim");
            vm.TelemetryType = SlipAngleTelemetryType.LateralLongitudinal;
            Type(vm.LateralRows, "lat.fl", "lat.fr", "lat.rl", "lat.rr");
            Type(vm.LongitudinalRows, "lon.fl", "lon.fr", "lon.rl", "lon.rr");
            vm.HasSurface = true;
            Assert.True(vm.ShowSurfaceRows);
            Type(vm.SurfaceRows, "s.fl", "s.fr", "s.rl", "s.rr");
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());

            SlipAngleSourceGame row = _store.Current("BrandNewSim");
            Assert.Equal("atan2PatchVelocity", row.Transform);
            Assert.Null(row.Unit);   // velocities are always radians: no "unit" written
            Assert.True(SlipAngleSourceCatalog.Parse(File.ReadAllText(_store.Path)).TryGetNative("BrandNewSim", out SlipAngleRawSourceEntry e));
            Assert.Equal(SlipAngleUnit.Radians, e.Unit);
            Assert.Equal(new[] { "lat.fl", "lat.fr", "lat.rl", "lat.rr" }, row.SlipAngle);
            Assert.Equal(new[] { "lon.fl", "lon.fr", "lon.rl", "lon.rr" }, row.Longitudinal);
            Assert.Equal(new[] { "s.fl", "s.fr", "s.rl", "s.rr" }, row.Surface);
        }

        [Fact]
        public void None_with_a_surface_keeps_a_surface_only_row_and_unchecking_Has_Surface_takes_it_out()
        {
            SlipAngleTabViewModel vm = NewVm("RFactor2");
            vm.TelemetryType = SlipAngleTelemetryType.None;
            Assert.Equal(SlipAngleSourceSaveResult.Removed, vm.ApplyTelemetrySource());
            SlipAngleSourceGame row = _store.Current("RFactor2");
            Assert.False(row.HasSlipAngle);
            Assert.NotNull(row.Surface);
            Assert.NotNull(row.SurfaceValues);   // the meaning of its values stays with the surface

            vm.HasSurface = false;
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.Null(_store.Current("RFactor2"));
            Assert.True(vm.ShowTelemetryRestore);
            Assert.True(vm.CanRestoreTelemetry);
        }

        [Fact]
        public void A_shown_group_with_an_empty_box_is_refused_with_a_red_line_and_stays_pending()
        {
            SlipAngleTabViewModel vm = NewVm("BrandNewSim");
            vm.TelemetryType = SlipAngleTelemetryType.LateralLongitudinal;
            Type(vm.LateralRows, "a", "b", "c", "d");
            vm.LongitudinalRows[0].Text = "e";
            Assert.Equal(SlipAngleSourceSaveResult.Incomplete, vm.ApplyTelemetrySource());
            Assert.True(vm.HasPendingTelemetry);
            Assert.NotEmpty(vm.TelemetryStatus);
            Assert.Null(_store.Current("BrandNewSim"));
        }

        [Fact]
        public void An_edit_is_pending_until_Apply_and_Restore_writes_the_shipped_row_at_once()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            int edits = 0;
            vm.Edited += () => edits++;
            vm.AngleRows[0].Text = "DataCorePlugin.GameRawData.Mine01";
            Assert.True(vm.HasPendingTelemetry);
            Assert.True(edits > 0);
            Assert.True(vm.ShowTelemetryRestore);
            Assert.True(vm.CanRestoreTelemetry);   // a typed edit can be dropped
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.False(vm.HasPendingTelemetry);
            Assert.Equal("DataCorePlugin.GameRawData.Mine01", _store.Current("AssettoCorsaEVO").SlipAngle[0]);
            Assert.True(vm.CanRestoreTelemetry);   // the file differs from the shipped row
            Assert.True(vm.RestoreTelemetryCommand.CanExecute(null));

            vm.RestoreTelemetryCommand.Execute(null);   // no Apply: written at once
            Assert.False(vm.HasPendingTelemetry);
            Assert.Equal(Core.Localization.Strings.Get("SlipAngle.Telemetry.Restored"), vm.TelemetryStatus);
            Assert.Equal("DataCorePlugin.GameRawData.Physics.slipAngle01", vm.AngleRows[0].Text);
            Assert.True(SlipAngleSourceGame.SameSettings(_store.Compiled("AssettoCorsaEVO"), _store.Current("AssettoCorsaEVO")));
            var onDisk = SlipAngleSourceCatalog.Parse(File.ReadAllText(_store.Path)).Find("AssettoCorsaEVO");
            Assert.True(SlipAngleSourceGame.SameSettings(_store.Compiled("AssettoCorsaEVO"), onDisk));
            Assert.False(_store.CanRestore("AssettoCorsaEVO"));
            Assert.True(vm.ShowTelemetryRestore);
            Assert.False(vm.CanRestoreTelemetry);
            Assert.False(vm.RestoreTelemetryCommand.CanExecute(null));
            Assert.Equal(SlipAngleSourceSaveResult.Unchanged, vm.ApplyTelemetrySource());   // nothing left for Apply
        }

        [Fact]
        public void Restore_drops_a_typed_edit_when_the_file_still_holds_the_shipped_row()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            string before = File.ReadAllText(_store.Path);
            vm.AngleRows[0].Text = "DataCorePlugin.GameRawData.Mine01";
            Assert.True(vm.CanRestoreTelemetry);
            vm.RestoreTelemetryCommand.Execute(null);
            Assert.False(vm.HasPendingTelemetry);
            Assert.Equal("DataCorePlugin.GameRawData.Physics.slipAngle01", vm.AngleRows[0].Text);
            Assert.Equal(before, File.ReadAllText(_store.Path));   // the file already had it
            Assert.False(vm.CanRestoreTelemetry);
        }

        [Fact]
        public void A_surface_only_shipped_game_offers_restore_and_an_unknown_game_does_not()
        {
            Assert.True(_store.HasCompiledDefault("Automobilista2"));
            SlipAngleTabViewModel vm = NewVm("Automobilista2");
            Assert.Equal(SlipAngleTelemetryType.None, vm.TelemetryType);
            Assert.True(vm.HasSurface);
            Assert.True(vm.ShowTelemetryRestore);
            Assert.False(vm.CanRestoreTelemetry);

            vm = NewVm("BrandNewSim");
            Assert.False(vm.ShowTelemetryRestore);
            Assert.False(vm.CanRestoreTelemetry);
            Assert.False(vm.RestoreTelemetryCommand.CanExecute(null));
        }

        // ---------------- Source Unit (owner, 2026-11-24) ----------------

        [Fact]
        public void Source_unit_offers_auto_detect_and_the_four_units_and_loads_from_the_row()
        {
            SlipAngleTabViewModel vm = NewVm("LFS");
            Assert.Equal(new[] { SlipAngleUnitChoice.AutoDetect, SlipAngleUnitChoice.Degrees, SlipAngleUnitChoice.Radians,
                SlipAngleUnitChoice.Ratio1, SlipAngleUnitChoice.Ratio100 }, vm.SourceUnitChoices.Select(c => c.Value));
            Assert.Equal(SlipAngleUnitChoice.Radians, vm.SourceUnit);
            Assert.Equal("radians", vm.CurrentEdit().Unit);
            Assert.Equal(SlipAngleUnitChoice.Ratio1, NewVm("FH6").SourceUnit);
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, NewVm("AssettoCorsaEVO").SourceUnit);
            Assert.Null(NewVm("RFactor2").CurrentEdit().Unit);   // velocities: always radians, no unit
            Assert.False(vm.HasPendingTelemetry);
        }

        [Fact]
        public void Changing_the_source_unit_is_pending_and_Apply_writes_it_into_the_games_row()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            int edits = 0;
            vm.Edited += () => edits++;
            vm.SourceUnit = SlipAngleUnitChoice.Ratio100;
            Assert.True(vm.HasPendingTelemetry);
            Assert.True(edits > 0);
            Assert.Equal("ratio100", vm.CurrentEdit().Unit);
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            var onDisk = SlipAngleSourceDocument.FromJson(File.ReadAllText(_store.Path)).Games.Single(g => g.Id == "AssettoCorsaEVO");
            Assert.Equal("ratio100", onDisk.Unit);
            Assert.Equal(SlipAngleUnitChoice.Ratio100, vm.SourceUnit);

            vm.SourceUnit = SlipAngleUnitChoice.AutoDetect;
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.Null(_store.Current("AssettoCorsaEVO").Unit);
        }

        [Fact]
        public void The_source_type_dropdown_saves_to_the_config_and_never_to_the_source_file()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            string before = File.ReadAllText(_store.Path);
            DateTime written = File.GetLastWriteTimeUtc(_store.Path);
            vm.SelectedUnitChoice = SlipAngleUnitChoice.CarAngle;
            vm.RefreshFields(new[] { SettingField.SlipAngleSourceUnit });
            Assert.True(_host.Settings.SlipAngle.SourceUnits.ForcesCarAngle("AssettoCorsaEVO"));
            vm.SelectedUnitChoice = SlipAngleUnitChoice.Degrees;
            vm.RefreshFields(new[] { SettingField.SlipAngleSourceUnit });
            Assert.Equal(SlipAngleUnitChoice.Degrees, _host.Settings.SlipAngle.SourceUnits.Choice("AssettoCorsaEVO"));
            Assert.False(vm.HasPendingTelemetry);
            Assert.Equal(SlipAngleSourceSaveResult.Unchanged, vm.ApplyTelemetrySource());
            Assert.Equal(before, File.ReadAllText(_store.Path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(_store.Path));
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, vm.SourceUnit);   // the file's unit is unchanged
        }

        // ---------------- Has Vertical Acceleration ----------------

        [Fact]
        public void Has_vertical_acceleration_shows_the_rows_flag_and_is_saved_by_Apply()
        {
            SlipAngleTabViewModel vm = NewVm("Rennsport");
            Assert.True(vm.HasVerticalAcceleration);
            Assert.False(vm.HasPendingTelemetry);

            vm.HasVerticalAcceleration = false;
            Assert.True(vm.HasPendingTelemetry);
            Assert.False(vm.CurrentEdit().HasVerticalAcceleration);
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.Null(_store.Current("Rennsport").VerticalAcceleration);
            Assert.True(vm.CanRestoreTelemetry);

            vm = NewVm("AssettoCorsaEVO");
            Assert.False(vm.HasVerticalAcceleration);
            vm.HasVerticalAcceleration = true;
            Assert.True(vm.HasPendingTelemetry);
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.Equal(true, _store.Current("AssettoCorsaEVO").VerticalAcceleration);
            Assert.True(vm.HasVerticalAcceleration);
        }

        [Fact]
        public void LFS_stays_a_tangent_when_its_angle_boxes_are_edited()
        {
            SlipAngleTabViewModel vm = NewVm("LFS");
            Assert.Equal(SlipAngleTelemetryType.AngleOnly, vm.TelemetryType);
            Assert.True(vm.ShowTelemetryTangent);
            vm.AngleRows[0].Text = "DataCorePlugin.GameRawData.Other.fl";
            Assert.Equal(SlipAngleSourceSaveResult.Saved, vm.ApplyTelemetrySource());
            Assert.Equal("atanOfTangent", _store.Current("LFS").Transform);
            Assert.Equal("radians", _store.Current("LFS").Unit);
        }

        [Fact]
        public void A_shared_or_pattern_row_says_where_the_source_comes_from_with_its_note()
        {
            SlipAngleTabViewModel vm = NewVm("F12026SeasonPack");
            Assert.Contains("regex:", vm.TelemetryHeader);
            Assert.Contains("F12023 through F12099", vm.TelemetryRowNote);
            vm = NewVm("Drift21");
            Assert.Empty(vm.TelemetryRowNote);
            Assert.DoesNotContain("regex:", vm.TelemetryHeader);
        }

        [Fact]
        public void A_game_change_shows_the_new_games_source_unless_an_edit_is_pending()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            _host.GameId = "FH6";
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("FH6", vm.TelemetryGameId);
            Assert.Equal("DataCorePlugin.GameRawData.TireSlipAngleFrontLeft", vm.AngleRows[0].Text);

            vm.AngleRows[0].Text = "mine";
            _host.GameId = "ACC";
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("FH6", vm.TelemetryGameId);   // the edit is still saved to the game it was typed for
            Assert.Equal("mine", vm.AngleRows[0].Text);
        }

        [Fact]
        public void No_source_file_no_editor_and_a_broken_file_turns_it_off()
        {
            _host.NativeSources = null;
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            Assert.False(vm.ShowTelemetry);
            Assert.Equal(SlipAngleSourceSaveResult.Unchanged, vm.ApplyTelemetrySource());

            _host.NativeSources = _store;
            File.WriteAllText(_store.Path, "{ broken");
            _store.Load();
            vm = NewVm("AssettoCorsaEVO");
            Assert.True(vm.ShowTelemetry);
            Assert.False(vm.ShowTelemetryRows);
            Assert.NotEmpty(vm.TelemetryMessage);
        }

        // ---------------- General Source Type ----------------

        [Fact]
        public void Always_car_angle_hides_everything_under_it_and_is_saved_by_Apply()
        {
            SlipAngleTabViewModel vm = NewVm("AssettoCorsaEVO");
            Assert.True(vm.ShowPerWheel);
            Assert.True(vm.ShowUnitChoice);
            int edits = 0;
            vm.Edited += () => edits++;
            vm.AngleSource = SlipAngleAngleSource.CarAngle;
            Assert.True(edits > 0);
            Assert.False(vm.ShowPerWheel);
            Assert.False(vm.ShowTelemetry);
            Assert.False(vm.ShowUnitChoice);
            Assert.Equal(Core.Localization.Strings.Get("SlipAngle.KeyData.Header.CarAngle"), vm.KeyDataHeader);

            var target = new SlipAngleChannelSettings();
            vm.SaveTo(target);
            Assert.Equal(SlipAngleAngleSource.CarAngle, target.AngleSource);
        }

        // ---------------- the surface line ----------------

        [Fact]
        public void The_surface_line_names_the_surface_key_in_use_its_origin_and_a_change_in_progress()
        {
            var loose = new SlipAngleStatus(SourceNativeness.Native, true, true, SlipAngleWindow.ShippedDefault, SlipAngleWindow.ShippedDefault,
                0.4, ColdStartTier.Tier4, false, false, "F12025", "x", surface: SurfaceBucket.Loose,
                surfaceSource: SurfaceBucketSource.SimHub, surfaceWeight: 0.6);
            var t = new SlipAngleLiveText(loose);
            Assert.Contains("Gravel / dirt", t.SurfaceText);
            Assert.Contains("SimHub", t.SurfaceText);
            Assert.Contains("60%", t.SurfaceText);
            Assert.Contains("another surface", t.SurfaceBorrowText);

            var settled = new SlipAngleStatus(SourceNativeness.Native, true, true, SlipAngleWindow.ShippedDefault, SlipAngleWindow.ShippedDefault,
                1.0, ColdStartTier.Tier4, true, true, "F12025", "x");
            t = new SlipAngleLiveText(settled);
            Assert.Contains("Tarmac", t.SurfaceText);
            Assert.DoesNotContain("%", t.SurfaceText);
            Assert.Empty(t.SurfaceBorrowText);                 // confirmed: nothing borrowed
            Assert.Empty(new SlipAngleLiveText(SlipAngleStatus.None).SurfaceText);   // no game
        }

        [Fact]
        public void The_tab_shows_the_surface_line_from_the_live_status()
        {
            _host.Status = new SlipAngleStatus(SourceNativeness.Native, true, true, SlipAngleWindow.ShippedDefault, SlipAngleWindow.ShippedDefault,
                0.4, ColdStartTier.Tier1, false, false, "RFactor2", "x", surface: SurfaceBucket.Loose, surfaceSource: SurfaceBucketSource.Game);
            SlipAngleTabViewModel vm = NewVm("RFactor2");
            Assert.Contains("Gravel / dirt", vm.SurfaceText);
            Assert.Contains("5 / 8 / 10", vm.SurfaceBorrowText);
        }
    }
}

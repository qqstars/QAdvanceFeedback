using System;
using System.Linq;
using System.Threading.Tasks;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>A plugin stand-in for the Slip Angle tab: real settings, a real hub.</summary>
    public sealed class FakeSlipAngleHost : ISlipAngleTabHost, ILearningMaturityHost
    {
        public FakeSlipAngleHost()
        {
            Hub = new SettingsHub(() => Settings, _ => Saves++, () => GameId, () => IdentityTier);
        }

        public QAdvanceFeedbackSettings Settings { get; set; } = new QAdvanceFeedbackSettings();
        public SettingsHub Hub { get; }
        public string GameId { get; set; } = "game";
        public string CurrentGameId => GameId;
        public SlipAngleStatus Status { get; set; } = SlipAngleStatus.None;
        public SourceNativeness IdentityTier { get; set; } = SourceNativeness.Native;
        /// <summary>What the plugin's learner holds per identity for the current context (the display rule's
        /// result); an identity absent here is an unknown key.</summary>
        public System.Collections.Generic.Dictionary<string, SlipAngleWindow> Learned { get; } =
            new System.Collections.Generic.Dictionary<string, SlipAngleWindow>(StringComparer.Ordinal);
        public SlipAngleWindow? LearnedWindow(string sourceIdentity) =>
            sourceIdentity != null && Learned.TryGetValue(sourceIdentity, out SlipAngleWindow w) ? w : (SlipAngleWindow?)null;
        public SlipAngleWindowMaturity? Maturity { get; set; }
        public SlipAngleWindowMaturity? LearnedSlipAngleMaturity(string sourceIdentity) => Maturity;
        public KeyDataMaturity LearnedKeyDataMaturity(bool isLock, string sourceIdentity) => null;
        public int KeyDataRevision { get; set; }
        public int Saves { get; set; }
        public void SaveLiveSettings() => Saves++;
        public bool EvaluatorAvailable { get; set; } = true;
        public SourceButtonMode ResolveSourceButton(ScriptType type) => SourceButtonMode.Picker;
        public string Picked { get; set; }
        public Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType type) =>
            Task.FromResult(Picked == null ? null : new SourceEdit(Picked, type));
        /// <summary>Null unless a test gives the tab a source file (the telemetry editor is hidden then).</summary>
        public SlipAngleSourceStore NativeSources { get; set; }
    }

    /// <summary>The plugin page's Slip Angle tab (docs\slip-angle-design.md 9.9).</summary>
    public class SlipAngleTabViewModelTests
    {
        private readonly FakeSlipAngleHost _host = new FakeSlipAngleHost();
        private readonly ManualTicker _ticker = new ManualTicker();

        private SlipAngleTabViewModel NewVm()
        {
            var vm = new SlipAngleTabViewModel(_host, _ticker);
            vm.Load(_host.Settings.SlipAngle);
            vm.Activate();
            return vm;
        }

        private static SlipAngleStatus Status(bool peakConfirmed = true, SourceNativeness tier = SourceNativeness.Native,
            bool pluginInternal = true, bool hasData = true, string game = "game", bool faulted = false, double quality = 0.9,
            bool manualGateReady = false, string identity = null) =>
            new SlipAngleStatus(tier, pluginInternal, hasData, new SlipAngleWindow(4.0, 7.0, 9.0), new SlipAngleWindow(4.0, 7.0, 9.0),
                0.63, ColdStartTier.Tier1, peakConfirmed, manualGateReady, game,
                identity ?? (tier == SourceNativeness.Fallback ? SlipAngleFrameProcessor.FallbackIdentity : SlipAngleFrameProcessor.NativeIdentity),
                faulted, quality);

        private string Identity => SettingsHub.SlipAngleIdentity(_host.Settings.SlipAngle, _host.IdentityTier);

        // ---------------- the brief's tests ----------------

        [Fact]
        public void Auto_makes_editors_read_only()
        {
            SlipAngleTabViewModel vm = NewVm();
            Assert.True(vm.Auto);
            Assert.False(vm.ValuesEditable);

            vm.Auto = false;
            Assert.True(vm.ValuesEditable);
            Assert.False(_host.Settings.SlipAngle.KeyData.GetAutoGenerate(Identity));

            vm.Auto = true;
            Assert.False(vm.ValuesEditable);
            Assert.True(_host.Settings.SlipAngle.KeyData.GetAutoGenerate(Identity));
        }

        [Fact]
        public void Auto_shows_the_learned_window_and_ignores_typed_values()
        {
            _host.Status = Status();
            _host.Learned[SlipAngleFrameProcessor.NativeIdentity] = new SlipAngleWindow(4.0, 7.0, 9.0);   // the exact key's window
            SlipAngleTabViewModel vm = NewVm();
            Assert.Equal(new double?[] { 4.0, 7.0, 9.0 }, new[] { vm.Min, vm.Best, vm.Max });

            vm.Max = 12.0;   // a stray write under Auto (the editor is disabled) must not commit
            _ticker.Advance(TimeSpan.FromSeconds(2));
            Assert.False(_host.Settings.SlipAngle.KeyData.TryGetSlot("game", Identity, out _));
        }

        [Theory]
        [InlineData(TriggerBy.Brake, true, false)]
        [InlineData(TriggerBy.Throttle, false, true)]
        [InlineData(TriggerBy.Any, true, true)]
        public void Trigger_by_controls_threshold_visibility(TriggerBy by, bool brake, bool throttle)
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.TriggerBy = by;
            Assert.Equal(brake, vm.ShowBrakeThreshold);
            Assert.Equal(throttle, vm.ShowThrottleThreshold);
        }

        [Fact]
        public void Status_text_for_unconfirmed_peak()
        {
            _host.Status = Status(peakConfirmed: false, quality: 0.55);
            SlipAngleTabViewModel vm = NewVm();
            Assert.Contains(Strings.Get("SlipAngle.Status.PeakNotReached"), vm.LearningStatus);
            Assert.Contains(Strings.Get("SlipAngle.Status.MaxNotConfirmed"), vm.LearningStatus);
            Assert.Contains("63", vm.LearningStatus);   // handover confidence 0.63
            Assert.Contains("55", vm.LearningStatus);   // window quality 0.55

            _host.Status = Status(peakConfirmed: true);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.DoesNotContain(Strings.Get("SlipAngle.Status.PeakNotReached"), vm.LearningStatus);
            Assert.DoesNotContain(Strings.Get("SlipAngle.Status.MaxNotConfirmed"), vm.LearningStatus);
        }

        [Fact]
        public void Graph_samples_hit_10_20_60_at_min_best_max()
        {
            _host.Status = Status();
            _host.Learned[SlipAngleFrameProcessor.NativeIdentity] = new SlipAngleWindow(4.0, 7.0, 9.0);   // the exact key's window
            SlipAngleTabViewModel vm = NewVm();
            double At(double deg) => vm.SourceGraphSamples.Single(p => Math.Abs(p.X - deg) < 1e-9).Y;

            // Min, Best and Max sit exactly on the 30 / 60 / 80 anchors (owner, 2026-11-22: Best 60 -> 20, Max 80 -> 60).
            Assert.Equal(10.0, At(4.0), 6);
            Assert.Equal(20.0, At(7.0), 6);
            Assert.Equal(60.0, At(9.0), 6);
            Assert.Equal(13.5, vm.SourceGraphSamples.Last().X, 9);   // End = 1.5 x Max
            // The plotted curve is the same samples on the 0-100 axis of the shared graph control.
            Assert.Equal(100.0, vm.SourceGraphCurve.Last().X, 9);
            Assert.Contains("13.5", vm.SourceGraphCaption);
        }

        [Fact]
        public void Values_validate_like_key_data_settings()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.Auto = false;

            // Gap below 0.2 deg: refused, shown, never committed.
            vm.Min = 8.0; vm.Best = 8.1; vm.Max = 10.0;
            Assert.Equal(Strings.Get("SlipAngle.KeyData.Invalid"), vm.KeyDataStatus);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(_host.Settings.SlipAngle.KeyData.TryGetSlot("game", Identity, out _));

            // Above 45 deg: refused.
            vm.Best = 9.0; vm.Max = 50.0;
            Assert.Equal(Strings.Get("SlipAngle.KeyData.Invalid"), vm.KeyDataStatus);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(_host.Settings.SlipAngle.KeyData.TryGetSlot("game", Identity, out _));

            // Valid: committed through the hub after the typing pause.
            vm.Min = 4.0; vm.Best = 7.0; vm.Max = 9.0;
            Assert.Equal(string.Empty, vm.KeyDataStatus);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.True(_host.Settings.SlipAngle.KeyData.TryGetSlot("game", Identity, out SlipAngleWindow w));
            Assert.Equal(new[] { 4.0, 7.0, 9.0 }, new[] { w.Min, w.Best, w.Max });
            Assert.True(SlipAngleKeyDataSettings.IsValid(4.0, 7.0, 9.0));
        }

        // ---------------- R19 warning / status ----------------

        [Fact]
        public void Fallback_warning_only_for_plugin_internal_on_the_fallback_tier()
        {
            _host.Status = Status(tier: SourceNativeness.Fallback);
            SlipAngleTabViewModel vm = NewVm();
            Assert.True(vm.ShowFallbackWarning);
            Assert.Equal(Strings.Get("Source.FallbackWarning.SlipAngle"), vm.FallbackWarningText);
            Assert.False(vm.ShowNoData);

            _host.Status = Status(tier: SourceNativeness.Native);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(vm.ShowFallbackWarning);

            _host.Status = Status(tier: SourceNativeness.Fallback, pluginInternal: false);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(vm.ShowFallbackWarning);
        }

        [Fact]
        public void No_data_status_when_raw_all_is_null_but_not_with_a_value()
        {
            _host.Status = Status(tier: SourceNativeness.Unknown, hasData: false);
            SlipAngleTabViewModel vm = NewVm();
            Assert.True(vm.ShowNoData);
            Assert.Equal(Strings.Get("SlipAngle.Status.NoData"), vm.NoDataText);
            Assert.False(vm.ShowFallbackWarning);

            _host.Status = Status(tier: SourceNativeness.Unknown, pluginInternal: false, hasData: true);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(vm.ShowNoData);
        }

        [Fact]
        public void Faulted_shows_neither_the_warning_nor_the_no_data_status()
        {
            _host.Status = SlipAngleStatus.Fault("game", sourceIsPluginInternal: true);
            SlipAngleTabViewModel vm = NewVm();
            Assert.False(vm.ShowFallbackWarning);
            Assert.False(vm.ShowNoData);
            Assert.Equal(string.Empty, vm.LearningStatus);
        }

        [Fact]
        public void No_game_shows_no_learning_status()
        {
            _host.Status = SlipAngleStatus.None;
            SlipAngleTabViewModel vm = NewVm();
            Assert.False(vm.ShowFallbackWarning);
            Assert.False(vm.ShowNoData);
            Assert.Equal(string.Empty, vm.LearningStatus);
        }

        // ---------------- page Apply path ----------------

        [Fact]
        public void Draft_edits_mark_the_page_dirty_and_apply_only_on_save()
        {
            SlipAngleTabViewModel vm = NewVm();
            int edits = 0;
            vm.Edited += () => edits++;

            vm.TriggerBy = TriggerBy.Brake;
            vm.WeightMax = 0.7;
            vm.HoldMs = 400;
            vm.MaxFlightMs = 900;
            vm.PulseEnabled = true;
            vm.AngleSource = SlipAngleAngleSource.CarAngle;
            vm.Projector.Slightly.Output = 12;
            Assert.Equal(7, edits);
            Assert.Equal(TriggerBy.Any, _host.Settings.SlipAngle.Gate.TriggerBy);   // not applied yet

            var target = new SlipAngleChannelSettings();
            vm.SaveTo(target);
            Assert.Equal(TriggerBy.Brake, target.Gate.TriggerBy);
            Assert.Equal(0.7, target.WeightMax);
            Assert.Equal(0.4, target.Drift.HoldSeconds, 9);
            Assert.Equal(0.9, target.Gate.MaxFlightSeconds, 9);
            Assert.True(target.Pulse.Enabled);
            Assert.Equal(SlipAngleAngleSource.CarAngle, target.AngleSource);
            Assert.Equal(12.0, target.Projector.SlightlyOutput);
            Assert.Equal(ProjectorPreset.CurveCustom, target.Projector.Preset);
        }

        [Fact]
        public void Loading_never_marks_dirty()
        {
            SlipAngleTabViewModel vm = NewVm();
            int edits = 0;
            vm.Edited += () => edits++;
            var s = new SlipAngleChannelSettings { WeightMax = 0.5 };
            s.Gate.TriggerBy = TriggerBy.Throttle;
            vm.Load(s);
            Assert.Equal(0, edits);
            Assert.Equal(0.5, vm.WeightMax);
            Assert.Equal(TriggerBy.Throttle, vm.TriggerBy);
        }

        [Fact]
        public void Drift_editors_clamp_to_the_spec_ranges_on_save()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.HoldMs = 10;          // below 50
            vm.Gamma = 9;            // above 6
            vm.MaxWobbleMs = 9000;   // above 5000
            var target = new SlipAngleChannelSettings();
            vm.SaveTo(target);
            Assert.Equal(DriftSettings.MinHoldSeconds, target.Drift.HoldSeconds, 9);
            Assert.Equal(DriftSettings.MaxGamma, target.Drift.Gamma, 9);
            Assert.Equal(5.0, target.Gate.MaxWobbleSeconds, 9);
        }

        [Fact]
        public void Preset_change_rewrites_the_anchors_and_the_curve()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.Projector.Preset = ProjectorPreset.Linear;
            // The shipped Start input is 15 now (owner 2026-11-19), so Linear's line runs 15 -> 100.
            Assert.InRange(vm.Projector.Slightly.Output.Value, 15.0 * 100.0 / 85.0 - 1e-6, 15.0 * 100.0 / 85.0 + 1e-6);
            Assert.InRange(vm.Projector.Critical.Output.Value, 65.0 * 100.0 / 85.0 - 1e-6, 65.0 * 100.0 / 85.0 + 1e-6);
            Assert.Equal(35.0 * 100.0 / 85.0, vm.Projector.Curve.Single(p => p.X == 50.0).Y, 6);
        }

        [Fact]
        public void Outside_trigger_change_updates_only_that_field_without_marking_dirty()
        {
            SlipAngleTabViewModel vm = NewVm();
            int edits = 0;
            vm.Edited += () => edits++;
            vm.WeightMax = 0.6;   // an unapplied edit that must survive
            edits = 0;

            _host.Hub.Set(SettingField.SlipAngleTriggerBy, TriggerBy.Throttle, SettingsOrigin.ShakeItPanel);
            vm.RefreshFields(new[] { SettingField.SlipAngleTriggerBy });

            Assert.Equal(TriggerBy.Throttle, vm.TriggerBy);
            Assert.Equal(0.6, vm.WeightMax);
            Assert.Equal(0, edits);
        }

        // ---------------- key data (immediate path, like Lock) ----------------

        [Fact]
        public void Learned_hints_show_only_under_manual()
        {
            _host.Status = Status();
            _host.Learned[SlipAngleFrameProcessor.NativeIdentity] = new SlipAngleWindow(4.0, 7.0, 9.0);   // the exact key's window
            SlipAngleTabViewModel vm = NewVm();
            Assert.False(vm.ShowLearnedHints);
            vm.Auto = false;
            Assert.True(vm.ShowLearnedHints);
            Assert.Contains("9.0", vm.MaxLearnedText);
            Assert.Contains("4.0", vm.MinLearnedText);
        }

        [Fact]
        public void Per_game_switch_is_saved_at_once()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.Auto = false;
            int saves = _host.Saves;
            vm.PerGame = true;
            Assert.True(_host.Settings.SlipAngle.KeyData.GetPerGame(Identity));
            Assert.True(_host.Saves > saves);
        }

        [Fact]
        public void Reset_restores_the_source_defaults_and_is_offered_only_when_something_differs()
        {
            SlipAngleTabViewModel vm = NewVm();
            Assert.False(vm.ShowReset);
            vm.Auto = false;
            Assert.True(vm.ShowReset);

            vm.ResetCommand.Execute(null);
            Assert.True(_host.Settings.SlipAngle.KeyData.GetAutoGenerate(Identity));
            Assert.True(vm.Auto);
            Assert.False(vm.ShowReset);
        }

        [Fact]
        public void A_pending_value_edit_is_written_before_auto_takes_over()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.Auto = false;
            vm.Min = 4.0; vm.Best = 7.0; vm.Max = 9.0;
            vm.Auto = true;   // before the 700 ms debounce
            Assert.True(_host.Settings.SlipAngle.KeyData.TryGetSlot("game", Identity, out SlipAngleWindow w));
            Assert.Equal(9.0, w.Max);
        }

        [Fact]
        public void Start_line_follows_min_and_the_left_graph_has_a_marker_at_15()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.Min = 5.0;
            Assert.Equal("Start: 2° (50% of Min)", vm.StartText);
            Assert.Contains(vm.KeyMarkers, m => m.At == 15.0);
            vm.Min = 9.0;
            Assert.Equal("Start: 4° (50% of Min)", vm.StartText);
            vm.Min = 1.5;
            Assert.Equal("Start: — (Min below 2°)", vm.StartText);
            Assert.DoesNotContain(vm.KeyMarkers, m => m.At == 15.0);   // no Start knot, no marker at 15
            vm.Min = 5.0;
            Assert.Contains(vm.KeyMarkers, m => m.At == 15.0);
        }

        [Fact]
        public void Start_marker_is_hidden_for_empty_or_non_finite_min_and_shown_on_load()
        {
            SlipAngleTabViewModel vm = NewVm();
            vm.Min = 9.0;
            Assert.Contains(vm.KeyMarkers, m => m.At == 15.0);
            vm.Min = null;
            Assert.DoesNotContain(vm.KeyMarkers, m => m.At == 15.0);
            Assert.Equal(string.Empty, vm.StartText);
            vm.Min = 9.0;
            vm.Min = double.NaN;
            Assert.DoesNotContain(vm.KeyMarkers, m => m.At == 15.0);

            // Load path: the shipped window (Min 5) has a Start.
            SlipAngleTabViewModel loaded = NewVm();
            Assert.Contains(loaded.KeyMarkers, m => m.At == 15.0);
        }

        // ---------------- the one display rule (owner, 2026-11-19; docs\full-mvvm-plan.md 3.5) ----------------

        [Fact]
        public void Restart_on_the_menu_shows_the_saved_exact_key()
        {
            // SimHub restarted, no game running: no frame-loop status, but the plugin's learner holds the
            // persisted context's exact key (the restored tier picks the Fallback identity).
            _host.GameId = null;
            _host.Status = SlipAngleStatus.None;
            _host.IdentityTier = SourceNativeness.Fallback;
            _host.Learned[SlipAngleFrameProcessor.FallbackIdentity] = new SlipAngleWindow(6.0, 9.0, 14.0);
            SlipAngleTabViewModel vm = NewVm();
            Assert.Equal(new double?[] { 6.0, 9.0, 14.0 }, new[] { vm.Min, vm.Best, vm.Max });
        }

        [Fact]
        public void First_launch_or_another_car_shows_the_default()
        {
            // No exact key known (first launch, or another car's context): the default, even when the frame
            // loop's status carries some window.
            _host.Status = Status();
            SlipAngleTabViewModel vm = NewVm();
            Assert.Equal(new double?[] { 5.0, 8.0, 10.0 }, new[] { vm.Min, vm.Best, vm.Max });
            vm.Auto = false;
            Assert.Equal(Strings.Get("KeyData.LearnedPending"), vm.MaxLearnedText);
        }

        [Fact]
        public void Manual_with_nothing_stored_prefills_the_default_and_hints_the_learned_window()
        {
            _host.Learned[SlipAngleFrameProcessor.NativeIdentity] = new SlipAngleWindow(4.0, 7.0, 9.0);
            _host.Settings.SlipAngle.KeyData.SetAutoGenerate(Identity, false);
            SlipAngleTabViewModel vm = NewVm();
            Assert.Equal(new double?[] { 5.0, 8.0, 10.0 }, new[] { vm.Min, vm.Best, vm.Max });
            Assert.True(vm.ShowLearnedHints);
            Assert.Contains("9.0", vm.MaxLearnedText);
            Assert.Contains("4.0", vm.MinLearnedText);
        }

        [Fact]
        public void Native_and_fallback_keys_are_never_cross_read()
        {
            _host.Learned[SlipAngleFrameProcessor.NativeIdentity] = new SlipAngleWindow(4.0, 7.0, 9.0);
            _host.IdentityTier = SourceNativeness.Fallback;
            SlipAngleTabViewModel vm = NewVm();
            Assert.Equal(new double?[] { 5.0, 8.0, 10.0 }, new[] { vm.Min, vm.Best, vm.Max });
        }
    }
}

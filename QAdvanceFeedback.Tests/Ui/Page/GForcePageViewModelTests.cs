using System;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The plugin page's G-Force tab (docs\full-mvvm-plan.md §3.2): every input reads and writes the
    /// page DRAFT, a ShakeIt-side change reaches it at once, and the rules the old code-behind enforced
    /// (Settings\SettingsControl.xaml.cs, SettingsControl.GForceShake.cs / .GForceTest.cs) still hold.
    /// </summary>
    public class GForcePageViewModelTests : IDisposable
    {
        private readonly FakeSettingsPageHost _host = new FakeSettingsPageHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly SettingsPageViewModel _page;

        public GForcePageViewModelTests()
        {
            _page = new SettingsPageViewModel(_host, new ImmediateDispatcher(), _ticker, new FakeDialogService(), new FakeClipboard());
        }

        public void Dispose()
        {
            _page.Dispose();
        }

        private GForcePageViewModel Vm => _page.GForceTab;
        private GForceSettings Live => _host.Settings.GForce;
        private GForceSettings Draft => _page.Context.Settings.GForce;

        // ------------------------------------------------------------------ load

        [Fact]
        public void Every_field_shows_the_live_value()
        {
            Live.FixedAccelMaxG = 1.11; Live.FixedDecelMaxG = 2.22; Live.FixedLatMaxG = 1.33;
            Live.AccelMaxMode = GMaxMode.Fixed; Live.DecelMaxMode = GMaxMode.Auto; Live.LatMaxMode = GMaxMode.Fixed;
            Live.AccelOutputScalePercent = 11; Live.BrakeOutputScalePercent = 12; Live.LateralOutputScalePercent = 13;
            Live.BrakeBottomFrontLatSplitPercent = 21; Live.BrakeBottomRearLatSplitPercent = 22; Live.BrakeBackLowLatSplitPercent = 23;
            Live.AccelBottomRearLatSplitPercent = 24; Live.AccelBackLowLatSplitPercent = 25; Live.AccelBackTopLatSplitPercent = 26;
            Live.BrakeBottomRearSustainPercent = 31; Live.BrakeBackLowSustainPercent = 32;
            Live.AccelBottomRearSustainPercent = 33; Live.AccelBackLowSustainPercent = 34;
            Live.SustainTimeConstantSeconds = 0.41; Live.TransientTimeConstantSeconds = 0.42; Live.TransientGain = 1.3;
            Live.RetriggerStrictness = 1.4; Live.AutoTransitionAnimationScale = 1.5; Live.FixedTransitionAnimationScale = 1.6;
            Live.LateralDirection = LateralDirectionMode.Reversed;
            Live.ShakeSustainPercent = 35;
            _page.Activate();

            Assert.False(Vm.AccelMax.Auto); Assert.Equal(1.11, Vm.AccelMax.Max);
            Assert.True(Vm.DecelMax.Auto); Assert.Equal(2.22, Vm.DecelMax.Max);
            Assert.False(Vm.LatMax.Auto); Assert.Equal(1.33, Vm.LatMax.Max);
            Assert.Equal(new double?[] { 11, 12, 13 }, new[] { Vm.AccelScale, Vm.BrakeScale, Vm.LateralScale });
            Assert.Equal(new double?[] { 21, 22, 23, 24, 25, 26 }, new[] { Vm.BrakeBottomFrontLatSplit, Vm.BrakeBottomRearLatSplit,
                Vm.BrakeBackLowLatSplit, Vm.AccelBottomRearLatSplit, Vm.AccelBackLowLatSplit, Vm.AccelBackTopLatSplit });
            Assert.Equal(new double?[] { 31, 32, 33, 34 }, new[] { Vm.BrakeBottomRearSustain, Vm.BrakeBackLowSustain,
                Vm.AccelBottomRearSustain, Vm.AccelBackLowSustain });
            Assert.Equal(new double?[] { 0.41, 0.42, 1.3, 1.4, 1.5, 1.6 }, new[] { Vm.SustainTau, Vm.TransientTau, Vm.TransientGain,
                Vm.RetriggerStrictness, Vm.AutoTransitionScale, Vm.FixedTransitionScale });
            Assert.Equal(LateralDirectionMode.Reversed, Vm.LateralDirection);
            Assert.Equal(35, Vm.Shake.Sustain);
            Assert.False(_page.IsDirty);
        }

        [Fact]
        public void The_recommended_frequency_note_formats_the_settings_range()
        {
            Assert.Equal(string.Format(Strings.Get("GForce.RecommendedHz.Note"), Live.RecommendedFromHz, Live.RecommendedToHz),
                Vm.RecommendedHzText);
        }

        // ------------------------------------------------------------------ edits go to the draft only

        [Fact]
        public void A_number_edit_writes_the_draft_marks_dirty_and_leaves_live_alone()
        {
            double liveBefore = Live.TransientGain;
            Vm.TransientGain = 2.5;

            Assert.Equal(2.5, Draft.TransientGain);
            Assert.Equal(liveBefore, Live.TransientGain);
            Assert.True(_page.IsDirty);
        }

        [Fact]
        public void Every_number_property_writes_its_own_draft_field()
        {
            Vm.AccelScale = 51; Vm.BrakeScale = 52; Vm.LateralScale = 53;
            Vm.BrakeBottomFrontLatSplit = 61; Vm.BrakeBottomRearLatSplit = 62; Vm.BrakeBackLowLatSplit = 63;
            Vm.AccelBottomRearLatSplit = 64; Vm.AccelBackLowLatSplit = 65; Vm.AccelBackTopLatSplit = 66;
            Vm.BrakeBottomRearSustain = 71; Vm.BrakeBackLowSustain = 72; Vm.AccelBottomRearSustain = 73; Vm.AccelBackLowSustain = 74;
            Vm.SustainTau = 0.81; Vm.TransientTau = 0.82; Vm.TransientGain = 0.83;
            Vm.RetriggerStrictness = 0.84; Vm.AutoTransitionScale = 0.85; Vm.FixedTransitionScale = 0.86;
            Vm.AccelMax.Max = 1.7; Vm.DecelMax.Max = 2.7; Vm.LatMax.Max = 1.9;

            Assert.Equal(new[] { 51.0, 52, 53 }, new[] { Draft.AccelOutputScalePercent, Draft.BrakeOutputScalePercent, Draft.LateralOutputScalePercent });
            Assert.Equal(new[] { 61.0, 62, 63, 64, 65, 66 }, new[] { Draft.BrakeBottomFrontLatSplitPercent, Draft.BrakeBottomRearLatSplitPercent,
                Draft.BrakeBackLowLatSplitPercent, Draft.AccelBottomRearLatSplitPercent, Draft.AccelBackLowLatSplitPercent, Draft.AccelBackTopLatSplitPercent });
            Assert.Equal(new[] { 71.0, 72, 73, 74 }, new[] { Draft.BrakeBottomRearSustainPercent, Draft.BrakeBackLowSustainPercent,
                Draft.AccelBottomRearSustainPercent, Draft.AccelBackLowSustainPercent });
            Assert.Equal(new[] { 0.81, 0.82, 0.83, 0.84, 0.85, 0.86 }, new[] { Draft.SustainTimeConstantSeconds, Draft.TransientTimeConstantSeconds,
                Draft.TransientGain, Draft.RetriggerStrictness, Draft.AutoTransitionAnimationScale, Draft.FixedTransitionAnimationScale });
            Assert.Equal(new[] { 1.7, 2.7, 1.9 }, new[] { Draft.FixedAccelMaxG, Draft.FixedDecelMaxG, Draft.FixedLatMaxG });
        }

        [Fact]
        public void An_emptied_box_keeps_the_value_and_is_not_an_edit()
        {
            // OLD RULE (SaveGForceToSettings, SettingsControl.xaml.cs): "Value ?? g.X" - an empty box keeps the value.
            double before = Draft.AccelOutputScalePercent;
            Vm.AccelScale = null;
            Vm.AccelMax.Max = null;

            Assert.Equal(before, Draft.AccelOutputScalePercent);
            Assert.False(_page.IsDirty);
        }

        [Fact]
        public void A_value_the_setting_clamps_reads_back_clamped()
        {
            Vm.AccelScale = 150;
            Assert.Equal(100, Vm.AccelScale);
            Assert.Equal(100, Draft.AccelOutputScalePercent);
        }

        [Fact]
        public void Lateral_direction_is_a_draft_edit()
        {
            Vm.LateralDirection = LateralDirectionMode.Reversed;

            Assert.Equal(LateralDirectionMode.Reversed, Draft.LateralDirection);
            Assert.Equal(LateralDirectionMode.Normal, Live.LateralDirection);
            Assert.True(_page.IsDirty);
        }

        [Fact]
        public void Apply_writes_the_page_edits_into_live()
        {
            Vm.BrakeScale = 40;
            Vm.DecelMax.Auto = false;
            Vm.Shake.Sustain = 45;

            _page.ApplyCommand.Execute(null);

            Assert.Equal(40, Live.BrakeOutputScalePercent);
            Assert.Equal(GMaxMode.Fixed, Live.DecelMaxMode);
            Assert.Equal(45, Live.ShakeSustainPercent);
            Assert.False(_page.IsDirty);
        }

        // ------------------------------------------------------------------ the maxima readouts

        [Fact]
        public void The_auto_toggle_is_a_draft_edit_and_rebuilds_its_readout_at_once()
        {
            // OLD RULE (ctor, SettingsControl.xaml.cs:190-195): toggling Auto marks dirty and refreshes the readout.
            Live.FixedAccelMaxG = 0.75;
            _page.Activate();
            Assert.True(Vm.AccelMax.Auto);
            Assert.Equal(GForceReadout.Build(true, 0.75, false, 0), Vm.AccelMax.Readout);

            Vm.AccelMax.Auto = false;

            Assert.Equal(GMaxMode.Fixed, Draft.AccelMaxMode);
            Assert.Equal(GMaxMode.Auto, Live.AccelMaxMode);
            Assert.True(_page.IsDirty);
            Assert.Equal(GForceReadout.Build(false, 0.75, false, 0), Vm.AccelMax.Readout);
        }

        [Fact]
        public void The_readout_describes_the_page_mode_with_the_applied_fixed_value()
        {
            // OLD RULE (RefreshGForceLearnedText, SettingsControl.xaml.cs:3385-3396): the mode comes from the
            // page's toggle, the fixed value and the detection from the RUNNING plugin's settings.
            Live.DecelMaxMode = GMaxMode.Fixed;
            Live.FixedDecelMaxG = 1.5;
            _page.Activate();

            Vm.DecelMax.Max = 2.5;   // unapplied

            Assert.Equal(GForceReadout.Build(false, 1.5, false, 0), Vm.DecelMax.Readout);
        }

        [Fact]
        public void The_readouts_refresh_every_second_while_the_tab_is_shown()
        {
            // OLD RULE (G-FORCE READOUT LIVE-REFRESH FIX, SettingsControl.xaml.cs:93-116, 155-171): the learner
            // keeps learning while the page is open, so the readout re-queries once a second.
            Vm.Activate();
            Live.FixedLatMaxG = 1.9;   // stands in for "the running plugin moved" with no event at all

            _ticker.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal(GForceReadout.ForAxis(Live, SettingField.LatMaxMode), Vm.LatMax.Readout);
        }

        [Fact]
        public void Hiding_the_tab_stops_the_readout_timer()
        {
            int before = _ticker.ActiveCount;
            Vm.Activate();
            Assert.Equal(before + 1, _ticker.ActiveCount);

            Vm.Deactivate();
            _ticker.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal(before, _ticker.ActiveCount);
        }

        [Fact]
        public void Showing_the_tab_again_restarts_the_readout_timer()
        {
            // The old timer was stopped on the first Unloaded and never restarted; now it follows the tab.
            Vm.Activate();
            Vm.Deactivate();
            Vm.Activate();
            Live.FixedLatMaxG = 2.1;
            _ticker.Advance(TimeSpan.FromSeconds(1));

            Assert.Contains("2.10", Vm.LatMax.Readout);
        }

        [Fact]
        public void Apply_refreshes_the_readout_to_the_applied_value()
        {
            Live.AccelMaxMode = GMaxMode.Fixed;
            _page.Activate();
            Vm.AccelMax.Max = 1.25;

            _page.ApplyCommand.Execute(null);

            Assert.Equal(GForceReadout.Build(false, 1.25, false, 0), Vm.AccelMax.Readout);
        }

        // ------------------------------------------------------------------ ShakeIt-side changes

        [Fact]
        public void A_ShakeIt_side_max_and_mode_reach_the_tab_without_marking_it_dirty()
        {
            var changed = new System.Collections.Generic.List<string>();
            Vm.DecelMax.PropertyChanged += (s, e) => changed.Add(e.PropertyName);

            _host.Hub.Set(SettingField.FixedDecelMaxG, 3.1, SettingsOrigin.ShakeItPanel);
            _host.Hub.Set(SettingField.DecelMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel);

            Assert.Equal(3.1, Vm.DecelMax.Max);
            Assert.False(Vm.DecelMax.Auto);
            Assert.Equal(GForceReadout.Build(false, 3.1, false, 0), Vm.DecelMax.Readout);
            Assert.Contains(null, changed);
            Assert.False(_page.IsDirty);
        }

        [Fact]
        public void A_ShakeIt_side_lateral_direction_and_integration_switch_reach_the_tab()
        {
            _host.Hub.Set(SettingField.LateralDirection, LateralDirectionMode.Reversed, SettingsOrigin.ShakeItPanel);
            _host.Hub.Set(SettingField.IntegrateSlipAngle, false, SettingsOrigin.ShakeItPanel);
            _host.Hub.Set(SettingField.IntegrateWheelLockAndSlip, false, SettingsOrigin.ShakeItPanel);

            Assert.Equal(LateralDirectionMode.Reversed, Vm.LateralDirection);
            Assert.False(Vm.Shake.IntegrateSlipAngle);
            Assert.False(Vm.Shake.IntegrateWheelLockAndSlip);
            Assert.False(Vm.Shake.SharedEnabled);
            Assert.False(_page.IsDirty);
        }

        [Fact]
        public void A_ShakeIt_side_change_keeps_an_unapplied_shake_edit()
        {
            Vm.Shake.Trigger = 33;
            _host.Hub.Set(SettingField.IntegrateSlipAngle, false, SettingsOrigin.ShakeItPanel);

            Assert.Equal(33, Vm.Shake.Trigger);
            Assert.Equal(33, Draft.ShakeTriggerThresholdPercent);
            Assert.True(_page.IsDirty);
        }

        // ------------------------------------------------------------------ the shake block (hosted)

        [Fact]
        public void A_shake_mode_pick_resets_all_three_scales_and_the_trigger()
        {
            // OWNER DECISION KEPT (docs\architecture.md / slip-angle report: "owner keeps the G-Force
            // mode-pick scale reset"; GForceShakeViewModel.Mode, owner 2026-09-07): picking a mode
            // overwrites hand-tuned scales with that mode's defaults.
            Vm.Shake.LockScale = 7.7; Vm.Shake.SlipScale = 6.6; Vm.Shake.AngleScale = 5.5; Vm.Shake.Trigger = 44;
            ShakeApplyMode pick = Vm.Shake.Mode == ShakeApplyMode.PerChannel ? ShakeApplyMode.AllChannelsGForce : ShakeApplyMode.PerChannel;

            Vm.Shake.Mode = pick;

            double scale = GForceSettings.DefaultShakeScaleFor(pick);
            Assert.Equal(pick, Draft.ShakeApplyMode);
            Assert.Equal(scale, Draft.WheelLockShakeScale);
            Assert.Equal(scale, Draft.WheelSlipShakeScale);
            Assert.Equal(scale, Draft.WheelSlipAngleShakeScale);
            Assert.Equal(GForceSettings.DefaultShakeTriggerFor(pick), Draft.ShakeTriggerThresholdPercent);
            Assert.True(_page.IsDirty);
        }

        [Fact]
        public void A_mode_reaching_the_tab_from_elsewhere_keeps_the_saved_scales()
        {
            // "A load never does": an external / reloaded mode shows the driver's own saved scales.
            ShakeApplyMode other = Live.ShakeApplyMode == ShakeApplyMode.PerChannel ? ShakeApplyMode.AllChannelsGForce : ShakeApplyMode.PerChannel;
            Live.ShakeApplyMode = other;
            Live.WheelLockShakeScale = 3.3;
            Live.ShakeTriggerThresholdPercent = 12;
            _page.Activate();

            Assert.Equal(other, Vm.Shake.Mode);
            Assert.Equal(3.3, Vm.Shake.LockScale);
            Assert.Equal(3.3, Draft.WheelLockShakeScale);
            Assert.Equal(12, Draft.ShakeTriggerThresholdPercent);
            Assert.False(_page.IsDirty);
        }

        [Fact]
        public void A_feeling_pick_rewrites_the_frequency_into_the_draft()
        {
            ShakeFeeling pick = Vm.Shake.Feeling == ShakeFeeling.SamePhase ? ShakeFeeling.Blending : ShakeFeeling.SamePhase;
            Vm.Shake.Frequency = 17;

            Vm.Shake.Feeling = pick;

            Assert.Equal(pick, Draft.ShakeFeeling);
            Assert.Equal(GForceSettings.DefaultShakeFrequencyFor(pick), Draft.ShakeFrequencyHz);
        }

        [Fact]
        public void A_shake_switch_is_a_draft_edit()
        {
            Vm.Shake.IntegrateSlipAngle = false;

            Assert.False(Draft.IntegrateSlipAngle);
            Assert.True(Live.IntegrateSlipAngle);
            Assert.True(_page.IsDirty);
        }

        [Fact]
        public void Restore_defaults_reloads_the_shake_block()
        {
            Live.ShakeSustainPercent = 60;
            _page.Activate();
            Assert.Equal(60, Vm.Shake.Sustain);

            _page.RestoreDefaultsCommand.Execute(null);

            Assert.Equal(new GForceSettings().ShakeSustainPercent, Vm.Shake.Sustain);
            Assert.False(_page.IsDirty);
        }

        // ------------------------------------------------------------------ the preview graph

        [Fact]
        public void The_preview_follows_frequency_hold_and_feeling()
        {
            // OLD RULE (OnGForceShakePropertyChanged, SettingsControl.GForceShake.cs): the preview redraws
            // when the frequency, hold or feeling changes - from the same GForceShake.FeelingPair the engine calls.
            var raised = new System.Collections.Generic.List<string>();
            Vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            Vm.Shake.Frequency = 3;

            Assert.Contains(nameof(GForcePageViewModel.PreviewLeft), raised);
            GForcePageShakePreview expected = GForcePageShakePreview.Build(3, Vm.Shake.Sustain, Vm.Shake.Feeling);
            Assert.Equal(expected.Left, Vm.PreviewLeft);
            Assert.Equal(expected.Right, Vm.PreviewRight);
        }

        // ------------------------------------------------------------------ the Test Effect

        [Fact]
        public void The_test_effect_runs_on_the_unapplied_page_values_and_never_marks_dirty()
        {
            // OLD RULES (SettingsControl.GForceTest.cs WireGForceTest / PageGForceSettingsForTest): the test reads
            // EVERY G-Force setting on the page, unapplied ones included, into a THROWAWAY object - nothing about
            // it is saved, applied or marks the page dirty.
            Vm.DecelMax.Max = 3.25;
            _page.ApplyCommand.Execute(null);
            Assert.False(_page.IsDirty);
            Vm.DecelMax.Max = 3.75;   // unapplied
            string liveJson = Newtonsoft.Json.JsonConvert.SerializeObject(Live);
            Vm.Activate();

            Vm.Test.PadY = 1.0;       // full braking
            Vm.Test.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(0.1));

            Assert.Contains("3.75", Vm.Test.LongText);
            Assert.True(_page.IsDirty);   // the unapplied edit, not the test
            Assert.Equal(liveJson, Newtonsoft.Json.JsonConvert.SerializeObject(Live));
            Assert.Equal(3.75, Draft.FixedDecelMaxG);

            Vm.Test.IsTestEnabled = false;
            _page.ApplyCommand.Execute(null);
            Vm.Test.IsTestEnabled = true;
            Assert.False(_page.IsDirty);
        }

        [Fact]
        public void The_test_effect_sees_an_edit_made_while_it_runs()
        {
            Vm.Activate();
            Vm.Test.PadY = 1.0;
            Vm.Test.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(0.1));

            Vm.DecelMax.Max = 4.5;
            Vm.Test.PadY = 0.5;       // input, so the test stays on
            Vm.Test.PadY = 1.0;
            _ticker.Advance(TimeSpan.FromSeconds(0.1));

            Assert.Contains("4.50", Vm.Test.LongText);
        }

        [Fact]
        public void The_test_effect_follows_a_lateral_direction_change_made_while_it_runs()
        {
            // Final review I1: the old page rebuilt the test settings from the controls on every call.
            Vm.Activate();
            Assert.Equal(LateralDirectionMode.Normal, Vm.SettingsForTest().LateralDirection);

            Vm.LateralDirection = LateralDirectionMode.Reversed;

            Assert.Equal(LateralDirectionMode.Reversed, Vm.SettingsForTest().LateralDirection);
        }

        [Fact]
        public void Hiding_the_tab_stops_the_test_effect()
        {
            // OLD RULE (WireGForceTest: "Leaving the page hands the outputs back to the live pipeline").
            Vm.Activate();
            Vm.Test.IsTestEnabled = true;

            Vm.Deactivate();

            Assert.False(Vm.Test.IsTestEnabled);
        }

        [Fact]
        public void The_test_effect_uses_the_page_host()
        {
            Vm.Activate();
            Vm.Test.IsTestEnabled = true;
            Assert.Equal(QAdvanceFeedback.Core.ShakeItEffects.TestKind.GForce, _host.Session.Active);
        }
    }
}

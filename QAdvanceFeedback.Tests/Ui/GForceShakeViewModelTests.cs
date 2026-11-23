using System.Collections.Generic;
using System.IO;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>The G-Force tab's shake block view model (final review ruling R40): same behaviour as the
    /// former code-behind - enable rules, the per-mode scale/trigger reset, the per-feeling frequency
    /// rewrite, load/save and dirty reporting.</summary>
    public class GForceShakeViewModelTests
    {
        private static GForceSettings Custom() => new GForceSettings
        {
            IntegrateWheelLockAndSlip = false,
            IntegrateSlipAngle = true,
            ShakeApplyMode = ShakeApplyMode.PerChannel,
            ShakeTriggerThresholdPercent = 17,
            ShakeFeeling = ShakeFeeling.Blending,
            ShakeFrequencyHz = 7,
            ShakeSustainPercent = 35,
            WheelLockShakeScale = 0.7,
            WheelSlipShakeScale = 0.8,
            WheelSlipAngleShakeScale = 0.9,
        };

        private static GForceShakeViewModel Loaded(GForceSettings g, List<string> edits)
        {
            var vm = new GForceShakeViewModel();
            vm.Edited += () => edits.Add("edit");
            vm.Load(g);
            return vm;
        }

        [Theory]
        [InlineData(true, true, true, true, true)]
        [InlineData(true, false, true, true, false)]
        [InlineData(false, true, true, false, true)]
        [InlineData(false, false, false, false, false)]
        public void Enable_rules_follow_the_two_switches(bool lockSlip, bool angle, bool shared, bool lockSlipScales, bool angleScale)
        {
            var vm = new GForceShakeViewModel { IntegrateWheelLockAndSlip = lockSlip, IntegrateSlipAngle = angle };
            Assert.Equal(shared, vm.SharedEnabled);
            Assert.Equal(lockSlipScales, vm.LockSlipScalesEnabled);
            Assert.Equal(angleScale, vm.AngleScaleEnabled);
        }

        [Fact]
        public void Switch_change_notifies_the_enabled_state()
        {
            var vm = new GForceShakeViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            vm.IntegrateSlipAngle = false;
            Assert.Contains(nameof(GForceShakeViewModel.SharedEnabled), changed);
            Assert.Contains(nameof(GForceShakeViewModel.AngleScaleEnabled), changed);
        }

        [Theory]
        [InlineData(ShakeApplyMode.PerChannel)]
        [InlineData(ShakeApplyMode.AllChannelsGForce)]
        [InlineData(ShakeApplyMode.AllChannelsLockSlip)]
        [InlineData(ShakeApplyMode.HigherOfGForceOrLockSlip)]
        public void Picking_a_mode_restores_that_modes_scales_and_trigger(ShakeApplyMode mode)
        {
            var edits = new List<string>();
            var g = Custom();
            g.ShakeApplyMode = mode == ShakeApplyMode.PerChannel ? ShakeApplyMode.AllChannelsGForce : ShakeApplyMode.PerChannel;
            GForceShakeViewModel vm = Loaded(g, edits);

            vm.Mode = mode;

            double scale = GForceSettings.DefaultShakeScaleFor(mode);
            Assert.Equal(scale, vm.LockScale);
            Assert.Equal(scale, vm.SlipScale);
            Assert.Equal(scale, vm.AngleScale);
            Assert.Equal(GForceSettings.DefaultShakeTriggerFor(mode), vm.Trigger);
            Assert.NotEmpty(edits);
        }

        [Fact]
        public void Picking_a_feeling_rewrites_the_frequency_and_hides_the_hold_for_blending()
        {
            var edits = new List<string>();
            var g = Custom();
            g.ShakeFeeling = ShakeFeeling.OppositePhase;
            GForceShakeViewModel vm = Loaded(g, edits);
            Assert.True(vm.SustainVisible);

            vm.Feeling = ShakeFeeling.Blending;
            Assert.Equal(GForceSettings.DefaultShakeFrequencyFor(ShakeFeeling.Blending), vm.Frequency);
            Assert.False(vm.SustainVisible);
            Assert.Equal(35, vm.Sustain);   // the hold is never rewritten
            Assert.Equal(Strings.Get("GForce.Shake.Feeling.Blending.Desc"), vm.FeelingDescription);
            Assert.NotEmpty(edits);
        }

        [Fact]
        public void Load_shows_the_saved_values_without_any_rewrite_and_is_not_an_edit()
        {
            var edits = new List<string>();
            GForceShakeViewModel vm = Loaded(Custom(), edits);

            Assert.Empty(edits);
            Assert.False(vm.IntegrateWheelLockAndSlip);
            Assert.True(vm.IntegrateSlipAngle);
            Assert.Equal(ShakeApplyMode.PerChannel, vm.Mode);
            Assert.Equal(17, vm.Trigger);
            Assert.Equal(ShakeFeeling.Blending, vm.Feeling);
            Assert.Equal(7, vm.Frequency);
            Assert.Equal(35, vm.Sustain);
            Assert.Equal(0.7, vm.LockScale);
            Assert.Equal(0.8, vm.SlipScale);
            Assert.Equal(0.9, vm.AngleScale);
            Assert.Equal(Strings.Get("GForce.Shake.Mode.PerChannel.Desc"), vm.ModeDescription);
        }

        [Fact]
        public void Save_round_trips_and_an_empty_number_keeps_the_existing_value()
        {
            GForceShakeViewModel vm = Loaded(Custom(), new List<string>());
            var target = new GForceSettings();
            vm.SaveTo(target);
            var expected = Custom();
            Assert.Equal(expected.IntegrateWheelLockAndSlip, target.IntegrateWheelLockAndSlip);
            Assert.Equal(expected.IntegrateSlipAngle, target.IntegrateSlipAngle);
            Assert.Equal(expected.ShakeApplyMode, target.ShakeApplyMode);
            Assert.Equal(expected.ShakeTriggerThresholdPercent, target.ShakeTriggerThresholdPercent);
            Assert.Equal(expected.ShakeFeeling, target.ShakeFeeling);
            Assert.Equal(expected.ShakeFrequencyHz, target.ShakeFrequencyHz);
            Assert.Equal(expected.ShakeSustainPercent, target.ShakeSustainPercent);
            Assert.Equal(expected.WheelLockShakeScale, target.WheelLockShakeScale);
            Assert.Equal(expected.WheelSlipShakeScale, target.WheelSlipShakeScale);
            Assert.Equal(expected.WheelSlipAngleShakeScale, target.WheelSlipAngleShakeScale);

            vm.Trigger = null;
            var keep = new GForceSettings { ShakeTriggerThresholdPercent = 42 };
            vm.SaveTo(keep);
            Assert.Equal(42, keep.ShakeTriggerThresholdPercent);
        }

        private static string SettingsFile(string name)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings", name));
        }

        [Fact]
        public void Driver_edits_raise_Edited_but_a_hub_write_does_not()
        {
            var edits = new List<string>();
            GForceShakeViewModel vm = Loaded(Custom(), edits);

            vm.SetIntegration(slipAngle: false, value: true);
            vm.SetIntegration(slipAngle: true, value: false);
            Assert.Empty(edits);
            Assert.True(vm.IntegrateWheelLockAndSlip);
            Assert.False(vm.IntegrateSlipAngle);
            Assert.True(vm.LockSlipScalesEnabled);
            Assert.False(vm.AngleScaleEnabled);

            vm.AngleScale = 1.1;
            Assert.Single(edits);
            vm.IntegrateSlipAngle = true;
            Assert.Equal(2, edits.Count);
        }
    }
}

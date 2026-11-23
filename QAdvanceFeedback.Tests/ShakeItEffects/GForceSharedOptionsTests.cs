using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// Lateral Feedback Direction and Integrate WheelLock and Slip are shared between the plugin
    /// settings page and the native G-Force ShakeIt effects, like the other G-Force settings
    /// (docs\shakeit-native-effects-design.md, "Panel settings").
    /// </summary>
    public class GForceSharedOptionsTests
    {
        private static SettingsHub NewHub(out QAdvanceFeedbackSettings settings, System.Action onSave = null)
        {
            var s = new QAdvanceFeedbackSettings();
            settings = s;
            return new SettingsHub(() => s, _ => onSave?.Invoke());
        }

        [Fact]
        public void Lateral_direction_is_a_shared_editable_field()
        {
            var hub = NewHub(out var s);
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            Assert.True(hub.Set(SettingField.LateralDirection, LateralDirectionMode.Reversed, SettingsOrigin.ShakeItPanel));

            Assert.Equal(LateralDirectionMode.Reversed, s.GForce.LateralDirection);
            Assert.Equal(new[] { SettingField.LateralDirection }, got.Fields);
        }

        [Fact]
        public void Integrate_wheel_lock_and_slip_is_a_shared_editable_field()
        {
            int saves = 0;
            var hub = NewHub(out var s, () => saves++);
            bool before = s.GForce.IntegrateWheelLockAndSlip;

            Assert.True(hub.Set(SettingField.IntegrateWheelLockAndSlip, !before, SettingsOrigin.ShakeItPanel));

            Assert.Equal(!before, s.GForce.IntegrateWheelLockAndSlip);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Diff_and_file_changes_include_both_options()
        {
            var hub = NewHub(out var s);
            var disk = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));
            disk.GForce.LateralDirection = LateralDirectionMode.Reversed;
            disk.GForce.IntegrateWheelLockAndSlip = !s.GForce.IntegrateWheelLockAndSlip;

            Assert.Equal(new[] { SettingField.LateralDirection, SettingField.IntegrateWheelLockAndSlip }, SettingsHub.Diff(s, disk));

            hub.NotifyFileChanged(disk);
            Assert.Equal(LateralDirectionMode.Reversed, s.GForce.LateralDirection);
            Assert.Equal(disk.GForce.IntegrateWheelLockAndSlip, s.GForce.IntegrateWheelLockAndSlip);
        }

        [Fact]
        public void Readout_shows_two_decimals_like_the_max_G_box()
        {
            CultureInfo original = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal("Fixed: 0.75G", GForceReadout.Build(false, 0.75, false, 0));
                Assert.Equal("Default: 0.75G. Auto detected: 2.34G", GForceReadout.Build(true, 0.75, true, 2.336));
                Strings.Culture = CultureInfo.GetCultureInfo("zh-Hans");
                Assert.Contains("0.75G", GForceReadout.Build(true, 0.75, false, 0));
            }
            finally { Strings.Culture = original; }
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(Path.Combine(dir.FullName, "QAdvanceFeedback"), Path.Combine(parts));
        }

        // ---- Integrate Slip Angle (docs\slip-angle-design.md section 10) ----

        [Fact]
        public void Panel_integrate_slip_angle_toggle_syncs_through_the_hub()
        {
            var host = new QAdvanceFeedback.Tests.Ui.FakePanelHost();
            var vm = new QAdvanceFeedback.Ui.ViewModels.GForceEffectViewModel(
                host, new QAdvanceFeedback.Tests.Ui.ManualTicker(), new QAdvanceFeedback.Tests.Ui.ImmediateDispatcher());
            vm.Activate();
            Assert.True(vm.IntegrateSlipAngle);   // the shipped default

            // Panel -> settings (one save, through the shared field).
            vm.IntegrateSlipAngle = false;
            Assert.False(host.Settings.GForce.IntegrateSlipAngle);
            Assert.Equal(1, host.Saves);

            // Page -> panel (a refresh is never a write).
            var changed = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            host.Settings.GForce.IntegrateSlipAngle = true;
            host.Hub.NotifyApplied(new[] { SettingField.IntegrateSlipAngle }, SettingsOrigin.PluginPage);
            Assert.True(vm.IntegrateSlipAngle);
            Assert.Contains(nameof(vm.IntegrateSlipAngle), changed);
            Assert.Equal(1, host.Saves);
        }

        [Fact]
        public void Mode_wording_names_the_angle_and_the_enum_names_stay()
        {
            CultureInfo original = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal("Higher of G-Force or wheel lock/slip/angle", Strings.Get("GForce.Shake.Mode.HigherOfBoth"));
                Assert.Equal("Integrate Slip Angle", Strings.Get("GForce.Shake.Angle.Enable"));
                Strings.Culture = CultureInfo.GetCultureInfo("zh-Hans");
                Assert.Contains("滑移角", Strings.Get("GForce.Shake.Mode.HigherOfBoth"));
            }
            finally { Strings.Culture = original; }
            // Persisted names are untouched.
            Assert.Equal("HigherOfGForceOrLockSlip", ShakeApplyMode.HigherOfGForceOrLockSlip.ToString());
            Assert.Equal("AllChannelsLockSlip", ShakeApplyMode.AllChannelsLockSlip.ToString());
        }

    }
}

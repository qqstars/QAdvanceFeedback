using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The ShakeIt panel catch-up (docs\full-mvvm-plan.md 3.4): a panel view model listens to the hub only
    /// while a view of it is on screen, so a settings change made while NO view is shown - before the first
    /// view, or after the last one closed - must be shown the moment a view is loaded again: the bound
    /// property reads the new value AND raises PropertyChanged, so the binding picks it up.
    /// Two ways a change arrives: <see cref="SettingsHub.Set"/> (another panel, or the plugin page writing
    /// one field) and a page Apply (settings written, then <see cref="SettingsHub.NotifyApplied"/>).
    /// </summary>
    public class PanelCatchUpTests
    {
        public enum Shown { Never, ThenHidden }
        public enum Via { HubSet, PageApply }

        /// <summary>One panel and one of its fields: how to build it, change the field each way, read it back.</summary>
        private sealed class Case
        {
            public Func<FakePanelHost, PanelViewModelBase> Create;
            public SettingField Field;
            public object Value;
            public Action<QAdvanceFeedbackSettings> WriteDirectly;
            public Func<PanelViewModelBase, object> Read;
            public string Property;
        }

        private static readonly ManualTicker Ticker = new ManualTicker();

        private static readonly Dictionary<string, Case> Cases = new Dictionary<string, Case>
        {
            ["LockSource"] = new Case
            {
                Create = h => new WheelChannelEffectViewModel(true, new FakeRouting(), h, Ticker, new ImmediateDispatcher()),
                Field = SettingField.LockSourceMode, Value = SourceMode.ShakeIt,
                WriteDirectly = s => s.Lock.SwitchSourceMode(SourceMode.ShakeIt, true),
                Read = vm => ((WheelChannelEffectViewModel)vm).Source, Property = nameof(WheelChannelEffectViewModel.Source),
            },
            ["SlipSource"] = new Case
            {
                Create = h => new WheelChannelEffectViewModel(false, new FakeRouting(), h, Ticker, new ImmediateDispatcher()),
                Field = SettingField.SlipSourceMode, Value = SourceMode.ShakeIt,
                WriteDirectly = s => s.Slip.SwitchSourceMode(SourceMode.ShakeIt, false),
                Read = vm => ((WheelChannelEffectViewModel)vm).Source, Property = nameof(WheelChannelEffectViewModel.Source),
            },
            ["GForceLateralMax"] = new Case
            {
                Create = h => new GForceEffectViewModel(h, Ticker, new ImmediateDispatcher()),
                Field = SettingField.FixedLatMaxG, Value = 1.7,
                WriteDirectly = s => s.GForce.FixedLatMaxG = 1.7,
                Read = vm => ((GForceEffectViewModel)vm).Axes[2].Max, Property = null,   // a child view model's property
            },
            ["GForceLateralDirection"] = new Case
            {
                Create = h => new GForceEffectViewModel(h, Ticker, new ImmediateDispatcher()),
                Field = SettingField.LateralDirection, Value = LateralDirectionMode.Reversed,
                WriteDirectly = s => s.GForce.LateralDirection = LateralDirectionMode.Reversed,
                Read = vm => ((GForceEffectViewModel)vm).LateralDirection, Property = nameof(GForceEffectViewModel.LateralDirection),
            },
            ["SlipAngleTriggerBy"] = new Case
            {
                Create = h => new SlipAngleEffectViewModel(h, new FakeSlipAngleEffectHost(), Ticker, new ImmediateDispatcher()),
                Field = SettingField.SlipAngleTriggerBy, Value = TriggerBy.Brake,
                WriteDirectly = s => s.SlipAngle.Gate.TriggerBy = TriggerBy.Brake,
                Read = vm => ((SlipAngleEffectViewModel)vm).TriggerBy, Property = nameof(SlipAngleEffectViewModel.TriggerBy),
            },
            ["SlipAngleBrakeThreshold"] = new Case
            {
                Create = h => new SlipAngleEffectViewModel(h, new FakeSlipAngleEffectHost(), Ticker, new ImmediateDispatcher()),
                Field = SettingField.SlipAngleBrakeThreshold, Value = 42.0,
                WriteDirectly = s => s.SlipAngle.Gate.BrakeThreshold = 42.0,
                Read = vm => ((SlipAngleEffectViewModel)vm).BrakeThreshold, Property = nameof(SlipAngleEffectViewModel.BrakeThreshold),
            },
            ["ClutchBitePoint"] = new Case
            {
                Create = h => new ClutchBiteViewModel(h, new FakeClutchHost(), Ticker, new ImmediateDispatcher()),
                Field = SettingField.ClutchBitePoint, Value = 63.0,
                WriteDirectly = s => s.Clutch.BitePoint = 63.0,
                Read = vm => ((ClutchBiteViewModel)vm).BitePoint, Property = nameof(ClutchBiteViewModel.BitePoint),
            },
            ["ClutchMode"] = new Case
            {
                Create = h => new ClutchBiteViewModel(h, new FakeClutchHost(), Ticker, new ImmediateDispatcher()),
                Field = SettingField.ClutchBiteMode, Value = ClutchBiteMode.Manual,
                WriteDirectly = s => s.Clutch.Mode = ClutchBiteMode.Manual,
                Read = vm => ((ClutchBiteViewModel)vm).Mode, Property = nameof(ClutchBiteViewModel.Mode),
            },
        };

        public static IEnumerable<object[]> All()
        {
            foreach (string name in Cases.Keys)
                foreach (Shown shown in new[] { Shown.Never, Shown.ThenHidden })
                    foreach (Via via in new[] { Via.HubSet, Via.PageApply })
                        yield return new object[] { name, shown, via };
        }

        [Theory]
        [MemberData(nameof(All))]
        public void A_change_made_while_the_panel_is_not_shown_is_shown_when_it_opens(string name, Shown shown, Via via)
        {
            Case c = Cases[name];
            var host = new FakePanelHost();
            if (name == "ClutchMode") host.Settings.Clutch.Mode = ClutchBiteMode.Auto;
            PanelViewModelBase vm = c.Create(host);
            if (shown == Shown.ThenHidden)
            {
                vm.Activate();
                Assert.NotEqual(c.Value, c.Read(vm));
                vm.Deactivate();
                Assert.False(vm.IsActive);
            }

            if (via == Via.HubSet)
                Assert.True(host.Hub.Set(c.Field, c.Value, SettingsOrigin.ShakeItPanel));
            else
            {
                c.WriteDirectly(host.Settings);
                host.Hub.NotifyApplied(new[] { c.Field }, SettingsOrigin.PluginPage);
            }
            Assert.Equal(c.Value, SettingsHub.Read(host.Settings, c.Field));
            // Not shown: the panel does not listen, so it still holds the old value (the catch-up is needed).
            Assert.NotEqual(c.Value, c.Read(vm));

            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            vm.Activate();

            Assert.Equal(c.Value, c.Read(vm));
            if (c.Property != null) Assert.Contains(c.Property, raised);
        }

        [Fact]
        public void The_G_Force_axis_raises_its_own_change_when_it_catches_up()
        {
            var host = new FakePanelHost();
            var vm = new GForceEffectViewModel(host, Ticker, new ImmediateDispatcher());
            host.Hub.Set(SettingField.FixedLatMaxG, 1.7, SettingsOrigin.PluginPage);

            var raised = new List<string>();
            vm.Axes[2].PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            vm.Activate();

            Assert.Contains(nameof(GForceAxisViewModel.Max), raised);
        }

        [Fact]
        public void A_panel_whose_settings_were_replaced_while_hidden_shows_the_new_settings()
        {
            var host = new FakePanelHost();
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), host, Ticker, new ImmediateDispatcher());
            vm.Activate();
            vm.Deactivate();

            var fresh = new QAdvanceFeedbackSettings();
            fresh.Lock.SwitchSourceMode(SourceMode.ShakeIt, true);
            host.Settings = fresh;
            vm.Activate();

            Assert.Equal(SourceMode.ShakeIt, vm.Source);
        }

        [Fact]
        public void A_view_replaced_in_one_layout_pass_keeps_the_panel_listening()
        {
            // ShakeIt builds a NEW view each time: the old view's Unloaded and the new one's Loaded arrive
            // in one layout pass. The panel stays subscribed, so a change in between is not missed either.
            var host = new FakePanelHost();
            var ui = new QueuedDispatcher();
            var vm = new GForceEffectViewModel(host, Ticker, ui);
            vm.Activate();
            vm.Deactivate();                 // old view unloaded (stop deferred to after layout)
            vm.Activate();                   // new view loaded
            ui.Flush();
            Assert.True(vm.IsActive);

            host.Hub.Set(SettingField.LateralDirection, LateralDirectionMode.Reversed, SettingsOrigin.PluginPage);
            Assert.Equal(LateralDirectionMode.Reversed, vm.LateralDirection);
        }
    }
}

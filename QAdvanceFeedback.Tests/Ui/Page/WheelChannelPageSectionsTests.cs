using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>The Wheel Lock / Wheel Slip tabs' Trigger threshold, Custom cold-start reference and
    /// Aggregation sections, and the tab view model that composes them.</summary>
    public class WheelChannelPageSectionsTests
    {
        private readonly WheelChannelPageSourcesTests _f = new WheelChannelPageSourcesTests();
        private WheelChannelPageTestHost Host => _f.Host;
        private SettingsPageViewModel Page => _f.Page;
        private QAdvanceFeedbackSettings Draft => _f.Draft;
        private WheelChannelPageViewModel Tab(bool isLock) => _f.Tab(isLock);

        // ------------------------------------------------------------------ tab shell

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_tab_composes_every_section_for_its_channel(bool isLock)
        {
            WheelChannelPageViewModel tab = Tab(isLock);
            Assert.Equal(isLock, tab.IsLock);
            Assert.NotNull(tab.Trigger);
            Assert.NotNull(tab.Sources);
            Assert.NotNull(tab.CustomReference);
            Assert.NotNull(tab.Aggregation);
            Assert.NotNull(tab.KeyData);
            Assert.NotNull(tab.Curve);
            Assert.NotNull(tab.Pulse);
            Assert.Equal(isLock, tab.Sources.IsLock);
        }

        // ------------------------------------------------------------------ trigger threshold

        [Fact]
        public void Lock_shows_brake_and_sensibility_Slip_shows_brake_and_throttle()
        {
            TriggerThresholdViewModel l = Tab(true).Trigger, s = Tab(false).Trigger;
            Assert.True(l.ShowSensibility); Assert.False(l.ShowThrottle);
            Assert.False(s.ShowSensibility); Assert.True(s.ShowThrottle);
            Assert.Equal(Strings.Get("Group.TriggerThreshold"), l.Header);
            Assert.Equal(Strings.Get("Sources.Threshold.LockBrake"), l.BrakeLabel);
            Assert.Equal(Strings.Get("Sources.Threshold.Lock.Note"), l.Note);
            Assert.Equal(Strings.Get("Sources.Threshold.Lock.Note"), l.BrakeTooltip);
            Assert.Equal(Strings.Get("Sources.Threshold.SlipBrake"), s.BrakeLabel);
            Assert.Equal(Strings.Get("Sources.Threshold.SlipThrottle"), s.ThrottleLabel);
            Assert.Equal(Strings.Get("Sources.Threshold.Slip.Note"), s.Note);
            Assert.Equal(Strings.Get("Sources.Threshold.Slip.Note"), s.BrakeTooltip);
            Assert.Equal(Strings.Get("Sources.Threshold.LockSensibility"), l.SensibilityLabel);
            Assert.Equal(Strings.Get("Sources.Threshold.LockSensibility.Note"), l.SensibilityNote);
        }

        [Fact]
        public void Thresholds_read_and_write_the_draft_clamped_and_apply_on_Apply()
        {
            TriggerThresholdViewModel s = Tab(false).Trigger;
            Assert.Equal(Draft.Slip.BrakeThresholdPercent, s.BrakeThreshold);
            s.ThrottleThreshold = 55;
            s.BrakeThreshold = 150;   // clamped 0-100 by the settings
            Assert.Equal(100, s.BrakeThreshold);
            Assert.Equal(55, Draft.Slip.ThrottleThresholdPercent);
            Assert.True(Page.IsDirty);
            Page.ApplyCommand.Execute(null);
            Assert.Equal(55, Host.Settings.Slip.ThrottleThresholdPercent);

            TriggerThresholdViewModel l = Tab(true).Trigger;
            l.LockSensibility = 65;
            Assert.Equal(65, Draft.Lock.LockSensibility);
        }

        [Fact]
        public void A_cleared_box_keeps_the_value()
        {
            // The old save read "Value ?? current".
            TriggerThresholdViewModel l = Tab(true).Trigger;
            double before = Draft.Lock.BrakeThresholdPercent;
            l.BrakeThreshold = null;
            Assert.Equal(before, l.BrakeThreshold);
            Assert.False(Page.IsDirty);
        }

        [Fact]
        public void A_ShakeIt_side_or_file_change_reaches_the_threshold()
        {
            TriggerThresholdViewModel l = Tab(true).Trigger;
            Host.Settings.Lock.BrakeThresholdPercent = 33;
            Page.Activate();   // catch-up adopt
            Assert.Equal(33, l.BrakeThreshold);
        }

        // ------------------------------------------------------------------ aggregation

        [Fact]
        public void Aggregation_reads_and_writes_the_draft_and_only_Slip_has_the_floor()
        {
            AggregationViewModel l = Tab(true).Aggregation, s = Tab(false).Aggregation;
            Assert.False(l.ShowFloor);
            Assert.True(s.ShowFloor);
            Assert.Equal(Draft.Lock.AggregationWFront, l.WFront);
            l.WMax = 0.8; l.WMin = 0.3; l.WFront = 0.7; l.WRear = 0.4;
            s.SlipFloorFactor = 2;   // capped at 1
            Assert.Equal(1, s.SlipFloorFactor);
            s.WRear = -1;            // clamped >= 0
            Assert.Equal(0, s.WRear);
            Page.ApplyCommand.Execute(null);
            Assert.Equal(0.8, Host.Settings.Lock.AggregationWMax);
            Assert.Equal(0.3, Host.Settings.Lock.AggregationWMin);
            Assert.Equal(0.7, Host.Settings.Lock.AggregationWFront);
            Assert.Equal(0.4, Host.Settings.Lock.AggregationWRear);
            Assert.Equal(1, Host.Settings.Slip.SlipFloorFactor);
        }

        [Fact]
        public void Aggregation_texts_differ_per_channel()
        {
            AggregationViewModel l = Tab(true).Aggregation, s = Tab(false).Aggregation;
            Assert.Equal(Strings.Get("Group.Aggregation"), l.Header);
            Assert.Equal(Strings.Get("Aggregation.Lock.Note"), l.Note);
            Assert.Equal(Strings.Get("Aggregation.Slip.Note"), s.Note);
            Assert.Equal(Strings.Get("Aggregation.SideHelp.Lock"), l.SideHelp);
            Assert.Equal(Strings.Get("Aggregation.SideHelp.Slip"), s.SideHelp);
            Assert.Equal(Strings.Get("Aggregation.AxleHelp"), s.AxleHelp);
            Assert.Equal(Strings.Get("Aggregation.FloorFactor.Label"), s.FloorLabel);
            Assert.Equal(Strings.Get("Aggregation.FloorFactor.Note"), s.FloorNote);
            Assert.Equal(Strings.Get("Aggregation.WMax.Label"), s.WMaxLabel);
        }

        // ------------------------------------------------------------------ custom cold-start reference

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_reference_shows_only_on_Custom(bool isLock)
        {
            CustomReferenceViewModel c = Tab(isLock).CustomReference;
            Assert.False(c.IsVisible);
            Tab(isLock).Sources.Mode = SourceMode.Custom;
            Assert.True(c.IsVisible);
            Tab(isLock).Sources.Mode = SourceMode.Viper;
            Assert.False(c.IsVisible);
            Assert.Equal(Strings.Get("Sources.CustomRef.Header"), c.Header);
            Assert.Equal(Strings.Get("Sources.CustomRef.SMax"), c.SMaxLabel);
        }

        [Fact]
        public void Entering_Custom_shows_the_reference_seeded_from_the_source_left()
        {
            Tab(false).Sources.Mode = SourceMode.Viper;
            Tab(false).Sources.Mode = SourceMode.Custom;
            Draft.KeyDataPointDefaults.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, false, out double a, out double b, out double c);
            CustomReferenceViewModel r = Tab(false).CustomReference;
            Assert.Equal(a, r.SMax); Assert.Equal(b, r.S90); Assert.Equal(c, r.S75);
        }

        [Fact]
        public void A_reference_never_reads_dashes_Raw_stands_in_when_nothing_is_stored()
        {
            // Owner, 2026-09-29: "the cold-start reference should ALWAYS have particular value".
            Host.Settings.Lock.SourceMode = SourceMode.Custom;
            Host.Settings.KeyDataPointDefaults.LockCustom = null;
            CustomReferenceViewModel r = Tab(true).CustomReference;
            Host.Settings.KeyDataPointDefaults.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, true, out double a, out double b, out double c);
            Assert.True(r.IsVisible);
            Assert.Equal(a, r.SMax); Assert.Equal(b, r.S90); Assert.Equal(c, r.S75);
            Assert.False(Page.IsDirty);   // the stand-in is shown, not written
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Apply_saves_the_shown_default_reference_when_Custom_has_none_stored(bool isLock)
        {
            // Owner, 2026-11-20: the 1.1.0 rule - the editors' stand-in is saved by the next Apply.
            (isLock ? Host.Settings.Lock : Host.Settings.Slip).SourceMode = SourceMode.Custom;
            if (isLock) Host.Settings.KeyDataPointDefaults.LockCustom = null;
            else Host.Settings.KeyDataPointDefaults.SlipCustom = null;
            CustomReferenceViewModel r = Tab(isLock).CustomReference;
            double? sMax = r.SMax, s90 = r.S90, s75 = r.S75;
            Assert.False(Page.IsDirty);

            Tab(isLock).Trigger.BrakeThreshold = 42;   // any other edit arms Apply
            Page.ApplyCommand.Execute(null);

            KeyDataPointDefaultSet saved = isLock ? Host.Settings.KeyDataPointDefaults.LockCustom : Host.Settings.KeyDataPointDefaults.SlipCustom;
            Assert.NotNull(saved);
            Assert.Equal(sMax, saved.SMax); Assert.Equal(s90, saved.S90); Assert.Equal(s75, saved.S75);
            Assert.False(Page.IsDirty);
        }

        [Fact]
        public void Apply_does_not_save_a_default_reference_while_the_channel_is_not_on_Custom()
        {
            Host.Settings.Lock.SourceMode = SourceMode.Viper;
            Host.Settings.KeyDataPointDefaults.LockCustom = null;
            Tab(true).Trigger.BrakeThreshold = 42;
            Page.ApplyCommand.Execute(null);
            Assert.Null(Host.Settings.KeyDataPointDefaults.LockCustom);
        }

        [Fact]
        public void A_reference_edit_writes_the_draft_capped_at_the_hard_limit_and_applies_on_Apply()
        {
            Tab(true).Sources.Mode = SourceMode.Custom;
            CustomReferenceViewModel r = Tab(true).CustomReference;
            r.SMax = 100;   // the hard SMax limit is 95
            Assert.Equal(KeyDataPointSettings.MaxSMax, r.SMax);
            Assert.Equal(KeyDataPointSettings.MaxSMax, Draft.KeyDataPointDefaults.LockCustom.SMax);
            Page.ApplyCommand.Execute(null);
            Assert.Equal(KeyDataPointSettings.MaxSMax, Host.Settings.KeyDataPointDefaults.LockCustom.SMax);
        }

        [Fact]
        public void An_unusable_triple_is_shown_but_not_stored()
        {
            // The old save kept the stored set when the editors did not hold a usable one.
            Tab(true).Sources.Mode = SourceMode.Custom;
            CustomReferenceViewModel r = Tab(true).CustomReference;
            double stored = Draft.KeyDataPointDefaults.LockCustom.S90;
            r.S90 = 99;   // above SMax: not a valid ordering
            Assert.Equal(stored, Draft.KeyDataPointDefaults.LockCustom.S90);
            r.S90 = stored - 1;
            Assert.Equal(stored - 1, Draft.KeyDataPointDefaults.LockCustom.S90);
        }
    }
}

using System;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Wheel Lock / Wheel Slip tab's "Trigger threshold" section (its own section ABOVE Sources - owner
    /// restructure, docs\lock-and-animation-report.md): the brake threshold on both channels, the throttle
    /// threshold on Slip only, and Lock's sensibility (SimHub's own <c>WheelsLockContainer.LockSensibility</c>:
    /// name, range 0-100, default 50) on Lock only. Each value reads and writes the draft; a cleared box keeps
    /// the value (the old save's <c>Value ?? current</c>); the settings clamp to 0-100.
    /// </summary>
    public sealed class TriggerThresholdViewModel : PageSectionViewModel
    {
        public TriggerThresholdViewModel(SettingsPageContext ctx, bool isLock) : base(ctx)
        {
            IsLock = isLock;
            Header = Strings.Get("Group.TriggerThreshold");
            BrakeLabel = Strings.Get(isLock ? "Sources.Threshold.LockBrake" : "Sources.Threshold.SlipBrake");
            ThrottleLabel = Strings.Get("Sources.Threshold.SlipThrottle");
            Note = Strings.Get(isLock ? "Sources.Threshold.Lock.Note" : "Sources.Threshold.Slip.Note");
            SensibilityLabel = Strings.Get("Sources.Threshold.LockSensibility");
            SensibilityNote = Strings.Get("Sources.Threshold.LockSensibility.Note");
        }

        public bool IsLock { get; }
        public bool ShowThrottle => !IsLock;
        public bool ShowSensibility => IsLock;

        public string Header { get; }
        public string BrakeLabel { get; }
        public string ThrottleLabel { get; }
        /// <summary>The channel's note, also the tooltip of its threshold labels and editors.</summary>
        public string Note { get; }
        public string BrakeTooltip => Note;
        public string SensibilityLabel { get; }
        /// <summary>Also the sensibility label's and editor's tooltip.</summary>
        public string SensibilityNote { get; }

        private WheelChannelSettings Channel => IsLock ? S.Lock : S.Slip;

        public double? BrakeThreshold
        {
            get => Channel?.BrakeThresholdPercent;
            set => SetClamped(value, () => Channel.BrakeThresholdPercent, v => Channel.BrakeThresholdPercent = v);
        }

        /// <summary>Slip only (Lock never reads it).</summary>
        public double? ThrottleThreshold
        {
            get => Channel?.ThrottleThresholdPercent;
            set => SetClamped(value, () => Channel.ThrottleThresholdPercent, v => Channel.ThrottleThresholdPercent = v);
        }

        /// <summary>Lock only (Slip never reads it).</summary>
        public double? LockSensibility
        {
            get => Channel?.LockSensibility;
            set => SetClamped(value, () => Channel.LockSensibility, v => Channel.LockSensibility = v);
        }

        private void SetClamped(double? value, Func<double> read, Action<double> write,
            [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (Channel == null) return;
            if (!value.HasValue) { OnPropertyChanged(name); return; }
            Edit(read(), value.Value, write, name);
            if (read() != value.Value) OnPropertyChanged(name);   // the setter clamped it
        }
    }
}

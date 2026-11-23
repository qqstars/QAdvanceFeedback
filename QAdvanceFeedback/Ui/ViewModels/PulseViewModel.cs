using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Pulse group of a Wheel Lock / Wheel Slip tab (old SettingsControl LoadChannel / SaveChannel pulse
    /// lines): pulse instead of holding flat at maximum, the half-cycle gap and the minimum value. Draft
    /// edits (Apply). An emptied editor leaves the value unchanged (the old <c>Value ?? current</c>); the
    /// gap shows what <see cref="PulseSettings.GapMs"/> stores (never below the 5 Hz floor).
    /// </summary>
    public sealed class PulseViewModel : PageSectionViewModel
    {
        public PulseViewModel(SettingsPageContext ctx, bool isLock) : base(ctx) { IsLock = isLock; }

        public bool IsLock { get; }

        private PulseSettings Pulse
        {
            get
            {
                WheelChannelSettings ch = IsLock ? S.Lock : S.Slip;
                return ch.Pulse ?? (ch.Pulse = new PulseSettings());
            }
        }

        public bool Enabled
        {
            get => Pulse.Enabled;
            set => Edit(Pulse.Enabled, value, v => Pulse.Enabled = v);
        }

        public double? GapMs
        {
            get => Pulse.GapMs;
            set
            {
                if (!value.HasValue) { OnPropertyChanged(nameof(GapMs)); return; }
                Edit(Pulse.GapMs, value.Value, v => Pulse.GapMs = v);
                OnPropertyChanged(nameof(GapMs));   // the floor may have moved it
            }
        }

        public double? MinValue
        {
            get => Pulse.MinValue;
            set
            {
                if (!value.HasValue) { OnPropertyChanged(nameof(MinValue)); return; }
                Edit(Pulse.MinValue, value.Value, v => Pulse.MinValue = v);
                OnPropertyChanged(nameof(MinValue));
            }
        }
    }
}

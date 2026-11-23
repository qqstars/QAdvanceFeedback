using System;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Wheel Lock / Wheel Slip tab's "Wheel combination" section (docs\aggregation-report.md): the
    /// Max/Min axle blend and the Front/Rear weights of the channel, plus the slip floor on Wheel Slip only
    /// (Wheel Lock has no equivalent control). Every value reads and writes the draft; a cleared box keeps the
    /// value; the settings clamp (weights &gt;= 0, the floor 0-1).
    /// </summary>
    public sealed class AggregationViewModel : PageSectionViewModel
    {
        public AggregationViewModel(SettingsPageContext ctx, bool isLock) : base(ctx)
        {
            IsLock = isLock;
            Header = Strings.Get("Group.Aggregation");
            Note = Strings.Get(isLock ? "Aggregation.Lock.Note" : "Aggregation.Slip.Note");
            WMaxLabel = Strings.Get("Aggregation.WMax.Label");
            WMinLabel = Strings.Get("Aggregation.WMin.Label");
            WFrontLabel = Strings.Get("Aggregation.WFront.Label");
            WRearLabel = Strings.Get("Aggregation.WRear.Label");
            AxleHelp = Strings.Get("Aggregation.AxleHelp");
            SideHelp = Strings.Get(isLock ? "Aggregation.SideHelp.Lock" : "Aggregation.SideHelp.Slip");
            FloorLabel = Strings.Get("Aggregation.FloorFactor.Label");
            FloorNote = Strings.Get("Aggregation.FloorFactor.Note");
        }

        public bool IsLock { get; }
        public bool IsSlip => !IsLock;
        /// <summary>The slip floor (Wheel Slip only).</summary>
        public bool ShowFloor => !IsLock;

        public string Header { get; }
        public string Note { get; }
        public string WMaxLabel { get; }
        public string WMinLabel { get; }
        public string WFrontLabel { get; }
        public string WRearLabel { get; }
        public string AxleHelp { get; }
        public string SideHelp { get; }
        public string FloorLabel { get; }
        /// <summary>Also the floor label's and editor's tooltip.</summary>
        public string FloorNote { get; }

        private WheelChannelSettings Channel => IsLock ? S.Lock : S.Slip;

        public double? WMax
        {
            get => Channel?.AggregationWMax;
            set => SetClamped(value, () => Channel.AggregationWMax, v => Channel.AggregationWMax = v);
        }

        public double? WMin
        {
            get => Channel?.AggregationWMin;
            set => SetClamped(value, () => Channel.AggregationWMin, v => Channel.AggregationWMin = v);
        }

        public double? WFront
        {
            get => Channel?.AggregationWFront;
            set => SetClamped(value, () => Channel.AggregationWFront, v => Channel.AggregationWFront = v);
        }

        public double? WRear
        {
            get => Channel?.AggregationWRear;
            set => SetClamped(value, () => Channel.AggregationWRear, v => Channel.AggregationWRear = v);
        }

        public double? SlipFloorFactor
        {
            get => Channel?.SlipFloorFactor;
            set => SetClamped(value, () => Channel.SlipFloorFactor, v => Channel.SlipFloorFactor = v);
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

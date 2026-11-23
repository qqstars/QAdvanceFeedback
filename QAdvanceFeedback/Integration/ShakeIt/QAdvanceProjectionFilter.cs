using System.Windows.Controls;
using Newtonsoft.Json;
using QAdvanceFeedback.Ui.ViewModels;
using QAdvanceFeedbackViews;
using SimHub.Plugins.DataPlugins.ShakeItV3.Filters;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// The response "filter" of the native WheelLock/WheelSlip effects. The plugin has already applied
    /// its own projection curve, so the output is the input unchanged (0..1 → 0..100). It only exists
    /// to put the READ-ONLY "Source value -> Projected value" graph - with the Test Effect balls - in
    /// ShakeIt's response-curve slot: no parameter editor, and the graph cannot be edited from ShakeIt.
    /// </summary>
    public sealed class QAdvanceProjectionFilter : FilterBase
    {
        // Parameterless for ShakeIt's JSON round-trip; the owning container sets the channel.
        public QAdvanceProjectionFilter() { }

        public QAdvanceProjectionFilter(bool isLock) { IsLock = isLock; }

        [JsonIgnore]
        public bool IsLock { get; set; }

        /// <summary>The container this filter belongs to: its view model feeds the graph, so the graph
        /// shows the same key data, routing and Test Effect balls as the effect's panel.</summary>
        [JsonIgnore]
        internal QAdvanceFeedbackWheelChannelContainerBase Owner { get; set; }

        public override double GetOutput(double input, bool simulation = false)
        {
            if (double.IsNaN(input) || input <= 0) return 0;
            return input >= 1 ? 100.0 : input * 100.0;
        }

        // A new control per request (like ShakeIt's own default): one WPF element cannot sit in two
        // editors at once. Without an owner the graph shows the curve only.
        [JsonIgnore]
        public override UserControl ResponseCurveEditor =>
            new GraphView(Owner?.ViewModel.Graph ?? new GraphViewModel(IsLock, PluginPanelHost.Default));

        // No editable parameters.
        protected override UserControl GetEditControl() => new UserControl();
    }

    /// <summary>
    /// The Slip Angle counterpart of <see cref="QAdvanceProjectionFilter"/> (docs\slip-angle-design.md
    /// 9.10): the same pass-through (the plugin already projected the value), with the READ-ONLY
    /// "Source (Raw.All, degrees, 0 -> End) -> Projected" graph in ShakeIt's response-curve slot.
    /// </summary>
    public sealed class QAdvanceSlipAngleProjectionFilter : FilterBase
    {
        // Parameterless for ShakeIt's JSON round-trip; the owning container sets itself.
        public QAdvanceSlipAngleProjectionFilter() { }

        /// <summary>The container this filter belongs to: its view model feeds the graph, so the graph
        /// shows the same key data as the effect's panel.</summary>
        [JsonIgnore]
        internal QAdvanceFeedbackSlipAngleContainer Owner { get; set; }

        public override double GetOutput(double input, bool simulation = false)
        {
            if (double.IsNaN(input) || input <= 0) return 0;
            return input >= 1 ? 100.0 : input * 100.0;
        }

        // A new control per request: one WPF element cannot sit in two editors at once. Without an owner
        // there is nothing to draw.
        [JsonIgnore]
        public override UserControl ResponseCurveEditor =>
            Owner != null ? new SlipAngleGraphView(Owner.ViewModel) : new UserControl();

        // No editable parameters.
        protected override UserControl GetEditControl() => new UserControl();
    }
}

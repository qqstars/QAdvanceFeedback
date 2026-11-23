using System.Collections.Generic;
using System.Windows.Controls;
using GameReaderCommon;
using GameReaderCommon.Enums;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Ui.ViewModels;
using QAdvanceFeedbackViews;
using SimHub.Plugins.DataPlugins.ShakeItV3.Calibration;
using SimHub.Plugins.DataPlugins.ShakeItV3.Effects;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers.Attributes;
using SimHub.Plugins.DataPlugins.ShakeItV3.Filters;
using SimHub.Plugins.DataPlugins.ShakeItV3.Outputs.Audio;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// Native ShakeIt effect for the Slip Angle (docs\slip-angle-design.md 9.10): ONE placement (no
    /// routing), playing exactly the published <c>SlipAngle.Projected.All</c> (0-100 -> 0..1) as captured on
    /// the data thread - 0 once that frame is older than 500 ms (<see cref="SlipAngleEffectOutput"/>). Never
    /// touches the learner. The curve slot shows the read-only degrees -> projected graph
    /// (<see cref="QAdvanceSlipAngleProjectionFilter"/>); the panel is <see cref="SlipAngleEffectView"/>.
    /// <para/>
    /// SimHub saves the class name as ContainerType and keys channel maps by it: frozen.
    /// The attribute's English name carries no "QAdvanceFeedback" prefix: the effect sits under the
    /// "QAdvanceFeedback" category (owner, 2026-11-18; see <see cref="NativeEffects"/>).
    /// </summary>
    [ShakeItContainerMetadata(206, "Slip Angle",
        "Projected tyre slip angle (the same value as SlipAngle.Projected.All): builds through the grip window and peaks past Max, eased down in a held drift.",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackSlipAngleContainer : EffectsContainerBase<ToneOutput>
    {
        private readonly QAdvanceFeedbackEffect _effect;
        private readonly QAdvanceSlipAngleProjectionFilter _filter;

        public QAdvanceFeedbackSlipAngleContainer()
        {
            _effect = new QAdvanceFeedbackEffect("All", FFBPlacement.All, ValueFor);
            _filter = new QAdvanceSlipAngleProjectionFilter { Owner = this };
            ShakeItEffectSupport.ApplyIdentity(this, NativeEffect.SlipAngle);
        }

        protected override bool AllowEffectsAggregations => false;

        protected override IEnumerable<EffectBase> AvailableEffects
        {
            get { yield return _effect; }
        }

        public override FilterBase Filter => _filter;

        // The description belongs to the "Add effect" list only (ShakeItEffectSupport.ClearBuiltInDescription).
        public override void LoadDefaultSettings()
        {
            base.LoadDefaultSettings();
            ShakeItEffectSupport.ClearBuiltInDescription(this, NativeEffect.SlipAngle);
        }

        [System.Runtime.Serialization.OnDeserialized]
        internal void OnDeserializedClearDescription(System.Runtime.Serialization.StreamingContext context) =>
            ShakeItEffectSupport.ClearBuiltInDescription(this, NativeEffect.SlipAngle);

        // For the Slip Angle Test Effect (docs\slip-angle-design.md 9.11) to play with no game running; the
        // gate below keeps every other out-of-game moment silent, exactly like ShakeIt's own in-game gate.
        [JsonIgnore]
        public override bool IsAllowedOutOfGame => true;

        public override void SetAudioDefaults(ToneOutput output) => ShakeItEffectSupport.Apply(output, ToneDefaults.SlipAngle);

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            _gateOpen = TestAwareOutputGate.Allows(
                ShakeItEffectSupport.Plugin?.TestSlipAngleActive == true,
                data?.GameRunning == true, data?.GamePaused == true, data?.GameInMenu == true,
                data?.NewData?.Spectating == true);
            // Every QAdvanceFeedback effect stamps the ShakeIt-source heartbeat and feeds the built-in
            // values (docs\shakeit-source-design.md §3.1).
            if (IsEnabled && parentEnabled) ShakeItBuiltInTap.Sample(data, calibratedFeedback);
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        // Set each ShakeIt frame, before the effect is evaluated (same thread).
        private bool _gateOpen;

        /// <summary>The effect's view model, shared by its edit panel and its curve-slot graph. Not saved.
        /// Created on first use, on the UI thread.</summary>
        [JsonIgnore]
        internal SlipAngleEffectViewModel ViewModel =>
            _viewModel ?? (_viewModel = new SlipAngleEffectViewModel(PluginPanelHost.Default, PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher()));
        private SlipAngleEffectViewModel _viewModel;

        protected override UserControl GetEditControl() => new SlipAngleEffectView(ViewModel);

        private double ValueFor()
        {
            if (!_gateOpen) return 0.0;
            return SlipAngleEffectOutput.Value0100(ShakeItEffectSupport.Plugin?.LatestSlipAngleOutput, FrameSnapshot.NowTicks());
        }
    }
}

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
    /// Native ShakeIt effect for the Clutch Bite (docs\clutch-bite-design.md): one channel, playing
    /// exactly the published <c>QAdvanceFeedback.Clutch.Bite.Output</c> (live in a game session, or the
    /// Clutch Test Effect). Its panel is the same view as the plugin page's Clutch tab.
    /// <para/>
    /// SimHub saves the class name as ContainerType and keys channel maps by it: frozen.
    /// </summary>
    [ShakeItContainerMetadata(205, "Clutch Bite",
        "Clutch bite feel (the same value as Clutch.Bite.Output): strongest at the bite point, learned per car.",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackClutchBiteContainer : EffectsContainerBase<ToneOutput>
    {
        private readonly QAdvanceFeedbackEffect _effect;

        public QAdvanceFeedbackClutchBiteContainer()
        {
            _effect = new QAdvanceFeedbackEffect("All", FFBPlacement.All, ValueFor);
            ShakeItEffectSupport.ApplyIdentity(this, NativeEffect.ClutchBite);
        }

        protected override bool AllowEffectsAggregations => false;

        protected override IEnumerable<EffectBase> AvailableEffects
        {
            get { yield return _effect; }
        }

        public override FilterBase Filter => null;

        // The description belongs to the "Add effect" list only (ShakeItEffectSupport.ClearBuiltInDescription).
        public override void LoadDefaultSettings()
        {
            base.LoadDefaultSettings();
            ShakeItEffectSupport.ClearBuiltInDescription(this, NativeEffect.ClutchBite);
        }

        [System.Runtime.Serialization.OnDeserialized]
        internal void OnDeserializedClearDescription(System.Runtime.Serialization.StreamingContext context) =>
            ShakeItEffectSupport.ClearBuiltInDescription(this, NativeEffect.ClutchBite);

        // Lets the Clutch Test Effect play with no game running; the gate keeps every other
        // out-of-game moment silent.
        [JsonIgnore]
        public override bool IsAllowedOutOfGame => true;

        public override void SetAudioDefaults(ToneOutput output) => ShakeItEffectSupport.Apply(output, ToneDefaults.Clutch);

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            _gateOpen = TestAwareOutputGate.Allows(
                ShakeItEffectSupport.Plugin?.TestClutchActive == true,
                data?.GameRunning == true, data?.GamePaused == true, data?.GameInMenu == true,
                data?.NewData?.Spectating == true);
            // Every QAdvanceFeedback effect stamps the ShakeIt-source heartbeat and feeds the built-in
            // values (docs\shakeit-source-design.md §3.1).
            if (IsEnabled && parentEnabled) ShakeItBuiltInTap.Sample(data, calibratedFeedback);
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        // Set each ShakeIt frame, before the effect is evaluated (same thread).
        private bool _gateOpen;

        /// <summary>The panel's view model (not saved), created on first use on the UI thread.</summary>
        [JsonIgnore]
        internal ClutchBiteViewModel ViewModel =>
            _viewModel ?? (_viewModel = new ClutchBiteViewModel(PluginPanelHost.Default, PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher()));
        private ClutchBiteViewModel _viewModel;

        protected override UserControl GetEditControl() => new ClutchBiteView(ViewModel);

        private double ValueFor()
        {
            if (!_gateOpen) return 0.0;
            FrameSnapshot<double?> snapshot = ShakeItEffectSupport.Plugin?.LatestClutch;
            if (snapshot == null || !snapshot.IsFresh(FrameSnapshot.NowTicks(), FrameSnapshot.StaleTicks)) return 0.0;
            return snapshot.Value ?? 0.0;
        }
    }
}

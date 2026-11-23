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
    /// Native ShakeIt effect for one G-Force pad pair (docs\shakeit-native-effects-design.md): two
    /// outputs, Left and Right, carrying the same values as the matching GForce.* properties. One
    /// effect per pair so each pair keeps its own shaker frequencies, like the owner's profile.
    /// No response curve (values pass straight through) and no aggregation picker.
    /// <para/>
    /// Subclass names are SimHub's saved ContainerType and channel-map key: frozen.
    /// </summary>
    public abstract class QAdvanceFeedbackGForceContainerBase : EffectsContainerBase<ToneOutput>
    {
        private readonly QAdvanceFeedbackEffect _left;
        private readonly QAdvanceFeedbackEffect _right;

        protected QAdvanceFeedbackGForceContainerBase()
        {
            _left = new QAdvanceFeedbackEffect("Left", FFBPlacement.Left, () => ValueFor(right: false));
            _right = new QAdvanceFeedbackEffect("Right", FFBPlacement.Right, () => ValueFor(right: true));
            ShakeItEffectSupport.ApplyIdentity(this, NativeEffects.For(Pad));
        }

        /// <summary>Which pad pair. Must not depend on instance state (read from the base constructor).</summary>
        protected abstract GForcePad Pad { get; }

        protected override bool AllowEffectsAggregations => false;

        protected override IEnumerable<EffectBase> AvailableEffects
        {
            get
            {
                yield return _left;
                yield return _right;
            }
        }

        public override FilterBase Filter => null;

        // The description belongs to the "Add effect" list only (ShakeItEffectSupport.ClearBuiltInDescription).
        public override void LoadDefaultSettings()
        {
            base.LoadDefaultSettings();
            ShakeItEffectSupport.ClearBuiltInDescription(this, NativeEffects.For(Pad));
        }

        [System.Runtime.Serialization.OnDeserialized]
        internal void OnDeserializedClearDescription(System.Runtime.Serialization.StreamingContext context) =>
            ShakeItEffectSupport.ClearBuiltInDescription(this, NativeEffects.For(Pad));

        // Lets the plugin settings page's G-Force Test Effect play through these effects in the menus
        // too; with no fresh frame the outputs read 0, so nothing plays otherwise.
        [JsonIgnore]
        public override bool IsAllowedOutOfGame => true;

        public override void SetAudioDefaults(ToneOutput output) => ShakeItEffectSupport.Apply(output, ToneDefaults.For(Pad));

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            _gateOpen = TestAwareOutputGate.Allows(
                ShakeItEffectSupport.Plugin?.TestEffectActive == true,
                data?.GameRunning == true, data?.GamePaused == true, data?.GameInMenu == true,
                data?.NewData?.Spectating == true);
            if (IsEnabled && parentEnabled) ShakeItBuiltInTap.Sample(data, calibratedFeedback);
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        // Set each ShakeIt frame, before the effects are evaluated (same thread).
        private bool _gateOpen;

        // The panel's view model (not saved), created on first use on the UI thread.
        [JsonIgnore]
        internal GForceEffectViewModel ViewModel =>
            _viewModel ?? (_viewModel = new GForceEffectViewModel(PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher()));
        private GForceEffectViewModel _viewModel;

        protected override UserControl GetEditControl() => new GForceEffectView(ViewModel);

        private double ValueFor(bool right) =>
            !_gateOpen ? 0.0
                : ChannelValues.GForce(ShakeItEffectSupport.FreshOrDefault(ShakeItEffectSupport.Plugin?.LatestGForce), Pad, right);
    }

    [ShakeItContainerMetadata(210, "G-Force BottomFront",
        "G-Force, seat-bottom front leg pads (GForce.Bottom.FrontLeft / FrontRight).",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackGForceBottomFrontContainer : QAdvanceFeedbackGForceContainerBase
    {
        protected override GForcePad Pad => GForcePad.BottomFront;
    }

    [ShakeItContainerMetadata(211, "G-Force BottomRear",
        "G-Force, seat-bottom rear butt pads (GForce.Bottom.RearLeft / RearRight).",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackGForceBottomRearContainer : QAdvanceFeedbackGForceContainerBase
    {
        protected override GForcePad Pad => GForcePad.BottomRear;
    }

    [ShakeItContainerMetadata(212, "G-Force BackLow",
        "G-Force, seat-back low waist pads (GForce.Back.LowLeft / LowRight).",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackGForceBackLowContainer : QAdvanceFeedbackGForceContainerBase
    {
        protected override GForcePad Pad => GForcePad.BackLow;
    }

    [ShakeItContainerMetadata(213, "G-Force BackTop",
        "G-Force, seat-back top back pads (GForce.Back.TopLeft / TopRight).",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackGForceBackTopContainer : QAdvanceFeedbackGForceContainerBase
    {
        protected override GForcePad Pad => GForcePad.BackTop;
    }
}

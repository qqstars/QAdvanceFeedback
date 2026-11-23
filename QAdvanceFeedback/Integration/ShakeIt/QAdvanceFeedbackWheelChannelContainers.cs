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
    /// Native ShakeIt effect for one of the plugin's projected channels (docs\shakeit-native-effects-design.md).
    /// Outputs the same pulsed projected values published as WheelLock/WheelSlip.Projected.*, routed by
    /// QAdvanceFeedback's own weighted aggregation (per wheel, front/rear, left/right or one channel) -
    /// never ShakeIt's max-based one. The routing is persisted in the container's own AggregationMode,
    /// whose setter is what tells ShakeIt's channel mapping that the effect list changed.
    /// <para/>
    /// SimHub saves the CLASS NAME of each subclass as ContainerType and keys device channel maps by it:
    /// the subclass names are frozen.
    /// </summary>
    public abstract class QAdvanceFeedbackWheelChannelContainerBase : EffectsContainerBase<ToneOutput>, IRoutingTarget
    {
        // One effect per slot, created once so ShakeIt's per-effect state survives routing changes.
        private readonly QAdvanceFeedbackEffect[] _slots;
        private readonly QAdvanceProjectionFilter _filter;
        private bool _resolvingEffects;

        protected QAdvanceFeedbackWheelChannelContainerBase()
        {
            _filter = new QAdvanceProjectionFilter(IsLock) { Owner = this };
            string[] names = { "All", "Front", "Rear", "Left", "Right", "Front left", "Front right", "Rear left", "Rear right" };
            _slots = new QAdvanceFeedbackEffect[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                EffectSlot slot = (EffectSlot)i;
                _slots[i] = new QAdvanceFeedbackEffect(names[i], (FFBPlacement)i, () => ValueFor(slot));
            }
            AggregationMode = EffectRoutingNames.Mono;
            ShakeItEffectSupport.ApplyIdentity(this, IsLock ? NativeEffect.WheelLock : NativeEffect.WheelSlip);
        }

        /// <summary>True for the WheelLock effect, false for WheelSlip. Must not depend on instance state
        /// (it is read from the base constructor).</summary>
        protected abstract bool IsLock { get; }

        [JsonIgnore]
        public EffectRouting Routing
        {
            get => EffectRoutingNames.Parse(AggregationMode);
            set
            {
                if (value == Routing) return;
                _availableEffectsCache = null;
                AggregationMode = EffectRoutingNames.ToAggregationMode(value);
            }
        }

        protected override bool AllowEffectsAggregations => false;

        protected override IEnumerable<EffectBase> AvailableEffects
        {
            get
            {
                // AggregationMode's getter falls back to EffectsAggregates (built from THIS list) when it
                // was never set - guard the re-entry so that can never recurse.
                EffectRouting routing = EffectRouting.Mono;
                if (!_resolvingEffects)
                {
                    _resolvingEffects = true;
                    try { routing = EffectRoutingNames.Parse(AggregationMode); }
                    finally { _resolvingEffects = false; }
                }
                foreach (EffectSlot slot in EffectRoutingSlots.For(routing)) yield return _slots[(int)slot];
            }
        }

        public override FilterBase Filter => _filter;

        // Safety net for profile loading: whatever AggregationMode the profile restored, the effect list
        // is rebuilt from it (the setter alone does not drop the cached list).
        [System.Runtime.Serialization.OnDeserialized]
        internal void OnDeserializedRebuildEffects(System.Runtime.Serialization.StreamingContext context)
        {
            _availableEffectsCache = null;
            OnEffectsChanged();
            ShakeItEffectSupport.ClearBuiltInDescription(this, Effect);
        }

        public override void LoadDefaultSettings()
        {
            base.LoadDefaultSettings();
            Routing = EffectRouting.Mono;
            ShakeItEffectSupport.ClearBuiltInDescription(this, Effect);
        }

        private NativeEffect Effect => IsLock ? NativeEffect.WheelLock : NativeEffect.WheelSlip;

        public override void SetAudioDefaults(ToneOutput output) =>
            ShakeItEffectSupport.Apply(output, IsLock ? ToneDefaults.Lock : ToneDefaults.Slip);

        // Lets the WheelLock/WheelSlip Test Effect play on the device with no game running
        // (docs\shakeit-test-effect-mvvm-design.md); the gate below keeps every other out-of-game moment
        // silent, exactly like ShakeIt's own in-game gate.
        [JsonIgnore]
        public override bool IsAllowedOutOfGame => true;

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            _gateOpen = TestAwareOutputGate.Allows(
                ShakeItEffectSupport.Plugin?.TestLockSlipActive == true,
                data?.GameRunning == true, data?.GamePaused == true, data?.GameInMenu == true,
                data?.NewData?.Spectating == true);
            if (IsEnabled && parentEnabled) ShakeItBuiltInTap.Sample(data, calibratedFeedback);
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        // Set each ShakeIt frame, before the effects are evaluated (same thread).
        private bool _gateOpen;

        /// <summary>
        /// The effect's view model, shared by its edit panel and its curve-slot graph
        /// (docs\shakeit-test-effect-mvvm-design.md). Not saved. Created on first use, on the UI thread.
        /// </summary>
        [JsonIgnore]
        internal WheelChannelEffectViewModel ViewModel =>
            _viewModel ?? (_viewModel = new WheelChannelEffectViewModel(IsLock, this, PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher()));
        private WheelChannelEffectViewModel _viewModel;

        protected override UserControl GetEditControl() => new WheelChannelEffectView(ViewModel);

        private double ValueFor(EffectSlot slot)
        {
            if (!_gateOpen) return 0.0;
            var projected = ShakeItEffectSupport.FreshOrDefault(ShakeItEffectSupport.Plugin?.LatestProjected);
            return IsLock ? ChannelValues.Lock(projected, slot) : ChannelValues.Slip(projected, slot);
        }
    }

    [ShakeItContainerMetadata(200, "Wheel Lock",
        "Projected wheel lock (the same values as WheelLock.Projected.*), per wheel, front/rear, left/right or one channel.",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackWheelLockContainer : QAdvanceFeedbackWheelChannelContainerBase
    {
        protected override bool IsLock => true;
    }

    [ShakeItContainerMetadata(201, "Wheel Slip",
        "Projected wheel slip (the same values as WheelSlip.Projected.*), per wheel, front/rear, left/right or one channel.",
        "QAdvanceFeedback", OutputMode.Any)]
    public sealed class QAdvanceFeedbackWheelSlipContainer : QAdvanceFeedbackWheelChannelContainerBase
    {
        protected override bool IsLock => false;
    }
}

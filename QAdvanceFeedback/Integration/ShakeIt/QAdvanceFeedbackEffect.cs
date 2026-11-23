using System;
using GameReaderCommon;
using GameReaderCommon.Enums;
using GameReaderCommon.Feedback;
using QAdvanceFeedback.Core.Health;
using SimHub.Plugins.DataPlugins.ShakeItV3.Calibration;
using SimHub.Plugins.DataPlugins.ShakeItV3.Effects;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// One output of a native QAdvanceFeedback ShakeIt effect: returns a value the plugin has already
    /// computed (0..100 → 0..1). Needs no game data of its own, so it is supported on every game.
    /// </summary>
    public sealed class QAdvanceFeedbackEffect : EffectBase
    {
        private readonly Func<double> _value0100;

        public QAdvanceFeedbackEffect(string name, FFBPlacement placement, Func<double> value0100)
            : base(name, placement)
        {
            _value0100 = value0100 ?? throw new ArgumentNullException(nameof(value0100));
        }

        public override bool GetIsSupportedOnCurrentGame(FeedbackCapabilities caps) => true;

        // No ShakeIt "Test effect" link: QAdvanceFeedback's own Test Effects in the effect panels play the
        // published values instead (docs\shakeit-test-effect-mvvm-design.md).
        public override bool AllowTestMode => false;

        public override double GetEffectValue(GameData data, CalibratedFeedbackData calibratedFeedback, EffectsContainerBase effectsContainer)
        {
            double v = SafeCall.Value(_value0100, 0.0, HealthSubsystems.ShakeItEffects, "Health.Impact.ShakeItEffects");
            if (double.IsNaN(v) || v <= 0) return 0;
            return v >= 100 ? 1.0 : v / 100.0;
        }
    }
}

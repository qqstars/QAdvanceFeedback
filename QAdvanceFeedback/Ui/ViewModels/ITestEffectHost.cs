using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// What a Test Effect view model needs from the plugin (docs\shakeit-test-effect-mvvm-design.md). The
    /// plugin implements it by forwarding to <c>PublishTestEffect</c> / <c>PublishLockSlipTest</c> / <c>PublishSlipAngleTest</c>, which
    /// write the published properties and the frames the native effects play. Null releases the outputs.
    /// </summary>
    public interface ITestEffectHost
    {
        TestEffectSession Session { get; }
        void PublishGForceTest(GForceOutput outputOrNull);
        void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull);
        /// <summary>The Slip Angle Test Effect's frame (docs\slip-angle-design.md 9.11) to
        /// <c>SlipAngle.Projected.All</c>; null releases it.</summary>
        void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull);
    }
}

using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// Optional host capability: how mature the learned key data is (docs\learning-maturity-indicator-report.md).
    /// The plugin's page and panel hosts implement it; a host without it (an older test stand-in) simply shows
    /// every indicator as "not learned yet". Kept apart from <see cref="IPanelHost"/> / <see cref="ISlipAngleTabHost"/>
    /// so adding it changed no existing host.
    /// </summary>
    public interface ILearningMaturityHost
    {
        /// <summary>The maturity behind <c>LearnedKeyData</c> for a Lock / Slip source (same key, same
        /// context); null when nothing is known.</summary>
        KeyDataMaturity LearnedKeyDataMaturity(bool isLock, string sourceIdentity);

        /// <summary>The maturity behind the learned Slip Angle window for a source; null when nothing is known.</summary>
        SlipAngleWindowMaturity? LearnedSlipAngleMaturity(string sourceIdentity);
    }
}

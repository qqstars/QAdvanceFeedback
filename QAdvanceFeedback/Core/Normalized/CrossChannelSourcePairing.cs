using System;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Which Lock key a Slip key may borrow cold-start evidence from (KeyedScaleLearner's cross-channel
    /// fallback) - docs\internal-source-learning-design.md: no source ever feeds another source's learning,
    /// so the borrow only ever pairs the SAME source type across the two channels.
    /// <list type="bullet">
    /// <item>Slip internal (Plugin Internal) - Lock internal, always, whatever Lock has selected.</item>
    /// <item>Slip ShakeIt - the active Lock identity only when it is ShakeIt too (paired by source type, so
    /// each channel keeps its own algorithm fingerprint).</item>
    /// <item>Slip Viper - the active Lock identity only when it is Viper too.</item>
    /// <item>Anything else (Custom, a mixed or unknown identity, or a Lock source of a different type) -
    /// no borrow at all (null): two different Custom expressions have no known relation.</item>
    /// </list>
    /// Pure. Classification is <see cref="KnownSourceColdStartReference.Classify"/>.
    /// </summary>
    public static class CrossChannelSourcePairing
    {
        /// <summary>The Lock identity <paramref name="slipIdentity"/>'s key may borrow from, or null.</summary>
        public static string LockIdentityFor(string slipIdentity, string activeLockIdentity,
            string slipInternalIdentity, string lockInternalIdentity)
        {
            if (string.IsNullOrEmpty(slipIdentity)) return null;
            if (!string.IsNullOrEmpty(slipInternalIdentity)
                && string.Equals(slipIdentity, slipInternalIdentity, StringComparison.Ordinal))
                return string.IsNullOrEmpty(lockInternalIdentity) ? null : lockInternalIdentity;

            if (string.IsNullOrEmpty(activeLockIdentity)) return null;
            KnownFeedbackSource slipType = KnownSourceColdStartReference.Classify(slipIdentity, isLockChannel: false);
            if (slipType != KnownFeedbackSource.ShakeItMotorsExport && slipType != KnownFeedbackSource.ViperLngWheelSlip)
                return null;
            return KnownSourceColdStartReference.Classify(activeLockIdentity, isLockChannel: true) == slipType
                ? activeLockIdentity
                : null;
        }
    }
}

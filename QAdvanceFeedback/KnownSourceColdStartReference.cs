using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.MotorsExport;

namespace QAdvanceFeedback
{
    /// <summary>Which of the two input sources this plugin has measured evidence for, as identified from
    /// a channel's composite <c>SourceIdentity</c> string.</summary>
    public enum KnownFeedbackSource
    {
        /// <summary>Anything this table has no measured evidence for - a hand-configured property, a
        /// script, a third-party plugin, or a mix of providers across the four wheels. Never guessed at:
        /// an unknown source keeps the pre-existing identity cold start exactly.</summary>
        Unknown = 0,

        /// <summary>This plugin's own Layer 3 output (<c>QAdvanceFeedback.WheelLock.Raw.*</c> /
        /// <c>WheelSlip.Raw.*</c>).</summary>
        QAdvanceFeedbackRaw = 1,

        /// <summary>SimHub's "ShakeIt Motors" plugin, exporting a legacy-iRacing wheels-lock/wheels-slip
        /// effect as a property.</summary>
        ShakeItMotorsExport = 2,
    }

    /// <summary>
    /// SHIPPED COLD-START REFERENCES for the two input sources this project has actually measured, used
    /// by <c>Core.Normalized.KeyedScaleLearner</c> at the one point where it previously had nothing to
    /// work with: a genuine Tier-1 cold start, where the mapping from source value to Normalized output
    /// fell back to plain identity.
    /// <para/>
    /// WHY IDENTITY WAS THE WRONG COLD DEFAULT. The Normalized layer maps a source value onto the
    /// canonical scale as <c>source * (80 / SMax)</c>. Identity is that formula with <c>SMax</c>
    /// implicitly pinned at 80. Every measurement below puts the real SMax well under 80, so identity
    /// both understates the output and places the four-range curve's 80-knot too high, putting ordinary
    /// braking in the wrong band.
    /// <para/>
    /// WHERE THE NUMBERS COME FROM. Every <c>ColdCeiling</c> in the six captured
    /// <c>QAdvanceFeedback.Parameters.json</c> files, restricted to entries with
    /// <c>ColdIsPrimaryTier == true</c> (a ceiling backed by genuine at-limit evidence rather than the
    /// weaker fallback estimate - the two populations differ by around twenty points, which is what makes
    /// a naive median over all entries wrong):
    /// <code>
    ///   channel  source    n   min    median   max
    ///   Lock     Raw       3   31.9   62.1     66.4
    ///   Lock     ShakeIt   3   49.1   70.7     71.2
    ///   Slip     Raw       3   20.4   62.5     64.6
    ///   Slip     ShakeIt   3   14.2   62.6     66.2
    /// </code>
    /// <para/>
    /// WHY THE CONSTANTS SIT AT THE TOP OF EACH RANGE. The error is not symmetric. Too LOW an SMax
    /// inflates <c>80 / SMax</c> and pushes the whole curve up - the "first several corners shake too
    /// hard" failure this exists to remove. Too HIGH merely makes the opening corners slightly weak,
    /// which self-corrects within a lap. Every constant is therefore chosen at or near the observed
    /// primary-tier MAXIMUM, deliberately conservative.
    /// <para/>
    /// HONEST LIMITATIONS. Three cars, one title (F1 25), one driver. These are defensible starting
    /// points for the opening minute of a brand-new installation, not physical constants. Any persisted
    /// evidence at all outranks them, and the handover to this key's own evidence is a continuous ramp,
    /// never a switch - see <c>KeyedScaleLearner.Tier1ColdCeiling</c>.
    /// </summary>
    public static class KnownSourceColdStartReference
    {
        /// <summary>Wheel Lock fed by this plugin's own Layer 3 Raw output. Observed primary-tier range
        /// 31.9 - 66.4 (median 62.1); set near the top - see this class's own remarks.</summary>
        public const double LockRawSMax = 66.0;

        /// <summary>Wheel Lock fed by a ShakeIt Motors legacy-iRacing export. Observed primary-tier range
        /// 49.1 - 71.2 (median 70.7) - the tightest and best-attested of the four.</summary>
        public const double LockShakeItSMax = 71.0;

        /// <summary>Wheel Slip fed by this plugin's own Layer 3 Raw output. Observed primary-tier range
        /// 20.4 - 64.6 (median 62.5).</summary>
        public const double SlipRawSMax = 64.0;

        /// <summary>Wheel Slip fed by a ShakeIt Motors legacy-iRacing export. Observed primary-tier range
        /// 14.2 - 66.2 (median 62.6).</summary>
        public const double SlipShakeItSMax = 66.0;

        /// <summary>
        /// Identifies which known source is feeding a channel, from the composite identity string
        /// <c>Core.Normalized.SourceIdentity.Compute</c> builds from the channel's four per-wheel source
        /// configurations.
        /// <para/>
        /// ALL FOUR WHEELS MUST AGREE. The identity deliberately keeps each wheel's own provider (they
        /// can in principle differ), and a channel mixing providers has no single measured SMax - so a
        /// mixed identity resolves to <see cref="KnownFeedbackSource.Unknown"/> and keeps the old
        /// identity cold start, rather than borrowing a number from whichever provider happened to be
        /// listed first.
        /// </summary>
        public static KnownFeedbackSource Classify(string sourceIdentity, bool isLockChannel)
        {
            if (string.IsNullOrWhiteSpace(sourceIdentity)) return KnownFeedbackSource.Unknown;

            // Matching the CHANNEL's own Raw prefix (not merely "Raw") keeps a channel deliberately
            // pointed at the OTHER channel's Raw output out of this table - a cross-wiring whose SMax
            // nothing here has measured.
            string rawMarker = isLockChannel ? PublishedPropertyNames.LockPrefix : PublishedPropertyNames.SlipPrefix;

            string[] wheels = sourceIdentity.Split(new[] { '~' }, StringSplitOptions.None);
            if (wheels.Length != 4) return KnownFeedbackSource.Unknown;

            bool allRaw = true;
            bool allShakeIt = true;
            foreach (string wheel in wheels)
            {
                if (wheel.IndexOf(rawMarker, StringComparison.OrdinalIgnoreCase) < 0) allRaw = false;

                // Keyed on the ShakeIt plugin's own type name rather than the recommended exported
                // property name, since the driver picks that name themselves. The measured SMax assumes
                // the legacy-iRacing effect the setup guide describes; a driver exporting some other
                // effect would be classified here too and would get a starting point calibrated for a
                // different signal until their own evidence takes over.
                if (wheel.IndexOf(MotorsExportPropertyNames.PluginTypeName, StringComparison.OrdinalIgnoreCase) < 0) allShakeIt = false;
            }

            if (allRaw && !allShakeIt) return KnownFeedbackSource.QAdvanceFeedbackRaw;
            if (allShakeIt && !allRaw) return KnownFeedbackSource.ShakeItMotorsExport;
            return KnownFeedbackSource.Unknown;
        }

        /// <summary>The shipped cold-start SMax for this channel/source, or false when this table has no
        /// measured evidence for it - in which case the caller must keep its previous behaviour (plain
        /// identity) rather than invent a number.</summary>
        public static bool TryGetSMax(string sourceIdentity, bool isLockChannel, out double smax)
        {
            switch (Classify(sourceIdentity, isLockChannel))
            {
                case KnownFeedbackSource.QAdvanceFeedbackRaw:
                    smax = isLockChannel ? LockRawSMax : SlipRawSMax;
                    return true;
                case KnownFeedbackSource.ShakeItMotorsExport:
                    smax = isLockChannel ? LockShakeItSMax : SlipShakeItSMax;
                    return true;
                default:
                    smax = 0.0;
                    return false;
            }
        }
    }
}

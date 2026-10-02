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

        /// <summary>viper4gh's CalcLngWheelSlip plugin, read through this plugin's own shipped NCalc
        /// preset - see <c>Core.Viper.ViperPropertyNames</c>. The ORIGINAL plugin only; the community
        /// fork is out of scope.</summary>
        ViperLngWheelSlip = 3,

        /// <summary>
        /// A source the driver configured themselves - see <see cref="Settings.SourceMode.Custom"/>.
        /// <para/>
        /// NOT RESOLVABLE FROM AN IDENTITY, which is the whole difference from the three above. Their
        /// identities are fixed strings this plugin generates, so <see cref="Classify"/> can recognise
        /// them; a custom source is whatever the driver typed, and a hash of unknown text says nothing
        /// about its scale. Its reference is therefore CONFIGURED rather than classified - see
        /// <see cref="Settings.KeyDataPointDefaults.LockCustom"/> - and seeded by copying whichever
        /// preset the driver edited their way in from.
        /// </summary>
        Custom = 4,
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
        /// Wheel Lock fed by viper4gh's CalcLngWheelSlip through this plugin's own NCalc preset.
        /// <para/>
        /// SET BY THE OWNER (2026-09-28), not measured. It REPLACES a measured 100.0 that had to be
        /// discarded: that number came from a capture taken through the integer-literal defect in the
        /// preset expression - NCalc's <c>Max</c> took its type from a bare <c>0</c> and rounded every
        /// reading to 0 or 1, see <see cref="Core.Viper.ViperPropertyNames.GetScript"/> - so it was the
        /// p90 of a square wave rather than of a real distribution. With the expression corrected the
        /// source is proportional and lands in a completely different range.
        /// <para/>
        /// WHY A LOW NUMBER IS THE RIGHT SHAPE, worth stating because 15 looks small beside the 64-71
        /// the other four sources use. Those read a severity already scaled toward full deflection;
        /// Viper publishes a raw longitudinal slip RATIO, and a tyre passes its friction peak at roughly
        /// 0.10-0.20 of that ratio on tarmac. On this preset's 0-100 mapping that is 10-20, so 15 says
        /// "the limit is around 15% slip" - a statement about physics, not a guess at an output level.
        /// It still has the property every constant in this table has: an at-limit reading maps onto the
        /// canonical anchor exactly, <c>15 * 80/15 = 80</c>.
        /// <para/>
        /// SCOPED TO VIPER ALONE. <see cref="Classify"/> matches this preset by whole-identity equality
        /// against its own hashed identity, so no other source can reach these two constants - pinned by
        /// ViperKeyDataPointsTests.
        /// </summary>
        public const double LockViperSMax = 15.0;

        /// <summary>
        /// Wheel Slip fed by viper4gh's CalcLngWheelSlip through this plugin's own NCalc preset.
        /// <para/>
        /// SET BY THE OWNER (2026-09-28), replacing a measured 70.0 discarded for exactly the same
        /// reason as <see cref="LockViperSMax"/>'s 100.0 - see there for the defect, and for why a low
        /// number is the correct shape for a raw slip ratio.
        /// <para/>
        /// LOWER THAN LOCK, and that ordering is physical rather than arbitrary: a driven wheel breaks
        /// traction at a smaller slip ratio than a braked wheel needs in order to pass its friction
        /// peak, so on the SAME signal the Slip channel's limit sits below the Lock channel's. The two
        /// measured sources in this table lean the same way (Lock 66/71 against Slip 64/66), just far
        /// less pronounced, because they are not raw ratios.
        /// </summary>
        public const double SlipViperSMax = 10.0;

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

            // VIPER IS MATCHED FIRST, AND BY WHOLE-IDENTITY EQUALITY RATHER THAN BY SUBSTRING.
            //
            // The other two sources are Plain property names, which SourceIdentity keeps VERBATIM, so a
            // marker substring identifies them. Viper is an NCalc preset, and SourceIdentity HASHES every
            // scripted source (FNV-1a -> "NCalc:<8 hex>") - by design, since an expression can be
            // arbitrarily long. There is therefore no property name left in the identity to look for.
            //
            // What makes this exact anyway: the preset's four expressions are compile-time constants, so
            // the identity they produce is a compile-time constant too. ViperSourceIdentity below rebuilds
            // it through the SAME SourceIdentity.Compute the engine uses, so the two can never drift - and
            // a driver who edits so much as a space of the preset gets a different hash and correctly falls
            // out of this table rather than keeping a calibration that no longer describes their source.
            if (string.Equals(sourceIdentity, ViperSourceIdentity(isLockChannel), StringComparison.Ordinal))
                return KnownFeedbackSource.ViperLngWheelSlip;

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

        /// <summary>
        /// The identity <c>Core.Normalized.SourceIdentity.Compute</c> produces for this channel when the
        /// shipped Viper preset is configured - built from the SAME constants
        /// <c>WheelChannelSettings.ApplyViperDefaults</c> writes, through the SAME Compute the engine
        /// calls, so the two are incapable of disagreeing.
        /// <para/>
        /// Recomputed per call rather than cached: it is four string concatenations and a hash, reached
        /// only from <see cref="Classify"/> (itself a cold-start path, not a per-frame one), and a static
        /// cache here would be one more piece of state to reason about for no measurable gain.
        /// </summary>
        public static string ViperSourceIdentity(bool isLockChannel)
        {
            const string ncalc = "NCalc";
            return Core.Normalized.SourceIdentity.Compute(
                Core.Viper.ViperPropertyNames.GetScript(isLockChannel, MotorsExportPropertyNames.FrontLeft), ncalc,
                Core.Viper.ViperPropertyNames.GetScript(isLockChannel, MotorsExportPropertyNames.FrontRight), ncalc,
                Core.Viper.ViperPropertyNames.GetScript(isLockChannel, MotorsExportPropertyNames.RearLeft), ncalc,
                Core.Viper.ViperPropertyNames.GetScript(isLockChannel, MotorsExportPropertyNames.RearRight), ncalc);
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
                case KnownFeedbackSource.ViperLngWheelSlip:
                    smax = isLockChannel ? LockViperSMax : SlipViperSMax;
                    return true;
                default:
                    smax = 0.0;
                    return false;
            }
        }
    }
}

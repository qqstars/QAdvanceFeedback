using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// This channel's own Layer-3 Raw source configuration, precomputed - what a channel reads when the
    /// source it is configured for cannot work right now.
    /// <para/>
    /// WHEN A CHANNEL FALLS BACK. Today: the Viper source selected while the running game is not one
    /// viper4gh's plugin computes for (see <see cref="Core.Viper.ViperSupportedGames"/>), which is a
    /// whole-channel condition rather than a per-frame one - the plugin returns before computing, so
    /// every wheel is dead for the entire session; and the ShakeIt Built-in source whenever it is not
    /// Supported with fresh values (not set up, idle in game, not supported, stale - see
    /// <c>Core.ShakeItEffects.ShakeItSourceResolution</c>), also for the whole channel. Custom-mode's null fallback is a DIFFERENT and
    /// narrower mechanism that already lives in <see cref="WheelSourceResolver"/>, per wheel per frame.
    /// <para/>
    /// PRECOMPUTED BECAUSE THIS IS THE FRAME LOOP. The names are compile-time constants, so building
    /// them per frame would be eight string concatenations sixty times a second for nothing.
    /// <para/>
    /// THE IDENTITY MATTERS AS MUCH AS THE VALUES. A channel reading Raw must also LEARN as Raw -
    /// <see cref="RawIdentity"/> - or its evidence would accumulate under the Viper (or ShakeIt) key while the
    /// numbers came from somewhere else, and switching back to a supported game would inherit a
    /// calibration built from the wrong signal.
    /// </summary>
    public static class RawSourceFallback
    {
        public static readonly string LockFrontLeft = DefaultWheelSources.RawPropertyName(true, MotorsExportPropertyNames.FrontLeft);
        public static readonly string LockFrontRight = DefaultWheelSources.RawPropertyName(true, MotorsExportPropertyNames.FrontRight);
        public static readonly string LockRearLeft = DefaultWheelSources.RawPropertyName(true, MotorsExportPropertyNames.RearLeft);
        public static readonly string LockRearRight = DefaultWheelSources.RawPropertyName(true, MotorsExportPropertyNames.RearRight);

        public static readonly string SlipFrontLeft = DefaultWheelSources.RawPropertyName(false, MotorsExportPropertyNames.FrontLeft);
        public static readonly string SlipFrontRight = DefaultWheelSources.RawPropertyName(false, MotorsExportPropertyNames.FrontRight);
        public static readonly string SlipRearLeft = DefaultWheelSources.RawPropertyName(false, MotorsExportPropertyNames.RearLeft);
        public static readonly string SlipRearRight = DefaultWheelSources.RawPropertyName(false, MotorsExportPropertyNames.RearRight);

        /// <summary>Raw is always a plain property reference - never a script.</summary>
        public const ScriptType RawScriptType = ScriptType.Plain;

        private static readonly string LockRawIdentity = SourceIdentity.Compute(
            LockFrontLeft, "Plain", LockFrontRight, "Plain", LockRearLeft, "Plain", LockRearRight, "Plain");

        private static readonly string SlipRawIdentity = SourceIdentity.Compute(
            SlipFrontLeft, "Plain", SlipFrontRight, "Plain", SlipRearLeft, "Plain", SlipRearRight, "Plain");

        /// <summary>The learner key a fallen-back channel must use - see this class's own remarks on why
        /// the identity has to move with the values.</summary>
        public static string RawIdentity(bool isLockChannel) => isLockChannel ? LockRawIdentity : SlipRawIdentity;

        /// <summary>
        /// CUSTOM MATCHING RAW (docs\internal-source-learning-design.md section 3.1, [call]): whether a
        /// channel's four configured rows are exactly this channel's Raw property names, each with the
        /// Plain script type - i.e. the channel reads Plugin Internal whatever its mode says. Such a
        /// channel learns under the internal key (the identity is the same string anyway) and is treated
        /// as Plugin Internal: the Custom cold-start reference must not apply to it.
        /// </summary>
        public static bool MatchesRaw(WheelChannelSettings channel, bool isLockChannel)
        {
            if (channel == null) return false;
            return RowIsRaw(channel.SourceFrontLeft, channel.ScriptTypeFrontLeft, PropertyName(isLockChannel, 0))
                && RowIsRaw(channel.SourceFrontRight, channel.ScriptTypeFrontRight, PropertyName(isLockChannel, 1))
                && RowIsRaw(channel.SourceRearLeft, channel.ScriptTypeRearLeft, PropertyName(isLockChannel, 2))
                && RowIsRaw(channel.SourceRearRight, channel.ScriptTypeRearRight, PropertyName(isLockChannel, 3));
        }

        /// <summary>
        /// Whether a channel's source identity reproduces one of the SHIPPED preset sources exactly - our
        /// Raw (Plugin Internal), the canonical ShakeIt identity (compared fingerprint-stripped, see
        /// <see cref="Core.ShakeItEffects.ShakeItAlgorithmFingerprint.BaseIdentity"/>), or the shipped Viper
        /// NCalc preset. A Custom channel that does so reads that source and shares its key (same signal),
        /// so it must be treated as that source: the Custom cold-start reference never applies to it, or a
        /// driver-configured Custom SMax would reach a shipped source's key.
        /// </summary>
        public static bool MatchesShippedPreset(string sourceIdentity, bool isLockChannel)
        {
            if (string.IsNullOrEmpty(sourceIdentity)) return false;
            if (string.Equals(sourceIdentity, RawIdentity(isLockChannel), System.StringComparison.Ordinal)) return true;
            string baseIdentity = Core.ShakeItEffects.ShakeItAlgorithmFingerprint.BaseIdentity(sourceIdentity);
            if (string.Equals(baseIdentity, Core.ShakeItEffects.ShakeItSourceResolution.ShakeItIdentity(isLockChannel), System.StringComparison.Ordinal))
                return true;
            return string.Equals(sourceIdentity, KnownSourceColdStartReference.ViperSourceIdentity(isLockChannel), System.StringComparison.Ordinal);
        }

        private static bool RowIsRaw(string source, ScriptType scriptType, string rawName)
            => scriptType == RawScriptType && string.Equals(source, rawName, System.StringComparison.Ordinal);

        /// <summary>One wheel's Raw property name. <paramref name="wheelIndex"/> is this project's
        /// canonical corner order: 0 FrontLeft, 1 FrontRight, 2 RearLeft, 3 RearRight.</summary>
        public static string PropertyName(bool isLockChannel, int wheelIndex)
        {
            if (isLockChannel)
            {
                switch (wheelIndex)
                {
                    case 0: return LockFrontLeft;
                    case 1: return LockFrontRight;
                    case 2: return LockRearLeft;
                    default: return LockRearRight;
                }
            }

            switch (wheelIndex)
            {
                case 0: return SlipFrontLeft;
                case 1: return SlipFrontRight;
                case 2: return SlipRearLeft;
                default: return SlipRearRight;
            }
        }
    }
}

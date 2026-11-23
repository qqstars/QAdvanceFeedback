using System.Collections.Generic;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Whether a Wheel Lock/Slip Raw branch reads a native game signal or a speed/RPM fallback estimate.
    /// </summary>
    public enum SourceNativeness
    {
        /// <summary>The branch is not recognised, or is explicitly not a native/fallback signal (e.g.
        /// <see cref="WheelSlipBranchNames.DirectSlipFeedback"/>, <see cref="WheelSlipBranchNames.NoBranchMatched"/>).</summary>
        Unknown,

        /// <summary>The branch reads a native wheel-level signal from the game.</summary>
        Native,

        /// <summary>The branch is a speed/RPM/pedal estimate standing in for a native signal.</summary>
        Fallback,
    }

    /// <summary>
    /// Pure, SimHub-free classifier that decides whether a selected Lock or Slip branch is a native game
    /// signal or a fallback estimate. Classification is by explicit table (spec section 7): every branch
    /// name not listed here is <see cref="SourceNativeness.Unknown"/>. Used to drive the orange
    /// "estimated, not native" warning.
    /// </summary>
    public static class BranchNativeness
    {
        private static readonly HashSet<string> LockNative = new HashSet<string>
        {
            WheelSlipBranchNames.WheelSpeedLock,
            WheelSlipBranchNames.Rps,
            WheelSlipBranchNames.RpsAndDummyRadius,
        };

        private static readonly HashSet<string> SlipNative = new HashSet<string>
        {
            WheelSlipBranchNames.PrecalibratedSlip,
            WheelSlipBranchNames.SlipData,
            WheelSlipBranchNames.WheelsSpeed,
        };

        private static readonly HashSet<string> Fallback = new HashSet<string>
        {
            WheelSlipBranchNames.BrakingVsSpeed,
            WheelSlipBranchNames.RpmVsSpeedLegacy,
            WheelSlipBranchNames.RpmVsSpeedAssumedLegacy,
            WheelSlipBranchNames.NoCapabilitiesSuppliedLockFallback,
            WheelSlipBranchNames.NoCapabilitiesSuppliedSlipFallback,
        };

        private static readonly HashSet<string> ExplicitlyUnknown = new HashSet<string>
        {
            WheelSlipBranchNames.DirectSlipFeedback,
            WheelSlipBranchNames.NoBranchMatched,
        };

        /// <summary>Classifies the branch selected for the Lock channel.</summary>
        public static SourceNativeness ForLock(string selectedBranch)
        {
            if (selectedBranch == null)
            {
                return SourceNativeness.Unknown;
            }

            if (LockNative.Contains(selectedBranch))
            {
                return SourceNativeness.Native;
            }

            return Fallback.Contains(selectedBranch) ? SourceNativeness.Fallback : SourceNativeness.Unknown;
        }

        /// <summary>Classifies the branch selected for the Slip channel.</summary>
        public static SourceNativeness ForSlip(string selectedBranch)
        {
            if (selectedBranch == null)
            {
                return SourceNativeness.Unknown;
            }

            if (SlipNative.Contains(selectedBranch))
            {
                return SourceNativeness.Native;
            }

            return Fallback.Contains(selectedBranch) ? SourceNativeness.Fallback : SourceNativeness.Unknown;
        }

        /// <summary>
        /// True when <paramref name="branch"/> appears in one of this class's explicit classification sets
        /// (native, fallback, or explicitly unknown). Used by tests to guarantee every
        /// <see cref="WheelSlipBranchNames"/> constant is deliberately classified.
        /// </summary>
        internal static bool IsClassified(string branch)
        {
            return branch != null
                && (LockNative.Contains(branch) || SlipNative.Contains(branch)
                    || Fallback.Contains(branch) || ExplicitlyUnknown.Contains(branch));
        }
    }
}

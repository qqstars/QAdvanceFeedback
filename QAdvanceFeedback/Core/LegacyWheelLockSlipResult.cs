namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The full published shape of one Layer 3 update: four wheels plus the Front/Rear/Left/Right/All
    /// aggregates, for both the Lock and the Slip channel - all already scaled to 0-100 and clamped
    /// (see <c>RawCalculatorEngine</c>). Mirrors <see cref="PublishedPropertyNames"/>'
    /// nine targets exactly, times two channels.
    /// </summary>
    public sealed class LegacyWheelLockSlipResult
    {
        public Corners LockWheels { get; }
        public double LockFront { get; }
        public double LockRear { get; }
        public double LockLeft { get; }
        public double LockRight { get; }
        public double LockAll { get; }

        public Corners SlipWheels { get; }
        public double SlipFront { get; }
        public double SlipRear { get; }
        public double SlipLeft { get; }
        public double SlipRight { get; }
        public double SlipAll { get; }

        /// <summary>
        /// Which Raw-layer signal shape this frame's Lock channel actually used - one of the
        /// <see cref="WheelSlipBranchNames"/> constants. Never null - the engine always supplies one of
        /// the named constants, so a driver comparing this against SimHub's own effect UI always has an
        /// answer, even when that answer is "no branch matched".
        /// </summary>
        public string SelectedLockBranch { get; }

        /// <summary>See <see cref="SelectedLockBranch"/> - the Slip channel's equivalent.</summary>
        public string SelectedSlipBranch { get; }

        /// <summary>
        /// ABSENT-VS-ZERO (telemetry-integrity pass, item 1): whether each of the Lock channel's 9
        /// published targets actually had a real reading this frame, in
        /// <see cref="PublishedPropertyNames"/>.Targets order (FrontLeft, FrontRight, RearLeft,
        /// RearRight, Front, Rear, Left, Right, All) - see <see cref="Aggregator.ComputeAvailable"/>'s own
        /// remarks. A false entry means the corresponding numeric property above is a harmless 0.0
        /// PLACEHOLDER (never itself published as a real "0" - see <c>PropertyPublisher</c>'s own
        /// nullable Raw-tier publish), not a genuine "nothing happening" reading. Defaults to all-true
        /// (full availability) when not supplied, so every pre-existing caller/test that predates this
        /// distinction keeps compiling and behaving exactly as before.
        /// </summary>
        public bool[] LockHasValue { get; }

        /// <summary>See <see cref="LockHasValue"/> - the Slip channel's equivalent.</summary>
        public bool[] SlipHasValue { get; }

        public LegacyWheelLockSlipResult(
            Corners lockWheels, double lockFront, double lockRear, double lockLeft, double lockRight, double lockAll,
            Corners slipWheels, double slipFront, double slipRear, double slipLeft, double slipRight, double slipAll,
            string selectedLockBranch = null, string selectedSlipBranch = null,
            bool[] lockHasValue = null, bool[] slipHasValue = null)
        {
            LockWheels = lockWheels;
            LockFront = lockFront;
            LockRear = lockRear;
            LockLeft = lockLeft;
            LockRight = lockRight;
            LockAll = lockAll;

            SlipWheels = slipWheels;
            SlipFront = slipFront;
            SlipRear = slipRear;
            SlipLeft = slipLeft;
            SlipRight = slipRight;
            SlipAll = slipAll;

            SelectedLockBranch = selectedLockBranch ?? WheelSlipBranchNames.NoBranchMatched;
            SelectedSlipBranch = selectedSlipBranch ?? WheelSlipBranchNames.NoBranchMatched;

            LockHasValue = NormaliseAvailability(lockHasValue);
            SlipHasValue = NormaliseAvailability(slipHasValue);
        }

        private static bool[] NormaliseAvailability(bool[] hasValue)
        {
            if (hasValue != null && hasValue.Length == 9) return hasValue;
            return new[] { true, true, true, true, true, true, true, true, true };
        }
    }
}

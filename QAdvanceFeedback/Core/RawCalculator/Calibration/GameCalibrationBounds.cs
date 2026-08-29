namespace QAdvanceFeedback.Core.RawCalculator.Calibration
{
    /// <summary>
    /// The per-game wheel-speed-delta bounds SimHub carries on its own <c>GameCalibration</c> - the same
    /// object, in the same <c>ShakeIt\ShakeItPrecalibration\GameData.json</c>, whose
    /// <c>PrecalibrationData</c> this plugin already imports. They are consumed by
    /// <see cref="DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip"/>.
    /// <para/>
    /// The defaults below are SimHub's own property initialisers, so a game with no shipped entry behaves
    /// identically to ShakeIt on that game.
    /// </summary>
    public sealed class GameCalibrationBounds
    {
        /// <summary>Low bound for the SLIP channel in the positive-delta (driven/coasting) regime. Only
        /// applies until the per-gear moving average has more than ten samples, after which SimHub
        /// replaces it with <c>average * 1.1</c>.</summary>
        public double WheelSpeedDeltaLowbound { get; set; } = 0.02;

        /// <summary>High bound in the positive-delta regime. Applies for as long as that regime does -
        /// unlike the low bound, nothing ever overrides it, so this is the shipped number with the most
        /// influence on the published value.</summary>
        public double WheelSpeedDeltaHighbound { get; set; } = 0.2;

        /// <summary>
        /// Low bound for the LOCK channel, per SimHub's own <c>GameCalibration</c>.
        /// <para/>
        /// CARRIED FOR FIDELITY, BUT UNREACHABLE IN THIS BRANCH - worth knowing before someone "fixes" a
        /// call site that appears to ignore it. SimHub assigns it, then either returns 0 for Lock in the
        /// positive-delta regime, or falls into the braking regime which overwrites the low bound with a
        /// hard-coded 0.4. There is no path on which this value reaches an Offset call. It is imported and
        /// stored so the converted data is a faithful copy of SimHub's file, not because it does anything
        /// here.
        /// </summary>
        public double WheelSpeedDeltaLowLockbound { get; set; } = 0.1;

        /// <summary>The bounds a game with no shipped entry gets - SimHub's own defaults.</summary>
        public static GameCalibrationBounds Defaults => new GameCalibrationBounds();
    }
}

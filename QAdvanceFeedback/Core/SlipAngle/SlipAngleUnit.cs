namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// The unit a game's native slip-angle reading is in (docs\slip-angle-spike-report.md: only
    /// Drift21 (by name), LFS (tangent, converted with atan) and the computed rFactor 2 / LMU / NASCAR
    /// rows are known by the table). Every other native field is <see cref="Unverified"/> in the table and
    /// resolved per game (owner, 2026-11-20 - <see cref="SlipAngleSourceUnits"/>): a game in the config's
    /// <c>ByAngle</c> list is <see cref="Degrees"/>, in <c>AngleUsingRatio1</c> / <c>AngleUsingRatio100</c>
    /// a grip ratio, anything else <see cref="Radians"/>; <see cref="SlipAngleUnitClassifier"/> moves an
    /// unlisted game to its list when its values say otherwise.
    /// </summary>
    public enum SlipAngleUnit
    {
        /// <summary>Not stated by the game reader (a table row); resolved per game.</summary>
        Unverified = 0,
        Degrees = 1,
        Radians = 2,

        /// <summary>A grip ratio on a 0..1 scale (0 = full grip, 1 = full slip; may go over 1 - Forza's
        /// normalized "TireSlipAngle"). Read as 0..100, then angle = 20% x value.</summary>
        Ratio1 = 3,

        /// <summary>A grip ratio on a 0..100 scale (0 = full grip, 100 = full slip). Angle = 20% x value.</summary>
        Ratio100 = 4,
    }
}

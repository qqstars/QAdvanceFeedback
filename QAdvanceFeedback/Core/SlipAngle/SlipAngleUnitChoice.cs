namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// The slip-angle unit dropdown's entries (owner, 2026-11-21 / 2026-11-22): each unit is one list in the plugin
    /// config (<see cref="SlipAngleSourceUnits"/>). Car angle ignores the game's tyre field and uses the car-angle
    /// estimate - always available. <see cref="AutoDetect"/> is no list: the game's unit is checked while driving.
    /// </summary>
    public enum SlipAngleUnitChoice
    {
        Degrees,
        Radians,
        Ratio1,
        Ratio100,
        CarAngle,
        AutoDetect,
    }
}

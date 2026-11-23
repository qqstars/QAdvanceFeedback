namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// What the native "Slip Angle" ShakeIt effect plays (docs\slip-angle-design.md 9.10): the published
    /// <c>SlipAngle.Projected.All</c> (0-100) captured on the data thread, or 0 when that frame is older
    /// than <see cref="FrameSnapshot.StaleTicks"/> (500 ms) - SimHub stopped calling the plugin, so the
    /// shaker must not hold the last value.
    /// </summary>
    public static class SlipAngleEffectOutput
    {
        public static double Value0100(FrameSnapshot<double> snapshot, long nowTicks)
        {
            if (snapshot == null || !snapshot.IsFresh(nowTicks, FrameSnapshot.StaleTicks)) return 0.0;
            double v = snapshot.Value;
            return ClampMath.IsFinite(v) ? ClampMath.Clamp(v, 0.0, 100.0) : 0.0;
        }
    }
}

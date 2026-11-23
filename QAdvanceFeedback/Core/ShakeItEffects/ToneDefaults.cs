namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// Starting values for a ShakeIt bass-shaker output (SimHub's <c>ToneOutput</c>): "when low"
    /// (<see cref="LowHz"/>), "when high" (<see cref="UseHigh"/>/<see cref="HighHz"/>) and white noise.
    /// ShakeIt applies them only when the effect's output is first created (add/paste), never over a
    /// driver's later edits.
    /// </summary>
    public sealed class ToneDefault
    {
        public ToneDefault(int lowHz, bool useHigh, int highHz, bool useNoise, int noisePercent)
        {
            LowHz = lowHz;
            UseHigh = useHigh;
            HighHz = highHz;
            UseNoise = useNoise;
            NoisePercent = noisePercent;
        }

        public int LowHz { get; }
        public bool UseHigh { get; }
        public int HighHz { get; }
        public bool UseNoise { get; }
        public int NoisePercent { get; }
    }

    /// <summary>
    /// The owner's chosen defaults: Lock/Slip/Clutch from the owner (2026-11-18); G-Force copied from
    /// docs\setup\G-Force\Sensit! Haptic Core - QAdvanceFeedback - GForce.siprofile.
    /// </summary>
    public static class ToneDefaults
    {
        // White-noise level ShakeIt itself starts from; left off.
        private const int ShakeItDefaultNoisePercent = 10;

        public static readonly ToneDefault Lock = new ToneDefault(40, true, 20, false, ShakeItDefaultNoisePercent);
        public static readonly ToneDefault Slip = new ToneDefault(45, true, 25, false, ShakeItDefaultNoisePercent);

        /// <summary>Clutch Bite (docs\clutch-bite-design.md): 40 Hz away from the bite, 15 Hz at it - a
        /// 4-cylinder idles at about 27 Hz and lugs lower as the clutch bites.</summary>
        public static readonly ToneDefault Clutch = new ToneDefault(40, true, 15, false, ShakeItDefaultNoisePercent);

        /// <summary>Slip Angle (docs\slip-angle-design.md 9.10): 40 Hz when low, 25 Hz when high, no noise.</summary>
        public static readonly ToneDefault SlipAngle = new ToneDefault(40, true, 25, false, ShakeItDefaultNoisePercent);

        private static readonly ToneDefault BottomFront = new ToneDefault(100, true, 60, false, 10);
        private static readonly ToneDefault BottomRear = new ToneDefault(100, true, 70, false, 10);
        private static readonly ToneDefault BackLow = new ToneDefault(100, true, 70, false, 10);
        private static readonly ToneDefault BackTop = new ToneDefault(100, true, 60, false, 10);

        public static ToneDefault For(GForcePad pad)
        {
            switch (pad)
            {
                case GForcePad.BottomFront: return BottomFront;
                case GForcePad.BottomRear: return BottomRear;
                case GForcePad.BackLow: return BackLow;
                default: return BackTop;
            }
        }
    }
}

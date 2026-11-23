using QAdvanceFeedback.Core.Clutch;

namespace QAdvanceFeedback.Settings
{
    public enum ClutchBiteMode { Auto, Manual }

    /// <summary>
    /// Clutch Bite settings (docs\clutch-bite-design.md, "Settings"). Shared by the plugin page's Clutch
    /// tab and the native ShakeIt effect through the settings hub. Values are clamped on set to the
    /// ranges <see cref="ClutchBiteCurve"/> uses.
    /// </summary>
    public sealed class ClutchBiteSettings
    {
        private double _bitePoint = 50.0, _fadedRange = 30.0, _gamma = 2.5;

        /// <summary>When the output applies, by the pedal's last direction of movement.</summary>
        public ClutchApplyDirection Apply { get; set; } = ClutchApplyDirection.PressAndRelease;

        /// <summary>Manual: <see cref="BitePoint"/> is final. Auto: it is the starting point learning moves from.</summary>
        public ClutchBiteMode Mode { get; set; } = ClutchBiteMode.Auto;

        /// <summary>Clutch position (0 released .. 100 pressed) where the output is 100; 10..90.</summary>
        public double BitePoint { get => _bitePoint; set => _bitePoint = ClutchBiteCurve.ClampBite(value); }

        /// <summary>Distance either side of the bite where the output reaches 0; 1..50.</summary>
        public double FadedRange { get => _fadedRange; set => _fadedRange = ClutchBiteCurve.ClampRange(value); }

        /// <summary>S-curve steepness; 1 = straight line; 1..6.</summary>
        public double Gamma { get => _gamma; set => _gamma = ClutchBiteCurve.ClampGamma(value); }

        /// <summary>SimHub controller axis property (e.g. <c>JoystickPlugin.Pedals_Z</c>) used when the game
        /// gives no clutch, and always by the Test Effect. Null = none.</summary>
        public string FallbackAxis { get; set; }

        /// <summary>For pedals that read 100 % when released.</summary>
        public bool InvertAxis { get; set; }

        /// <summary>The bite point learning parameters - edited on the plugin page's Clutch tab only.</summary>
        public ClutchLearningSettings Learning { get; set; } = new ClutchLearningSettings();

        /// <summary>The values the Core engine uses.</summary>
        public ClutchBiteParameters ToParameters() =>
            new ClutchBiteParameters(Apply, Mode == ClutchBiteMode.Auto, BitePoint, FadedRange, Gamma,
                (Learning ?? new ClutchLearningSettings()).ToParameters());
    }

    /// <summary>
    /// The tunable bite point learning parameters (docs\clutch-bite-design.md, "Learning parameters").
    /// Shown on the plugin page's Clutch tab only, never in the ShakeIt panel. Values are clamped on set.
    /// </summary>
    public sealed class ClutchLearningSettings
    {
        private ClutchLearningParameters _p = ClutchLearningParameters.Default;

        public double ArmClutchPercent { get => _p.ArmClutchPercent; set => _p = _p.With(armClutchPercent: value); }
        public double RpmDipPercent { get => _p.RpmDipPercent; set => _p = _p.With(rpmDipPercent: value); }
        public double ConfirmSpeedKmh { get => _p.ConfirmSpeedKmh; set => _p = _p.With(confirmSpeedKmh: value); }
        public double MaxReleaseRatePerSec { get => _p.MaxReleaseRatePerSec; set => _p = _p.With(maxReleaseRatePerSec: value); }
        public double MaxThrottleSwing { get => _p.MaxThrottleSwing; set => _p = _p.With(maxThrottleSwing: value); }
        public double SteadyIdlePercent { get => _p.SteadyIdlePercent; set => _p = _p.With(steadyIdlePercent: value); }
        public int MatureSamples { get => _p.MatureSamples; set => _p = _p.With(matureSamples: value); }
        public double MatureSpreadPoints { get => _p.MatureSpreadPoints; set => _p = _p.With(matureSpreadPoints: value); }

        public ClutchLearningParameters ToParameters() => _p;

        public void Apply(ClutchLearningParameters p) =>
            _p = ClutchLearningParameters.Clamped(p.ArmClutchPercent, p.RpmDipPercent, p.ConfirmSpeedKmh, p.MaxReleaseRatePerSec,
                p.MaxThrottleSwing, p.SteadyIdlePercent, p.MatureSamples, p.MatureSpreadPoints);
    }
}

using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>Which pedal(s) open the slip-angle gate.</summary>
    public enum TriggerBy
    {
        Brake,
        Throttle,
        /// <summary>Brake at/above its threshold OR throttle at/above its own.</summary>
        Any
    }

    /// <summary>
    /// Plain mutable settings for <see cref="SlipAngleGate"/> (serializer-agnostic: public get/set
    /// properties, no attributes; a later Settings task persists it).
    /// </summary>
    public sealed class SlipAngleGateSettings
    {
        public TriggerBy TriggerBy { get; set; } = TriggerBy.Any;
        /// <summary>Brake percent (0-100) at/above which the brake counts as open.</summary>
        public double BrakeThreshold { get; set; } = 5.0;
        /// <summary>Throttle percent (0-100) at/above which the throttle counts as open.</summary>
        public double ThrottleThreshold { get; set; } = 10.0;
        /// <summary>Flight time that earns the full landing wobble.</summary>
        public double MaxFlightSeconds { get; set; } = 1.0;
        /// <summary>Wobble length after a flight of <see cref="MaxFlightSeconds"/> or more.</summary>
        public double MaxWobbleSeconds { get; set; } = 0.5;
    }

    /// <summary>One frame's gating verdict (docs\slip-angle-design.md 9.2).</summary>
    public readonly struct SlipAngleGateVerdict
    {
        public SlipAngleGateVerdict(bool pedalOpen, double speedFade, bool airborne, double landingFade,
            bool inWobble, bool hopExcluded, bool learnSpeedOk)
        {
            PedalOpen = pedalOpen;
            SpeedFade = speedFade;
            Airborne = airborne;
            LandingFade = landingFade;
            InWobble = inWobble;
            HopExcluded = hopExcluded;
            LearnSpeedOk = learnSpeedOk;
        }

        /// <summary>The configured pedal trigger is satisfied.</summary>
        public bool PedalOpen { get; }
        /// <summary>0..1, linear over 10 to 20 km/h ground speed.</summary>
        public double SpeedFade { get; }
        /// <summary>The car is airborne (IsFlying).</summary>
        public bool Airborne { get; }
        /// <summary>0..1 S-curve across the post-landing wobble; 0 while airborne; 1 when no wobble is active.</summary>
        public double LandingFade { get; }
        /// <summary>A post-landing wobble is in progress.</summary>
        public bool InWobble { get; }
        /// <summary>Vertical-g hop exclusion (only ever true when VerticalG is reported).</summary>
        public bool HopExcluded { get; }
        /// <summary>Ground speed is at least 30 km/h.</summary>
        public bool LearnSpeedOk { get; }

        /// <summary>Learning may use this frame: fast enough, grounded, not wobbling, not hopping.</summary>
        public bool LearningAllowed => LearnSpeedOk && !Airborne && !InWobble && !HopExcluded;
    }

    /// <summary>
    /// Slip Angle gating (docs\slip-angle-design.md 9.2, rulings R5/R7). Stateful: measures flight time
    /// to size the landing wobble, and holds the hop exclusion for 200 ms. LandingFade uses smoothstep
    /// t^2(3-2t) as its S-curve. Never throws.
    /// </summary>
    public sealed class SlipAngleGate
    {
        public const double FadeStartKmh = 10.0;
        public const double FadeEndKmh = 20.0;
        public const double LearnMinKmh = 30.0;
        public const double HopThresholdG = 0.5;
        public const double HopHoldSeconds = 0.2;
        private const double NominalDt = 1.0 / 60.0;
        private const double Eps = 1e-9;

        private bool _wasAirborne;
        private double _flight;
        private bool _wobbling;
        private double _wobbleDur;
        private double _wobbleElapsed;
        private double _hopRemaining;

        /// <summary>Diagnostics: seconds left in the current landing wobble (0 when none).</summary>
        public double WobbleRemainingSeconds => _wobbling ? System.Math.Max(0.0, _wobbleDur - _wobbleElapsed) : 0.0;

        public SlipAngleGateVerdict Step(ITelemetryFrame f, double dt, SlipAngleGateSettings s)
        {
            if (s == null) s = new SlipAngleGateSettings();
            if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0.0) dt = NominalDt;

            // Pedals (null / NaN read as 0).
            double brake = Clean(f?.BrakePercent);
            double throttle = Clean(f?.ThrottlePercent);
            bool b = brake >= s.BrakeThreshold, t = throttle >= s.ThrottleThreshold;
            bool pedal = s.TriggerBy == TriggerBy.Brake ? b : s.TriggerBy == TriggerBy.Throttle ? t : (b || t);

            // Speed.
            double speed = Clean(f?.GroundSpeedKmh ?? f?.SpeedKmh);
            double speedFade = Clamp01((speed - FadeStartKmh) / (FadeEndKmh - FadeStartKmh));
            bool learnSpeed = speed >= LearnMinKmh;

            // Airborne / landing wobble.
            bool air = f != null && f.IsAirborne;
            double fade;
            if (air)
            {
                _flight = _wasAirborne ? _flight + dt : dt;
                _wobbling = false;
                fade = 0.0;
            }
            else
            {
                if (_wasAirborne)
                {
                    double maxFlight = s.MaxFlightSeconds > 0 ? s.MaxFlightSeconds : 0.0;
                    double maxWobble = s.MaxWobbleSeconds > 0 ? s.MaxWobbleSeconds : 0.0;
                    double scale = maxFlight > 0 ? Math.Min(1.0, _flight / maxFlight) : 1.0;
                    _wobbleDur = scale * maxWobble;
                    _wobbleElapsed = 0.0;
                    _wobbling = _wobbleDur > Eps;
                }
                else if (_wobbling)
                {
                    _wobbleElapsed += dt;
                    if (_wobbleElapsed >= _wobbleDur - Eps) _wobbling = false;
                }
                _flight = 0.0;
                if (_wobbling)
                {
                    double u = Clamp01(_wobbleElapsed / _wobbleDur);
                    fade = u * u * (3.0 - 2.0 * u);
                }
                else fade = 1.0;
            }
            _wasAirborne = air;

            // Hop exclusion (only when VerticalG is reported).
            double? vg = f?.VerticalG;
            bool hopNow = vg.HasValue && !double.IsNaN(vg.Value) && Math.Abs(vg.Value - 1.0) > HopThresholdG;
            if (hopNow) _hopRemaining = HopHoldSeconds;
            else if (_hopRemaining > 0.0) _hopRemaining -= dt;
            bool hop = hopNow || _hopRemaining > Eps;

            return new SlipAngleGateVerdict(pedal, speedFade, air, fade, _wobbling, hop, learnSpeed);
        }

        private static double Clean(double? v) => v.HasValue && !double.IsNaN(v.Value) ? v.Value : 0.0;

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
    }
}

using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>Result of <see cref="SlipAngleProjectedEngine.Compute"/>; all values 0-100, never NaN.</summary>
    public sealed class SlipAngleProjectedResult
    {
        public SlipAngleProjectedResult(double finalAll, double pulseFreeAll, double preDriftAll,
            DriftState driftState, double driftFactor)
        {
            FinalAll = finalAll;
            PulseFreeAll = pulseFreeAll;
            PreDriftAll = preDriftAll;
            DriftState = driftState;
            DriftFactor = driftFactor;
        }

        /// <summary>Published value (pulsed).</summary>
        public double FinalAll { get; }
        /// <summary>Same value before the pulse stage.</summary>
        public double PulseFreeAll { get; }
        /// <summary>After the cold-start maturity scale, before drift attenuation.</summary>
        public double PreDriftAll { get; }
        public DriftState DriftState { get; }
        public double DriftFactor { get; }
    }

    /// <summary>
    /// Slip Angle's projected layer (docs\slip-angle-design.md 9.6). Order: project -> cold-start
    /// maturity scale -> drift attenuation -> x LandingFade x SpeedFade -> pedal gate -> pulse.
    /// </summary>
    public sealed class SlipAngleProjectedEngine
    {
        private readonly OutputProjector _projector;
        private readonly DriftSettings _drift;
        private readonly DriftAttenuator _attenuator = new DriftAttenuator();
        private readonly PulseGenerator _pulse;

        public SlipAngleProjectedEngine(OutputProjector projector, PulseSettings pulse, DriftSettings drift)
        {
            _projector = projector ?? throw new System.ArgumentNullException(nameof(projector));
            _pulse = new PulseGenerator(pulse ?? new PulseSettings());
            _drift = drift ?? new DriftSettings();
        }

        /// <summary>The configured drift settings.</summary>
        public DriftSettings Drift => _drift;

        /// <summary>Back to drift state Normal (full output) with the drift EMA and timers cleared -
        /// used across a gap in the frame stream (no game / paused / menu), like a game switch.</summary>
        public void ResetDrift() => _attenuator.Reset();

        /// <param name="driftStartNu">Nᵤ above which drift counting runs (<see cref="SlipAngleDriftStart.Nu"/>).</param>
        /// <param name="throttlePercent">The frame's throttle, for the drift throttle clear (null = none).</param>
        /// <param name="driftOverride">This frame's drift settings instead of the configured ones (a grip-ratio
        /// source that stops at full slip: the longer hold); null = the configured ones.</param>
        public SlipAngleProjectedResult Compute(double normalized, double nUnclamped, double maturity,
            ColdStartTier tier, SlipAngleGateVerdict gate, double dt,
            double driftStartNu = DriftAttenuator.DriftThreshold, double? throttlePercent = null,
            DriftSettings driftOverride = null)
        {
            double projected = _projector.Project(Finite(normalized));
            double scale = ColdStartScale.Compute(maturity, ColdStartTierFloors.FloorFor(tier));
            double preDrift = ColdStartScale.ApplyAmplitudeScale(projected, scale);

            // Drift contract: Reset on every airborne / landing-wobble frame; otherwise it keeps
            // stepping (even with the pedal closed) so its state tracks the slide.
            double drifted;
            if (gate.Airborne || gate.InWobble)
            {
                _attenuator.Reset();
                drifted = preDrift;
            }
            else
            {
                drifted = _attenuator.Step(nUnclamped, preDrift, dt, driftOverride ?? _drift, driftStartNu, throttlePercent);
            }

            double value = drifted * Fade(gate.LandingFade) * Fade(gate.SpeedFade);
            if (gate.Airborne || !gate.PedalOpen) value = 0.0;
            value = ClampMath.To0100(Finite(value));

            double final = _pulse.Advance(dt, value);
            return new SlipAngleProjectedResult(ClampMath.To0100(Finite(final)), value, preDrift,
                _attenuator.State, _attenuator.Factor);
        }

        private static double Finite(double v) => ClampMath.IsFinite(v) ? v : 0.0;
        private static double Fade(double f) => ClampMath.To01(Finite(f));
    }
}

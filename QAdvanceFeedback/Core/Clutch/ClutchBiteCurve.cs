using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Core.Clutch
{
    /// <summary>When the Clutch Bite output applies, by the pedal's last direction of movement.</summary>
    public enum ClutchApplyDirection { PressAndRelease, PressOnly, ReleaseOnly }

    /// <summary>
    /// The Clutch Bite output curve (docs\clutch-bite-design.md, "Output calculation"): 100 at the bite
    /// point, fading to 0 at bite ± range along a symmetric S-curve on each side, with a 5-point deadzone
    /// at each end of the pedal travel.
    /// <code>
    /// lo = max(5, b − r)    hi = min(95, b + r)    S(t) = tᵏ / (tᵏ + (1−t)ᵏ)
    /// </code>
    /// Clutch is 0 released .. 100 fully pressed (SimHub's [Clutch] convention).
    /// </summary>
    public static class ClutchBiteCurve
    {
        public const double Deadzone = 5.0;
        public const double MinBite = 10.0, MaxBite = 90.0;
        public const double MinRange = 1.0, MaxRange = 50.0;
        public const double MinGamma = 1.0, MaxGamma = 6.0;

        public static double ClampBite(double bite) => ClampMath.Clamp(Finite(bite, 50.0), MinBite, MaxBite);
        public static double ClampRange(double range) => ClampMath.Clamp(Finite(range, 30.0), MinRange, MaxRange);
        public static double ClampGamma(double gamma) => ClampMath.Clamp(Finite(gamma, 2.5), MinGamma, MaxGamma);

        /// <summary>Start and end of the output (where it is 0), after the deadzones.</summary>
        public static (double Lo, double Hi) Edges(double bite, double range)
        {
            double b = ClampBite(bite), r = ClampRange(range);
            return (Math.Max(Deadzone, b - r), Math.Min(100.0 - Deadzone, b + r));
        }

        /// <summary>The output, 0..100, for a clutch position.</summary>
        public static double Output(double clutch0100, double bite, double range, double gamma)
        {
            if (!ClampMath.IsFinite(clutch0100)) return 0.0;
            double b = ClampBite(bite), k = ClampGamma(gamma);
            (double lo, double hi) = Edges(b, range);
            double c = clutch0100;
            if (c <= lo || c >= hi) return 0.0;
            double t = c <= b ? (c - lo) / (b - lo) : (hi - c) / (hi - b);
            return 100.0 * S(t, k);
        }

        /// <summary>The curve in value coordinates (x = clutch, y = output), evenly sampled.</summary>
        public static IReadOnlyList<GraphPoint> Points(double bite, double range, double gamma, int samples = 101)
        {
            var points = new List<GraphPoint>(samples);
            for (int i = 0; i < samples; i++)
            {
                double x = samples == 1 ? 0.0 : i * 100.0 / (samples - 1);
                points.Add(new GraphPoint(x, Output(x, bite, range, gamma)));
            }
            return points;
        }

        /// <summary>Symmetric S-curve on 0..1: slow start, steep middle, slow finish; k = 1 is a line.</summary>
        public static double S(double t, double k)
        {
            t = ClampMath.To01(t);
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            double a = Math.Pow(t, k), b = Math.Pow(1.0 - t, k);
            return a / (a + b);
        }

        private static double Finite(double v, double fallback) => ClampMath.IsFinite(v) ? v : fallback;
    }

    public enum ClutchMoveDirection { Unknown, Pressing, Releasing }

    /// <summary>
    /// The pedal's last direction of movement (option B): it stays as it was while the pedal is held,
    /// and flips only after the pedal has moved <see cref="Hysteresis"/> points the other way from the
    /// last turning point, so a shaky foot cannot flip it.
    /// </summary>
    public sealed class ClutchDirectionTracker
    {
        public const double Hysteresis = 2.0;

        private double? _reference;   // Unknown: the first position; otherwise the turning point
        public ClutchMoveDirection Last { get; private set; }

        public void Update(double clutch0100)
        {
            if (!ClampMath.IsFinite(clutch0100)) return;
            double c = clutch0100;
            if (!_reference.HasValue) { _reference = c; return; }
            double r = _reference.Value;

            switch (Last)
            {
                case ClutchMoveDirection.Unknown:
                    if (c - r >= Hysteresis) { Last = ClutchMoveDirection.Pressing; _reference = c; }
                    else if (r - c >= Hysteresis) { Last = ClutchMoveDirection.Releasing; _reference = c; }
                    break;
                case ClutchMoveDirection.Releasing:
                    if (c < r) _reference = c;                                   // still releasing: track the lowest point
                    else if (c - r >= Hysteresis) { Last = ClutchMoveDirection.Pressing; _reference = c; }
                    break;
                case ClutchMoveDirection.Pressing:
                    if (c > r) _reference = c;                                   // still pressing: track the highest point
                    else if (r - c >= Hysteresis) { Last = ClutchMoveDirection.Releasing; _reference = c; }
                    break;
            }
        }

        public bool Allows(ClutchApplyDirection apply)
        {
            switch (apply)
            {
                case ClutchApplyDirection.PressOnly: return Last == ClutchMoveDirection.Pressing;
                case ClutchApplyDirection.ReleaseOnly: return Last == ClutchMoveDirection.Releasing;
                default: return true;
            }
        }

        public void Reset()
        {
            _reference = null;
            Last = ClutchMoveDirection.Unknown;
        }
    }
}

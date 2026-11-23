using System;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Maps a slip angle in degrees to the 0-100 Normalized scale (docs\slip-angle-design.md 8): a
    /// monotone cubic through (0,0), (Start,15 when present), (Min,30), (Best,60), (Max,80), (End,100) with End = 1.5 x Max.
    /// <see cref="Normalize"/> clamps to [0,100]; <see cref="NormalizeUnclamped"/> carries on past End
    /// at +20 points per 0.5 x Max. An invalid window falls back to <see cref="SlipAngleWindow.ShippedDefault"/>.
    /// </summary>
    public static class SlipAngleNormalizer
    {
        public const double StartPoint = 15.0;
        public const double MinPoint = 30.0;
        public const double BestPoint = 60.0;
        public const double MaxPoint = 80.0;
        public const double EndPoint = 100.0;

        /// <summary>Points added per 0.5 x Max beyond End in the unclamped form.</summary>
        public const double OvershootPointsPerHalfMax = 20.0;

        [ThreadStatic] private static SlipAngleWindow _cachedWindow;
        [ThreadStatic] private static MonotoneCubicCurve _cachedCurve;

        private static MonotoneCubicCurve CurveFor(SlipAngleWindow w)
        {
            MonotoneCubicCurve c = _cachedCurve;
            SlipAngleWindow k = _cachedWindow;
            if (c != null && k.Min == w.Min && k.Best == w.Best && k.Max == w.Max) return c;

            double? start = w.Start;
            c = start.HasValue
                ? new MonotoneCubicCurve(
                    new[] { 0.0, start.Value, w.Min, w.Best, w.Max, w.End },
                    new[] { 0.0, StartPoint, MinPoint, BestPoint, MaxPoint, EndPoint })
                : new MonotoneCubicCurve(
                    new[] { 0.0, w.Min, w.Best, w.Max, w.End },
                    new[] { 0.0, MinPoint, BestPoint, MaxPoint, EndPoint });
            _cachedWindow = w;
            _cachedCurve = c;
            return c;
        }

        private static SlipAngleWindow Usable(SlipAngleWindow w) => w.IsValid ? w : SlipAngleWindow.ShippedDefault;

        public static double Normalize(double alphaDeg, SlipAngleWindow w)
        {
            if (!ClampMath.IsFinite(alphaDeg) || alphaDeg <= 0.0) return 0.0;
            double n = CurveFor(Usable(w)).Evaluate(alphaDeg);
            return n < 0.0 ? 0.0 : (n > EndPoint ? EndPoint : n);
        }

        public static double NormalizeUnclamped(double alphaDeg, SlipAngleWindow w)
        {
            if (!ClampMath.IsFinite(alphaDeg) || alphaDeg <= 0.0) return 0.0;
            w = Usable(w);
            if (alphaDeg <= w.End) return Normalize(alphaDeg, w);
            return EndPoint + OvershootPointsPerHalfMax * (alphaDeg - w.End) / (0.5 * w.Max);
        }

        /// <summary>Inverse of <see cref="Normalize"/>: the smallest slip angle (deg) reaching
        /// <paramref name="normalized"/>. 100 and above give End; non-finite or non-positive give 0.</summary>
        public static double SourceFor(double normalized, SlipAngleWindow w)
        {
            if (!ClampMath.IsFinite(normalized) || normalized <= 0.0) return 0.0;
            w = Usable(w);
            if (normalized >= EndPoint) return w.End;

            double lo = 0.0, hi = w.End;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Normalize(mid, w) >= normalized) hi = mid; else lo = mid;
            }
            return hi;
        }
    }
}

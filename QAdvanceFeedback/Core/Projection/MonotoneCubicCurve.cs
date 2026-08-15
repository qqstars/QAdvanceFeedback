using System;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Fritsch-Carlson monotone cubic Hermite interpolation over strictly increasing x. Ported
    /// verbatim from the sibling ReliableWheelLockSlip project's Core/MonotoneCubicCurve.cs (per the
    /// brief: "port the sibling project's output projector wholesale ... exactly the same
    /// behaviour"). Only the namespace changed.
    /// <para/>
    /// The tangent at each knot is limited so the interpolant is provably monotone between knots
    /// whose y-values are non-decreasing, while remaining C1-smooth. This matters because the
    /// anchors here are user-editable: a plain cubic spline (natural or not-a-knot) can dip below
    /// a knot whenever the data contains a flat interior segment -- e.g. two anchors sharing the
    /// same output -- which a driver typing into spinners produces routinely, not as an edge case.
    /// Tangents are precomputed in the constructor; evaluation is clamped at both ends and
    /// allocation-free, so it is safe in the per-frame path.
    /// </summary>
    public sealed class MonotoneCubicCurve
    {
        private readonly double[] _xs;
        private readonly double[] _ys;
        private readonly double[] _m;

        public MonotoneCubicCurve(double[] xs, double[] ys)
        {
            if (xs == null) throw new ArgumentNullException(nameof(xs));
            if (ys == null) throw new ArgumentNullException(nameof(ys));
            if (xs.Length != ys.Length) throw new ArgumentException("xs and ys must be the same length.");
            if (xs.Length < 2) throw new ArgumentException("At least two control points are required.");
            for (int i = 1; i < xs.Length; i++)
                if (xs[i] <= xs[i - 1]) throw new ArgumentException("xs must be strictly increasing.");

            _xs = (double[])xs.Clone();
            _ys = (double[])ys.Clone();
            _m = BuildTangents(_xs, _ys);
        }

        /// <summary>Fritsch-Carlson tangent construction: initial secant-averaged tangents, then
        /// limited per interval so the Hermite interpolant cannot overshoot into a decrease.</summary>
        private static double[] BuildTangents(double[] xs, double[] ys)
        {
            int n = xs.Length;
            int segments = n - 1;

            var d = new double[segments];
            for (int i = 0; i < segments; i++)
                d[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);

            var m = new double[n];
            m[0] = d[0];
            m[n - 1] = d[segments - 1];
            for (int i = 1; i < n - 1; i++)
                m[i] = (d[i - 1] + d[i]) / 2.0;

            for (int i = 0; i < segments; i++)
            {
                double di = d[i];
                if (di == 0.0)
                {
                    m[i] = 0.0;
                    m[i + 1] = 0.0;
                    continue;
                }

                double a = m[i] / di;
                double b = m[i + 1] / di;
                if (a < 0.0) m[i] = 0.0;
                if (b < 0.0) m[i + 1] = 0.0;

                // Recompute from the (possibly just-zeroed) tangents, not the pre-zeroing values --
                // otherwise a negative a/b that triggers the sumSq>9 rescale gets resurrected by
                // t*a*di / t*b*di below, undoing the zeroing and reintroducing a decrease.
                a = m[i] / di;
                b = m[i + 1] / di;
                double sumSq = a * a + b * b;
                if (sumSq > 9.0)
                {
                    double t = 3.0 / Math.Sqrt(sumSq);
                    m[i] = t * a * di;
                    m[i + 1] = t * b * di;
                }
            }

            return m;
        }

        public double Evaluate(double x)
        {
            if (double.IsNaN(x)) return _ys[0];

            int last = _xs.Length - 1;
            if (x <= _xs[0]) return _ys[0];
            if (x >= _xs[last]) return _ys[last];

            int i = 1;
            while (i < last && x > _xs[i]) i++;

            double x0 = _xs[i - 1], x1 = _xs[i];
            double y0 = _ys[i - 1], y1 = _ys[i];
            double m0 = _m[i - 1], m1 = _m[i];
            double h = x1 - x0;
            double s = ClampMath.SafeDiv(x - x0, h, 0.0);
            double s2 = s * s;
            double s3 = s2 * s;

            double h00 = 2.0 * s3 - 3.0 * s2 + 1.0;
            double h10 = s3 - 2.0 * s2 + s;
            double h01 = -2.0 * s3 + 3.0 * s2;
            double h11 = s3 - s2;

            return y0 * h00 + h * m0 * h10 + y1 * h01 + h * m1 * h11;
        }
    }
}

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Monotone piecewise-linear curve over strictly increasing x. Ported verbatim from the sibling
    /// ReliableWheelLockSlip project's Core/PiecewiseCurve.cs - kept only because
    /// MonotoneCubicCurveTests' smoothness-comparison test (ported below) needs a polyline to compare
    /// against; not used by the plugin's own output shaping (that is <see cref="OutputProjector"/>'s
    /// job). Evaluation is clamped at both ends and allocation-free.
    /// </summary>
    public sealed class PiecewiseCurve
    {
        private readonly double[] _xs;
        private readonly double[] _ys;

        public PiecewiseCurve(double[] xs, double[] ys)
        {
            if (xs == null) throw new System.ArgumentNullException(nameof(xs));
            if (ys == null) throw new System.ArgumentNullException(nameof(ys));
            if (xs.Length != ys.Length) throw new System.ArgumentException("xs and ys must be the same length.");
            if (xs.Length < 2) throw new System.ArgumentException("At least two control points are required.");
            for (int i = 1; i < xs.Length; i++)
                if (xs[i] <= xs[i - 1]) throw new System.ArgumentException("xs must be strictly increasing.");

            _xs = (double[])xs.Clone();
            _ys = (double[])ys.Clone();
        }

        public double Evaluate(double x)
        {
            if (double.IsNaN(x)) return _ys[0];
            if (x <= _xs[0]) return _ys[0];
            int last = _xs.Length - 1;
            if (x >= _xs[last]) return _ys[last];

            int i = 1;
            while (i < last && x > _xs[i]) i++;

            double x0 = _xs[i - 1], x1 = _xs[i];
            double y0 = _ys[i - 1], y1 = _ys[i];
            double t = ClampMath.SafeDiv(x - x0, x1 - x0, 0.0);
            return y0 + t * (y1 - y0);
        }
    }
}

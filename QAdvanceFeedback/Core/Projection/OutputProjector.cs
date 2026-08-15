using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Shapes the semantic 0-100 lock/slip value into the shaker strength the driver actually feels.
    /// Ported verbatim from the sibling ReliableWheelLockSlip project's
    /// Core/OutputProjector.cs::OutputProjector (per the brief: "port the sibling project's output
    /// projector wholesale ... it is well-tested and the owner wants exactly the same behaviour").
    /// The control-point curve is built once, from settings that have first been clamped/sorted and
    /// then had the strictly-increasing/non-decreasing/closing-point rules applied (see
    /// <see cref="BuildControlPoints"/>); <see cref="Project"/> only evaluates the result, so it is
    /// safe in the per-frame path.
    /// </summary>
    public sealed class OutputProjector
    {
        private readonly MonotoneCubicCurve _curve;

        public OutputProjector(ProjectorSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            ProjectorSettings s = settings.WithClampedSortedAnchors();
            double[] xs, ys;
            BuildControlPoints(s, out xs, out ys);
            _curve = new MonotoneCubicCurve(xs, ys);
        }

        public double Project(double value0100)
        {
            if (double.IsNaN(value0100)) return 0.0;
            return ClampMath.To0100(_curve.Evaluate(value0100));
        }

        /// <summary>
        /// (0,0), then (StartInput, 0) if StartInput is above zero, then each anchor whose input is
        /// strictly less than EndInput AND strictly greater than the point before it (dropping
        /// anchors at/below the start point, anchors at/above the end point, and duplicate/
        /// out-of-order survivors alike), with outputs forced non-decreasing, then (EndInput, 100)
        /// closing the shaped part of the curve, and finally (100,100) unless a point already sits
        /// at input 100. Falls back to the straight line (0,0)-(100,100) if fewer than two points
        /// survive.
        /// </summary>
        private static void BuildControlPoints(ProjectorSettings s, out double[] xs, out double[] ys)
        {
            var px = new List<double> { 0.0 };
            var py = new List<double> { 0.0 };

            if (s.StartInput > 0.0)
                Accept(px, py, s.StartInput, 0.0);

            if (s.SlightlyInput < s.EndInput) Accept(px, py, s.SlightlyInput, s.SlightlyOutput);
            if (s.ModerateInput < s.EndInput) Accept(px, py, s.ModerateInput, s.ModerateOutput);
            if (s.CriticalInput < s.EndInput) Accept(px, py, s.CriticalInput, s.CriticalOutput);

            Accept(px, py, s.EndInput, 100.0);

            if (px[px.Count - 1] < 100.0)
            {
                px.Add(100.0);
                py.Add(100.0);
            }

            if (px.Count < 2)
            {
                xs = new[] { 0.0, 100.0 };
                ys = new[] { 0.0, 100.0 };
                return;
            }

            xs = px.ToArray();
            ys = py.ToArray();
        }

        private static void Accept(List<double> px, List<double> py, double x, double y)
        {
            double lastX = px[px.Count - 1];
            if (x <= lastX) return;

            double lastY = py[py.Count - 1];
            px.Add(x);
            py.Add(Math.Max(y, lastY));
        }
    }
}

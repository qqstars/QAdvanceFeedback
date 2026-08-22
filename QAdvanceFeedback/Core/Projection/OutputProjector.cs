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
        /// (0,StartOutput), then (StartInput, StartOutput) if StartInput is above zero, then each anchor
        /// whose input is strictly less than EndInput AND strictly greater than the point before it
        /// (dropping anchors at/below the start point, anchors at/above the end point, and duplicate/
        /// out-of-order survivors alike), with outputs forced non-decreasing, then (EndInput, EndOutput)
        /// closing the shaped part of the curve, and finally (100,EndOutput) unless a point already sits
        /// at input 100. Falls back to the straight line (0,StartOutput)-(100,EndOutput) if fewer than
        /// two points survive.
        /// <para/>
        /// PRE-RELEASE ADDITION (configurable Start/End OUTPUTS - <see cref="ProjectorSettings.StartOutput"/>/
        /// <see cref="ProjectorSettings.EndOutput"/>): used to be hard-fixed at 0/100; both ends of the
        /// point list now use the configured values instead, giving a CONTINUOUS floor/ceiling (every
        /// input at/below StartInput reads exactly StartOutput; every input at/above EndInput reads
        /// exactly EndOutput) rather than a step - see <see cref="ProjectorSettings"/>'s own remarks on
        /// why a step was rejected. MONOTONICITY POLICY when a driver's StartOutput/EndOutput conflicts
        /// with an anchor's own output (e.g. StartOutput above Slightly's own output, or EndOutput below
        /// Max Grip's own output): this is NOT rejected/validated on entry - it is silently resolved at
        /// EVALUATION time by the SAME <see cref="Accept"/> non-decreasing clamp every anchor/hidden
        /// point already goes through (<c>Math.Max(y, lastY)</c>). Concretely: a too-high StartOutput
        /// raises every following anchor's own EFFECTIVE output up to at least itself (a driver's
        /// Slightly/Ideal/Max Grip settings that sit below it are silently absorbed into a longer flat
        /// start, never causing a decrease); a too-low EndOutput is itself raised up to whatever height
        /// the curve had already reached by EndInput (it can never pull the curve back DOWN - "cap the
        /// maximum" only actually caps if the requested cap is at or above every anchor's own output).
        /// If StartOutput ends up at or above EndOutput once all anchors are folded in, the entire curve
        /// degenerates to a flat line at that shared height - a well-defined, monotone (trivially),
        /// non-crashing outcome, not an error. See <c>OutputProjectorTests</c>' own pinned regressions
        /// for all four combinations the brief asked to be tested.
        /// <para/>
        /// PRE-RELEASE Change 2b (configurable flatten ranges): each of the three named anchors also
        /// gets up to two HIDDEN control points inserted around it - see
        /// <see cref="AcceptSetpointWithFlatten"/> for the exact mechanism and formula. Skipped entirely
        /// under the Linear preset, which must remain an exact straight line (a flattened plateau would
        /// break that).
        /// </summary>
        private static void BuildControlPoints(ProjectorSettings s, out double[] xs, out double[] ys)
        {
            var px = new List<double> { 0.0 };
            var py = new List<double> { s.StartOutput };

            if (s.StartInput > 0.0)
                Accept(px, py, s.StartInput, s.StartOutput);

            bool flattenEnabled = s.Preset != ProjectorPreset.Linear;
            double effectiveStartInput = s.StartInput > 0.0 ? s.StartInput : 0.0;

            AcceptSetpointWithFlatten(px, py, s.EndInput, flattenEnabled,
                s.SlightlyInput, s.SlightlyOutput, s.SlightlyFlattenRange,
                effectiveStartInput, s.StartOutput, s.ModerateInput, s.ModerateOutput);
            AcceptSetpointWithFlatten(px, py, s.EndInput, flattenEnabled,
                s.ModerateInput, s.ModerateOutput, s.ModerateFlattenRange,
                s.SlightlyInput, s.SlightlyOutput, s.CriticalInput, s.CriticalOutput);
            AcceptSetpointWithFlatten(px, py, s.EndInput, flattenEnabled,
                s.CriticalInput, s.CriticalOutput, s.CriticalFlattenRange,
                s.ModerateInput, s.ModerateOutput, s.EndInput, s.EndOutput);

            Accept(px, py, s.EndInput, s.EndOutput);

            if (px[px.Count - 1] < 100.0)
            {
                px.Add(100.0);
                py.Add(Math.Max(s.EndOutput, py[py.Count - 1]));
            }

            if (px.Count < 2)
            {
                xs = new[] { 0.0, 100.0 };
                ys = new[] { s.StartOutput, s.EndOutput };
                return;
            }

            xs = px.ToArray();
            ys = py.ToArray();
        }

        /// <summary>
        /// FLATTEN-RANGE bleed fraction (pre-release Change 2b, owner's own explicit per-setpoint
        /// plateau request): how much of a full LINEAR extrapolation toward each neighbouring anchor a
        /// hidden control point at the edge of a setpoint's own flatten range is allowed to drift by,
        /// before being pulled back toward the setpoint's own output. Chosen by direct measurement
        /// against the owner's own two worked examples (30-&gt;10 with range 3 next to Start 20-&gt;0,
        /// expecting the 27/33 hidden points to land "very close to 10, e.g. ~9.5 and ~10.5"; and an
        /// anchor with a much larger local gradient and range 7, expecting "may deviate more, e.g.
        /// ~67 and ~73"): a fixed 20% bleed reproduces both examples within a point or so (0.6 vs the
        /// requested ~0.5 at the small range; ~2.1-2.8 vs the requested ~3 at the large one) while being
        /// a single, simple, size-independent constant - the actual plateau WIDTH is controlled entirely
        /// by the range itself (the hidden points sit exactly at setpoint+/-range), so this fraction only
        /// has to govern how much OUTPUT drift is still allowed inside that band, not how wide the band
        /// is.
        /// </summary>
        private const double FlattenBleedFraction = 0.2;

        /// <summary>
        /// Inserts, in x-order, the LEFT hidden point (if any), the setpoint itself, then the RIGHT
        /// hidden point (if any) - all via <see cref="Accept"/>, so every existing ordering/monotonicity/
        /// duplicate-collapsing guarantee applies unconditionally to the hidden points too, with zero
        /// special-casing.
        /// <para/>
        /// THE HIDDEN POINTS: for a setpoint at (<paramref name="x"/>, <paramref name="y"/>) with its own
        /// <paramref name="range"/> (the owner's own per-setpoint flatten-range spinner), the hidden
        /// points sit at exactly <c>x - effectiveRange</c> and <c>x + effectiveRange</c> - the range IS
        /// the plateau's own half-width, so a larger range directly makes a visibly longer near-flat
        /// band, exactly as asked. <paramref name="range"/> is first CLAMPED to at most half the distance
        /// to whichever real neighbouring anchor sits on that side (<paramref name="leftNeighborX"/>/
        /// <paramref name="rightNeighborX"/>) - halving guarantees that even if BOTH this setpoint and
        /// its neighbour extend their own ranges maximally, their plateaus can never cross or overlap
        /// each other. A range of 0 (or a neighbour close enough to clamp it to 0) naturally degrades to
        /// "no hidden points" - the hidden points would coincide with the setpoint's own input, and
        /// <see cref="Accept"/> already drops any point at or below the point before it, so no special
        /// "range==0" branch is needed.
        /// <para/>
        /// THE HIDDEN OUTPUTS: each hidden point's output is the setpoint's own output, nudged toward
        /// (never past) what a straight line to that side's real neighbour would give at that input, by
        /// <see cref="FlattenBleedFraction"/> of the full linear difference - see that constant's own
        /// remarks for the derivation and the owner's worked-example comparison. Because the nudge is
        /// always a fraction of the LOCAL secant slope in the direction of a non-decreasing neighbour,
        /// the hidden point's output is always between the setpoint's own output and the neighbour's
        /// (never past either), so this can never introduce a decrease - <see cref="Accept"/>'s own
        /// non-decreasing enforcement is a second, independent safety net on top of that.
        /// </summary>
        private static void AcceptSetpointWithFlatten(
            List<double> px, List<double> py, double endInput, bool flattenEnabled,
            double x, double y, double range,
            double leftNeighborX, double leftNeighborY, double rightNeighborX, double rightNeighborY)
        {
            if (x >= endInput) return; // matches the original "anchor at/above the end is dropped" rule

            if (flattenEnabled && range > 0.0)
            {
                double effectiveRange = range;
                effectiveRange = Math.Min(effectiveRange, (x - leftNeighborX) / 2.0);
                effectiveRange = Math.Min(effectiveRange, (rightNeighborX - x) / 2.0);
                effectiveRange = Math.Min(effectiveRange, endInput - x);
                if (effectiveRange > 0.0)
                {
                    double slopeLeft = ClampMath.SafeDiv(y - leftNeighborY, x - leftNeighborX, 0.0);
                    double slopeRight = ClampMath.SafeDiv(rightNeighborY - y, rightNeighborX - x, 0.0);

                    Accept(px, py, x - effectiveRange, y - FlattenBleedFraction * slopeLeft * effectiveRange);
                    Accept(px, py, x, y);
                    Accept(px, py, x + effectiveRange, y + FlattenBleedFraction * slopeRight * effectiveRange);
                    return;
                }
            }

            Accept(px, py, x, y);
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

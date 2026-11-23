using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The Slip Angle tab's "Source -> Projected" graph (docs\slip-angle-design.md 9.9): Raw.All in
    /// DEGREES, 0 to End (1.5 x Max), through the same normalization the engine uses
    /// (<see cref="SlipAngleNormalizer.Normalize"/>) and the projector curve. The Slip Angle counterpart of
    /// <see cref="SourceToProjectedGraph"/>.
    /// <para/>
    /// Sampled every <see cref="DefaultStepDeg"/>, plus Min / Best / Max / End themselves, so the curve
    /// always passes exactly through the key angles even when they are off the grid. Points are in value
    /// coordinates (X = degrees, Y = projected 0-100, pulse-free). An invalid window falls back to the
    /// shipped default, as the normalizer does.
    /// </summary>
    public static class SlipAngleSourceToProjectedGraph
    {
        public const double DefaultStepDeg = 0.25;

        public static IReadOnlyList<GraphPoint> Sample(SlipAngleWindow window, ProjectorSettings projectorSettings, double stepDeg = DefaultStepDeg)
        {
            var points = new List<GraphPoint>();
            if (projectorSettings == null) return points;
            SlipAngleWindow w = window.IsValid ? window : SlipAngleWindow.ShippedDefault;
            if (!ClampMath.IsFinite(stepDeg) || stepDeg <= 0.0) stepDeg = DefaultStepDeg;

            var xs = new SortedSet<double>();
            double end = w.End;
            int steps = (int)Math.Floor(end / stepDeg + 1e-9);
            for (int i = 0; i <= steps; i++) xs.Add(Math.Min(i * stepDeg, end));
            if (w.Start.HasValue) xs.Add(w.Start.Value);
            xs.Add(w.Min);
            xs.Add(w.Best);
            xs.Add(w.Max);
            xs.Add(end);

            var projector = new OutputProjector(projectorSettings);
            double previous = double.NegativeInfinity;
            foreach (double x in xs)
            {
                if (x - previous < 1e-9) continue;   // a key angle within float noise of a grid point
                previous = x;
                double normalized = SlipAngleNormalizer.Normalize(x, w);
                points.Add(new GraphPoint(x, projector.Project(ClampMath.To0100(normalized))));
            }
            return points;
        }
    }
}

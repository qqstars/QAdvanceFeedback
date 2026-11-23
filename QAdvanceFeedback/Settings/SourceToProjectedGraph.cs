using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The "Source value -> Projected value" graph: the SOURCE value all the way through to the
    /// published projection. Drawn by the plugin settings page and by the read-only graph of the native
    /// WheelLock/WheelSlip ShakeIt effects, so both show the same curve.
    /// <para/>
    /// There is no closed form for this - the key data points and the projector curve compose - so it
    /// is SAMPLED every 5 across 0-100. The normalization step mirrors what the engine does: the
    /// four-range curve under Mapping, or the two-range (SMax) curve under Max-Grip-Only. A missing value
    /// falls back to the channel default SMax, and S90/S75 to the fractions derived from SMax.
    /// </summary>
    public static class SourceToProjectedGraph
    {
        public static IReadOnlyList<GraphPoint> Points(ProjectorSettings projectorSettings, bool isLock, bool maxGripOnly,
            double? sMax, double? s90, double? s75, double width, double height)
        {
            var points = new List<GraphPoint>();
            if (projectorSettings == null) return points;
            var projector = new OutputProjector(projectorSettings);

            // The SAME piecewise construction the engine uses (KeyDataNormalization) - shared with the
            // Lock/Slip Test Effect, so the test plays exactly what this graph shows.
            for (int source = 0; source <= 100; source += 5)
            {
                double normalized = KeyDataNormalization.Normalize(source, isLock, maxGripOnly, sMax, s90, s75);
                double projected = projector.Project(ClampMath.To0100(normalized));
                points.Add(new GraphPoint(source / 100.0 * width, height - projected / 100.0 * height));
            }
            return points;
        }
    }
}

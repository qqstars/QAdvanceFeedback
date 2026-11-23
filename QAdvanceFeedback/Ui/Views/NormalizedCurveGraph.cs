using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The "Normalized value -> Final Projected value" curve-editor plot, as a bindable control: the
    /// Wheel Lock tab's left graph (SettingsControl.RefreshCurvePlot / RenderGraphDecorations /
    /// RenderKeyPointMarkers) drawn from dependency properties instead of from page code-behind, so an
    /// MVVM view can host it. Same size, grid, baseline, ticks, dashed key-point markers with their
    /// labels, blue curve and the five coloured anchor dots. Value coordinates 0-100 on both axes.
    /// </summary>
    public sealed class NormalizedCurveGraph : Canvas
    {
        public static readonly DependencyProperty CurveProperty = Register(nameof(Curve), typeof(IReadOnlyList<GraphPoint>));
        public static readonly DependencyProperty AnchorsProperty = Register(nameof(Anchors), typeof(IReadOnlyList<GraphPoint>));
        public static readonly DependencyProperty MarkersProperty = Register(nameof(Markers), typeof(IReadOnlyList<GraphMarker>));

        /// <summary>The curve, normalized (X) -> projected (Y).</summary>
        public IReadOnlyList<GraphPoint> Curve { get => (IReadOnlyList<GraphPoint>)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
        /// <summary>Start / Slightly / Moderate / Critical / End, in that order.</summary>
        public IReadOnlyList<GraphPoint> Anchors { get => (IReadOnlyList<GraphPoint>)GetValue(AnchorsProperty); set => SetValue(AnchorsProperty, value); }
        /// <summary>The dashed key-point markers (ascending).</summary>
        public IReadOnlyList<GraphMarker> Markers { get => (IReadOnlyList<GraphMarker>)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

        private const double W = 420, H = 160, Dot = 8.0;
        private static readonly Brush[] DotBrushes = { Brushes.Gray, Brushes.Orange, Brushes.DodgerBlue, Brushes.Red, Brushes.Gray };

        private static DependencyProperty Register(string name, System.Type type) =>
            DependencyProperty.Register(name, type, typeof(NormalizedCurveGraph), new PropertyMetadata(null, (d, e) => ((NormalizedCurveGraph)d).Redraw()));

        public NormalizedCurveGraph()
        {
            Width = W;
            Height = H;
            Background = Brushes.White;
            ClipToBounds = true;
            Redraw();
        }

        private static double X(double v) => ClampMath.To0100(v) / 100.0 * W;
        private static double Y(double v) => H - ClampMath.To0100(v) / 100.0 * H;

        private void Redraw()
        {
            Children.Clear();
            GraphFurniture.AddGrid(Children, W, H);
            GraphFurniture.AddKeyMarkers(Children, Markers, W, H);

            IReadOnlyList<GraphPoint> curve = Curve;
            if (curve != null && curve.Count > 0)
            {
                var points = new PointCollection();
                foreach (GraphPoint p in curve) points.Add(new Point(X(p.X), Y(p.Y)));
                Children.Add(new Polyline { Points = points, Stroke = Brushes.DodgerBlue, StrokeThickness = 2 });
            }

            IReadOnlyList<GraphPoint> anchors = Anchors;
            if (anchors == null) return;
            for (int i = 0; i < anchors.Count && i < DotBrushes.Length; i++)
            {
                var dot = new Ellipse { Width = Dot, Height = Dot, Fill = DotBrushes[i] };
                SetLeft(dot, X(anchors[i].X) - Dot / 2.0);
                SetTop(dot, Y(anchors[i].Y) - Dot / 2.0);
                Children.Add(dot);
            }
        }
    }
}

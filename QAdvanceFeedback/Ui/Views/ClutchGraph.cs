using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The read-only Clutch input → Output graph (docs\clutch-bite-design.md, "Graph"): the S-curve,
    /// the shaded 5-point deadzones, the bite marker, a faint configured-bite marker under Auto, and the
    /// Test Effect ball. Everything comes from its dependency properties.
    /// </summary>
    public sealed class ClutchGraph : Canvas
    {
        public static readonly DependencyProperty CurveProperty = Register(nameof(Curve), typeof(IReadOnlyList<GraphPoint>));
        public static readonly DependencyProperty BiteMarkerProperty = Register(nameof(BiteMarker), typeof(double), 50.0);
        public static readonly DependencyProperty ConfiguredMarkerProperty = Register(nameof(ConfiguredMarker), typeof(double?));
        public static readonly DependencyProperty TestBallProperty = Register(nameof(TestBall), typeof(GraphPoint?));

        public IReadOnlyList<GraphPoint> Curve { get => (IReadOnlyList<GraphPoint>)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
        public double BiteMarker { get => (double)GetValue(BiteMarkerProperty); set => SetValue(BiteMarkerProperty, value); }
        public double? ConfiguredMarker { get => (double?)GetValue(ConfiguredMarkerProperty); set => SetValue(ConfiguredMarkerProperty, value); }
        public GraphPoint? TestBall { get => (GraphPoint?)GetValue(TestBallProperty); set => SetValue(TestBallProperty, value); }

        private const double W = 420, H = 160, TickHeight = 5.0, BallSize = 13.0;
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(26, 0, 0, 0));
        private static readonly Brush DeadzoneBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
        private static readonly Brush CurveBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
        private static readonly Brush BiteBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        private static readonly Brush ConfiguredBrush = new SolidColorBrush(Color.FromArgb(110, 0x60, 0x60, 0x60));

        private static DependencyProperty Register(string name, System.Type type, object defaultValue = null) =>
            DependencyProperty.Register(name, type, typeof(ClutchGraph), new PropertyMetadata(defaultValue, (d, e) => ((ClutchGraph)d).Redraw()));

        public ClutchGraph()
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

            // Deadzones 0..5 and 95..100.
            AddRect(0, X(5), DeadzoneBrush);
            AddRect(X(95), W - X(95), DeadzoneBrush);

            for (int pct = 0; pct <= 100; pct += 20)
            {
                double y = H - pct / 100.0 * H;
                if (pct == 0) y -= 0.5;
                Children.Add(new Line { X1 = 0, X2 = W, Y1 = y, Y2 = y, Stroke = pct == 0 ? Brushes.Black : GridBrush, StrokeThickness = 1 });
                double x = pct == 100 ? W - 1 : pct / 100.0 * W;
                Children.Add(new Line { X1 = x, X2 = x, Y1 = H - TickHeight, Y2 = H, Stroke = Brushes.Black, StrokeThickness = 1 });
            }

            if (ConfiguredMarker.HasValue)
                Children.Add(new Line { X1 = X(ConfiguredMarker.Value), X2 = X(ConfiguredMarker.Value), Y1 = 0, Y2 = H, Stroke = ConfiguredBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            Children.Add(new Line { X1 = X(BiteMarker), X2 = X(BiteMarker), Y1 = 0, Y2 = H, Stroke = BiteBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 2 } });

            IReadOnlyList<GraphPoint> curve = Curve;
            if (curve != null && curve.Count > 0)
            {
                var points = new PointCollection();
                foreach (GraphPoint p in curve) points.Add(new Point(X(p.X), Y(p.Y)));
                Children.Add(new Polyline { Points = points, Stroke = CurveBrush, StrokeThickness = 2 });
            }

            if (TestBall.HasValue)
            {
                GraphPoint b = TestBall.Value;
                var ball = new Ellipse
                {
                    Width = BallSize, Height = BallSize,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x8E, 0x1B, 0x1B)), StrokeThickness = 1,
                    Fill = new RadialGradientBrush(Color.FromRgb(0xFF, 0x8A, 0x80), Color.FromRgb(0xC6, 0x28, 0x28)) { GradientOrigin = new Point(0.35, 0.30), RadiusX = 0.75, RadiusY = 0.75 },
                };
                SetLeft(ball, X(b.X) - BallSize / 2.0);
                SetTop(ball, Y(b.Y) - BallSize / 2.0);
                Children.Add(ball);
            }
        }

        private void AddRect(double left, double width, Brush fill)
        {
            var r = new Rectangle { Width = width, Height = H, Fill = fill };
            SetLeft(r, left);
            SetTop(r, 0);
            Children.Add(r);
        }
    }
}

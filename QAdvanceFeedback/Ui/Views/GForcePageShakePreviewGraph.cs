using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The G-Force tab's LEFT/RIGHT PREVIEW GRAPH (v1.0.8): one second of the shake, green Left / red
    /// Right. The traces come from its dependency properties in unit coordinates (seconds 0..1, level
    /// 0..1 - <c>GForcePageShakePreview</c>); this control only maps them to pixels and draws the axes.
    /// Ported unchanged from the old page's <c>RefreshShakePreview</c> (Settings\SettingsControl.GForceTest.cs):
    /// every number is matched to the Lock/Slip curve plot, so the two graphs read as the same instrument.
    /// </summary>
    public sealed class GForcePageShakePreviewGraph : Canvas
    {
        public static readonly DependencyProperty LeftTraceProperty = Register(nameof(LeftTrace));
        public static readonly DependencyProperty RightTraceProperty = Register(nameof(RightTrace));

        public IReadOnlyList<GraphPoint> LeftTrace { get => (IReadOnlyList<GraphPoint>)GetValue(LeftTraceProperty); set => SetValue(LeftTraceProperty, value); }
        public IReadOnlyList<GraphPoint> RightTrace { get => (IReadOnlyList<GraphPoint>)GetValue(RightTraceProperty); set => SetValue(RightTraceProperty, value); }

        /// <summary>Same 2 px the Lock/Slip curve polyline uses.</summary>
        private const double PreviewStroke = 2.0;

        /// <summary>Same 5 px as <c>HorizontalTickHeight</c> on the Lock/Slip plot. (The owner recalled
        /// 8 px; the shipped value is 5.)</summary>
        private const double PreviewTick = 5.0;

        private const double PreviewMarginTop = 10.0;
        private const double PreviewMarginRight = 10.0;
        private const double PreviewMarginLeft = 34.0;    // room for the "100%" label
        private const double PreviewLabelGap = 3.0;       // clear space under the ms labels

        /// <summary>How far the 0 ms rule overshoots the 100% line, so it reads as an origin.</summary>
        private const double PreviewOriginOvershoot = 5.0;

        private static readonly Brush PreviewLeftBrush = Brushes.ForestGreen;
        private static readonly Brush PreviewRightBrush = Brushes.Crimson;
        private static readonly Brush PreviewAxisBrush = Brushes.Black;
        private static readonly Brush PreviewGridBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));

        private static DependencyProperty Register(string name) =>
            DependencyProperty.Register(name, typeof(IReadOnlyList<GraphPoint>), typeof(GForcePageShakePreviewGraph),
                new PropertyMetadata(null, (d, e) => ((GForcePageShakePreviewGraph)d).Redraw()));

        public GForcePageShakePreviewGraph()
        {
            Width = 420;
            Height = 200;
            Background = Brushes.White;
            ClipToBounds = true;
            Redraw();
        }

        private void Redraw()
        {
            Children.Clear();

            double width = Width;
            double height = Height;
            double plotLeft = PreviewMarginLeft;
            double plotRight = width - PreviewMarginRight;
            double labelHeight = 14.0;
            double axisY = height - PreviewLabelGap - labelHeight - PreviewTick;
            double plotTop = PreviewMarginTop;

            // THE CURVE MUST NOT TOUCH EITHER RULE. Half a stroke of clearance at both ends keeps the
            // line fully between the 0 axis and the (invisible) 100% line, so neither is obscured.
            double half = PreviewStroke / 2.0;
            double zeroY = axisY - half;
            double fullY = plotTop + half;

            // ---- gridlines every 200 ms, labelled 0..800 (never 1000 - it sits on the right edge). ----
            for (int ms = 0; ms <= 1000; ms += 200)
            {
                double x = plotLeft + (plotRight - plotLeft) * (ms / 1000.0);
                bool isOrigin = ms == 0;

                Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = isOrigin ? plotTop - PreviewOriginOvershoot : plotTop,
                    Y2 = axisY,
                    Stroke = isOrigin ? PreviewAxisBrush : PreviewGridBrush,
                    StrokeThickness = 1,
                });

                Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = axisY, Y2 = axisY + PreviewTick,
                    Stroke = PreviewAxisBrush, StrokeThickness = 1,
                });

                if (ms == 1000) continue;   // the owner's explicit exception

                var label = new TextBlock
                {
                    Text = ms.ToString(CultureInfo.InvariantCulture) + "ms",
                    FontSize = 10,
                    Foreground = PreviewAxisBrush,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                SetLeft(label, x - (ms == 0 ? 0 : label.DesiredSize.Width / 2.0));
                SetTop(label, axisY + PreviewTick);
                Children.Add(label);
            }

            // ---- the 0 rule, and the 100% marker on the left of the origin. ----
            Children.Add(new Line
            {
                X1 = plotLeft, X2 = plotRight, Y1 = axisY, Y2 = axisY,
                Stroke = PreviewAxisBrush, StrokeThickness = 1,
            });

            var fullLabel = new TextBlock { Text = "100%", FontSize = 10, Foreground = PreviewAxisBrush };
            fullLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(fullLabel, plotLeft - fullLabel.DesiredSize.Width - 3.0);
            SetTop(fullLabel, plotTop - fullLabel.DesiredSize.Height / 2.0);
            Children.Add(fullLabel);

            // ---- the two traces. ----
            AddTrace(LeftTrace, PreviewLeftBrush, plotLeft, plotRight, zeroY, fullY);
            AddTrace(RightTrace, PreviewRightBrush, plotLeft, plotRight, zeroY, fullY);
        }

        private void AddTrace(IReadOnlyList<GraphPoint> trace, Brush brush, double plotLeft, double plotRight, double zeroY, double fullY)
        {
            if (trace == null || trace.Count == 0) return;
            var points = new PointCollection(trace.Count);
            foreach (GraphPoint p in trace)
                points.Add(new Point(plotLeft + (plotRight - plotLeft) * p.X, zeroY + (fullY - zeroY) * p.Y));
            Children.Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = PreviewStroke });
        }
    }
}

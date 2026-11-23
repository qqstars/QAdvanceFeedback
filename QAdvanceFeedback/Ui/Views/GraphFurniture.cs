using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// The furniture every value graph shares (the Wheel Lock tab's RenderGraphDecorations /
    /// RenderKeyPointMarkers look): a faint rule every 20% of height with a solid black baseline, a tick
    /// below every 20% of width, and dashed key-point markers with right-aligned labels that are dropped
    /// when they would cross the previous marker. Used by <see cref="NormalizedCurveGraph"/> and
    /// <see cref="SourceProjectedGraph"/>.
    /// </summary>
    internal static class GraphFurniture
    {
        public const double TickHeight = 5.0;
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(26, 0, 0, 0));      // 10% black
        private static readonly Brush KeyPointBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)); // 50% black

        /// <summary>Grid rules, baseline and bottom ticks for a <paramref name="w"/> x <paramref name="h"/> canvas.</summary>
        public static void AddGrid(UIElementCollection children, double w, double h)
        {
            for (int pct = 0; pct <= 100; pct += 20)
            {
                double y = h - pct / 100.0 * h;
                if (pct == 0) y -= 0.5;   // keep the 1px baseline crisp
                children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = pct == 0 ? Brushes.Black : GridBrush, StrokeThickness = 1 });
                double x = pct == 100 ? w - 1 : pct / 100.0 * w;   // the last tick would fall outside the clip
                children.Add(new Line { X1 = x, X2 = x, Y1 = h - TickHeight, Y2 = h, Stroke = Brushes.Black, StrokeThickness = 1 });
            }
        }

        /// <summary>Dashed vertical markers at each <see cref="GraphMarker.At"/> (0-100, ascending), labelled at the top.</summary>
        public static void AddKeyMarkers(UIElementCollection children, IReadOnlyList<GraphMarker> markers, double w, double h)
        {
            if (markers == null) return;
            for (int i = 0; i < markers.Count; i++)
            {
                double x = ClampMath.To0100(markers[i].At) / 100.0 * w;
                children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = KeyPointBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });

                var label = new TextBlock { Text = markers[i].Label, Foreground = KeyPointBrush, FontSize = 9, Margin = new Thickness(0) };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double width = label.DesiredSize.Width;
                double previous = i == 0 ? 0.0 : ClampMath.To0100(markers[i - 1].At) / 100.0 * w;
                if (x - width - 2 < previous + 1) continue;   // would cross the previous marker
                Canvas.SetLeft(label, x - width - 2);
                Canvas.SetTop(label, 1);
                children.Add(label);
            }
        }
    }
}

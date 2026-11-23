using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Ui.Mvvm
{
    /// <summary>
    /// WPF-free math behind <c>LabelColumnSync</c>: the natural width of a grid's leading (label) columns,
    /// computed from its cells' desired widths the way an Auto column sizes, and the MinWidth the last
    /// leading column needs so the controls after it start at a group's common x.
    /// </summary>
    public static class LeadingColumns
    {
        public readonly struct Cell
        {
            public Cell(int column, int span, double width) { Column = column; Span = Math.Max(1, span); Width = width; }
            public int Column { get; }
            public int Span { get; }
            public double Width { get; }
        }

        /// <summary>Cumulative natural edges of the first <paramref name="count"/> columns: element k is
        /// where column k starts (element 0 is 0, the last is the total). Cells reaching past the leading
        /// columns are ignored.</summary>
        public static double[] Natural(int count, IEnumerable<Cell> cells)
        {
            var list = new List<Cell>(cells);
            var edge = new double[count + 1];
            for (int k = 1; k <= count; k++)
            {
                edge[k] = edge[k - 1];
                foreach (var c in list)
                    if (c.Column + c.Span == k && c.Column >= 0) edge[k] = Math.Max(edge[k], edge[c.Column] + c.Width);
            }
            return edge;
        }

        public static double Total(double[] edges) => edges[edges.Length - 1];

        /// <summary>The MinWidth for the last leading column so the leading columns total
        /// <paramref name="target"/> (0 when they are already that wide).</summary>
        public static double LastColumnMinWidth(double[] edges, double target)
        {
            double start = edges.Length >= 2 ? edges[edges.Length - 2] : 0;
            return Math.Max(0, target - start);
        }
    }
}

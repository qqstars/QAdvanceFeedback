using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// Pure-layout attached behaviour (allowed by the MVVM rule): gives
    /// grids in DIFFERENT views - e.g. a ShakeIt panel and the Test Effect below its graph, which ShakeIt
    /// hosts separately so they cannot share one SharedSizeScope - the same label-column width, so their
    /// toggles, sliders and editors start at the same x.
    /// <list type="bullet">
    /// <item><c>Group="{Binding X}"</c> joins a group keyed by that object (any shared view-model
    /// instance). On a <see cref="Grid"/> it syncs that grid's first <c>Columns</c> columns (default 1);
    /// on any other element it syncs every descendant grid whose first column is
    /// <c>SharedSizeGroup="Label"</c>.</item>
    /// </list>
    /// Collapsed cells don't count, so the width follows what is shown. Re-checked after every layout pass.
    /// </summary>
    public static class LabelColumnSync
    {
        public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
            "Group", typeof(object), typeof(LabelColumnSync), new PropertyMetadata(null, OnGroupChanged));

        public static object GetGroup(DependencyObject d) => d.GetValue(GroupProperty);
        public static void SetGroup(DependencyObject d, object value) => d.SetValue(GroupProperty, value);

        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
            "Columns", typeof(int), typeof(LabelColumnSync), new PropertyMetadata(1));

        public static int GetColumns(DependencyObject d) => (int)d.GetValue(ColumnsProperty);
        public static void SetColumns(DependencyObject d, int value) => d.SetValue(ColumnsProperty, value);

        private static readonly ConditionalWeakTable<object, List<WeakReference<FrameworkElement>>> Groups =
            new ConditionalWeakTable<object, List<WeakReference<FrameworkElement>>>();
        private static readonly ConditionalWeakTable<FrameworkElement, Hook> Hooks = new ConditionalWeakTable<FrameworkElement, Hook>();

        private sealed class Hook { public EventHandler OnLayout; public bool Attached; }

        private static void OnGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement fe)) return;
            if (e.OldValue != null) Leave(e.OldValue, fe);
            if (e.NewValue != null) Join(e.NewValue, fe);

            if (Hooks.TryGetValue(fe, out _)) return;
            var hook = new Hook();
            hook.OnLayout = (s, a) => ViewGuard.Run(() => { var g = GetGroup(fe); if (g != null) Sync(g); });
            Hooks.Add(fe, hook);
            fe.Loaded += (s, a) => { if (!hook.Attached) { fe.LayoutUpdated += hook.OnLayout; hook.Attached = true; } };
            fe.Unloaded += (s, a) => { if (hook.Attached) { fe.LayoutUpdated -= hook.OnLayout; hook.Attached = false; } };
            if (fe.IsLoaded) { fe.LayoutUpdated += hook.OnLayout; hook.Attached = true; }
        }

        private static void Join(object group, FrameworkElement fe)
        {
            var members = Groups.GetOrCreateValue(group);
            members.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, fe));
            members.Add(new WeakReference<FrameworkElement>(fe));
        }

        private static void Leave(object group, FrameworkElement fe)
        {
            if (Groups.TryGetValue(group, out var members))
                members.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, fe));
        }

        private static void Sync(object group)
        {
            if (!Groups.TryGetValue(group, out var members)) return;
            var grids = new List<(Grid grid, int count, double[] edges)>();
            foreach (var w in members)
            {
                if (!w.TryGetTarget(out var fe) || !fe.IsLoaded) continue;
                if (fe is Grid g) Add(grids, g, Math.Max(1, GetColumns(g)));
                else foreach (var lg in LabelGrids(fe)) Add(grids, lg, 1);
            }
            if (grids.Count < 2) return;

            double target = grids.Max(x => LeadingColumns.Total(x.edges));
            foreach (var (grid, count, edges) in grids)
            {
                var col = grid.ColumnDefinitions[count - 1];
                double min = LeadingColumns.LastColumnMinWidth(edges, target);
                if (Math.Abs(col.MinWidth - min) > 0.5) col.MinWidth = min;
            }
        }

        private static void Add(List<(Grid, int, double[])> into, Grid grid, int count)
        {
            if (grid.ColumnDefinitions.Count < count + 1) return;
            var cells = new List<LeadingColumns.Cell>();
            foreach (UIElement child in grid.Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                cells.Add(new LeadingColumns.Cell(Grid.GetColumn(child), Grid.GetColumnSpan(child), child.DesiredSize.Width));
            }
            into.Add((grid, count, LeadingColumns.Natural(count, cells)));
        }

        /// <summary>Descendant grids whose first column is in the shared "Label" group, skipping any
        /// inside a collapsed parent (their cells measure 0 anyway).</summary>
        private static IEnumerable<Grid> LabelGrids(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is UIElement u && u.Visibility == Visibility.Collapsed) continue;
                if (child is Grid g && g.ColumnDefinitions.Count > 1 && g.ColumnDefinitions[0].SharedSizeGroup == "Label")
                    yield return g;
                foreach (var d in LabelGrids(child)) yield return d;
            }
        }
    }
}

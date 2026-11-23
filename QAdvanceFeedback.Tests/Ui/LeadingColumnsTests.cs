using System;
using System.IO;
using QAdvanceFeedback.Ui.Mvvm;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>The label-column sync math (LabelColumnSync): the natural width of a grid's leading
    /// columns, and the MinWidth its last leading column needs to reach a group's common width.</summary>
    public class LeadingColumnsTests
    {
        [Fact]
        public void Single_column_natural_width_is_the_widest_cell()
        {
            var w = LeadingColumns.Natural(1, new[] { new LeadingColumns.Cell(0, 1, 40), new LeadingColumns.Cell(0, 1, 145), new LeadingColumns.Cell(1, 1, 300) });
            Assert.Equal(new[] { 0.0, 145.0 }, w);
        }

        [Fact]
        public void Spanning_label_widens_only_the_last_leading_column_beyond_its_cells()
        {
            // label | "Auto" ; a long label spanning both
            var w = LeadingColumns.Natural(2, new[]
            {
                new LeadingColumns.Cell(0, 1, 150), new LeadingColumns.Cell(1, 1, 35), new LeadingColumns.Cell(0, 2, 200),
                new LeadingColumns.Cell(2, 1, 60),
            });
            Assert.Equal(new[] { 0.0, 150.0, 200.0 }, w);
        }

        [Fact]
        public void MinWidth_brings_the_last_leading_column_to_the_group_width()
        {
            double[] narrow = { 0, 30 };            // "Test"
            double[] wide = { 0, 150, 200 };        // G-Force panel: label | Auto
            double target = Math.Max(LeadingColumns.Total(narrow), LeadingColumns.Total(wide));
            Assert.Equal(200, LeadingColumns.LastColumnMinWidth(narrow, target));
            Assert.Equal(50, LeadingColumns.LastColumnMinWidth(wide, target));
        }

        // ---- Views: the Test Effect rows share the panel's label column ---------------------------------

        [Fact]
        public void Lock_slip_panel_and_graph_test_join_one_label_column_group()
        {
            string wheel = View("WheelChannelEffectView.xaml"), graph = View("GraphView.xaml");
            Assert.Contains("v:LabelColumnSync.Group=\"{Binding Test}\"", wheel);
            Assert.Contains("v:LabelColumnSync.Group=\"{Binding Test}\"", graph);
        }

        [Fact]
        public void GForce_test_has_a_labelled_toggle_row_in_the_panel_label_column()
        {
            string panel = View("GForceEffectView.xaml"), test = View("GForceTestView.xaml");
            Assert.Contains("v:LabelColumnSync.Group=\"{Binding Test}\"", panel);
            Assert.Contains("v:LabelColumnSync.Columns=\"2\"", panel);
            Assert.Contains("v:LabelColumnSync.Group=\"{Binding}\"", test);
            Assert.Contains("{v:Loc ShakeItPanel.Test.Label}", test);
            Assert.Contains("SharedSizeGroup=\"Label\"", test);
        }

        private static string View(string name)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "Ui", "Views", name));
        }
    }
}

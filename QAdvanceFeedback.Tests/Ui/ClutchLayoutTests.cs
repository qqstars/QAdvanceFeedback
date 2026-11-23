using System.IO;
using QAdvanceFeedback.Ui.ViewModels;

using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>Clutch view layout (owner, 2026-11-18): the plugin page shows the settings in categories
    /// (GroupBoxes); the ShakeIt panel shows no learning parameters at all - not even collapsed, so their
    /// long labels cannot widen the shared label column - and lines up with Lock/Slip. No "Your bite
    /// point" text beside the editor.</summary>
    public class ClutchLayoutTests
    {
        [Fact]
        public void Learning_parameters_exist_only_on_the_plugin_page()
        {
            var host = new FakePanelHost();
            var page = new ClutchBiteViewModel(host, new FakeClutchHost(), new ManualTicker(), new ImmediateDispatcher(), showLearning: true);
            var panel = new ClutchBiteViewModel(host, new FakeClutchHost(), new ManualTicker(), new ImmediateDispatcher());
            Assert.Same(page.Learning, page.LearningSection);
            Assert.Null(panel.LearningSection);
            Assert.True(page.IsCategorized);
            Assert.False(panel.IsCategorized);
        }

        [Fact]
        public void View_categorizes_on_the_page_and_drops_the_configured_text()
        {
            string view = View("ClutchBiteView.xaml");
            Assert.DoesNotContain("ConfiguredText", view);
            Assert.Contains("Content=\"{Binding LearningSection}\"", view);
            // A ContentTemplate renders even for null content (and hidden rows still widen the shared
            // label column): the rows come from a typed template, which never applies to null.
            Assert.Contains("<DataTemplate DataType=\"{x:Type vm:ClutchLearningViewModel}\">", view);
            Assert.DoesNotContain("<ContentControl.ContentTemplate>", view);
            Assert.Contains("<GroupBox", view);
            Assert.Contains("IsCategorized", view);
            foreach (string key in new[] { "Clutch.Header", "Clutch.Group.Axis", "Clutch.Learning.Header", "Clutch.Group.Graph", "Clutch.Test.Header" })
                Assert.Contains("Header=\"{v:Loc " + key + "}\"", view);
        }

        private static string View(string name)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "Ui", "Views", name));
        }
    }
}

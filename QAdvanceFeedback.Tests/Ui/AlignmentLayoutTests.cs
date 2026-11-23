using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>Alignment rules (owner, 2026-11-20), pinned on the XAML as text (WPF is not instantiable here):
    /// a ShakeIt panel's toggle starts, ON/OFF text first, where the dropdowns and editors start; each
    /// settings tab's rows share the one label column, so every editor starts at the same x.</summary>
    public class AlignmentLayoutTests
    {
        private static string ViewsDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))), "QAdvanceFeedback", "Ui", "Views");

        private static string Xaml(string name) => File.ReadAllText(Path.Combine(ViewsDir(), name));

        [Fact]
        public void No_view_shifts_a_toggle_or_indents_a_control_to_the_toggles_track()
        {
            foreach (string f in Directory.GetFiles(ViewsDir(), "*.xaml"))
                Assert.DoesNotContain("TrackAlignment", File.ReadAllText(f));
            Assert.False(File.Exists(Path.Combine(ViewsDir(), "TrackAlignment.cs")));
        }

        /// <summary>The XAML of the Grid named <paramref name="name"/>, up to its first column definitions.</summary>
        private static string GridHead(string xaml, string name)
        {
            int at = xaml.IndexOf("x:Name=\"" + name + "\"");
            Assert.True(at > 0, name + " not found");
            int end = xaml.IndexOf("</Grid.ColumnDefinitions>", at);
            return xaml.Substring(at, end - at);
        }

        [Theory]
        [InlineData("TriggerRows")]
        [InlineData("SensibilityRow")]
        [InlineData("SourceModePanel")]
        [InlineData("AlgorithmRows")]
        [InlineData("SlipFloorRow")]
        public void Wheel_channel_rows_use_the_tabs_shared_label_column(string grid) =>
            Assert.Contains("SharedSizeGroup=\"ChannelAlign\"", GridHead(Xaml("WheelChannelPageView.xaml"), grid));

        [Fact]
        public void Slip_threshold_rows_are_one_under_the_other()
        {
            string xaml = Xaml("WheelChannelPageView.xaml");
            int at = xaml.IndexOf("x:Name=\"TriggerRows\"");
            string body = xaml.Substring(at, xaml.IndexOf("</Grid>", at) - at);
            Assert.Contains("Grid.Row=\"0\" Grid.Column=\"1\" Value=\"{Binding BrakeThreshold}\"", body);
            Assert.Contains("Grid.Row=\"1\" Grid.Column=\"1\" Value=\"{Binding ThrottleThreshold}\"", body);
        }

        [Fact]
        public void Slip_angle_source_row_uses_its_tabs_shared_label_column() =>
            Assert.Contains("SharedSizeGroup=\"SlipAngleAlign\"", GridHead(Xaml("SlipAngleTabView.xaml"), "SlipAngleSourceRow"));

        [Fact]
        public void Key_data_editors_sit_at_the_left_of_their_wider_column()
        {
            // Key data rows: the learning LED and the editor in one left-aligned panel, LED first (owner,
            // 2026-11-21: the indicator sits LEFT of the box on the settings page and takes no space under Manual).
            const string LedThenEditor = @"<StackPanel Grid\.Row=""[234]"" Grid\.Column=""1"" Orientation=""Horizontal"" HorizontalAlignment=""Left""[^>]*>\s*<v:LearningLed DataContext=""\{Binding (\w+)Indicator\}""[^>]*/>\s*<(local|c):NumericEditor Value=""\{Binding \1\}""";
            Assert.Equal(3, Regex.Matches(Xaml("WheelChannelKeyDataView.xaml"), LedThenEditor).Count);
            // the Custom reference editors (same shared editor column) and Slip Angle's Max/Best/Min
            Assert.Equal(3, Regex.Matches(Xaml("WheelChannelPageView.xaml"), @"Grid\.Column=""1"" Value=""\{Binding S(Max|90|75)\}"" HorizontalAlignment=""Left""").Count);
            Assert.Equal(3, Regex.Matches(Xaml("SlipAngleTabView.xaml"), LedThenEditor).Count);
        }
    }
}

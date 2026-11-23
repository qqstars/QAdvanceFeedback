using System.Globalization;
using System.IO;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// The WheelLock/WheelSlip ShakeIt graph ("Source value -> Projected value") and the G-Force
    /// readout label are the plugin settings page's own, through one shared implementation each
    /// (docs\shakeit-native-effects-design.md).
    /// </summary>
    public class PanelReadoutSharingTests
    {
        private const double W = 420, H = 160;

        // ---- Source value -> Projected value -------------------------------------------------

        [Fact]
        public void Source_graph_is_sampled_every_5_from_0_to_100()
        {
            var points = SourceToProjectedGraph.Points(ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock),
                isLock: true, maxGripOnly: false, sMax: 85, s90: 75, s75: 60, W, H);

            Assert.Equal(21, points.Count);
            Assert.Equal(0, points[0].X, 9);
            Assert.Equal(W, points[20].X, 9);
        }

        [Fact]
        public void Mapping_puts_each_key_data_point_at_its_canonical_normalized_position()
        {
            // Identity projector: output == normalized, so the graph shows the normalization itself.
            var identity = new ProjectorSettings { StartInput = 0, EndInput = 100, SlightlyInput = 30, SlightlyOutput = 30, ModerateInput = 60, ModerateOutput = 60, CriticalInput = 80, CriticalOutput = 80 };
            var projector = new OutputProjector(identity);

            var points = SourceToProjectedGraph.Points(identity, isLock: true, maxGripOnly: false, sMax: 80, s90: 60, s75: 40, W, H);

            Assert.Equal(H - projector.Project(80) / 100.0 * H, points[16].Y, 6);   // source 80 = SMax -> 80
            Assert.Equal(H - projector.Project(60) / 100.0 * H, points[12].Y, 6);   // source 60 = S90 -> 60
            Assert.Equal(H - projector.Project(30) / 100.0 * H, points[8].Y, 6);    // source 40 = S75 -> 30
        }

        [Fact]
        public void Max_grip_only_ignores_the_lower_anchors()
        {
            var identity = new ProjectorSettings { StartInput = 0, EndInput = 100, SlightlyInput = 30, SlightlyOutput = 30, ModerateInput = 60, ModerateOutput = 60, CriticalInput = 80, CriticalOutput = 80 };

            var a = SourceToProjectedGraph.Points(identity, isLock: false, maxGripOnly: true, sMax: 50, s90: 45, s75: 35, W, H);
            var b = SourceToProjectedGraph.Points(identity, isLock: false, maxGripOnly: true, sMax: 50, s90: null, s75: null, W, H);

            Assert.Equal(H - 80 / 100.0 * H, a[10].Y, 6);   // source 50 = SMax -> 80
            for (int i = 0; i < a.Count; i++) Assert.Equal(a[i].Y, b[i].Y, 9);
        }

        [Fact]
        public void Missing_values_fall_back_to_the_channel_default_SMax()
        {
            var settings = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Slip);

            var fallback = SourceToProjectedGraph.Points(settings, isLock: false, maxGripOnly: false, sMax: null, s90: null, s75: null, W, H);
            double d = KeyDataPointSettings.SlipDefaultSMax;
            var explicitDefault = SourceToProjectedGraph.Points(settings, isLock: false, maxGripOnly: false,
                sMax: d, s90: d * KeyDataPointSettings.DerivedS90Fraction, s75: d * KeyDataPointSettings.DerivedS75Fraction, W, H);

            for (int i = 0; i < fallback.Count; i++) Assert.Equal(explicitDefault[i].Y, fallback[i].Y, 9);
        }

        [Fact]
        public void Null_projector_draws_nothing()
        {
            Assert.Empty(SourceToProjectedGraph.Points(null, true, false, 85, 75, 60, W, H));
        }

        // ---- G-Force readout -------------------------------------------------------------------

        [Fact]
        public void Readout_texts_match_the_page_pattern()
        {
            CultureInfo original = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal("Fixed: 1.50G", GForceReadout.Build(autoMode: false, fixedValue: 1.5, hasDetected: true, detectedValue: 2.3));
                Assert.Equal("Default: 1.50G. Auto detected: 2.30G", GForceReadout.Build(true, 1.5, true, 2.3));
                Assert.Equal("Default: 1.50G. Auto: still using default (no data yet)", GForceReadout.Build(true, 1.5, false, 0));
            }
            finally { Strings.Culture = original; }
        }

        [Fact]
        public void Readout_for_an_axis_reads_its_applied_mode_and_default()
        {
            CultureInfo original = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                var g = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 1.2, DecelMaxMode = GMaxMode.Fixed, FixedDecelMaxG = 2.5 };

                Assert.Equal("Default: 1.20G. Auto: still using default (no data yet)", GForceReadout.ForAxis(g, SettingField.AccelMaxMode));
                Assert.Equal("Fixed: 2.50G", GForceReadout.ForAxis(g, SettingField.DecelMaxMode));
                Assert.Equal(string.Empty, GForceReadout.ForAxis(null, SettingField.LatMaxMode));
            }
            finally { Strings.Culture = original; }
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(Path.Combine(dir.FullName, "QAdvanceFeedback"), Path.Combine(parts));
        }
    }
}

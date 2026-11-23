using System;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.ShakeItEffects;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// ShakeIt's own built-in lock/slip read through a native QAdvanceFeedback effect
    /// (docs\shakeit-native-effects-design.md, "ShakeIt source replaces the export-based ShakeIt mode").
    /// </summary>
    public class ShakeItTapTests
    {
        private static readonly long Ms = TimeSpan.TicksPerMillisecond;

        [Fact]
        public void Deduper_runs_once_per_frame_time()
        {
            var d = new TapDeduper();
            DateTime t = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(d.ShouldRun(t));
            Assert.False(d.ShouldRun(t));   // a second effect / second profile in the same frame
            Assert.True(d.ShouldRun(t.AddMilliseconds(16)));
        }

        [Fact]
        public void Fresh_tap_gives_the_wheel_value()
        {
            var snap = new FrameSnapshot<ShakeItTapValues>(
                new ShakeItTapValues(new Corners(10, 20, 30, 40), new Corners(1, 2, 3, 4)), 0);

            Assert.True(ShakeItTapValues.TryGet(snap, 100 * Ms, isLock: true, wheelIndex: 2, out double lockRl));
            Assert.Equal(30, lockRl);
            Assert.True(ShakeItTapValues.TryGet(snap, 100 * Ms, isLock: false, wheelIndex: 3, out double slipRr));
            Assert.Equal(4, slipRr);
        }

        [Fact]
        public void Stale_or_missing_tap_gives_nothing()
        {
            var snap = new FrameSnapshot<ShakeItTapValues>(
                new ShakeItTapValues(Corners.Uniform(50), Corners.Uniform(50)), 0);

            Assert.False(ShakeItTapValues.TryGet(snap, 600 * Ms, true, 0, out _));
            Assert.False(ShakeItTapValues.TryGet(null, 0, true, 0, out _));
        }

        [Fact]
        public void Tap_values_are_clamped_to_0_100()
        {
            var v = new ShakeItTapValues(new Corners(-5, 150, double.NaN, 50), Corners.Zero);
            Assert.Equal(0, v.Lock.FrontLeft);
            Assert.Equal(100, v.Lock.FrontRight);
            Assert.Equal(0, v.Lock.RearLeft);
            Assert.Equal(50, v.Lock.RearRight);
        }

        [Fact]
        public void Tap_diagnostics_are_published_but_product_names_are_unchanged()
        {
            string[] product = AllPublishedProperties.ProductNames().ToArray();
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            Assert.Equal(66, product.Length);   // 62 + Clutch.Bite.Output (docs\clutch-bite-design.md) + 3 Slip Angle
            Assert.DoesNotContain(product, n => n.Contains("ShakeIt"));
            Assert.Contains("Diag.ShakeIt.TapActive", diag);
            foreach (string w in new[] { "FrontLeft", "FrontRight", "RearLeft", "RearRight" })
            {
                Assert.Contains(ShakeItTapPropertyNames.GetPublishedName(true, w), diag);
                Assert.Contains(ShakeItTapPropertyNames.GetPublishedName(false, w), diag);
            }
        }

        [Fact]
        public void Publisher_csv_snapshot_carries_the_tap_values_in_header_order()
        {
            string[] header = AllPublishedProperties.AllNames(diagnosticsEnabled: true).ToArray();
            var publisher = new PropertyPublisher();
            publisher.UpdateShakeItTap(true, new Corners(11, 12, 13, 14), new Corners(21, 22, 23, 24));

            object[] values = publisher.SnapshotAllValuesForCsv();

            Assert.Equal(header.Length, values.Length);
            Assert.Equal(true, values[Array.IndexOf(header, "Diag.ShakeIt.TapActive")]);
            Assert.Equal(13.0, values[Array.IndexOf(header, "Diag.ShakeIt.WheelLock.RearLeft")]);
            Assert.Equal(24.0, values[Array.IndexOf(header, "Diag.ShakeIt.WheelSlip.RearRight")]);
        }
    }
}

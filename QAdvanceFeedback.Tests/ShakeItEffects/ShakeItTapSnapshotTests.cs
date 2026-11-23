using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// A ShakeIt tap is only learned from under the algorithm snapshot it was computed with: right after a
    /// setting change, a tap still carrying the OLD snapshot routes the channel to Raw under the Raw
    /// identity, so old-algorithm values never reach the NEW ShakeIt key.
    /// </summary>
    public class ShakeItTapSnapshotTests
    {
        private static ShakeItAlgorithmSnapshot WithLockSensibility(double sensibility)
        {
            ShakeItAlgorithmInputs d = ShakeItAlgorithmInputs.Defaults;
            return new ShakeItAlgorithmSnapshot(new ShakeItAlgorithmInputs(
                d.LockUseLegacyIracingAlgorithm, sensibility, d.LockOnly, d.LockBrakeThresholdPercent,
                d.SlipUseLegacyIracingAlgorithm, d.SlipMuteWhileLockActive, d.SlipBrakeThresholdPercent,
                d.SlipThrottleThresholdPercent));
        }

        private static double Raw(object state, int i) => 100 + i;

        private static double NeverRead(object state, int i) => throw new System.InvalidOperationException();

        /// <summary>The plugin's per-frame decision: freshness includes "computed under the current
        /// snapshot", then route + resolve with the CURRENT snapshot's fingerprint.</summary>
        private static ChannelSourceResolution Frame(bool isLock, ShakeItTapValues tap, ShakeItAlgorithmSnapshot current)
        {
            bool tapFresh = tap != null && ShakeItSourceResolution.TapComputedUnder(tap, current);
            ChannelSourceRoute route = ShakeItSourceResolution.Route(isShakeItMode: true, viperBlocked: false,
                ShakeItSourceState.Supported, tapFresh);
            return ShakeItSourceResolution.Resolve<object>(route, isLock, tapFresh ? tap : null, null, Raw, NeverRead,
                null, RawSourceFallback.RawIdentity(isLock), current.Fingerprint(isLock));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void After_a_settings_change_an_old_snapshot_tap_never_learns_under_the_new_key(bool isLock)
        {
            ShakeItAlgorithmSnapshot before = WithLockSensibility(50);
            ShakeItAlgorithmSnapshot after = WithLockSensibility(70);
            var oldTap = new ShakeItTapValues(Corners.Uniform(40), Corners.Uniform(30), true, true, "G", before);
            var newTap = new ShakeItTapValues(Corners.Uniform(45), Corners.Uniform(35), true, true, "G", after);
            string newKey = ShakeItSourceResolution.ShakeItIdentity(isLock, after.Fingerprint(isLock));

            // Before the change: the tap is current, ShakeIt learns under the old key.
            ChannelSourceResolution steady = Frame(isLock, oldTap, before);
            Assert.Equal(ChannelSourceRoute.ShakeItBuiltIn, steady.Route);
            Assert.Equal(ShakeItSourceResolution.ShakeItIdentity(isLock, before.Fingerprint(isLock)), steady.Identity);

            // The change lands; ShakeIt has not produced a frame under it yet: Raw values, Raw key.
            ChannelSourceResolution stale = Frame(isLock, oldTap, after);
            Assert.Equal(ChannelSourceRoute.RawFallback, stale.Route);
            Assert.Equal(RawSourceFallback.RawIdentity(isLock), stale.Identity);
            Assert.NotEqual(newKey, stale.Identity);
            Assert.Equal(new Corners(100, 101, 102, 103), stale.Values);

            // The first tap computed under the new snapshot is learned under the new key.
            ChannelSourceResolution fresh = Frame(isLock, newTap, after);
            Assert.Equal(ChannelSourceRoute.ShakeItBuiltIn, fresh.Route);
            Assert.Equal(newKey, fresh.Identity);
        }

        [Fact]
        public void Tap_snapshot_match_is_by_reference_and_null_safe()
        {
            ShakeItAlgorithmSnapshot a = WithLockSensibility(50);
            ShakeItAlgorithmSnapshot sameInputs = WithLockSensibility(50);
            var tap = new ShakeItTapValues(Corners.Zero, Corners.Zero, true, true, "G", a);

            Assert.True(ShakeItSourceResolution.TapComputedUnder(tap, a));
            Assert.False(ShakeItSourceResolution.TapComputedUnder(tap, sameInputs));   // the plugin reuses one instance
            Assert.False(ShakeItSourceResolution.TapComputedUnder(null, a));
            Assert.False(ShakeItSourceResolution.TapComputedUnder(new ShakeItTapValues(Corners.Zero, Corners.Zero), a));
        }
    }
}

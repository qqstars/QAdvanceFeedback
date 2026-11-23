using System.Linq;
using QAdvanceFeedback.Core.Health;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="HealthRecoveryTracker"/>: a one-off fault's health entry is cleared after a sustained
    /// run of good frames. Each test uses its own subsystem name (the registry is process-wide).
    /// </summary>
    public class HealthRecoveryTrackerTests
    {
        private static bool Present(string subsystem) => HealthRegistry.Snapshot().Any(e => e.Subsystem == subsystem);

        [Fact]
        public void Entry_is_cleared_after_300_consecutive_good_frames()
        {
            const string sub = "Test.HealthRecovery.Clears";
            var t = new HealthRecoveryTracker(sub);
            Assert.Equal(300, t.GoodFramesToClear);

            HealthRegistry.Report(sub, HealthSeverity.Degraded, "Key", "boom");
            t.NoteFault();
            for (int i = 0; i < 299; i++) Assert.False(t.NoteGood());
            Assert.True(Present(sub));
            Assert.True(t.NoteGood());
            Assert.False(Present(sub));
            Assert.False(t.IsFaulted);
        }

        [Fact]
        public void A_fault_restarts_the_count()
        {
            const string sub = "Test.HealthRecovery.Restart";
            var t = new HealthRecoveryTracker(sub);
            HealthRegistry.Report(sub, HealthSeverity.Degraded, "Key", "boom");
            t.NoteFault();
            for (int i = 0; i < 250; i++) t.NoteGood();
            HealthRegistry.Report(sub, HealthSeverity.Degraded, "Key", "again");
            t.NoteFault();
            for (int i = 0; i < 299; i++) Assert.False(t.NoteGood());
            Assert.True(Present(sub));
            Assert.True(t.NoteGood());
            Assert.False(Present(sub));
        }

        [Fact]
        public void Good_frames_without_a_fault_never_touch_the_registry()
        {
            const string sub = "Test.HealthRecovery.NoFault";
            var t = new HealthRecoveryTracker(sub, 1);
            HealthRegistry.Report(sub, HealthSeverity.Degraded, "Key", "reported elsewhere");
            for (int i = 0; i < 5; i++) Assert.False(t.NoteGood());
            Assert.True(Present(sub));
            HealthRegistry.Clear(sub);
        }
    }
}

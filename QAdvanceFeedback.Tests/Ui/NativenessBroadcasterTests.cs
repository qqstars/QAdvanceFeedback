using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    public class NativenessBroadcasterTests
    {
        private const double Dt = 1.0 / 60;

        [Fact]
        public void Alternating_every_frame_for_five_seconds_broadcasts_nothing()
        {
            int n = 0;
            var b = new NativenessBroadcaster(() => n++);
            for (int i = 0; i < 300; i++)
                b.Update(i % 2 == 0 ? SourceNativeness.Fallback : SourceNativeness.Native, SourceNativeness.Unknown, Dt);
            Assert.Equal(0, n);
            Assert.Equal(SourceNativeness.Unknown, b.Lock);
        }

        [Fact]
        public void Flip_back_and_forth_after_a_published_change_broadcasts_nothing_more()
        {
            int n = 0;
            var b = new NativenessBroadcaster(() => n++);
            for (int i = 0; i < 60; i++) b.Update(SourceNativeness.Native, SourceNativeness.Native, Dt);
            Assert.Equal(1, n);
            for (int i = 0; i < 300; i++)
                b.Update(i % 2 == 0 ? SourceNativeness.Fallback : SourceNativeness.Native, SourceNativeness.Native, Dt);
            Assert.Equal(1, n);
            Assert.Equal(SourceNativeness.Native, b.Lock);
        }

        [Fact]
        public void Stable_change_broadcasts_once_after_the_hold()
        {
            int n = 0;
            var b = new NativenessBroadcaster(() => n++);
            int frames = 0;
            while (n == 0 && frames < 600) { b.Update(SourceNativeness.Fallback, SourceNativeness.Native, Dt); frames++; }

            Assert.Equal(1, n);
            Assert.True(frames * Dt >= NativenessBroadcaster.HoldSeconds);
            Assert.Equal(SourceNativeness.Fallback, b.Lock);
            Assert.Equal(SourceNativeness.Native, b.Slip);
            for (int i = 0; i < 120; i++) b.Update(SourceNativeness.Fallback, SourceNativeness.Native, Dt);
            Assert.Equal(1, n);
        }

        [Fact]
        public void Clear_applies_immediately_and_only_when_needed()
        {
            int n = 0;
            var b = new NativenessBroadcaster(() => n++);
            b.Clear();
            Assert.Equal(0, n);
            for (int i = 0; i < 60; i++) b.Update(SourceNativeness.Fallback, SourceNativeness.Fallback, Dt);
            Assert.Equal(1, n);
            b.Clear();
            Assert.Equal(2, n);
            Assert.Equal(SourceNativeness.Unknown, b.Lock);
            Assert.Equal(SourceNativeness.Unknown, b.Slip);
        }
    }
}

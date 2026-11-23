using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Settings
{
    /// <summary>Ball colours (radial gradient fills in the view): front red, rear blue, left brighter and
    /// right darker; Left/Right in grey; one channel orange.</summary>
    public enum BallColour { BrightRed, DarkRed, BrightBlue, DarkBlue, Red, Blue, BrightGrey, DarkGrey, Orange }

    /// <summary>One Test Effect ball on the "Source value -> Projected value" graph.</summary>
    public readonly struct TestBall
    {
        public TestBall(EffectSlot slot, double source0100, double projected0100, BallColour colour)
        {
            Slot = slot; Source0100 = source0100; Projected0100 = projected0100; Colour = colour;
        }

        public EffectSlot Slot { get; }
        /// <summary>x: the channel's source value (a wheel's slider, or the same aggregation over the four
        /// sliders for a group).</summary>
        public double Source0100 { get; }
        /// <summary>y: the channel's PULSE-FREE projected value, so the ball sits on the curve and does not
        /// flicker; the device still gets the pulsed value.</summary>
        public double Projected0100 { get; }
        public BallColour Colour { get; }
    }

    /// <summary>The balls for an effect's current routing: one per output channel.</summary>
    public static class TestBalls
    {
        /// <summary>Plain-aggregate x, kept for existing tests; not the panel path (the VM uses the key-data overload).</summary>
        public static IReadOnlyList<TestBall> For(EffectRouting routing, bool isLock, Corners sources0100,
            AggregationWeights weights, ProjectedWheelLockSlipResult result) =>
            For(routing, isLock, sources0100, weights, result, false, null);

        /// <summary>As above, but a group ball's x is the group slider value
        /// (<see cref="TestGroupSliders.SourceFor"/>), so the ball sits on the source-to-projected curve.
        /// A null <paramref name="keyData"/> keeps the plain aggregate of the four sliders.</summary>
        public static IReadOnlyList<TestBall> For(EffectRouting routing, bool isLock, Corners sources0100,
            AggregationWeights weights, ProjectedWheelLockSlipResult result, bool maxGripOnly, KeyDataView keyData)
        {
            var balls = new List<TestBall>();
            if (result == null) return balls;
            WheelAggregate agg = Aggregator.Compute(sources0100, weights);
            foreach (EffectSlot slot in EffectRoutingSlots.For(routing))
            {
                double y = isLock ? ChannelValues.LockWithoutPulse(result, slot) : ChannelValues.SlipWithoutPulse(result, slot);
                TestGroup? group = GroupOf(slot);
                double x = group.HasValue && keyData != null
                    ? TestGroupSliders.SourceFor(group.Value, sources0100, weights, isLock, maxGripOnly, keyData)
                    : SourceFor(slot, sources0100, agg);
                balls.Add(new TestBall(slot, x, y, ColourFor(slot)));
            }
            return balls;
        }

        private static TestGroup? GroupOf(EffectSlot slot)
        {
            switch (slot)
            {
                case EffectSlot.Front: return TestGroup.Front;
                case EffectSlot.Rear: return TestGroup.Rear;
                case EffectSlot.Left: return TestGroup.Left;
                case EffectSlot.Right: return TestGroup.Right;
                case EffectSlot.FrontLeft:
                case EffectSlot.FrontRight:
                case EffectSlot.RearLeft:
                case EffectSlot.RearRight: return null;
                default: return TestGroup.All;
            }
        }

        private static double SourceFor(EffectSlot slot, Corners s, WheelAggregate agg)
        {
            switch (slot)
            {
                case EffectSlot.FrontLeft: return s.FrontLeft;
                case EffectSlot.FrontRight: return s.FrontRight;
                case EffectSlot.RearLeft: return s.RearLeft;
                case EffectSlot.RearRight: return s.RearRight;
                case EffectSlot.Front: return agg.Front;
                case EffectSlot.Rear: return agg.Rear;
                case EffectSlot.Left: return agg.Left;
                case EffectSlot.Right: return agg.Right;
                default: return agg.All;
            }
        }

        private static BallColour ColourFor(EffectSlot slot)
        {
            switch (slot)
            {
                case EffectSlot.FrontLeft: return BallColour.BrightRed;
                case EffectSlot.FrontRight: return BallColour.DarkRed;
                case EffectSlot.RearLeft: return BallColour.BrightBlue;
                case EffectSlot.RearRight: return BallColour.DarkBlue;
                case EffectSlot.Front: return BallColour.Red;
                case EffectSlot.Rear: return BallColour.Blue;
                case EffectSlot.Left: return BallColour.BrightGrey;
                case EffectSlot.Right: return BallColour.DarkGrey;
                default: return BallColour.Orange;
            }
        }
    }
}

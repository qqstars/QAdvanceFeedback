using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Settings
{
    /// <summary>The wheel groups a Lock/Slip Test Effect group slider can drive.</summary>
    public enum TestGroup
    {
        All,
        Front,
        Rear,
        Left,
        Right
    }

    /// <summary>
    /// Group slider maths for the Lock/Slip Test Effect. A group slider's value is the source that, alone,
    /// maps to the group's aggregated normalized value (the engine's own sequence), so the graph dot
    /// always sits on the source-to-projected curve.
    /// </summary>
    public static class TestGroupSliders
    {
        private static readonly IReadOnlyList<TestGroup> None = new TestGroup[0];
        private static readonly IReadOnlyList<TestGroup> Single = new[] { TestGroup.All };
        private static readonly IReadOnlyList<TestGroup> FrontRear = new[] { TestGroup.Front, TestGroup.Rear };
        private static readonly IReadOnlyList<TestGroup> LeftRight = new[] { TestGroup.Left, TestGroup.Right };

        public static IReadOnlyList<TestGroup> GroupsFor(EffectRouting routing)
        {
            switch (routing)
            {
                case EffectRouting.Mono: return Single;
                case EffectRouting.FrontRear: return FrontRear;
                case EffectRouting.LeftRight: return LeftRight;
                default: return None;
            }
        }

        public static double SourceFor(TestGroup group, Corners sources0100, AggregationWeights weights,
            bool isLock, bool maxGripOnly, KeyDataView keyData)
        {
            double? sMax = keyData?.SMax, s90 = keyData?.S90, s75 = keyData?.S75;
            double N(double source) =>
                ClampMath.To0100(KeyDataNormalization.Normalize(ClampMath.To0100(source), isLock, maxGripOnly, sMax, s90, s75));

            var wheels = new Corners(N(sources0100.FrontLeft), N(sources0100.FrontRight), N(sources0100.RearLeft), N(sources0100.RearRight));
            WheelAggregate agg = Aggregator.Compute(wheels, weights);
            double value;
            switch (group)
            {
                case TestGroup.Front: value = agg.Front; break;
                case TestGroup.Rear: value = agg.Rear; break;
                case TestGroup.Left: value = agg.Left; break;
                case TestGroup.Right: value = agg.Right; break;
                default: value = agg.All; break;
            }
            return KeyDataNormalizationInverse.SourceFor(value, isLock, maxGripOnly, sMax, s90, s75);
        }

        public static Corners Write(TestGroup group, double value, Corners current)
        {
            double fl = current.FrontLeft, fr = current.FrontRight, rl = current.RearLeft, rr = current.RearRight;
            switch (group)
            {
                case TestGroup.All: fl = fr = rl = rr = value; break;
                case TestGroup.Front: fl = fr = value; break;
                case TestGroup.Rear: rl = rr = value; break;
                case TestGroup.Left: fl = rl = value; break;
                case TestGroup.Right: fr = rr = value; break;
            }
            return new Corners(fl, fr, rl, rr);
        }
    }
}

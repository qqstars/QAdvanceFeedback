using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    public class TestGroupSlidersTests
    {
        [Fact]
        public void Groups_follow_routing()
        {
            Assert.Equal(new[] { TestGroup.All }, TestGroupSliders.GroupsFor(EffectRouting.Mono));
            Assert.Equal(new[] { TestGroup.Front, TestGroup.Rear }, TestGroupSliders.GroupsFor(EffectRouting.FrontRear));
            Assert.Equal(new[] { TestGroup.Left, TestGroup.Right }, TestGroupSliders.GroupsFor(EffectRouting.LeftRight));
            Assert.Empty(TestGroupSliders.GroupsFor(EffectRouting.Corners));
        }

        [Fact]
        public void Write_sets_only_member_wheels()
        {
            var c = new Corners(1, 2, 3, 4);
            Assert.Equal(new Corners(9, 9, 9, 9), TestGroupSliders.Write(TestGroup.All, 9, c));
            Assert.Equal(new Corners(9, 9, 3, 4), TestGroupSliders.Write(TestGroup.Front, 9, c));
            Assert.Equal(new Corners(1, 2, 9, 9), TestGroupSliders.Write(TestGroup.Rear, 9, c));
            Assert.Equal(new Corners(9, 2, 9, 4), TestGroupSliders.Write(TestGroup.Left, 9, c));
            Assert.Equal(new Corners(1, 9, 3, 9), TestGroupSliders.Write(TestGroup.Right, 9, c));
        }

        private static double Group(QAdvanceFeedback.Core.Projection.ProjectedWheelLockSlipResult r, bool isLock, TestGroup g)
        {
            switch (g)
            {
                case TestGroup.Front: return isLock ? r.LockFrontWithoutPulse : r.SlipFrontWithoutPulse;
                case TestGroup.Rear: return isLock ? r.LockRearWithoutPulse : r.SlipRearWithoutPulse;
                case TestGroup.Left: return isLock ? r.LockLeftWithoutPulse : r.SlipLeftWithoutPulse;
                case TestGroup.Right: return isLock ? r.LockRightWithoutPulse : r.SlipRightWithoutPulse;
                default: return isLock ? r.LockAllWithoutPulse : r.SlipAllWithoutPulse;
            }
        }

        private static double AggOf(WheelAggregate a, TestGroup g)
        {
            switch (g)
            {
                case TestGroup.Front: return a.Front;
                case TestGroup.Rear: return a.Rear;
                case TestGroup.Left: return a.Left;
                case TestGroup.Right: return a.Right;
                default: return a.All;
            }
        }

        [Fact]
        public void Dot_is_always_on_the_curve()
        {
            var rnd = new Random(12345);
            var keys = new KeyDataView[]
            {
                null,
                new KeyDataView { SMax = 80, S90 = 68, S75 = 50, ShowLowerAnchors = true },
            };
            var modes = new[] { (true, false), (true, true), (false, false) };
            var groups = new[] { TestGroup.All, TestGroup.Front, TestGroup.Rear, TestGroup.Left, TestGroup.Right };
            int checkedProjected = 0;

            for (int i = 0; i < 500; i++)
            {
                var wheels = new Corners(rnd.NextDouble() * 100, rnd.NextDouble() * 100, rnd.NextDouble() * 100, rnd.NextDouble() * 100);
                var arbitrary = new AggregationWeights(rnd.NextDouble() * 1.5, rnd.NextDouble() * 1.5, rnd.NextDouble() * 1.5, rnd.NextDouble() * 1.5, rnd.NextDouble());
                double a = rnd.NextDouble(), b = rnd.NextDouble();

                foreach (var key in keys)
                foreach (var (isLock, maxGrip) in modes)
                {
                    double? sMax = key?.SMax, s90 = key?.S90, s75 = key?.S75;
                    double nMax100 = KeyDataNormalization.Normalize(100, isLock, maxGrip, sMax, s90, s75);
                    double N(double s) => ClampMath.To0100(KeyDataNormalization.Normalize(ClampMath.To0100(s), isLock, maxGrip, sMax, s90, s75));
                    var normWheels = new Corners(N(wheels.FrontLeft), N(wheels.FrontRight), N(wheels.RearLeft), N(wheels.RearRight));

                    // arbitrary weights: the slider sits on the curve at the (capped) aggregated value.
                    WheelAggregate agg = Aggregator.Compute(normWheels, arbitrary);
                    foreach (var g in groups)
                    {
                        double src = TestGroupSliders.SourceFor(g, wheels, arbitrary, isLock, maxGrip, key);
                        double expected = Math.Min(AggOf(agg, g), nMax100);
                        Assert.Equal(expected, KeyDataNormalization.Normalize(src, isLock, maxGrip, sMax, s90, s75), 6);
                    }

                    // per-stage pairs summing to 1: four equal wheels at the group source project like the real wheels.
                    var unit = new AggregationWeights(a, 1 - a, b, 1 - b, isLock ? 0 : rnd.NextDouble());
                    var channel = new WheelChannelSettings
                    {
                        NormalizePattern = maxGrip ? NormalizePattern.MaxGripOnly : NormalizePattern.Mapping,
                        AggregationWMax = unit.WMax, AggregationWMin = unit.WMin,
                        AggregationWFront = unit.WFront, AggregationWRear = unit.WRear,
                        SlipFloorFactor = unit.SlipFloorFactor,
                    };
                    var engine = new LockSlipTestEngine(isLock, channel);
                    var real = engine.Step(wheels, key, 1.0 / 60);
                    WheelAggregate uAgg = Aggregator.Compute(normWheels, channel.ToAggregationWeights());
                    foreach (var g in groups)
                    {
                        if (AggOf(uAgg, g) > nMax100 + 1e-9) continue; // beyond the source range: capped at 100.
                        double src = TestGroupSliders.SourceFor(g, wheels, channel.ToAggregationWeights(), isLock, maxGrip, key);
                        var equal = new LockSlipTestEngine(isLock, channel).Step(Corners.Uniform(src), key, 1.0 / 60);
                        Assert.Equal(Group(real, isLock, g), Group(equal, isLock, g), 6);
                        checkedProjected++;
                    }
                }
            }
            Assert.True(checkedProjected > 1000);
        }
    }
}

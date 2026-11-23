using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// Task G, Slip Angle half (docs\internal-source-learning-design.md 3.2): Plugin Internal (Native or
    /// Fallback, whichever this frame's raw angle is) learns every frame, also while Custom is selected;
    /// Custom learns only while selected; only the active source publishes.
    /// </summary>
    public class SlipAngleInternalSourceLearningTests
    {
        private const double Dt = 1.0 / 60.0;
        private const string CustomId = "Plain:Custom.FL~Plain:Custom.FR~Plain:Custom.RL~Plain:Custom.RR";

        private static TelemetryFrame Frame(int i)
        {
            // A sweep up through the slip-angle window and back, with lateral G following it.
            double phase = (i % 240) / 240.0;
            double vlat = 0.5 + 4.5 * Math.Sin(Math.PI * phase);
            double latG = 0.3 + 1.1 * Math.Sin(Math.PI * phase);
            return new TelemetryFrame(groundSpeedKmh: 100.0, brakePercent: 50, throttlePercent: 0,
                lateralLocalVelocity: vlat, forwardLocalVelocity: 27.8, lateralG: latG, longitudinalG: 0.0,
                yawRateRadPerSec: 0.3, isAirborne: false);
        }

        private static SlipAngleInput Native(int i, double scale)
        {
            double phase = (i % 240) / 240.0;
            double a = scale * (1.0 + 7.0 * Math.Sin(Math.PI * phase));
            return new SlipAngleInput("G", true, new Corners(a, a * 1.05, a * 1.2, a * 1.15), true, SlipAngleUnit.Degrees);
        }

        private static SlipAngleFrameRequest Request(int i, bool custom, SlipAngleInput input)
        {
            var r = new SlipAngleFrameRequest { GameId = "G", CarId = "C", Frame = Frame(i), Dt = Dt, Input = input ?? new SlipAngleInput("G") };
            if (custom)
            {
                double phase = (i % 240) / 240.0;
                double a = 2.0 + 9.0 * Math.Sin(Math.PI * phase);
                r.UseCustomSource = true;
                r.CustomSourceIdentity = CustomId;
                r.CustomWheelsDeg = new double?[] { a, a, a * 1.1, a * 1.1 };
            }
            return r;
        }

        private static long Samples(KeyedSlipAngleLearner learner, string identity) =>
            learner.Export()
                .Where(kv => KeyedSlipAngleLearner.TryParseKey(kv.Key, out _, out _, out string s, out _, out _) && s == identity)
                .Sum(kv => kv.Value.Learner?.Curve?.TotalSamples ?? 0L);

        [Fact]
        public void Plugin_internal_fallback_learns_while_custom_is_selected()
        {
            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 1200; i++) p.Process(Request(i, custom: true, input: null));

            Assert.True(Samples(p.Learner, SlipAngleFrameProcessor.FallbackIdentity) > 0);
            Assert.True(Samples(p.Learner, CustomId) > 0);
        }

        [Fact]
        public void Plugin_internal_native_learns_while_custom_is_selected()
        {
            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 1200; i++) p.Process(Request(i, custom: true, input: Native(i, 1.0)));

            Assert.True(Samples(p.Learner, SlipAngleFrameProcessor.NativeIdentity) > 0);
            Assert.Equal(0, Samples(p.Learner, SlipAngleFrameProcessor.FallbackIdentity));
        }

        [Fact]
        public void Custom_does_not_learn_while_plugin_internal_is_selected()
        {
            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 1200; i++) p.Process(Request(i, custom: false, input: null));

            Assert.True(Samples(p.Learner, SlipAngleFrameProcessor.FallbackIdentity) > 0);
            Assert.Equal(0, Samples(p.Learner, CustomId));
            Assert.DoesNotContain(p.Learner.Export().Keys, k => k.Contains(CustomId));
        }

        [Fact]
        public void Plugin_internal_learns_the_same_samples_selected_or_not()
        {
            var selected = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            var alongside = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 600; i++)
            {
                selected.Process(Request(i, custom: false, input: Native(i, 1.0)));
                alongside.Process(Request(i, custom: true, input: Native(i, 1.0)));
            }

            // One observation per frame either way (never two), under the same key.
            Assert.Equal(Samples(selected.Learner, SlipAngleFrameProcessor.NativeIdentity),
                Samples(alongside.Learner, SlipAngleFrameProcessor.NativeIdentity));
        }

        private static string InternalState(KeyedSlipAngleLearner learner) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(learner.Export()
                .Where(kv => KeyedSlipAngleLearner.TryParseKey(kv.Key, out _, out _, out string s, out _, out _)
                             && (s == SlipAngleFrameProcessor.NativeIdentity || s == SlipAngleFrameProcessor.FallbackIdentity))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());

        [Fact]
        public void Plugin_internal_state_is_identical_with_custom_or_internal_selected_across_dead_custom_and_grass()
        {
            // rFactor 2 reports the surface per wheel; frames 700-1000 are on grass ("2"). Custom's
            // properties are unreadable for frames 300-550 (all four null). The shared surface classifier is
            // driven by Plugin Internal alone, so neither the dead Custom stretch nor the grass transition
            // can put Plugin Internal's evidence in a different bucket.
            Func<int, SlipAngleInput> input = i =>
            {
                double phase = (i % 240) / 240.0;
                double a = 1.0 + 7.0 * Math.Sin(Math.PI * phase);
                string s = i >= 700 && i < 1000 ? "2" : "0";
                return new SlipAngleInput("RFactor2", true, new Corners(a, a * 1.05, a * 1.2, a * 1.15), true, SlipAngleUnit.Degrees,
                    rawWheelSurface: new[] { s, s, s, s });
            };
            Func<int, bool, SlipAngleFrameRequest> request = (i, custom) =>
            {
                SlipAngleFrameRequest r = Request(i, custom, input(i));
                r.GameId = "RFactor2";
                if (custom && i >= 300 && i < 550) r.CustomWheelsDeg = new double?[] { null, null, null, null };
                return r;
            };

            var selected = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            var custom = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 1400; i++)
            {
                SlipAngleFrameResult a = selected.Process(request(i, false));
                SlipAngleFrameResult b = custom.Process(request(i, true));
                Assert.Equal(a.SurfaceBucket, b.SurfaceBucket);
            }

            string expected = InternalState(selected.Learner);
            Assert.Contains("loose", expected);   // the grass stretch really reached a loose bucket
            Assert.Equal(expected, InternalState(custom.Learner));
        }

        [Fact]
        public void Only_the_active_custom_source_publishes()
        {
            // Two runs whose Plugin Internal input differs (so its learning differs); the published Custom
            // output must not move at all.
            var a = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            var b = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 900; i++)
            {
                SlipAngleFrameResult ra = a.Process(Request(i, custom: true, input: Native(i, 1.0)));
                SlipAngleFrameResult rb = b.Process(Request(i, custom: true, input: Native(i, 2.5)));
                Assert.Equal(CustomId, ra.SourceIdentity);
                Assert.Equal(ra.RawAll, rb.RawAll);
                Assert.Equal(ra.NormalizedAll, rb.NormalizedAll);
                Assert.Equal(ra.ProjectedAll, rb.ProjectedAll);
                Assert.Equal(ra.LearnedWindow, rb.LearnedWindow);
            }
            Assert.True(Samples(a.Learner, SlipAngleFrameProcessor.NativeIdentity) > 0);
        }
    }
}

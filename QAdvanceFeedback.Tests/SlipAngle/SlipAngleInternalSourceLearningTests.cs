using System;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// Task G, Slip Angle half (docs\internal-source-learning-design.md 3.2): Plugin Internal (Native or
    /// Fallback, whichever this frame's raw angle is) learns every frame under its own key. (The Custom source
    /// was removed, owner 2026-11-24: Plugin Internal is the only source.)
    /// </summary>
    public class SlipAngleInternalSourceLearningTests
    {
        private const double Dt = 1.0 / 60.0;

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

        private static SlipAngleFrameRequest Request(int i, SlipAngleInput input) =>
            new SlipAngleFrameRequest { GameId = "G", CarId = "C", Frame = Frame(i), Dt = Dt, Input = input ?? new SlipAngleInput("G") };

        private static long Samples(KeyedSlipAngleLearner learner, string identity) =>
            learner.Export()
                .Where(kv => KeyedSlipAngleLearner.TryParseKey(kv.Key, out _, out _, out string s, out _, out _) && s == identity)
                .Sum(kv => kv.Value.Learner?.Curve?.TotalSamples ?? 0L);

        [Fact]
        public void Plugin_internal_fallback_learns_under_the_fallback_identity()
        {
            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 1200; i++)
            {
                SlipAngleFrameResult r = p.Process(Request(i, null));
                Assert.True(r.SourceIsPluginInternal);
            }

            Assert.True(Samples(p.Learner, SlipAngleFrameProcessor.FallbackIdentity) > 0);
            Assert.Equal(0, Samples(p.Learner, SlipAngleFrameProcessor.NativeIdentity));
        }

        [Fact]
        public void Plugin_internal_native_learns_under_the_native_identity_only()
        {
            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 1200; i++) p.Process(Request(i, Native(i, 1.0)));

            Assert.True(Samples(p.Learner, SlipAngleFrameProcessor.NativeIdentity) > 0);
            Assert.Equal(0, Samples(p.Learner, SlipAngleFrameProcessor.FallbackIdentity));
        }

        [Fact]
        public void Every_learned_key_belongs_to_plugin_internal()
        {
            var p = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 600; i++) p.Process(Request(i, i < 300 ? null : Native(i, 1.0)));

            foreach (string key in p.Learner.Export().Keys)
            {
                Assert.True(KeyedSlipAngleLearner.TryParseKey(key, out _, out _, out string s, out _, out _), key);
                Assert.Contains(s, new[] { SlipAngleFrameProcessor.NativeIdentity, SlipAngleFrameProcessor.FallbackIdentity });
            }
        }
    }
}

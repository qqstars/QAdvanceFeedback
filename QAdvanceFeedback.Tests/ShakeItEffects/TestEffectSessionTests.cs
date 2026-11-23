using System.Collections.Generic;
using System.IO;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// The plugin side of the Test Effects (docs\shakeit-test-effect-mvvm-design.md): one test at a time,
    /// the out-of-game gate, and the rule that the native effects play exactly the published property
    /// values.
    /// </summary>
    public class TestEffectSessionTests
    {
        private readonly object _a = new object(), _b = new object();

        [Fact]
        public void Begin_stops_the_other_owner_and_raises_Stopped()
        {
            var session = new TestEffectSession();
            var stopped = new List<TestStop>();
            session.Stopped += s => stopped.Add(s);

            Assert.True(session.Begin(_a, TestKind.GForce));
            Assert.True(session.Begin(_b, TestKind.Lock));

            Assert.Equal(TestKind.Lock, session.Active);
            Assert.Same(_b, session.ActiveOwner);
            TestStop stop = Assert.Single(stopped);
            Assert.Same(_a, stop.Owner);
            Assert.Equal(TestKind.Lock, stop.By);
        }

        [Fact]
        public void An_owner_of_the_same_kind_also_stops_the_first()
        {
            var session = new TestEffectSession();
            var stopped = new List<TestStop>();
            session.Stopped += s => stopped.Add(s);
            session.Begin(_a, TestKind.Lock);
            Assert.True(session.Begin(_b, TestKind.Lock));
            Assert.Same(_a, Assert.Single(stopped).Owner);
        }

        [Fact]
        public void End_by_an_owner_that_is_not_active_is_a_noop()
        {
            var session = new TestEffectSession();
            var stopped = new List<TestStop>();
            session.Stopped += s => stopped.Add(s);
            session.Begin(_a, TestKind.Slip);

            session.End(_b);
            Assert.Equal(TestKind.Slip, session.Active);

            session.End(_a);
            Assert.Equal(TestKind.None, session.Active);
            Assert.Null(session.ActiveOwner);
            Assert.Empty(stopped);   // ending your own test is not "stopped by another"
        }

        [Fact]
        public void Beginning_again_by_the_active_owner_changes_nothing()
        {
            var session = new TestEffectSession();
            int stopped = 0;
            session.Stopped += _ => stopped++;
            session.Begin(_a, TestKind.Lock);
            Assert.False(session.Begin(_a, TestKind.Lock));
            Assert.Equal(0, stopped);
        }

        [Theory]
        [InlineData(TestKind.Lock, TestKind.Slip, true)]
        [InlineData(TestKind.Lock, TestKind.Lock, true)]
        [InlineData(TestKind.GForce, TestKind.GForce, true)]
        [InlineData(TestKind.GForce, TestKind.Lock, false)]
        [InlineData(TestKind.Slip, TestKind.GForce, false)]
        [InlineData(TestKind.SlipAngle, TestKind.SlipAngle, true)]
        [InlineData(TestKind.SlipAngle, TestKind.Slip, false)]
        [InlineData(TestKind.Lock, TestKind.SlipAngle, false)]
        public void Kinds_sharing_outputs(TestKind a, TestKind b, bool shared) =>
            Assert.Equal(shared, TestEffectSession.SharesOutputs(a, b));

        [Theory]
        [InlineData(false, true, false, false, false, true)]    // driving
        [InlineData(false, true, true, false, false, false)]    // paused
        [InlineData(false, false, false, false, false, false)]  // no game
        [InlineData(true, false, false, false, false, true)]    // test, out of game
        [InlineData(true, true, true, true, true, true)]        // test overrides everything
        public void Gate_opens_for_test_or_live_game_only(bool test, bool running, bool paused, bool menu, bool spectating, bool expected) =>
            Assert.Equal(expected, TestAwareOutputGate.Allows(test, running, paused, menu, spectating));

        [Fact]
        public void Native_effect_values_equal_the_published_projected_values()
        {
            var r = new ProjectedWheelLockSlipResult(
                new Corners(11, 12, 13, 14), 15, 16, 17, 18, 19,
                new Corners(21, 22, 23, 24), 25, 26, 27, 28, 29,
                new Corners(1, 1, 1, 1), 1, 1, 1, 1, 1,
                new Corners(2, 2, 2, 2), 2, 2, 2, 2, 2);
            var publisher = new PropertyPublisher();
            publisher.UpdateProjected(r);
            Dictionary<string, object> published = Published(publisher);

            foreach (EffectSlot slot in System.Enum.GetValues(typeof(EffectSlot)).Cast<EffectSlot>())
            {
                string target = slot.ToString();
                Assert.Equal((double)published[AllPublishedProperties.ProjectedLockPrefix + target], ChannelValues.Lock(r, slot));
                Assert.Equal((double)published[AllPublishedProperties.ProjectedSlipPrefix + target], ChannelValues.Slip(r, slot));
            }
        }

        [Fact]
        public void Native_effect_values_equal_the_published_gforce_values()
        {
            var o = new GForceOutput(1, 2, 3, 4, 5, 6, 7, 8);
            var publisher = new PropertyPublisher();
            publisher.UpdateGForce(o);
            Dictionary<string, object> published = Published(publisher);
            List<string> names = GForcePublishedNames.AllNames().ToList();

            GForcePad[] pads = { GForcePad.BottomFront, GForcePad.BottomRear, GForcePad.BackLow, GForcePad.BackTop };
            for (int p = 0; p < pads.Length; p++)
            {
                Assert.Equal((double?)published[names[2 * p]], ChannelValues.GForce(o, pads[p], right: false));
                Assert.Equal((double?)published[names[2 * p + 1]], ChannelValues.GForce(o, pads[p], right: true));
            }
        }

        [Fact]
        public void DataUpdate_skips_projected_publish_while_lock_slip_test_active()
        {
            string plugin = File.ReadAllText(PluginFile("QAdvanceFeedback.cs"));
            int call = plugin.IndexOf("_publisher.UpdateProjected(projected);", System.StringComparison.Ordinal);
            Assert.True(call > 0);
            string before = plugin.Substring(System.Math.Max(0, call - 400), 400);
            Assert.Contains("if (!_testLockSlipActive)", before);
        }

        [Fact]
        public void Wheel_effects_open_only_for_the_lock_slip_test_or_a_live_game()
        {
            string containers = File.ReadAllText(PluginFile("Integration", "ShakeIt", "QAdvanceFeedbackWheelChannelContainers.cs"));
            Assert.Contains("public override bool IsAllowedOutOfGame => true;", containers);
            Assert.Contains("TestAwareOutputGate.Allows(", containers);
            Assert.Contains("TestLockSlipActive", containers);

            string effect = File.ReadAllText(PluginFile("Integration", "ShakeIt", "QAdvanceFeedbackEffect.cs"));
            Assert.Contains("public override bool AllowTestMode => false;", effect);
        }

        private static Dictionary<string, object> Published(PropertyPublisher publisher)
        {
            List<string> names = AllPublishedProperties.AllNames(diagnosticsEnabled: true).ToList();
            object[] values = publisher.SnapshotAllValuesForCsv();
            var map = new Dictionary<string, object>();
            for (int i = 0; i < names.Count; i++) map[names[i]] = values[i];
            return map;
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(Path.Combine(dir.FullName, "QAdvanceFeedback"), Path.Combine(parts));
        }
    }
}

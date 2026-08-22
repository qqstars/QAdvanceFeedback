using System;
using System.Linq;
using QAdvanceFeedback.Core.Health;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="SafeCall"/> is the exact guard every <c>AttachDelegate</c> value-provider lambda in
    /// <c>PropertyPublisher.Register</c> is wrapped in (<c>PropertyPublisher.AttachSafe</c> calls
    /// straight through to <see cref="SafeCall.Value{T}"/> - see that method's own remarks). This class
    /// cannot be exercised directly against a live SimHub property read (that needs a running SimHub
    /// host - see <c>PropertyPublisherStructureTests</c>'s own remarks on why <c>PropertyPublisher.cs</c>
    /// itself cannot be link-compiled here), so these tests exercise the underlying mechanism directly:
    /// a throwing provider, called exactly the way SimHub itself would call it.
    /// <para/>
    /// MUTATION EVIDENCE: <see cref="A_throwing_provider_never_propagates_and_returns_the_fallback"/>
    /// fails immediately if <see cref="SafeCall.Value{T}"/>'s <c>try/catch</c> is removed (the thrown
    /// <see cref="InvalidOperationException"/> would propagate straight out of the call, exactly the
    /// "one plugin's exception breaks the shared pipeline" hazard this whole task hardens against) -
    /// this is the direct evidence for the value-provider guard the brief asks for.
    /// </summary>
    public class SafeCallTests
    {
        private static string NewSubsystem() => "Test-SafeCall-" + Guid.NewGuid();

        [Fact]
        public void A_non_throwing_provider_returns_its_own_value_untouched()
        {
            string subsystem = NewSubsystem();
            object result = SafeCall.Value(() => (object)42.0, (object)null, subsystem, "Key");

            Assert.Equal(42.0, result);
            Assert.DoesNotContain(HealthRegistry.Snapshot(), e => e.Subsystem == subsystem);
        }

        /// <summary>MUTATION EVIDENCE (see this class's own remarks) - remove the try/catch inside
        /// <see cref="SafeCall.Value{T}"/> and this test fails with the provider's own
        /// <see cref="InvalidOperationException"/> escaping instead of the fallback being returned.</summary>
        [Fact]
        public void A_throwing_provider_never_propagates_and_returns_the_fallback()
        {
            string subsystem = NewSubsystem();

            object result = SafeCall.Value<object>(
                () => throw new InvalidOperationException("simulated AttachDelegate fault"),
                fallback: "safe-default",
                subsystem: subsystem,
                impactKey: "Some.Impact.Key");

            Assert.Equal("safe-default", result);
        }

        [Fact]
        public void A_throwing_provider_records_exactly_one_health_entry_with_the_given_subsystem_and_impact_key()
        {
            string subsystem = NewSubsystem();

            SafeCall.Value<object>(() => throw new InvalidOperationException("boom"), null, subsystem, "Impact.Key.Here");

            HealthEntry entry = HealthRegistry.Snapshot().Single(e => e.Subsystem == subsystem);
            Assert.Equal(HealthSeverity.Degraded, entry.Severity);
            Assert.Equal("Impact.Key.Here", entry.ImpactKey);
            Assert.Contains("boom", entry.Detail);
        }

        /// <summary>Mirrors the exact real shape used for every published property this session: a
        /// value provider that throws EVERY frame (a persistently-faulted property) must still only
        /// ever create ONE health entry, not one per frame - see HealthRegistry's own "log once, not
        /// per frame" tests for the registry side of this; this proves the SafeCall call site actually
        /// reaches it that way under repeated real invocation, not just a single simulated one.</summary>
        [Fact]
        public void A_provider_that_keeps_throwing_every_frame_never_grows_past_one_entry()
        {
            string subsystem = NewSubsystem();
            for (int frame = 0; frame < 200; frame++)
            {
                SafeCall.Value<object>(() => throw new InvalidOperationException("frame fault"), null, subsystem, "Key");
            }

            var matches = HealthRegistry.Snapshot().Where(e => e.Subsystem == subsystem).ToArray();
            Assert.Single(matches);
            Assert.Equal(200, matches[0].OccurrenceCount);
        }
    }
}

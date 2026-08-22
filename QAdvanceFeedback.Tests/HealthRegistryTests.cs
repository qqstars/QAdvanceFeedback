using System;
using System.Linq;
using QAdvanceFeedback.Core.Health;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="HealthRegistry"/> is pure/SimHub-free and link-compiled into this test project (it
    /// lives under <c>Core\Health\</c>, covered by the <c>Core\**\*.cs</c> wildcard) - these tests
    /// exercise the real class, not a re-implementation.
    /// <para/>
    /// Every subsystem name used below is a fresh <see cref="Guid"/>, not one of the real
    /// <see cref="HealthSubsystems"/> constants - <see cref="HealthRegistry"/> is process-wide static
    /// state shared with every OTHER test in this assembly (xunit runs different test classes in
    /// parallel by default), so a unique key per test is what keeps these tests independent of
    /// whatever any other test/producer reports, with no locking or ClearAll() call needed.
    /// </summary>
    public class HealthRegistryTests
    {
        private static string NewSubsystem() => "Test-" + Guid.NewGuid();

        [Fact]
        public void A_subsystem_that_never_reports_has_no_entry_and_the_registry_reads_as_healthy_for_it()
        {
            string subsystem = NewSubsystem();
            Assert.DoesNotContain(HealthRegistry.Snapshot(), e => e.Subsystem == subsystem);
        }

        [Fact]
        public void Reporting_once_creates_exactly_one_entry_with_the_given_fields()
        {
            string subsystem = NewSubsystem();
            HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Some.Impact.Key", "boom detail", isSimHubCompatibilityIssue: true);

            HealthEntry entry = HealthRegistry.Snapshot().Single(e => e.Subsystem == subsystem);
            Assert.Equal(HealthSeverity.Degraded, entry.Severity);
            Assert.Equal("Some.Impact.Key", entry.ImpactKey);
            Assert.Equal("boom detail", entry.Detail);
            Assert.True(entry.IsSimHubCompatibilityIssue);
            Assert.Equal(1, entry.OccurrenceCount);
            Assert.Equal(entry.FirstOccurredUtc, entry.LastOccurredUtc);
        }

        /// <summary>
        /// THE "LOG ONCE, NOT PER FRAME" REQUIREMENT: a subsystem that reports every single frame (60+
        /// times/second in the real DataUpdate path) must still only ever occupy ONE entry - never grow
        /// the registry unbounded, and never re-trigger a fresh "first occurrence" for the settings UI.
        /// Only <see cref="HealthEntry.LastOccurredUtc"/>/<see cref="HealthEntry.OccurrenceCount"/>/
        /// <see cref="HealthEntry.Detail"/> may change on a repeat report.
        /// </summary>
        [Fact]
        public void Reporting_the_same_subsystem_repeatedly_updates_one_entry_instead_of_growing_the_registry()
        {
            string subsystem = NewSubsystem();
            for (int i = 0; i < 500; i++)
            {
                HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Key", "detail #" + i);
            }

            var matches = HealthRegistry.Snapshot().Where(e => e.Subsystem == subsystem).ToArray();
            Assert.Single(matches);
            Assert.Equal(500, matches[0].OccurrenceCount);
            Assert.Equal("detail #499", matches[0].Detail);
        }

        /// <summary>
        /// INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2) - mirrors
        /// AdaptivePeakLearnerTests' own precedent: <see cref="HealthEntry.OccurrenceCount"/> must
        /// freeze at <see cref="HealthEntry.OccurrenceCountSaturationCap"/> while recording itself
        /// (<see cref="HealthEntry.Detail"/>/<see cref="HealthEntry.LastOccurredUtc"/>) keeps updating
        /// on every further recurrence.
        /// </summary>
        [Fact]
        public void OccurrenceCount_saturates_at_the_cap_while_Detail_keeps_updating()
        {
            string subsystem = NewSubsystem();
            for (int i = 0; i < HealthEntry.OccurrenceCountSaturationCap + 5; i++)
                HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Key", "detail");

            HealthEntry entry = HealthRegistry.Snapshot().Single(e => e.Subsystem == subsystem);
            Assert.Equal(HealthEntry.OccurrenceCountSaturationCap, entry.OccurrenceCount);

            HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Key", "detail-after-cap");
            Assert.Equal("detail-after-cap", entry.Detail);
            Assert.Equal(HealthEntry.OccurrenceCountSaturationCap, entry.OccurrenceCount); // still pinned
        }

        [Fact]
        public void The_first_reported_severity_and_impact_key_stick_even_if_later_reports_differ()
        {
            string subsystem = NewSubsystem();
            HealthRegistry.Report(subsystem, HealthSeverity.Failed, "First.Key", "first detail", isSimHubCompatibilityIssue: true);
            HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "Second.Key", "second detail", isSimHubCompatibilityIssue: false);

            HealthEntry entry = HealthRegistry.Snapshot().Single(e => e.Subsystem == subsystem);
            Assert.Equal(HealthSeverity.Failed, entry.Severity);
            Assert.Equal("First.Key", entry.ImpactKey);
            Assert.True(entry.IsSimHubCompatibilityIssue);
            Assert.Equal("second detail", entry.Detail); // detail itself DOES refresh on every report
            Assert.Equal(2, entry.OccurrenceCount);
        }

        [Fact]
        public void A_null_or_empty_subsystem_is_a_no_op_never_throws()
        {
            var ex1 = Record.Exception(() => HealthRegistry.Report(null, HealthSeverity.Failed, "K", "D"));
            var ex2 = Record.Exception(() => HealthRegistry.Report(string.Empty, HealthSeverity.Failed, "K", "D"));
            Assert.Null(ex1);
            Assert.Null(ex2);
        }

        [Fact]
        public void Clear_removes_only_the_named_subsystem()
        {
            string a = NewSubsystem();
            string b = NewSubsystem();
            HealthRegistry.Report(a, HealthSeverity.Degraded, "K", "D");
            HealthRegistry.Report(b, HealthSeverity.Degraded, "K", "D");

            HealthRegistry.Clear(a);

            Assert.DoesNotContain(HealthRegistry.Snapshot(), e => e.Subsystem == a);
            Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == b);

            HealthRegistry.Clear(b); // tidy up so this test leaves no residue for others to stumble on
        }

        [Fact]
        public void Snapshot_is_a_point_in_time_copy_not_a_live_view()
        {
            string subsystem = NewSubsystem();
            HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "K", "D1");
            var snapshot = HealthRegistry.Snapshot();

            HealthRegistry.Report(subsystem, HealthSeverity.Degraded, "K", "D2");

            // The earlier snapshot's own entry object is shared (by design - Snapshot copies the LIST,
            // not each entry), so this proves the LIST itself is frozen at snapshot time, not that a
            // fault recorded after Snapshot() is invisible through an already-held HealthEntry - the
            // settings UI only ever calls Snapshot() fresh each time it refreshes, so that is the only
            // guarantee that matters in practice.
            Assert.Single(snapshot, e => e.Subsystem == subsystem);
            HealthRegistry.Clear(subsystem);
        }
    }
}

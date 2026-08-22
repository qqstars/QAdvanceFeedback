using System;

namespace QAdvanceFeedback.Core.Health
{
    /// <summary>
    /// One subsystem's current fault state, as recorded by <see cref="HealthRegistry"/>. Immutable
    /// except for the "recorded again" bookkeeping (<see cref="LastOccurredUtc"/>/
    /// <see cref="OccurrenceCount"/>/<see cref="Detail"/>), which <see cref="HealthRegistry"/> updates
    /// in place - a persistently-recurring fault (e.g. a value provider that keeps throwing every
    /// frame) mutates the ONE entry for its subsystem rather than growing a new one each time, which is
    /// what actually gives "log once, not per frame" its teeth here: even a caller that reports every
    /// single frame cannot make this registry grow unbounded or re-alert repeatedly.
    /// </summary>
    public sealed class HealthEntry
    {
        /// <summary>Stable identifier for the subsystem - see <see cref="HealthSubsystems"/>. Also the
        /// dictionary key <see cref="HealthRegistry"/> itself is keyed by.</summary>
        public string Subsystem { get; }

        public HealthSeverity Severity { get; }

        /// <summary>Localization KEY (not the rendered text) for a short, driver-readable explanation
        /// of the practical impact - "what does this mean for me", never a stack trace. The settings UI
        /// resolves this through <c>Strings.Get</c> at display time, so it always renders in whatever
        /// culture is active even if that changes after the fault was first recorded.</summary>
        public string ImpactKey { get; }

        /// <summary>True when this fault's likely root cause is a SimHub update having moved, renamed
        /// or reshaped an internal type/member this plugin depends on via reflection (or another
        /// undocumented SimHub dependency, e.g. <c>GetFeedbackCapabilities</c>) - the ONE case the
        /// owner asked to be named plainly ("this feature needs an update for your SimHub version")
        /// rather than shown as an opaque failure.</summary>
        public bool IsSimHubCompatibilityIssue { get; }

        /// <summary>The underlying exception detail - English, deliberately never localized, since this
        /// exists to be copied into a bug report, not read as prose by the driver.</summary>
        public string Detail { get; private set; }

        public DateTime FirstOccurredUtc { get; }

        public DateTime LastOccurredUtc { get; private set; }

        /// <summary>How many times this subsystem has reported a fault this session. Starts at 1.</summary>
        public int OccurrenceCount { get; private set; }

        internal HealthEntry(
            string subsystem, HealthSeverity severity, string impactKey, string detail,
            bool isSimHubCompatibilityIssue, DateTime nowUtc)
        {
            Subsystem = subsystem;
            Severity = severity;
            ImpactKey = impactKey;
            IsSimHubCompatibilityIssue = isSimHubCompatibilityIssue;
            Detail = detail ?? string.Empty;
            FirstOccurredUtc = nowUtc;
            LastOccurredUtc = nowUtc;
            OccurrenceCount = 1;
        }

        /// <summary>INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2) - a
        /// persistently-recurring fault (this class's own remarks: "a value provider that keeps
        /// throwing every frame") could in principle drive <see cref="OccurrenceCount"/> toward
        /// <see cref="int.MaxValue"/> over a sufficiently long-running, never-restarted session; saturate
        /// it at the same shared cap every other counter in this codebase uses
        /// (<see cref="Normalized.GripLearner.SampleCountSaturationCap"/>) rather than let it wrap
        /// negative. A plain counter with nothing else's own mean depending on it - freezing it has no
        /// side effect beyond the reported count itself no longer climbing past the cap; every other
        /// field (<see cref="Detail"/>/<see cref="LastOccurredUtc"/>) keeps updating on every recurrence
        /// regardless.</summary>
        public const int OccurrenceCountSaturationCap = 1_000_000;

        internal void RecordRecurrence(string detail, DateTime nowUtc)
        {
            if (!string.IsNullOrEmpty(detail)) Detail = detail;
            LastOccurredUtc = nowUtc;
            if (OccurrenceCount < OccurrenceCountSaturationCap) OccurrenceCount++;
        }
    }
}

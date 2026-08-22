using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core.Health
{
    /// <summary>
    /// Process-wide registry of "which subsystems are currently degraded", so the settings UI can
    /// surface a driver-readable warning instead of a silent, invisible degrade. Pure and SimHub-free
    /// (only <c>System</c>/<c>System.Collections.Generic</c>/<c>System.Linq</c>) - deliberately, so it
    /// links directly into the SimHub-free test project and is exercised for real, exactly like
    /// <c>ConfigStore</c>/<c>RuntimeStore</c>/<c>CsvExportWriter</c> already are.
    /// <para/>
    /// DESIGN - "no entries" IS the healthy state: every producer in this plugin only ever calls
    /// <see cref="Report"/> from inside a catch block (or an equivalent guard-clause fallback path) -
    /// nothing reports "I am fine" on every frame, which is what keeps the settings UI's health section
    /// invisible/one-line in the normal case rather than a running tally of every subsystem that
    /// happens to be working. See <see cref="IsHealthy"/>.
    /// <para/>
    /// THREAD SAFETY - <see cref="Report"/> is called from wildly different threads (the SimHub
    /// game/data thread via <c>DataUpdate</c>, the WPF UI thread via the settings control, and the raw
    /// ThreadPool thread backing <c>RuntimeStore</c>'s background flush timer - see
    /// docs\pipeline-exception-safety-report.md for why THAT thread in particular must never be allowed
    /// to throw past this call), so every access is behind one lock. The lock is held only for a
    /// dictionary lookup/mutation - never while formatting a message or touching UI - so it can never
    /// become a new place for one subsystem's fault to stall another's.
    /// </summary>
    public static class HealthRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, HealthEntry> Entries =
            new Dictionary<string, HealthEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Records a fault for <paramref name="subsystem"/> (see <see cref="HealthSubsystems"/>). The
        /// FIRST call for a given subsystem creates its entry (severity/impact/compatibility-flag are
        /// fixed at that point); every subsequent call for the SAME subsystem only bumps
        /// <see cref="HealthEntry.LastOccurredUtc"/>/<see cref="HealthEntry.OccurrenceCount"/> and
        /// refreshes <see cref="HealthEntry.Detail"/> - this is what makes "log/record once, not per
        /// frame" hold even for a caller that reports every single frame. A null/empty
        /// <paramref name="subsystem"/> is a no-op (never throws - this method is itself called from
        /// inside other classes' OWN exception handlers, so it must not be a new way for one of those to
        /// fail).
        /// </summary>
        public static void Report(
            string subsystem, HealthSeverity severity, string impactKey, string detail,
            bool isSimHubCompatibilityIssue = false)
        {
            if (string.IsNullOrEmpty(subsystem)) return;

            DateTime now = DateTime.UtcNow;
            lock (Gate)
            {
                if (Entries.TryGetValue(subsystem, out HealthEntry existing))
                {
                    existing.RecordRecurrence(detail, now);
                }
                else
                {
                    Entries[subsystem] = new HealthEntry(subsystem, severity, impactKey, detail, isSimHubCompatibilityIssue, now);
                }
            }
        }

        /// <summary>A point-in-time copy of every currently-recorded entry - safe to enumerate without
        /// holding the lock, and never mutated after being returned (see <see cref="HealthEntry"/>'s own
        /// "recorded again" mutation, which only ever happens to an entry still held INSIDE this
        /// registry, never to one already handed out via a previous snapshot).</summary>
        public static IReadOnlyList<HealthEntry> Snapshot()
        {
            lock (Gate) return Entries.Values.ToArray();
        }

        /// <summary>True when nothing has ever reported a fault this session - the settings UI's own
        /// "all good" state.</summary>
        public static bool IsHealthy
        {
            get { lock (Gate) return Entries.Count == 0; }
        }

        /// <summary>Removes one subsystem's entry, if present. Not used by any production code path
        /// today (a fault, once recorded, stays visible for the rest of the session so a driver who
        /// opens the settings screen late still sees it) - kept for test isolation and for a future
        /// "this subsystem recovered" path.</summary>
        public static void Clear(string subsystem)
        {
            if (string.IsNullOrEmpty(subsystem)) return;
            lock (Gate) Entries.Remove(subsystem);
        }

        /// <summary>Test-only reset. Not called from any production code path.</summary>
        public static void ClearAll()
        {
            lock (Gate) Entries.Clear();
        }
    }
}

namespace QAdvanceFeedback.Core.Health
{
    /// <summary>
    /// How seriously a subsystem reported by <see cref="HealthRegistry"/> is degraded. Deliberately a
    /// small, closed set - this plugin only ever needs to answer "is this feature still working, only
    /// half-working, or not working at all", never a finer-grained diagnostic taxonomy.
    /// </summary>
    public enum HealthSeverity
    {
        /// <summary>Reserved for a future informational entry that is not itself a fault. No producer
        /// in this plugin currently reports <see cref="Info"/> - only genuine faults are ever recorded
        /// (see <see cref="HealthRegistry"/>'s own remarks on why "no entries at all" is deliberately
        /// the healthy/no-clutter state, not "an Info entry saying everything is fine").</summary>
        Info = 0,

        /// <summary>The subsystem stopped doing its job, but a graceful fallback is already active and
        /// the rest of the plugin is unaffected (e.g. the property picker is unavailable, so the
        /// settings UI falls back to a plain text box).</summary>
        Degraded = 1,

        /// <summary>The subsystem could not do its job at all this session, with no fallback covering
        /// for it (e.g. the plugin failed to finish initialising).</summary>
        Failed = 2,
    }
}

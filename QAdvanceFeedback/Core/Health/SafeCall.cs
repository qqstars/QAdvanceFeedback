using System;

namespace QAdvanceFeedback.Core.Health
{
    /// <summary>
    /// Defensive wrapper for a value-provider delegate - the exact shape every SimHub
    /// <c>AttachDelegate</c> lambda has. See <c>PropertyPublisher.Register</c>'s own remarks on why
    /// EVERY one of those lambdas must individually be incapable of throwing: they are invoked by
    /// arbitrary consumers (dashboards, ShakeIt effects, the property picker) on paths SimHub does NOT
    /// guard at the primitive level - <c>PropertyEntry.Evaluate()</c>/<c>PropertyEntryWrapper.
    /// GetValue()</c> are themselves unguarded primitives (confirmed by decompilation, see
    /// docs\pipeline-exception-safety-report.md), so a throw here would propagate straight into
    /// whichever OTHER plugin/dashboard/effect happened to be reading this property at the time.
    /// <para/>
    /// <see cref="Value{T}"/> catches ANY exception <paramref name="provider"/> throws, records it in
    /// <see cref="HealthRegistry"/> (once - see that class's own remarks on why a caller that throws
    /// every frame still only ever creates one entry) and returns <paramref name="fallback"/> instead of
    /// letting the exception escape into SimHub's own property-read call stack.
    /// </summary>
    public static class SafeCall
    {
        public static T Value<T>(Func<T> provider, T fallback, string subsystem, string impactKey)
        {
            try
            {
                return provider();
            }
            catch (Exception e)
            {
                HealthRegistry.Report(subsystem, HealthSeverity.Degraded, impactKey, e.ToString());
                return fallback;
            }
        }
    }
}

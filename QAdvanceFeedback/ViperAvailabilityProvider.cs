using System;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Viper;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Adapts a live <see cref="PluginManager"/> to <see cref="ViperAvailabilityResolver"/> so the
    /// settings UI can decide whether to show the "plugin not detected" warning under the Viper
    /// source-mode option. The twin of <see cref="MotorsExportAvailabilityProvider"/> and held to the
    /// same discipline: never throws, degrades silently, logs at most once.
    /// <para/>
    /// ONE ANSWER FOR BOTH CHANNELS, unlike the ShakeIt provider. Lock and Slip read the SAME four Viper
    /// properties and differ only in sign, so there is a single availability fact; a per-channel API
    /// here would imply a distinction that does not exist.
    /// <para/>
    /// NOT CACHED as a sticky true/false: four cheap property lookups, re-checked each call, so a driver
    /// who installs the plugin mid-session sees the warning clear without restarting SimHub. Only the
    /// log line is throttled.
    /// </summary>
    public sealed class ViperAvailabilityProvider
    {
        private bool _loggedUnavailable;

        public bool IsAvailable(PluginManager pluginManager)
        {
            bool available = ViperAvailabilityResolver.IsAvailable(name => SafeGet(pluginManager, name));
            if (!available) LogOnceUnavailable();
            return available;
        }

        private static object SafeGet(PluginManager pluginManager, string name)
        {
            try { return pluginManager?.GetPropertyValue(name); }
            catch (Exception e)
            {
                // Distinct from the resolver simply concluding "not installed" - the common, expected,
                // NOT-a-fault case already surfaced by the inline UI warning. This is a real exception
                // out of GetPropertyValue, which IS worth recording.
                HealthRegistry.Report(HealthSubsystems.ShakeItExport, HealthSeverity.Degraded,
                    "Health.Impact.ShakeItExport", e.ToString());
                return null;
            }
        }

        private void LogOnceUnavailable()
        {
            if (_loggedUnavailable) return;
            _loggedUnavailable = true;

            try
            {
                SimHub.Logging.Current?.Info(
                    "QAdvanceFeedback: viper4gh CalcLngWheelSlip is not currently publishing "
                    + ViperPropertyNames.ComputedPrefix
                    + "{FL,FR,RL,RR}. The Viper source mode will read 0 until it does.");
            }
            catch
            {
                // Logging is a convenience here; never let it affect the settings page.
            }
        }
    }
}

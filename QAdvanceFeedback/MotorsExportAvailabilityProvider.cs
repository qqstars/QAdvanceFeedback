using System;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.MotorsExport;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Adapts a live <see cref="PluginManager"/> to <see cref="MotorsExportAvailabilityResolver"/> so
    /// the settings UI can decide, per channel, whether to show the "not currently available yet"
    /// inline note under the ShakeIt Motors source-mode toggle - the toggle itself is always visible
    /// and switchable regardless (see <c>Settings.SettingsControl</c>'s own remarks). Follows the same
    /// discipline as <see cref="SimHubScriptEditor"/>/<see cref="PropertyPickerLauncher"/>/
    /// <see cref="SimHubExpressionEvaluator"/>: never throws, degrades silently, logs at most once per
    /// channel. Unlike those three, no private-type reflection is needed here at all - SimHub's own
    /// <c>PluginManager.GetPropertyValue</c> is a normal public API - all of the actual "is this really
    /// usable" logic lives in the pure, unit-tested <see cref="MotorsExportAvailabilityResolver"/>; this
    /// class only supplies the live property reader and the once-only logging.
    /// <para/>
    /// Deliberately NOT cached as a sticky true/false forever: availability is re-checked every call
    /// (each check is just four cheap property lookups), so a driver who sets up the ShakeIt export
    /// mid-session sees the note disappear without a SimHub restart. Only the "not currently available"
    /// log line is throttled to once per channel.
    /// </summary>
    public sealed class MotorsExportAvailabilityProvider
    {
        private bool _loggedLockUnavailable;
        private bool _loggedSlipUnavailable;

        public bool IsLockAvailable(PluginManager pluginManager) => Check(pluginManager, isLockChannel: true);

        public bool IsSlipAvailable(PluginManager pluginManager) => Check(pluginManager, isLockChannel: false);

        private bool Check(PluginManager pluginManager, bool isLockChannel)
        {
            bool available = MotorsExportAvailabilityResolver.IsAvailable(name => SafeGet(pluginManager, name), isLockChannel);
            if (!available) LogOnceUnavailable(isLockChannel);
            return available;
        }

        private static object SafeGet(PluginManager pluginManager, string name)
        {
            try { return pluginManager?.GetPropertyValue(name); }
            catch (Exception e)
            {
                // Distinct from the resolver simply concluding "not configured yet" (the common,
                // expected, NOT-a-fault case already surfaced via the inline UI note) - this is a real
                // exception from GetPropertyValue itself, which IS a fault worth recording.
                HealthRegistry.Report(HealthSubsystems.ShakeItExport, HealthSeverity.Degraded,
                    "Health.Impact.ShakeItExport", e.ToString());
                return null;
            }
        }

        private void LogOnceUnavailable(bool isLockChannel)
        {
            if (isLockChannel)
            {
                if (_loggedLockUnavailable) return;
                _loggedLockUnavailable = true;
            }
            else
            {
                if (_loggedSlipUnavailable) return;
                _loggedSlipUnavailable = true;
            }

            string channel = isLockChannel ? "Wheel Lock" : "Wheel Slip";
            try
            {
                SimHub.Logging.Current.Info(
                    "QAdvanceFeedback: ShakeIt Motors export for " + channel + " is not currently available " +
                    "(one or more of the four expected properties is not registered) - the 'Use ShakeIt " +
                    "Motors output' toggle is hidden for this channel until it is. See docs\\shakeit-export-guide.md.");
            }
            catch { /* logging must never be the thing that throws */ }
        }
    }
}

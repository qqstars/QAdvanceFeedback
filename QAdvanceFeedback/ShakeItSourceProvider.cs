using QAdvanceFeedback.Core.ShakeIt;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Adapts a live <see cref="PluginManager"/> to <see cref="ShakeItAvailabilityResolver"/> so the
    /// settings UI can decide, ONCE per channel, whether to show the "Use ShakeIt Motors output"
    /// toggle at all - "if ShakeIt cannot be FULLY resolved, hide the toggle entirely and behave as
    /// Manual" (the brief's own words). Follows the same discipline as
    /// <see cref="SimHubScriptEditor"/>/<see cref="PropertyPickerLauncher"/>/
    /// <see cref="SimHubExpressionEvaluator"/>: never throws, degrades silently, logs at most once per
    /// channel. Unlike those three, no private-type reflection is needed here at all - SimHub's own
    /// <c>PluginManager.GetPropertyValue</c> is a normal public API (already used by
    /// <see cref="WheelSourceResolver"/>) - all of the actual "is this really usable" logic lives in
    /// the pure, unit-tested <see cref="ShakeItAvailabilityResolver"/>; this class only supplies the
    /// live property reader and the once-only logging.
    /// <para/>
    /// Deliberately NOT cached as a sticky true/false forever: availability is re-checked every call
    /// (each check is just four cheap <c>PluginManager.GetPropertyValue</c> lookups - the same cost
    /// <see cref="WheelSourceResolver"/> already pays every frame for a Plain source), so a driver who
    /// sets up the ShakeIt export mid-session sees the toggle appear without a SimHub restart. Only
    /// the "not currently available" LOG line is throttled to once per channel, so a driver who simply
    /// has not configured ShakeIt yet does not get a line spammed into the log.
    /// </summary>
    public sealed class ShakeItSourceProvider
    {
        private bool _loggedLockUnavailable;
        private bool _loggedSlipUnavailable;

        public bool IsLockAvailable(PluginManager pluginManager) => Check(pluginManager, isLockChannel: true);

        public bool IsSlipAvailable(PluginManager pluginManager) => Check(pluginManager, isLockChannel: false);

        private bool Check(PluginManager pluginManager, bool isLockChannel)
        {
            bool available = ShakeItAvailabilityResolver.IsAvailable(name => SafeGet(pluginManager, name), isLockChannel);
            if (!available) LogOnceUnavailable(isLockChannel);
            return available;
        }

        private static object SafeGet(PluginManager pluginManager, string name)
        {
            try { return pluginManager?.GetPropertyValue(name); }
            catch { return null; }
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

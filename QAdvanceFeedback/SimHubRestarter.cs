using System;
using QAdvanceFeedback.Core.Localization;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Restarts SimHub after an update (owner, 2026-11-25, 1.3.0) - THE WAY AZOM DOES IT: SimHub's own
    /// <see cref="PluginManager.RequestApplicationExit"/> with <c>restart: true</c>, which raises SimHub's
    /// <c>ApplicationExitRequested</c>; the SimHub application then runs its normal exit (every plugin's End - this one's
    /// final saves included) and starts itself again. The first version closed the WPF application itself and relied on a
    /// detached helper process to start SimHub again; SimHub did not come back (owner-reported).
    /// </summary>
    public static class SimHubRestarter
    {
        /// <summary>Asks SimHub to restart. Returns an error message, or null when the restart was requested.</summary>
        public static string Restart()
        {
            try
            {
                QAdvanceFeedback plugin = QAdvanceFeedback.Instance;
                PluginManager pm = plugin?.PluginManager;
                if (pm == null) return string.Format(Strings.Get("Update.Error.Apply"), "SimHub's plugin manager is not available");
                plugin.SaveBeforeRestart();   // best effort; End saves again during SimHub's exit
                SimHub.Logging.Current.Info("QAdvanceFeedback: update installed - asking SimHub to restart");
                pm.RequestApplicationExit(true);
                return null;
            }
            catch (Exception e)
            {
                return string.Format(Strings.Get("Update.Error.Apply"), e.Message);
            }
        }
    }
}

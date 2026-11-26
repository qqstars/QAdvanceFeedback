using System;
using System.Diagnostics;
using QAdvanceFeedback.Core.Localization;

namespace QAdvanceFeedback
{
    /// <summary>Restarts SimHub after an update (owner, 2026-11-25, 1.3.0) - apart from <see cref="PluginUpdater"/>
    /// because it needs WPF (the application to close).</summary>
    public static class SimHubRestarter
    {
        /// <summary>
        /// Starts a hidden helper that waits for this SimHub process to exit and then starts the same SimHub executable
        /// again, then closes SimHub normally (so every plugin's End - this one's final saves included - still runs).
        /// Returns an error message, or null.
        /// </summary>
        public static string Restart()
        {
            try
            {
                Process current = Process.GetCurrentProcess();
                string exe = current.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return string.Format(Strings.Get("Update.Error.Apply"), "SimHub's executable was not found");
                string command = "Wait-Process -Id " + current.Id + " -ErrorAction SilentlyContinue; Start-Process -FilePath '"
                    + exe.Replace("'", "''") + "'";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                System.Windows.Application app = System.Windows.Application.Current;
                if (app == null) return string.Format(Strings.Get("Update.Error.Apply"), "no application to close");
                app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
                return null;
            }
            catch (Exception e)
            {
                return string.Format(Strings.Get("Update.Error.Apply"), e.Message);
            }
        }

    }
}

using System.Collections.Generic;
using QAdvanceFeedback.Core.Health;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// What the General tab needs from the running plugin beyond <see cref="ISettingsPageHost"/>
    /// (docs\full-mvvm-plan.md §3.2). The General view model casts <c>Ctx.Host</c> to this and tolerates
    /// its absence (health then comes straight from <see cref="HealthRegistry"/>, the version reads
    /// "unknown" and the import is disabled).
    /// </summary>
    public interface IGeneralPageHost
    {
        /// <summary>The plugin health entries now recorded - <see cref="HealthRegistry.Snapshot"/>.</summary>
        IReadOnlyList<HealthEntry> HealthSnapshot();

        /// <summary>The running plugin DLL's file version (its assembly version as a fallback, "unknown"
        /// if neither can be read) - never hardcoded, so the General tab cannot drift from the DLL loaded.</summary>
        string PluginVersion { get; }

        /// <summary>Syncs the parameters file with SimHub's ShakeIt preset files now (the "Convert SimHub ShakeIt Reference
        /// Data" button) - the same as every plugin start. <paramref name="overrideExisting"/>: a game already held takes
        /// SimHub's values (true) or is kept, only new ones added (false).</summary>
        global::QAdvanceFeedback.ShakeItConversionOutcome ImportShakeItPrecalibration(bool overrideExisting);
    }

    /// <summary>
    /// Optional host capability (owner, 2026-11-25, 1.3.0): the auto-update. The state itself is
    /// <see cref="UpdateStatusViewModel.Current"/>, which the check sets from its own thread.
    /// </summary>
    public interface IUpdateHost
    {
        /// <summary>Starts the update check on a new background thread and returns at once.</summary>
        void CheckForUpdate();

        /// <summary>Puts the downloaded DLL in place and restarts SimHub; an error message, or null when restarting.</summary>
        string ApplyUpdate();
    }
}

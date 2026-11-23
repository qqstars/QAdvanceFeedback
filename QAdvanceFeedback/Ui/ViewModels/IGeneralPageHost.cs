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

        /// <summary>Imports SimHub's shipped ShakeIt precalibration now (the "Convert SimHub ShakeIt
        /// Reference Data" button); <paramref name="overrideExisting"/> replaces data this plugin already
        /// holds. Persists at once - not part of the page's Apply. Never throws (the plugin reports a
        /// failure in the outcome's message).</summary>
        global::QAdvanceFeedback.ShakeItConversionOutcome ImportShakeItPrecalibration(bool overrideExisting);
    }
}

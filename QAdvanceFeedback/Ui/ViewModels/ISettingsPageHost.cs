using System;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// What the settings page needs from the running plugin (docs\full-mvvm-plan.md §3.2). Extends
    /// <see cref="IPanelHost"/>, so the page reads the same settings, hub, game and learned values through
    /// the same rules as the ShakeIt panels. Areas that need more declare their own small interface; the
    /// plugin implements them all. Tests use <c>FakeSettingsPageHost</c>.
    /// </summary>
    public interface ISettingsPageHost : IPanelHost
    {
        /// <summary>Applies and saves the live settings (rebuilds the engines, writes the config file) -
        /// what the page's Apply calls after the draft was written into the live settings.</summary>
        void ApplySettings();

        /// <summary>The shipped default settings (<see cref="QAdvanceFeedbackSettings.CreateDefault"/>).
        /// Restore-all does not build from this: it keeps today's carry-across rule by running
        /// <see cref="QAdvanceFeedbackSettings.RestoreDefaults"/> on a copy of the live settings.</summary>
        QAdvanceFeedbackSettings CreateDefaultSettings();

        /// <summary>The plugin's state the page shows changed (current game, learned key data, source
        /// availability): view models re-read the host. Replaces the plugin's push calls into the page.
        /// May be raised on any thread - marshal with <c>IUiDispatcher.Post</c>.</summary>
        event EventHandler PluginStateChanged;

        /// <summary>Writes an error line to the SimHub log (a failed page action; see
        /// <see cref="SettingsPageContext.ReportFailure"/>).</summary>
        void LogError(string message);
    }

    /// <summary>
    /// Everything the running plugin offers the settings page: <see cref="ISettingsPageHost"/> plus every
    /// area's own host interface. The plugin's page host (Integration\ShakeIt\PluginPageHost) implements
    /// this, so a member an area needs cannot be forgotten. The view models still take
    /// <see cref="ISettingsPageHost"/> and cast to the area interface they use (tolerating its absence),
    /// so a test fake implements only what its area needs - and "no such host" stays a testable state.
    /// </summary>
    public interface IPluginSettingsPageHost : ISettingsPageHost, IWheelChannelPageHost, IKeyDataPageHost,
        IGeneralPageHost, ISlipAngleTabHost, IClutchHost, IShakeItSourceStateHost
    {
    }
}

using System;
using System.Windows.Controls;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Integration.ShakeIt;
using QAdvanceFeedback.Ui.ViewModels;
using QAdvanceFeedbackViews;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// The plugin settings page (docs\full-mvvm-plan.md §3): ONE <see cref="SettingsPageViewModel"/>,
    /// created with the first page SimHub asks for and disposed with the plugin (<see cref="End"/>). Its
    /// hub subscription therefore lives from then on, whether or not the page is on screen - the
    /// owner-reported defect was a page that listened only while loaded. Every view SimHub builds
    /// (<see cref="GetWPFSettingsControl"/>) is a thin <see cref="Settings.SettingsControl"/> bound to it.
    /// <para/>
    /// The plugin never pushes into the page any more: it raises <see cref="SettingsPageStateChanged"/>
    /// (game change, the 1 Hz learned readout while driving, a key data seed, a ShakeIt source state or
    /// nativeness change) and the view models re-read what they show through
    /// <see cref="PluginPageHost"/> - by the same rules as the ShakeIt panels.
    /// </summary>
    public sealed partial class QAdvanceFeedback
    {
        private SettingsPageViewModel _settingsPage;
        private PluginPageHost _pageHost;

        /// <summary>The state the settings page shows changed (see the class remarks). Raised on any
        /// thread, the data thread included; the page marshals it to the UI thread.</summary>
        public event EventHandler SettingsPageStateChanged;

        private void RaiseSettingsPageStateChanged()
        {
            EventHandler handler = SettingsPageStateChanged;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception e)
            {
                // A page fault must never take the frame loop down.
                HealthRegistry.Report(HealthSubsystems.SettingsUi, HealthSeverity.Degraded, "Health.Impact.SettingsUi", e.ToString());
            }
        }

        /// <summary>Builds a page view over the one page view model (created on first use, on SimHub's UI
        /// thread). Throws only on a genuine construction fault - <see cref="GetWPFSettingsControl"/>
        /// turns that into a placeholder.</summary>
        private Control CreateSettingsControl(PluginManager pluginManager)
        {
            if (_settingsPage == null)
            {
                _pageHost = new PluginPageHost(this, pluginManager);
                _settingsPage = new SettingsPageViewModel(_pageHost, new WpfUiDispatcher(), new DispatcherTicker(),
                    new WpfDialogService(), new WpfClipboard());
            }
            // A NEW VIEW: tell it the running game at once rather than at the next game CHANGE, which
            // while paused in one title never comes.
            _lastGameIdPushedToUi = null;
            var control = new Settings.SettingsControl(_settingsPage);
            _pageHost.AttachView(control);
            return control;
        }

        private void DisposeSettingsPage()
        {
            SettingsPageViewModel page = _settingsPage;
            _settingsPage = null;
            page?.Dispose();
        }

        /// <summary>
        /// Whether the manual override gate has opened for the current (game, car) context and a source
        /// identity - the value the old 1 Hz push carried as lockManualLive / slipManualLive, read by the
        /// page (and only by it) through the same context the learned display uses
        /// (<see cref="GetLearnedKeyDataForDisplay"/>). False with no context.
        /// </summary>
        public bool ManualGateReadyForDisplay(bool isLockChannel, string sourceIdentity)
        {
            try
            {
                lock (_engineLock)   // the learners are written by the frame - see DataUpdate
                {
                    if (_lastGameIdForKeyData == null) return false;
                    return _normalizedEngine.ManualGateReady(_lastGameIdForKeyData, _lastCarIdForKeyData, sourceIdentity, isLockChannel);
                }
            }
            catch (Exception) { return false; }
        }
    }
}

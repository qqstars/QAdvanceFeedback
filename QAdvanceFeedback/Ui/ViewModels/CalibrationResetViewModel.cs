using System;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// Optional host capability (owner, 2026-11-25): the slip / lock calibration of the CURRENT key - the track and car
    /// the game reports, the calibration's own key, shared by Wheel Slip and Wheel Lock. The plugin's page and panel
    /// hosts implement it; a host without it shows no Reset Calibration button.
    /// </summary>
    public interface ICalibrationResetHost
    {
        /// <summary>Whether any calibration exists for the current key.</summary>
        bool HasCalibrationForCurrentKey();

        /// <summary>Deletes the current key's calibration (every metric: Slip, Lock front and rear) and saves the file
        /// soon. True when something was deleted.</summary>
        bool ResetCalibrationForCurrentKey();

        /// <summary>"Persist Calibration data?" as applied.</summary>
        bool PersistCalibration { get; }

        /// <summary>Applies "Persist Calibration data?" at once: saves the config now; turning it off deletes every
        /// calibration (memory and file). Both tabs follow through the settings broadcast.</summary>
        void SetPersistCalibration(bool persist);
    }

    /// <summary>
    /// "Persist Calibration data?" (owner, 2026-11-25): ONE instance shared by the Wheel Lock and Wheel Slip tabs, so a
    /// change on one shows on the other at once (both views bind two-way to the same <see cref="IsOn"/>). A change is
    /// applied immediately by the host - not with the page's Apply - and the page draft adopts it from the broadcast.
    /// <see cref="Refresh"/> re-reads the applied value (a change made elsewhere, Restore All Defaults).
    /// </summary>
    public sealed class CalibrationPersistViewModel : ObservableObject
    {
        private readonly Func<object> _host;
        private bool _isOn = true;

        public CalibrationPersistViewModel(Func<object> host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>The switch: two-way; setting it applies at once.</summary>
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (!SetProperty(ref _isOn, value)) return;
                (_host() as ICalibrationResetHost)?.SetPersistCalibration(value);
                Changed?.Invoke();
            }
        }

        /// <summary>Raised after the driver changed the switch (the owners refresh their Reset button).</summary>
        public event Action Changed;

        /// <summary>Re-reads the applied value without applying anything.</summary>
        public void Refresh()
        {
            if (!(_host() is ICalibrationResetHost host)) return;
            SetProperty(ref _isOn, host.PersistCalibration, nameof(IsOn));
        }
    }

    /// <summary>
    /// The Reset Calibration button of the Wheel Lock / Wheel Slip settings tabs and their ShakeIt panels (owner,
    /// 2026-11-25: "in some game, we might change the wheel for a same car"). Shown only while the current key has
    /// calibration data (<see cref="IsVisible"/>); a reset deletes it for both channels, so the button hides at once -
    /// and on the other channel's tab or panel at its next <see cref="Refresh"/>. The owner refreshes it on its own
    /// ticker (once a second) while on screen.
    /// </summary>
    public sealed class CalibrationResetViewModel : ObservableObject
    {
        private readonly Func<object> _host;
        private bool _isVisible;
        private RelayCommand _resetCommand;

        /// <param name="host">The current host - read at every call (a panel may be built before the plugin runs);
        /// anything that is not an <see cref="ICalibrationResetHost"/> shows no button.</param>
        public CalibrationResetViewModel(Func<object> host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>The button is shown: the current key has calibration data.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            private set => SetProperty(ref _isVisible, value);
        }

        public RelayCommand ResetCommand => _resetCommand ?? (_resetCommand = new RelayCommand(Reset));

        /// <summary>Re-reads whether the current key has calibration data.</summary>
        public void Refresh() => IsVisible = (_host() as ICalibrationResetHost)?.HasCalibrationForCurrentKey() ?? false;

        private void Reset()
        {
            (_host() as ICalibrationResetHost)?.ResetCalibrationForCurrentKey();
            Refresh();
        }
    }
}

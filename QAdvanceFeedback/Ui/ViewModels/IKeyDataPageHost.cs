using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// What the Wheel Lock / Wheel Slip key data section of the settings page needs from the running
    /// plugin beyond <see cref="ISettingsPageHost"/> (docs\full-mvvm-plan.md §3.2: "manual-gate state").
    /// The plugin implements it (Wave 2 makes <see cref="ISettingsPageHost"/> extend it); a host without it
    /// reads as "no shadow, gate not ready".
    /// </summary>
    public interface IKeyDataPageHost
    {
        /// <summary>
        /// Whether a channel configured as <paramref name="draftChannel"/> (the page's unapplied source
        /// mode) cannot work right now, so it reads - and keys its key data as - Raw: the Viper source on
        /// an unsupported title, or the ShakeIt Built-in source not set up / not supported
        /// (<c>QAdvanceFeedback.ViperSourceBlocked(channel, CurrentGameId) ||
        /// QAdvanceFeedback.ShakeItSourceBlocked(channel, isLock)</c> - the old page's ChannelShadowsRaw).
        /// </summary>
        bool SourceShadowsRaw(WheelChannelSettings draftChannel, bool isLock);

        /// <summary>
        /// Whether the manual override gate has opened for the current (game, car) context and this
        /// source identity: the session finished calibrating and 30 s of driving accumulated
        /// (<c>NormalizedWheelLockSlipEngine.ManualGateReady</c>, the value the old 1 Hz push carried as
        /// lockManualLive / slipManualLive).
        /// </summary>
        bool ManualGateReady(bool isLock, string sourceIdentity);
    }
}

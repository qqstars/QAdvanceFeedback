using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// One channel's "ShakeIt algorithm" settings (docs\shakeit-source-design.md §6): what the hidden
    /// WheelsLock/WheelsSlip container reads when the channel's source is ShakeIt Built-in. Stored on
    /// <see cref="WheelChannelSettings.ShakeItAlgorithm"/> for both channels; each channel uses its own part:
    /// <list type="bullet">
    /// <item>Wheel Lock: <see cref="UseLegacyIracingAlgorithm"/>, <see cref="LockSensibility"/>,
    /// <see cref="LockOnly"/>.</item>
    /// <item>Wheel Slip: <see cref="UseLegacyIracingAlgorithm"/>, <see cref="MuteWhileLockActive"/>.</item>
    /// </list>
    /// The other channel's fields stay at their defaults and are never read. SimHub's
    /// <c>LockSensibilityEx</c> is the same value as <c>LockSensibility</c>, and <c>LowQualityAlgorythmLevel</c>
    /// is a capability-derived warning level SimHub recomputes per game (not saved, not in its editor), so
    /// neither is a setting here (Task D report). A config saved before this existed loads the defaults.
    /// </summary>
    public sealed class ShakeItAlgorithmSettings
    {
        private double _lockSensibility = ShakeItAlgorithmDefaults.LockSensibility;

        /// <summary>SimHub's "Use legacy IRacing algorythm". Default on.</summary>
        public bool UseLegacyIracingAlgorithm { get; set; } = ShakeItAlgorithmDefaults.UseLegacyIracingAlgorithm;

        /// <summary>Lock only: SimHub's "Lock Sensibility", 0-100, snapped to its editor's step of 5 on every
        /// set (so on load too). Default 50.
        /// Separate from <see cref="WheelChannelSettings.LockSensibility"/>, which drives our Raw algorithm.</summary>
        public double LockSensibility
        {
            get => _lockSensibility;
            set => _lockSensibility = ShakeItAlgorithmDefaults.NormaliseLockSensibility(value);
        }

        /// <summary>Lock only: SimHub's "Use lock only" (on) vs "Use lock and slip" (off) - its
        /// <c>WheelsLockContainer.IsLock</c>. Default on.</summary>
        public bool LockOnly { get; set; } = ShakeItAlgorithmDefaults.LockOnly;

        /// <summary>Slip only: "Mute slip while lock is active" - emulated by us. Default off.</summary>
        public bool MuteWhileLockActive { get; set; } = ShakeItAlgorithmDefaults.MuteWhileLockActive;

        /// <summary>The channel's learning-key fingerprint: empty at the defaults.</summary>
        public string Fingerprint(bool isLockChannel) => isLockChannel
            ? ShakeItAlgorithmFingerprint.ForLock(UseLegacyIracingAlgorithm, LockSensibility, LockOnly)
            : ShakeItAlgorithmFingerprint.ForSlip(UseLegacyIracingAlgorithm, MuteWhileLockActive);

        /// <summary>
        /// The hidden containers' configuration for these two channels, reusing <paramref name="previous"/>
        /// while nothing changed (no allocation on an unchanged frame). Null channels read the defaults.
        /// </summary>
        public static ShakeItAlgorithmSnapshot Snapshot(WheelChannelSettings lockChannel, WheelChannelSettings slipChannel,
            ShakeItAlgorithmSnapshot previous)
        {
            ShakeItAlgorithmInputs d = ShakeItAlgorithmInputs.Defaults;
            ShakeItAlgorithmSettings l = lockChannel?.ShakeItAlgorithm;
            ShakeItAlgorithmSettings s = slipChannel?.ShakeItAlgorithm;
            var inputs = new ShakeItAlgorithmInputs(
                l?.UseLegacyIracingAlgorithm ?? d.LockUseLegacyIracingAlgorithm,
                l?.LockSensibility ?? d.LockSensibility,
                l?.LockOnly ?? d.LockOnly,
                lockChannel?.BrakeThresholdPercent ?? d.LockBrakeThresholdPercent,
                s?.UseLegacyIracingAlgorithm ?? d.SlipUseLegacyIracingAlgorithm,
                s?.MuteWhileLockActive ?? d.SlipMuteWhileLockActive,
                slipChannel?.BrakeThresholdPercent ?? d.SlipBrakeThresholdPercent,
                slipChannel?.ThrottleThresholdPercent ?? d.SlipThrottleThresholdPercent);
            return ShakeItAlgorithmSnapshot.Reuse(previous, inputs);
        }
    }
}

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Root user-configuration object, persisted through <c>ConfigStore</c> to
    /// <c>plugin.QAdvanceFeedback.config.json</c> (under SimHub's <c>PluginsData\Common</c> folder -
    /// see <c>ConfigStore</c>'s own remarks). Public settable properties with a parameterless
    /// constructor so Newtonsoft round-trips it without attributes, matching every other settings
    /// POCO in this plugin family.
    /// <para/>
    /// Holds every top-level settings group the plugin wiring needs: the Wheel Lock/Wheel Slip
    /// channels (<see cref="WheelChannelSettings"/>), the G-force channels (<see cref="GForceSettings"/>
    /// - added here by the wiring task, which owns composing the previously-independent settings
    /// groups into one root object), and the two General-tab toggles (<see cref="GeneralSettings"/>).
    /// </summary>
    public sealed class QAdvanceFeedbackSettings
    {
        public int Version { get; set; } = 1;

        /// <summary>
        /// The shipped starting points per source type - see <see cref="Settings.KeyDataPointDefaults"/>.
        /// <para/>
        /// Declared HERE, second only to <see cref="Version"/>, because Newtonsoft writes properties in
        /// declaration order: this is the block a driver is most likely to want to find and retune, so it
        /// belongs at the top of the file rather than buried under two channels' worth of settings.
        /// </summary>
        public KeyDataPointDefaults KeyDataPointDefaults { get; set; } = Settings.KeyDataPointDefaults.CreateShipped();

        public WheelChannelSettings Lock { get; set; } = WheelChannelSettings.CreateLockDefaults();

        public WheelChannelSettings Slip { get; set; } = WheelChannelSettings.CreateSlipDefaults();

        public GForceSettings GForce { get; set; } = new GForceSettings();

        public GeneralSettings General { get; set; } = new GeneralSettings();

        /// <summary>
        /// The single internally-defined default settings object every "restore defaults" path derives
        /// from, so the shipped defaults (this class's own field initialisers above, which already
        /// call <see cref="WheelChannelSettings.CreateLockDefaults"/>/<c>CreateSlipDefaults</c> and
        /// construct fresh <see cref="GForceSettings"/>/<see cref="GeneralSettings"/>) and the settings
        /// UI's "Restore all default settings" button can never drift apart - both ultimately just
        /// call <c>new QAdvanceFeedbackSettings()</c>, either directly (a fresh plugin install) or
        /// through <see cref="RestoreDefaults"/> (an existing settings object, reset in place).
        /// </summary>
        public static QAdvanceFeedbackSettings CreateDefault() => new QAdvanceFeedbackSettings();

        /// <summary>
        /// Resets EVERY setting on THIS instance - Wheel Lock, Wheel Slip, G-Force, General - back to
        /// <see cref="CreateDefault"/>'s values, in place. Deliberately mutates the existing object
        /// (rather than requiring the caller to swap in a whole new one) because the live plugin and
        /// the settings UI both hold a reference to one specific <see cref="QAdvanceFeedbackSettings"/>
        /// instance (see <c>QAdvanceFeedback.Settings</c>'s own remarks) - replacing the four
        /// sub-objects in place keeps that single shared reference valid and is exactly what the
        /// settings UI's "Restore all default settings" button calls, from any tab, before its own
        /// call to Apply persists the result.
        /// </summary>
        public void RestoreDefaults()
        {
            // KEY DATA POINTS SURVIVE A RESTORE (v1.0.7.2, owner's explicit instruction: "reset to
            // default should NOT clean the saved SMax/S90/S75 for Global, and SMax/S90/S75 for
            // per-game"). They are not a tuning preference that a fresh start should discard - they are
            // an accumulated record of what each source and each game actually read, some of it earned
            // over many sessions and some of it typed by hand. Wiping the per-game table in particular
            // would silently throw away work the driver cannot easily reconstruct.
            //
            // The AutoGenerate/PerGame switches are carried over too: resetting those would flip a
            // driver who deliberately chose manual back to Auto, which is a behaviour change disguised
            // as a preferences reset.
            KeyDataPointSettings lockKeyData = Lock != null ? Lock.KeyDataPoints : null;
            KeyDataPointSettings slipKeyData = Slip != null ? Slip.KeyDataPoints : null;
            // Retuned defaults are the driver's own work too - a preferences reset must not silently
            // revert numbers they measured and typed in themselves.
            KeyDataPointDefaults defaults = KeyDataPointDefaults;

            QAdvanceFeedbackSettings fresh = CreateDefault();
            Version = fresh.Version;
            Lock = fresh.Lock;
            Slip = fresh.Slip;
            GForce = fresh.GForce;
            General = fresh.General;

            if (lockKeyData != null) Lock.KeyDataPoints = lockKeyData;
            if (slipKeyData != null) Slip.KeyDataPoints = slipKeyData;
            if (defaults != null) KeyDataPointDefaults = defaults;
        }
    }
}

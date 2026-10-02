using System;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.RawCalculator;
using QAdvanceFeedback.Settings;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    [PluginDescription("Legacy/Normalized/Projected wheel lock & slip (0-100) plus G-force feedback channels for ShakeIt and dashboards.")]
    [PluginAuthor("Mr.Q")]
    [PluginName("QAdvanceFeedback")]
    // IMPORTANT - read before renaming this class:
    // SimHub.Plugins.PluginManager.GetName(name, pluginType) is hard-coded (confirmed by decompiling
    // SimHub.Plugins.dll) as `pluginType.Name + "." + name` for every AttachDelegate/AddProperty/
    // AddAction overload - so THIS CLASS'S OWN NAME is unavoidably the first segment of every
    // property this plugin publishes. This class is named QAdvanceFeedback (matching the namespace
    // root), NOT QAdvanceFeedbackPlugin, so Type.Name supplies the required "QAdvanceFeedback."
    // prefix exactly once - see docs\layer123-report.md for the full reasoning (this is a deliberate,
    // verified-by-decompilation resolution of the brief's own literal-vs-functional conflict, not a
    // guess).
    //
    // IPluginExtensions.AttachDelegate<T,U> also resolves typeof(T) from the STATIC type of its
    // receiver, not GetType() - PropertyPublisher.Register is generic in the concrete plugin type
    // (TPlugin) for exactly this reason (see its own remarks) - a plain IPlugin parameter there would
    // silently publish everything under "IPlugin.*" regardless of this class's name.
    public sealed class QAdvanceFeedback : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        // Renamed per the owner's request (was plugin.QAdvanceFeedback.*.json) - the OLD names are
        // kept as constants purely so ConfigStore/RuntimeStore can import a still-present old file
        // exactly once (see their own remarks); nothing ever writes to the old names again.
        private const string ConfigFileName = "QAdvanceFeedback.config.json";
        private const string LegacyConfigFileName = "plugin.QAdvanceFeedback.config.json";
        private const string ParametersFileName = "QAdvanceFeedback.Parameters.json";
        private const string LegacyParametersFileName = "plugin.QAdvanceFeedback.runtime.json";

        private string _configPath;
        private string _viperPath;

        /// <summary>The supported-game list for viper4gh's plugin - see
        /// <see cref="Core.Viper.ViperSupportedGames"/>. Loaded once at Init and replaced only by an
        /// explicit Apply from the settings page, never per frame.</summary>
        private Core.Viper.ViperDocument _viperDocument = Core.Viper.ViperSupportedGames.CreateShippedDocument();

        /// <summary>The Viper document the settings page reads and writes. Never null.</summary>
        public Core.Viper.ViperDocument ViperDocument
        {
            get { return _viperDocument; }
            set { if (value != null) _viperDocument = value; }
        }

        /// <summary>Persists <see cref="ViperDocument"/> - called from the settings page's Apply, the
        /// only thing that writes this file.</summary>
        public bool SaveViperDocument()
            => ViperStore.Save(_viperPath, _viperDocument, LogWarning);

        /// <summary>
        /// Whether a channel configured for the Viper source can actually read it right now: the Viper
        /// plugin only computes for the games in <see cref="ViperDocument"/>, and on any other title it
        /// returns before writing, leaving its four properties at the 0 it declared them with. A channel
        /// in that state reads its own Layer-3 Raw instead - see <see cref="RawSourceFallback"/>.
        /// <para/>
        /// NO GAME IS NOT AN UNSUPPORTED GAME, and conflating the two was a serious defect (owner-
        /// reported, 2026-09-30: "Viper support warning will always be displayed, and always use Raw").
        /// <para/>
        /// <see cref="DataUpdate"/> returns early unless <c>GameRunning &amp;&amp; !GamePaused &amp;&amp;
        /// !GameInMenu</c>, so <c>PushLearnedKeyDataPointsToSettingsUi</c> never runs at the SimHub
        /// menu - which is exactly where a driver configures a plugin. The settings page's
        /// <c>_currentGameId</c> therefore stayed EMPTY, <c>IsSupported("")</c> is false, and the whole
        /// Viper channel presented as permanently unsupported: amber warning, source rows disabled
        /// showing Raw, and - the damaging part - the page re-keyed its key data points to RAW's
        /// identity. Every number typed while configuring was filed under Raw. Start the game, Viper
        /// becomes supported, the identity flips to Viper, and its slot is empty: "everytime restarted
        /// will always go back to default".
        /// <para/>
        /// With no game there is nothing to block: no telemetry is flowing, so forcing Raw buys nothing,
        /// while claiming the title is unsupported is a statement this code cannot possibly know to be
        /// true. Blocking resumes the moment a real game id arrives and turns out not to be supported.
        /// </summary>
        public bool ViperSourceBlocked(WheelChannelSettings channel, string gameId)
            => channel != null
               && channel.SourceMode == SourceMode.Viper
               && !string.IsNullOrWhiteSpace(gameId)
               && !Core.Viper.ViperSupportedGames.IsSupported(gameId, _viperDocument.SupportedGames);

        private QAdvanceFeedbackSettings _settings;

        // Layer 2 (SimHub telemetry adapter) and Layer 3 (Raw calculator engine) - see
        // docs\architecture.md for the full layer model.
        private readonly ITelemetryAdapter _adapter = new SimHubTelemetryAdapter();
        // Concrete rather than ILegacyWheelLockSlipEngine because this plugin also owns Layer 3's
        // PERSISTED ShakeIt calibration (RuntimeDocument Version 11). The interface stays a pure
        // "one sample in, one result out" contract for every other implementation and test.
        private readonly RawCalculatorEngine _legacyEngine = new RawCalculatorEngine();

        /// <summary>Everything converted from SimHub's shipped ShakeIt reference data - presets, per-game
        /// wheel-speed-delta bounds, and the source timestamps that make the start-up re-import cheap.
        /// See <see cref="ShakeItPrecalibrationConverter"/>.</summary>
        private ShakeItImportState _shakeIt = new ShakeItImportState();

        /// <summary>The game whose presets are currently installed in the engine, so the per-game preset
        /// set is resolved once per title rather than every frame.</summary>
        private string _precalibrationInstalledForGame;

        /// <summary>Frames remaining before the calibration histogram is handed to the runtime cache.
        /// Throttled because the histogram changes slowly and copying it is far more expensive than the
        /// small dictionaries the other per-frame saves push.</summary>
        private int _calibrationSaveCountdown;

        private const int CalibrationSaveIntervalFrames = 300;
        /// <summary>
        /// Turn one channel's persisted key data points into the plain triple the engine consumes.
        /// <para/>
        /// Resolution order, per the owner's own rules:
        /// <list type="number">
        /// <item>Auto - nothing to apply, the learned values are published.</item>
        /// <item>A configured value for this context (global, or this game under Per-Game).</item>
        /// <item>The shipped default for this SOURCE TYPE - our Raw and a ShakeIt export have known
        /// scales, so each gets its own numbers. A source we do not recognise (a script, an NCalc
        /// expression) deliberately gets NO default: there is no honest guess for a signal whose scale
        /// has never been seen, so such a channel stays on its learned values until the driver
        /// configures something.</item>
        /// </list>
        /// Max-Grip-Only derives the two lower anchors rather than reading them, so the hidden values
        /// can never drift out of order behind the driver's back.
        /// </summary>
        private static ManualAnchors ResolveManualAnchors(
            KeyDataPointSettings keyData, string gameId, string sourceIdentity, bool isLockChannel,
            NormalizePattern pattern, KeyDataPointDefaults defaults)
        {
            if (keyData == null || keyData.GetAutoGenerate(sourceIdentity)) return ManualAnchors.None;

            double sMax, s90, s75;
            if (!keyData.TryGetManual(gameId, sourceIdentity, out sMax, out s90, out s75))
            {
                // The CONFIGURED per-source starting points, so a driver's retuning of the defaults block
                // reaches the output the same way their typed values do.
                if (!KeyDataPointSettings.TryResolveShippedDefaults(
                        sourceIdentity, isLockChannel, defaults, out sMax, out s90, out s75))
                    return ManualAnchors.None;   // unknown source, nothing configured - stay on learned
            }

            if (pattern == NormalizePattern.MaxGripOnly)
                KeyDataPointSettings.DeriveLowerAnchors(sMax, out s90, out s75);

            return KeyDataPointSettings.IsValid(sMax, s90, s75)
                ? ManualAnchors.Of(sMax, s90, s75)
                : ManualAnchors.None;
        }

        /// <summary>
        /// Keep the settings page's "[Learned Value: xx.x]" hints current while it is open.
        /// <para/>
        /// Costs nothing when the page is closed (the weak reference simply fails to resolve), and reads
        /// only already-computed learner state - it never drives learning or output. The values pushed
        /// are the SAME ones the Normalized layer would publish under Auto, which is what makes the hint
        /// meaningful as a reference while the driver is typing a manual value.
        /// </summary>
        /// <summary>How often the settings page's learned readouts are refreshed. Matches the cadence
        /// the G-Force learned readout already uses - a learned ceiling moves far too slowly to be worth
        /// marshalling onto the UI thread every frame, and rewriting the value boxes 60 times a second
        /// would be churn the driver cannot even perceive.</summary>
        private const double KeyDataPushIntervalSeconds = 1.0;

        private double _secondsSinceKeyDataPush;

        /// <summary>The last game id handed to the settings page, so the dispatcher is touched only when
        /// it actually changes. Cleared when a new page is created, so a freshly opened page is told
        /// immediately rather than waiting for a game switch that may never come.</summary>
        private string _lastGameIdPushedToUi;

        /// <summary>
        /// Tell the settings page which game is running, regardless of whether it is being DRIVEN.
        /// <para/>
        /// Called above <see cref="DataUpdate"/>'s learning gate - see the remarks there for why. Sends
        /// nothing but the id: the learned values still ride the 1 Hz push, which is correctly gated,
        /// because a paused game has nothing new to teach.
        /// </summary>
        private void PushCurrentGameToSettingsUi(string gameId)
        {
            if (string.Equals(gameId, _lastGameIdPushedToUi, StringComparison.Ordinal)) return;

            Settings.SettingsControl control;
            if (_settingsControl == null || !_settingsControl.TryGetTarget(out control) || control == null) return;

            _lastGameIdPushedToUi = gameId;
            control.Dispatcher.BeginInvoke((Action)(() => control.UpdateCurrentGame(gameId)));
        }

        private void PushLearnedKeyDataPointsToSettingsUi(
            string gameId, string carId, string lockSourceIdentity, string slipSourceIdentity, double dtSeconds)
        {
            Settings.SettingsControl control;
            if (_settingsControl == null || !_settingsControl.TryGetTarget(out control) || control == null)
            {
                _secondsSinceKeyDataPush = 0.0;   // no page open - start the clock fresh when one appears
                return;
            }

            if (ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0) _secondsSinceKeyDataPush += dtSeconds;
            if (_secondsSinceKeyDataPush < KeyDataPushIntervalSeconds) return;
            _secondsSinceKeyDataPush = 0.0;

            bool ignored;
            double? lockSMax = _normalizedEngine.LockScaleLearner.LearnedCeiling(gameId, carId, lockSourceIdentity, out ignored);
            double? slipSMax = _normalizedEngine.SlipScaleLearner.LearnedCeiling(gameId, carId, slipSourceIdentity, out ignored);

            // Lock's lower anchors are MEASURED (ratios learned per corner); Slip's are DERIVED, because
            // it has nothing to measure them from - see KeyDataPointSettings.DeriveLowerAnchors.
            double? lockS90 = null, lockS75 = null;
            if (lockSMax.HasValue && lockSMax.Value > 0.0)
            {
                lockS90 = lockSMax.Value * _normalizedEngine.LockAnchors.PhysicalS90Ratio(
                    gameId, carId, lockSourceIdentity, KeyDataPointSettings.DerivedS90Fraction);
                lockS75 = lockSMax.Value * _normalizedEngine.LockAnchors.PhysicalS75Ratio(
                    gameId, carId, lockSourceIdentity, KeyDataPointSettings.DerivedS75Fraction);
            }

            double? slipS90 = null, slipS75 = null;
            if (slipSMax.HasValue && slipSMax.Value > 0.0)
            {
                double a90, a75;
                KeyDataPointSettings.DeriveLowerAnchors(slipSMax.Value, out a90, out a75);
                slipS90 = a90; slipS75 = a75;
            }

            control.Dispatcher.BeginInvoke((Action)(() => control.UpdateLearnedKeyDataPoints(
                gameId, lockSourceIdentity, slipSourceIdentity, _keyDataRevision,
                lockSMax, lockS90, lockS75,
                _normalizedEngine.ManualGateReady(gameId, carId, lockSourceIdentity, isLockChannel: true),
                slipSMax, slipS90, slipS75,
                _normalizedEngine.ManualGateReady(gameId, carId, slipSourceIdentity, isLockChannel: false))));
        }

        /// <summary>
        /// Write the learned values into a manual slot the first time that slot is eligible, and persist
        /// immediately - once per slot.
        /// <para/>
        /// A slot is (mode, game, source), so this re-arms exactly where the owner asked: a never-played
        /// game in per-game mode seeds again, and so does a newly selected source. Global mode seeds once
        /// per source and does not re-seed for a new title.
        /// <para/>
        /// Saves with <see cref="ConfigStore.Save"/> directly rather than ApplySettings, which also
        /// rebuilds the projected engine - correct from the settings page, but not something to do in the
        /// middle of a frame. The manual anchors are re-resolved from settings every frame anyway, so the
        /// new values take effect on the next one without any rebuild.
        /// </summary>
        private void AutoPersistSeededKeyDataPoints(
            string gameId, string carId, string lockSourceIdentity, string slipSourceIdentity)
        {
            if (_settings == null) return;
            _lastCarIdForKeyData = carId;
            _lastGameIdForKeyData = gameId;

            bool changed = false;
            changed |= TrySeedKeyDataSlot(_settings.Lock.KeyDataPoints, gameId, lockSourceIdentity,
                _normalizedEngine.ManualGateReady(gameId, carId, lockSourceIdentity, isLockChannel: true),
                _settings.Lock.NormalizePattern, isLockChannel: true);
            changed |= TrySeedKeyDataSlot(_settings.Slip.KeyDataPoints, gameId, slipSourceIdentity,
                _normalizedEngine.ManualGateReady(gameId, carId, slipSourceIdentity, isLockChannel: false),
                _settings.Slip.NormalizePattern, isLockChannel: false);

            if (!changed) return;
            ConfigStore.Save(_configPath, _settings, LogWarning);
            _keyDataRevision++;
        }

        private bool TrySeedKeyDataSlot(KeyDataPointSettings keyData, string gameId, string sourceIdentity,
            bool gateReady, NormalizePattern pattern, bool isLockChannel)
        {
            if (keyData == null || keyData.GetAutoGenerate(sourceIdentity)) return false;
            if (!gateReady) return false;
            if (keyData.GetPerGame(sourceIdentity) && string.IsNullOrEmpty(gameId)) return false;
            if (keyData.IsSeeded(gameId, sourceIdentity)) return false;

            bool ignored;
            KeyedScaleLearner learner = isLockChannel
                ? _normalizedEngine.LockScaleLearner : _normalizedEngine.SlipScaleLearner;
            double? learnedSMax = learner.LearnedCeiling(gameId, _lastCarIdForKeyData, sourceIdentity, out ignored);
            if (!learnedSMax.HasValue || learnedSMax.Value <= 0.0) return false;

            double sMax = Math.Round(learnedSMax.Value, 1);
            double s90, s75;
            if (isLockChannel && pattern == NormalizePattern.Mapping)
            {
                s90 = Math.Round(sMax * _normalizedEngine.LockAnchors.PhysicalS90Ratio(
                    gameId, _lastCarIdForKeyData, sourceIdentity, KeyDataPointSettings.DerivedS90Fraction), 1);
                s75 = Math.Round(sMax * _normalizedEngine.LockAnchors.PhysicalS75Ratio(
                    gameId, _lastCarIdForKeyData, sourceIdentity, KeyDataPointSettings.DerivedS75Fraction), 1);
            }
            else
            {
                KeyDataPointSettings.DeriveLowerAnchors(sMax, out s90, out s75);
                s90 = Math.Round(s90, 1); s75 = Math.Round(s75, 1);
            }

            if (!KeyDataPointSettings.IsValid(sMax, s90, s75)) return false;

            keyData.SetManual(gameId, sourceIdentity, sMax, s90, s75, seeded: true);
            return true;
        }

        /// <summary>The car the last seed attempt ran against - the learners key by it, so it must match
        /// what the frame loop is currently computing with.</summary>
        private string _lastCarIdForKeyData;

        /// <summary>Bumped whenever a slot is seeded, so an open settings page knows to reload its boxes
        /// even though the (mode, game, source) context has not changed.</summary>
        private int _keyDataRevision;

        /// <summary>
        /// The learned ceiling the settings page should display for a channel, WITHOUT needing a game to
        /// be running.
        /// <para/>
        /// Prefers the key currently being driven, which is the precise answer. Falls back to the best
        /// entry the parameters file holds for this same source - so a driver who opens the settings on
        /// the SimHub menu still sees what the plugin learned last time, rather than a shipped default
        /// that its own evidence has long since superseded.
        /// </summary>
        public double? GetLearnedCeilingForDisplay(bool isLockChannel, string sourceIdentity)
        {
            KeyedScaleLearner learner = isLockChannel
                ? _normalizedEngine.LockScaleLearner : _normalizedEngine.SlipScaleLearner;

            if (!string.IsNullOrEmpty(_lastGameIdForKeyData))
            {
                bool ignored;
                double? current = learner.LearnedCeiling(
                    _lastGameIdForKeyData, _lastCarIdForKeyData, sourceIdentity, out ignored);
                if (current.HasValue && current.Value > 0.0) return current;
            }

            return learner.PersistedCeilingForSource(sourceIdentity);
        }

        /// <summary>The game the last frame reported - paired with <see cref="_lastCarIdForKeyData"/>.</summary>
        private string _lastGameIdForKeyData;

        /// <summary>The live settings page, if one is open - see GetWPFSettingsControl for why weak.</summary>
        private WeakReference<Settings.SettingsControl> _settingsControl;

        private readonly NormalizedWheelLockSlipEngine _normalizedEngine = new NormalizedWheelLockSlipEngine();
        private readonly GForceEngine _gforceEngine = new GForceEngine();
        private readonly WheelSourceResolver _sourceResolver = new WheelSourceResolver();
        private readonly PropertyPublisher _publisher = new PropertyPublisher();
        private readonly CsvExportWriter _csv = new CsvExportWriter();

        // Rebuilt whenever settings change (Init, and the settings UI's Apply) - see RebuildProjectedEngine.
        private ProjectedWheelLockSlipEngine _projectedEngine;

        private RuntimeStore _runtimeStore;

        // Tracks the game/car SimHub is currently reporting, so a switch can be detected. Empty, not
        // null, so the very first frame (an empty -> real name transition) also triggers one harmless
        // reset rather than needing a separate "have we seen a game yet" flag.
        private string _lastGameName = string.Empty;
        private string _lastCarId = string.Empty;

        private string _loggedDataUpdateFault;

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle => "QAdvanceFeedback";

        public ImageSource PictureIcon => null;

        /// <summary>Exposed so the settings UI edits/saves the SAME live object this plugin reads
        /// from every frame - matching the sibling project's own convention.</summary>
        public QAdvanceFeedbackSettings Settings => _settings;

        /// <summary>
        /// Set while the settings page's Test Effect panel is driving the eight G-Force channels by
        /// hand. Volatile because it is written from the WPF dispatcher thread and read from SimHub's
        /// own data thread.
        /// </summary>
        private volatile bool _testEffectActive;

        /// <summary>
        /// Publishes one hand-driven G-Force frame to <c>QAdvanceFeedback.GForce.*</c>, so a driver can
        /// set their ShakeIt levels against the real properties with no game running.
        /// <para/>
        /// WHY THIS EXISTS AT ALL: the panel drives a private <see cref="GForceEngine"/> inside the
        /// settings control, which moved the on-screen readout but published nothing - and with no game
        /// running <see cref="DataUpdate"/> returns before publishing anything either, so ShakeIt saw
        /// null on every channel. It writes through the SAME <see cref="PropertyPublisher.UpdateGForce"/>
        /// the live path uses; the publisher's properties are pull-delegates over that backing array, so
        /// a value written here is what SimHub reads next, with or without a running game.
        /// <para/>
        /// Null CLEARS the override and hands the channels back to the live pipeline. Both the toggle
        /// going off and the settings page unloading must call it that way, or a closed page would pin
        /// the outputs forever.
        /// </summary>
        public void PublishTestEffect(GForceOutput output)
        {
            try
            {
                if (_publisher == null) return;

                if (output != null)
                {
                    _testEffectActive = true;
                    _publisher.UpdateGForce(output);
                }
                else
                {
                    // Order matters: stop claiming the channels BEFORE zeroing them, so a live frame
                    // arriving in between overwrites the zeroes rather than being suppressed by them.
                    _testEffectActive = false;
                    _publisher.UpdateGForce(GForceOutput.Empty);
                }
            }
            catch (Exception e)
            {
                _testEffectActive = false;
                SimHub.Logging.Current.Error("QAdvanceFeedback: PublishTestEffect failed - " + e);
            }
        }

        public void Init(PluginManager pluginManager)
        {
            try
            {
                InitCore(pluginManager);
            }
            catch (Exception e)
            {
                // PIPELINE-EXCEPTION-SAFETY (docs\pipeline-exception-safety-report.md): SimHub's own
                // PluginManager iterates every plugin's Init() with NO per-plugin try/catch around the
                // call (confirmed by decompilation) - a throw here would abort the whole load loop and
                // starve every plugin still waiting to be initialised, exactly the owner-reported "a
                // different plugin (AZOM) stops working after a game switch, only a SimHub restart
                // recovers it" symptom shape, even though Init only runs at load/enable time rather
                // than on every switch. _settings/_runtimeStore may be left partially constructed
                // below (ConfigStore.Load/RuntimeStore's own constructor already degrade to safe
                // defaults on their own internal I/O failures and cannot be the cause; this guards
                // against an internal Core error - e.g. a Corner/curve construction faulting on a
                // corrupted config file dictionary) - DataUpdate/End independently null-guard/short-
                // circuit whatever this leaves unset, so the WORST case is "this plugin does nothing
                // useful this session", never "SimHub cannot finish loading".
                SimHub.Logging.Current.Error("QAdvanceFeedback: Init failed - " + e);
                HealthRegistry.Report(HealthSubsystems.Init, HealthSeverity.Failed, "Health.Impact.Init", e.ToString());
            }
        }

        private void InitCore(PluginManager pluginManager)
        {
            _viperPath = pluginManager.GetCommonStoragePath(ViperStore.FileName);
            _viperDocument = ViperStore.Load(_viperPath, LogWarning);

            _configPath = pluginManager.GetCommonStoragePath(ConfigFileName);
            string legacyConfigPath = pluginManager.GetCommonStoragePath(LegacyConfigFileName);
            _settings = ConfigStore.Load(_configPath, LogWarning, legacyPath: legacyConfigPath);

            // REPAIR A CONFIG DAMAGED BY v1.1.0's SHADOW BUG, once, at load - see
            // WheelChannelSettings.RepairPresetSourcesIfStale for the full account. A channel saved as
            // "Viper" over four RAW property names is repaired back to Viper's own scripts here, which
            // fixes the engine on this very frame and the settings page the moment it opens. Saved
            // immediately so the repair is not re-done every start, and logged because silently
            // rewriting a driver's stored configuration is not something to do without a trace.
            bool repairedLock = _settings.Lock?.RepairPresetSourcesIfStale(true) ?? false;
            bool repairedSlip = _settings.Slip?.RepairPresetSourcesIfStale(false) ?? false;
            if (repairedLock || repairedSlip)
            {
                ConfigStore.Save(_configPath, _settings, LogWarning);
                LogWarning("QAdvanceFeedback: repaired stored source configuration for "
                    + (repairedLock && repairedSlip ? "both channels"
                        : repairedLock ? "Wheel Lock" : "Wheel Slip")
                    + " - the saved text did not match the selected source mode.");
            }

            string parametersPath = pluginManager.GetCommonStoragePath(ParametersFileName);
            string legacyParametersPath = pluginManager.GetCommonStoragePath(LegacyParametersFileName);
            _runtimeStore = new RuntimeStore(parametersPath, legacyParametersPath, logInfo: LogInfo, logWarning: LogWarning);

            // Per-(game,car,source) keyed Lock/Slip learned parameters (see KeyedGripLearner,
            // docs\branch-dispatch-and-source-keyed-learning-report.md "Part 2") - load whatever was
            // already persisted for every key, MIGRATING any pre-source-keying (game,car)-only key to
            // the new (game,car,source) shape first (GripLearnerKeyMigration - never discards a stint's
            // worth of learning silently), then seed the pre-per-car legacy global value (if any) as
            // the cold-start default for the first brand-new key each channel encounters.
            _runtimeStore.LoadLockLearners(out var lockLearnerData);
            _normalizedEngine.LockLearners.ImportAll(GripLearnerKeyMigration.MigrateLegacyKeys(lockLearnerData));
            _runtimeStore.LoadSlipLearners(out var slipLearnerData);
            _normalizedEngine.SlipLearners.ImportAll(GripLearnerKeyMigration.MigrateLegacyKeys(slipLearnerData));

            if (_runtimeStore.TryGetLegacyLockSeed(out double legacyLockPeak, out int legacyLockSamples))
                _normalizedEngine.LockLearners.SeedLegacy(legacyLockPeak, legacyLockSamples);
            if (_runtimeStore.TryGetLegacySlipSeed(out double legacySlipPeak, out int legacySlipSamples))
                _normalizedEngine.SlipLearners.SeedLegacy(legacySlipPeak, legacySlipSamples);

            // COLD/WARM PERSISTENCE (telemetry-integrity pass, item 3, RuntimeDocument Version 3) - the
            // Raw-side per-source calibration learner's cold ceilings. ImportAll seeds each key's COLD
            // reference; this session's own hot evidence (dispersion-weighted) then blends with it live -
            // see KeyedScaleLearner.PublishedCeiling/PersistedCeiling's own remarks.
            // ---- LAYER 3 SHAKEIT CALIBRATION (RuntimeDocument Version 11, 1.0.7.1).
            // Resume the calibration histogram exactly as SimHub resumes its own - no reference, no
            // blend, no handover; the learner simply continues accumulating where it stopped.
            _runtimeStore.LoadShakeItCalibration(out var shakeItCalibration);
            _legacyEngine.Calibration.ImportCalibrations(shakeItCalibration);

            _runtimeStore.LoadShakeItPrecalibration(out _shakeIt.Presets);
            _runtimeStore.LoadShakeItGameBounds(out _shakeIt.Bounds);
            _runtimeStore.LoadShakeItSourceTimestamps(out _shakeIt.SourceTimestamps);

            // IMPORT ON EVERY LAUNCH, NEVER OVERRIDING. Running this each start is what keeps the plugin
            // current: when SimHub updates and ships reference data for new games, it is picked up
            // automatically rather than waiting for someone to press a button. It is cheap because
            // onlyChangedFiles compares each source file's last-write time against the one recorded from
            // the previous import - an unchanged installation costs three timestamp reads and opens
            // nothing. overrideExisting is hard-coded false here: an automatic, unattended import must
            // never replace what this plugin has learned. Replacing that is the settings button's job,
            // and only with its checkbox ticked.
            try
            {
                ShakeItConversionOutcome imported = ShakeItPrecalibrationConverter.Convert(
                    ShakeItPrecalibrationConverter.DefaultRootDirectory(), _shakeIt,
                    overrideExisting: false, onlyChangedFiles: true);

                if (imported.FilesRead > 0)
                {
                    _runtimeStore.SaveShakeItPrecalibration(_shakeIt.Presets);
                    _runtimeStore.SaveShakeItGameBounds(_shakeIt.Bounds);
                    _runtimeStore.SaveShakeItSourceTimestamps(_shakeIt.SourceTimestamps);
                }

                LogInfoIfDiagnostics("ShakeIt reference data: " + imported.Message);
            }
            catch (Exception e)
            {
                // A missing or unreadable SimHub install must never stop the plugin loading - Layer 3
                // simply calibrates from scratch, exactly as ShakeIt itself does on a fresh install.
                LogWarning("ShakeIt reference data import skipped: " + e.Message);
            }

            _runtimeStore.LoadLockScaleLearners(out var lockScaleData);
            _normalizedEngine.LockScaleLearner.ImportAll(lockScaleData);
            _runtimeStore.LoadSlipScaleLearners(out var slipScaleData);
            _normalizedEngine.SlipScaleLearner.ImportAll(slipScaleData);

            // COLD-START CONTINUITY AND CROSS-CAR SEEDING (RuntimeDocument Version 4,
            // docs\cold-start-and-timing-fix-report.md) - the shared, (game,car)-only physical-limit
            // detector that now solely gates KeyedScaleLearner's PRIMARY tier, and the per-(game,source)
            // cross-car cold-start seed a brand-new car can start from - both previously session-scoped
            // only (flagged, not fixed, in the f1-normalization-fix-report's own Concerns).
            _runtimeStore.LoadLockPhysicalReference(out var lockPhysicalReferenceData);
            _normalizedEngine.LockPhysicalReference.ImportAll(lockPhysicalReferenceData);
            _runtimeStore.LoadSlipPhysicalReference(out var slipPhysicalReferenceData);
            _normalizedEngine.SlipPhysicalReference.ImportAll(slipPhysicalReferenceData);
            _runtimeStore.LoadLockScaleCrossCarSeed(out var lockScaleCrossCarSeedData);
            _normalizedEngine.LockScaleLearner.ImportCrossCarSeeds(lockScaleCrossCarSeedData);
            _runtimeStore.LoadSlipScaleCrossCarSeed(out var slipScaleCrossCarSeedData);
            _normalizedEngine.SlipScaleLearner.ImportCrossCarSeeds(slipScaleCrossCarSeedData);

            // PER-GAME TELEMETRY SUPPORT DETECTION (item 2, RuntimeDocument Version 3) - a title already
            // proven (in a PREVIOUS session) to support loose-surface reporting is trusted from frame one
            // of this one too, before this session has observed anything itself.
            _runtimeStore.LoadSurfaceSupport(out var surfaceSupportData);
            _normalizedEngine.SurfaceSupport.ImportAll(surfaceSupportData);

            // FEATURE C (docs\v1068-four-range-report.md, RuntimeDocument Version 8) - WheelLock's own
            // learned S75/S90 anchors. WHEELLOCK ONLY - no Slip equivalent.
            _runtimeStore.LoadLockAnchors(out var lockAnchorsData);
            _normalizedEngine.LockAnchors.ImportAll(lockAnchorsData);

            _runtimeStore.LoadGForceLearners(out var accelMaxima, out var decelMaxima, out var latMaxima);
            _settings.GForce.ImportLearnedMaxima(accelMaxima, decelMaxima, latMaxima);

            RebuildProjectedEngine();
            _settings.GForce.ApplyTo(_gforceEngine);

            _publisher.Register(this, _settings.General.EnableDiagnostics);

            // "When [diagnostics is] off... write NO log output" (per the brief) - scoped to this
            // plugin's own informational/diagnostic tracing (Init/End notices and anything similar),
            // NOT to genuine warnings/errors (a corrupt config file, a CSV write failure): silencing
            // those too would make a real operational problem invisible with no way to diagnose it,
            // which would work against the very purpose of the toggle. This is a judgment call on an
            // otherwise-underspecified interaction, flagged rather than silently assumed.
            LogInfoIfDiagnostics("initialised");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            try
            {
                // GamePaused/GameInMenu joined this guard alongside the pre-existing GameRunning check
                // (docs\gforce-direction-fix-report.md, the owner's learning-validity-gate ask): a
                // paused game or a menu screen must not reach ANY of Core, including the cross-frame
                // learners - this is the SimHub-specific half of that gate (see
                // Core.TelemetryLearningGate's own remarks for why "game running/paused/menu" is
                // deliberately NOT re-checked a second time down in Core).
                if (data == null) return;

                // THE GAME'S IDENTITY IS NOT TELEMETRY, so it is read BEFORE the gate (owner-reported,
                // 2026-09-30: "gameInMenu or GamePaused ... is the very typical scenario when the user
                // adjust the parameters").
                //
                // The gate below is a LEARNING gate and must stay exactly as strict - a paused game or
                // a menu screen must not reach Core. But it was also starving the settings page of the
                // one fact it cannot get anywhere else: which game is running. That page needs it for
                // two things that have nothing to do with learning - the Per-Game key data slot, and
                // whether the Viper source is supported on this title. A driver who pauses, alt-tabs to
                // SimHub and edits - the normal way this plugin is configured - was therefore editing a
                // page that believed no game existed: Per-Game edits had no slot to go to, and a
                // perfectly supported title was reported as unsupported.
                //
                // Cheap: a string compare per frame, and the dispatcher is only touched when the answer
                // actually changes.
                PushCurrentGameToSettingsUi(data.GameName ?? string.Empty);

                if (!data.GameRunning || data.GamePaused || data.GameInMenu
                    || data.NewData == null || data.OldData == null)
                {
                    return;
                }

                string gameId = data.GameName ?? string.Empty;
                string carId = data.NewData.CarId ?? string.Empty;
                ResetOnGameSwitch(gameId);

                TelemetrySample sample = _adapter.Read(data);

                // Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md) -
                // diagnostics-only raw per-wheel/car-level telemetry and FeedbackCapabilities capture,
                // added so a future capture can fit SimHub's own three candidate WheelRPS/WheelSpeed
                // Lock branches against real numbers instead of guessing a fourth time (see
                // docs\raw-match-rootcause-report.md §2d). Never read by anything below this line -
                // computed unconditionally (same "always computed, gate only affects who SEES it"
                // philosophy already used for Diag.Direction/Diag.MotionLevel/etc. further down) so it
                // is present in the CSV export even when EnableDiagnostics itself is off.
                RawWheelTelemetrySnapshot rawTelemetry = _adapter.CaptureRawTelemetry(data, pluginManager);
                _publisher.UpdateRawTelemetry(sample.New, rawTelemetry);

                // ---- Layer 3: legacy RPM/speed algorithm - published as-is, unaffected by any
                // user-configured Layer 4 source below. Pedal thresholds are owner-configurable
                // (deliberate deviation from SimHub's own hard-coded Brake>20/Throttle>40 - see
                // LegacyThresholds' own remarks) and apply in BOTH Manual and ShakeIt source modes,
                // since they gate Layer 3 itself, upstream of which Layer 4 source is selected.
                var thresholds = new LegacyThresholds
                {
                    LockBrakeThresholdPercent = _settings.Lock.BrakeThresholdPercent,
                    SlipBrakeThresholdPercent = _settings.Slip.BrakeThresholdPercent,
                    SlipThrottleThresholdPercent = _settings.Slip.ThrottleThresholdPercent,
                    LockSensibility = _settings.Lock.LockSensibility
                };
                // Aggregation weights (docs\aggregation-report.md) - read fresh from settings every
                // frame, exactly like thresholds above, so an Apply takes effect on the very next frame
                // with no engine rebuild (the owner's explicit "tune without a rebuild" requirement).
                AggregationWeights lockAggregation = _settings.Lock.ToAggregationWeights();
                AggregationWeights slipAggregation = _settings.Slip.ToAggregationWeights();

                // rawTelemetry (captured just above) drives the branch dispatch this frame - see
                // WheelSlipBranchSelector/RawCalculatorEngine's own remarks.
                // LAYER 3 SHAKEIT CALIBRATION: name the (track, car) these frames belong to. SimHub
                // keys its calibrations as track;car;metric, so using the same two fields is what makes
                // an imported ShakeIt calibration line up with ours entry for entry.
                _legacyEngine.SetContext(data.NewData.TrackIdWithConfig, data.NewData.CarModel);

                if (!string.Equals(_precalibrationInstalledForGame, gameId, StringComparison.Ordinal))
                {
                    _precalibrationInstalledForGame = gameId;
                    // Presets are per GAME in SimHub, so they are swapped on a title change and never
                    // allowed to leak from one title into another.
                    _legacyEngine.Calibration.SetPrecalibration(
                        ShakeItPrecalibrationConverter.ResolveForGame(_shakeIt.Presets, gameId));
                    _legacyEngine.Calibration.SetGameBounds(
                        ShakeItPrecalibrationConverter.ResolveBoundsForGame(_shakeIt.Bounds, gameId));
                }

                LegacyWheelLockSlipResult legacy = _legacyEngine.Compute(sample, thresholds, lockAggregation, slipAggregation, rawTelemetry);
                _publisher.UpdateRaw(legacy);

                // ---- Layer 4 input selection: each of the four wheels, per channel, resolves either
                // the shipped default (a plain reference back to Layer 3's own Raw property) or
                // whatever the driver configured instead - see WheelSourceResolver's remarks. Falls
                // back to Layer 3's own value for that wheel on any resolution failure.
                // VIPER ON AN UNSUPPORTED TITLE READS RAW INSTEAD (v1.1.0). Decided per CHANNEL rather
                // than per wheel, because this is not a wheel going quiet - viper4gh's plugin returns
                // before computing anything on a game it does not handle, so all four of its properties
                // sit at the 0 it declared them with for the entire session. Evaluated every frame so a
                // game switch takes effect immediately, but it is two list lookups, not per-wheel work.
                bool lockForceRaw = ViperSourceBlocked(_settings.Lock, gameId);
                bool slipForceRaw = ViperSourceBlocked(_settings.Slip, gameId);

                Corners lockSources = new Corners(
                    ResolveWheel(pluginManager, _settings.Lock, lockForceRaw, true, 0, legacy.LockWheels.FrontLeft),
                    ResolveWheel(pluginManager, _settings.Lock, lockForceRaw, true, 1, legacy.LockWheels.FrontRight),
                    ResolveWheel(pluginManager, _settings.Lock, lockForceRaw, true, 2, legacy.LockWheels.RearLeft),
                    ResolveWheel(pluginManager, _settings.Lock, lockForceRaw, true, 3, legacy.LockWheels.RearRight));

                Corners slipSources = new Corners(
                    ResolveWheel(pluginManager, _settings.Slip, slipForceRaw, false, 0, legacy.SlipWheels.FrontLeft),
                    ResolveWheel(pluginManager, _settings.Slip, slipForceRaw, false, 1, legacy.SlipWheels.FrontRight),
                    ResolveWheel(pluginManager, _settings.Slip, slipForceRaw, false, 2, legacy.SlipWheels.RearLeft),
                    ResolveWheel(pluginManager, _settings.Slip, slipForceRaw, false, 3, legacy.SlipWheels.RearRight));

                // Diag.Source.* (docs\raw-gap-and-pad-balance-report.md): publish exactly what Layer 4
                // is about to consume, BEFORE calling it - so a future "does our Raw match the
                // configured source" investigation can read this straight off the CSV/SimHub property
                // instead of inverting the Normalized transform to recover it (as this task's own
                // report had to, for lack of this diagnostic).
                _publisher.UpdateSource(lockSources, slipSources, lockAggregation, slipAggregation);

                // SOURCE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md,
                // "Part 2"): derive each channel's own current source identity from its four per-wheel
                // Source/ScriptType fields (SourceIdentity - a plain property name kept verbatim, an
                // expression hashed) so KeyedGripLearner isolates a genuinely different signal's own
                // learning session rather than silently reusing whatever was learned for a previous,
                // differently-scaled source under the same (game,car).
                // THE IDENTITY MOVES WITH THE VALUES. A channel that fell back above is reading Raw, so
                // it must LEARN as Raw too - otherwise its evidence would pile up under the Viper key
                // while the numbers came from somewhere else, and returning to a supported game would
                // inherit a calibration built from the wrong signal.
                string lockSourceIdentity = lockForceRaw
                    ? RawSourceFallback.RawIdentity(true)
                    : SourceIdentity.Compute(
                        _settings.Lock.SourceFrontLeft, _settings.Lock.ScriptTypeFrontLeft.ToString(),
                        _settings.Lock.SourceFrontRight, _settings.Lock.ScriptTypeFrontRight.ToString(),
                        _settings.Lock.SourceRearLeft, _settings.Lock.ScriptTypeRearLeft.ToString(),
                        _settings.Lock.SourceRearRight, _settings.Lock.ScriptTypeRearRight.ToString());
                string slipSourceIdentity = slipForceRaw
                    ? RawSourceFallback.RawIdentity(false)
                    : SourceIdentity.Compute(
                        _settings.Slip.SourceFrontLeft, _settings.Slip.ScriptTypeFrontLeft.ToString(),
                        _settings.Slip.SourceFrontRight, _settings.Slip.ScriptTypeFrontRight.ToString(),
                        _settings.Slip.SourceRearLeft, _settings.Slip.ScriptTypeRearLeft.ToString(),
                        _settings.Slip.SourceRearRight, _settings.Slip.ScriptTypeRearRight.ToString());

                // A CUSTOM SOURCE'S COLD-START REFERENCE (v1.1.0). KnownSourceColdStartReference works
                // by identity and cannot classify a source the driver wrote, so it has no number to
                // offer for one - the driver configures it instead, seeded from whichever preset they
                // edited their way in from. This is how that value reaches the LIVE calibration rather
                // than only the settings page.
                //
                // Pushed every frame, like the thresholds below, so an Apply takes effect on the next
                // frame with no engine rebuild. Guarded by the identity inside the learner, so a source
                // switch - or a channel falling back to Raw just above - stops using it automatically.
                ApplyCustomColdStart(_normalizedEngine.LockScaleLearner, _settings.Lock, lockForceRaw,
                    lockSourceIdentity, _settings.KeyDataPointDefaults?.LockCustom);
                ApplyCustomColdStart(_normalizedEngine.SlipScaleLearner, _settings.Slip, slipForceRaw,
                    slipSourceIdentity, _settings.KeyDataPointDefaults?.SlipCustom);

                // TRIGGER THRESHOLD (owner-requested restructure - docs\lock-and-animation-report.md):
                // the SAME thresholds gate Layer 3's Raw (above) AND Layer 4's Normalized (here) - the
                // whole channel, not just the algorithm's own per-wheel term. The SAME aggregation
                // weights apply too (docs\aggregation-report.md) - Layer 4 aggregates its OWN per-wheel
                // output with the same formula/weights Layer 3 used, "inheriting" the scheme.
                // SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - legacy.LockWheels/
                // SlipWheels is Layer 3's OWN Raw, ALWAYS computed above regardless of which source is
                // actually configured (see UpdateRaw just above) - passed through as the independent
                // "is the wheel genuinely near its limit" measurement the Normalized engine can fall back
                // to if the configured source (lockSources/slipSources) goes quiet. See
                // NormalizedWheelLockSlipEngine's own remarks for the full mechanism.
                // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - read fresh from settings
                // every frame, exactly like thresholds/aggregation above, so switching the Normalize
                // Pattern dropdown takes effect on the very next frame with no engine rebuild. Lock-only:
                // Slip has no selector and the engine's own Slip call site never reads this flag.
                _normalizedEngine.LockNormalizePattern = _settings.Lock.NormalizePattern;
                _normalizedEngine.SlipNormalizePattern = _settings.Slip.NormalizePattern;

                // KEY DATA POINTS (v1.0.7.2) - resolved fresh every frame, exactly like the pattern above,
                // so a settings change lands on the next frame. The Settings layer owns the per-game
                // selection, the source-type defaults and the validation; the engine is handed a plain
                // resolved triple and decides only WHEN to apply it (ManualOverrideGate). Learning itself
                // is untouched by any of this and keeps running whatever the driver has configured.
                _normalizedEngine.LockManualAnchors = ResolveManualAnchors(
                    _settings.Lock.KeyDataPoints, gameId, lockSourceIdentity, isLockChannel: true,
                    _settings.Lock.NormalizePattern, _settings.KeyDataPointDefaults);
                _normalizedEngine.SlipManualAnchors = ResolveManualAnchors(
                    _settings.Slip.KeyDataPoints, gameId, slipSourceIdentity, isLockChannel: false,
                    _settings.Slip.NormalizePattern, _settings.KeyDataPointDefaults);

                // THE ONE-TIME SEED RUNS HERE, in the frame loop - NOT in the settings page. It must not
                // depend on whether the driver happens to have the page open, or "play a new game and the
                // value updates once" would silently not happen for anyone who never opens it.
                AutoPersistSeededKeyDataPoints(gameId, carId, lockSourceIdentity, slipSourceIdentity);
                PushLearnedKeyDataPointsToSettingsUi(gameId, carId, lockSourceIdentity, slipSourceIdentity,
                    sample.Dt.HasValue ? sample.Dt.Value.TotalSeconds : 0.0);

                NormalizedWheelLockSlipResult normalized = _normalizedEngine.Compute(
                    sample, lockSources, slipSources, gameId, carId, thresholds, lockAggregation, slipAggregation,
                    lockSourceIdentity, slipSourceIdentity, legacy.LockWheels, legacy.SlipWheels);
                _publisher.UpdateNormalized(normalized);
                _publisher.UpdateSourceScaleCalibration(
                    _normalizedEngine.LockScaleCeiling, _normalizedEngine.LockScaleCeilingIsPrimaryTier,
                    _normalizedEngine.SlipScaleCeiling, _normalizedEngine.SlipScaleCeilingIsPrimaryTier);
                _publisher.UpdateSourceFallback(
                    _normalizedEngine.LockSourceFallbackActive, _normalizedEngine.SlipSourceFallbackActive);
                _publisher.UpdateSurfaceLearning(
                    _normalizedEngine.SurfaceEverReportedLoose, _normalizedEngine.LockLooseFraction, _normalizedEngine.SlipLooseFraction);
                // TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md).
                _publisher.UpdateColdStartTier(
                    _normalizedEngine.LockColdStartTier.ToString(), _normalizedEngine.SlipColdStartTier.ToString());

                double dtSeconds = sample.Dt.HasValue && sample.Dt.Value.TotalSeconds > 0 ? sample.Dt.Value.TotalSeconds : 0.0;
                // TIERED COLD-START REFERENCE SYSTEM (v1.0.7) - each channel's own resolved tier decides
                // Layer 5's Stage-1 floor (0.5/0.6/0.7/0.8 for Tier 1/2/3/4 - see ColdStartTierFloors),
                // replacing the fixed 0.5 every pre-1.0.7 build used unconditionally.
                ProjectedWheelLockSlipResult projected = _projectedEngine.Compute(normalized, dtSeconds,
                    _normalizedEngine.LockColdStartConfidence, _normalizedEngine.SlipColdStartConfidence,
                    _normalizedEngine.LockColdStartFloor, _normalizedEngine.SlipColdStartFloor);
                _publisher.UpdateProjected(projected);

                // ---- G-force channels.
                _settings.GForce.SetCurrentGameAndCar(gameId, carId);

                double accelMaxG = _settings.GForce.EffectiveAccelMaxG(gameId, carId, sample.FrameTime);
                double decelMaxG = _settings.GForce.EffectiveDecelMaxG(gameId, carId, sample.FrameTime);
                double latMaxG = _settings.GForce.EffectiveLatMaxG(gameId, carId, sample.FrameTime);
                // MODE-DEPENDENT TRANSITION SCALING (docs\robust-auto-gforce-report.md) - blended by the
                // SAME continuous ramp weight EffectiveAccelMaxG/EffectiveDecelMaxG themselves just used,
                // so neither the max nor the scale ever steps relative to the other.
                double accelTransitionScale = _settings.GForce.EffectiveAccelTransitionScale(gameId, carId, sample.FrameTime);
                double decelTransitionScale = _settings.GForce.EffectiveDecelTransitionScale(gameId, carId, sample.FrameTime);
                // The owner-requested "Integrate Wheel Lock and Slip" shake reads Layer 5's curve-shaped
                // value WITHOUT the pulse stage (docs\raw-gap-and-pad-balance-report.md, the
                // pulse-into-shake fix) - NOT the pulsed WheelLock.Projected.All/WheelSlip.Projected.All
                // a dashboard displays. The pulse's own 100->min->100 waveform is a presentation effect
                // for the shaker motor; feeding it into the shake's own amplitude produced one
                // oscillation modulating another (an unstable "wiggle" with no relation to G) whenever
                // a channel was both saturated and pulsing. LockAllWithoutPulse/SlipAllWithoutPulse are
                // the SAME curve projection with that one stage skipped - identical to the pulsed value
                // whenever the pulse is not actually engaged, differing only while a pulse cycle runs -
                // see ProjectedWheelLockSlipResult's own remarks.
                GForceOutput gforce = _gforceEngine.Compute(
                    sample, accelMaxG, decelMaxG, projected.LockAllWithoutPulse, projected.SlipAllWithoutPulse,
                    accelTransitionScale, decelTransitionScale, latMaxG);

                // THE TEST EFFECT PANEL WINS while it is running (see PublishTestEffect). Everything
                // else this frame still happens - the learners must not be starved just because the
                // settings page is open - only the eight published channels are handed over, so the two
                // cannot fight over them frame by frame.
                if (!_testEffectActive) _publisher.UpdateGForce(gforce);

                // DIRECTION FIX (docs\gforce-direction-fix-report.md): feed the AUTO-mode learners the
                // SAME direction-correct attribution _gforceEngine.Compute (just above) used
                // (GForceEngine.CurrentDirection - resolved from differentiated ground speed), never
                // LongitudinalG's own unverified sign - the OLD code fed ObserveAccelG/ObserveDecelG
                // straight off Math.Max(0, +-longG), which had the exact same braking/accelerating
                // swap bug the engine itself had (confirmed inverted on Forza Horizon 6). Also gated by
                // the owner-requested learning validity gate (pit/replay/session-restart/dt/speed/
                // teleport - see GForceSettings.IsFrameValidForLearning's own remarks) so a menu,
                // loading screen, or discontinuity cannot corrupt the learned maxima.
                // NOTE: this necessarily reads AFTER Compute above, so this frame's OWN observation
                // affects the NEXT frame's EffectiveAccelMaxG/EffectiveDecelMaxG rather than this one's
                // (a harmless, one-frame lag for a value GForceMaxLearner only confirms after two
                // consecutive similar readings anyway) - calling the stateful direction resolver a
                // second time here, ahead of Compute, would incorrectly advance it twice for one frame.
                if (_settings.GForce.IsFrameValidForLearning(sample))
                {
                    double? longG = sample.New?.LongitudinalG;
                    if (longG.HasValue && ClampMath.IsFinite(longG.Value))
                    {
                        double magnitude = Math.Abs(longG.Value);
                        LongitudinalMotionState gforceDirection = _gforceEngine.CurrentDirection;
                        if (gforceDirection == LongitudinalMotionState.SpeedingUp)
                            _settings.GForce.ObserveAccelG(gameId, carId, magnitude, sample.FrameTime);
                        else if (gforceDirection == LongitudinalMotionState.Slowing)
                            _settings.GForce.ObserveDecelG(gameId, carId, magnitude, sample.FrameTime);
                    }

                    // LATERAL (v1.0.8) - fed its own learner, gated by the same learning-validity check.
                    // UNLIKE the longitudinal pair there is no direction to resolve: cornering left and
                    // right are the same grip question, so the absolute magnitude is what is observed.
                    double? latG = sample.New?.LateralG;
                    if (latG.HasValue && ClampMath.IsFinite(latG.Value))
                        _settings.GForce.ObserveLatG(gameId, carId, Math.Abs(latG.Value), sample.FrameTime);
                }

                // ---- Runtime persistence: in-memory cache only every frame (see RuntimeStore's own
                // remarks) - the background timer/Flush is what actually reaches disk.
                // LAYER 3 SHAKEIT CALIBRATION - throttled, unlike the small dictionaries below.
                if (--_calibrationSaveCountdown <= 0)
                {
                    _calibrationSaveCountdown = CalibrationSaveIntervalFrames;
                    _runtimeStore.SaveShakeItCalibration(_legacyEngine.Calibration.ExportCalibrations());
                }

                _runtimeStore.SaveLockLearners(_normalizedEngine.LockLearners.ExportAll());
                _runtimeStore.SaveSlipLearners(_normalizedEngine.SlipLearners.ExportAll());
                // COLD/WARM (item 3): ExportAll itself protects an already-persisted cold ceiling from a
                // noisy session - see KeyedScaleLearner.PersistedCeiling's own remarks.
                _runtimeStore.SaveLockScaleLearners(_normalizedEngine.LockScaleLearner.ExportAll());
                _runtimeStore.SaveSlipScaleLearners(_normalizedEngine.SlipScaleLearner.ExportAll());
                // Per-game telemetry support (item 2).
                _runtimeStore.SaveSurfaceSupport(_normalizedEngine.SurfaceSupport.ExportAll());
                // COLD-START CONTINUITY AND CROSS-CAR SEEDING (RuntimeDocument Version 4) - see Init's
                // own remarks.
                _runtimeStore.SaveLockPhysicalReference(_normalizedEngine.LockPhysicalReference.ExportAll());
                _runtimeStore.SaveSlipPhysicalReference(_normalizedEngine.SlipPhysicalReference.ExportAll());
                _runtimeStore.SaveLockScaleCrossCarSeed(_normalizedEngine.LockScaleLearner.ExportCrossCarSeeds());
                _runtimeStore.SaveSlipScaleCrossCarSeed(_normalizedEngine.SlipScaleLearner.ExportCrossCarSeeds());
                // FEATURE C (RuntimeDocument Version 8) - WheelLock's own learned S75/S90 anchors.
                _runtimeStore.SaveLockAnchors(_normalizedEngine.LockAnchors.ExportAll());
                _settings.GForce.ExportLearnedMaxima(out var accelSnapshot, out var decelSnapshot, out var latSnapshot);
                _runtimeStore.SaveGForceLearners(accelSnapshot, decelSnapshot, latSnapshot);

                // ---- Diagnostics (always computed; SimHub only sees them if EnableDiagnostics was on
                // at Init - see PropertyPublisher.Register).
                AchievedMotion.Result motion = AchievedMotion.Resolve(sample);
                // Diag.Lock/Slip.LearnedPeakG: the actual COLD/WARM BLENDED reference (item 3) this
                // frame's Ratio() call divides by - see GripLearner.PublishedPeakG's own remarks.
                _publisher.UpdateIdentity(gameId, carId);
                // BUG FIX (docs\pipeline-exception-safety-report.md, Part B - "is accumulation
                // stuck?"): this used to call PublishedPeakG/Confidence with NO surface-bucket
                // argument, defaulting to the empty-string bucket, while ComputeChannel only ever
                // Observe()s under the REAL "Sealed"/"Loose" bucket - two different KeyedGripLearner
                // dictionary keys, so this readout could never find what real accumulation was
                // actually writing to and permanently showed the seed (peak 1.0, confidence 0)
                // regardless of genuine learning - see NormalizedWheelLockSlipEngine.
                // LockCurrentSurfaceBucket's own remarks for the full diagnosis. Passing the SAME
                // bucket ComputeChannel itself just observed under fixes the read.
                _publisher.UpdateDiagnostics(
                    _normalizedEngine.CurrentDirection, motion.Level, motion.MagnitudeG,
                    _normalizedEngine.LockLearners.PublishedPeakG(gameId, carId, lockSourceIdentity, _normalizedEngine.LockCurrentSurfaceBucket), _normalizedEngine.LockLearners.Confidence(gameId, carId, lockSourceIdentity, _normalizedEngine.LockCurrentSurfaceBucket),
                    _normalizedEngine.SlipLearners.PublishedPeakG(gameId, carId, slipSourceIdentity, _normalizedEngine.SlipCurrentSurfaceBucket), _normalizedEngine.SlipLearners.Confidence(gameId, carId, slipSourceIdentity, _normalizedEngine.SlipCurrentSurfaceBucket),
                    _settings.GForce.CurrentLearnedAccelMaxG, _settings.GForce.CurrentLearnedDecelMaxG);

                UpdateCsvExport(pluginManager);
            }
            catch (Exception e)
            {
                // Logged once per distinct fault rather than every frame - a persistent problem would
                // otherwise write 60-100 lines a second to the SimHub log for as long as it lasts. This
                // is a genuine operational error, not diagnostic tracing, so it is NOT gated behind
                // EnableDiagnostics (see Init's own remarks on that distinction).
                string message = "QAdvanceFeedback: DataUpdate failed - " + e;
                if (!string.Equals(_loggedDataUpdateFault, message, StringComparison.Ordinal))
                {
                    _loggedDataUpdateFault = message;
                    SimHub.Logging.Current.Error(message);
                }
                HealthRegistry.Report(HealthSubsystems.TelemetryAdapter, HealthSeverity.Degraded,
                    "Health.Impact.TelemetryAdapter", e.ToString());
            }
        }

        /// <summary>SimHub keeps running across a game switch; the adapter's Dt bookkeeping and the
        /// Normalized engine's learned direction filter must not carry stale state across the gap - see
        /// SimHubTelemetryAdapter.Reset/NormalizedWheelLockSlipEngine.ResetDirection's own remarks.
        /// <para/>
        /// GForceSettings.ResetLearning() is hooked here too, at GAME-change granularity only (NOT on
        /// every same-game car change): GForceMaxLearner already keys its learned maxima per
        /// (game, car) - see that class's remarks - so a car swap within the same game naturally
        /// starts a fresh, isolated key on its own; calling the wholesale ResetLearning() on every car
        /// change would needlessly discard every OTHER car's already-learned maximum too, defeating
        /// the entire point of that per-car keying. This is a judgment call on the brief's literal
        /// "session/vehicle-change reset" wording, flagged rather than assumed - the safer, less
        /// destructive reading is applied.</summary>
        /// <summary>
        /// One wheel's Layer-4 reading: the configured source normally, or this channel's own Raw
        /// property when <paramref name="forceRaw"/> - see <see cref="RawSourceFallback"/>.
        /// <para/>
        /// <paramref name="wheelIndex"/> is the canonical corner order (0 FL, 1 FR, 2 RL, 3 RR).
        /// </summary>
        private double ResolveWheel(PluginManager pluginManager, WheelChannelSettings channel,
            bool forceRaw, bool isLockChannel, int wheelIndex, double layer3Raw)
        {
            if (forceRaw)
                return _sourceResolver.Resolve(pluginManager,
                    RawSourceFallback.PropertyName(isLockChannel, wheelIndex),
                    RawSourceFallback.RawScriptType, layer3Raw);

            switch (wheelIndex)
            {
                case 0: return _sourceResolver.Resolve(pluginManager, channel.SourceFrontLeft, channel.ScriptTypeFrontLeft, layer3Raw);
                case 1: return _sourceResolver.Resolve(pluginManager, channel.SourceFrontRight, channel.ScriptTypeFrontRight, layer3Raw);
                case 2: return _sourceResolver.Resolve(pluginManager, channel.SourceRearLeft, channel.ScriptTypeRearLeft, layer3Raw);
                default: return _sourceResolver.Resolve(pluginManager, channel.SourceRearRight, channel.ScriptTypeRearRight, layer3Raw);
            }
        }

        /// <summary>
        /// Point one channel's scale learner at the driver-configured cold-start SMax, when that is
        /// what applies - see <c>KeyedScaleLearner.ConfiguredColdStartIdentity</c>.
        /// <para/>
        /// Only a channel actually ON the Custom source, and not currently fallen back to Raw, gets an
        /// override; every other case is cleared, so a learner can never keep a number belonging to a
        /// source it is no longer reading.
        /// </summary>
        private static void ApplyCustomColdStart(Core.Normalized.KeyedScaleLearner learner,
            WheelChannelSettings channel, bool forceRaw, string sourceIdentity, KeyDataPointDefaultSet custom)
        {
            if (learner == null) return;

            bool applies = channel != null
                           && !forceRaw
                           && channel.SourceMode == SourceMode.Custom
                           && custom != null
                           && custom.IsUsable();

            learner.ConfiguredColdStartIdentity = applies ? sourceIdentity : null;
            learner.ConfiguredColdStartSMax = applies ? custom.SMax : 0.0;
        }

        private void ResetOnGameSwitch(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            if (string.Equals(gameId, _lastGameName, StringComparison.Ordinal)) return;

            _adapter.Reset();
            _normalizedEngine.ResetDirection();
            _settings.GForce.ResetLearning();
            _gforceEngine.Reset();
            _lastGameName = gameId;
        }

        private void UpdateCsvExport(PluginManager pluginManager)
        {
            if (_settings.General.ExportCsv)
            {
                if (!_csv.IsRecording)
                {
                    string fileName = "QAdvanceFeedback.session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                    string path = pluginManager.GetCommonStoragePath(fileName);
                    var header = new System.Collections.Generic.List<string>(AllPublishedProperties.ProductNames());
                    header.AddRange(AllPublishedProperties.DiagnosticNames());
                    _csv.Start(path, header, LogWarning);
                }
                _csv.WriteRow(_publisher.SnapshotAllValuesForCsv(), LogWarning);
            }
            else if (_csv.IsRecording)
            {
                _csv.Stop();
            }
        }

        /// <summary>
        /// Imports SimHub's shipped ShakeIt precalibration on demand - the settings tab's "Convert
        /// SimHub ShakeIt Reference Data" button. Persists the result and re-installs the presets for
        /// the running title immediately, so the effect is audible without a restart.
        /// </summary>
        /// <param name="overrideExisting">The tab's "Override current data if exists?" checkbox. False
        /// keeps whatever this plugin already holds for a game; true replaces it with SimHub's.</param>
        public ShakeItConversionOutcome ImportShakeItPrecalibration(bool overrideExisting)
        {
            try
            {
                // onlyChangedFiles FALSE: an explicit button press re-reads every file regardless of
                // timestamps, so a driver who wants to force a refresh (or who has just ticked the
                // override box) always gets one.
                ShakeItConversionOutcome outcome = ShakeItPrecalibrationConverter.Convert(
                    ShakeItPrecalibrationConverter.DefaultRootDirectory(), _shakeIt,
                    overrideExisting, onlyChangedFiles: false);

                _runtimeStore?.SaveShakeItPrecalibration(_shakeIt.Presets);
                _runtimeStore?.SaveShakeItGameBounds(_shakeIt.Bounds);
                _runtimeStore?.SaveShakeItSourceTimestamps(_shakeIt.SourceTimestamps);

                // Force the next frame to re-resolve this title's presets rather than waiting for a
                // game change that may never come while the driver is sitting in the settings tab.
                _precalibrationInstalledForGame = null;

                LogInfoIfDiagnostics("ShakeIt precalibration import: " + outcome.Message);
                return outcome;
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: ShakeIt precalibration import failed - " + e);
                return new ShakeItConversionOutcome { SourceFound = false, Message = "Import failed: " + e.Message };
            }
        }

        public void End(PluginManager pluginManager)
        {
            try
            {
                _csv.Stop();

                // Final, synchronous flush - guaranteed to write the last few seconds of learning
                // before the process is allowed to exit (see RuntimeStore.Flush's own remarks).
                // The calibration save is throttled during play, so force a final one here.
                _runtimeStore?.SaveShakeItCalibration(_legacyEngine.Calibration.ExportCalibrations());

                _runtimeStore?.Flush();
                _runtimeStore?.Dispose();

                ConfigStore.Save(_configPath, _settings, LogWarning);
                LogInfoIfDiagnostics("shut down");
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: shutdown failed - " + e);
                HealthRegistry.Report(HealthSubsystems.Shutdown, HealthSeverity.Degraded, "Health.Impact.Shutdown", e.ToString());
            }
        }

        /// <summary>
        /// PIPELINE-EXCEPTION-SAFETY: SimHub calls this whenever its own settings host builds/refreshes
        /// the list of per-plugin tabs, alongside every OTHER enabled plugin's own
        /// <c>GetWPFSettingsControl</c> - the exact same "one plugin's exception must not degrade every
        /// other plugin sharing SimHub's iteration" concern <c>Init</c>/<c>DataUpdate</c>/<c>End</c>
        /// already guard against (see this class's own remarks and the pipeline-exception-safety
        /// report). <see cref="Settings.SettingsControl"/>'s constructor does real work (reads
        /// <see cref="_settings"/>, probes ShakeIt Motors export availability via reflection, wires ~40
        /// WPF controls) - a corrupt/partial <see cref="_settings"/> object (e.g. Init above having
        /// failed) or an unexpected reflection surface could throw there. Falls back to a minimal,
        /// clearly-labelled placeholder control rather than letting that throw escape into SimHub's own
        /// settings-panel construction.
        /// </summary>
        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            try
            {
                var control = new Settings.SettingsControl(this, pluginManager);
                // A NEW PAGE KNOWS NOTHING, so re-arm the game push for it - otherwise a page opened
                // mid-session would wait for a game CHANGE to be told which game is running, and while
                // paused in one title that change never comes.
                _lastGameIdPushedToUi = null;
                // WEAK, deliberately: SimHub owns this control's lifetime and may discard it whenever the
                // settings page closes. A strong field here would keep a dead WPF tree alive for the rest
                // of the session, and would be a leak every time the page is reopened.
                _settingsControl = new WeakReference<Settings.SettingsControl>(control);
                return control;
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: settings control failed to load - " + e);
                HealthRegistry.Report(HealthSubsystems.SettingsUi, HealthSeverity.Failed, "Health.Impact.SettingsUi", e.ToString());
                return new Label
                {
                    Content = "QAdvanceFeedback: settings failed to load - see SimHub log for details.",
                    Margin = new System.Windows.Thickness(12)
                };
            }
        }

        /// <summary>
        /// Applies edited settings: rebuilds the Layer 5 (curve+pulse) engine from the current
        /// Lock/Slip Projector/Pulse settings, re-applies the G-force engine's tunables, and persists
        /// to disk. Called from the settings UI's single global Apply button.
        /// </summary>
        public void ApplySettings()
        {
            RebuildProjectedEngine();
            _settings.GForce.ApplyTo(_gforceEngine);
            ConfigStore.Save(_configPath, _settings, LogWarning);
        }

        private void RebuildProjectedEngine()
        {
            _projectedEngine = new ProjectedWheelLockSlipEngine(
                new OutputProjector(_settings.Lock.Projector), _settings.Lock.Pulse,
                new OutputProjector(_settings.Slip.Projector), _settings.Slip.Pulse);
        }

        private void LogInfoIfDiagnostics(string message)
        {
            if (!_settings.General.EnableDiagnostics) return;
            SimHub.Logging.Current.Info("QAdvanceFeedback: " + message);
        }

        // ConfigStore/RuntimeStore take logging as plain Action<string> delegates rather than a
        // SimHub reference directly (see their own remarks) - these adapt SimHub's actual logger to
        // that shape, once, here at the composition root. Warnings/errors are never gated behind
        // EnableDiagnostics - see Init's own remarks on why.
        private static void LogInfo(string message) => SimHub.Logging.Current.Info(message);
        private static void LogWarning(string message) => SimHub.Logging.Current.Warn(message);
    }
}

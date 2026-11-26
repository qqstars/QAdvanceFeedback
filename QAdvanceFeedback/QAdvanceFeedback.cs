using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.RawCalculator;
using QAdvanceFeedback.Core.SlipAngle;
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
    public sealed partial class QAdvanceFeedback : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        // Renamed per the owner's request (was plugin.QAdvanceFeedback.*.json) - the OLD names are
        // kept as constants purely so ConfigStore/RuntimeStore can import a still-present old file
        // exactly once (see their own remarks); nothing ever writes to the old names again.
        private const string ConfigFileName = "QAdvanceFeedback.config.json";
        private const string LegacyConfigFileName = "plugin.QAdvanceFeedback.config.json";
        private const string ParametersFileName = "QAdvanceFeedback.Parameters.json";
        private const string LegacyParametersFileName = "plugin.QAdvanceFeedback.runtime.json";

        private string _configPath;

        /// <summary>Writes the config file off the data thread (owner, 2026-11-25) - see <see cref="ConfigSaveScheduler"/>.</summary>
        private ConfigSaveScheduler _configSaver;
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

        /// <summary>
        /// Whether a channel configured for the ShakeIt Built-in source is reading - and keying its data
        /// as - Raw right now: the published state for that channel is NotSetUp or NotSupported
        /// (docs\shakeit-source-design.md §4; <see cref="Core.ShakeItEffects.ShakeItSourceResolution.FallsBackToRaw"/>).
        /// The ShakeIt twin of <see cref="ViperSourceBlocked"/>, for the settings page. SetUpIdle (no game,
        /// or the first in-game moment before values arrive) is not blocked, for the same "no game is not
        /// an unsupported game" reason.
        /// </summary>
        public bool ShakeItSourceBlocked(WheelChannelSettings channel, bool isLockChannel)
            => channel != null
               && channel.SourceMode == SourceMode.ShakeIt
               && Core.ShakeItEffects.ShakeItSourceResolution.FallsBackToRaw(ShakeItSourceState.For(isLockChannel));

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

        /// <summary>How often the settings page is told to re-read the learned key data while driving.
        /// Matches the cadence the G-Force learned readout already uses - a learned ceiling moves far too
        /// slowly to be worth marshalling onto the UI thread every frame.</summary>
        private const double KeyDataPushIntervalSeconds = 1.0;

        private double _secondsSinceKeyDataPush;

        /// <summary>The last game id the settings page was told about, so it is told only when the answer
        /// actually changes. Cleared when the page is created, so it is told at once.</summary>
        private string _lastGameIdPushedToUi;

        /// <summary>
        /// Tell the settings page the running game changed, regardless of whether it is being DRIVEN
        /// (<see cref="SettingsPageStateChanged"/>; the page reads <see cref="CurrentGameId"/>).
        /// <para/>
        /// Called above <see cref="DataUpdate"/>'s learning gate - see the remarks there for why. The
        /// learned values still ride the 1 Hz notification, which is correctly gated, because a paused
        /// game has nothing new to teach.
        /// </summary>
        private void PushCurrentGameToSettingsUi(string gameId)
        {
            if (string.Equals(gameId, _lastGameIdPushedToUi, StringComparison.Ordinal)) return;
            if (_settingsPage == null) return;
            _lastGameIdPushedToUi = gameId;
            RaiseSettingsPageStateChanged();
        }

        /// <summary>
        /// Keep the settings page's learned readouts ("[Learned Value: xx.x]", the Auto values, the manual
        /// gate) current while driving: once a second the page is told to re-read them through the one
        /// display rule (<see cref="GetLearnedKeyDataForDisplay"/>, <see cref="ManualGateReadyForDisplay"/>) -
        /// the same values the ShakeIt panels read. Costs nothing before a page exists; reads only
        /// already-computed learner state - it never drives learning or output.
        /// </summary>
        private void PushLearnedKeyDataPointsToSettingsUi(double dtSeconds)
        {
            if (_settingsPage == null)
            {
                _secondsSinceKeyDataPush = 0.0;   // no page yet - start the clock fresh when one appears
                return;
            }

            if (ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0) _secondsSinceKeyDataPush += dtSeconds;
            if (_secondsSinceKeyDataPush < KeyDataPushIntervalSeconds) return;
            _secondsSinceKeyDataPush = 0.0;
            RaiseSettingsPageStateChanged();
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
            _runtimeStore?.SaveLastKeyDataContext(gameId, carId);   // no-op unless it changed

            bool changed = false;
            changed |= TrySeedKeyDataSlot(_settings.Lock.KeyDataPoints, gameId, lockSourceIdentity,
                _normalizedEngine.ManualGateReady(gameId, carId, lockSourceIdentity, isLockChannel: true),
                _settings.Lock.NormalizePattern, isLockChannel: true);
            changed |= TrySeedKeyDataSlot(_settings.Slip.KeyDataPoints, gameId, slipSourceIdentity,
                _normalizedEngine.ManualGateReady(gameId, carId, slipSourceIdentity, isLockChannel: false),
                _settings.Slip.NormalizePattern, isLockChannel: false);

            if (!changed) return;
            SaveConfig();
            _keyDataRevision++;
            RaiseSettingsPageStateChanged();   // the page reloads the seeded slot
        }

        private bool TrySeedKeyDataSlot(KeyDataPointSettings keyData, string gameId, string sourceIdentity,
            bool gateReady, NormalizePattern pattern, bool isLockChannel)
        {
            if (keyData == null || keyData.GetAutoGenerate(sourceIdentity)) return false;
            if (!gateReady) return false;
            if (keyData.GetPerGame(sourceIdentity) && string.IsNullOrEmpty(gameId)) return false;
            if (keyData.IsSeeded(gameId, sourceIdentity)) return false;

            // The one display rule - so the seed writes exactly what the page and panels were showing.
            KeyDataTriple learned = KeyDataPanelModel.LearnedKeyData(
                _normalizedEngine, isLockChannel, gameId, _lastCarIdForKeyData, sourceIdentity);
            if (learned == null) return false;

            double sMax = learned.SMax;
            double s90, s75;
            if (isLockChannel && pattern == NormalizePattern.Mapping)
            {
                s90 = learned.S90;
                s75 = learned.S75;
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
        /// The learned key data the settings page and the ShakeIt panels display for a channel's source,
        /// WITHOUT needing a game to be running - by the one display rule
        /// (<see cref="KeyDataPanelModel.LearnedKeyData"/>).
        /// <para/>
        /// RESTART DISPLAY (owner, 2026-11-19). The context is the (game, car) being driven, or - on the
        /// SimHub menu after a restart - the one persisted last time
        /// (<see cref="RuntimeStore.TryLoadLastKeyDataContext"/>). With that context the exact persist key
        /// is formed: if the parameters file holds it, its value is shown; otherwise the cold-start
        /// default. Never the best value of some OTHER game or car for the same source (the old
        /// <c>PersistedCeilingForSource</c> fallback did that). Null with no context at all: the page and
        /// the panels then show the source's shipped default.
        /// </summary>
        public KeyDataTriple GetLearnedKeyDataForDisplay(bool isLockChannel, string sourceIdentity)
        {
            try
            {
                lock (_engineLock)
                {
                    if (_lastGameIdForKeyData == null) return null;
                    return KeyDataPanelModel.LearnedKeyData(
                        _normalizedEngine, isLockChannel, _lastGameIdForKeyData, _lastCarIdForKeyData, sourceIdentity);
                }
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: GetLearnedKeyDataForDisplay failed - " + e);
                return null;
            }
        }

        /// <summary>The game the last frame reported - paired with <see cref="_lastCarIdForKeyData"/>.</summary>
        private string _lastGameIdForKeyData;

        /// <summary>
        /// THE DISPLAY FOLLOWS THE KEY AT ONCE (owner, 2026-11-22): a game or car change moves the key data display
        /// to the new key immediately - at the menu, paused, before any learnable frame - so a key with no real data
        /// shows its borrowed cold start (the half LED and "Borrowed from ...") straight away instead of the previous
        /// game's own values until the first driven frame. DISPLAY ONLY: no learner is touched, and the driven frame
        /// (<see cref="AutoPersistSeededKeyDataPoints"/>) still sets the same context from the frame it learns from.
        /// No game reported keeps the last context (the restart display). A car that is not known yet (a new game at
        /// its menu) is shown as no car; within the same game an unknown car keeps the last one.
        /// </summary>
        private void FollowKeyDataDisplayContext(string gameId, string carId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            bool sameGame = string.Equals(gameId, _lastGameIdForKeyData, StringComparison.Ordinal);
            string car = !string.IsNullOrEmpty(carId) ? carId : sameGame ? _lastCarIdForKeyData ?? string.Empty : string.Empty;
            if (sameGame && string.Equals(car, _lastCarIdForKeyData ?? string.Empty, StringComparison.Ordinal)) return;
            _lastGameIdForKeyData = gameId;
            _lastCarIdForKeyData = car;
            _runtimeStore?.SaveLastKeyDataContext(gameId, car);
            RaiseSettingsPageStateChanged();
        }

        /// <summary>
        /// The maturity behind <see cref="GetLearnedKeyDataForDisplay"/> - same context, same key - for the
        /// settings page's and the ShakeIt panels' learning indicators (docs\learning-maturity-indicator-report.md).
        /// Read from the UI thread under the engine lock, like the learned values themselves (see <see cref="DataUpdate"/>:
        /// the "last cached answer" this used to fall back to on a collision is what froze the Lock LED). Null with no
        /// context.
        /// </summary>
        public KeyDataMaturity GetLearnedKeyDataMaturityForDisplay(bool isLockChannel, string sourceIdentity)
        {
            try
            {
                lock (_engineLock)
                {
                    if (_lastGameIdForKeyData == null || string.IsNullOrEmpty(sourceIdentity)) return null;
                    return KeyDataPanelModel.LearnedKeyDataMaturity(
                        _normalizedEngine, isLockChannel, _lastGameIdForKeyData, _lastCarIdForKeyData, sourceIdentity);
                }
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: GetLearnedKeyDataMaturityForDisplay failed - " + e);
                return null;
            }
        }

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

        /// <summary>The running plugin, for the native ShakeIt effects SimHub constructs itself
        /// (docs\shakeit-native-effects-design.md). Null before Init and after End.</summary>
        public static QAdvanceFeedback Instance { get; private set; }

        /// <summary>The game SimHub reports, paused or in menus included (empty when none) - selects the
        /// Per-Game key data slot for the ShakeIt panels, exactly as the settings page's own game push does.</summary>
        public string CurrentGameId => _currentGameId;
        private volatile string _currentGameId;

        /// <summary>Whether the Wheel Lock / Wheel Slip Raw branch selected this frame is a native signal or a
        /// fallback estimate (Unknown with no game running). A change is broadcast once as
        /// <see cref="SettingField.SourceAvailability"/>; read the values after that notification.</summary>
        public SourceNativeness CurrentLockNativeness => Nativeness.Lock;
        public SourceNativeness CurrentSlipNativeness => Nativeness.Slip;
        private readonly NativenessBroadcaster _nativeness;
        private NativenessBroadcaster Nativeness => _nativeness;

        /// <summary>Whether each channel's ShakeIt Built-in source can work now (docs\shakeit-source-design.md
        /// §3–4), per channel because ShakeIt's lock and slip support differ per game (direct-mode lock vs
        /// slip). Data-thread snapshot, debounced 0.5 s; a change is broadcast as
        /// <see cref="SettingField.SourceAvailability"/>.</summary>
        public Core.ShakeItEffects.ShakeItSourceStates ShakeItSourceState => _shakeItStates.Published;
        private readonly Core.ShakeItEffects.ShakeItSourceStateBroadcaster _shakeItStates;
        private readonly Core.ShakeItEffects.ShakeItSourceAvailability _lockShakeItAvailability = new Core.ShakeItEffects.ShakeItSourceAvailability();
        private readonly Core.ShakeItEffects.ShakeItSourceAvailability _slipShakeItAvailability = new Core.ShakeItEffects.ShakeItSourceAvailability();
        private Integration.ShakeIt.ShakeItProfileInspector _shakeItInspector;
        private long _lastShakeItAvailabilityTicks;

        public QAdvanceFeedback()
        {
            _nativeness = new NativenessBroadcaster(NotifyNativenessChanged);
            _shakeItStates = new Core.ShakeItEffects.ShakeItSourceStateBroadcaster(NotifyNativenessChanged);
            CreateSlipAngle();
        }

        /// <summary>The per-wheel slip-angle source file (null before Init).</summary>
        internal SlipAngleSourceStore SlipAngleSources => _slipAngleSources;
        private SlipAngleSourceStore _slipAngleSources;
        private string _slipAngleSourcesGame;

        /// <summary>A game started (or changed): pick up a hand edit of the source file, or create it again if it was
        /// deleted. A string compare per frame; the file is only touched when the game changes.</summary>
        private void ReloadSlipAngleSourcesOnGameChange(string gameId)
        {
            if (string.Equals(gameId, _slipAngleSourcesGame, StringComparison.Ordinal)) return;
            _slipAngleSourcesGame = gameId;
            // The data thread reads the catalog in memory (SlipAngleRawSourceTable.Catalog, lock-free); whether the
            // file changed on disk is checked on a pool thread (owner, 2026-11-25: no file read on the data thread).
            SlipAngleSourceStore sources = _slipAngleSources;
            if (string.IsNullOrEmpty(gameId) || sources == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { sources.ReloadIfChanged(); }
                catch (Exception e) { LogWarning("QAdvanceFeedback: Slip Angle source file check failed - " + e.Message); }
            });
        }

        private void NotifyNativenessChanged()
        {
            OnUiThread(() => Hub?.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime));
            RaiseSettingsPageStateChanged();
        }

        /// <summary>Bumped whenever the plugin itself writes key data (the one-time learned seed), so a
        /// panel showing stored values knows to reload them.</summary>
        public int KeyDataRevision => _keyDataRevision;

        /// <summary>True while a G-Force Test Effect (plugin page or ShakeIt panel) drives the G-Force channels.</summary>
        public bool TestEffectActive => _testEffectActive;

        /// <summary>True while a WheelLock/WheelSlip Test Effect drives <c>Projected.*</c>
        /// (docs\shakeit-test-effect-mvvm-design.md).</summary>
        public bool TestLockSlipActive => _testLockSlipActive;

        /// <summary>Which Test Effect runs - at most one at a time. Static: view models subscribe to it once
        /// and must keep the same session across a plugin restart.</summary>
        public static Core.ShakeItEffects.TestEffectSession TestSession { get; } = new Core.ShakeItEffects.TestEffectSession();

        /// <summary>Written from the WPF dispatcher thread, read from SimHub's data thread.</summary>
        private volatile bool _testLockSlipActive;

        /// <summary>
        /// Publishes one WheelLock/WheelSlip Test Effect frame to <c>WheelLock/WheelSlip.Projected.*</c>
        /// and <see cref="LatestProjected"/> from the same result. The native effects read only
        /// <see cref="LatestProjected"/>, so they play exactly the published property values. While a
        /// test publishes, the live <see cref="DataUpdate"/> still computes (and learns from real
        /// telemetry) but does not overwrite these outputs.
        /// <para/>
        /// Null releases the outputs: stop claiming them first, then publish zeroes, so a live frame
        /// arriving in between overwrites the zeroes rather than being suppressed (same order as
        /// <see cref="PublishTestEffect"/>).
        /// </summary>
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult result)
        {
            try
            {
                if (_publisher == null) return;
                if (result != null)
                {
                    _testLockSlipActive = true;
                    _publisher.UpdateProjected(result);
                    PublishLatestProjected(result);
                }
                else
                {
                    _testLockSlipActive = false;
                    ProjectedWheelLockSlipResult zero = EmptyProjected();
                    _publisher.UpdateProjected(zero);
                    PublishLatestProjected(zero);
                }
            }
            catch (Exception e)
            {
                _testLockSlipActive = false;
                SimHub.Logging.Current.Error("QAdvanceFeedback: PublishLockSlipTest failed - " + e);
            }
        }

        private void PublishLatestProjected(ProjectedWheelLockSlipResult result) =>
            System.Threading.Volatile.Write(ref _latestProjected,
                new Core.ShakeItEffects.FrameSnapshot<ProjectedWheelLockSlipResult>(result, Core.ShakeItEffects.FrameSnapshot.NowTicks()));

        private static ProjectedWheelLockSlipResult EmptyProjected()
        {
            var z = new Corners(0, 0, 0, 0);
            return new ProjectedWheelLockSlipResult(z, 0, 0, 0, 0, 0, z, 0, 0, 0, 0, 0, z, 0, 0, 0, 0, 0, z, 0, 0, 0, 0, 0);
        }

        /// <summary>Single writer of the settings shared with the ShakeIt effect panels.</summary>
        public SettingsHub Hub { get; private set; }

        /// <summary>Latest projected Lock/Slip frame (the values behind WheelLock/WheelSlip.Projected.*).</summary>
        public Core.ShakeItEffects.FrameSnapshot<ProjectedWheelLockSlipResult> LatestProjected =>
            System.Threading.Volatile.Read(ref _latestProjected);

        /// <summary>Latest G-Force frame - the same eight values published as GForce.*, including the
        /// settings page's Test Effect while it runs.</summary>
        public Core.ShakeItEffects.FrameSnapshot<GForceOutput> LatestGForce =>
            System.Threading.Volatile.Read(ref _latestGForce);

        private Core.ShakeItEffects.FrameSnapshot<ProjectedWheelLockSlipResult> _latestProjected;
        private Core.ShakeItEffects.FrameSnapshot<GForceOutput> _latestGForce;
        private ConfigFileWatcher _configWatcher;
        private QAdvanceFeedbackSettings _lastAppliedSettings;
        private volatile string _lastSavedConfigHash;

        private void PublishLatestGForce(GForceOutput output) =>
            System.Threading.Volatile.Write(ref _latestGForce,
                new Core.ShakeItEffects.FrameSnapshot<GForceOutput>(output, Core.ShakeItEffects.FrameSnapshot.NowTicks()));

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
                    PublishLatestGForce(output);
                }
                else
                {
                    // Order matters: stop claiming the channels BEFORE zeroing them, so a live frame
                    // arriving in between overwrites the zeroes rather than being suppressed by them.
                    _testEffectActive = false;
                    _publisher.UpdateGForce(GForceOutput.Empty);
                    PublishLatestGForce(GForceOutput.Empty);
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
            _configSaver?.Dispose();
            _configSaver = new ConfigSaveScheduler(WriteConfigFile, LogWarning);
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
                SaveConfigNow();
                LogWarning("QAdvanceFeedback: repaired stored source configuration for "
                    + (repairedLock && repairedSlip ? "both channels"
                        : repairedLock ? "Wheel Lock" : "Wheel Slip")
                    + " - the saved text did not match the selected source mode.");
            }

            // SLIP ANGLE UNIT LISTS (owner, 2026-11-20): a config without them gets the hard-coded lists, written
            // at once so the file shows them; the reader reads the live lists every frame and reports the
            // games it detects (degrees / a grip ratio / "over 100%"), which are saved the same way.
            if (_settings.SlipAngle?.EnsureSourceUnits() == true) SaveConfigNow();

            // VERSION 21 (owner, 2026-11-25): the key data slots and per-source flags store a source by its hashed key,
            // like the parameters file - a config from before is converted once and saved.
            bool rekeyedLock = _settings.Lock?.KeyDataPoints?.RekeySources() ?? false;
            bool rekeyedSlip = _settings.Slip?.KeyDataPoints?.RekeySources() ?? false;
            if (rekeyedLock || rekeyedSlip) SaveConfigNow();
            if (_adapter is SimHubTelemetryAdapter slipAngleAdapter)
            {
                slipAngleAdapter.SlipAngleReader.Units = () => _settings?.SlipAngle?.SourceUnits;
                slipAngleAdapter.SlipAngleReader.UnitsChanged += OnSlipAngleUnitsChanged;
                // "Always car angle" (owner, 2026-11-23): no game's tyre field is read, even one with a source.
                slipAngleAdapter.SlipAngleReader.AlwaysCarAngle = () => _settings?.SlipAngle?.AngleSource == SlipAngleAngleSource.CarAngle;
            }

            // THE PER-WHEEL SLIP-ANGLE SOURCES (owner, 2026-11-23) come from PluginsData\Common\
            // QAdvanceFeedback.SlipAngleSource.json ONLY - created from the copy compiled into this DLL when missing,
            // re-read when the running game changes (DataUpdate), written by the settings page's Apply. A change is
            // broadcast like a source-availability change, so the tab and the ShakeIt panel re-read the game's unit.
            _slipAngleSources = new SlipAngleSourceStore(pluginManager.GetCommonStoragePath(SlipAngleSourceStore.FileName), LogWarning);
            _slipAngleSources.Changed += NotifyNativenessChanged;
            _slipAngleSources.Load();
            // VERSION 21 SLIP ANGLE SOURCE KEYS (owner, 2026-11-25): a Slip Angle learner's source key hashes the game's
            // whole definition - the row's settings (sources, transform, surface, unit, vertical flag; id and note
            // excluded) and the unit in use - so a changed definition learns under a new key.
            Core.KeyRules.SourceKeys.SlipAngleDefinition = SlipAngleDefinitionText;
            Core.KeyRules.SourceKeys.SlipAngleDefinitionChanged();

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
            // INTERNAL SOURCE ALWAYS LEARNS (docs\internal-source-learning-design.md): every source-keyed
            // family also folds the retired hidden "__layer3_raw_fallback__" key into the channel's own
            // internal key once (InternalSourceKeyMigration) - each channel to its OWN internal identity.
            // VERSION 21 (owner, 2026-11-25): every loaded learner key gets its source part in the hashed form
            // (Core.KeyRules.SourceKeys) - after the older migrations, which still read and write readable identities.
            _normalizedEngine.LockLearners.ImportAll(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.MigrateGrip(
                GripLearnerKeyMigration.MigrateLegacyKeys(lockLearnerData), RawSourceFallback.RawIdentity(true))));
            _runtimeStore.LoadSlipLearners(out var slipLearnerData);
            _normalizedEngine.SlipLearners.ImportAll(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.MigrateGrip(
                GripLearnerKeyMigration.MigrateLegacyKeys(slipLearnerData), RawSourceFallback.RawIdentity(false))));

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
            _runtimeStore.LoadSlipLockCalibration(out var slipLockCalibration);
            _legacyEngine.Calibration.ImportCalibrations(slipLockCalibration,
                fromOlderVersion: _runtimeStore.LoadedVersion > 0 && _runtimeStore.LoadedVersion < Core.KeyRules.KeyRule.LatestVersionNumber);

            _runtimeStore.LoadShakeItPrecalibration(out _shakeIt.Presets);
            _runtimeStore.LoadShakeItGameBounds(out _shakeIt.Bounds);

            // SYNC WITH SIMHUB'S SHAKEIT PRESET FILES ON EVERY START (owner, 2026-11-25): all three files are read, with
            // no timestamp check, so a SimHub update reaches the plugin on its next start. "Override current data if
            // exists?" (General, default on) decides a game already held: on, it takes SimHub's values (full upsert); off,
            // it is kept and only new games / metrics are added. A game SimHub dropped is kept either way. These sections
            // hold SimHub's data only (nothing here learns into them). An unchanged result is not rewritten.
            try
            {
                ShakeItConversionOutcome imported = ShakeItPrecalibrationConverter.Convert(
                    ShakeItPrecalibrationConverter.DefaultRootDirectory(), _shakeIt,
                    overrideExisting: _settings?.General?.ShakeItImportOverride ?? true);

                if (imported.FilesRead > 0)
                {
                    _runtimeStore.SaveShakeItPrecalibration(_shakeIt.Presets);
                    _runtimeStore.SaveShakeItGameBounds(_shakeIt.Bounds);
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
            _normalizedEngine.LockScaleLearner.ImportAll(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.MigrateScale(lockScaleData, RawSourceFallback.RawIdentity(true))));
            _runtimeStore.LoadSlipScaleLearners(out var slipScaleData);
            _normalizedEngine.SlipScaleLearner.ImportAll(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.MigrateScale(slipScaleData, RawSourceFallback.RawIdentity(false))));
            // SMax EVIDENCE (RuntimeDocument v14, docs\learning-maturity-indicator-report.md) - after the cold
            // ceilings, so a restart continues each key's learning (and its maturity) where it stopped.
            _runtimeStore.LoadScaleEvidence(true, out var lockScaleEvidence);
            _normalizedEngine.LockScaleLearner.ImportEvidence(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.Migrate(lockScaleEvidence, RawSourceFallback.RawIdentity(true), s => s != null)));
            _runtimeStore.LoadScaleEvidence(false, out var slipScaleEvidence);
            _normalizedEngine.SlipScaleLearner.ImportEvidence(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.Migrate(slipScaleEvidence, RawSourceFallback.RawIdentity(false), s => s != null)));
            // Restart display: the (game, car) the key data was last shown for, so the page and the
            // ShakeIt panels can form the exact persist key while SimHub is still on the menu.
            if (_runtimeStore.TryLoadLastKeyDataContext(out string lastKeyDataGame, out string lastKeyDataCar))
            {
                _lastGameIdForKeyData = lastKeyDataGame;
                _lastCarIdForKeyData = lastKeyDataCar;
            }

            // Clutch Bite learning (docs\clutch-bite-design.md) - kept beside the Lock/Slip learners.
            LoadClutchLearning();
            // Slip Angle window learners (RuntimeDocument v12, docs\slip-angle-design.md 9.5.9).
            LoadSlipAngleLearning();

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
            _normalizedEngine.LockAnchors.ImportAll(Core.KeyRules.SourceKeys.RekeyLearners(InternalSourceKeyMigration.MigrateAnchors(lockAnchorsData, RawSourceFallback.RawIdentity(true))));

            _runtimeStore.LoadGForceLearners(out var accelMaxima, out var decelMaxima, out var latMaxima);
            _settings.GForce.ImportLearnedMaxima(accelMaxima, decelMaxima, latMaxima);

            RebuildProjectedEngine();
            _settings.GForce.ApplyTo(_gforceEngine);

            _publisher.Register(this, _settings.General.EnableDiagnostics);

            // AUTO-UPDATE (owner, 2026-11-25, 1.3.0): a previous update's .dll.old / .dll.new go; what was downloaded last
            // time shows at once; the latest release is checked on a new background thread (nothing here waits for it).
            PluginUpdater.DeleteLeftovers();
            PluginUpdater.ShowDownloaded();
            PluginUpdater.CheckInBackground(LogWarning);

            // TWO-WAY SETTINGS SYNC (docs\shakeit-native-effects-design.md): the hub applies and saves
            // exactly like the settings page's Apply; the watcher is the backup path for writes made
            // outside this process, and never re-reports our own saves.
            // The hub broadcasts its own writes itself, so its apply path only records the new baseline.
            Hub = new SettingsHub(() => _settings, _ =>
            {
                ApplyCore();
                _lastAppliedSettings = SnapshotSettings();
            }, () => _currentGameId, () => CurrentSlipAngleIdentityTier);
            _lastAppliedSettings = SnapshotSettings();
            _lastSavedConfigHash = ConfigFileWatcher.TryHashFile(_configPath);
            try
            {
                _configWatcher = new ConfigFileWatcher(_configPath, () => _lastSavedConfigHash,
                    fromDisk => OnUiThread(() => Hub?.NotifyFileChanged(fromDisk)));
            }
            catch (Exception e) when (e is ArgumentException || e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                LogWarning("QAdvanceFeedback: config file watcher unavailable - " + e.Message);
            }
            Instance = this;

            // ShakeIt source "set up" check: 1 Hz profile inspection on the UI thread, plugin-owned so it
            // runs with the settings page closed (docs\shakeit-source-design.md §3.1, §7).
            _shakeItInspector = new Integration.ShakeIt.ShakeItProfileInspector(pluginManager);
            _shakeItInspector.Start();

            // "When [diagnostics is] off... write NO log output" (per the brief) - scoped to this
            // plugin's own informational/diagnostic tracing (Init/End notices and anything similar),
            // NOT to genuine warnings/errors (a corrupt config file, a CSV write failure): silencing
            // those too would make a real operational problem invisible with no way to diagnose it,
            // which would work against the very purpose of the toggle. This is a judgment call on an
            // otherwise-underspecified interaction, flagged rather than silently assumed.
            LogInfoIfDiagnostics("initialised");
        }

        /// <summary>
        /// ONE FRAME, UNDER THE ENGINE LOCK (owner-reported, 2026-11-22: after FH6 -> F1 in one session the Wheel
        /// Lock SMax and its LED froze at the first F1 reading - "orange, 1%" - until SimHub was restarted, while Slip
        /// and Slip Angle moved on). The settings page and the ShakeIt panels read the learners from the UI thread
        /// (<see cref="GetLearnedKeyDataForDisplay"/>, <see cref="GetLearnedKeyDataMaturityForDisplay"/>,
        /// <see cref="ManualGateReadyForDisplay"/>) while this frame writes them. Unsynchronised, a read that walks a
        /// histogram the frame is filling throws "collection was modified" - measured: 0 of 193,931 reads succeeded
        /// while Lock was learning F1 - so the value read failed every time and the maturity read kept returning its
        /// first cached answer. Slip Angle never had the problem: its reads already share the data thread's lock.
        /// The UI only ever posts to its dispatcher asynchronously, so the frame never waits on the UI thread while
        /// holding this - no deadlock - and a UI read waits at most one frame.
        /// </summary>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            lock (_engineLock) DataUpdateUnderEngineLock(pluginManager, ref data);
        }

        /// <summary>Taken by every frame and by every UI read of the learners - see <see cref="DataUpdate"/>.</summary>
        private readonly object _engineLock = new object();

        private void DataUpdateUnderEngineLock(PluginManager pluginManager, ref GameData data)
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
                _currentGameId = data.GameName ?? string.Empty;
                ReloadSlipAngleSourcesOnGameChange(_currentGameId);
                FollowKeyDataDisplayContext(_currentGameId, data.NewData?.CarId);
                FollowSlipAngleDisplayContext(_currentGameId, data.NewData?.CarId);
                PushCurrentGameToSettingsUi(data.GameName ?? string.Empty);   // after: the page reads CurrentGameId
                // The calibration's current key (track, car) is identity too - named before the gate so the Reset
                // Calibration button knows it while paused (nothing is learned here; the gate below still decides that).
                if (data.NewData != null) _legacyEngine.SetContext(data.GameName ?? string.Empty, data.NewData.TrackIdWithConfig, data.NewData.CarModel);

                // ShakeIt source availability runs every frame, in game or not (the heartbeat and the
                // profile check matter at the menu too) - see UpdateShakeItAvailability.
                UpdateShakeItAvailability(pluginManager, data);

                // SHAKEIT ALGORITHM (docs\shakeit-source-design.md §6-7): the hidden ShakeIt containers'
                // settings + our pedal thresholds as one immutable snapshot for ShakeIt's thread. Reused
                // while unchanged (no per-frame allocation); the tap applies a new one on its next frame.
                // Configured BEFORE the learning gate, so a change made at the menu reaches the tap before the
                // first in-game frame instead of on it (the routing below refuses a tap computed under any
                // other snapshot - see ShakeItSourceResolution.TapComputedUnder).
                _shakeItAlgorithm = ShakeItAlgorithmSettings.Snapshot(_settings.Lock, _settings.Slip, _shakeItAlgorithm);
                Integration.ShakeIt.ShakeItBuiltInTap.Configure(_shakeItAlgorithm);

                if (!data.GameRunning || data.GamePaused || data.GameInMenu
                    || data.NewData == null || data.OldData == null)
                {
                    Nativeness.Clear();
                    // Clutch Bite plays only in a session (or while its Test Effect runs).
                    UpdateClutchOutOfSession();
                    // Slip Angle: outputs 0 / Raw.All null, learner untouched.
                    UpdateSlipAngleOutOfSession();
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
                // (The ShakeIt algorithm snapshot for this frame was configured above, before the gate.)
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
                _legacyEngine.SetContext(data.GameName ?? string.Empty, data.NewData.TrackIdWithConfig, data.NewData.CarModel);

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
                Nativeness.Update(BranchNativeness.ForLock(legacy.SelectedLockBranch), BranchNativeness.ForSlip(legacy.SelectedSlipBranch),
                    sample.Dt.HasValue ? sample.Dt.Value.TotalSeconds : 0.0);

                // ---- Layer 4 input selection: each of the four wheels, per channel, resolves either
                // the shipped default (a plain reference back to Layer 3's own Raw property) or
                // whatever the driver configured instead - see WheelSourceResolver's remarks. Falls
                // back to Layer 3's own value for that wheel on any resolution failure.
                // VIPER ON AN UNSUPPORTED TITLE READS RAW INSTEAD (v1.1.0). Decided per CHANNEL rather
                // than per wheel, because this is not a wheel going quiet - viper4gh's plugin returns
                // before computing anything on a game it does not handle, so all four of its properties
                // sit at the 0 it declared them with for the entire session. Evaluated every frame so a
                // game switch takes effect immediately, but it is two list lookups, not per-wheel work.
                bool lockViperBlocked = ViperSourceBlocked(_settings.Lock, gameId);
                bool slipViperBlocked = ViperSourceBlocked(_settings.Slip, gameId);

                // SHAKEIT BUILT-IN (docs\shakeit-source-design.md §4). A ShakeIt-mode channel reads
                // ShakeIt's built-in values through our native effect ONLY while that channel's state is
                // Supported and the tap is fresh; otherwise it reads Raw for all four wheels AND learns
                // under the Raw identity, exactly like a blocked Viper channel. Decided per CHANNEL, never
                // per wheel, so ShakeIt learning never receives a Raw value. The legacy export properties
                // are no longer read.
                var frameTap = Integration.ShakeIt.ShakeItBuiltInTap.Latest;
                // A tap computed under an OLDER algorithm snapshot (a setting changed since, and ShakeIt has
                // not yet produced a frame with the new one) is not fresh for learning: those are the old
                // algorithm's values, and the identity below already carries the NEW fingerprint. The
                // channel takes the Raw fallback route for those frames (Task C) instead.
                bool tapFresh = frameTap != null && frameTap.Value != null
                    && frameTap.IsFresh(Core.ShakeItEffects.FrameSnapshot.NowTicks(), Core.ShakeItEffects.FrameSnapshot.StaleTicks)
                    && Core.ShakeItEffects.ShakeItSourceResolution.TapComputedUnder(frameTap.Value, _shakeItAlgorithm);
                Core.ShakeItEffects.ShakeItTapValues frameTapValues = tapFresh ? frameTap.Value : null;
                _publisher.UpdateShakeItTap(tapFresh,
                    tapFresh ? frameTapValues.Lock : Corners.Zero, tapFresh ? frameTapValues.Slip : Corners.Zero);

                Core.ShakeItEffects.ChannelSourceRoute lockRoute = Core.ShakeItEffects.ShakeItSourceResolution.Route(
                    _settings.Lock.SourceMode == SourceMode.ShakeIt, lockViperBlocked, _lockShakeItAvailability.State, tapFresh);
                Core.ShakeItEffects.ChannelSourceRoute slipRoute = Core.ShakeItEffects.ShakeItSourceResolution.Route(
                    _settings.Slip.SourceMode == SourceMode.ShakeIt, slipViperBlocked, _slipShakeItAvailability.State, tapFresh);

                // SOURCE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md,
                // "Part 2"): each channel's own current source identity comes from its four per-wheel
                // Source/ScriptType fields (SourceIdentity - a plain property name kept verbatim, an
                // expression hashed) so KeyedGripLearner isolates a genuinely different signal's own
                // learning session rather than silently reusing whatever was learned for a previous,
                // differently-scaled source under the same (game,car). The ShakeIt Built-in route does NOT
                // use the rows: its identity is always the canonical ShakeIt identity
                // (ShakeItSourceResolution.ShakeItIdentity - the legacy export names, today's string, spec
                // §5), so stale rows (e.g. a channel switched to ShakeIt while shadowed to Raw) can never
                // file ShakeIt evidence under another key.
                // THE IDENTITY MOVES WITH THE VALUES. A channel that falls back reads Raw, so it must
                // LEARN as Raw too - otherwise its evidence would pile up under the Viper/ShakeIt key while
                // the numbers came from somewhere else. ShakeItSourceResolution returns both together.
                _lockWheelRead.Set(this, pluginManager, _settings.Lock, true, legacy.LockWheels);
                _slipWheelRead.Set(this, pluginManager, _settings.Slip, false, legacy.SlipWheels);
                Core.ShakeItEffects.ChannelSourceResolution lockResolved = Core.ShakeItEffects.ShakeItSourceResolution.Resolve(
                    lockRoute, true, frameTapValues, _lockWheelRead, ReadRawWheel, ReadConfiguredWheel,
                    lockRoute == Core.ShakeItEffects.ChannelSourceRoute.Configured ? ConfiguredSourceIdentity(_settings.Lock) : null,
                    RawSourceFallback.RawIdentity(true), _shakeItAlgorithm.LockFingerprint);
                Core.ShakeItEffects.ChannelSourceResolution slipResolved = Core.ShakeItEffects.ShakeItSourceResolution.Resolve(
                    slipRoute, false, frameTapValues, _slipWheelRead, ReadRawWheel, ReadConfiguredWheel,
                    slipRoute == Core.ShakeItEffects.ChannelSourceRoute.Configured ? ConfiguredSourceIdentity(_settings.Slip) : null,
                    RawSourceFallback.RawIdentity(false), _shakeItAlgorithm.SlipFingerprint);

                Corners lockSources = lockResolved.Values;
                Corners slipSources = slipResolved.Values;
                bool lockForceRaw = lockResolved.Route == Core.ShakeItEffects.ChannelSourceRoute.RawFallback;
                bool slipForceRaw = slipResolved.Route == Core.ShakeItEffects.ChannelSourceRoute.RawFallback;
                string lockSourceIdentity = lockResolved.Identity;
                string slipSourceIdentity = slipResolved.Identity;

                // Diag.Source.* (docs\raw-gap-and-pad-balance-report.md): publish exactly what Layer 4
                // is about to consume, BEFORE calling it - so a future "does our Raw match the
                // configured source" investigation can read this straight off the CSV/SimHub property
                // instead of inverting the Normalized transform to recover it (as this task's own
                // report had to, for lack of this diagnostic).
                _publisher.UpdateSource(lockSources, slipSources, lockAggregation, slipAggregation);

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
                    lockSourceIdentity, _settings.KeyDataPointDefaults?.LockCustom, isLockChannel: true);
                ApplyCustomColdStart(_normalizedEngine.SlipScaleLearner, _settings.Slip, slipForceRaw,
                    slipSourceIdentity, _settings.KeyDataPointDefaults?.SlipCustom, isLockChannel: false);

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
                // INTERNAL SOURCE ALWAYS LEARNS - what each channel's manual key data would be with Plugin
                // Internal selected, so its always-on learning pass takes the branch it takes when selected.
                // Learning only; the published output never reads these.
                _normalizedEngine.LockInternalSourceIdentity = RawSourceFallback.RawIdentity(true);
                _normalizedEngine.SlipInternalSourceIdentity = RawSourceFallback.RawIdentity(false);
                _normalizedEngine.LockInternalManualAnchors = ResolveManualAnchors(
                    _settings.Lock.KeyDataPoints, gameId, RawSourceFallback.RawIdentity(true), isLockChannel: true,
                    _settings.Lock.NormalizePattern, _settings.KeyDataPointDefaults);
                _normalizedEngine.SlipInternalManualAnchors = ResolveManualAnchors(
                    _settings.Slip.KeyDataPoints, gameId, RawSourceFallback.RawIdentity(false), isLockChannel: false,
                    _settings.Slip.NormalizePattern, _settings.KeyDataPointDefaults);

                // THE ONE-TIME SEED RUNS HERE, in the frame loop - NOT in the settings page. It must not
                // depend on whether the driver happens to have the page open, or "play a new game and the
                // value updates once" would silently not happen for anyone who never opens it.
                AutoPersistSeededKeyDataPoints(gameId, carId, lockSourceIdentity, slipSourceIdentity);
                PushLearnedKeyDataPointsToSettingsUi(sample.Dt.HasValue ? sample.Dt.Value.TotalSeconds : 0.0);

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
                // A running WheelLock/WheelSlip Test Effect owns Projected.* (docs\shakeit-test-effect-mvvm-design.md);
                // everything above still ran, so learning from real telemetry is unaffected.
                if (!_testLockSlipActive)
                {
                    _publisher.UpdateProjected(projected);
                    PublishLatestProjected(projected);
                }

                // ---- Slip Angle (docs\slip-angle-design.md 9) - after Lock/Slip Projected, before G-Force.
                // Own try/catch inside (HealthSubsystems.SlipAngle): a fault publishes the empty result and
                // the rest of this frame carries on.
                SlipAngleFrameResult slipAngle = UpdateSlipAngle(pluginManager, data, sample, gameId, carId, dtSeconds);

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
                // see ProjectedWheelLockSlipResult's own remarks. "Integrate Slip Angle" (docs\slip-angle-design.md
                // section 10) reads the same flavour: the pulse-free projected Slip Angle (already
                // drift-attenuated).
                GForceOutput gforce = _gforceEngine.Compute(
                    sample, accelMaxG, decelMaxG, projected.LockAllWithoutPulse, projected.SlipAllWithoutPulse,
                    accelTransitionScale, decelTransitionScale, latMaxG, slipAngle?.ProjectedPulseFree ?? 0.0);

                // THE TEST EFFECT PANEL WINS while it is running (see PublishTestEffect). Everything
                // else this frame still happens - the learners must not be starved just because the
                // settings page is open - only the eight published channels are handed over, so the two
                // cannot fight over them frame by frame.
                if (!_testEffectActive)
                {
                    _publisher.UpdateGForce(gforce);
                    PublishLatestGForce(gforce);
                }

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

                // ---- Clutch Bite (docs\clutch-bite-design.md): output, standing-start learning.
                UpdateClutch(data, sample, gameId, carId);

                // ---- Runtime persistence (owner, 2026-11-25): NOTHING is exported per frame any more. The save
                // thread (RuntimeStore's 5-second tick) asks for the learned data; this frame answers only then - one
                // volatile read otherwise. A game / car / track change hands it over at once and saves soon.
                _runtimeStore.MarkLearningFrame();
                bool saveKeyChanged = FollowSaveKey(gameId, data.NewData.CarModel, data.NewData.TrackIdWithConfig);
                if (saveKeyChanged || _runtimeStore.SnapshotRequested)
                {
                    SaveLearningSnapshotNow();
                    _runtimeStore.SnapshotTaken();
                    if (saveKeyChanged) _runtimeStore.SaveSoon();
                }

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
        /// One wheel of this channel's own Raw property - what a fallen-back channel reads (see
        /// <see cref="RawSourceFallback"/>). <paramref name="wheelIndex"/> is the canonical corner order
        /// (0 FL, 1 FR, 2 RL, 3 RR).
        /// </summary>
        private double ResolveRawWheel(PluginManager pluginManager, bool isLockChannel, int wheelIndex, double layer3Raw)
            => _sourceResolver.Resolve(pluginManager,
                RawSourceFallback.PropertyName(isLockChannel, wheelIndex),
                RawSourceFallback.RawScriptType, layer3Raw);

        /// <summary>
        /// The per-channel state the frame loop hands <see cref="Core.ShakeItEffects.ShakeItSourceResolution.Resolve{TState}"/>
        /// with the two static readers below, so resolving a channel allocates nothing per frame. One reused
        /// instance per channel, data thread only.
        /// </summary>
        private sealed class WheelReadContext
        {
            public QAdvanceFeedback Plugin;
            public PluginManager PluginManager;
            public WheelChannelSettings Channel;
            public bool IsLockChannel;
            public Corners Layer3Raw;

            public void Set(QAdvanceFeedback plugin, PluginManager pluginManager, WheelChannelSettings channel,
                bool isLockChannel, Corners layer3Raw)
            {
                Plugin = plugin;
                PluginManager = pluginManager;
                Channel = channel;
                IsLockChannel = isLockChannel;
                Layer3Raw = layer3Raw;
            }
        }

        private readonly WheelReadContext _lockWheelRead = new WheelReadContext();

        /// <summary>The ShakeIt algorithm snapshot last handed to the tap (data thread); its fingerprints
        /// key the ShakeIt Built-in route's learning (empty at the defaults).</summary>
        private Core.ShakeItEffects.ShakeItAlgorithmSnapshot _shakeItAlgorithm = Core.ShakeItEffects.ShakeItAlgorithmSnapshot.Defaults;
        private readonly WheelReadContext _slipWheelRead = new WheelReadContext();

        private static readonly Func<WheelReadContext, int, double> ReadRawWheel =
            (c, i) => c.Plugin.ResolveRawWheel(c.PluginManager, c.IsLockChannel, i, c.Layer3Raw[i]);

        private static readonly Func<WheelReadContext, int, double> ReadConfiguredWheel =
            (c, i) => c.Plugin.ResolveConfiguredWheel(c.PluginManager, c.Channel, i, c.Layer3Raw[i]);

        /// <summary>The identity of the channel's four configured Source/ScriptType fields - used only on
        /// the Configured route (Raw, Viper, Custom); the ShakeIt route uses the canonical
        /// <see cref="Core.ShakeItEffects.ShakeItSourceResolution.ShakeItIdentity"/>.</summary>
        private static string ConfiguredSourceIdentity(WheelChannelSettings channel)
            => SourceIdentity.Compute(
                channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                channel.SourceRearRight, channel.ScriptTypeRearRight.ToString());

        /// <summary>
        /// One wheel of the channel's configured source (Raw, Viper, Custom - never ShakeIt mode, which
        /// reads the built-in values or Raw, see <see cref="Core.ShakeItEffects.ShakeItSourceResolution"/>).
        /// Per-wheel resolution failures fall back to Layer 3's own value for that wheel.
        /// </summary>
        private double ResolveConfiguredWheel(PluginManager pluginManager, WheelChannelSettings channel,
            int wheelIndex, double layer3Raw)
        {
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
            WheelChannelSettings channel, bool forceRaw, string sourceIdentity, KeyDataPointDefaultSet custom,
            bool isLockChannel)
        {
            if (learner == null) return;

            // CUSTOM MATCHING RAW (docs\internal-source-learning-design.md 3.1): a Custom channel whose four
            // rows are exactly our Raw properties IS Plugin Internal - it learns under the internal key, so
            // the Custom cold-start reference must not reach that key.
            bool applies = channel != null
                           && !forceRaw
                           && channel.SourceMode == SourceMode.Custom
                           && !RawSourceFallback.MatchesRaw(channel, isLockChannel)
                           // ...and likewise one reproducing the shipped ShakeIt or Viper source.
                           && !RawSourceFallback.MatchesShippedPreset(sourceIdentity, isLockChannel)
                           && custom != null
                           && custom.IsUsable();

            learner.ConfiguredColdStartIdentity = applies ? sourceIdentity : null;
            learner.ConfiguredColdStartSMax = applies ? custom.SMax : 0.0;
        }

        /// <summary>Hands the SMax evidence of both channels to the runtime store (v14) - part of
        /// <see cref="SaveLearningSnapshotNow"/>; each call re-snapshots only the keys learned since the previous one.</summary>
        private void SaveScaleEvidenceNow()
        {
            _runtimeStore?.SaveScaleEvidence(true, _normalizedEngine.LockScaleLearner.ExportEvidence());
            _runtimeStore?.SaveScaleEvidence(false, _normalizedEngine.SlipScaleLearner.ExportEvidence());
        }

        /// <summary>
        /// THE LEARNING SNAPSHOT (owner, 2026-11-25): hands a copy of every frame-fed learner to the runtime store, on the
        /// DATA thread (the learners are not thread-safe). Called only when the save thread asks
        /// (<see cref="RuntimeStore.SnapshotRequested"/>, every 5 seconds while driving), on a game / car / track change,
        /// and at End - never per frame. The store's writer then compares and writes only the sections that changed.
        /// (The Slip Angle and Clutch learners hand theirs over on their own change-gated throttles.)
        /// </summary>
        private void SaveLearningSnapshotNow()
        {
            if (_runtimeStore == null) return;
            // Copies only of the calibration entries that changed since the last snapshot; nothing when none did - and
            // nothing at all while "Persist Calibration data?" is off (learned for the session only).
            if (PersistCalibration)
            {
                Dictionary<string, Core.RawCalculator.Calibration.CalibrationData> calibrations =
                    _legacyEngine.Calibration.ExportCalibrations(out bool calibrationsChanged);
                if (calibrationsChanged) _runtimeStore.SaveSlipLockCalibration(calibrations);
            }
            SaveScaleEvidenceNow();
            _runtimeStore.SaveLockLearners(_normalizedEngine.LockLearners.ExportAll());
            _runtimeStore.SaveSlipLearners(_normalizedEngine.SlipLearners.ExportAll());
            // COLD/WARM (item 3): ExportAll itself protects an already-persisted cold ceiling from a noisy session - see
            // KeyedScaleLearner.PersistedCeiling's own remarks.
            _runtimeStore.SaveLockScaleLearners(_normalizedEngine.LockScaleLearner.ExportAll());
            _runtimeStore.SaveSlipScaleLearners(_normalizedEngine.SlipScaleLearner.ExportAll());
            _runtimeStore.SaveSurfaceSupport(_normalizedEngine.SurfaceSupport.ExportAll());
            // COLD-START CONTINUITY AND CROSS-CAR SEEDING (RuntimeDocument Version 4).
            _runtimeStore.SaveLockPhysicalReference(_normalizedEngine.LockPhysicalReference.ExportAll());
            _runtimeStore.SaveSlipPhysicalReference(_normalizedEngine.SlipPhysicalReference.ExportAll());
            _runtimeStore.SaveLockScaleCrossCarSeed(_normalizedEngine.LockScaleLearner.ExportCrossCarSeeds());
            _runtimeStore.SaveSlipScaleCrossCarSeed(_normalizedEngine.SlipScaleLearner.ExportCrossCarSeeds());
            // FEATURE C (RuntimeDocument Version 8) - WheelLock's own learned S75/S90 anchors.
            _runtimeStore.SaveLockAnchors(_normalizedEngine.LockAnchors.ExportAll());
            _settings.GForce.ExportLearnedMaxima(out var accelSnapshot, out var decelSnapshot, out var latSnapshot);
            _runtimeStore.SaveGForceLearners(accelSnapshot, decelSnapshot, latSnapshot);
        }

        private string _saveKeyGame;
        private string _saveKeyCar;
        private string _saveKeyTrack;

        /// <summary>True when the (game, car, track) of this learning frame differs from the previous one - the learned
        /// data of the key just left is then saved at once (owner, 2026-11-25). Three reference/ordinal compares per
        /// frame; the very first frame is not a change.</summary>
        private bool FollowSaveKey(string gameId, string carModel, string track)
        {
            carModel = carModel ?? string.Empty;
            track = track ?? string.Empty;
            if (string.Equals(_saveKeyGame, gameId, StringComparison.Ordinal)
                && string.Equals(_saveKeyCar, carModel, StringComparison.Ordinal)
                && string.Equals(_saveKeyTrack, track, StringComparison.Ordinal)) return false;
            bool first = _saveKeyGame == null;
            _saveKeyGame = gameId;
            _saveKeyCar = carModel;
            _saveKeyTrack = track;
            return !first;
        }

        // ---------------------------------------------------------------- reset calibration (owner, 2026-11-25)

        /// <summary>"Persist Calibration data?" as applied (on with no settings).</summary>
        private bool PersistCalibration => _settings?.General?.PersistSlipLockCalibration ?? true;

        /// <summary>
        /// "Persist Calibration data?" from the Wheel Lock / Wheel Slip tabs - applied AT ONCE (owner, 2026-11-25): the
        /// config is saved now (UI thread), and turning it off deletes every calibration, in memory and in the parameters
        /// file (saved soon, off the data thread). The broadcast lets the page draft adopt it and both tabs follow.
        /// </summary>
        public void SetPersistCalibration(bool persist)
        {
            try
            {
                if (_settings?.General == null || _settings.General.PersistSlipLockCalibration == persist) return;
                _settings.General.PersistSlipLockCalibration = persist;
                SaveConfigNow();
                if (!persist)
                {
                    lock (_engineLock)
                    {
                        int removed = _legacyEngine.Calibration.ClearAll();
                        _runtimeStore?.SaveSlipLockCalibration(_legacyEngine.Calibration.ExportCalibrations());
                        LogInfo("QAdvanceFeedback: calibration is no longer saved - " + removed + " saved entries deleted");
                    }
                    _runtimeStore?.SaveSoon();
                }
                OnUiThread(() => Hub?.NotifyApplied(new[] { SettingField.SlipLockCalibrationPersist }, SettingsOrigin.Runtime));
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: SetPersistCalibration failed - " + e);
            }
        }

        /// <summary>
        /// Whether the slip / lock calibration holds anything for the CURRENT key (the track and car the last frame
        /// reported - the calibration's own key, shared by Wheel Slip and Wheel Lock). For the Reset Calibration button's
        /// visibility. UI thread, under the engine lock like the other learned-data reads.
        /// </summary>
        public bool HasCalibrationForCurrentKey()
        {
            try
            {
                lock (_engineLock) return _legacyEngine.HasCalibrationForCurrentContext();
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: HasCalibrationForCurrentKey failed - " + e);
                return false;
            }
        }

        /// <summary>
        /// Deletes the slip / lock calibration of the CURRENT key (all its metrics: Slip, RPS-to-speed front and rear)
        /// and saves the file soon - e.g. after fitting a different wheel to the same car. Driving on that key learns it
        /// again from nothing. True when something was deleted.
        /// </summary>
        public bool ResetCalibrationForCurrentKey()
        {
            try
            {
                bool removed;
                lock (_engineLock)
                {
                    removed = _legacyEngine.ResetCalibrationForCurrentContext() > 0;
                    if (removed && _runtimeStore != null && PersistCalibration)
                        _runtimeStore.SaveSlipLockCalibration(_legacyEngine.Calibration.ExportCalibrations());
                }
                if (removed)
                {
                    _runtimeStore?.SaveSoon();
                    LogInfo("QAdvanceFeedback: slip / lock calibration reset for the current track and car");
                }
                return removed;
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: ResetCalibrationForCurrentKey failed - " + e);
                return false;
            }
        }

        private void ResetOnGameSwitch(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            if (string.Equals(gameId, _lastGameName, StringComparison.Ordinal)) return;

            _adapter.Reset();
            _normalizedEngine.ResetDirection();
            _settings.GForce.ResetLearning();
            _gforceEngine.Reset();
            ResetSlipAngleOnGameSwitch();
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

        /// <summary>The settings button: the same sync as every start, now - for a SimHub update installed while the plugin
        /// runs. <paramref name="overrideExisting"/> is the tab's "Override current data if exists?" as shown.</summary>
        public ShakeItConversionOutcome ImportShakeItPrecalibration(bool overrideExisting)
        {
            try
            {
                ShakeItConversionOutcome outcome = ShakeItPrecalibrationConverter.Convert(
                    ShakeItPrecalibrationConverter.DefaultRootDirectory(), _shakeIt, overrideExisting);

                _runtimeStore?.SaveShakeItPrecalibration(_shakeIt.Presets);
                _runtimeStore?.SaveShakeItGameBounds(_shakeIt.Bounds);

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
                DisposeSettingsPage();

                // Final, synchronous flush - guaranteed to write the last few seconds of learning
                // before the process is allowed to exit (see RuntimeStore.Flush's own remarks).
                // The calibration save is throttled during play, so force a final one here.
                lock (_engineLock)   // a last frame may still be running on the data thread
                {
                    if (_runtimeStore != null) SaveLearningSnapshotNow();
                    if (_runtimeStore != null) SaveClutchLearningNow();
                    if (_runtimeStore != null) SaveSlipAngleLearningNow();
                }

                _runtimeStore?.Flush();
                _runtimeStore?.Dispose();

                _configWatcher?.Dispose();
                _configWatcher = null;
                _shakeItInspector?.Dispose();
                _shakeItInspector = null;
                SaveConfigNow();
                _configSaver?.Dispose();
                _configSaver = null;
                if (ReferenceEquals(Instance, this)) Instance = null;
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
        /// report). Building the page does real work (the first call creates the one settings page view
        /// model over <see cref="_settings"/> - see QAdvanceFeedback.SettingsPage.cs - and every call
        /// builds the bound view tree) - a corrupt/partial <see cref="_settings"/> object (e.g. Init above
        /// having failed) or an unexpected XAML / binding fault could throw there. Falls back to a minimal,
        /// clearly-labelled placeholder control rather than letting that throw escape into SimHub's own
        /// settings-panel construction.
        /// </summary>
        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            try
            {
                return CreateSettingsControl(pluginManager);
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
        /// <summary>
        /// Applies and saves the live settings (the settings page's Apply and its other immediate-save
        /// paths), then tells every ShakeIt effect panel exactly which shared fields changed.
        /// </summary>
        public void ApplySettings()
        {
            ApplyCore();
            BroadcastAppliedChanges(SettingsOrigin.PluginPage);
        }

        private void ApplyCore()
        {
            Core.KeyRules.SourceKeys.SlipAngleDefinitionChanged();   // a unit choice may have changed
            RebuildProjectedEngine();
            _settings.GForce.ApplyTo(_gforceEngine);
            SaveConfigNow();
        }

        /// <summary>Diffs the shared fields against the last applied state and broadcasts the change.</summary>
        private void BroadcastAppliedChanges(SettingsOrigin origin)
        {
            QAdvanceFeedbackSettings now = SnapshotSettings();
            IReadOnlyList<SettingField> changed = SettingsHub.Diff(_lastAppliedSettings, now, _currentGameId, CurrentSlipAngleIdentityTier);
            _lastAppliedSettings = now;
            Hub?.NotifyApplied(changed, origin);
        }

        private QAdvanceFeedbackSettings SnapshotSettings() =>
            _settings == null ? null
                : Newtonsoft.Json.JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(_settings));

        /// <summary>The Slip Angle reader changed a unit list (data thread): saved like a key data seed, and the
        /// pages told so they re-key the Slip Angle key data (a ratio game has its own learning key).</summary>
        /// <summary>The text a game's Slip Angle source key hashes (Version 21): the row's canonical settings and the unit
        /// in use. Any thread (built once per game and definition change, then cached by SourceKeys).</summary>
        private string SlipAngleDefinitionText(string gameId)
        {
            SlipAngleSourceGame row = SlipAngleRawSourceTable.Catalog.Find(gameId);
            SlipAngleUnit unit = SettingsHub.NativeUnit(gameId, _settings?.SlipAngle?.SourceUnits);
            return SlipAngleSourceGame.SettingsText(row) + "|unit=" + unit;
        }

        private void OnSlipAngleUnitsChanged()
        {
            Core.KeyRules.SourceKeys.SlipAngleDefinitionChanged();   // the game's unit is part of its source key
            try
            {
                SaveConfig();
                LogInfo("QAdvanceFeedback: Slip Angle source units updated for " + _currentGameId + ".");
                OnUiThread(() => Hub?.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime));
            }
            catch (Exception e)
            {
                LogWarning("QAdvanceFeedback: saving the Slip Angle source units failed - " + e.Message);
            }
        }

        /// <summary>The one place the config file is written. Records the saved content's hash so the
        /// <see cref="ConfigFileWatcher"/> recognises - and ignores - this process's own writes.</summary>
        /// <summary>
        /// A save asked for while driving (a key data seed, a Slip Angle seed on a new surface, a new unit list): NEVER
        /// written on the calling (data) thread - the background <see cref="ConfigSaveScheduler"/> writes it 100 ms after
        /// the last such request. The settings in memory are already current; nothing reads the file back to use them.
        /// </summary>
        private void SaveConfig()
        {
            if (_configSaver != null) _configSaver.RequestSave();
            else WriteConfigFile();
        }

        /// <summary>The same save, synchronously on the caller - plugin Init, the settings page's Apply, End (never the data
        /// thread). Serialised with the background writes by the scheduler's write lock.</summary>
        /// <summary>
        /// Before an update restarts SimHub (UI thread; AZOM persists first too): the config and the learned data are
        /// written now, so nothing depends on how SimHub's exit runs. Best effort - End saves again during the exit.
        /// </summary>
        internal void SaveBeforeRestart()
        {
            try
            {
                SaveConfigNow();
                lock (_engineLock) SaveLearningSnapshotNow();
                _runtimeStore?.Flush();
            }
            catch (Exception e)
            {
                LogWarning("QAdvanceFeedback: saving before the restart failed - " + e.Message);
            }
        }

        private void SaveConfigNow()
        {
            if (_configSaver != null) _configSaver.SaveNow();
            else WriteConfigFile();
        }

        /// <summary>The write itself (the scheduler's thread, or <see cref="SaveConfigNow"/>'s caller).</summary>
        private void WriteConfigFile()
        {
            ConfigStore.Save(_configPath, _settings, LogWarning);
            _lastSavedConfigHash = ConfigFileWatcher.TryHashFile(_configPath);
        }

        /// <summary>
        /// Steps both channels' ShakeIt source state machines (docs\shakeit-source-design.md §3–4) from the
        /// profile inspection, the effects' heartbeat and the built-in tap, then publishes the pair through
        /// the debounced broadcaster. Guarded: a failure is recorded and never aborts the frame. Does not
        /// change which source a channel reads (that is the resolver's job).
        /// </summary>
        private void UpdateShakeItAvailability(PluginManager pluginManager, GameData data)
        {
            try
            {
                long now = Core.ShakeItEffects.FrameSnapshot.NowTicks();
                double dt = _lastShakeItAvailabilityTicks == 0 ? 0.0
                    : Math.Max(0.0, Math.Min(1.0, (now - _lastShakeItAvailabilityTicks) / (double)TimeSpan.TicksPerSecond));
                _lastShakeItAvailabilityTicks = now;

                long heartbeat = Integration.ShakeIt.ShakeItBuiltInTap.HeartbeatTicks;
                var tap = Integration.ShakeIt.ShakeItBuiltInTap.Latest;
                string gameId = data.GameName ?? string.Empty;
                var input = new Core.ShakeItEffects.ShakeItAvailabilityInput
                {
                    EnabledInProfile = _shakeItInspector?.Latest.EnabledInProfile == true,
                    HeartbeatAgeSeconds = heartbeat == 0 ? double.PositiveInfinity : (now - heartbeat) / (double)TimeSpan.TicksPerSecond,
                    GameRunning = data.GameRunning,
                    GamePaused = data.GamePaused,
                    GameInMenu = data.GameInMenu,
                    // ShakeIt's own in-game gate (ShakeITV3PluginBase.DataUpdate): only then is our effect
                    // handed the calibrated data the built-in values need.
                    HasFeedbackData = data.OldData != null && data.NewData?.FeedbackData != null && data.OldData?.FeedbackData != null,
                    GameId = gameId,
                    ValuesFresh = tap != null && tap.IsFresh(now, Core.ShakeItEffects.FrameSnapshot.StaleTicks),
                    DtSeconds = dt
                };

                // The tap's verdict when it has one for this game; else ShakeIt's own check on SimHub's
                // capabilities for the SELECTED game - what hides Lock/Slip from Add Effect and marks them
                // unsupported in ShakeIt's list. Needs neither a running game nor feedback data, so a game
                // like Counter-Strike is reported unsupported as soon as it is selected (owner, 2026-11-20).
                input.GameSupported = Core.ShakeItEffects.ShakeItTapValues.SupportFor(tap, gameId, isLock: true)
                    ?? (gameId.Length > 0 ? Integration.ShakeIt.ShakeItBuiltInTap.SupportedByCapabilities(pluginManager, isLock: true) : null);
                Core.ShakeItEffects.ShakeItSourceState lockState = _lockShakeItAvailability.Step(input);
                input.GameSupported = Core.ShakeItEffects.ShakeItTapValues.SupportFor(tap, gameId, isLock: false)
                    ?? (gameId.Length > 0 ? Integration.ShakeIt.ShakeItBuiltInTap.SupportedByCapabilities(pluginManager, isLock: false) : null);
                Core.ShakeItEffects.ShakeItSourceState slipState = _slipShakeItAvailability.Step(input);

                _shakeItStates.Update(lockState, slipState, dt);
            }
            catch (Exception e)
            {
                HealthRegistry.Report(HealthSubsystems.ShakeItEffects, HealthSeverity.Degraded,
                    "Health.Impact.ShakeItEffects", e.ToString());
            }
        }

        /// <summary>Run on the WPF dispatcher when there is one (settings UIs listen to the hub there),
        /// else inline.</summary>
        private static void OnUiThread(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        private void RebuildProjectedEngine()
        {
            _projectedEngine = new ProjectedWheelLockSlipEngine(
                new OutputProjector(_settings.Lock.Projector), _settings.Lock.Pulse,
                new OutputProjector(_settings.Slip.Projector), _settings.Slip.Pulse);
            RebuildSlipAngleProjection();
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

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

        private QAdvanceFeedbackSettings _settings;

        // Layer 2 (SimHub telemetry adapter) and Layer 3 (Raw calculator engine) - see
        // docs\architecture.md for the full layer model.
        private readonly ITelemetryAdapter _adapter = new SimHubTelemetryAdapter();
        private readonly ILegacyWheelLockSlipEngine _legacyEngine = new RawCalculatorEngine();
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
            _configPath = pluginManager.GetCommonStoragePath(ConfigFileName);
            string legacyConfigPath = pluginManager.GetCommonStoragePath(LegacyConfigFileName);
            _settings = ConfigStore.Load(_configPath, LogWarning, legacyPath: legacyConfigPath);

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

            _runtimeStore.LoadGForceLearners(out var accelMaxima, out var decelMaxima);
            _settings.GForce.ImportLearnedMaxima(accelMaxima, decelMaxima);

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
                if (data == null || !data.GameRunning || data.GamePaused || data.GameInMenu
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
                LegacyWheelLockSlipResult legacy = _legacyEngine.Compute(sample, thresholds, lockAggregation, slipAggregation, rawTelemetry);
                _publisher.UpdateRaw(legacy);

                // ---- Layer 4 input selection: each of the four wheels, per channel, resolves either
                // the shipped default (a plain reference back to Layer 3's own Raw property) or
                // whatever the driver configured instead - see WheelSourceResolver's remarks. Falls
                // back to Layer 3's own value for that wheel on any resolution failure.
                Corners lockSources = new Corners(
                    _sourceResolver.Resolve(pluginManager, _settings.Lock.SourceFrontLeft, _settings.Lock.ScriptTypeFrontLeft, legacy.LockWheels.FrontLeft),
                    _sourceResolver.Resolve(pluginManager, _settings.Lock.SourceFrontRight, _settings.Lock.ScriptTypeFrontRight, legacy.LockWheels.FrontRight),
                    _sourceResolver.Resolve(pluginManager, _settings.Lock.SourceRearLeft, _settings.Lock.ScriptTypeRearLeft, legacy.LockWheels.RearLeft),
                    _sourceResolver.Resolve(pluginManager, _settings.Lock.SourceRearRight, _settings.Lock.ScriptTypeRearRight, legacy.LockWheels.RearRight));

                Corners slipSources = new Corners(
                    _sourceResolver.Resolve(pluginManager, _settings.Slip.SourceFrontLeft, _settings.Slip.ScriptTypeFrontLeft, legacy.SlipWheels.FrontLeft),
                    _sourceResolver.Resolve(pluginManager, _settings.Slip.SourceFrontRight, _settings.Slip.ScriptTypeFrontRight, legacy.SlipWheels.FrontRight),
                    _sourceResolver.Resolve(pluginManager, _settings.Slip.SourceRearLeft, _settings.Slip.ScriptTypeRearLeft, legacy.SlipWheels.RearLeft),
                    _sourceResolver.Resolve(pluginManager, _settings.Slip.SourceRearRight, _settings.Slip.ScriptTypeRearRight, legacy.SlipWheels.RearRight));

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
                string lockSourceIdentity = SourceIdentity.Compute(
                    _settings.Lock.SourceFrontLeft, _settings.Lock.ScriptTypeFrontLeft.ToString(),
                    _settings.Lock.SourceFrontRight, _settings.Lock.ScriptTypeFrontRight.ToString(),
                    _settings.Lock.SourceRearLeft, _settings.Lock.ScriptTypeRearLeft.ToString(),
                    _settings.Lock.SourceRearRight, _settings.Lock.ScriptTypeRearRight.ToString());
                string slipSourceIdentity = SourceIdentity.Compute(
                    _settings.Slip.SourceFrontLeft, _settings.Slip.ScriptTypeFrontLeft.ToString(),
                    _settings.Slip.SourceFrontRight, _settings.Slip.ScriptTypeFrontRight.ToString(),
                    _settings.Slip.SourceRearLeft, _settings.Slip.ScriptTypeRearLeft.ToString(),
                    _settings.Slip.SourceRearRight, _settings.Slip.ScriptTypeRearRight.ToString());

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

                double dtSeconds = sample.Dt.HasValue && sample.Dt.Value.TotalSeconds > 0 ? sample.Dt.Value.TotalSeconds : 0.0;
                ProjectedWheelLockSlipResult projected = _projectedEngine.Compute(normalized, dtSeconds,
                    _normalizedEngine.LockColdStartConfidence, _normalizedEngine.SlipColdStartConfidence);
                _publisher.UpdateProjected(projected);

                // ---- G-force channels.
                _settings.GForce.SetCurrentGameAndCar(gameId, carId);

                double accelMaxG = _settings.GForce.EffectiveAccelMaxG(gameId, carId, sample.FrameTime);
                double decelMaxG = _settings.GForce.EffectiveDecelMaxG(gameId, carId, sample.FrameTime);
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
                    accelTransitionScale, decelTransitionScale);
                _publisher.UpdateGForce(gforce);

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
                }

                // ---- Runtime persistence: in-memory cache only every frame (see RuntimeStore's own
                // remarks) - the background timer/Flush is what actually reaches disk.
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
                _settings.GForce.ExportLearnedMaxima(out var accelSnapshot, out var decelSnapshot);
                _runtimeStore.SaveGForceLearners(accelSnapshot, decelSnapshot);

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

        public void End(PluginManager pluginManager)
        {
            try
            {
                _csv.Stop();

                // Final, synchronous flush - guaranteed to write the last few seconds of learning
                // before the process is allowed to exit (see RuntimeStore.Flush's own remarks).
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
                return new Settings.SettingsControl(this, pluginManager);
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

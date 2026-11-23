using System;
using System.Collections.Generic;
using GameReaderCommon;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Slip Angle composition (docs\slip-angle-design.md 9). The pipeline itself is the pure
    /// <see cref="SlipAngleFrameProcessor"/>; this file only reads the Layer 2 inputs, calls it once per
    /// running frame (after Lock/Slip Projected, before G-Force), publishes the plain result and keeps
    /// the UI snapshots current. Its own try/catch reports <see cref="HealthSubsystems.SlipAngle"/> and
    /// publishes the empty result, so a Slip Angle fault never stops Lock/Slip/G-Force.
    /// <para>Threading: the processor and its learner belong to the data thread (the learner's getters
    /// mutate); <c>End</c> takes the same lock for the final save. Everything the UI reads is an
    /// immutable snapshot written once per frame.</para>
    /// </summary>
    public sealed partial class QAdvanceFeedback
    {
        private readonly object _slipAngleLock = new object();
        private readonly KeyedSlipAngleLearner _slipAngleLearner = new KeyedSlipAngleLearner();
        private SlipAngleFrameProcessor _slipAngleProcessor;
        private SlipAngleStatus _slipAngleStatus = SlipAngleStatus.None;

        // Data thread only: a Slip Angle fault's health entry is cleared after 300 consecutive good frames.
        private readonly HealthRecoveryTracker _slipAngleHealth = new HealthRecoveryTracker(HealthSubsystems.SlipAngle);

        /// <summary>Debounces the Slip Angle tier like Lock/Slip (<see cref="NativenessBroadcaster"/>; only its
        /// first channel is used) and broadcasts <see cref="SettingField.SourceAvailability"/> on a change.</summary>
        private NativenessBroadcaster _slipAngleNativeness;

        /// <summary>Called from the constructor (the save sink and the broadcaster need <c>this</c>).</summary>
        private void CreateSlipAngle()
        {
            _slipAngleLearner.SpeedBandSave = data => _runtimeStore?.SaveSlipAngleSpeedBands(data);
            _slipAngleProcessor = new SlipAngleFrameProcessor(_slipAngleLearner,
                data => _runtimeStore?.SaveSlipAngleLearners(data));
            _slipAngleNativeness = new NativenessBroadcaster(NotifyNativenessChanged);
            _slipAngleSeedKeyData = new SlipAngleDisplayRuleKeyData(_slipAngleLearner);
        }

        private SlipAngleFrameProcessor SlipAngleProcessor => _slipAngleProcessor;

        private NativenessBroadcaster SlipAngleNativeness => _slipAngleNativeness;

        /// <summary>Whether the Slip Angle source is native, the body-motion fallback, or absent (Unknown;
        /// also with no game). Debounced; a change is broadcast as <see cref="SettingField.SourceAvailability"/>.</summary>
        public SourceNativeness CurrentSlipAngleNativeness => SlipAngleNativeness.Lock;

        // Data thread writes; any thread reads. Kept across pause / menu, reset on a game switch.
        private readonly SlipAngleIdentityTier _slipAngleIdentityTier = new SlipAngleIdentityTier();

        /// <summary>The tier the Slip Angle key-data identity (Native / Fallback slot) is chosen by for the
        /// hub, the tab and the ShakeIt panel: the last in-session confirmed tier for the current game,
        /// kept across pause / menu (where <see cref="CurrentSlipAngleNativeness"/> drops to Unknown at
        /// once for the warning) and reset on a game switch. See <see cref="SlipAngleIdentityTier"/>.</summary>
        public SourceNativeness CurrentSlipAngleIdentityTier => _slipAngleKeyDataContext.IdentityTier(_slipAngleIdentityTier.Tier);

        /// <summary>The Slip Angle key-data context (docs\full-mvvm-plan.md 3.5): the (game, car, tier,
        /// surface blend) being driven, or on the menu after a restart the persisted last one
        /// (<see cref="RuntimeStore.TryLoadLastSlipAngleContext"/>). Before a game runs this session the
        /// persisted tier also picks the identity (<see cref="CurrentSlipAngleIdentityTier"/>).</summary>
        private readonly SlipAngleKeyDataContext _slipAngleKeyDataContext = new SlipAngleKeyDataContext();

        // Data thread only: the persisted key data with the one-time seed routed through the display rule.
        private SlipAngleDisplayRuleKeyData _slipAngleSeedKeyData;

        /// <summary>The maturity behind <see cref="GetLearnedSlipAngleWindowForDisplay"/> - same context, same
        /// keys, same lock (docs\learning-maturity-indicator-report.md). Null with no context.</summary>
        public SlipAngleWindowMaturity? GetLearnedSlipAngleMaturityForDisplay(string sourceIdentity)
        {
            try
            {
                SlipAngleKeyDataContext.Snapshot context = _slipAngleKeyDataContext.Current;
                if (context == null) return null;
                lock (_slipAngleLock) return SlipAngleKeyDataModel.LearnedWindowMaturity(_slipAngleLearner, context, sourceIdentity);
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: GetLearnedSlipAngleMaturityForDisplay failed - " + e);
                return null;
            }
        }

        /// <summary>
        /// The learned Slip Angle window the tab and the ShakeIt panel display for <paramref name="sourceIdentity"/>,
        /// WITHOUT needing a game to be running - by the one display rule
        /// (<see cref="SlipAngleKeyDataModel.LearnedWindow(KeyedSlipAngleLearner, SlipAngleKeyDataContext.Snapshot, string)"/>):
        /// the exact (game, car, identity, surface) key's published window, capped for the source, when the
        /// learner (the parameters file, after a restart) holds that exact key; null otherwise and with no
        /// context at all (the UI then shows the source's default). Never another key's window. Any thread:
        /// the learner is read under the data thread's lock (its getters mutate).
        /// </summary>
        public SlipAngleWindow? GetLearnedSlipAngleWindowForDisplay(string sourceIdentity)
        {
            try
            {
                SlipAngleKeyDataContext.Snapshot context = _slipAngleKeyDataContext.Current;
                if (context == null) return null;
                lock (_slipAngleLock) return SlipAngleKeyDataModel.LearnedWindow(_slipAngleLearner, context, sourceIdentity);
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Error("QAdvanceFeedback: GetLearnedSlipAngleWindowForDisplay failed - " + e);
                return null;
            }
        }

        /// <summary>The latest Slip Angle status for the UI (warning, no-data status, learned window,
        /// maturity) - plain values captured on the data thread.</summary>
        public SlipAngleStatus LatestSlipAngleStatus => System.Threading.Volatile.Read(ref _slipAngleStatus);

        /// <summary>The latest Slip Angle frame as published (Projected.All, pulse-free value, diagnostics).</summary>
        public SlipAngleFrameResult LatestSlipAngle => _publisher.SlipAngleSnapshot;

        private Core.ShakeItEffects.FrameSnapshot<double> _latestSlipAngleOutput;

        /// <summary>The published SlipAngle.Projected.All (0-100) with the time it was captured - what the
        /// native Slip Angle ShakeIt effect plays (0 once it is older than 500 ms). Written with every publish.</summary>
        public Core.ShakeItEffects.FrameSnapshot<double> LatestSlipAngleOutput => System.Threading.Volatile.Read(ref _latestSlipAngleOutput);

        /// <summary>True while the Slip Angle Test Effect drives <c>SlipAngle.Projected.All</c>
        /// (docs\slip-angle-design.md 9.11). Written from the WPF dispatcher thread, read on the data thread.</summary>
        public bool TestSlipAngleActive => _testSlipAngleActive;
        private volatile bool _testSlipAngleActive;

        /// <summary>
        /// Publishes one Slip Angle Test Effect frame to <c>SlipAngle.Projected.All</c> and
        /// <see cref="LatestSlipAngleOutput"/> from the same value (the native effect plays only the latter,
        /// so it plays exactly the published property, with or without a game running). While a test
        /// publishes, the live pipeline still runs (and learns from real telemetry) but does not overwrite
        /// these outputs. Raw.All / Normalized.All read as unavailable / 0 during a test: the test feeds the
        /// slider straight into the projection, not through the Raw layer.
        /// <para/>
        /// Null releases the output: stop claiming it first, then publish the empty frame, so a live frame
        /// arriving in between overwrites the empty one rather than being suppressed (the order of
        /// <see cref="PublishLockSlipTest"/>).
        /// </summary>
        public void PublishSlipAngleTest(SlipAngleProjectedResult result)
        {
            try
            {
                if (_publisher == null) return;
                if (result != null)
                {
                    _testSlipAngleActive = true;
                    var frame = new SlipAngleFrameResult
                    {
                        ProjectedAll = result.FinalAll,
                        ProjectedPulseFree = result.PulseFreeAll,
                        DriftState = result.DriftState,
                        DriftFactor = result.DriftFactor,
                    };
                    _publisher.UpdateSlipAngle(frame);
                    CaptureSlipAngleOutput(frame);
                }
                else
                {
                    _testSlipAngleActive = false;
                    _publisher.UpdateSlipAngle(SlipAngleFrameResult.Empty);
                    CaptureSlipAngleOutput(SlipAngleFrameResult.Empty);
                }
            }
            catch (Exception e)
            {
                _testSlipAngleActive = false;
                SimHub.Logging.Current.Error("QAdvanceFeedback: PublishSlipAngleTest failed - " + e);
            }
        }

        private void CaptureSlipAngleOutput(SlipAngleFrameResult result) =>
            System.Threading.Volatile.Write(ref _latestSlipAngleOutput, new Core.ShakeItEffects.FrameSnapshot<double>(
                (result ?? SlipAngleFrameResult.Empty).ProjectedAll, Core.ShakeItEffects.FrameSnapshot.NowTicks()));

        private void LoadSlipAngleLearning()
        {
            _runtimeStore.LoadSlipAngleLearners(out Dictionary<string, SlipAngleKeyState> data);
            // RATIO SCALE CHANGE (owner, 2026-11-22; runtime file v16): a file from before it holds the slip-ratio windows at
            // twice the new angles - they start again (speed groups and k do not depend on the scale and stay).
            if (_runtimeStore.LoadedOlderVersion) KeyedSlipAngleLearner.DropSource(data, SlipAngleFrameProcessor.NativeRatioIdentity);
            _runtimeStore.LoadSlipAngleSpeedBands(out Dictionary<string, SlipAngleSpeedBandSetState> bands);
            lock (_slipAngleLock)
            {
                _slipAngleLearner.Import(data);
                _slipAngleLearner.SpeedBands.Import(bands);
                // An older file holds the learners in the old array shape: export them once now, so the first write
                // (5 s after start) already carries the v15 single values instead of the arrays.
                if (_runtimeStore.LoadedOlderVersion)
                    _slipAngleLearner.Save(saved => _runtimeStore?.SaveSlipAngleLearners(saved));
            }
            // Restart display: the context the key data was last shown for, so the tab and the ShakeIt
            // panel form the exact persist key while SimHub is still on the menu (after the import, so the
            // learner already holds the persisted windows).
            if (_runtimeStore.TryLoadLastSlipAngleContext(out string game, out string car, out SourceNativeness tier))
                _slipAngleKeyDataContext.Restore(game, car, tier);
        }

        /// <summary>Final save at shutdown (under the data-thread lock: Export mutates).</summary>
        private void SaveSlipAngleLearningNow()
        {
            lock (_slipAngleLock) _slipAngleLearner.Save(data => _runtimeStore?.SaveSlipAngleLearners(data));
        }

        /// <summary>Init / Apply: rebuild the projection (curve, pulse, drift) and apply the crossing gate
        /// switch (R29) from settings.</summary>
        private void RebuildSlipAngleProjection()
        {
            SlipAngleChannelSettings s = _settings?.SlipAngle;
            if (s == null) return;
            lock (_slipAngleLock)
                SlipAngleProcessor.Configure(new OutputProjector(s.Projector), s.Pulse, s.Drift, s.CrossingGateEnabled);
        }

        private void ResetSlipAngleOnGameSwitch()
        {
            lock (_slipAngleLock) SlipAngleProcessor.Reset();
            _slipAngleIdentityTier.Reset();
        }

        /// <summary>No game / paused / menu: outputs 0 / null, learner untouched.</summary>
        private void UpdateSlipAngleOutOfSession()
        {
            try
            {
                lock (_slipAngleLock) SlipAngleProcessor.NoteGap();
                PublishSlipAngle(SlipAngleFrameResult.Empty, string.Empty, 0.0, clear: true);
            }
            catch (Exception e)
            {
                HealthRegistry.Report(HealthSubsystems.SlipAngle, HealthSeverity.Degraded, "Health.Impact.SlipAngle", e.ToString());
                _slipAngleHealth.NoteFault();
            }
        }

        /// <summary>One running frame.</summary>
        /// <returns>This frame's real (telemetry) result - <see cref="SlipAngleFrameResult.Empty"/> on a
        /// fault - which G-Force integrates (docs\slip-angle-design.md section 10) whether or not a Slip
        /// Angle Test Effect currently owns the published outputs, exactly like Lock/Slip's.</returns>
        private SlipAngleFrameResult UpdateSlipAngle(PluginManager pluginManager, GameData data, TelemetrySample sample,
            string gameId, string carId, double dtSeconds)
        {
            try
            {
                SlipAngleChannelSettings s = _settings.SlipAngle ?? new SlipAngleChannelSettings();
                SlipAngleInput input = _adapter.ReadSlipAngleInput(data, pluginManager, sample);
                bool custom = s.SourceMode == SourceMode.Custom;

                var request = new SlipAngleFrameRequest
                {
                    GameId = gameId,
                    CarId = carId,
                    Frame = sample.New,
                    Dt = dtSeconds,
                    Input = input,
                    NativeSupport = (_adapter as SimHubTelemetryAdapter)?.SlipAngleNativeSupport,
                    UseCustomSource = custom,
                    Gate = s.Gate,
                    WeightMax = s.WeightMax,
                    WeightMin = s.WeightMin,
                };
                if (custom)
                {
                    WheelSourceSet c = s.CustomSources ?? new WheelSourceSet();
                    request.CustomWheelsDeg = new[]
                    {
                        ResolveSlipAngleWheel(pluginManager, c.FrontLeft, c.ScriptTypeFrontLeft),
                        ResolveSlipAngleWheel(pluginManager, c.FrontRight, c.ScriptTypeFrontRight),
                        ResolveSlipAngleWheel(pluginManager, c.RearLeft, c.ScriptTypeRearLeft),
                        ResolveSlipAngleWheel(pluginManager, c.RearRight, c.ScriptTypeRearRight),
                    };
                    request.CustomSourceIdentity = SourceIdentity.Compute(
                        c.FrontLeft, c.ScriptTypeFrontLeft.ToString(), c.FrontRight, c.ScriptTypeFrontRight.ToString(),
                        c.RearLeft, c.ScriptTypeRearLeft.ToString(), c.RearRight, c.ScriptTypeRearRight.ToString());
                }

                // The one-time Manual seed goes through the display rule (what the tab and panel show), on
                // the display context's surface blend - the previous frame's, Sealed on a new game / car.
                SlipAngleKeyDataContext.Snapshot context = _slipAngleKeyDataContext.Current;
                SurfaceBlend seedBlend = context != null && string.Equals(context.GameId, gameId ?? string.Empty, StringComparison.Ordinal)
                    && string.Equals(context.CarId, carId ?? string.Empty, StringComparison.Ordinal)
                    ? context.Blend : SlipAngleKeyDataModel.MenuBlend;

                SlipAngleFrameResult result;
                lock (_slipAngleLock)
                {
                    request.KeyData = s.KeyData == null ? null : _slipAngleSeedKeyData.Configure(s.KeyData, carId, seedBlend);
                    result = SlipAngleProcessor.Process(request);
                }

                // The one-time learned seed of a manual slot - persisted at once, exactly like Lock/Slip's
                // AutoPersistSeededKeyDataPoints (ConfigStore.Save via SaveConfig, then the revision bump).
                if (result.KeyDataSeeded)
                {
                    SaveConfig();
                    _keyDataRevision++;
                }

                PublishSlipAngle(result, gameId, dtSeconds, clear: false);
                NoteSlipAngleKeyDataContext(result, gameId, carId);
                _slipAngleHealth.NoteGood();
                return result;
            }
            catch (Exception e)
            {
                HealthRegistry.Report(HealthSubsystems.SlipAngle, HealthSeverity.Degraded, "Health.Impact.SlipAngle", e.ToString());
                _slipAngleHealth.NoteFault();
                try
                {
                    if (!_testSlipAngleActive)
                    {
                        _publisher.UpdateSlipAngle(SlipAngleFrameResult.Empty);
                        CaptureSlipAngleOutput(SlipAngleFrameResult.Empty);
                    }
                    SlipAngleNativeness.Clear();
                    _slipAngleIdentityTier.NoteCleared();
                    bool pluginInternal = _settings?.SlipAngle?.SourceMode != SourceMode.Custom;
                    System.Threading.Volatile.Write(ref _slipAngleStatus, SlipAngleStatus.Fault(gameId, pluginInternal));
                }
                catch (Exception) { /* never into SimHub's data loop */ }
                return SlipAngleFrameResult.Empty;
            }
        }

        /// <summary>
        /// Every frame SimHub reports, the menu and pause included (owner, 2026-11-22): the Slip Angle display moves to a
        /// new game or car at once, as Lock/Slip's does (<see cref="FollowKeyDataDisplayContext"/>) - display only, no
        /// learner touched. A new game also forgets the previous game's identity tier (it picks only the key-data slot the
        /// UI shows; the next running frame confirms the new game's own).
        /// </summary>
        private void FollowSlipAngleDisplayContext(string gameId, string carId)
        {
            SlipAngleKeyDataContext.Snapshot before = _slipAngleKeyDataContext.Current;
            if (!_slipAngleKeyDataContext.Follow(gameId, carId)) return;
            if (before != null && !string.Equals(before.GameId, gameId, StringComparison.Ordinal)) _slipAngleIdentityTier.Reset();
            SlipAngleKeyDataContext.Snapshot now = _slipAngleKeyDataContext.Current;
            _runtimeStore?.SaveLastSlipAngleContext(now.GameId, now.CarId, now.Tier);
        }

        /// <summary>A running frame: the display context follows the live (game, car, identity tier, surface
        /// blend); a change of game / car / tier is persisted (a no-op in the store unless it changed).</summary>
        private void NoteSlipAngleKeyDataContext(SlipAngleFrameResult result, string gameId, string carId)
        {
            SourceNativeness tier = _slipAngleIdentityTier.Tier;
            var blend = new SurfaceBlend(result.SurfaceBucket, result.SurfaceWeightSealed, result.SurfaceWeightLoose,
                result.SurfaceWeightLowGrip, result.SurfaceSource);
            if (_slipAngleKeyDataContext.Observe(gameId, carId, tier, blend))
                _runtimeStore?.SaveLastSlipAngleContext(gameId, carId, tier);
        }

        private void PublishSlipAngle(SlipAngleFrameResult result, string gameId, double dtSeconds, bool clear)
        {
            // A running Slip Angle Test Effect owns the outputs (PublishSlipAngleTest); the status still updates.
            if (!_testSlipAngleActive)
            {
                _publisher.UpdateSlipAngle(result);
                CaptureSlipAngleOutput(result);
            }
            if (clear)
            {
                SlipAngleNativeness.Clear();
                _slipAngleIdentityTier.NoteCleared();
            }
            else
            {
                SlipAngleNativeness.Update(result.Tier, SourceNativeness.Unknown, dtSeconds);
                _slipAngleIdentityTier.Observe(gameId, SlipAngleNativeness.Lock);
            }
            System.Threading.Volatile.Write(ref _slipAngleStatus,
                clear ? SlipAngleStatus.None : SlipAngleStatus.From(result, SlipAngleNativeness.Lock, gameId));
        }

        /// <summary>One Custom wheel in degrees: the configured source through the shared
        /// <see cref="WheelSourceResolver"/> (no 0-100 clamp there); null when missing or not a number. The
        /// processor takes |α| and clamps it to 0-45°.</summary>
        private double? ResolveSlipAngleWheel(PluginManager pluginManager, string source, ScriptType scriptType)
        {
            double v = _sourceResolver.Resolve(pluginManager, source, scriptType, double.NaN);
            return ClampMath.IsFinite(v) ? v : (double?)null;
        }
    }
}

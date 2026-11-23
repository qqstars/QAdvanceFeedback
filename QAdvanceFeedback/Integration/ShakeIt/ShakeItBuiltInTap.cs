using System;
using System.Threading;
using GameReaderCommon;
using GameReaderCommon.Feedback;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.ShakeItEffects;
using SimHub.Plugins;
using SimHub.Plugins.DataPlugins.ShakeItV3.Calibration;
using SimHub.Plugins.DataPlugins.ShakeItV3.Effects;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// Reads SimHub's OWN built-in wheel lock/slip (docs\shakeit-native-effects-design.md, "ShakeIt
    /// source replaces the export-based ShakeIt mode"). Any native QAdvanceFeedback ShakeIt effect calls
    /// <see cref="Sample"/> with the GameData + CalibratedFeedbackData ShakeIt hands it; once per frame
    /// (<see cref="TapDeduper"/>) this evaluates a private, never-added WheelsLock/WheelsSlip container
    /// pair and publishes the four wheels of each, 0..100, as <see cref="Latest"/>.
    /// <para/>
    /// The hidden containers are configured from the immutable <see cref="ShakeItAlgorithmSnapshot"/> the
    /// plugin hands <see cref="Configure"/> (docs\shakeit-source-design.md §6-7): the channels' ShakeIt
    /// algorithm settings plus filters that follow our own pedal thresholds. A new snapshot is applied on
    /// ShakeIt's thread on the next frame, inside the guarded call; an unchanged one costs a reference
    /// compare. Before any snapshot arrives, <see cref="ShakeItAlgorithmSnapshot.Defaults"/> is used.
    /// SimHub's own "mute when lock is active" stays off on the hidden copy (it dereferences the real
    /// profile) and is emulated here by <see cref="ShakeItMuteEmulation"/>.
    /// </summary>
    public static class ShakeItBuiltInTap
    {
        private static readonly object Gate = new object();
        private static readonly TapDeduper Deduper = new TapDeduper();
        private static WheelsLockContainer _lock;
        private static WheelsSlipContainer _slip;
        private static FeedbackCapabilities _caps;
        private static string _game;
        private static bool? _lockSupported;
        private static bool? _slipSupported;
        private static FrameSnapshot<ShakeItTapValues> _latest;
        private static long _heartbeatTicks;
        private static ShakeItAlgorithmSnapshot _requested;   // written by the plugin, read on ShakeIt's thread
        private static ShakeItAlgorithmSnapshot _applied;     // ShakeIt's thread only, under Gate

        /// <summary>Latest values, or null before the first frame. Check freshness before use.</summary>
        public static FrameSnapshot<ShakeItTapValues> Latest => Volatile.Read(ref _latest);

        /// <summary><see cref="FrameSnapshot.NowTicks"/> of the last time ShakeIt processed an enabled
        /// QAdvanceFeedback effect (in game or not); 0 before the first (docs\shakeit-source-design.md §3.1).</summary>
        public static long HeartbeatTicks => Interlocked.Read(ref _heartbeatTicks);

        /// <summary>The configuration the hidden containers use from the next frame on. Any thread; null
        /// means the defaults. Pass the same instance while nothing changed (see
        /// <see cref="ShakeItAlgorithmSnapshot.Reuse"/>).</summary>
        public static void Configure(ShakeItAlgorithmSnapshot snapshot) => Volatile.Write(ref _requested, snapshot);

        /// <summary>
        /// Called by EVERY native QAdvanceFeedback effect's <c>ProcessEffects</c> while it (and its groups)
        /// are enabled. Always stamps the heartbeat - ShakeIt processes our effects out of game too
        /// (<c>IsAllowedOutOfGame</c>, with a null <paramref name="calibratedFeedback"/>) - and computes the
        /// built-in values only in game.
        /// </summary>
        public static void Sample(GameData data, CalibratedFeedbackData calibratedFeedback)
        {
            Interlocked.Exchange(ref _heartbeatTicks, FrameSnapshot.NowTicks());
            if (data == null || calibratedFeedback == null || !data.GameRunning) return;

            SafeCall.Value(() =>
            {
                lock (Gate)
                {
                    if (!Deduper.ShouldRun(data.FrameTime)) return false;
                    EnsureEngines(calibratedFeedback.FeedbackCapabilities, data.GameName);
                    ApplyAlgorithm(Volatile.Read(ref _requested) ?? ShakeItAlgorithmSnapshot.Defaults);

                    Corners lockValues = Evaluate(_lock, data, calibratedFeedback);
                    // Like SimHub's own IsMuted, a muted frame does not evaluate slip at all.
                    Corners slipValues = ShakeItMuteEmulation.IsMuted(_applied.SlipMuteWhileLockActive, lockValues)
                        ? Corners.Zero
                        : Evaluate(_slip, data, calibratedFeedback);

                    var values = new ShakeItTapValues(lockValues, slipValues,
                        _lockSupported, _slipSupported, data.GameName ?? string.Empty, _applied);
                    Volatile.Write(ref _latest, new FrameSnapshot<ShakeItTapValues>(values, FrameSnapshot.NowTicks()));
                }
                return true;
            }, false, HealthSubsystems.ShakeItEffects, "Health.Impact.ShakeItEffects");
        }

        private static void EnsureEngines(FeedbackCapabilities caps, string game)
        {
            if (_lock == null)
            {
                _lock = new WheelsLockContainer();
                _lock.LoadDefaultSettings();        // sets IsLock; ApplyAlgorithm sets it from the settings
                _slip = new WheelsSlipContainer();
                _slip.LoadDefaultSettings();
                _slip.MuteWhenLockEffectIsActive = false;   // would dereference ParentProfile (null here)
                _applied = null;                            // fresh containers: configure on this frame
            }

            if (!ReferenceEquals(caps, _caps) || !string.Equals(game, _game, StringComparison.Ordinal))
            {
                _caps = caps;
                _game = game;
                _lock.SetFeedbackCapabilities(caps, game ?? string.Empty);
                _slip.SetFeedbackCapabilities(caps, game ?? string.Empty);
                // ShakeIt's own "is this effect supported on the current game" (docs\shakeit-source-design.md
                // §3.2), per channel: lock and slip differ (WheelsLockDirectMode vs WheelsSlipDirectMode).
                _lockSupported = SupportedOn(_lock.FrontLeft, caps);
                _slipSupported = SupportedOn(_slip.FrontLeft, caps);
            }
        }

        /// <summary>Configures the hidden containers from <paramref name="snapshot"/> when it is not the one
        /// already applied. ShakeIt's thread, under <see cref="Gate"/>.</summary>
        private static void ApplyAlgorithm(ShakeItAlgorithmSnapshot snapshot)
        {
            if (ReferenceEquals(snapshot, _applied)) return;

            _lock.UseLegacyIracingAlgorythm = snapshot.LockUseLegacyIracingAlgorithm;
            _lock.LockSensibility = snapshot.LockSensibility;
            _lock.IsLock = snapshot.LockOnly;
            _lock.BrakeFilter = snapshot.LockBrakeFilter;

            _slip.UseLegacyIracingAlgorythm = snapshot.SlipUseLegacyIracingAlgorithm;
            _slip.MuteWhenLockEffectIsActive = false;   // emulated (ShakeItMuteEmulation)
            _slip.UseBrakeFilter = snapshot.SlipUseBrakeFilter;
            _slip.BrakeFilter = snapshot.SlipBrakeFilter;
            _slip.UseThrottleFilter = snapshot.SlipUseThrottleFilter;
            _slip.ThrottleFilter = snapshot.SlipThrottleFilter;

            _applied = snapshot;
        }

        private static WheelSlipEffect _probeLock, _probeSlip;   // the plugin's data thread only
        private static FeedbackCapabilities _probeCaps;
        private static bool? _probeLockSupported, _probeSlipSupported;

        /// <summary>
        /// ShakeIt's own "is Wheel Lock / Wheel Slip supported on the current game" from SimHub's
        /// capabilities for the SELECTED game, the call ShakeIt itself makes for its Add Effect list and
        /// its "not supported" mark (owner, 2026-11-20). Needs neither a running game nor feedback data, so
        /// it answers where the tap has no verdict (Counter-Strike). Null when SimHub has no capabilities
        /// or the call fails. The plugin's data thread; recomputed only when the capabilities object changes.
        /// </summary>
        public static bool? SupportedByCapabilities(PluginManager pluginManager, bool isLock)
        {
            try
            {
                FeedbackCapabilities caps = pluginManager?.GameManager?.GetFeedbackCapabilities();
                if (caps == null) return null;
                if (!ReferenceEquals(caps, _probeCaps))
                {
                    if (_probeLock == null)
                    {
                        _probeLock = new WheelSlipEffect("Probe lock", GameReaderCommon.Enums.FFBPlacement.FrontLeft, isLock: true);
                        _probeSlip = new WheelSlipEffect("Probe slip", GameReaderCommon.Enums.FFBPlacement.FrontLeft, isLock: false);
                    }
                    _probeLockSupported = SupportedOn(_probeLock, caps);
                    _probeSlipSupported = SupportedOn(_probeSlip, caps);
                    _probeCaps = caps;
                }
                return isLock ? _probeLockSupported : _probeSlipSupported;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool? SupportedOn(EffectBase effect, FeedbackCapabilities caps) =>
            caps == null || effect == null ? (bool?)null : effect.GetIsSupportedOnCurrentGame(caps);

        private static Corners Evaluate(EffectsContainerBase container, GameData data, CalibratedFeedbackData cfd)
        {
            WheelSlipEffect fl, fr, rl, rr;
            if (container is WheelsLockContainer l)
            {
                fl = (WheelSlipEffect)l.FrontLeft; fr = (WheelSlipEffect)l.FrontRight;
                rl = (WheelSlipEffect)l.RearLeft; rr = (WheelSlipEffect)l.RearRight;
            }
            else
            {
                var s = (WheelsSlipContainer)container;
                fl = (WheelSlipEffect)s.FrontLeft; fr = (WheelSlipEffect)s.FrontRight;
                rl = (WheelSlipEffect)s.RearLeft; rr = (WheelSlipEffect)s.RearRight;
            }

            return new Corners(
                Math.Abs(fl.GetEffectValue(data, cfd, container)) * 100.0,
                Math.Abs(fr.GetEffectValue(data, cfd, container)) * 100.0,
                Math.Abs(rl.GetEffectValue(data, cfd, container)) * 100.0,
                Math.Abs(rr.GetEffectValue(data, cfd, container)) * 100.0);
        }
    }
}

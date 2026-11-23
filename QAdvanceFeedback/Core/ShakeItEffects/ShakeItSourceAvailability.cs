using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.Health;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>Whether a channel's ShakeIt Built-in source can work right now
    /// (docs\shakeit-source-design.md §3–4).</summary>
    public enum ShakeItSourceState
    {
        /// <summary>No enabled QAdvanceFeedback effect in either ShakeIt plugin's current profile, or
        /// ShakeIt has not processed one for more than <see cref="ShakeItSourceAvailability.HeartbeatSeconds"/>.</summary>
        NotSetUp = 0,
        /// <summary>Set up; no game being driven (no game, paused, menu), or in game waiting for the first values.</summary>
        SetUpIdle = 1,
        /// <summary>Set up, the game is supported (or not yet known) and the built-in values are fresh.</summary>
        Supported = 2,
        /// <summary>Set up, but the game reported unsupported (sticks until the game changes) or the values
        /// stayed stale for <see cref="ShakeItSourceAvailability.StaleSeconds"/> of in-game time (until fresh
        /// values return or the game changes).</summary>
        NotSupported = 3
    }

    /// <summary>One step's inputs to <see cref="ShakeItSourceAvailability"/>. Plain values; no clock.</summary>
    public struct ShakeItAvailabilityInput
    {
        /// <summary>At least one QAdvanceFeedback effect is enabled (with its groups) in a ShakeIt profile.</summary>
        public bool EnabledInProfile;
        /// <summary>Seconds since ShakeIt last processed one of our enabled effects; +∞ when never.</summary>
        public double HeartbeatAgeSeconds;
        public bool GameRunning;
        public bool GamePaused;
        public bool GameInMenu;
        /// <summary>ShakeIt's own in-game gate holds (OldData != null and both frames carry FeedbackData) -
        /// only then does ShakeIt hand our effects the calibrated data the built-in values need.</summary>
        public bool HasFeedbackData;
        /// <summary>The game SimHub reports (empty when none). A change re-checks a sticky NotSupported.</summary>
        public string GameId;
        /// <summary>ShakeIt's own effect says the current game is supported (true/false), or null when not known yet.</summary>
        public bool? GameSupported;
        /// <summary>The built-in values are fresh this step.</summary>
        public bool ValuesFresh;
        /// <summary>Time since the previous step, seconds.</summary>
        public double DtSeconds;
    }

    /// <summary>
    /// Per-channel state machine for the ShakeIt Built-in source (docs\shakeit-source-design.md §3–4).
    /// Pure and time-injected; called once per data-thread step.
    /// <list type="bullet">
    /// <item>Set up = enabled in profile AND heartbeat ≤ <see cref="HeartbeatSeconds"/>; otherwise NotSetUp.</item>
    /// <item>Driving = running, not paused, not in a menu, and ShakeIt's own in-game gate holds
    /// (<see cref="ShakeItAvailabilityInput.HasFeedbackData"/>).</item>
    /// <item>The selected game REPORTED unsupported (running or not, driving or not) → NotSupported, sticky
    /// for the game until <see cref="ShakeItAvailabilityInput.GameId"/> changes. No game selected is never
    /// a verdict.</item>
    /// <item>Values stale for <see cref="StaleSeconds"/> of driving time → NotSupported, but NOT sticky:
    /// it holds while not driving and recovers to Supported on the first fresh values.</item>
    /// <item>Not driving → SetUpIdle (unless a verdict above holds); the staleness clock resets.</item>
    /// <item>Driving with fresh values → Supported. Within the staleness grace the previous Supported is
    /// held (no flicker on a brief dropout), else SetUpIdle.</item>
    /// </list>
    /// </summary>
    public sealed class ShakeItSourceAvailability
    {
        public const double HeartbeatSeconds = 2.0;
        public const double StaleSeconds = 3.0;

        private string _gameId = string.Empty;
        private bool _reportedUnsupported;
        private bool _staleVerdict;
        private double _staleFor;

        public ShakeItSourceState State { get; private set; } = ShakeItSourceState.NotSetUp;

        public ShakeItSourceState Step(ShakeItAvailabilityInput input)
        {
            string game = input.GameId ?? string.Empty;
            if (!string.Equals(game, _gameId, StringComparison.Ordinal))
            {
                _gameId = game;
                _reportedUnsupported = false;
                _staleVerdict = false;
                _staleFor = 0;
            }

            bool setUp = input.EnabledInProfile
                && !double.IsNaN(input.HeartbeatAgeSeconds)
                && input.HeartbeatAgeSeconds <= HeartbeatSeconds;
            bool driving = input.GameRunning && !input.GamePaused && !input.GameInMenu && input.HasFeedbackData;

            if (!setUp)
            {
                _staleFor = 0;
                return State = ShakeItSourceState.NotSetUp;
            }
            // The selected game reported unsupported is a verdict whether or not it is running or driving
            // (owner, 2026-11-20): ShakeIt's own capability check answers for the selected game, the way
            // its Add Effect list and its "not supported" mark do. Outside driving, no game selected is
            // never a verdict; while driving, the verdict counts as it always did.
            if (!_reportedUnsupported && input.GameSupported == false && (driving || game.Length > 0))
            {
                _reportedUnsupported = true;
                _staleFor = 0;
            }
            if (_reportedUnsupported) return State = ShakeItSourceState.NotSupported;
            if (!driving)
            {
                _staleFor = 0;
                return State = _staleVerdict ? ShakeItSourceState.NotSupported : ShakeItSourceState.SetUpIdle;
            }
            if (input.ValuesFresh)
            {
                _staleFor = 0;
                _staleVerdict = false;
                return State = ShakeItSourceState.Supported;
            }
            if (_staleVerdict) return State = ShakeItSourceState.NotSupported;

            if (input.DtSeconds > 0 && !double.IsInfinity(input.DtSeconds)) _staleFor += input.DtSeconds;
            if (_staleFor >= StaleSeconds)
            {
                _staleVerdict = true;
                _staleFor = 0;
                return State = ShakeItSourceState.NotSupported;
            }
            return State = State == ShakeItSourceState.Supported ? ShakeItSourceState.Supported : ShakeItSourceState.SetUpIdle;
        }
    }

    /// <summary>
    /// Holds the published Wheel Lock / Wheel Slip ShakeIt source states and calls <c>notify</c> once per
    /// change, after the new pair has been stable for <see cref="HoldSeconds"/> of caller-supplied time
    /// (same debounce as <see cref="NativenessBroadcaster"/>). Pure: no wall clock.
    /// </summary>
    public sealed class ShakeItSourceStateBroadcaster
    {
        public const double HoldSeconds = 0.5;

        private readonly Action _notify;
        private volatile ShakeItSourceStates _published = ShakeItSourceStates.Initial;
        private ShakeItSourceStates _pending;
        private double _pendingFor;

        public ShakeItSourceStateBroadcaster(Action notify) { _notify = notify; }

        /// <summary>The published pair (safe to read from any thread).</summary>
        public ShakeItSourceStates Published => _published;

        public void Update(ShakeItSourceState lockState, ShakeItSourceState slipState, double dtSeconds)
        {
            ShakeItSourceStates current = _published;
            if (lockState == current.Lock && slipState == current.Slip) { _pending = null; return; }

            if (_pending == null || _pending.Lock != lockState || _pending.Slip != slipState)
            {
                _pending = new ShakeItSourceStates(lockState, slipState);
                _pendingFor = 0;
                return;
            }

            _pendingFor += dtSeconds > 0 ? dtSeconds : 0;
            if (_pendingFor < HoldSeconds) return;
            _published = _pending;
            _pending = null;
            _notify?.Invoke();
        }
    }

    /// <summary>Immutable Lock/Slip ShakeIt source state pair.</summary>
    public sealed class ShakeItSourceStates
    {
        public static readonly ShakeItSourceStates Initial =
            new ShakeItSourceStates(ShakeItSourceState.NotSetUp, ShakeItSourceState.NotSetUp);

        public ShakeItSourceStates(ShakeItSourceState lockState, ShakeItSourceState slipState)
        {
            Lock = lockState;
            Slip = slipState;
        }

        public ShakeItSourceState Lock { get; }
        public ShakeItSourceState Slip { get; }

        public ShakeItSourceState For(bool isLock) => isLock ? Lock : Slip;
    }

    /// <summary>One container in a ShakeIt profile, as read by an <see cref="IShakeItProfileReader"/>.</summary>
    public sealed class ShakeItContainerInfo
    {
        public ShakeItContainerInfo(string typeName, bool isEnabled, IReadOnlyList<bool> ancestorGroupsEnabled = null)
        {
            TypeName = typeName ?? string.Empty;
            IsEnabled = isEnabled;
            AncestorGroupsEnabled = ancestorGroupsEnabled ?? Array.Empty<bool>();
        }

        /// <summary>The container's class name (what SimHub saves as ContainerType).</summary>
        public string TypeName { get; }
        public bool IsEnabled { get; }
        /// <summary>IsEnabled of every group the container sits in, innermost first.</summary>
        public IReadOnlyList<bool> AncestorGroupsEnabled { get; }

        public bool IsEffectivelyEnabled
        {
            get
            {
                if (!IsEnabled) return false;
                foreach (bool g in AncestorGroupsEnabled) if (!g) return false;
                return true;
            }
        }
    }

    /// <summary>Reads one ShakeIt plugin's current profile (Motors or Bass Shakers). Integration
    /// implements it over SimHub's API; tests use fakes.</summary>
    public interface IShakeItProfileReader
    {
        /// <summary>Display name for diagnostics ("ShakeIt Motors").</summary>
        string Name { get; }

        /// <summary>Every (non-group) container of the plugin's current profile, or null when the plugin
        /// or its profile is not there. Throws when SimHub's API shape is not as expected.</summary>
        IReadOnlyList<ShakeItContainerInfo> ReadContainers();
    }

    /// <summary>Immutable result of one profile inspection.</summary>
    public sealed class ShakeItProfileSnapshot
    {
        public static readonly ShakeItProfileSnapshot None = new ShakeItProfileSnapshot(0, false);

        public ShakeItProfileSnapshot(int enabledEffectCount, bool failed)
        {
            EnabledEffectCount = enabledEffectCount;
            Failed = failed;
        }

        /// <summary>Enabled QAdvanceFeedback effects across both plugins' current profiles.</summary>
        public int EnabledEffectCount { get; }
        /// <summary>The inspection failed (API shape moved); treated as not set up.</summary>
        public bool Failed { get; }
        public bool EnabledInProfile => !Failed && EnabledEffectCount > 0;
    }

    /// <summary>
    /// Counts the enabled QAdvanceFeedback effects in the ShakeIt profiles (docs\shakeit-source-design.md §3.1).
    /// A container counts when it is one of <see cref="OurContainerTypeNames"/> and it and all its groups are
    /// enabled. Never throws. Each reader is guarded on its own: a failing reader flags
    /// <see cref="HealthSubsystems.ShakeItProfile"/> as a SimHub compatibility issue but the other reader's count
    /// still stands; the snapshot is failed (= not set up) only when every reader failed. A fully clean
    /// inspection clears that entry.
    /// </summary>
    public static class ShakeItProfileInspection
    {
        /// <summary>The class names of every QAdvanceFeedback ShakeIt container (frozen: SimHub saves them as
        /// ContainerType).</summary>
        public static readonly IReadOnlyCollection<string> OurContainerTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "QAdvanceFeedbackWheelLockContainer",
            "QAdvanceFeedbackWheelSlipContainer",
            "QAdvanceFeedbackGForceBottomFrontContainer",
            "QAdvanceFeedbackGForceBottomRearContainer",
            "QAdvanceFeedbackGForceBackLowContainer",
            "QAdvanceFeedbackGForceBackTopContainer",
            "QAdvanceFeedbackClutchBiteContainer",
            "QAdvanceFeedbackSlipAngleContainer"
        };

        public static bool IsOurs(string typeName) =>
            typeName != null && ((HashSet<string>)OurContainerTypeNames).Contains(typeName);

        public static ShakeItProfileSnapshot Inspect(IEnumerable<IShakeItProfileReader> readers)
        {
            int count = 0, attempted = 0, failed = 0;
            if (readers == null) return ShakeItProfileSnapshot.None;
            foreach (IShakeItProfileReader reader in readers)
            {
                if (reader == null) continue;
                attempted++;
                try
                {
                    IReadOnlyList<ShakeItContainerInfo> containers = reader.ReadContainers();
                    if (containers == null) continue;
                    foreach (ShakeItContainerInfo c in containers)
                        if (c != null && IsOurs(c.TypeName) && c.IsEffectivelyEnabled) count++;
                }
                catch (Exception e)
                {
                    failed++;
                    HealthRegistry.Report(HealthSubsystems.ShakeItProfile, HealthSeverity.Degraded,
                        "Health.Impact.ShakeItProfile", SafeName(reader) + ": " + e, isSimHubCompatibilityIssue: true);
                }
            }
            if (failed == 0) HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
            if (attempted > 0 && failed == attempted) return new ShakeItProfileSnapshot(0, true);
            return new ShakeItProfileSnapshot(count, false);
        }

        private static string SafeName(IShakeItProfileReader reader)
        {
            try { return reader.Name ?? "?"; }
            catch (Exception) { return "?"; }
        }
    }
}

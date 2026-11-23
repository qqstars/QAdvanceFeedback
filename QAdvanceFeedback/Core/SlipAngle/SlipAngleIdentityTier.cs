using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// The Slip Angle source tier used ONLY to pick the key-data identity (Native / Fallback slot) for
    /// the settings hub, the Slip Angle tab and the ShakeIt panel - "the last in-session tier for the
    /// current game".
    /// <para/>
    /// The debounced broadcaster tier (<see cref="NativenessBroadcaster"/>) drops to Unknown at once when
    /// the game pauses, enters a menu or stops (<see cref="NoteCleared"/>), which is right for the orange
    /// fallback warning but wrong for identity: Unknown maps to the Native slot, so an edit made while
    /// paused on a fallback game would land in the Native slot and never apply on resume. This value
    /// survives those gaps. It changes only when
    /// <list type="bullet">
    /// <item>the game id changes (a game switch: it restarts from the current confirmed tier), or</item>
    /// <item>the CONFIRMED (debounced) tier changes during a running frame (<see cref="Observe"/>).</item>
    /// </list>
    /// A drop to Unknown caused by <see cref="NoteCleared"/> is not a confirmed in-session change, so
    /// resuming into the same tier leaves it untouched.
    /// <para/>
    /// Pure. Written on the data thread only; <see cref="Tier"/> may be read from any thread.
    /// </summary>
    public sealed class SlipAngleIdentityTier
    {
        private volatile SourceNativeness _tier = SourceNativeness.Unknown;
        private SourceNativeness _lastConfirmed = SourceNativeness.Unknown;
        private string _gameId;

        /// <summary>The tier the key-data identity is keyed by.</summary>
        public SourceNativeness Tier => _tier;

        /// <summary>A running frame, after the broadcaster's update: <paramref name="confirmedTier"/> is
        /// its debounced tier.</summary>
        public void Observe(string gameId, SourceNativeness confirmedTier)
        {
            gameId = gameId ?? string.Empty;
            if (!string.Equals(gameId, _gameId, StringComparison.Ordinal))
            {
                _gameId = gameId;
                _lastConfirmed = confirmedTier;
                _tier = confirmedTier;
                return;
            }
            if (confirmedTier != _lastConfirmed) _tier = confirmedTier;
            _lastConfirmed = confirmedTier;
        }

        /// <summary>The broadcaster was cleared (no game / paused / menu / fault): its tier is Unknown
        /// now, but that is not an in-session change - the identity tier is kept.</summary>
        public void NoteCleared() => _lastConfirmed = SourceNativeness.Unknown;

        /// <summary>A game switch: forget the previous game's tier.</summary>
        public void Reset()
        {
            _gameId = null;
            _lastConfirmed = SourceNativeness.Unknown;
            _tier = SourceNativeness.Unknown;
        }
    }
}

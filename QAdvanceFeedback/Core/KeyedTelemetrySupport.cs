using System.Collections.Generic;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// PER-GAME TELEMETRY SUPPORT DETECTION (telemetry-integrity pass, item 2). SimHub's own
    /// <c>FeedbackCapabilities</c> is the PRIMARY, authoritative source for whether a title supports a
    /// given channel - already read via <c>pluginManager.GameManager.GetFeedbackCapabilities()</c> and
    /// trusted directly wherever a flag covers a field (see <c>RawWheelTelemetryBuilder</c>/
    /// <c>WheelSlipBranchSelector</c>). This class exists ONLY for the one field this plugin's own audit
    /// found NO capability flag covers at all: <see cref="ITelemetryFrame.WheelOnLooseSurfaceFrontLeft"/>
    /// (see that property's own remarks) - a plain <c>bool[]</c> that defaults to <c>false</c> whether a
    /// title populates it meaningfully or not, exactly like <c>WheelRPS</c>/<c>WheelSpeed</c> default to
    /// zero, except with no matching flag to settle the question the way those two have.
    /// <para/>
    /// DETECTION IS CONDITIONAL, NEVER "ALWAYS ABSENT": <c>false</c> is never treated as evidence of
    /// absence (a car can spend an entire session on sealed tarmac - "never reported loose" happens on
    /// EVERY genuinely-supported title on a tarmac-only track too, so it proves nothing either way).
    /// Only <c>true</c> counts, and only SUSTAINED <c>true</c> evidence
    /// (<see cref="MinSustainedTrueObservations"/>) promotes a game - a single stray glitch frame must
    /// not be enough, the same reasoning the RawRPS-vs-groundspeed "impossible zero" example illustrates
    /// for a field that DOES have its own flag.
    /// <para/>
    /// PROMOTION INSTANT, DEMOTION NEVER (within a session OR across a restart): once
    /// <see cref="MinSustainedTrueObservations"/> is reached, the game is marked supported permanently -
    /// a title does not stop supporting a field because a later track/car never happens to need it again.
    /// <para/>
    /// KEYED BY GAME ONLY - support is a property of the TITLE, not the car/source/surface (this task's
    /// own explicit instruction) - see <see cref="ExportAll"/>/<see cref="ImportAll"/> for the
    /// per-GAME-only persisted shape <c>RuntimeStore</c> round-trips through
    /// <c>QAdvanceFeedback.Parameters.json</c>, so a known-supported game is trusted from frame one of a
    /// LATER session, before that session has observed anything itself - "unknown game: detect from
    /// scratch, defaulting to absent until proven present" falls out of this by construction (a game with
    /// no persisted entry simply reads <see cref="IsSupported"/> false until its own evidence arrives).
    /// </summary>
    public sealed class KeyedTelemetrySupport
    {
        /// <summary>Consecutive-or-not TRUE observations required before a game is promoted - small
        /// enough that a genuine excursion onto grass/gravel promotes within a couple of seconds of real
        /// driving, large enough that one glitched/noisy frame cannot promote a title that does not
        /// really support this field. Our own chosen value, not derived from an external reference.</summary>
        public const int MinSustainedTrueObservations = 3;

        private readonly Dictionary<string, int> _trueObservationCounts = new Dictionary<string, int>(System.StringComparer.Ordinal);
        private readonly HashSet<string> _supportedGames = new HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>False for a game never seen or never promoted (the honest "unknown -&gt; absent"
        /// default) - true once promoted, and never reverts to false afterward.</summary>
        public bool IsSupported(string gameId) => !string.IsNullOrEmpty(gameId) && _supportedGames.Contains(gameId);

        /// <summary>
        /// Feeds one frame's own raw reading for <paramref name="gameId"/>.
        /// <paramref name="rawValue"/>: null means the underlying field/array itself was not reachable
        /// this frame (no evidence either way - not counted); false is a real, legitimate reading that
        /// proves nothing about support either way (see this class's own remarks); true is the only
        /// signal that ever counts toward promotion, and only after
        /// <see cref="MinSustainedTrueObservations"/> of them.
        /// </summary>
        public void Observe(string gameId, bool? rawValue)
        {
            if (string.IsNullOrEmpty(gameId) || !rawValue.HasValue || !rawValue.Value) return;
            if (_supportedGames.Contains(gameId)) return; // already promoted - demotion never happens, nothing left to track

            _trueObservationCounts.TryGetValue(gameId, out int count);
            count++;
            if (count >= MinSustainedTrueObservations)
            {
                _supportedGames.Add(gameId);
                _trueObservationCounts.Remove(gameId); // no longer needed once promoted
            }
            else
            {
                _trueObservationCounts[gameId] = count;
            }
        }

        /// <summary>Every game currently promoted - the only entries worth persisting (an
        /// unpromoted/in-progress observation count is deliberately session-only: a game that was almost,
        /// but not yet, promoted when the session ended gets no special treatment next time, exactly the
        /// same "unknown -> detect from scratch" default a genuinely never-seen game gets).</summary>
        public Dictionary<string, bool> ExportAll()
        {
            var export = new Dictionary<string, bool>(System.StringComparer.Ordinal);
            foreach (string game in _supportedGames) export[game] = true;
            return export;
        }

        /// <summary>Restores every previously-promoted game - called once at Init.</summary>
        public void ImportAll(IDictionary<string, bool> data)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, bool> pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || !pair.Value) continue;
                _supportedGames.Add(pair.Key);
            }
        }

        /// <summary>Clears every game's state - a full "forget everything", not an ordinary game switch
        /// (which needs no clearing at all - each game is already isolated by its own key, and demotion
        /// never happens on a mere switch, unlike the per-channel presence/loose-fraction smoothing state
        /// <c>NormalizedWheelLockSlipEngine.ResetDirection</c> still clears).</summary>
        public void Reset()
        {
            _trueObservationCounts.Clear();
            _supportedGames.Clear();
        }
    }
}

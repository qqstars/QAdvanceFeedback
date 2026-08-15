using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// AUTO-mode "learn the maximum actually observed" for one G-force axis (acceleration OR
    /// deceleration - the caller owns two separate instances, see GForceSettings), keyed per
    /// (gameId, carId) so the same game with a different car learns an independent maximum, exactly
    /// as the brief requires.
    /// <para/>
    /// OUTLIER REJECTION (mandatory, and the entire reason this class exists rather than a plain
    /// running max): a candidate value that exceeds the current learned max is NOT accepted
    /// immediately. It is held as a "pending candidate" and only promoted once a SECOND, similar
    /// reading (within <see cref="ConfirmToleranceRatio"/> of the first) arrives - i.e. the new
    /// maximum must be seen at least <see cref="ConfirmFrames"/> times before it is trusted. A single
    /// one-frame spike (e.g. a collision) sets a pending candidate that is simply discarded the next
    /// frame when the reading drops back to something ordinary, so it never becomes the learned
    /// reference. This is the direct, deliberate fix for the exact failure mode described in the
    /// brief: "a 19.9 g spike, and normalising against a fixed constant ruined the sibling project's
    /// signal" - this class exists so that mistake cannot repeat here.
    /// </summary>
    public sealed class GForceMaxLearner
    {
        /// <summary>How many similar consecutive readings above the current learned max are required
        /// before the new value is trusted. 2 is the minimum that distinguishes "one freak frame"
        /// from "a genuinely sustained new maximum" without requiring an unreasonably long
        /// confirmation window.</summary>
        public const int ConfirmFrames = 2;

        /// <summary>Two candidate readings are considered "the same event" (and therefore mutually
        /// confirming) when they are within this fraction of each other.</summary>
        public const double ConfirmToleranceRatio = 0.15;

        /// <summary>Separator used by <see cref="MakeKey"/>. Chosen to be a character that cannot
        /// appear in a SimHub GameName/CarId (both are simple identifiers in practice), so
        /// gameId="Foo", carId="Bar1" cannot collide with gameId="FooX", carId="Bar1" etc.</summary>
        private const string KeySeparator = "|#|";

        private sealed class LearnedState
        {
            public double LearnedMax;
            public double? PendingCandidate;
            public int PendingCount;
        }

        private readonly Dictionary<string, LearnedState> _states = new Dictionary<string, LearnedState>(StringComparer.Ordinal);

        /// <summary>Composite (gameId, carId) key - this is what makes the learned maximum bind per
        /// game AND per car, per the brief (the same game with a different car learns an independent
        /// maximum under AUTO).</summary>
        public static string MakeKey(string gameId, string carId)
            => (gameId ?? string.Empty) + KeySeparator + (carId ?? string.Empty);

        /// <summary>The currently learned maximum for this (gameId, carId), or 0.0 if nothing has
        /// been confirmed yet - the caller (GForceSettings) is responsible for falling back to a
        /// sane seed/Fixed value when this is 0.0, this class only ever reports what it has actually
        /// confirmed.</summary>
        public double GetLearnedMax(string gameId, string carId)
        {
            string key = MakeKey(gameId, carId);
            return _states.TryGetValue(key, out LearnedState s) ? s.LearnedMax : 0.0;
        }

        /// <summary>Feed one frame's G-force magnitude (already non-negative, e.g. Math.Abs of the
        /// signed reading) into the learner for this (gameId, carId). Non-positive or NaN
        /// magnitudes are ignored (nothing to learn from).</summary>
        public void Observe(string gameId, string carId, double magnitude)
        {
            if (double.IsNaN(magnitude) || magnitude <= 0.0) return;

            string key = MakeKey(gameId, carId);
            if (!_states.TryGetValue(key, out LearnedState s))
            {
                s = new LearnedState();
                _states[key] = s;
            }

            if (magnitude <= s.LearnedMax)
            {
                // Not a new candidate - and it does not continue any in-flight confirmation streak
                // either (a genuinely sustained new max would keep producing values ABOVE the old
                // learned max on every frame, not just once).
                s.PendingCandidate = null;
                s.PendingCount = 0;
                return;
            }

            if (s.PendingCandidate.HasValue &&
                Math.Abs(magnitude - s.PendingCandidate.Value) <= ConfirmToleranceRatio * Math.Max(magnitude, s.PendingCandidate.Value))
            {
                s.PendingCount++;
                s.PendingCandidate = Math.Max(s.PendingCandidate.Value, magnitude);

                if (s.PendingCount >= ConfirmFrames)
                {
                    s.LearnedMax = s.PendingCandidate.Value;
                    s.PendingCandidate = null;
                    s.PendingCount = 0;
                }
            }
            else
            {
                // Either the first-ever above-max reading, or a reading that does not resemble the
                // in-flight pending candidate (e.g. the pending candidate itself was a stale one-off) -
                // start (or restart) the confirmation streak from here.
                s.PendingCandidate = magnitude;
                s.PendingCount = 1;
            }
        }

        /// <summary>Clears all learned/pending state for every key - used when a session ends so a
        /// later session does not inherit stale learning (analogous to
        /// SimHubTelemetryAdapter.Reset).</summary>
        public void Reset() => _states.Clear();

        /// <summary>
        /// Snapshots only the CONFIRMED learned maxima (never an in-flight pending candidate - a
        /// reading still awaiting its second confirmation is, by this class's own design, not yet
        /// trustworthy, so persisting it would let a single almost-spike survive a restart and get
        /// confirmed later by an unrelated frame) - keyed exactly as <see cref="MakeKey"/> produces,
        /// for <c>RuntimeStore</c> to write to <c>plugin.QAdvanceFeedback.runtime.json</c>.
        /// </summary>
        public Dictionary<string, double> ExportLearnedMaxima()
        {
            var export = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in _states)
            {
                if (pair.Value.LearnedMax > 0.0) export[pair.Key] = pair.Value.LearnedMax;
            }
            return export;
        }

        /// <summary>Restores confirmed learned maxima persisted by <see cref="ExportLearnedMaxima"/>
        /// - called once at Init. A null/empty source leaves this learner exactly as freshly
        /// constructed (no keys learned yet). Never restores a pending candidate (there is none to
        /// restore - see <see cref="ExportLearnedMaxima"/>'s remarks), so every restored key starts
        /// with a clean confirmation streak for its very next observation.</summary>
        public void ImportLearnedMaxima(IDictionary<string, double> data)
        {
            if (data == null) return;
            foreach (var pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || !ClampMath.IsFinite(pair.Value) || pair.Value <= 0.0) continue;
                _states[pair.Key] = new LearnedState { LearnedMax = pair.Value };
            }
        }
    }
}

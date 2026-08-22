using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// AUTO-mode "learn a REPRESENTATIVE maximum actually observed" for one G-force axis (acceleration
    /// OR deceleration - the caller owns two separate instances, see GForceSettings), keyed per
    /// (gameId, carId) so the same game with a different car learns an independent reference, exactly
    /// as the brief requires.
    /// <para/>
    /// ROBUST ESTIMATION, NOT A BLIND MAXIMUM (docs\robust-auto-gforce-report.md - REPLACES this
    /// class's previous revisions, first a decaying-maximum-plus-2-frame-confirmation scheme, then a
    /// 200-sample-minimum trimmed band): a plain running/decaying maximum is fragile against a handful
    /// of high-but-plausible, non-representative readings that pass the plausibility cap below -
    /// clipping an NPC car's rear bumper in Forza, braking down a slope, a brief telemetry blip. This
    /// class delegates to <see cref="RobustBandEstimator"/> per key - see that class's own remarks for
    /// the index-based pool-selection algorithm and why it needs NO minimum-sample gate at all (the
    /// owner's own explicit, hard constraint, extended to this call site too): the pool itself degrades
    /// gracefully (widening/narrowing) across the whole range of n instead of ever refusing to answer.
    /// <para/>
    /// TWO GATES BEFORE A SAMPLE EVER REACHES THE ESTIMATOR: the HARD plausibility ceiling
    /// (<see cref="LearnCapG"/> - a crash/wall-impact-magnitude reading is rejected OUTRIGHT, never even
    /// entering the window), and the caller's own per-frame validity gate
    /// (<c>Settings.GForceSettings.IsFrameValidForLearning</c> - pit/replay/session-restart/teleport).
    /// This class trusts that both have already been applied to whatever <see cref="Observe"/> is called
    /// with.
    /// <para/>
    /// SLIDING 2-MINUTE REAL-TIME WINDOW (unaffected by the removal of the sample-count gate - see
    /// <see cref="DefaultWindow"/>): using actual sample timestamps rather than a sample count, so a
    /// mid-session surface/condition change is tracked within roughly one window instead of being
    /// permanently diluted by a no-longer-representative earlier condition.
    /// <para/>
    /// NO GATE ANYWHERE: <see cref="GetLearnedMax"/> returns 0.0 ONLY when there is truly NO evidence at
    /// all for a key - no live sample ever observed within the current window AND no persisted seed was
    /// ever imported for it. The caller (<see cref="Settings.GForceSettings"/>) falls back to its
    /// configured FIXED default in exactly that one case - see this class's own remarks and that class's
    /// own ramp for how a subsequent large jump away from that fallback is smoothed rather than stepped.
    /// </summary>
    public sealed class GForceMaxLearner
    {
        /// <summary>
        /// HARD outlier ceiling (docs\field-fixes-report.md, defect A's "ALSO" clause): a magnitude
        /// above this is rejected OUTRIGHT before it can ever reach the estimator - this is what keeps a
        /// wall-impact-scale spike (a real captured session's own <c>Diag.GForce.LearnedAccelMaxG</c>
        /// once reached 179.8) from ever being folded in, regardless of how the estimator downstream
        /// would have handled it. 8.0g mirrors <see cref="Normalized.GripLearner.MaxPlausibleG"/>'s own,
        /// already-correct ceiling/rationale (a session-reset teleport or a one-frame/few-frame
        /// telemetry glitch, not a real car).
        /// </summary>
        public const double MaxPlausibleG = 8.0;

        /// <summary>The sliding REAL-TIME window - 2 minutes, using actual sample timestamps rather
        /// than a sample count, so a mid-session surface/condition change (e.g. a WRC stage's tarmac
        /// section ending) is tracked within roughly one window instead of being permanently diluted
        /// by data from a no-longer-representative earlier condition.</summary>
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);

        /// <summary>Bucket resolution used when constructing each key's own <see cref="RobustBandEstimator"/> -
        /// fine enough (a fixed number of buckets across [0, <see cref="LearnCapG"/>]) that the
        /// pro-rated-bucket approximation (see that class's own remarks) is never visible at G-force
        /// scale.</summary>
        private const int BucketsPerG = 100;

        /// <summary>Separator used by <see cref="MakeKey"/>. Chosen to be a character that cannot
        /// appear in a SimHub GameName/CarId (both are simple identifiers in practice), so
        /// gameId="Foo", carId="Bar1" cannot collide with gameId="FooX", carId="Bar1" etc.</summary>
        private const string KeySeparator = "|#|";

        /// <summary>The learning-path reject ceiling actually used by THIS instance - defaults to
        /// <see cref="MaxPlausibleG"/> (so a plain <c>new GForceMaxLearner()</c>, including every
        /// pre-existing test in this file, is unaffected), but <see cref="Settings.GForceSettings"/>
        /// constructs its accel/decel instances with tighter, ASYMMETRIC values instead (see
        /// <see cref="Settings.GForceSettings.AccelLearnMaxPlausibleG"/>/
        /// <see cref="Settings.GForceSettings.DecelLearnMaxPlausibleG"/> for the derivation) -
        /// docs\gforce-direction-fix-report.md: REJECT (this class) and CLAMP
        /// (<see cref="GForceEngine.LiveMagnitudeClampG"/>, the live path) are different needs with
        /// different, deliberately different-valued bounds.</summary>
        public double LearnCapG { get; }

        /// <summary>See <see cref="DefaultWindow"/> - overridable (tests only; every real caller uses
        /// the default).</summary>
        public TimeSpan Window { get; }

        /// <param name="learnCapG">See <see cref="LearnCapG"/>. A non-positive or non-finite value
        /// falls back to <see cref="MaxPlausibleG"/> rather than disabling rejection entirely.</param>
        /// <param name="window">See <see cref="Window"/>. Defaults to <see cref="DefaultWindow"/> (2
        /// minutes) when null.</param>
        public GForceMaxLearner(double learnCapG = MaxPlausibleG, TimeSpan? window = null)
        {
            LearnCapG = learnCapG > 0.0 && ClampMath.IsFinite(learnCapG) ? learnCapG : MaxPlausibleG;
            Window = window ?? DefaultWindow;
        }

        private sealed class LearnedState
        {
            public RobustBandEstimator Estimator;

            /// <summary>A value restored from a previous session (see <see cref="ImportLearnedMaxima"/>)
            /// - used only until this session's own window has at least one live sample of its own,
            /// matching the owner's explicit "switching cars mid-session immediately picks up that car's
            /// own value" requirement. Null for a key that has never been imported.</summary>
            public double? ImportedSeed;
        }

        private readonly Dictionary<string, LearnedState> _states = new Dictionary<string, LearnedState>(StringComparer.Ordinal);

        /// <summary>Composite (gameId, carId) key - this is what makes the learned reference bind per
        /// game AND per car, per the brief (the same game with a different car learns an independent
        /// reference under AUTO).</summary>
        public static string MakeKey(string gameId, string carId)
            => (gameId ?? string.Empty) + KeySeparator + (carId ?? string.Empty);

        private RobustBandEstimator NewEstimator()
        {
            int bucketCount = Math.Max(64, (int)Math.Ceiling(LearnCapG * BucketsPerG));
            return new RobustBandEstimator(0.0, LearnCapG, bucketCount, Window);
        }

        private LearnedState GetOrCreate(string key)
        {
            if (!_states.TryGetValue(key, out LearnedState s))
            {
                s = new LearnedState { Estimator = NewEstimator() };
                _states[key] = s;
            }
            return s;
        }

        /// <summary>The currently learned reference for this (gameId, carId): the live pool-based
        /// estimate if the window currently holds AT LEAST ONE valid sample (no minimum beyond that -
        /// see this class's own remarks); otherwise the persisted seed from a previous session if one
        /// was ever imported for this key; otherwise 0.0 - the caller (<see cref="Settings.GForceSettings"/>)
        /// is responsible for falling back to a sane FIXED value when this is 0.0, this class only ever
        /// reports what it actually has evidence for.</summary>
        public double GetLearnedMax(string gameId, string carId)
        {
            string key = MakeKey(gameId, carId);
            if (!_states.TryGetValue(key, out LearnedState s)) return 0.0;
            if (s.Estimator.TryEstimate(out double live)) return live;
            return s.ImportedSeed ?? 0.0;
        }

        /// <summary>Feed one frame's G-force magnitude (already non-negative, e.g. Math.Abs of the
        /// signed reading) into the learner for this (gameId, carId), at <paramref name="timestampUtc"/>
        /// (used for the sliding real-time window - see this class's own remarks). Non-positive, NaN,
        /// or implausible (see <see cref="LearnCapG"/>) magnitudes are ignored.</summary>
        public void Observe(string gameId, string carId, double magnitude, DateTime timestampUtc)
        {
            if (double.IsNaN(magnitude) || magnitude <= 0.0) return;
            if (magnitude > LearnCapG) return;

            string key = MakeKey(gameId, carId);
            GetOrCreate(key).Estimator.Observe(timestampUtc, magnitude);
        }

        /// <summary>Clears all learned state for every key - used when a session ends so a later
        /// session does not inherit stale learning (analogous to SimHubTelemetryAdapter.Reset).</summary>
        public void Reset() => _states.Clear();

        /// <summary>
        /// Snapshots the current best-known reference for every key that has one (a live estimate, or -
        /// for a key with no live sample yet this session - a still-live imported seed) so a NEXT restart
        /// can immediately resume from it - keyed exactly as <see cref="MakeKey"/> produces, for
        /// <c>RuntimeStore</c> to write to <c>plugin.QAdvanceFeedback.runtime.json</c>.
        /// </summary>
        public Dictionary<string, double> ExportLearnedMaxima()
        {
            var export = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in _states)
            {
                double value = pair.Value.Estimator.TryEstimate(out double live) ? live : (pair.Value.ImportedSeed ?? 0.0);
                if (value > 0.0) export[pair.Key] = value;
            }
            return export;
        }

        /// <summary>Restores a previously persisted reference per key - called once at Init. A restored
        /// key is used until this session's own window records its own first live sample, at which
        /// point the live estimate takes over exactly like any other cold-start default. A null/empty
        /// source leaves this learner exactly as freshly constructed.</summary>
        public void ImportLearnedMaxima(IDictionary<string, double> data)
        {
            if (data == null) return;
            foreach (var pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || !ClampMath.IsFinite(pair.Value) || pair.Value <= 0.0) continue;
                _states[pair.Key] = new LearnedState { Estimator = NewEstimator(), ImportedSeed = pair.Value };
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.Clutch
{
    /// <summary>One key's saved clutch bite learning (in <c>QAdvanceFeedback.Parameters.json</c>):
    /// the samples while learning, or only the matured bite once learning has finished.</summary>
    public sealed class ClutchBiteLearnerState
    {
        public List<double> Samples { get; set; } = new List<double>();
        public int TotalCount { get; set; }
        public double? Matured { get; set; }
    }

    /// <summary>What a key's bite point is right now.</summary>
    public sealed class ClutchBiteStatus
    {
        /// <summary>The learned median, or the matured bite; null with no evidence.</summary>
        public double? Learned { get; set; }
        public int Count { get; set; }
        public bool IsMatured { get; set; }
        /// <summary>The bite point the output uses.</summary>
        public double Final { get; set; }
    }

    /// <summary>
    /// Clutch bite learning per (game, car, clutch source) (docs\clutch-bite-design.md, "Learning").
    /// <list type="bullet">
    /// <item>Before maturity, the final bite blends the configured value toward the median of the kept
    /// samples with the Lock/Slip SMax ramp, <see cref="ColdWarmBlend.ConcaveHotWeight"/>. Dispersion is
    /// measured in pedal points (standard deviation / 50), not relative to the mean.</item>
    /// <item>Every sample is kept across sessions (the last <see cref="KeepSamples"/>), so a car keeps
    /// learning until it matures.</item>
    /// <item>Mature at <see cref="MatureCount"/> samples within <see cref="MatureSd"/> points: the
    /// median becomes the fixed bite, the samples are dropped, and learning stops for that key.</item>
    /// </list>
    /// Not thread-safe: the data thread owns it; the UI reads statuses through the plugin.
    /// </summary>
    public sealed class ClutchBiteLearner
    {
        public const int KeepSamples = 15;
        public const int MatureCount = 9;
        public const double MatureSd = 3.0;
        public const double DispersionReferencePoints = 50.0;

        private readonly Dictionary<string, ClutchBiteLearnerState> _states =
            new Dictionary<string, ClutchBiteLearnerState>(StringComparer.Ordinal);

        public static string Key(string game, string car, string sourceIdentity) =>
            KeyedGripLearner.MakeKey(game, car, sourceIdentity);

        public void Import(Dictionary<string, ClutchBiteLearnerState> data)
        {
            _states.Clear();
            if (data == null) return;
            foreach (KeyValuePair<string, ClutchBiteLearnerState> kv in data)
                if (kv.Key != null && kv.Value != null) _states[kv.Key] = Copy(kv.Value);
        }

        public Dictionary<string, ClutchBiteLearnerState> ExportAll() =>
            _states.ToDictionary(kv => kv.Key, kv => Copy(kv.Value), StringComparer.Ordinal);

        /// <summary>Adds an accepted sample. False (ignored) when the key has matured. Maturity uses the
        /// parameters' sample count and spread (defaults: <see cref="MatureCount"/>, <see cref="MatureSd"/>).</summary>
        public bool AddSample(string key, double sample, ClutchLearningParameters? parameters = null)
        {
            ClutchLearningParameters p = parameters ?? ClutchLearningParameters.Default;
            if (string.IsNullOrEmpty(key) || !ClampMath.IsFinite(sample)) return false;
            if (!_states.TryGetValue(key, out ClutchBiteLearnerState state))
                _states[key] = state = new ClutchBiteLearnerState();
            if (state.Matured.HasValue) return false;

            state.Samples.Add(ClampMath.To0100(sample));
            while (state.Samples.Count > KeepSamples) state.Samples.RemoveAt(0);
            state.TotalCount++;

            if (state.Samples.Count >= p.MatureSamples && StandardDeviation(state.Samples) <= p.MatureSpreadPoints)
            {
                state.Matured = Median(state.Samples);
                state.Samples.Clear();
            }
            return true;
        }

        /// <param name="rampSamples">The blend ramp's full-trust count (the parameters' mature samples).</param>
        public ClutchBiteStatus Status(string key, double configured, bool auto, int rampSamples = MatureCount)
        {
            var status = new ClutchBiteStatus { Final = configured };
            if (string.IsNullOrEmpty(key) || !_states.TryGetValue(key, out ClutchBiteLearnerState state)) return status;

            status.Count = state.TotalCount;
            if (state.Matured.HasValue)
            {
                status.IsMatured = true;
                status.Learned = state.Matured;
                if (auto) status.Final = state.Matured.Value;
                return status;
            }
            if (state.Samples.Count == 0) return status;

            double median = Median(state.Samples);
            status.Learned = median;
            if (!auto) return status;
            double dispersion = state.Samples.Count < 2 ? 0.0 : StandardDeviation(state.Samples) / DispersionReferencePoints;
            double weight = ColdWarmBlend.ConcaveHotWeight(state.TotalCount, dispersion, Math.Max(1, rampSamples));
            status.Final = ColdWarmBlend.Blend(configured, median, weight);
            return status;
        }

        public void Forget(string key)
        {
            if (key != null) _states.Remove(key);
        }

        private static ClutchBiteLearnerState Copy(ClutchBiteLearnerState s) => new ClutchBiteLearnerState
        {
            Samples = new List<double>(s.Samples ?? new List<double>()),
            TotalCount = s.TotalCount,
            Matured = s.Matured,
        };

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        /// <summary>Sample standard deviation (n − 1).</summary>
        private static double StandardDeviation(List<double> values)
        {
            if (values.Count < 2) return 0.0;
            double mean = values.Average();
            return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        }
    }
}

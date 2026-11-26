using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core.RawCalculator.Calibration
{
    /// <summary>
    /// A faithful port of SimHub's own
    /// <c>SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.CalibrationData</c> (decompiled from the
    /// shipped <c>SimHub.Plugins.dll</c> this project already references).
    /// <para/>
    /// WHY A PORT RATHER THAN OUR OWN LEARNER. Layer 3's whole purpose is to reproduce what the ShakeIt
    /// Motors plugin publishes. The previous implementation reproduced ShakeIt's FORMULAS but paired them
    /// with this project's own distribution learner, whose conventions differ in four ways that each
    /// change the published number: a 200-sample maturity bar instead of 500, a flat 4-decimal histogram
    /// instead of a magnitude-adaptive one, no running maximum (so no pre-maturity fallback at all - the
    /// single largest divergence, see <see cref="GetPercentile"/>), and no memoisation (so percentiles
    /// refreshed every frame where ShakeIt's refresh on a widening interval). This type exists so that
    /// difference is zero.
    /// <para/>
    /// EVERY constant, rounding rule and branch below is SimHub's, not a choice made here. Do not "tidy"
    /// them - the adaptive rounding ladder, the <see cref="CalibrationPointsAdded"/> seed of 1.0, the
    /// positives-only point counter, and the cache-clear interval are all observable in the published
    /// value.
    /// </summary>
    public sealed class CalibrationData : ICalibrationData
    {
        /// <summary>Sample count at which SimHub considers a calibration complete
        /// (<c>IsReady =&gt; Count &gt;= 7000</c>) and stops feeding it
        /// (<c>CalibrationDataProvider.UpdateData</c>).</summary>
        public const int ReadySamples = 7000;

        /// <summary>Positive-bucket count below which <see cref="GetPercentile"/> cannot compute a real
        /// percentile and falls back to <see cref="Max"/> - SimHub's <c>GetPercentileValue</c> gate.
        /// NOTE this is 500, NOT this project's own 200-sample convention.</summary>
        public const int MinSamplesForPercentile = 500;

        /// <summary>The running maximum's own de-rating in the pre-maturity fallback - SimHub's
        /// <c>Math.Max(1.0, Max * 0.9) * percentile / 100.0</c>.</summary>
        public const double FallbackMaxFactor = 0.9;

        public double Max;

        /// <summary>The histogram, keyed by the ADAPTIVELY rounded magnitude - see
        /// <see cref="AddValue"/>. Public with a getter only, matching SimHub's own shape so Newtonsoft
        /// round-trips it by populating the existing instance.</summary>
        public Dictionary<double, int> Values { get; } = new Dictionary<double, int>();

        public double Sum { get; set; }

        public int Count { get; set; }

        /// <summary>Counts only observations that landed in a POSITIVE bucket, and seeds at 1.0 (not 0)
        /// exactly as SimHub does - both details are load-bearing, the first for the percentile gate and
        /// the feed cap, the second for the cache-clear interval's own modulo.</summary>
        public double CalibrationPointsAdded { get; set; } = 1.0;

        /// <summary>SimHub memoises percentiles and clears the cache on a widening interval, so a
        /// published percentile is deliberately STALE between clears rather than recomputed per frame.
        /// Reproduced because it is directly observable in the output.
        /// <para/>
        /// PRIVATE, unlike SimHub's own public properties, for one practical reason: this type is
        /// persisted to <c>QAdvanceFeedback.Parameters.json</c>, and these two are pure derived caches -
        /// serialising them would bloat the file and, worse, restore a stale percentile alongside a
        /// histogram it no longer matches. Being private they are simply absent from the JSON and rebuild
        /// themselves on demand.</summary>
        private readonly Dictionary<string, double?> _percentiles = new Dictionary<string, double?>();

        private readonly Dictionary<string, double?> _average = new Dictionary<string, double?>();

        /// <summary>
        /// COMPACT FORM (owner, 2026-11-25, parameters Version 20: "makes the parameters.json as small as possible after
        /// learned"). A calibration is FINISHED once it has more than <see cref="ReadySamples"/> positive points:
        /// <c>CalibrationDataProvider</c> never feeds it again. The plugin then reads only two things from it - Slip's
        /// <see cref="SlipLowPercentile"/> / <see cref="SlipHighPercentile"/> (<c>DispatchBranchFormulas</c>) and
        /// RPS-to-speed's average (Sum / Count) - so a finished entry is COMPACTED (<see cref="Compact"/>): its histogram
        /// and Max are dropped, in memory and in the file, and it keeps the point counter (which marks it finished),
        /// Sum, Count and - for Slip only (<see cref="KeepsPercentiles"/>) - the two percentiles.
        /// <para/>
        /// Any OTHER percentile asked of a compact entry is not known any more: the entry starts learning again from
        /// nothing (<see cref="RestartLearning"/>) rather than answering a wrong value. Nothing asks for one today.
        /// </summary>
        public const double SlipLowPercentile = 15.0;

        /// <summary>See <see cref="SlipLowPercentile"/>.</summary>
        public const double SlipHighPercentile = 99.0;

        /// <summary>True once no more points are fed (<c>CalibrationDataProvider.UpdateData</c>'s own cap).</summary>
        public bool IsFinished => CalibrationPointsAdded > ReadySamples;

        /// <summary>A finished entry whose histogram was dropped: it answers from its kept percentiles.</summary>
        public bool IsCompact => IsFinished && Values.Count == 0;

        /// <summary>Whether a compact entry keeps the two Slip percentiles - false for the RPS-to-speed metrics, which are
        /// only averaged. Set by <c>CalibrationDataProvider</c> from the entry's key; never saved.</summary>
        public bool KeepsPercentiles { get; private set; } = true;

        private double? _low;
        private double? _high;

        /// <summary>The saved <see cref="SlipLowPercentile"/> of a compact entry (null otherwise - not written).</summary>
        public double? Percentile15
        {
            get => IsCompact && KeepsPercentiles ? _low : null;
            set => _low = value;
        }

        /// <summary>The saved <see cref="SlipHighPercentile"/> of a compact entry (null otherwise - not written).</summary>
        public double? Percentile99
        {
            get => IsCompact && KeepsPercentiles ? _high : null;
            set => _high = value;
        }

        /// <summary>READ ONLY FROM A VERSION 17-19 FILE: the 100-value percentile table those versions saved for a
        /// finished entry. Its 15th and 99th values become the kept percentiles; it is never written.</summary>
        public double[] PercentileTable
        {
            set
            {
                if (value == null || value.Length != 100) return;
                _low = value[(int)SlipLowPercentile - 1];
                _high = value[(int)SlipHighPercentile - 1];
            }
        }

        /// <summary>The histogram and Max are saved only while the entry is still being fed.</summary>
        public bool ShouldSerializeValues() => !IsCompact;

        /// <summary>See <see cref="ShouldSerializeValues"/>.</summary>
        public bool ShouldSerializeMax() => !IsCompact;

        /// <summary>Tells the entry which metric it is (by the provider, from the key): an RPS-to-speed entry keeps no
        /// percentiles. Data thread only.</summary>
        public void SetKeepsPercentiles(bool keeps)
        {
            KeepsPercentiles = keeps;
            if (keeps) return;
            _low = null;
            _high = null;
        }

        /// <summary>
        /// Drops the histogram of a FINISHED entry, keeping what the plugin reads. The kept percentiles are the very
        /// values the next read would have returned (memoised ones included), so the published value does not move.
        /// A no-op for an unfinished or already compact entry. Data thread only (the thread that feeds and reads it).
        /// </summary>
        public void Compact()
        {
            if (!IsFinished || Values.Count == 0) return;
            if (KeepsPercentiles)
            {
                _low = GetPercentileInternal(SlipLowPercentile, false);
                _high = GetPercentileInternal(SlipHighPercentile, false);
            }
            Values.Clear();
            Max = 0.0;
            _percentiles.Clear();
            _average.Clear();
            Revision++;
        }

        /// <summary>A compact entry asked for something it no longer knows: it learns again from nothing.</summary>
        private void RestartLearning()
        {
            Values.Clear();
            Max = 0.0;
            Sum = 0.0;
            Count = 0;
            CalibrationPointsAdded = 1.0;
            _low = null;
            _high = null;
            _percentiles.Clear();
            _average.Clear();
            Revision++;
        }

        /// <summary>Bumped on every change (a point fed, compacted, restarted) - the provider copies an entry for the
        /// writer only when this moved. In memory only.</summary>
        public int Revision { get; private set; }

        /// <summary>A deep, independent copy - what the parameters writer serialises, so it never reads an entry the
        /// data thread is still feeding. Data thread only.</summary>
        public CalibrationData Copy()
        {
            var copy = new CalibrationData
            {
                Max = Max,
                Sum = Sum,
                Count = Count,
                CalibrationPointsAdded = CalibrationPointsAdded,
                KeepsPercentiles = KeepsPercentiles,
                _low = _low,
                _high = _high,
            };
            foreach (KeyValuePair<double, int> kv in Values) copy.Values[kv.Key] = kv.Value;
            return copy;
        }

        public bool IsReady => Count >= ReadySamples;

        public double Completion => (double)Count / ReadySamples * 100.0;

        public bool IsPreset() => false;

        public ICalibrationData GetUnderlyingAutocalibration() => this;

        public void AddValue(double value)
        {
            if (IsCompact) return;   // a compact (finished) entry is never fed - the provider's cap already says so
            if (double.IsNaN(value) || double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value)) return;

            Max = Math.Max(Math.Abs(value), Max);
            Sum += Math.Abs(value);
            if (Count < CountSaturationCap) Count++;

            // SimHub's ADAPTIVE bucket resolution: fine near zero, progressively coarser with magnitude.
            // A flat 4-decimal histogram (this project's previous convention) produces different
            // percentile keys for the same samples, so this ladder is reproduced exactly.
            value = Math.Round(Math.Abs(value), 4);
            if (value > 1000.0) value = Math.Round(value / 10.0) * 10.0;
            if (value > 100.0) value = Math.Round(value / 5.0) * 5.0;
            else if (value > 50.0) value = Math.Round(value);
            else if (value > 10.0) value = Math.Round(value, 1);
            else if (value > 1.0) value = Math.Round(value, 2);

            Values.TryGetValue(value, out int existing);
            Values[value] = existing + 1;
            Revision++;

            if (value > 0.0 && CalibrationPointsAdded < CountSaturationCap) CalibrationPointsAdded += 1.0;

            if (CalibrationPointsAdded % (250 * Math.Max(1, Math.Min((int)(CalibrationPointsAdded / 500.0), 8))) == 0.0)
            {
                _percentiles.Clear();
                _average.Clear();
            }
        }

        /// <summary>
        /// THE DIVERGENCE THIS PORT EXISTS TO REMOVE. Returns a plain <see cref="double"/> - never null.
        /// When the histogram cannot yet support a real percentile (fewer than
        /// <see cref="MinSamplesForPercentile"/> positive samples) SimHub synthesises a band from the
        /// running maximum instead of going silent, so the ShakeIt effect produces a usable reading from
        /// the very first sample. The previous implementation returned null here, and its caller then
        /// published a literal 0 - which is why WheelSlip came in late.
        /// </summary>
        public double GetPercentile(double percentile, bool includeZero = false)
        {
            if (percentile == 0.0) return 0.0;

            double? computed = GetPercentileInternal(percentile, includeZero);
            if (!computed.HasValue) return Math.Max(1.0, Max * FallbackMaxFactor) * percentile / 100.0;
            return computed ?? 1.0;
        }

        private double? GetPercentileInternal(double percentile, bool includeZero)
        {
            if (IsCompact)
            {
                if (!includeZero && KeepsPercentiles && percentile == SlipLowPercentile && _low.HasValue) return _low;
                if (!includeZero && KeepsPercentiles && percentile == SlipHighPercentile && _high.HasValue) return _high;
                RestartLearning();
                return null;
            }
            string key = includeZero + "," + percentile;
            if (_percentiles.TryGetValue(key, out double? cached)) return cached;

            double? value = GetPercentileValue(percentile, includeZero);
            _percentiles[key] = value;
            return value;
        }

        public double? GetAverage()
        {
            if (Count == 0) return null;
            if (_average.TryGetValue("Average", out double? cached)) return cached;

            double? value = Sum / Count;
            _average["Average"] = value;
            return value;
        }

        private double? GetPercentileValue(double excelPercentile, bool includeZero)
        {
            Values.Remove(double.NaN);
            Values.Remove(double.NegativeInfinity);
            Values.Remove(double.PositiveInfinity);

            int total = includeZero
                ? Values.Sum(entry => entry.Value)
                : Values.Where(entry => entry.Key > 0.0).Sum(entry => entry.Value);
            if (total < MinSamplesForPercentile) return null;

            List<KeyValuePair<double, int>> ordered = includeZero
                ? Values.OrderBy(entry => entry.Key).ToList()
                : Values.Where(entry => entry.Key > 0.0).OrderBy(entry => entry.Key).ToList();

            double rank = (total - 1) * (excelPercentile / 100.0) + 1.0;
            if (rank == 1.0) return ordered[0].Key;
            if (rank == total) return ordered[ordered.Count - 1].Key;
            return GetSequence((int)rank - 1, ordered);
        }

        private static double GetSequence(int index, List<KeyValuePair<double, int>> items)
        {
            int running = 0;
            int position = 0;
            while (index > running)
            {
                running += items[position].Value;
                position++;
            }
            return items.Count <= position ? items[items.Count - 1].Key : items[position].Key;
        }

        /// <summary>INT32/precision guard this project applies to every long-lived counter. Chosen far
        /// above any reachable value - <c>CalibrationDataProvider</c> stops feeding at
        /// <see cref="ReadySamples"/> positive points - so it can never alter a published number; it only
        /// removes the theoretical wrap on a counter fed every frame forever.</summary>
        public const int CountSaturationCap = 1_000_000;
    }
}

using System;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>Plain snapshot of one <see cref="GripCurveBins"/> (persisted by the window learner).
    /// One entry per bin in each array (<see cref="GripCurveBins.BinCount"/>).</summary>
    public sealed class GripCurveBinsState
    {
        /// <summary>Observations per bin, saturating at <see cref="GripLearner.SampleCountSaturationCap"/>.</summary>
        public int[] Counts;
        /// <summary>Per-bin decaying P90 of |lateral G|.</summary>
        public double[] P90;
        /// <summary>Per-bin decaying mean |G - P90| (the estimator's step scale).</summary>
        public double[] Spread;
        /// <summary>Seed buffer, flattened as <c>bin * MinSamplesPerBin + k</c>: the first observations
        /// of each bin still below <see cref="GripCurveBins.MinSamplesPerBin"/> samples (the first
        /// <c>Counts[bin]</c> entries are meaningful; seeded bins hold zeros).</summary>
        public double[] Seed;
    }

    /// <summary>One landmark of the saved grip curve: an angle, the curve's smoothed lateral G there, and the
    /// observation count and spread of a bin there (interpolated between the two bins around it).</summary>
    public sealed class GripCurveKnot
    {
        public double Deg;
        public double G;
        public int Samples;
        public double Spread;
    }

    /// <summary>
    /// The grip curve saved as SINGLE VALUES (owner, 2026-11-21: "use single values instead arrays for the
    /// parameters"; docs\parameters-file-format-report.md): which bins are populated (two bit masks), the peak, and
    /// a handful of landmarks (<see cref="GripCurveKnot"/>) - the first and last populated bin, the plateau around
    /// the peak and its outside neighbours, where the rising side falls below 95/90/85/75/60/50/40/25/10% of the peak
    /// and where the falling side drops below 90/75%. <see cref="GripCurveBins.ImportSummary"/> rebuilds every
    /// populated bin from straight lines between the landmarks (counts on a log scale), so a restart continues close
    /// to, not identical with, an uninterrupted run. A null landmark was not reached.
    /// </summary>
    public sealed class GripCurveSummary
    {
        /// <summary>The first and last populated bin; -1 when no bin is populated.</summary>
        public int FirstBin = -1;
        public int LastBin = -1;
        /// <summary>Which bins are populated, one bit per bin: bins 0-63 in <see cref="Populated0"/>, 64-119 in
        /// <see cref="Populated1"/> (bit k = bin 64 + k). Holes stay holes after the rebuild.</summary>
        public ulong Populated0;
        public ulong Populated1;
        /// <summary>Every observation in the curve, bins still seeding included (saturating per bin).</summary>
        public long TotalSamples;
        /// <summary>The highest smoothed lateral G.</summary>
        public double PeakG;
        public GripCurveKnot First;
        public GripCurveKnot Rise10;
        public GripCurveKnot Rise25;
        public GripCurveKnot Rise40;
        public GripCurveKnot Rise50;
        public GripCurveKnot Rise60;
        public GripCurveKnot Rise75;
        public GripCurveKnot Rise85;
        public GripCurveKnot Rise90;
        public GripCurveKnot Rise95;
        /// <summary>The populated bin just outside the plateau's low edge.</summary>
        public GripCurveKnot LoOutside;
        /// <summary>The plateau's edges: the bin centres around the peak at or above 97% of it.</summary>
        public GripCurveKnot PlateauLo;
        public GripCurveKnot PlateauHi;
        public GripCurveKnot HiOutside;
        public GripCurveKnot Fall90;
        public GripCurveKnot Fall75;
        public GripCurveKnot Last;
    }

    /// <summary>
    /// The grip curve half of the Slip Angle window learner (docs\slip-angle-design.md 9.5 steps 2-4,
    /// 9.5.5): |lateral G| against slip angle in 0.25 deg bins over 0-30 deg.
    /// <para><b>Per-bin decaying P90 (estimator choice).</b> Each bin runs a stochastic-approximation
    /// (Robbins-Monro) quantile tracker: <c>q += gain * (0.9 - [x &lt; q])</c>, with
    /// <c>gain = eta * StepScale * spread</c>, <c>eta = max(1/n, MinEta)</c> and <c>spread</c> the
    /// bin's own decaying mean |x - q|. The tracker is SEEDED rather than started from one sample: a
    /// bin buffers its first <see cref="MinSamplesPerBin"/> observations and, when it reaches that count
    /// (the moment it starts to count toward the curve), q is set to their exact P90 and spread to their
    /// mean |x - q|, so an outlier first sample cannot bias the bin. From then on (1/n) it converges
    /// quickly like a running quantile;
    /// afterwards the floor <see cref="MinEta"/> makes it decay with a memory of roughly
    /// 1/<see cref="MinEta"/> of THAT bin's observations. Chosen over a per-bin histogram because the
    /// state is three numbers per bin (360 per learner; a 0-4 g histogram would be ~10,000 values per
    /// learner, and there are nine learners per car key to persist). It is deterministic, bounded,
    /// scale-free (the step follows the bin's own spread), and advances only when the bin itself is
    /// observed: unvisited bins never decay (9.5.5), and drift frames in one bin cannot move another
    /// bin. Within a bin the guarantee is that of a P90: the at-limit level holds while at-limit frames
    /// stay above ~10% of that bin's recent (~1/<see cref="MinEta"/>) observations; once a bin's recent
    /// history is more than ~90% sub-limit frames its P90 erodes toward them.</para>
    /// <para><b>Holes.</b> The plateau and rising-side searches bridge a single under-populated bin
    /// (fewer than <see cref="MinSamplesPerBin"/> samples) between populated bins; two or more
    /// consecutive under-populated bins end the run.</para>
    /// <para><b>Smoothing.</b> Before the peak/plateau search each populated bin (at least
    /// <see cref="MinSamplesPerBin"/> samples) is replaced by the median of itself and its populated
    /// immediate neighbours (3-bin moving median, the spec's "max-of-median": <see cref="PeakG"/> is the
    /// max of these medians). With only one populated neighbour the lower of the two is used (a single
    /// spiking bin is still rejected); an isolated bin keeps its own value.</para>
    /// Not thread-safe: the data thread owns it.
    /// </summary>
    public sealed class GripCurveBins
    {
        public const double BinWidthDeg = 0.25;
        public const double MaxAlphaDeg = 30.0;
        public const int BinCount = 120;
        /// <summary>A bin counts toward the curve from this many observations (9.5 step 3).</summary>
        public const int MinSamplesPerBin = 20;
        /// <summary>The tracked quantile.</summary>
        public const double Quantile = 0.9;
        /// <summary>Floor of the per-observation gain: memory of ~500 of that bin's own observations.</summary>
        public const double MinEta = 1.0 / 500.0;
        /// <summary>Quantile step in units of the bin's spread.</summary>
        public const double StepScale = 3.0;
        /// <summary>Inputs above this are clamped (bounds the influence of a single spike; crash
        /// rejection proper is the window learner's job).</summary>
        public const double MaxPlausibleG = 8.0;
        /// <summary>Plateau: bins at or above this share of the peak (hidden constant, 9.5 step 4).</summary>
        public const double PlateauShare = 0.97;
        /// <summary>Min: where the rising side first reaches this share of the peak.</summary>
        public const double MinShare = 0.90;
        /// <summary>Fallback Min = Best x this when the rising side is not populated.</summary>
        public const double FallbackMinToBest = 5.0 / 8.0;
        /// <summary>Shape check: the top bin is within this share of the peak...</summary>
        public const double RisingTopShare = 0.99;
        /// <summary>...and at least this share of the peak above the curve <see cref="RisingLookbackDeg"/> lower.</summary>
        public const double RisingMinGainShare = 0.02;
        public const double RisingLookbackDeg = 1.0;

        private readonly int[] _counts = new int[BinCount];
        private readonly double[] _p90 = new double[BinCount];
        private readonly double[] _spread = new double[BinCount];
        private readonly double[] _seed = new double[BinCount * MinSamplesPerBin];
        private readonly double[] _smoothed = new double[BinCount];
        private bool _dirty = true;

        /// <summary>Center of bin <paramref name="bin"/>, degrees.</summary>
        public static double BinCenterDeg(int bin) => (bin + 0.5) * BinWidthDeg;

        /// <summary>Observations recorded in bin <paramref name="bin"/> (0 when out of range).</summary>
        public int SampleCount(int bin) => bin >= 0 && bin < BinCount ? _counts[bin] : 0;

        /// <summary>Records one valid frame. Non-finite or negative values, and alpha above
        /// <see cref="MaxAlphaDeg"/>, are ignored.</summary>
        public void Observe(double alphaDeg, double absLatG)
        {
            if (!ClampMath.IsFinite(alphaDeg) || alphaDeg < 0.0 || alphaDeg > MaxAlphaDeg) return;
            if (!ClampMath.IsFinite(absLatG) || absLatG < 0.0) return;
            double x = Math.Min(absLatG, MaxPlausibleG);
            int i = Math.Min((int)(alphaDeg / BinWidthDeg), BinCount - 1);
            _dirty = true;

            int n = _counts[i];
            if (n < MinSamplesPerBin)
            {
                _seed[i * MinSamplesPerBin + n] = x;
                _counts[i] = ++n;
                if (n == MinSamplesPerBin) SeedBin(i);
                return;
            }

            if (n < GripLearner.SampleCountSaturationCap) n++;
            _counts[i] = n;
            double eta = Math.Max(1.0 / n, MinEta);
            double q = _p90[i];
            _spread[i] += eta * (Math.Abs(x - q) - _spread[i]);
            double gain = eta * StepScale * _spread[i];
            q += x < q ? -gain * (1.0 - Quantile) : gain * Quantile;
            _p90[i] = Math.Max(0.0, q);
        }

        /// <summary>Seeds bin <paramref name="i"/>'s tracker from its buffered first samples: q = their
        /// exact P90 (linear between order statistics), spread = their mean |x - q|.</summary>
        private void SeedBin(int i)
        {
            int offset = i * MinSamplesPerBin;
            var sorted = new double[MinSamplesPerBin];
            Array.Copy(_seed, offset, sorted, 0, MinSamplesPerBin);
            Array.Sort(sorted);
            double pos = Quantile * (MinSamplesPerBin - 1);
            int k = (int)pos;
            double q = k + 1 < MinSamplesPerBin ? sorted[k] + (pos - k) * (sorted[k + 1] - sorted[k]) : sorted[k];
            double spread = 0.0;
            for (int s = 0; s < MinSamplesPerBin; s++) spread += Math.Abs(sorted[s] - q);
            _p90[i] = q;
            _spread[i] = spread / MinSamplesPerBin;
            Array.Clear(_seed, offset, MinSamplesPerBin);
        }

        /// <summary>Max of the smoothed P90 over populated bins; 0 with none.</summary>
        public double PeakG
        {
            get
            {
                Smooth();
                double peak = 0.0;
                for (int i = 0; i < BinCount; i++)
                    if (IsPopulated(i) && _smoothed[i] > peak) peak = _smoothed[i];
                return peak;
            }
        }

        /// <summary>
        /// Best and Min from the curve restricted to bins centred below <paramref name="maxDeg"/>
        /// (9.5 step 4). The peak used is the highest smoothed bin below Max (ruling R20); the plateau
        /// is the contiguous populated run around it (single-bin holes bridged) at or above
        /// <see cref="PlateauShare"/> of that peak and Best is its midpoint. Min is where the rising
        /// side (walking down from the plateau over populated bins, single-bin holes bridged) first
        /// reaches <see cref="MinShare"/> of the peak, interpolated
        /// between bin centres; when no populated bin below that level is reached, Min =
        /// Best x <see cref="FallbackMinToBest"/>. Gaps of <see cref="SlipAngleWindow.MinGapDeg"/> are
        /// then enforced downward (Best &lt;= Max - gap, Min &lt;= Best - gap). False when no populated
        /// bin lies below Max or no valid window results.
        /// </summary>
        public bool TryBestAndMin(double maxDeg, out double best, out double min)
        {
            best = double.NaN;
            min = double.NaN;
            if (!ClampMath.IsFinite(maxDeg) || maxDeg <= 0.0) return false;
            Smooth();

            int peakBin = -1;
            double peak = 0.0;
            for (int i = 0; i < BinCount && BinCenterDeg(i) < maxDeg; i++)
                if (IsPopulated(i) && _smoothed[i] > peak) { peak = _smoothed[i]; peakBin = i; }
            if (peakBin < 0) return false;

            double plateauFloor = PlateauShare * peak;
            int lo = peakBin, hi = peakBin;
            for (int next; (next = NextPopulated(lo, -1, maxDeg)) >= 0 && _smoothed[next] >= plateauFloor;) lo = next;
            for (int next; (next = NextPopulated(hi, +1, maxDeg)) >= 0 && _smoothed[next] >= plateauFloor;) hi = next;
            double b = 0.5 * (BinCenterDeg(lo) + BinCenterDeg(hi));

            double minFloor = MinShare * peak;
            int above = lo, j;
            while ((j = NextPopulated(above, -1, maxDeg)) >= 0 && _smoothed[j] >= minFloor) above = j;
            double m;
            if (j >= 0)
            {
                double gBelow = _smoothed[j], gAbove = _smoothed[above];
                double t = gAbove > gBelow ? (minFloor - gBelow) / (gAbove - gBelow) : 1.0;
                m = BinCenterDeg(j) + ClampMath.To01(t) * (BinCenterDeg(above) - BinCenterDeg(j));
            }
            else
            {
                m = b * FallbackMinToBest;
            }

            b = Math.Min(b, maxDeg - SlipAngleWindow.MinGapDeg);
            m = Math.Min(m, b - SlipAngleWindow.MinGapDeg);
            if (!new SlipAngleWindow(m, b, maxDeg).IsValid) return false;
            best = b;
            min = m;
            return true;
        }

        /// <summary>
        /// Shape check (9.5.5): true when the curve is still clearly rising at its highest populated
        /// bin - that bin is within <see cref="RisingTopShare"/> of <see cref="PeakG"/> and at least
        /// <see cref="RisingMinGainShare"/> of the peak above the highest populated bin
        /// <see cref="RisingLookbackDeg"/> or more lower. Also true when there is too little curve to
        /// show a fall-off (no populated bin, or none that far below the top): a peak cannot be
        /// confirmed from that.
        /// </summary>
        public bool IsStillRisingAtTop
        {
            get
            {
                Smooth();
                int top = -1;
                for (int i = BinCount - 1; i >= 0; i--)
                    if (IsPopulated(i)) { top = i; break; }
                if (top < 0) return true;

                int lookbackBins = (int)Math.Round(RisingLookbackDeg / BinWidthDeg);
                int reference = -1;
                for (int i = top - lookbackBins; i >= 0; i--)
                    if (IsPopulated(i)) { reference = i; break; }
                if (reference < 0) return true;

                double peak = PeakG;
                return _smoothed[top] >= RisingTopShare * peak
                    && _smoothed[top] - _smoothed[reference] >= RisingMinGainShare * peak;
            }
        }

        /// <summary>
        /// The curve's expected |lateral G| at <paramref name="alphaDeg"/>: linear between populated
        /// bin centres (smoothed P90), linear from (0, 0) below the first populated bin, held at the
        /// last value above the highest. NaN with no populated bin or a non-finite/negative alpha.
        /// </summary>
        public double ExpectedLatG(double alphaDeg)
        {
            if (!ClampMath.IsFinite(alphaDeg) || alphaDeg < 0.0) return double.NaN;
            Smooth();
            int prev = -1;
            for (int i = 0; i < BinCount; i++)
            {
                if (!IsPopulated(i)) continue;
                double c = BinCenterDeg(i);
                if (alphaDeg <= c)
                {
                    if (prev < 0) return _smoothed[i] * alphaDeg / c;
                    double pc = BinCenterDeg(prev);
                    double t = (alphaDeg - pc) / (c - pc);
                    return _smoothed[prev] + t * (_smoothed[i] - _smoothed[prev]);
                }
                prev = i;
            }
            return prev < 0 ? double.NaN : _smoothed[prev];
        }

        /// <summary>A copy of the full state.</summary>
        public GripCurveBinsState Export() => new GripCurveBinsState
        {
            Counts = (int[])_counts.Clone(),
            P90 = (double[])_p90.Clone(),
            Spread = (double[])_spread.Clone(),
            Seed = (double[])_seed.Clone()
        };

        /// <summary>Restores <see cref="Export"/>'s state exactly. A null state or wrong array lengths
        /// resets to empty; a bin with a non-positive count, a non-finite/negative value, or (still
        /// seeding) a non-finite/negative buffered sample is reset; counts above the saturation cap
        /// are clamped to it.</summary>
        public void Import(GripCurveBinsState state)
        {
            Array.Clear(_counts, 0, BinCount);
            Array.Clear(_p90, 0, BinCount);
            Array.Clear(_spread, 0, BinCount);
            Array.Clear(_seed, 0, _seed.Length);
            _dirty = true;
            if (state?.Counts == null || state.P90 == null || state.Spread == null || state.Seed == null) return;
            if (state.Counts.Length != BinCount || state.P90.Length != BinCount || state.Spread.Length != BinCount
                || state.Seed.Length != _seed.Length) return;

            for (int i = 0; i < BinCount; i++)
            {
                int n = state.Counts[i];
                if (n <= 0) continue;
                if (n < MinSamplesPerBin)
                {
                    int offset = i * MinSamplesPerBin;
                    bool ok = true;
                    for (int k = 0; k < n && ok; k++)
                        ok = ClampMath.IsFinite(state.Seed[offset + k]) && state.Seed[offset + k] >= 0.0;
                    if (!ok) continue;
                    Array.Copy(state.Seed, offset, _seed, offset, n);
                    _counts[i] = n;
                    continue;
                }
                double q = state.P90[i], s = state.Spread[i];
                if (!ClampMath.IsFinite(q) || q < 0.0 || !ClampMath.IsFinite(s) || s < 0.0) continue;
                _counts[i] = Math.Min(n, GripLearner.SampleCountSaturationCap);
                _p90[i] = q;
                _spread[i] = s;
            }
        }

        /// <summary>The curve as single values (see <see cref="GripCurveSummary"/>). Side-effect-free.</summary>
        public GripCurveSummary ExportSummary()
        {
            Smooth();
            var s = new GripCurveSummary();
            long total = 0;
            for (int i = 0; i < BinCount; i++) total += _counts[i];
            s.TotalSamples = total;

            int first = -1, last = -1, peakBin = -1;
            double peak = 0.0;
            for (int i = 0; i < BinCount; i++)
            {
                if (!IsPopulated(i)) continue;
                if (first < 0) first = i;
                last = i;
                if (i < 64) s.Populated0 |= 1UL << i;
                else s.Populated1 |= 1UL << (i - 64);
                if (_smoothed[i] > peak) { peak = _smoothed[i]; peakBin = i; }
            }
            if (first < 0) return s;
            s.FirstBin = first;
            s.LastBin = last;
            s.PeakG = peak;
            s.First = KnotAt(first);
            s.Last = KnotAt(last);
            if (peakBin < 0) return s;

            const double anyDeg = double.MaxValue;
            double plateauFloor = PlateauShare * peak;
            int lo = peakBin, hi = peakBin, next;
            while ((next = NextPopulated(lo, -1, anyDeg)) >= 0 && _smoothed[next] >= plateauFloor) lo = next;
            if (next >= 0) s.LoOutside = KnotAt(next);
            while ((next = NextPopulated(hi, +1, anyDeg)) >= 0 && _smoothed[next] >= plateauFloor) hi = next;
            if (next >= 0) s.HiOutside = KnotAt(next);
            s.PlateauLo = KnotAt(lo);
            s.PlateauHi = KnotAt(hi);

            s.Rise95 = Crossing(lo, -1, 0.95 * peak);
            s.Rise90 = Crossing(lo, -1, 0.90 * peak);
            s.Rise85 = Crossing(lo, -1, 0.85 * peak);
            s.Rise75 = Crossing(lo, -1, 0.75 * peak);
            s.Rise60 = Crossing(lo, -1, 0.60 * peak);
            s.Rise50 = Crossing(lo, -1, 0.50 * peak);
            s.Rise40 = Crossing(lo, -1, 0.40 * peak);
            s.Rise25 = Crossing(lo, -1, 0.25 * peak);
            s.Rise10 = Crossing(lo, -1, 0.10 * peak);
            s.Fall90 = Crossing(hi, +1, 0.90 * peak);
            s.Fall75 = Crossing(hi, +1, 0.75 * peak);
            return s;
        }

        private GripCurveKnot KnotAt(int bin) => new GripCurveKnot
        {
            Deg = BinCenterDeg(bin), G = _smoothed[bin], Samples = _counts[bin], Spread = _spread[bin],
        };

        /// <summary>Walking from <paramref name="from"/> in <paramref name="dir"/> over populated bins (single holes
        /// bridged), where the smoothed curve first drops below <paramref name="level"/>: the angle interpolated
        /// between the two bin centres, G = the level, count and spread interpolated. Null when the walk ends first.</summary>
        private GripCurveKnot Crossing(int from, int dir, double level)
        {
            int at = from, j;
            while ((j = NextPopulated(at, dir, double.MaxValue)) >= 0 && _smoothed[j] >= level) at = j;
            if (j < 0) return null;
            double gIn = _smoothed[at], gOut = _smoothed[j];
            double t = ClampMath.To01(gIn > gOut ? (gIn - level) / (gIn - gOut) : 1.0);
            return new GripCurveKnot
            {
                Deg = BinCenterDeg(at) + t * (BinCenterDeg(j) - BinCenterDeg(at)),
                G = level,
                Samples = (int)Math.Round(Math.Exp(Math.Log(_counts[at]) + t * (Math.Log(_counts[j]) - Math.Log(_counts[at])))),
                Spread = _spread[at] + t * (_spread[j] - _spread[at]),
            };
        }

        /// <summary>
        /// Rebuilds the bins from <see cref="ExportSummary"/>: every populated bin gets its P90 and spread on straight
        /// lines through the landmarks and its observation count on a log scale between them (held flat beyond the
        /// first and last landmark). Bins not populated at the save - bins that were still seeding included - start
        /// empty. A null or unusable summary resets to empty.
        /// </summary>
        public void ImportSummary(GripCurveSummary s)
        {
            Array.Clear(_counts, 0, BinCount);
            Array.Clear(_p90, 0, BinCount);
            Array.Clear(_spread, 0, BinCount);
            Array.Clear(_seed, 0, _seed.Length);
            _dirty = true;
            if (s == null || s.FirstBin < 0 || s.LastBin < s.FirstBin || s.LastBin >= BinCount) return;
            if (!Ok(s.PeakG) || !(s.PeakG > 0.0)) return;

            var knots = new System.Collections.Generic.SortedDictionary<double, GripCurveKnot>();
            foreach (GripCurveKnot k in new[] { s.First, s.Rise10, s.Rise25, s.Rise40, s.Rise50, s.Rise60, s.Rise75, s.Rise85,
                         s.Rise90, s.Rise95, s.LoOutside, s.PlateauLo, s.PlateauHi, s.HiOutside, s.Fall90, s.Fall75, s.Last })
            {
                if (k == null || !Ok(k.Deg) || !(k.Deg > 0.0) || k.Deg > MaxAlphaDeg || !Ok(k.G) || !Ok(k.Spread)) continue;
                knots[Math.Round(k.Deg, 9)] = k;
            }
            if (knots.Count == 0) return;
            var x = new double[knots.Count];
            var g = new double[knots.Count];
            var ln = new double[knots.Count];
            var sp = new double[knots.Count];
            int n = 0;
            foreach (System.Collections.Generic.KeyValuePair<double, GripCurveKnot> kv in knots)
            {
                x[n] = kv.Key;
                g[n] = Math.Min(kv.Value.G, MaxPlausibleG);
                ln[n] = Math.Log(Math.Min(Math.Max(kv.Value.Samples, MinSamplesPerBin), GripLearner.SampleCountSaturationCap));
                sp[n] = kv.Value.Spread;
                n++;
            }

            bool hasMask = s.Populated0 != 0 || s.Populated1 != 0;
            for (int i = s.FirstBin; i <= s.LastBin; i++)
            {
                if (hasMask && ((i < 64 ? s.Populated0 >> i : s.Populated1 >> (i - 64)) & 1UL) == 0) continue;
                double c = BinCenterDeg(i);
                int k = 1;
                while (k < n && x[k] < c) k++;
                double t;
                int a, b;
                if (c <= x[0]) { a = b = 0; t = 0.0; }
                else if (k >= n) { a = b = n - 1; t = 0.0; }
                else { a = k - 1; b = k; t = (c - x[a]) / (x[b] - x[a]); }
                _p90[i] = Math.Max(0.0, g[a] + t * (g[b] - g[a]));
                _spread[i] = Math.Max(0.0, sp[a] + t * (sp[b] - sp[a]));
                int count = (int)Math.Round(Math.Exp(ln[a] + t * (ln[b] - ln[a])));
                _counts[i] = Math.Min(Math.Max(count, MinSamplesPerBin), GripLearner.SampleCountSaturationCap);
            }
        }
        private static bool Ok(double v) => ClampMath.IsFinite(v) && v >= 0.0;

        private bool IsPopulated(int bin) => _counts[bin] >= MinSamplesPerBin;

        /// <summary>The next populated bin from <paramref name="from"/> in direction
        /// <paramref name="dir"/>, bridging at most one under-populated bin and staying centred below
        /// <paramref name="maxDeg"/>; -1 when there is none.</summary>
        private int NextPopulated(int from, int dir, double maxDeg)
        {
            for (int step = 1; step <= 2; step++)
            {
                int k = from + dir * step;
                if (k < 0 || k >= BinCount || BinCenterDeg(k) >= maxDeg) return -1;
                if (IsPopulated(k)) return k;
            }
            return -1;
        }

        private void Smooth()
        {
            if (!_dirty) return;
            _dirty = false;
            for (int i = 0; i < BinCount; i++)
            {
                if (!IsPopulated(i)) { _smoothed[i] = 0.0; continue; }
                double v = _p90[i];
                bool hasL = i > 0 && IsPopulated(i - 1);
                bool hasR = i + 1 < BinCount && IsPopulated(i + 1);
                if (hasL && hasR) _smoothed[i] = Median3(_p90[i - 1], v, _p90[i + 1]);
                else if (hasL) _smoothed[i] = Math.Min(v, _p90[i - 1]);
                else if (hasR) _smoothed[i] = Math.Min(v, _p90[i + 1]);
                else _smoothed[i] = v;
            }
        }

        private static double Median3(double a, double b, double c) =>
            Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }
}

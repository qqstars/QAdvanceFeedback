using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>The persisted state of one 40 km/h speed band (RuntimeDocument v13 <c>SlipAngleSpeedBands</c>).</summary>
    public sealed class SlipAngleSpeedBandState
    {
        /// <summary>The band's lower edge in km/h (0, 40, 80, ...; the last band is open-ended).</summary>
        public int LowKmh;
        /// <summary>Running mean of the lateral G of the band's accepted crossings (0 = none yet).</summary>
        public double MeanG;
        /// <summary>Crossings in <see cref="MeanG"/>.</summary>
        public long SampleCount;
        /// <summary>Crossings that fell within 15% of the mean (3 = matured).</summary>
        public long CountedCount;
        /// <summary>Mean of the higher crossings rejected so far (0 = none).</summary>
        public double ChallengerMeanG;
        public long ChallengerCount;
        /// <summary>The band's peak lateral G (sustained over 10 frames, slowly decaying) - the 85% bar.</summary>
        public double PeakG;
        /// <summary>Learnable seconds driven in the band (saturating): under <see cref="SlipAngleSpeedBands.YoungSeconds"/>
        /// its peak is no reference yet (the fast Slip Angle learning weighs such a crossing at 10%).</summary>
        public double ReferenceSeconds;
    }

    /// <summary>The persisted speed bands of one (game, car, source, surface) - (game, car) in a file before 2026-11-22.</summary>
    public sealed class SlipAngleSpeedBandSetState
    {
        public List<SlipAngleSpeedBandState> Bands = new List<SlipAngleSpeedBandState>();
    }

    /// <summary>
    /// SPEED-AWARE LATERAL G for the Slip Angle learner (owner-approved design, 2026-11-21;
    /// docs\slip-angle-speed-bands-report.md). In a downforce car the grip limit rises with speed, so raw
    /// lateral G mixes speeds and the slip-angle grip curve keeps rising (F1 never confirmed). This class
    /// learns, per (game, car) and 40 km/h band, the lateral G of the car's crossings - the corners taken
    /// at the limit - and exposes <see cref="Scale"/>: the highest band's mean over this speed's mean, which
    /// the processor multiplies lateral G by before the slip-angle learner and the surface classifier see it.
    /// <para><b>Crossings.</b> The same <see cref="CrossingGate"/> as the learner (<see cref="CrossingGateParameters.SlipAngle"/>)
    /// on the Plugin Internal All angle, with "at the limit" = lateral G at least 85% of the band's own
    /// <see cref="SlipAngleSpeedBandState.PeakG"/>; its lateral G is the gate's walked-back G peak. A crossing
    /// is applied only after <see cref="ImpactGuardParameters.RetroFrames"/> further learnable frames, so an
    /// impact right after it drops it.</para>
    /// <para><b>Acceptance (owner's staged rule).</b> An empty band takes the first crossing as its mean. Then
    /// a crossing is accepted within 45% of the mean while nothing has been counted, 30% while 1-2 have, 15%
    /// once matured (3 counted); it is counted when within 15%. A crossing above the band is a challenger:
    /// a second one within 15% of it replaces the band. A crossing below it is a corner not at the limit and
    /// is dropped. The mean is the plain average for the first <see cref="MeanWindow"/> crossings, then
    /// follows newer ones with weight 1/<see cref="MeanWindow"/>.</para>
    /// <para><b>PER GAME, CAR, SOURCE AND SURFACE, NEVER BORROWED (owner, 2026-11-22;
    /// <see cref="SlipAngleBorrowRules.SpeedBandScope"/>).</b> Each surface learns its own bands - gravel's lateral G is
    /// typically 60-80% of tarmac's, so tarmac's bands would keep gravel from ever reaching 85% of the band peak and
    /// learning. A brand-new key starts empty (scale 1). Bands saved per (game, car) before this move to that car's
    /// Sealed key of the Plugin Internal source they were learned from, the first time it is used (<see cref="Import"/>).</para>
    /// <para><b>Scale</b> uses MATURED bands only (the owner's
    /// rule: a band's mean becomes its speed lateral G at the 3rd counted crossing) - a provisional seed, one
    /// light corner, would otherwise scale its speed by up to 2.5 (measured on the F1 log). It is 1 until a
    /// band matures, so a cold car and a car without downforce behave as before. Not thread-safe: the data thread owns it.</para>
    /// </summary>
    public sealed class SlipAngleSpeedBands
    {
        public const int BandWidthKmh = 40;
        /// <summary>Bands 0..8; band 8 is 320 km/h and above.</summary>
        public const int BandCount = 9;
        /// <summary>Below this speed nothing is learned and the scale is 1.</summary>
        public const double MinSpeedKmh = 30.0;
        public const double CandidateShareOfPeak = 0.85;
        public const double ToleranceUncounted = 0.45;
        public const double ToleranceCounting = 0.30;
        public const double ToleranceMatured = 0.15;
        public const double CountTolerance = 0.15;
        public const long MaturedCount = 3;
        public const long ChallengersToReplace = 2;
        public const long MeanWindow = 20;
        /// <summary>The band peak decays by this fraction per second of learnable time in the band.</summary>
        public const double PeakDecayPerSecond = 1.0 / 600.0;
        /// <summary>The band peak is the lowest lateral G over this many consecutive frames in the band (1/6 s
        /// at 60 Hz): on the F1 log it matches each band's 98th percentile, where 3 frames let kerb and
        /// transition spikes through (160-200 km/h: 4.38 g vs a 3.67 g p98).</summary>
        public const int SustainFrames = 10;
        public const double MaxScale = 2.5;
        /// <summary>Above this a lateral G is not a corner (the impact guard's own bound is looser).</summary>
        public const double MaxPlausibleG = 8.0;

        private static readonly int RetroFrames = ImpactGuardParameters.SlipAngle.RetroFrames;
        private const double NominalFrameSeconds = 1.0 / 60.0;

        private readonly Dictionary<string, BandSet> _sets = new Dictionary<string, BandSet>(StringComparer.Ordinal);
        private bool _changed;

        /// <summary>The storage key of a (game, car, source, surface).</summary>
        public static string MakeKey(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket) =>
            ColdStartTierResolver.Key(gameId ?? string.Empty, carId ?? string.Empty, sourceIdentity ?? string.Empty,
                KeyedSlipAngleLearner.BucketName(bucket));

        /// <summary>The (game, car) key bands were saved under before 2026-11-22.</summary>
        public static string LegacyKey(string gameId, string carId) =>
            ColdStartTierResolver.Key(gameId ?? string.Empty, carId ?? string.Empty, string.Empty);

        /// <summary>Bands loaded from an older file, by <see cref="LegacyKey"/>, not yet moved (see <see cref="Set"/>).</summary>
        private readonly Dictionary<string, BandSet> _legacy = new Dictionary<string, BandSet>(StringComparer.Ordinal);

        /// <summary>The band a speed falls in, or -1 below <see cref="MinSpeedKmh"/> / non-finite.</summary>
        public static int BandIndex(double speedKmh)
        {
            if (!ClampMath.IsFinite(speedKmh) || speedKmh < MinSpeedKmh) return -1;
            return Math.Min(BandCount - 1, (int)(speedKmh / BandWidthKmh));
        }

        /// <summary>True when something was learned since the last <see cref="Export"/>.</summary>
        public bool Changed => _changed;

        /// <summary>
        /// One frame. <paramref name="learnable"/>: the learner's own gates (learning allowed, impact guard
        /// allows, sealed surface). <paramref name="retroInvalidate"/>: the impact guard's retro flag.
        /// </summary>
        public void Observe(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, double speedKmh, double alphaDeg, double absLatG,
            bool learnable, bool retroInvalidate, double dt)
        {
            BandSet s = Set(gameId, carId, sourceIdentity, bucket, create: true);
            if (retroInvalidate) s.ClearPending();
            int b = BandIndex(speedKmh);
            double g = Math.Abs(absLatG);
            if (!learnable || b < 0 || !ClampMath.IsFinite(alphaDeg) || alphaDeg < 0.0 || !ClampMath.IsFinite(g) || g > MaxPlausibleG)
            {
                s.CancelFrameState();
                return;
            }
            double aged = ClampMath.IsFinite(dt) && dt > 0.0 ? dt : NominalFrameSeconds;
            SlipAngleSpeedBandState band = s.Bands[b];

            // The band peak: the lowest of the last SustainFrames frames in this band (a one-frame spike
            // never raises it), decaying slowly, never below the band's mean.
            if (s.SustainBand != b) { s.SustainBand = b; s.SustainCount = 0; }
            s.Sustain[s.SustainCount % SustainFrames] = g;
            s.SustainCount++;
            if (band.PeakG > 0.0) band.PeakG = Math.Max(band.PeakG * (1.0 - PeakDecayPerSecond * aged), band.MeanG);
            if (band.ReferenceSeconds < ReferenceSecondsCap) band.ReferenceSeconds = Math.Min(ReferenceSecondsCap, band.ReferenceSeconds + aged);
            if (s.SustainCount >= SustainFrames)
            {
                double sustained = s.Sustain[0];
                for (int i = 1; i < SustainFrames; i++) sustained = Math.Min(sustained, s.Sustain[i]);
                if (sustained > band.PeakG) band.PeakG = sustained;
            }
            _changed = true;

            bool candidate = band.PeakG > 0.0 && g >= CandidateShareOfPeak * band.PeakG;
            s.Gate.Observe(alphaDeg, g, g, candidate, aged, 0);

            if (s.PendingFrames > 0 && --s.PendingFrames == 0)
            {
                ObserveCrossing(s.Bands[s.PendingBand], s.PendingG);
                s.ClearPending();
            }
            if (s.Gate.CrossedThisFrame)
            {
                double crossingG = s.Gate.TeachingFallbackBasis(g);
                if (ClampMath.IsFinite(crossingG) && crossingG > 0.0)
                {
                    s.PendingBand = b;
                    s.PendingG = crossingG;
                    s.PendingFrames = RetroFrames;
                }
            }
        }

        /// <summary>The owner's staged acceptance for one crossing's lateral G (see the class remarks).
        /// True when the crossing was accepted into the band.</summary>
        public static bool ObserveCrossing(SlipAngleSpeedBandState band, double g)
        {
            if (band == null || !ClampMath.IsFinite(g) || g <= 0.0) return false;
            if (band.SampleCount <= 0 || !(band.MeanG > 0.0))
            {
                band.MeanG = g;
                band.SampleCount = 1;
                band.CountedCount = 0;
                return true;
            }
            double tolerance = band.CountedCount >= MaturedCount ? ToleranceMatured
                : band.CountedCount >= 1 ? ToleranceCounting : ToleranceUncounted;
            double deviation = (g - band.MeanG) / band.MeanG;
            if (Math.Abs(deviation) <= tolerance)
            {
                if (Math.Abs(deviation) <= CountTolerance) band.CountedCount++;
                band.SampleCount++;
                band.MeanG += (g - band.MeanG) / Math.Min(band.SampleCount, MeanWindow);
                return true;
            }
            if (deviation < 0.0) return false;   // below the band: not at the limit

            if (band.ChallengerCount > 0 && band.ChallengerMeanG > 0.0
                && Math.Abs(g - band.ChallengerMeanG) / band.ChallengerMeanG <= CountTolerance)
            {
                band.ChallengerMeanG = (band.ChallengerMeanG * band.ChallengerCount + g) / (band.ChallengerCount + 1);
                band.ChallengerCount++;
            }
            else
            {
                band.ChallengerMeanG = g;
                band.ChallengerCount = 1;
            }
            if (band.ChallengerCount < ChallengersToReplace) return false;
            band.MeanG = band.ChallengerMeanG;
            band.SampleCount = band.ChallengerCount;
            band.CountedCount = band.ChallengerCount - 1;
            band.ChallengerMeanG = 0.0;
            band.ChallengerCount = 0;
            if (band.PeakG < band.MeanG) band.PeakG = band.MeanG;
            return true;
        }

        /// <summary>
        /// The factor lateral G is multiplied by at <paramref name="speedKmh"/>: the highest matured band mean
        /// over the mean at this speed (piecewise linear between the centres of the matured bands, flat outside
        /// them), in [1, <see cref="MaxScale"/>]. 1 below <see cref="MinSpeedKmh"/> or with no matured band.
        /// </summary>
        public double Scale(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, double speedKmh)
        {
            BandSet s = Set(gameId, carId, sourceIdentity, bucket, create: false);
            if (s == null || BandIndex(speedKmh) < 0) return 1.0;
            double highest = 0.0;
            double lowerCentre = double.NaN, lowerMean = 0.0, upperCentre = double.NaN, upperMean = 0.0;
            for (int i = 0; i < BandCount; i++)
            {
                SlipAngleSpeedBandState band = s.Bands[i];
                if (!IsMatured(band)) continue;
                if (band.MeanG > highest) highest = band.MeanG;
                double centre = i * BandWidthKmh + BandWidthKmh / 2.0;
                if (centre <= speedKmh) { lowerCentre = centre; lowerMean = band.MeanG; }
                else if (double.IsNaN(upperCentre)) { upperCentre = centre; upperMean = band.MeanG; }
            }
            if (!(highest > 0.0)) return 1.0;
            double reference;
            if (double.IsNaN(lowerCentre)) reference = upperMean;
            else if (double.IsNaN(upperCentre)) reference = lowerMean;
            else reference = lowerMean + (upperMean - lowerMean) * (speedKmh - lowerCentre) / (upperCentre - lowerCentre);
            if (!(reference > 0.0)) return 1.0;
            return ClampMath.Clamp(highest / reference, 1.0, MaxScale);
        }

        /// <summary>Seconds of driving in a band before its peak counts as a reference (owner, 2026-11-21).</summary>
        public const double YoungSeconds = 2.0;

        /// <summary>Cap for <see cref="SlipAngleSpeedBandState.ReferenceSeconds"/> (nothing reads past
        /// <see cref="YoungSeconds"/>; the cap only keeps the value bounded).</summary>
        public const double ReferenceSecondsCap = 3600.0;

        /// <summary>
        /// The band at <paramref name="speedKmh"/> as a reference for the fast Slip Angle learning, without
        /// allocating: its peak and learned mean lateral G, whether it is matured, and whether it is still young
        /// (under <see cref="YoungSeconds"/> of driving, or no peak yet). False below <see cref="MinSpeedKmh"/>.
        /// </summary>
        public bool TryReference(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, double speedKmh,
            out double peakG, out double meanG, out bool matured, out bool young)
        {
            peakG = meanG = 0.0;
            matured = false;
            young = true;
            int b = BandIndex(speedKmh);
            if (b < 0) return false;
            BandSet s = Set(gameId, carId, sourceIdentity, bucket, create: false);
            if (s == null) return true;
            SlipAngleSpeedBandState band = s.Bands[b];
            peakG = band.PeakG;
            meanG = band.MeanG;
            matured = IsMatured(band);
            young = !(band.PeakG > 0.0) || band.ReferenceSeconds < YoungSeconds;
            return true;
        }

        /// <summary>A band's mean is its speed lateral G once <see cref="MaturedCount"/> crossings were counted.</summary>
        public static bool IsMatured(SlipAngleSpeedBandState band) =>
            band != null && band.CountedCount >= MaturedCount && band.MeanG > 0.0;

        /// <summary>A copy of one band's state (null when the (game, car) is unknown) - diagnostics and tests.</summary>
        public SlipAngleSpeedBandState Band(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, int index)
        {
            BandSet s = Set(gameId, carId, sourceIdentity, bucket, create: false);
            if (s == null || index < 0 || index >= BandCount) return null;
            return Copy(s.Bands[index]);
        }

        /// <summary>A gap (game switch, pause): the frame-to-frame state starts over; learned bands stay.</summary>
        public void ResetFrameState()
        {
            foreach (BandSet s in _sets.Values) s.CancelFrameState();
        }

        // ---- persistence ------------------------------------------------------------------------------

        /// <summary>Every (game, car) with a learned band - fresh objects.</summary>
        public Dictionary<string, SlipAngleSpeedBandSetState> Export()
        {
            var d = new Dictionary<string, SlipAngleSpeedBandSetState>(_sets.Count + _legacy.Count, StringComparer.Ordinal);
            // Older bands not moved yet stay in the file under their old key until their car is driven.
            foreach (KeyValuePair<string, BandSet> kv in _sets.Concat(_legacy))
            {
                var state = new SlipAngleSpeedBandSetState();
                foreach (SlipAngleSpeedBandState band in kv.Value.Bands)
                    if (band.SampleCount > 0 || band.PeakG > 0.0 || band.ChallengerCount > 0) state.Bands.Add(Copy(band));
                if (state.Bands.Count > 0) d[kv.Key] = state;
            }
            _changed = false;
            return d;
        }

        /// <summary>Replaces everything with <paramref name="states"/> (null: empty). Bands with an unknown
        /// edge, a negative count or a non-finite value are skipped.</summary>
        public void Import(Dictionary<string, SlipAngleSpeedBandSetState> states)
        {
            _sets.Clear();
            _legacy.Clear();
            _changed = false;
            if (states == null) return;
            foreach (KeyValuePair<string, SlipAngleSpeedBandSetState> kv in states)
            {
                if (kv.Key == null || kv.Value?.Bands == null) continue;
                var s = new BandSet();
                foreach (SlipAngleSpeedBandState band in kv.Value.Bands)
                {
                    if (band == null || band.LowKmh < 0 || band.LowKmh % BandWidthKmh != 0) continue;
                    int i = band.LowKmh / BandWidthKmh;
                    if (i >= BandCount || !IsSane(band)) continue;
                    s.Bands[i] = Copy(band);
                }
                // A (game, car) key from before 2026-11-22: kept aside, moved to the car's Sealed key on first use.
                bool parsed = ColdStartTierResolver.TryParseKey(kv.Key, out string game, out _, out string src, out string surf);
                bool legacy = parsed && src.Length == 0 && surf.Length == 0;
                // A surface reached only by the old guessing (owner, 2026-11-22: never guess): dropped.
                if (parsed && !legacy && TryBucket(surf, out SurfaceBucket bucket) && SurfaceBucketMap.IsGuessedBucket(game, bucket))
                {
                    _changed = true;
                    continue;
                }
                if (legacy) _legacy[kv.Key] = s;
                else _sets[kv.Key] = s;
            }
        }

        private static bool TryBucket(string name, out SurfaceBucket bucket)
        {
            bucket = SurfaceBucket.Sealed;
            if (name == KeyedSlipAngleLearner.BucketName(SurfaceBucket.Sealed)) return true;
            if (name == KeyedSlipAngleLearner.BucketName(SurfaceBucket.Loose)) { bucket = SurfaceBucket.Loose; return true; }
            if (name == KeyedSlipAngleLearner.BucketName(SurfaceBucket.LowGrip)) { bucket = SurfaceBucket.LowGrip; return true; }
            return false;
        }

        private static bool IsSane(SlipAngleSpeedBandState b) =>
            b.SampleCount >= 0 && b.CountedCount >= 0 && b.ChallengerCount >= 0
            && Finite01Plus(b.MeanG) && Finite01Plus(b.ChallengerMeanG) && Finite01Plus(b.PeakG)
            && ClampMath.IsFinite(b.ReferenceSeconds) && b.ReferenceSeconds >= 0.0 && b.ReferenceSeconds <= ReferenceSecondsCap;

        private static bool Finite01Plus(double v) => ClampMath.IsFinite(v) && v >= 0.0 && v <= MaxPlausibleG;

        private static SlipAngleSpeedBandState Copy(SlipAngleSpeedBandState b) => new SlipAngleSpeedBandState
        {
            LowKmh = b.LowKmh,
            MeanG = b.MeanG,
            SampleCount = b.SampleCount,
            CountedCount = b.CountedCount,
            ChallengerMeanG = b.ChallengerMeanG,
            ChallengerCount = b.ChallengerCount,
            PeakG = b.PeakG,
            ReferenceSeconds = b.ReferenceSeconds,
        };

        private BandSet Set(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, bool create)
        {
            string key = MakeKey(gameId, carId, sourceIdentity, bucket);
            if (_sets.TryGetValue(key, out BandSet s)) return s;
            // MIGRATION, NOT BORROWING: bands saved per (game, car) before 2026-11-22 were learned on the sealed surface
            // from the Plugin Internal source - they become exactly that key's own bands, once.
            string legacyKey = LegacyKey(gameId, carId);
            if (bucket == SurfaceBucket.Sealed && IsPluginInternal(sourceIdentity) && _legacy.TryGetValue(legacyKey, out BandSet old))
            {
                _legacy.Remove(legacyKey);
                _sets[key] = old;
                _changed = true;
                return old;
            }
            if (!create) return null;
            s = new BandSet();
            _sets[key] = s;
            return s;
        }

        private static bool IsPluginInternal(string sourceIdentity) =>
            sourceIdentity == SlipAngleFrameProcessor.NativeIdentity || sourceIdentity == SlipAngleFrameProcessor.NativeRatioIdentity
            || sourceIdentity == SlipAngleFrameProcessor.FallbackIdentity;

        private sealed class BandSet
        {
            public readonly SlipAngleSpeedBandState[] Bands = new SlipAngleSpeedBandState[BandCount];
            public readonly CrossingGate Gate = new CrossingGate(CrossingGateParameters.SlipAngle)
            {
                MaxGCollapseFractionPerSecond = 0.0,
                MinSaturatedWheelsForRejection = 0,
                WalkBackGBandFraction = 0.10,
                NonCrossingWeight = 0.0,
            };
            public readonly double[] Sustain = new double[SustainFrames];
            public int SustainBand = -1;
            public int SustainCount;
            public int PendingFrames;
            public int PendingBand;
            public double PendingG;

            public BandSet()
            {
                for (int i = 0; i < BandCount; i++) Bands[i] = new SlipAngleSpeedBandState { LowKmh = i * BandWidthKmh };
            }

            public void ClearPending()
            {
                PendingFrames = 0;
                PendingG = 0.0;
            }

            public void CancelFrameState()
            {
                Gate.Reset();
                SustainBand = -1;
                SustainCount = 0;
                ClearPending();
            }
        }
    }
}

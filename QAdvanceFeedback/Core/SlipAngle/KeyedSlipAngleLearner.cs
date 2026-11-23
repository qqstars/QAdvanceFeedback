using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>The three learners per context (docs\slip-angle-design.md 9.5 intro).</summary>
    public enum SlipAngleTarget
    {
        /// <summary>Front axle (internal, k only).</summary>
        Front = 0,
        /// <summary>Rear axle (internal, k only).</summary>
        Rear = 1,
        /// <summary>Raw.All - its window IS the published key data set.</summary>
        All = 2,
    }

    /// <summary>
    /// The persisted state of one Slip Angle key (RuntimeDocument v12 <c>SlipAngleLearners</c>): the
    /// window learner itself plus what the keyed layer needs to restart exactly where it stopped.
    /// </summary>
    public sealed class SlipAngleKeyState
    {
        /// <summary>The window learner (its prior included).</summary>
        public SlipAngleLearnerState Learner;

        /// <summary>The window this key last PUBLISHED (degrees); all zero when unknown. It is the key's
        /// cold reference next session, so a restart with no new driving publishes it exactly.</summary>
        public double PublishedMin;
        public double PublishedBest;
        public double PublishedMax;

        /// <summary>The borrowing tier the key's cold reference came from (1-4; 0 = unknown).</summary>
        public int ColdTier;

        /// <summary>The bucket's front / rear balance k evidence (All-target keys only; null otherwise and in older
        /// files) - see <see cref="SlipAngleBalanceEstimator"/>.</summary>
        public SlipAngleBalanceState Balance;

        public bool ShouldSerializeBalance() => Balance != null;
    }

    /// <summary>
    /// The keyed Slip Angle learner (docs\slip-angle-design.md 9.3, 9.5 intro, 9.5.6-9): one
    /// <see cref="SlipAngleWindowLearner"/> per <c>(game, car, sourceIdentity, target, surfaceBucket)</c>.
    /// <para><b>Keys.</b> Built ONLY through <see cref="ColdStartTierResolver.Key"/>, with the target folded
    /// into the source segment: <c>game|#|car|#|source/target|#|surface</c> (target <c>front</c>/<c>rear</c>/<c>all</c>,
    /// surface <c>sealed</c>/<c>loose</c>/<c>lowgrip</c>), so the resolver's "same source" means "same source
    /// and same target" - a rear window is never borrowed for a front key.</para>
    /// <para><b>Learning</b> goes to the classifier's dominant bucket (<see cref="SurfaceBlend.Active"/>)
    /// only: <see cref="Observe"/> feeds that bucket's front, rear and All learners. <b>Publishing</b>
    /// blends the buckets' windows component-wise (Min, Best, Max) by the blend weights.</para>
    /// <para><b>Cold reference, latched per key.</b> When a key's learner is created its borrowing tier is
    /// resolved (<see cref="ColdStartTierResolver"/>; evidence = a PeakConfirmed learner, tie-break sample
    /// count = its maturity observations; the resolver's <c>ceiling</c> output is unused - the reference
    /// is the whole <see cref="SlipAngleWindowLearner.Published"/> window of the borrowed key's learner,
    /// its learned window, so borrowing never recurses) and that window becomes BOTH the learner's prior
    /// (so the 9.5.5 raise-only rule works against the borrowed Best, not the shipped one) and the key's
    /// cold reference; Tier 1 = <see cref="SlipAngleWindow.ShippedDefault"/>. Until the key confirms, a
    /// strictly BETTER tier appearing (another key confirming) re-priors it
    /// (<see cref="SlipAngleWindowLearner.SetPrior"/>). On the frame the key itself confirms, the cold
    /// reference is latched to the window it was publishing just before (prior x raise) and is never
    /// swapped again this session - in particular not when its own confirmation makes the resolver's
    /// Tier-4 guard withdraw the other-surface candidate.</para>
    /// <para><b>One key's window</b> (<see cref="BucketWindow"/>): unconfirmed = the learner's own
    /// published prior (borrowed prior x raise-only scale); confirmed = <c>ColdWarmBlend.Blend(cold,
    /// live.Published, w)</c> per component. <c>w</c> starts at 0 when the key confirms (or is imported) and
    /// moves toward its target at most <see cref="HotWeightSlewPerSecond"/> per second of observed time
    /// (so the published window is continuous across the confirmation frame), target =
    /// <see cref="HotWeightTarget"/>: <c>ConcaveCountConfidence(n, 10) x clamp01(WindowQuality / 0.5)</c>, n =
    /// crossings completed this session. It reaches 1 - the pure learned window - after 10 session
    /// crossings with a window quality of at least 0.5. A convex blend of two valid windows is valid.</para>
    /// <para><b>Restart.</b> <see cref="Export"/> stores each key's last published window and cold tier
    /// (<see cref="SlipAngleKeyState"/>); <see cref="Import"/> makes the published window the cold reference
    /// with w = 0, so a restart with no new driving publishes exactly what was last published, and new
    /// driving moves it on toward the learned window again.</para>
    /// <para><b>BORROWING (owner, 2026-11-22): every rule is in <see cref="SlipAngleBorrowRules"/>.</b> The cold
    /// window's candidates are filtered by <see cref="SlipAngleBorrowRules.WindowScope"/> before the resolver ranks
    /// them (a slip-ratio source: same game, any car or surface; an angle source: same game, car and source, another
    /// surface; never another game).</para>
    /// <para><b>k (9.3, owner 2026-11-22)</b> = each weighted bucket's <see cref="SlipAngleBalanceEstimator"/> (the
    /// median rear / front axle angle near the limit, pulled toward its starting value until enough evidence), blended
    /// across the buckets by weight in the log domain. The starting value is the k of the same game, car and source
    /// on another surface (<see cref="SlipAngleBorrowRules.KScope"/>), else 1. Clamped to [<see cref="KMin"/>,
    /// <see cref="KMax"/>]; slewed at most <see cref="KSlewPerSecond"/> per second in <see cref="Observe"/> - except
    /// while the dominant bucket has no k evidence of its own, when k IS its starting value at once (a brand-new key
    /// starts fresh, not eased from the previous surface). It replaces Best_rear / Best_front of the confirmed windows,
    /// which needed both axles' peaks confirmed and on F1 never got there.</para>
    /// <para><b>Maturity / tier</b> for the projection, on the All target: maturity = the weight-blended
    /// maturity of the buckets (each counted separately); tier = the dominant bucket's latched cold tier
    /// (resolved afresh for a key with no learner yet).</para>
    /// <para><b>Persistence (9.5.9).</b> <see cref="SaveIfDue"/> exports on the CALLING (data) thread at most
    /// every <see cref="PersistIntervalSeconds"/> and only after an observation, handing fresh objects
    /// (never mutated afterwards) to a sink (<c>RuntimeStore.SaveSlipAngleLearners</c>, in-memory cache);
    /// the store's timer thread only ever snapshots the cache, never this class.</para>
    /// Not thread-safe: the data thread owns it (window getters may re-prior an unconfirmed learner).
    /// </summary>
    public sealed class KeyedSlipAngleLearner
    {
        /// <summary>Owner, 2026-11-22: was 0.5 - the F1 log measures 0.29-0.46.</summary>
        public const double KMin = 0.3;
        public const double KMax = 2.0;
        public const double KSlewPerSecond = 0.2;
        public const double PersistIntervalSeconds = 5.0;
        /// <summary>Session crossings at which the count term of <see cref="HotWeightTarget"/> reaches 1.</summary>
        public const double CrossingsForFullTrust = 10.0;
        /// <summary>Window quality at which the quality term of <see cref="HotWeightTarget"/> reaches 1.</summary>
        public const double QualityForFullTrust = 0.5;
        /// <summary>Fastest change of the live-vs-cold weight, per second of observed time.</summary>
        public const double HotWeightSlewPerSecond = 0.1;

        private const char TargetSeparator = '/';
        private const double NominalFrameSeconds = 1.0 / 60.0;

        private static readonly SurfaceBucket[] AllBuckets = { SurfaceBucket.Sealed, SurfaceBucket.Loose, SurfaceBucket.LowGrip };

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, KState> _k = new Dictionary<string, KState>(StringComparer.Ordinal);
        /// <summary>The balance k evidence per bucket, keyed by the bucket's All-target key.</summary>
        private readonly Dictionary<string, SlipAngleBalanceEstimator> _balance = new Dictionary<string, SlipAngleBalanceEstimator>(StringComparer.Ordinal);
        private readonly ColdStartTierResolver _resolver = new ColdStartTierResolver();
        private readonly Func<string, bool> _hasEvidence;
        private readonly ColdStartTierResolver.TryGetReference _tryGetReference;
        private int _evidenceEpoch;
        private double _sinceSave;
        private bool _changedSinceSave;

        public KeyedSlipAngleLearner()
        {
            // A LENDER = any key with learning of its own and some confidence (owner, 2026-11-22; was a confirmed peak only,
            // which FH6 never reaches - so it never lent). What it lends is weighted by that confidence (ResolveFresh).
            _hasEvidence = key => _entries.TryGetValue(key, out Entry e) && LenderConfidence(e) > 0.0;
            _tryGetReference = TryGetReference;
        }

        private bool _crossingGateEnabled = true;

        /// <summary>The speed-aware lateral G bands (owner, 2026-11-21) - learned and read by the processor,
        /// saved with the windows (<see cref="SpeedBandSave"/>), loaded by the plugin. Data thread only.</summary>
        public SlipAngleSpeedBands SpeedBands { get; } = new SlipAngleSpeedBands();

        /// <summary>Where <see cref="Save"/> also hands the speed bands (RuntimeStore.SaveSlipAngleSpeedBands);
        /// null: not saved.</summary>
        public Action<Dictionary<string, SlipAngleSpeedBandSetState>> SpeedBandSave { get; set; }

        /// <summary>The Slip Angle crossing gate switch (settings <c>SlipAngle.CrossingGateEnabled</c>, default
        /// on), applied to every key's learner, existing and new - see
        /// <see cref="SlipAngleWindowLearner.CrossingGateEnabled"/>. Data thread only.</summary>
        public bool CrossingGateEnabled
        {
            get => _crossingGateEnabled;
            set
            {
                if (value == _crossingGateEnabled) return;
                _crossingGateEnabled = value;
                foreach (Entry e in _entries.Values) e.Learner.CrossingGateEnabled = value;
            }
        }

        // ---- keys -------------------------------------------------------------------------------------

        public static string TargetName(SlipAngleTarget target) =>
            target == SlipAngleTarget.Front ? "front" : target == SlipAngleTarget.Rear ? "rear" : "all";

        public static string BucketName(SurfaceBucket bucket) =>
            bucket == SurfaceBucket.Sealed ? "sealed" : bucket == SurfaceBucket.Loose ? "loose" : "lowgrip";

        /// <summary>The resolver's source segment: <c>sourceIdentity/target</c>.</summary>
        public static string SourceSegment(string sourceIdentity, SlipAngleTarget target) =>
            (sourceIdentity ?? string.Empty) + TargetSeparator + TargetName(target);

        /// <summary>The storage key, via <see cref="ColdStartTierResolver.Key"/>.</summary>
        public static string MakeKey(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket)
        {
            string key = ColdStartTierResolver.Key(gameId, carId, SourceSegment(sourceIdentity, target), BucketName(bucket));
            Debug.Assert(ColdStartTierResolver.TryParseKey(key, out _, out _, out string s, out string surf)
                && s == SourceSegment(sourceIdentity, target) && surf == BucketName(bucket),
                "A Slip Angle key must parse back to its four resolver parts: " + key);
            return key;
        }

        /// <summary>Splits <see cref="MakeKey"/>'s key back into its five parts; false for anything else.</summary>
        public static bool TryParseKey(string key, out string gameId, out string carId, out string sourceIdentity,
            out SlipAngleTarget target, out SurfaceBucket bucket)
        {
            sourceIdentity = string.Empty;
            target = SlipAngleTarget.All;
            bucket = SurfaceBucket.Sealed;
            if (!ColdStartTierResolver.TryParseKey(key, out gameId, out carId, out string segment, out string surface)) return false;
            if (key.Split(new[] { "|#|" }, StringSplitOptions.None).Length != 4) return false;
            int cut = segment.LastIndexOf(TargetSeparator);
            if (cut < 0) return false;
            sourceIdentity = segment.Substring(0, cut);
            string t = segment.Substring(cut + 1);
            if (t == "front") target = SlipAngleTarget.Front;
            else if (t == "rear") target = SlipAngleTarget.Rear;
            else if (t == "all") target = SlipAngleTarget.All;
            else return false;
            if (surface == "sealed") bucket = SurfaceBucket.Sealed;
            else if (surface == "loose") bucket = SurfaceBucket.Loose;
            else if (surface == "lowgrip") bucket = SurfaceBucket.LowGrip;
            else return false;
            return true;
        }

        // ---- learning ---------------------------------------------------------------------------------

        /// <summary>
        /// One frame: feeds the dominant bucket's front, rear and All learners (alpha in degrees; a NaN
        /// alpha - an unavailable axle - teaches that learner nothing), then slews k toward its target on
        /// <paramref name="blend"/>.
        /// </summary>
        public void Observe(string gameId, string carId, string sourceIdentity, SurfaceBlend blend,
            double alphaFrontDeg, double alphaRearDeg, double alphaAllDeg, double absLatG,
            ImpactVerdict impact, bool learnAllowed, double dt, SlipAngleEarlyContext? early = null)
        {
            double aged = ClampMath.IsFinite(dt) && dt > 0.0 ? dt : NominalFrameSeconds;
            SurfaceBucket b = blend.Active;
            ObserveOne(gameId, carId, sourceIdentity, SlipAngleTarget.Front, b, alphaFrontDeg, absLatG, impact, learnAllowed, aged, early);
            ObserveOne(gameId, carId, sourceIdentity, SlipAngleTarget.Rear, b, alphaRearDeg, absLatG, impact, learnAllowed, aged, early);
            ObserveOne(gameId, carId, sourceIdentity, SlipAngleTarget.All, b, alphaAllDeg, absLatG, impact, learnAllowed, aged, early);
            // THE BALANCE k: only both axles, only frames the learners themselves may learn from, never a slide.
            if (ClampMath.IsFinite(alphaFrontDeg) && ClampMath.IsFinite(alphaRearDeg))
            {
                string bk = MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, b);
                if (!_balance.TryGetValue(bk, out SlipAngleBalanceEstimator be)) _balance[bk] = be = new SlipAngleBalanceEstimator();
                be.Observe(alphaFrontDeg, alphaRearDeg, absLatG, SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity),
                    learnAllowed && impact.LearnAllowed && early?.Sliding != true);
            }
            _changedSinceSave = true;
            StepK(gameId, carId, sourceIdentity, blend, aged);
        }

        private void ObserveOne(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket,
            double alphaDeg, double absLatG, ImpactVerdict impact, bool learnAllowed, double dt, SlipAngleEarlyContext? early)
        {
            string key = MakeKey(gameId, carId, sourceIdentity, target, bucket);
            if (!_entries.TryGetValue(key, out Entry e))
            {
                ColdStartTier tier = ResolveFresh(gameId, carId, sourceIdentity, target, bucket, key, out string borrowedKey, out SlipAngleWindow cold);
                e = new Entry(gameId, carId, sourceIdentity, target, bucket, new SlipAngleWindowLearner(cold, SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity)) { CrossingGateEnabled = _crossingGateEnabled }, cold, tier, borrowedKey, false);
                e.Epoch = _evidenceEpoch;
                _entries[key] = e;
            }
            EnsureCurrent(e, key);

            SlipAngleWindowLearner l = e.Learner;
            bool wasConfirmed = l.PeakConfirmed;
            SlipAngleWindow before = wasConfirmed ? default : l.Published;
            l.Observe(alphaDeg, absLatG, impact, learnAllowed, dt, early);
            if (!wasConfirmed && l.PeakConfirmed)
            {
                // Latch: the cold reference is what the key published just before; w starts at 0, so the
                // published window does not move on this frame.
                e.Cold = before;
                e.ColdLatched = true;
                e.Weight = 0.0;
                _evidenceEpoch++;
            }
            else if (l.PeakConfirmed)
            {
                double step = HotWeightSlewPerSecond * dt;
                e.Weight += ClampMath.Clamp(HotWeightTarget(l.WindowQuality, e.SessionCrossings) - e.Weight, -step, step);
            }
        }

        /// <summary>Before the key confirms: adopt a strictly better tier that appeared since (re-prior).</summary>
        private void EnsureCurrent(Entry e, string key)
        {
            // FOLLOW THE LENDER (owner, 2026-11-22): until this key learns anything of its own, its start follows the
            // lender as the lender's confidence grows (or another lender appears / leaves) - every read, not only when a
            // key confirms. From its first own data the start it had then stays, and its own corners take over.
            if (!e.ColdLatched && !e.Restored && !e.Learner.PeakConfirmed && !HasOwnData(e))
            {
                ColdStartTier now = ResolveFresh(e.Game, e.Car, e.Source, e.Target, e.Bucket, key, out string lender, out SlipAngleWindow start);
                e.Epoch = _evidenceEpoch;
                e.ColdTier = now;
                e.BorrowedKey = lender;
                e.Cold = start;
                e.Learner.SetPrior(start);
                return;
            }
            if (e.Epoch == _evidenceEpoch) return;
            e.Epoch = _evidenceEpoch;
            if (e.ColdLatched || e.Learner.PeakConfirmed) return;
            ColdStartTier tier = ResolveFresh(e.Game, e.Car, e.Source, e.Target, e.Bucket, key, out string borrowedKey, out SlipAngleWindow cold);
            if (tier <= e.ColdTier) return;
            e.ColdTier = tier;
            e.BorrowedKey = borrowedKey;
            e.Cold = cold;
            e.Learner.SetPrior(cold);
        }

        /// <summary>Live-vs-cold weight target: <c>ConcaveCountConfidence(n, CrossingsForFullTrust) x
        /// clamp01(windowQuality / QualityForFullTrust)</c>, n = this session's completed crossings; 0 at n = 0,
        /// exactly 1 from 10 crossings at quality 0.5 or better.</summary>
        public static double HotWeightTarget(double windowQuality, int sessionCrossings)
        {
            if (sessionCrossings <= 0 || !ClampMath.IsFinite(windowQuality) || windowQuality <= 0.0) return 0.0;
            double count = ColdWarmBlend.ConcaveCountConfidence(sessionCrossings, CrossingsForFullTrust);
            return ClampMath.To01(count * ClampMath.To01(windowQuality / QualityForFullTrust));
        }

        // ---- windows ----------------------------------------------------------------------------------

        /// <summary>The published (All target) window on <paramref name="blend"/>.</summary>
        public SlipAngleWindow Window(string gameId, string carId, string sourceIdentity, SurfaceBlend blend) =>
            Window(gameId, carId, sourceIdentity, SlipAngleTarget.All, blend);

        /// <summary><paramref name="target"/>'s window on <paramref name="blend"/>: the buckets' windows blended
        /// component-wise by weight (the dominant bucket's alone when no weight is positive).</summary>
        public SlipAngleWindow Window(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBlend blend)
        {
            double sum = 0.0, min = 0.0, best = 0.0, max = 0.0;
            foreach (SurfaceBucket b in AllBuckets)
            {
                double w = blend.Weight(b);
                if (!(w > 0.0) || !ClampMath.IsFinite(w)) continue;
                SlipAngleWindow bw = BucketWindow(gameId, carId, sourceIdentity, target, b);
                sum += w;
                min += w * bw.Min;
                best += w * bw.Best;
                max += w * bw.Max;
            }
            if (!(sum > 0.0)) return BucketWindow(gameId, carId, sourceIdentity, target, blend.Active);
            if (Math.Abs(sum - 1.0) < 1e-15) return new SlipAngleWindow(min, best, max);
            return new SlipAngleWindow(min / sum, best / sum, max / sum);
        }

        /// <summary>One key's window (see the class remarks); for a key with no learner yet, its borrowed
        /// window (or the shipped default).</summary>
        public SlipAngleWindow BucketWindow(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket)
        {
            string key = MakeKey(gameId, carId, sourceIdentity, target, bucket);
            if (!_entries.TryGetValue(key, out Entry e))
            {
                ResolveFresh(gameId, carId, sourceIdentity, target, bucket, key, out _, out SlipAngleWindow cold);
                return cold.CappedAt(SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity));
            }
            EnsureCurrent(e, key);
            return Published(e);
        }

        /// <summary>The key's published window, with Max capped for its source
        /// (<see cref="SlipAngleWindowLearner.MaxCapDegFor"/>: 20 degrees native, 30 car-angle) - whatever
        /// it was learned, borrowed or loaded from. The persisted window comes from here too.</summary>
        private static SlipAngleWindow Published(Entry e)
        {
            SlipAngleWindowLearner live = e.Learner;
            double cap = SlipAngleWindowLearner.MaxCapDegFor(e.Source);
            if (!live.PeakConfirmed) return live.Published.CappedAt(cap);
            SlipAngleWindow hot = live.Published;
            double w = e.Weight;
            return new SlipAngleWindow(
                ColdWarmBlend.Blend(e.Cold.Min, hot.Min, w),
                ColdWarmBlend.Blend(e.Cold.Best, hot.Best, w),
                ColdWarmBlend.Blend(e.Cold.Max, hot.Max, w)).CappedAt(cap);
        }

        // ---- tiers ------------------------------------------------------------------------------------

        /// <summary>The dominant bucket's All-target tier (for <c>ColdStartTierFloors</c>).</summary>
        public ColdStartTier Tier(string gameId, string carId, string sourceIdentity, SurfaceBlend blend) =>
            BucketTier(gameId, carId, sourceIdentity, SlipAngleTarget.All, blend.Active, out _);

        /// <summary>One key's tier and the key it borrowed from: latched for a key with a learner (the
        /// borrowed key is null when unknown, e.g. after a restart), resolved afresh otherwise.</summary>
        public ColdStartTier BucketTier(string gameId, string carId, string sourceIdentity, SlipAngleTarget target,
            SurfaceBucket bucket, out string borrowedKey)
        {
            string key = MakeKey(gameId, carId, sourceIdentity, target, bucket);
            if (_entries.TryGetValue(key, out Entry e))
            {
                EnsureCurrent(e, key);
                borrowedKey = e.BorrowedKey;
                return e.ColdTier;
            }
            return ResolveFresh(gameId, carId, sourceIdentity, target, bucket, key, out borrowedKey, out _);
        }

        private ColdStartTier ResolveFresh(string gameId, string carId, string sourceIdentity, SlipAngleTarget target,
            SurfaceBucket bucket, string key, out string borrowedKey, out SlipAngleWindow cold)
        {
            // THE BORROWING RULE (SlipAngleBorrowRules.WindowScope): only the keys it allows are candidates at all.
            var wanted = new SlipAngleBorrowKey(gameId, carId, SourceSegment(sourceIdentity, target), BucketName(bucket));
            SlipAngleBorrowScope scope = SlipAngleBorrowRules.WindowScope(sourceIdentity);
            List<string> candidates = _entries.Keys.Where(k => SlipAngleBorrowRules.Allows(scope, k, wanted)).ToList();
            ColdStartTier tier = _resolver.Resolve(gameId, carId, SourceSegment(sourceIdentity, target), BucketName(bucket),
                key, _hasEvidence, candidates, _tryGetReference, out borrowedKey, out _);
            SlipAngleWindow shipped = SlipAngleWindow.ShippedDefault.CappedAt(SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity));
            if (borrowedKey != null && _entries.TryGetValue(borrowedKey, out Entry borrowed))
            {
                // BORROWED BY CONFIDENCE (owner, 2026-11-22): the shipped start moved toward the lender's window by the
                // lender's own confidence - 30% confident moves it 30% of the way, 100% takes the lender's window.
                double c = LenderConfidence(borrowed);
                SlipAngleWindow lent = borrowed.Learner.Published;
                cold = new SlipAngleWindow(
                    shipped.Min + c * (lent.Min - shipped.Min),
                    shipped.Best + c * (lent.Best - shipped.Best),
                    shipped.Max + c * (lent.Max - shipped.Max));
                if (cold.IsValid) return tier;
            }
            borrowedKey = null;
            cold = SlipAngleWindow.ShippedDefault;
            return ColdStartTier.Tier1;
        }

        /// <summary>A key's confidence as a lender: its own Max confidence (the LED), 0 with no learning of its own.</summary>
        private static double LenderConfidence(Entry e) =>
            e.Learner.PeakConfirmed ? 1.0 : HasOwnData(e) ? ClampMath.To01(e.Learner.MaxMaturity) : 0.0;

        private bool TryGetReference(string key, out int samples, out double ceiling)
        {
            samples = 0;
            ceiling = 0.0;
            if (!_entries.TryGetValue(key, out Entry e)) return false;
            double c = LenderConfidence(e);
            if (!(c > 0.0)) return false;
            // Within a tier the MOST CONFIDENT lender wins (owner, 2026-11-22), then the more observed one.
            long obs = Math.Min(e.Learner.Observations, 999L);
            samples = (int)(Math.Round(c * 1000.0) * 1000 + obs);
            ceiling = e.Learner.Published.Max;
            return true;
        }

        /// <summary>True when a key's learner has taught anything of its own (a maturity observation, a crossing or an
        /// early-estimate crossing) - from then on it is no longer borrowing, whatever its prior came from.</summary>
        private static bool HasOwnData(Entry e) =>
            e.Learner.Observations > 0 || e.Learner.CrossingCount > 0 || e.Learner.Early.Crossings > 0;

        /// <summary>
        /// THE BORROWED WINDOW (owner, 2026-11-22): true when the key has nothing of its own yet and starts from another
        /// key's window (<see cref="SlipAngleBorrowRules.WindowScope"/>) - the half LED. <paramref name="tier"/> says from
        /// where (4 another surface, 3 another car), <paramref name="lenderMaturity"/> is the lender's own window maturity.
        /// False once the key has its own data, and for a shipped start. Data thread / under the learner's lock.
        /// </summary>
        public bool TryWindowBorrow(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket,
            out ColdStartTier tier, out double lenderMaturity)
        {
            tier = ColdStartTier.Tier1;
            lenderMaturity = 0.0;
            string key = MakeKey(gameId, carId, sourceIdentity, target, bucket);
            string lender;
            if (_entries.TryGetValue(key, out Entry e))
            {
                EnsureCurrent(e, key);
                if (HasOwnData(e) || e.ColdTier <= ColdStartTier.Tier1 || e.BorrowedKey == null) return false;
                tier = e.ColdTier;
                lender = e.BorrowedKey;
            }
            else
            {
                tier = ResolveFresh(gameId, carId, sourceIdentity, target, bucket, key, out lender, out _);
                if (lender == null || tier <= ColdStartTier.Tier1) { tier = ColdStartTier.Tier1; return false; }
            }
            if (_entries.TryGetValue(lender, out Entry l)) lenderMaturity = ClampMath.To01(l.Learner.MaxMaturity);
            return true;
        }

        /// <summary>True when the exact key has a learner, or would start from a borrowed window - the display shows
        /// that window (what the output uses) rather than the shipped default.</summary>
        public bool HasWindowOrBorrow(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket) =>
            _entries.ContainsKey(MakeKey(gameId, carId, sourceIdentity, target, bucket))
            || TryWindowBorrow(gameId, carId, sourceIdentity, target, bucket, out _, out _);

        /// <summary>Diagnostics / status text: true when the key's own learner has confirmed its peak.</summary>
        public bool PeakConfirmed(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, target, bucket), out Entry e) && e.Learner.PeakConfirmed;

        /// <summary>Diagnostics: the key's own learner window quality (0 when the key has no learner).</summary>
        public double WindowQuality(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, target, bucket), out Entry e) ? e.Learner.WindowQuality : 0.0;

        /// <summary>Diagnostics: the key's live learner window (its learned window once confirmed), before
        /// the live-vs-cold blend; null when the key has no learner.</summary>
        public SlipAngleWindow? LearnedWindow(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, target, bucket), out Entry e) ? e.Learner.Published : (SlipAngleWindow?)null;

        /// <summary>
        /// The maturity of one key's OWN learned window for the indicator (docs\learning-maturity-indicator-report.md).
        /// Until the peak is confirmed the whole window is still the starting one, so Max, Best and Min all report
        /// the same confirmation progress (<see cref="SlipAngleWindowLearner.MaxMaturity"/>: consistent crossings of
        /// 3, at most 0.99). Once confirmed, <paramref name="max"/> is 1 and <paramref name="curve"/> (Best and Min,
        /// read off the curve under that Max) is window quality over <see cref="QualityForFullTrust"/>. A borrowed
        /// cold window is not this key's learning and counts for nothing. False when the key has no learner.
        /// </summary>
        public bool TryWindowMaturity(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket,
            out double max, out double curve)
        {
            max = curve = 0.0;
            string key = MakeKey(gameId, carId, sourceIdentity, target, bucket);
            if (!_entries.TryGetValue(key, out Entry e)) return false;
            EnsureCurrent(e, key);
            max = ClampMath.To01(e.Learner.MaxMaturity);
            curve = e.Learner.PeakConfirmed ? ClampMath.To01(e.Learner.WindowQuality / QualityForFullTrust) : max;
            return true;
        }

        // ---- maturity, classifier inputs -----------------------------------------------------------------

        /// <summary>The All target's maturity, blended by the bucket weights (0 for a bucket never driven).</summary>
        public double Maturity(string gameId, string carId, string sourceIdentity, SurfaceBlend blend)
        {
            double sum = 0.0, m = 0.0;
            foreach (SurfaceBucket b in AllBuckets)
            {
                double w = blend.Weight(b);
                if (!(w > 0.0) || !ClampMath.IsFinite(w)) continue;
                sum += w;
                if (_entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, b), out Entry e))
                    m += w * e.Learner.Maturity;
            }
            if (!(sum > 0.0))
                return _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, blend.Active), out Entry a)
                    ? a.Learner.Maturity : 0.0;
            return ClampMath.To01(m / sum);
        }

        /// <summary>The bucket's own All-target curve at <paramref name="alphaDeg"/> (NaN without one) -
        /// the surface classifier's <c>expected</c> input.</summary>
        public double ExpectedLatG(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, double alphaDeg) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, bucket), out Entry e)
                ? e.Learner.ExpectedLatG(alphaDeg) : double.NaN;

        /// <summary>The sealed All-target curve's peak G (0 without one) - the classifier's sealed reference.</summary>
        /// <summary>Diagnostics: the key's fast pre-confirmation estimate - corners' worth of evidence and crossings
        /// the slide guard ignored. False when the key has no learner.</summary>
        public bool TryEarly(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket, out double weight, out long guarded)
        {
            weight = 0.0;
            guarded = 0;
            if (!_entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, target, bucket), out Entry e)) return false;
            weight = e.Learner.Early.Weight;
            guarded = e.Learner.Early.Guarded;
            return true;
        }

        /// <summary>Diagnostics: the key's completed crossings (0 when it has no learner).</summary>
        public int CrossingCount(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, target, bucket), out Entry e) ? e.Learner.CrossingCount : 0;

        /// <summary>Diagnostics: the key's curve peak lateral G (0 when it has no learner or curve).</summary>
        public double CurvePeakG(string gameId, string carId, string sourceIdentity, SlipAngleTarget target, SurfaceBucket bucket) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, target, bucket), out Entry e) ? e.Learner.PeakG : 0.0;

        public double SealedPeakG(string gameId, string carId, string sourceIdentity) =>
            _entries.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, SurfaceBucket.Sealed), out Entry e)
                ? e.Learner.PeakG : 0.0;

        // ---- k ----------------------------------------------------------------------------------------

        /// <summary>The (slewed) equalisation ratio for the context; before the context's first
        /// <see cref="Observe"/>, its target on <paramref name="blend"/>.</summary>
        public double K(string gameId, string carId, string sourceIdentity, SurfaceBlend blend)
        {
            string ctx = ContextKey(gameId, carId, sourceIdentity);
            return _k.TryGetValue(ctx, out KState s) ? s.Value : KTarget(gameId, carId, sourceIdentity, blend);
        }

        /// <summary>Clamp to [<see cref="KMin"/>, <see cref="KMax"/>]; 1.0 when not finite.</summary>
        public static double ClampK(double k) => ClampMath.IsFinite(k) ? ClampMath.Clamp(k, KMin, KMax) : 1.0;

        /// <summary>The balance k on <paramref name="blend"/>: the weighted buckets' estimates blended in the log domain
        /// (each pulled toward its starting value, <see cref="StartingK"/>; the dominant bucket alone when no weight is
        /// positive).</summary>
        private double KTarget(string gameId, string carId, string sourceIdentity, SurfaceBlend blend)
        {
            double sum = 0.0, log = 0.0;
            foreach (SurfaceBucket b in AllBuckets)
            {
                double w = blend.Weight(b);
                if (!(w > 0.0) || !ClampMath.IsFinite(w)) continue;
                sum += w;
                log += w * Math.Log(BucketK(gameId, carId, sourceIdentity, b));
            }
            return sum > 0.0 ? ClampK(Math.Exp(log / sum)) : BucketK(gameId, carId, sourceIdentity, blend.Active);
        }

        private double BucketK(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket)
        {
            double start = StartingK(gameId, carId, sourceIdentity, bucket, out _);
            return _balance.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, bucket), out SlipAngleBalanceEstimator e)
                ? e.K(KMin, KMax, start) : start;
        }

        /// <summary>True when the bucket has k evidence of its own.</summary>
        private bool HasOwnK(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket) =>
            _balance.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, bucket), out SlipAngleBalanceEstimator e)
            && e.HasEvidence;

        /// <summary>
        /// A bucket's STARTING k (owner, 2026-11-22): the k of the key <see cref="SlipAngleBorrowRules.KScope"/> allows -
        /// the same game, car and source on another surface - with the most near-limit evidence; 1 when none.
        /// <paramref name="lender"/> is the lending surface (null: 1). Never chained: the lender's own k toward 1.
        /// </summary>
        public double StartingK(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, out SurfaceBucket? lender)
        {
            lender = null;
            var wanted = new SlipAngleBorrowKey(gameId, carId, SourceSegment(sourceIdentity, SlipAngleTarget.All), BucketName(bucket));
            SlipAngleBalanceEstimator best = null;
            foreach (KeyValuePair<string, SlipAngleBalanceEstimator> kv in _balance)
            {
                if (!kv.Value.HasEvidence || !SlipAngleBorrowRules.Allows(SlipAngleBorrowRules.KScope, kv.Key, wanted)) continue;
                if (best != null && kv.Value.NearLimitFrames <= best.NearLimitFrames) continue;
                if (!TryParseKey(kv.Key, out _, out _, out _, out _, out SurfaceBucket b)) continue;
                best = kv.Value;
                lender = b;
            }
            return best == null ? 1.0 : best.K(KMin, KMax);
        }

        /// <summary>True when the bucket has no k evidence of its own and starts from another surface's k (the tooltip
        /// says so; the LED colour follows the window only).</summary>
        public bool KBorrowed(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket, out SurfaceBucket? lender)
        {
            lender = null;
            if (HasOwnK(gameId, carId, sourceIdentity, bucket)) return false;
            StartingK(gameId, carId, sourceIdentity, bucket, out lender);
            return lender.HasValue;
        }

        /// <summary>Diagnostics: the bucket's near-limit frames counted toward k (0 without any).</summary>
        public long BalanceFrames(string gameId, string carId, string sourceIdentity, SurfaceBucket bucket) =>
            _balance.TryGetValue(MakeKey(gameId, carId, sourceIdentity, SlipAngleTarget.All, bucket), out SlipAngleBalanceEstimator e)
                ? e.NearLimitFrames : 0;

        private void StepK(string gameId, string carId, string sourceIdentity, SurfaceBlend blend, double dt)
        {
            string ctx = ContextKey(gameId, carId, sourceIdentity);
            double target = KTarget(gameId, carId, sourceIdentity, blend);
            if (!_k.TryGetValue(ctx, out KState s))
            {
                _k[ctx] = new KState { Value = target };
                return;
            }
            // A BRAND-NEW KEY STARTS FRESH (owner, 2026-11-22): with no k evidence of its own on the dominant bucket, k is
            // its starting value at once - not eased over from the surface driven before.
            if (!HasOwnK(gameId, carId, sourceIdentity, blend.Active))
            {
                s.Value = target;
                return;
            }
            double step = KSlewPerSecond * dt;
            s.Value += ClampMath.Clamp(target - s.Value, -step, step);
        }

        private static string ContextKey(string gameId, string carId, string sourceIdentity) =>
            ColdStartTierResolver.Key(gameId, carId, sourceIdentity);

        // ---- persistence ------------------------------------------------------------------------------

        /// <summary>Every key's state - fresh objects (data thread only; changes nothing but may re-prior an
        /// unconfirmed learner to a better tier first, exactly as reading its window would).</summary>
        public Dictionary<string, SlipAngleKeyState> Export()
        {
            var d = new Dictionary<string, SlipAngleKeyState>(_entries.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                EnsureCurrent(kv.Value, kv.Key);
                SlipAngleWindow p = Published(kv.Value);
                d[kv.Key] = new SlipAngleKeyState
                {
                    Learner = kv.Value.Learner.Export(),
                    PublishedMin = p.Min,
                    PublishedBest = p.Best,
                    PublishedMax = p.Max,
                    ColdTier = (int)kv.Value.ColdTier,
                    Balance = _balance.TryGetValue(kv.Key, out SlipAngleBalanceEstimator be) ? be.Export() : null,
                };
            }
            return d;
        }

        /// <summary>
        /// Replaces everything with <paramref name="states"/> (null: empty). Malformed keys and null states
        /// are skipped. Each key's persisted published window becomes its cold reference (w = 0); a
        /// confirmed key's is latched. A key without one (older state) uses its learner's window; an
        /// unconfirmed key whose state has no prior is given its resolved borrowed prior. Session crossing
        /// counts and k start fresh.
        /// </summary>
        public void Import(Dictionary<string, SlipAngleKeyState> states)
        {
            _entries.Clear();
            _k.Clear();
            _balance.Clear();
            _evidenceEpoch++;
            _changedSinceSave = false;
            _sinceSave = 0.0;
            if (states == null) return;
            // (_changedSinceSave is set again below when a guessed bucket is dropped, so the file is rewritten.)

            var missingPrior = new List<string>();
            foreach (KeyValuePair<string, SlipAngleKeyState> kv in states)
            {
                if (kv.Value?.Learner == null
                    || !TryParseKey(kv.Key, out string g, out string c, out string s, out SlipAngleTarget t, out SurfaceBucket b)) continue;
                // Learned while the surface was GUESSED (owner, 2026-11-22: never guess) - a Loose / Low-grip bucket of a
                // game that reports no surface. Dropped, with its k; the next save writes the file without it.
                if (SurfaceBucketMap.IsGuessedBucket(g, b)) { _changedSinceSave = true; continue; }
                var learner = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault, SlipAngleWindowLearner.MaxCapDegFor(s));
                learner.Import(kv.Value.Learner);
                learner.CrossingGateEnabled = _crossingGateEnabled;
                var published = new SlipAngleWindow(kv.Value.PublishedMin, kv.Value.PublishedBest, kv.Value.PublishedMax);
                bool hasPublished = published.IsValid && published.Max <= SlipAngleWindowLearner.MaxCeilingDeg;
                SlipAngleWindow cold = hasPublished ? published : learner.Published;
                int tierValue = kv.Value.ColdTier;
                ColdStartTier tier = tierValue >= 1 && tierValue <= 4 ? (ColdStartTier)tierValue : ColdStartTier.Tier1;
                bool latched = learner.PeakConfirmed;
                var e = new Entry(g, c, s, t, b, learner, cold, tier, null, latched) { Epoch = _evidenceEpoch, Restored = true };
                _entries[kv.Key] = e;
                // The balance k carries on after a restart (owner, 2026-11-22): its evidence comes back with the key.
                if (t == SlipAngleTarget.All && kv.Value.Balance != null)
                {
                    var be = new SlipAngleBalanceEstimator();
                    be.Import(kv.Value.Balance);
                    _balance[kv.Key] = be;
                }
                bool priorPersisted = new SlipAngleWindow(kv.Value.Learner.PriorMin, kv.Value.Learner.PriorBest, kv.Value.Learner.PriorMax).IsValid;
                // A borrowed prior is resolved again under today's rules (SlipAngleBorrowRules): a file written before
                // them may hold another game's or another car's window for an angle source.
                if (!latched && (!priorPersisted || tier > ColdStartTier.Tier1)) missingPrior.Add(kv.Key);
            }

            // Second pass, once every key is known: older states carry no prior - give them their tier.
            foreach (string key in missingPrior)
            {
                Entry e = _entries[key];
                e.Restored = false;   // nothing usable of its own was saved: it may follow a lender like a new key
                ColdStartTier tier = ResolveFresh(e.Game, e.Car, e.Source, e.Target, e.Bucket, key, out string borrowedKey, out SlipAngleWindow cold);
                e.ColdTier = tier;
                e.BorrowedKey = borrowedKey;
                e.Cold = cold;
                e.Learner.SetPrior(cold);
            }
        }

        /// <summary>
        /// Removes every key of <paramref name="sourceIdentity"/> from a loaded file's states - used once for an older
        /// file's slip-RATIO windows (owner, 2026-11-22): they were learned at 20% x value, twice the angles of the new
        /// scale (<see cref="SlipAngleSourceScale.DegreesPerPercent"/>), so they would be wrong. Returns how many.
        /// </summary>
        public static int DropSource(Dictionary<string, SlipAngleKeyState> states, string sourceIdentity)
        {
            if (states == null) return 0;
            List<string> gone = states.Keys
                .Where(k => TryParseKey(k, out _, out _, out string s, out _, out _) && string.Equals(s, sourceIdentity, StringComparison.Ordinal))
                .ToList();
            foreach (string k in gone) states.Remove(k);
            return gone.Count;
        }

        /// <summary>
        /// Data-thread persistence: when something was observed since the last save and at least
        /// <see cref="PersistIntervalSeconds"/> have passed, exports on THIS thread and hands the result to
        /// <paramref name="save"/> (e.g. <c>RuntimeStore.SaveSlipAngleLearners</c>, which only touches the
        /// in-memory cache). True when it saved.
        /// </summary>
        public bool SaveIfDue(double dt, Action<Dictionary<string, SlipAngleKeyState>> save)
        {
            if (ClampMath.IsFinite(dt) && dt > 0.0) _sinceSave += dt;
            if (!_changedSinceSave || _sinceSave < PersistIntervalSeconds - 1e-9 || save == null) return false;
            Save(save);
            return true;
        }

        /// <summary>Exports now (on the calling, data thread) into <paramref name="save"/> - for a car /
        /// source switch or shutdown.</summary>
        public void Save(Action<Dictionary<string, SlipAngleKeyState>> save)
        {
            if (save == null) return;
            save(Export());
            SpeedBandSave?.Invoke(SpeedBands.Export());
            _sinceSave = 0.0;
            _changedSinceSave = false;
        }

        // ---- types ------------------------------------------------------------------------------------

        private sealed class Entry
        {
            public Entry(string game, string car, string source, SlipAngleTarget target, SurfaceBucket bucket,
                SlipAngleWindowLearner learner, SlipAngleWindow cold, ColdStartTier coldTier, string borrowedKey, bool latched)
            {
                Game = game;
                Car = car;
                Source = source;
                Target = target;
                Bucket = bucket;
                Learner = learner;
                Cold = cold;
                ColdTier = coldTier;
                BorrowedKey = borrowedKey;
                ColdLatched = latched;
                CrossingsAtStart = learner.CrossingCount;
            }

            public string Game { get; }
            public string Car { get; }
            public string Source { get; }
            public SlipAngleTarget Target { get; }
            public SurfaceBucket Bucket { get; }
            public SlipAngleWindowLearner Learner { get; }

            /// <summary>The cold reference (latched at confirmation, or the persisted published window).</summary>
            public SlipAngleWindow Cold;
            public ColdStartTier ColdTier;
            public string BorrowedKey;
            public bool ColdLatched;
            /// <summary>Loaded from the parameters file: its saved window is its OWN saved learning (a restart), never
            /// replaced by a lender's (owner, 2026-11-22).</summary>
            public bool Restored;
            /// <summary>Live-vs-cold weight, slewed (0 at confirmation / import).</summary>
            public double Weight;
            /// <summary>The evidence epoch this entry last checked for a better tier.</summary>
            public int Epoch;
            public int CrossingsAtStart { get; }

            public int SessionCrossings => Math.Max(0, Learner.CrossingCount - CrossingsAtStart);
        }

        private sealed class KState
        {
            public double Value;
        }
    }
}

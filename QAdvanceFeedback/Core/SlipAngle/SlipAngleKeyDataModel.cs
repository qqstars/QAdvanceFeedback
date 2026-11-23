using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// THE ONE SLIP ANGLE KEY-DATA DISPLAY RULE (owner, 2026-11-19: "make sure the Slip Angle also
    /// following the same rule similar to the Lock/Slip for the setting/displaying default value";
    /// docs\full-mvvm-plan.md 3.5) - the Slip Angle twin of <c>Settings.KeyDataPanelModel.LearnedKeyData</c>
    /// / <c>Resolve</c>. Used by the Slip Angle tab (Auto read-out, learned hints), the ShakeIt Slip Angle
    /// panel and the plugin's one-time Manual seed, so what is shown is what is seeded.
    /// <para/>
    /// <see cref="LearnedWindow(KeyedSlipAngleLearner, string, string, string, SurfaceBlend)"/>: the
    /// EXACT key's published window - (game, car, source identity, target All, surface bucket), the
    /// identity being Plugin Internal's Native / Fallback identity (by the current identity tier) or a
    /// Custom composite - with Max capped for the source (<see cref="SlipAngleWindowLearner.MaxCapDegFor"/>:
    /// 20 degrees native, 30 car-angle). After a restart with no driving that IS the parameters file's
    /// window for that key (<see cref="KeyedSlipAngleLearner.Import"/> makes it the key's cold reference).
    /// Null when the learner holds no such key: the UI then shows <see cref="DefaultWindow"/>. Never another
    /// key's window - in particular never the learner's borrowed (another car / game / surface) window that
    /// <see cref="KeyedSlipAngleLearner.BucketWindow"/> returns for a key it has no learner for.
    /// <para/>
    /// Not thread-safe on its own: the learner's getters mutate (re-prior), so the plugin calls this under
    /// the same lock the data thread processes frames under.
    /// </summary>
    public static class SlipAngleKeyDataModel
    {
        private static readonly SurfaceBucket[] Buckets = { SurfaceBucket.Sealed, SurfaceBucket.Loose, SurfaceBucket.LowGrip };

        /// <summary>The surface the UI reads with no classified blend (the SimHub menu after a restart,
        /// before the first running frame): Sealed only - where the classifier itself starts.</summary>
        public static SurfaceBlend MenuBlend => SurfaceBlend.Only(SurfaceBucket.Sealed);

        /// <summary>The exact key's window on the Sealed surface (<see cref="MenuBlend"/>).</summary>
        public static SlipAngleWindow? LearnedWindow(KeyedSlipAngleLearner learner, string gameId, string carId, string sourceIdentity) =>
            LearnedWindow(learner, gameId, carId, sourceIdentity, MenuBlend);

        /// <summary>The exact key's window for a context (null context: unknown).</summary>
        public static SlipAngleWindow? LearnedWindow(KeyedSlipAngleLearner learner, SlipAngleKeyDataContext.Snapshot context, string sourceIdentity) =>
            context == null ? (SlipAngleWindow?)null : LearnedWindow(learner, context.GameId, context.CarId, sourceIdentity, context.Blend);

        /// <summary>
        /// The exact key's published window on <paramref name="blend"/>: the buckets with weight whose exact
        /// key exists, blended component-wise by weight (renormalised), capped for the source. When every
        /// weighted bucket's key exists this is exactly <see cref="KeyedSlipAngleLearner.Window(string, string, string, SurfaceBlend)"/>
        /// - what the engine applies. A weighted bucket with no key of its own contributes nothing (the
        /// learner would put a borrowed or shipped window there). Null when no weighted bucket's key exists,
        /// or with no game / identity / learner.
        /// </summary>
        public static SlipAngleWindow? LearnedWindow(KeyedSlipAngleLearner learner, string gameId, string carId,
            string sourceIdentity, SurfaceBlend blend)
        {
            if (learner == null || string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(sourceIdentity)) return null;
            string car = carId ?? string.Empty;
            double sum = 0.0, min = 0.0, best = 0.0, max = 0.0;
            bool anyWeight = false;
            foreach (SurfaceBucket b in Buckets)
            {
                double w = blend.Weight(b);
                if (!(w > 0.0) || !ClampMath.IsFinite(w)) continue;
                anyWeight = true;
                if (!Has(learner, gameId, car, sourceIdentity, b)) continue;
                SlipAngleWindow bw = learner.BucketWindow(gameId, car, sourceIdentity, SlipAngleTarget.All, b);
                sum += w;
                min += w * bw.Min;
                best += w * bw.Best;
                max += w * bw.Max;
            }
            SlipAngleWindow result;
            if (!anyWeight)
            {
                // No positive weight: the dominant bucket alone (the learner's own rule).
                if (!Has(learner, gameId, car, sourceIdentity, blend.Active)) return null;
                result = learner.BucketWindow(gameId, car, sourceIdentity, SlipAngleTarget.All, blend.Active);
            }
            else if (!(sum > 0.0)) return null;
            else if (Math.Abs(sum - 1.0) < 1e-15) result = new SlipAngleWindow(min, best, max);
            else result = new SlipAngleWindow(min / sum, best / sum, max / sum);

            result = result.CappedAt(SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity));
            return result.IsValid ? result : (SlipAngleWindow?)null;
        }

        /// <summary>The key has a learner, or starts from a window the borrowing rules allow (SlipAngleBorrowRules) - what
        /// the output actually uses (owner, 2026-11-22).</summary>
        private static bool Has(KeyedSlipAngleLearner learner, string game, string car, string identity, SurfaceBucket bucket) =>
            learner.HasWindowOrBorrow(game, car, identity, SlipAngleTarget.All, bucket);

        /// <summary>
        /// The maturity behind <see cref="LearnedWindow(KeyedSlipAngleLearner, SlipAngleKeyDataContext.Snapshot, string)"/>
        /// (docs\learning-maturity-indicator-report.md): the same exact keys on the same blend, each key's
        /// <see cref="KeyedSlipAngleLearner.TryWindowMaturity"/> weighted like its window. A weighted bucket with no
        /// key of its own contributes 0 (its window would be borrowed, not learned). Null with no context, game
        /// or identity; all zero when no key exists. Same threading as <see cref="LearnedWindow(KeyedSlipAngleLearner, string, string, string, SurfaceBlend)"/>.
        /// </summary>
        public static SlipAngleWindowMaturity? LearnedWindowMaturity(KeyedSlipAngleLearner learner, SlipAngleKeyDataContext.Snapshot context, string sourceIdentity)
        {
            if (context == null || learner == null || string.IsNullOrEmpty(context.GameId) || string.IsNullOrEmpty(sourceIdentity)) return null;
            string car = context.CarId ?? string.Empty;
            SurfaceBlend blend = context.Blend;
            // BORROWING (owner, 2026-11-22): the LED colour shows a borrowed WINDOW only - the dominant bucket's, while it
            // has nothing of its own: its right half the lender's confidence, its left half this key's own (0). A borrowed
            // k is for the tooltip only.
            bool borrowed = learner.TryWindowBorrow(context.GameId, car, sourceIdentity, SlipAngleTarget.All, blend.Active,
                out Normalized.ColdStartTier from, out double lender);
            bool kBorrowed = learner.KBorrowed(context.GameId, car, sourceIdentity, blend.Active, out _);
            double sum = 0.0, max = 0.0, curve = 0.0;
            foreach (SurfaceBucket b in Buckets)
            {
                double w = blend.Weight(b);
                if (!(w > 0.0) || !ClampMath.IsFinite(w)) continue;
                sum += w;
                if (learner.TryWindowMaturity(context.GameId, car, sourceIdentity, SlipAngleTarget.All, b, out double m, out double c))
                {
                    max += w * m;
                    curve += w * c;
                }
            }
            if (!(sum > 0.0))
            {
                // No positive weight: the dominant bucket alone (the learner's own rule).
                learner.TryWindowMaturity(context.GameId, car, sourceIdentity, SlipAngleTarget.All, blend.Active, out max, out curve);
                return borrowed
                    ? new SlipAngleWindowMaturity(lender, lender, lender, from, max, kBorrowed)
                    : new SlipAngleWindowMaturity(max, curve, curve, Normalized.ColdStartTier.Tier1, max, kBorrowed);
            }
            double own = ClampMath.To01(max / sum);
            return borrowed
                ? new SlipAngleWindowMaturity(lender, lender, lender, from, own, kBorrowed)
                : new SlipAngleWindowMaturity(own, ClampMath.To01(curve / sum), ClampMath.To01(curve / sum), Normalized.ColdStartTier.Tier1, own, kBorrowed);
        }

        /// <summary>The source's default window: <see cref="SlipAngleWindow.ShippedDefault"/> capped for it.</summary>
        public static SlipAngleWindow DefaultWindow(string sourceIdentity) =>
            SlipAngleWindow.ShippedDefault.CappedAt(SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity));

        /// <summary>
        /// What the Min/Best/Max boxes show (the rule of <c>Settings.KeyDataPanelModel.Resolve</c>): Auto =
        /// the learned window (<paramref name="learned"/>, from <see cref="LearnedWindow(KeyedSlipAngleLearner, string, string, string, SurfaceBlend)"/>)
        /// else the source's default; Manual = the stored slot else the source's default - never the
        /// learned window (the one-time seed fills the slot with it, <see cref="TrySeed"/>).
        /// </summary>
        public static SlipAngleWindow Shown(bool auto, SlipAngleWindow? manualSlot, SlipAngleWindow? learned, string sourceIdentity)
        {
            SlipAngleWindow? pick = auto ? learned : manualSlot;
            return pick.HasValue && pick.Value.IsValid ? pick.Value : DefaultWindow(sourceIdentity);
        }

        /// <summary>The one-time Manual seed of a slot from <paramref name="shown"/> - the display rule's
        /// learned window, so it writes exactly what the tab and panel show. Nothing known: no seed.</summary>
        public static bool TrySeed(ISlipAngleKeyData keyData, string gameId, string sourceIdentity, SlipAngleWindow? shown) =>
            keyData != null && shown.HasValue && keyData.TrySeed(gameId, sourceIdentity, shown.Value);
    }

    /// <summary>
    /// The persisted key data as the frame processor sees it, with the one-time Manual seed routed through
    /// the display rule: <see cref="TrySeed"/> ignores the processor's own learned window (which, for a key
    /// the learner has no learner for, is a borrowed or shipped window) and seeds
    /// <see cref="SlipAngleKeyDataModel.LearnedWindow(KeyedSlipAngleLearner, string, string, string, SurfaceBlend)"/>
    /// for the exact key instead - exactly what the tab and panel show (Lock/Slip's
    /// <c>TrySeedKeyDataSlot</c> rule). Everything else passes through. Data thread only: <see cref="Configure"/>
    /// before each <see cref="SlipAngleFrameProcessor.Process"/>, under the processor's lock.
    /// </summary>
    public sealed class SlipAngleDisplayRuleKeyData : ISlipAngleKeyData
    {
        private readonly KeyedSlipAngleLearner _learner;
        private ISlipAngleKeyData _inner;
        private string _carId = string.Empty;
        private SurfaceBlend _blend = SlipAngleKeyDataModel.MenuBlend;

        public SlipAngleDisplayRuleKeyData(KeyedSlipAngleLearner learner)
        {
            _learner = learner ?? throw new ArgumentNullException(nameof(learner));
        }

        /// <summary>This frame's settings key data, car and surface blend (the display context's).</summary>
        public SlipAngleDisplayRuleKeyData Configure(ISlipAngleKeyData inner, string carId, SurfaceBlend blend)
        {
            _inner = inner;
            _carId = carId ?? string.Empty;
            _blend = blend;
            return this;
        }

        /// <summary>The settings key data this frame wraps (null: none - the processor then needs none).</summary>
        public ISlipAngleKeyData Inner => _inner;

        public bool GetAutoGenerate(string sourceIdentity) => _inner == null || _inner.GetAutoGenerate(sourceIdentity);

        public bool TrySeed(string gameId, string sourceIdentity, SlipAngleWindow learned) =>
            SlipAngleKeyDataModel.TrySeed(_inner, gameId, sourceIdentity,
                SlipAngleKeyDataModel.LearnedWindow(_learner, gameId, _carId, sourceIdentity, _blend));

        public SlipAngleWindow Resolve(string gameId, string sourceIdentity, SlipAngleWindow learned,
            Normalized.ManualOverrideGate gate, string gateKey, double maturity) =>
            _inner == null ? learned : _inner.Resolve(gameId, sourceIdentity, learned, gate, gateKey, maturity);
    }

    /// <summary>
    /// The key-data context the Slip Angle UI forms the exact key from (docs\full-mvvm-plan.md 3.5): the
    /// (game, car) being driven and the classified surface blend, or - on the SimHub menu after a restart -
    /// the last (game, car, identity tier) persisted in <c>RuntimeDocument.LastSlipAngleGame/Car/Tier</c>
    /// (Sealed surface). The Slip Angle twin of Lock/Slip's <c>LastKeyDataGame/Car</c>.
    /// <para/>
    /// Written on the data thread (<see cref="Observe"/>) and at Init (<see cref="Restore"/>); read from any
    /// thread (<see cref="Current"/> is an immutable snapshot).
    /// </summary>
    public sealed class SlipAngleKeyDataContext
    {
        /// <summary>One immutable context.</summary>
        public sealed class Snapshot
        {
            public Snapshot(string gameId, string carId, SourceNativeness tier, SurfaceBlend blend)
            {
                GameId = gameId ?? string.Empty;
                CarId = carId ?? string.Empty;
                Tier = tier;
                Blend = blend;
            }

            public string GameId { get; }
            public string CarId { get; }
            /// <summary>The identity tier (Native / Fallback / Unknown) in force for this context.</summary>
            public SourceNativeness Tier { get; }
            public SurfaceBlend Blend { get; }
        }

        private Snapshot _current;
        private volatile bool _live;

        /// <summary>Null on a first launch (nothing persisted, no game yet): the UI shows the default.</summary>
        public Snapshot Current => System.Threading.Volatile.Read(ref _current);

        /// <summary>Init, after the learners are imported: the persisted last context (Sealed surface).
        /// Ignored once a game ran this session, and for a null / empty game.</summary>
        public void Restore(string gameId, string carId, SourceNativeness tier)
        {
            if (_live || string.IsNullOrEmpty(gameId)) return;
            System.Threading.Volatile.Write(ref _current, new Snapshot(gameId, carId, tier, SlipAngleKeyDataModel.MenuBlend));
        }

        /// <summary>
        /// THE DISPLAY FOLLOWS THE KEY AT ONCE (owner, 2026-11-22; Lock/Slip's <c>FollowKeyDataDisplayContext</c> rule):
        /// any frame SimHub reports - the menu and pause included - moves the context to a new game or car straight away,
        /// on the Sealed surface, so a key with nothing of its own shows its borrowed or shipped start (and the half LED)
        /// before the first driven frame. DISPLAY ONLY: no learner is touched; <see cref="Observe"/> still sets the live
        /// context on every running frame. No game keeps the context (the restart display). An unknown car is no car in a
        /// new game and keeps the last car in the same game. The tier carries over within a game, else Unknown. True when
        /// the context moved (the caller persists it).
        /// </summary>
        public bool Follow(string gameId, string carId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            Snapshot cur = Current;
            bool sameGame = cur != null && string.Equals(cur.GameId, gameId, StringComparison.Ordinal);
            string car = !string.IsNullOrEmpty(carId) ? carId : sameGame ? cur.CarId : string.Empty;
            if (sameGame && string.Equals(cur.CarId, car, StringComparison.Ordinal)) return false;
            System.Threading.Volatile.Write(ref _current, new Snapshot(gameId, car, sameGame ? cur.Tier : SourceNativeness.Unknown,
                SlipAngleKeyDataModel.MenuBlend));
            return true;
        }

        /// <summary>A running Slip Angle frame: the live context. True when (game, car, tier) changed - the
        /// caller persists it then. A null / empty game is ignored.</summary>
        public bool Observe(string gameId, string carId, SourceNativeness tier, SurfaceBlend blend)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            carId = carId ?? string.Empty;
            Snapshot before = Current;
            _live = true;
            System.Threading.Volatile.Write(ref _current, new Snapshot(gameId, carId, tier, blend));
            return before == null
                || !string.Equals(before.GameId, gameId, StringComparison.Ordinal)
                || !string.Equals(before.CarId, carId, StringComparison.Ordinal)
                || before.Tier != tier;
        }

        /// <summary>The tier the key-data identity is chosen by: <paramref name="liveTier"/> (the in-session
        /// <see cref="SlipAngleIdentityTier"/>) once a game ran this session; before that (the menu after a
        /// restart) the persisted tier, so the tab, the panel and the hub pick the same Native / Fallback slot
        /// the context was saved with.</summary>
        public SourceNativeness IdentityTier(SourceNativeness liveTier)
        {
            if (_live) return liveTier;
            Snapshot s = Current;
            return s?.Tier ?? liveTier;
        }
    }
}

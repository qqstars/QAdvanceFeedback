using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Settings
{
    /// <summary>A channel's three key data points (SMax / S90 / S75), compared by value.</summary>
    public sealed class KeyDataTriple : IEquatable<KeyDataTriple>
    {
        public KeyDataTriple(double sMax, double s90, double s75) { SMax = sMax; S90 = s90; S75 = s75; }

        public double SMax { get; }
        public double S90 { get; }
        public double S75 { get; }

        public bool Equals(KeyDataTriple other) =>
            other != null && SMax.Equals(other.SMax) && S90.Equals(other.S90) && S75.Equals(other.S75);

        public override bool Equals(object obj) => Equals(obj as KeyDataTriple);
        public override int GetHashCode() => SMax.GetHashCode() ^ (S90.GetHashCode() * 31) ^ (S75.GetHashCode() * 17);
        public override string ToString() => $"{SMax}/{S90}/{S75}";
    }

    /// <summary>Where a learned SMax comes from, for the indicator's tooltip (owner, 2026-11-21).</summary>
    public enum KeyDataOrigin
    {
        /// <summary>Nothing learned: the starting value.</summary>
        None,
        /// <summary>This car's own at-limit evidence.</summary>
        Own,
        /// <summary>Saved in an earlier session without evidence (an older runtime file).</summary>
        EarlierSession,
        /// <summary>Borrowed from the same car on another surface.</summary>
        BorrowedSurface,
        /// <summary>Borrowed from another car in the same game.</summary>
        BorrowedCar,
        /// <summary>Borrowed from another game.</summary>
        BorrowedGame,
        /// <summary>Slip borrowing from Wheel Lock's learning.</summary>
        FromLock,
    }

    /// <summary>How mature each learned key data point is, 0..1 (see <see cref="KeyDataPanelModel.LearnedKeyDataMaturity"/>),
    /// and where SMax's maturity comes from.</summary>
    public sealed class KeyDataMaturity
    {
        public KeyDataMaturity(double sMax, double s90, double s75, KeyDataOrigin origin = KeyDataOrigin.Own, bool borrowed = false,
            double ownSMax = 0.0)
        {
            SMax = sMax; S90 = s90; S75 = s75; Origin = origin; Borrowed = borrowed; OwnSMax = ownSMax;
        }

        public double SMax { get; }
        public double S90 { get; }
        public double S75 { get; }
        /// <summary>What SMax's maturity reflects (the S90 / S75 of a Slip channel follow it).</summary>
        public KeyDataOrigin Origin { get; }
        /// <summary>SMax's shown maturity is borrowed (another surface / car / game, or Wheel Lock) - only while this
        /// key has no real data at all (owner, 2026-11-22). The indicator's left half then shows the colour of
        /// <see cref="OwnSMax"/> (Red: none) and its right half the borrowed level.</summary>
        public bool Borrowed { get; }
        /// <summary>This key's own SMax confidence, 0..1 (its own at-limit evidence only).</summary>
        public double OwnSMax { get; }
    }

    /// <summary>What a key data section shows for one channel's applied source.</summary>
    public sealed class KeyDataView
    {
        /// <summary>Auto-generate on: the values are a read-only readout of what the channel uses.</summary>
        public bool Auto { get; set; }
        public bool Editable { get; set; }
        /// <summary>False under Max-Grip-Only, where S90/S75 are derived from SMax, not configured.</summary>
        public bool ShowLowerAnchors { get; set; }
        /// <summary>Manual + Per-Game with no game running: there is no slot to show or write.</summary>
        public bool PerGameWithoutGame { get; set; }
        public double? SMax { get; set; }
        public double? S90 { get; set; }
        public double? S75 { get; set; }
    }

    /// <summary>
    /// The key data rules of the plugin settings page, for the channel's APPLIED source, shared with the
    /// native WheelLock/WheelSlip ShakeIt panels (docs\shakeit-native-effects-design.md, "Key data points
    /// in the ShakeIt panel"):
    /// <list type="bullet">
    /// <item>Auto: read-only; shows the learned SMax (S90/S75 derived from it), else the source's shipped
    /// reference, else nothing.</item>
    /// <item>Manual: editable; shows the slot the source's Global/Per-Game choice selects, else the
    /// shipped defaults as a starting point. Writes go through <see cref="TryCommitManual"/>, the page's
    /// own rule.</item>
    /// <item>Max-Grip-Only: SMax only.</item>
    /// </list>
    /// </summary>
    public static class KeyDataPanelModel
    {
        /// <summary>The source identity the engine keys this channel's learning by.</summary>
        public static string Identity(WheelChannelSettings ch) =>
            ch == null ? null : SourceIdentity.Compute(
                ch.SourceFrontLeft, ch.ScriptTypeFrontLeft.ToString(),
                ch.SourceFrontRight, ch.ScriptTypeFrontRight.ToString(),
                ch.SourceRearLeft, ch.ScriptTypeRearLeft.ToString(),
                ch.SourceRearRight, ch.ScriptTypeRearRight.ToString());

        /// <summary>
        /// <see cref="Identity(WheelChannelSettings)"/> plus, for a ShakeIt Built-in channel, its ShakeIt
        /// algorithm fingerprint - the key the engine learns that channel under
        /// (<see cref="Core.ShakeItEffects.ShakeItSourceResolution.ShakeItIdentity"/>; docs\shakeit-source-design.md
        /// §6). Equal to the one-argument form at the algorithm defaults (empty fingerprint) and for every
        /// other source.
        /// </summary>
        public static string Identity(WheelChannelSettings ch, bool isLockChannel)
        {
            string identity = Identity(ch);
            if (identity == null || ch.SourceMode != SourceMode.ShakeIt || ch.ShakeItAlgorithm == null) return identity;
            return identity + ch.ShakeItAlgorithm.Fingerprint(isLockChannel);
        }

        /// <summary>
        /// THE ONE DISPLAY RULE for learned key data (owner, 2026-11-19), used by the settings page push
        /// and pull, both ShakeIt panels and the plugin's one-time Manual seed: what the engine itself
        /// divides by - <see cref="KeyedScaleLearner.PublishedCeiling"/> for the exact
        /// (game, car, source) key. After a restart, with no driving yet, that IS the parameters file's
        /// value when the exact persist key exists, and the cold-start default when it does not - never
        /// another game's or car's value. As the session gathers evidence it moves exactly as the output
        /// does.
        /// <para/>
        /// S90/S75: Lock = SMax x the learned (persisted) physical anchor ratios, which start at 0.90 /
        /// 0.70; Slip = SMax x 0.90 / 0.70 (it has nothing to measure them from). Rounded to one decimal
        /// like every key-data box. Null when there is no engine, no game context, or no positive ceiling.
        /// </summary>
        /// <summary>
        /// THE MATURITY behind <see cref="LearnedKeyData"/> (docs\learning-maturity-indicator-report.md), 0..1 per
        /// value, for the same exact (game, car, source) key:
        /// <list type="bullet">
        /// <item>SMax = the highest of: <see cref="KeyedScaleLearner.CeilingHandoverConfidence"/> (this key's own
        /// at-limit evidence, persisted since v14); 50% for a ceiling saved in an earlier session without evidence;
        /// a borrowed reference - 50% same car on another surface, 30% another car in this game, 10% another game;
        /// and for Slip 30% while it borrows from Wheel Lock's learning. <see cref="KeyDataMaturity.Origin"/> says
        /// which (owner, 2026-11-21).</item>
        /// <item>Lock S90 / S75 = the lower of SMax's and that anchor's physical-ratio maturity (S90 = SMax x the
        /// ratio, so it is never more mature than either).</item>
        /// <item>Slip S90 / S75 = SMax's (they are derived from it).</item>
        /// </list>
        /// Null with no engine, no game context or no identity (the indicator then reads "not learned yet").
        /// </summary>
        /// <summary>The indicator's share for a ceiling saved in an earlier session without evidence.</summary>
        public const double EarlierSessionShare = 0.5;
        /// <summary>The indicator's shares for a borrowed reference: same car other surface / other car / other game.</summary>
        public const double BorrowedSurfaceShare = 0.5;
        public const double BorrowedCarShare = 0.3;
        public const double BorrowedGameShare = 0.1;
        /// <summary>The indicator's share while Slip borrows from Wheel Lock's learning.</summary>
        public const double FromLockShare = 0.3;
        /// <summary>The least a key with real data shows (1%, Orange "Learning - 1% confident"): its learning has
        /// started, so it never reads as "not learned yet".</summary>
        public const double MinimumLearningShare = 0.01;

        public static KeyDataMaturity LearnedKeyDataMaturity(NormalizedWheelLockSlipEngine engine, bool isLock,
            string gameId, string carId, string sourceIdentity)
        {
            if (engine == null || gameId == null || string.IsNullOrEmpty(sourceIdentity)) return null;
            string car = carId ?? string.Empty;
            KeyedScaleLearner learner = isLock ? engine.LockScaleLearner : engine.SlipScaleLearner;
            double sMax = ClampMath.To01(learner.CeilingHandoverConfidence(gameId, car, sourceIdentity));
            double own = sMax;
            KeyDataOrigin origin = sMax > 0.0 ? KeyDataOrigin.Own : KeyDataOrigin.None;
            bool borrowed = false;
            // BORROWING IS ONLY A COLD START (owner, 2026-11-22): the half LED and the "Borrowed from ..." label show only
            // while this game / car / source has NO real data at all. From its first real data on - in this session, or
            // reloaded from the parameters file after a restart - it is its own learning, still going: its own maturity
            // and the normal label, never a borrowed share. (Before, a key with a few own samples and its own confidence
            // still 0% kept the borrowed 30% and showed a full Yellow LED labelled "borrowed".)
            if (learner.HasOwnEvidence(gameId, car, sourceIdentity))
            {
                // Real data, not matured yet: learning has started, so never "not learned yet" (0%).
                if (sMax < MinimumLearningShare) sMax = MinimumLearningShare;
                origin = KeyDataOrigin.Own;
                own = sMax;
            }
            else
            {
                void Consider(double rate, KeyDataOrigin from)
                {
                    if (rate > sMax) { sMax = rate; origin = from; }
                }
                if (learner.HasColdWithoutEvidence(gameId, car, sourceIdentity)) Consider(EarlierSessionShare, KeyDataOrigin.EarlierSession);
                switch (learner.ResolveTier(gameId, car, sourceIdentity))
                {
                    case ColdStartTier.Tier4: Consider(BorrowedSurfaceShare, KeyDataOrigin.BorrowedSurface); break;
                    case ColdStartTier.Tier3: Consider(BorrowedCarShare, KeyDataOrigin.BorrowedCar); break;
                    case ColdStartTier.Tier2: Consider(BorrowedGameShare, KeyDataOrigin.BorrowedGame); break;
                }
                if (!isLock && learner.CrossChannelLockConfidence(gameId, car, sourceIdentity) > 0.0) Consider(FromLockShare, KeyDataOrigin.FromLock);
                borrowed = origin == KeyDataOrigin.BorrowedSurface || origin == KeyDataOrigin.BorrowedCar
                           || origin == KeyDataOrigin.BorrowedGame || origin == KeyDataOrigin.FromLock;
            }
            if (!isLock) return new KeyDataMaturity(sMax, sMax, sMax, origin, borrowed, own);
            double s90 = Math.Min(sMax, ClampMath.To01(engine.LockAnchors.PhysicalS90RatioConfidence(gameId, car, sourceIdentity)));
            double s75 = Math.Min(sMax, ClampMath.To01(engine.LockAnchors.PhysicalS75RatioConfidence(gameId, car, sourceIdentity)));
            return new KeyDataMaturity(sMax, s90, s75, origin, borrowed, own);
        }

        public static KeyDataTriple LearnedKeyData(NormalizedWheelLockSlipEngine engine, bool isLock,
            string gameId, string carId, string sourceIdentity)
        {
            if (engine == null || gameId == null || string.IsNullOrEmpty(sourceIdentity)) return null;
            string car = carId ?? string.Empty;
            KeyedScaleLearner learner = isLock ? engine.LockScaleLearner : engine.SlipScaleLearner;
            double? published = learner.PublishedCeiling(gameId, car, sourceIdentity, out _);
            if (!published.HasValue || !(published.Value > 0.0)) return null;

            double sMax = Math.Round(published.Value, 1);
            double s90, s75;
            // Lock: EXACTLY the S75 / S90 the output builds its curve from (owner, 2026-11-22: "SMax > S90 > S75 always") -
            // never a learned ratio on its own, which could exceed 1 (S90 54.6 / S75 54.1 over SMax 52.5). Without a
            // buildable curve (the output then rescales by SMax alone) and for Slip: the fixed shares of SMax.
            if (!isLock || !engine.TryResolveLockAnchors(gameId, car, sourceIdentity, published.Value, out s75, out s90))
                KeyDataPointSettings.DeriveLowerAnchors(sMax, out s90, out s75);
            s90 = Math.Round(s90, 1);
            s75 = Math.Round(s75, 1);
            // Rounding to 0.1 must not break the order either.
            if (!(s90 < sMax)) s90 = Math.Round(sMax - 0.1, 1);
            if (!(s75 < s90)) s75 = Math.Round(s90 - 0.1, 1);
            return new KeyDataTriple(sMax, s90, s75);
        }

        /// <summary>What the key data section shows for the channel's applied source. <paramref name="learned"/>
        /// is <see cref="LearnedKeyData"/> (null when nothing is known).</summary>
        public static KeyDataView Resolve(WheelChannelSettings ch, bool isLock, string gameId, KeyDataTriple learned, KeyDataPointDefaults defaults)
        {
            var view = new KeyDataView();
            if (ch?.KeyDataPoints == null) return view;

            KeyDataPointSettings k = ch.KeyDataPoints;
            // The engine's key, fingerprint included: the slot (Auto flag, manual values) is per key, and
            // the shipped starting point is classified on the source type (KeyDataPointSettings.ClassifyExact
            // ignores the fingerprint), exactly as the engine does.
            string id = Identity(ch, isLock);
            view.ShowLowerAnchors = ch.NormalizePattern != NormalizePattern.MaxGripOnly;
            view.Auto = k.GetAutoGenerate(id);
            view.Editable = !view.Auto;
            defaults = defaults ?? KeyDataPointDefaults.CreateShipped();

            if (view.Auto)
            {
                if (learned != null && learned.SMax > 0.0)
                    Set(view, learned.SMax, learned.S90, learned.S75);
                else if (KeyDataPointSettings.TryResolveShippedDefaults(id, isLock, defaults, out double a, out double b, out double c))
                    Set(view, a, b, c);
                return view;
            }

            if (k.GetPerGame(id) && string.IsNullOrEmpty(gameId))
            {
                view.PerGameWithoutGame = true;
                return view;
            }

            if (k.TryGetManual(gameId, id, out double m1, out double m2, out double m3)) Set(view, m1, m2, m3);
            else if (KeyDataPointSettings.TryResolveShippedDefaults(id, isLock, defaults, out double d1, out double d2, out double d3))
                Set(view, d1, d2, d3);
            return view;
        }

        /// <summary>
        /// Write typed values into the slot the STORED flags select - refused under Auto, for Per-Game
        /// with no game, and for an invalid triple. Under Max-Grip-Only S90/S75 are derived from SMax.
        /// Latches the slot (seeded) so the plugin's one-time learned-value seed stands down.
        /// </summary>
        public static bool TryCommitManual(KeyDataPointSettings k, string gameId, string sourceIdentity,
            double? sMax, double? s90, double? s75, bool maxGripOnly)
        {
            if (k == null || k.GetAutoGenerate(sourceIdentity)) return false;
            if (k.GetPerGame(sourceIdentity) && string.IsNullOrEmpty(gameId)) return false;

            double a = sMax ?? 0.0, b = s90 ?? 0.0, c = s75 ?? 0.0;
            if (maxGripOnly) KeyDataPointSettings.DeriveLowerAnchors(a, out b, out c);
            if (!KeyDataPointSettings.IsValid(a, b, c)) return false;

            k.SetManual(gameId, sourceIdentity, a, b, c, seeded: true);
            return true;
        }

        private static void Set(KeyDataView v, double a, double b, double c) { v.SMax = a; v.S90 = b; v.S75 = c; }
    }
}

using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Tests.Frozen;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// BEHAVIOUR-PRESERVATION PROOF for the <see cref="ColdStartTierResolver"/> extraction (slip-angle
    /// plan, Task 8; docs\slip-angle-design.md sections 4 and 9.5.7). <see cref="KeyedScaleLearner"/>'s
    /// tier resolution now delegates its candidate scan to the shared resolver while keeping its own
    /// write-once cache; after every single operation of a randomised stream, the shipped learner must
    /// agree EXACTLY (tier, borrowed key, bit pattern of the reference ceiling) with
    /// <see cref="FrozenColdStartTierResolution"/>, a verbatim copy of the pre-extraction code fed the
    /// identical primary-tier evidence in the identical insertion order.
    /// <para/>
    /// The streams mix: several games / cars / sources (empty included) / surface buckets (empty
    /// included), null query ids, evidence that is valid, non-positive, non-finite or zero-weight (so
    /// the learner's own guards decide whether a key is created), non-qualifying observations (keys with
    /// a Count but no usable average), queries with and without a surface hint (null, empty, real),
    /// surfaced own keys, lockToCache true and false (the write-once cache and its re-scan path),
    /// repeated queries on the same key, and Reset mid-stream. Sample-count ties are frequent by
    /// construction, so the lexicographic tie-break is exercised constantly.
    /// </summary>
    public class ColdStartTierResolverEquivalenceTests
    {
        private const int Seeds = 300;
        private const int OpsPerSeed = 600;

        private static readonly string[] Games = { "GameA", "GameB", "GameC" };
        private static readonly string[] Cars = { "CarA", "CarB", "CarC" };
        private static readonly string[] Sources = { "Raw", "ShakeIt", "" };
        private static readonly string[] Surfaces = { "", "Dry", "Wet", "Gravel" };
        private static readonly string[] QuerySurfaces = { null, "", "Dry", "Wet", "Gravel", "Snow" };

        private sealed class Pair
        {
            public KeyedScaleLearner Live = new KeyedScaleLearner();
            public Dictionary<string, OnlineDistributionLearner> FrozenStore = new Dictionary<string, OnlineDistributionLearner>(StringComparer.Ordinal);
            public FrozenColdStartTierResolution Frozen;
            public HashSet<string> FrozenCached = new HashSet<string>(StringComparer.Ordinal);

            public Pair() { Frozen = new FrozenColdStartTierResolution(FrozenStore); }

            public void Reset()
            {
                Live.Reset();
                FrozenStore.Clear();
                Frozen = new FrozenColdStartTierResolution(FrozenStore);
                FrozenCached.Clear();
            }

            // Mirrors KeyedScaleLearner.GetOrCreate (verbatim semantics: same key, same insertion point).
            public OnlineDistributionLearner GetOrCreate(string g, string c, string s, string surf)
            {
                string key = KeyedGripLearner.MakeKey(g, c, s, surf);
                if (!FrozenStore.TryGetValue(key, out OnlineDistributionLearner learner))
                {
                    learner = new OnlineDistributionLearner();
                    FrozenStore[key] = learner;
                }
                return learner;
            }
        }

        private static string Pick(Random r, string[] values) => values[r.Next(values.Length)];

        private static string PickQueryId(Random r, string[] values) => r.Next(12) == 0 ? null : Pick(r, values);

        private static double PickValue(Random r)
        {
            switch (r.Next(10))
            {
                case 0: return 0.0;
                case 1: return -1.0;
                case 2: return double.NaN;
                case 3: return double.PositiveInfinity;
                default: return 0.1 + r.NextDouble() * 99.9;
            }
        }

        private static double PickWeight(Random r)
        {
            switch (r.Next(10))
            {
                case 0: return 0.0;
                case 1: return double.NaN;
                case 2: return -0.5;
                case 3:
                case 4: return 1.0;
                default: return r.NextDouble();
            }
        }

        [Fact]
        public void Shipped_learner_matches_the_frozen_pre_extraction_resolver_bit_for_bit()
        {
            Assert.False(KeyedScaleLearner.ForceTier1ForTesting);
            long resolveTierCalls = 0, resolveReferenceCalls = 0, nonTier1 = 0, cacheHits = 0;
            var tiersSeen = new HashSet<ColdStartTier>();

            for (int seed = 0; seed < Seeds; seed++)
            {
                var r = new Random(seed);
                var p = new Pair();
                for (int op = 0; op < OpsPerSeed; op++)
                {
                    int kind = r.Next(100);
                    if (kind < 30)
                    {
                        string g = Pick(r, Games), c = Pick(r, Cars), s = Pick(r, Sources), surf = Pick(r, Surfaces);
                        double v = PickValue(r), w = PickWeight(r);
                        p.Live.ObserveAtPhysicalLimit(g, c, s, v, w, surf);
                        // KeyedScaleLearner.ObserveAtPhysicalLimit's own guards, then its GetOrCreate + AddValue.
                        if (ClampMath.IsFinite(v) && v > 0.0 && ClampMath.IsFinite(w) && w > 0.0)
                            p.GetOrCreate(g, c, s, surf).AddValue(v, w);
                    }
                    else if (kind < 36)
                    {
                        string g = Pick(r, Games), c = Pick(r, Cars), s = Pick(r, Sources), surf = Pick(r, Surfaces);
                        p.Live.ObserveNonQualifyingAtPhysicalLimit(g, c, s, surf);
                        p.GetOrCreate(g, c, s, surf).AddNonQualifyingObservation();
                    }
                    else if (kind < 60)
                    {
                        string g = PickQueryId(r, Games), c = PickQueryId(r, Cars), s = PickQueryId(r, Sources), surf = Pick(r, QuerySurfaces);
                        ColdStartTier live = p.Live.ResolveTier(g, c, s, surf);
                        ColdStartTier frozen = p.Frozen.ResolveTier(g, c, s, surf);
                        Assert.Equal(frozen, live);
                        tiersSeen.Add(live);
                        resolveTierCalls++;
                    }
                    else if (kind < 99)
                    {
                        string g = PickQueryId(r, Games), c = PickQueryId(r, Cars), s = PickQueryId(r, Sources), surf = Pick(r, QuerySurfaces);
                        // Mostly the default (unsurfaced) own key, as both live callers pass; sometimes a
                        // surfaced one, which the shared resolver must handle identically too.
                        string ownKey = r.Next(5) == 0
                            ? KeyedGripLearner.MakeKey(g, c, s, Pick(r, Surfaces))
                            : KeyedGripLearner.MakeKey(g, c, s);
                        bool lockToCache = r.Next(2) == 0;
                        string surfArg = surf ?? string.Empty; // both live call sites pass currentSurfaceBucket ?? ""
                        string cacheKey = ownKey + "|surf#" + surfArg;
                        bool frozenWasCached = p.FrozenCached.Contains(cacheKey);

                        // What a fresh, uncached scan of the frozen code borrows right now.
                        var fresh = new FrozenColdStartTierResolution(p.FrozenStore);
                        fresh.ResolveReference(g, c, s, surfArg, ownKey, lockToCache: true, out _);
                        string freshKey = fresh.TryGetCachedReferenceKey(ownKey, surfArg);

                        ColdStartTier liveTier = p.Live.ResolveReference(g, c, s, surfArg, ownKey, lockToCache, out double liveCeiling, out string liveKey);
                        ColdStartTier frozenTier = p.Frozen.ResolveReference(g, c, s, surfArg, ownKey, lockToCache, out double frozenCeiling);
                        if (lockToCache) p.FrozenCached.Add(cacheKey);
                        string expectedKey = frozenWasCached ? p.Frozen.TryGetCachedReferenceKey(ownKey, surfArg) : freshKey;

                        Assert.Equal(frozenTier, liveTier);
                        Assert.Equal(BitConverter.DoubleToInt64Bits(frozenCeiling), BitConverter.DoubleToInt64Bits(liveCeiling));
                        Assert.Equal(expectedKey, liveKey);
                        if (frozenTier != ColdStartTier.Tier1) nonTier1++;
                        if (frozenWasCached) cacheHits++;
                        tiersSeen.Add(liveTier);
                        resolveReferenceCalls++;
                    }
                    else
                    {
                        p.Reset();
                    }
                }
            }

            // The stream must actually have exercised what it claims to.
            Assert.Equal(4, tiersSeen.Count);
            Assert.True(resolveTierCalls > 10000, $"resolveTier calls {resolveTierCalls}");
            Assert.True(resolveReferenceCalls > 10000, $"resolveReference calls {resolveReferenceCalls}");
            Assert.True(nonTier1 > 1000, $"non-Tier1 {nonTier1}");
            Assert.True(cacheHits > 1000, $"cache hits {cacheHits}");
        }

        [Fact]
        public void Shared_resolver_alone_matches_the_frozen_scan_over_the_same_store()
        {
            // The resolver driven directly over a learner-shaped store, with no KeyedScaleLearner in the
            // loop - the shape the Slip Angle learner will use over its own keys.
            var resolver = new ColdStartTierResolver();
            for (int seed = 0; seed < Seeds; seed++)
            {
                var r = new Random(10000 + seed);
                var store = new Dictionary<string, OnlineDistributionLearner>(StringComparer.Ordinal);
                int observations = r.Next(1, 40);
                for (int i = 0; i < observations; i++)
                {
                    string key = KeyedGripLearner.MakeKey(Pick(r, Games), Pick(r, Cars), Pick(r, Sources), Pick(r, Surfaces));
                    if (!store.TryGetValue(key, out OnlineDistributionLearner l)) store[key] = l = new OnlineDistributionLearner();
                    if (r.Next(6) == 0) l.AddNonQualifyingObservation(); else l.AddValue(0.1 + r.NextDouble() * 99.9, 1.0);
                }

                bool HasEvidence(string k) => store.TryGetValue(k, out OnlineDistributionLearner l) && l != null && l.Count > 0;
                bool TryGetReference(string k, out int samples, out double ceiling)
                {
                    double? avg = store.TryGetValue(k, out OnlineDistributionLearner l) ? l.GetAverage() : null;
                    samples = avg.HasValue ? l.Count : 0;
                    ceiling = avg ?? 0.0;
                    return avg.HasValue;
                }

                for (int q = 0; q < 30; q++)
                {
                    string g = PickQueryId(r, Games), c = PickQueryId(r, Cars), s = PickQueryId(r, Sources);
                    string surf = Pick(r, QuerySurfaces) ?? string.Empty;
                    string ownKey = KeyedGripLearner.MakeKey(g, c, s);

                    var frozen = new FrozenColdStartTierResolution(store);
                    ColdStartTier expected = frozen.ResolveReference(g, c, s, surf, ownKey, lockToCache: true, out double expectedCeiling);
                    ColdStartTier actual = resolver.Resolve(g, c, s, surf, ownKey, HasEvidence, store.Keys, TryGetReference,
                        out string borrowedKey, out double ceiling);

                    Assert.Equal(expected, actual);
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expectedCeiling), BitConverter.DoubleToInt64Bits(ceiling));
                    Assert.Equal(frozen.TryGetCachedReferenceKey(ownKey, surf), borrowedKey);
                }
            }
        }
    }
}

using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The shared borrowing-tier resolver extracted from <see cref="KeyedScaleLearner"/> (slip-angle plan,
    /// Task 8; docs\slip-angle-design.md sections 4 and 9.5.7), driven directly over a plain key set so
    /// its contract is pinned independently of any learner. Bit-identity with the pre-extraction
    /// learner is proven separately by <see cref="ColdStartTierResolverEquivalenceTests"/>.
    /// </summary>
    public class ColdStartTierResolverTests
    {
        private const string Raw = "Raw";
        private const string ShakeIt = "ShakeIt";

        /// <summary>A tiny candidate store: key -> (samples, ceiling). samples == 0 means "known key, no
        /// evidence yet"; a null ceiling means "evidence, but no usable average".</summary>
        private sealed class Store
        {
            private readonly Dictionary<string, (int Samples, double? Ceiling)> _entries = new Dictionary<string, (int, double?)>();

            public Store Add(string game, string car, string source, string surface, int samples, double? ceiling)
            {
                _entries[ColdStartTierResolver.Key(game, car, source, surface)] = (samples, ceiling);
                return this;
            }

            public IEnumerable<string> Keys => _entries.Keys;

            public bool HasEvidence(string key) => _entries.TryGetValue(key, out var e) && e.Samples > 0;

            public bool TryGetReference(string key, out int samples, out double ceiling)
            {
                if (_entries.TryGetValue(key, out var e) && e.Ceiling.HasValue)
                {
                    samples = e.Samples;
                    ceiling = e.Ceiling.Value;
                    return true;
                }
                samples = 0;
                ceiling = 0.0;
                return false;
            }

            public ColdStartTier Resolve(string game, string car, string source, string surface, out string borrowedKey, out double ceiling)
                => new ColdStartTierResolver().Resolve(game, car, source, surface,
                    ColdStartTierResolver.Key(game, car, source, ""), HasEvidence, Keys, TryGetReference,
                    out borrowedKey, out ceiling);
        }

        private static Store EveryTier() => new Store()
            .Add("GameB", "CarX", ShakeIt, "", 50, 20.0)   // Tier 2 candidate for GameA/CarA
            .Add("GameA", "CarB", ShakeIt, "", 40, 30.0)   // Tier 3 candidate
            .Add("GameA", "CarA", ShakeIt, "Dry", 30, 40.0); // Tier 4 candidate when querying "Wet"

        [Theory]
        [InlineData("Wet", true, true, true, ColdStartTier.Tier4, "GameA|#|CarA|#|ShakeIt|#|Dry", 40.0)]
        [InlineData("Wet", false, true, true, ColdStartTier.Tier3, "GameA|#|CarB|#|ShakeIt|#|", 30.0)]
        [InlineData("Wet", false, false, true, ColdStartTier.Tier2, "GameB|#|CarX|#|ShakeIt|#|", 20.0)]
        [InlineData("Wet", false, false, false, ColdStartTier.Tier1, null, 0.0)]
        public void Picks_most_specific_tier(string surface, bool tier4, bool tier3, bool tier2,
            ColdStartTier expected, string expectedKey, double expectedCeiling)
        {
            var store = new Store();
            if (tier2) store.Add("GameB", "CarX", ShakeIt, "", 50, 20.0);
            if (tier3) store.Add("GameA", "CarB", ShakeIt, "", 40, 30.0);
            if (tier4) store.Add("GameA", "CarA", ShakeIt, "Dry", 30, 40.0);

            ColdStartTier tier = store.Resolve("GameA", "CarA", ShakeIt, surface, out string key, out double ceiling);

            Assert.Equal(expected, tier);
            Assert.Equal(expectedKey, key);
            Assert.Equal(expectedCeiling, ceiling);
        }

        [Fact]
        public void Same_source_only()
        {
            // Every tier has a candidate - but only under ShakeIt. A Raw key must never borrow from them.
            Store store = EveryTier();
            Assert.Equal(ColdStartTier.Tier1, store.Resolve("GameA", "CarA", Raw, "Wet", out string key, out double ceiling));
            Assert.Null(key);
            Assert.Equal(0.0, ceiling);

            // ...and a Raw candidate is found for a Raw key even when a richer ShakeIt one exists.
            store.Add("GameC", "CarQ", Raw, "", 1, 7.0);
            Assert.Equal(ColdStartTier.Tier2, store.Resolve("GameA", "CarA", Raw, "Wet", out key, out ceiling));
            Assert.Equal("GameC|#|CarQ|#|Raw|#|", key);
            Assert.Equal(7.0, ceiling);
        }

        [Fact]
        public void No_surface_hint_never_offers_tier4()
        {
            Store store = new Store().Add("GameA", "CarA", ShakeIt, "Dry", 30, 40.0);
            Assert.Equal(ColdStartTier.Tier1, store.Resolve("GameA", "CarA", ShakeIt, "", out _, out _));
            Assert.Equal(ColdStartTier.Tier1, store.Resolve("GameA", "CarA", ShakeIt, null, out _, out _));
        }

        [Fact]
        public void Current_surface_with_own_evidence_is_not_cold_for_tier4()
        {
            Store store = new Store()
                .Add("GameA", "CarA", ShakeIt, "Dry", 30, 40.0)
                .Add("GameA", "CarA", ShakeIt, "Wet", 1, 45.0);
            // Querying "Wet": Wet itself has evidence, so Dry is not offered - and Wet is the query's own
            // surface, so it is not a candidate either.
            Assert.Equal(ColdStartTier.Tier1, store.Resolve("GameA", "CarA", ShakeIt, "Wet", out _, out _));
        }

        [Fact]
        public void Within_a_tier_most_samples_wins_then_smallest_key()
        {
            Store store = new Store()
                .Add("GameA", "CarC", ShakeIt, "", 10, 1.0)
                .Add("GameA", "CarB", ShakeIt, "", 20, 2.0)
                .Add("GameA", "CarD", ShakeIt, "", 20, 3.0);
            Assert.Equal(ColdStartTier.Tier3, store.Resolve("GameA", "CarA", ShakeIt, "", out string key, out double ceiling));
            Assert.Equal("GameA|#|CarB|#|ShakeIt|#|", key);
            Assert.Equal(2.0, ceiling);
        }

        [Fact]
        public void Candidates_without_evidence_or_average_are_skipped()
        {
            Store store = new Store()
                .Add("GameA", "CarB", ShakeIt, "", 0, 9.0)    // known, no evidence
                .Add("GameA", "CarC", ShakeIt, "", 5, null)   // evidence, no average
                .Add("GameB", "CarX", ShakeIt, "", 1, 4.0);
            Assert.Equal(ColdStartTier.Tier2, store.Resolve("GameA", "CarA", ShakeIt, "", out string key, out _));
            Assert.Equal("GameB|#|CarX|#|ShakeIt|#|", key);
        }

        [Fact]
        public void Own_key_is_never_its_own_candidate()
        {
            Store store = new Store().Add("GameA", "CarA", ShakeIt, "", 99, 50.0);
            Assert.Equal(ColdStartTier.Tier1, store.Resolve("GameA", "CarA", ShakeIt, "Wet", out _, out _));
        }

        [Fact]
        public void Key_round_trips_and_matches_the_learner_key_shape()
        {
            string key = ColdStartTierResolver.Key("G", "C", "S", "Wet");
            Assert.Equal(KeyedGripLearner.MakeKey("G", "C", "S", "Wet"), key);
            Assert.True(ColdStartTierResolver.TryParseKey(key, out string g, out string c, out string s, out string surf));
            Assert.Equal(("G", "C", "S", "Wet"), (g, c, s, surf));

            Assert.Equal(KeyedGripLearner.MakeKey(null, null, null, null), ColdStartTierResolver.Key(null, null, null, null));
            Assert.False(ColdStartTierResolver.TryParseKey("G|#|C", out _, out _, out _, out _));
            Assert.False(ColdStartTierResolver.TryParseKey(null, out g, out _, out _, out _));
            Assert.Equal(string.Empty, g);
        }
    }
}

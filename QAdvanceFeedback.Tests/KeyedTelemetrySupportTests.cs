using System.Collections.Generic;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// PER-GAME TELEMETRY SUPPORT DETECTION (telemetry-integrity pass, item 2) -
    /// <see cref="KeyedTelemetrySupport"/>. See that class's own remarks for why this exists (the ONE
    /// SimHub field this plugin's own audit found no <c>FeedbackCapabilities</c> flag covers at all -
    /// loose-surface reporting) and the sustained-evidence/promotion-only/keyed-by-game-alone design.
    /// </summary>
    public class KeyedTelemetrySupportTests
    {
        [Fact]
        public void An_unseen_game_defaults_to_absent()
        {
            var detector = new KeyedTelemetrySupport();
            Assert.False(detector.IsSupported("NeverSeenGame"));
        }

        [Fact]
        public void A_single_true_observation_does_not_promote_on_its_own()
        {
            var detector = new KeyedTelemetrySupport();
            detector.Observe("GameA", true);

            Assert.False(detector.IsSupported("GameA"),
                "a single, possibly-glitched true reading must not be enough to promote a game on its own");
        }

        [Fact]
        public void Sustained_true_evidence_promotes_the_game()
        {
            var detector = new KeyedTelemetrySupport();
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                detector.Observe("GameA", true);

            Assert.True(detector.IsSupported("GameA"));
        }

        [Fact]
        public void False_and_null_observations_never_count_toward_promotion_however_many_there_are()
        {
            var detector = new KeyedTelemetrySupport();
            for (int i = 0; i < 10000; i++) detector.Observe("GameA", false);
            for (int i = 0; i < 10000; i++) detector.Observe("GameA", null);

            Assert.False(detector.IsSupported("GameA"),
                "no amount of false/null evidence should ever promote a game - only sustained TRUE readings count");
        }

        [Fact]
        public void Once_promoted_a_game_is_never_demoted_by_a_later_false_reading()
        {
            var detector = new KeyedTelemetrySupport();
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                detector.Observe("GameA", true);
            Assert.True(detector.IsSupported("GameA"));

            for (int i = 0; i < 5000; i++) detector.Observe("GameA", false);

            Assert.True(detector.IsSupported("GameA"), "promotion must never be reverted, however much false evidence follows");
        }

        [Fact]
        public void Support_is_keyed_by_game_only_not_by_any_other_dimension()
        {
            var detector = new KeyedTelemetrySupport();
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                detector.Observe("GameA", true);

            // A DIFFERENT game gets no benefit from GameA's own promotion.
            Assert.True(detector.IsSupported("GameA"));
            Assert.False(detector.IsSupported("GameB"));
        }

        [Fact]
        public void ExportAll_only_ever_contains_promoted_games()
        {
            var detector = new KeyedTelemetrySupport();
            detector.Observe("GameA", true); // one observation - not yet promoted
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                detector.Observe("GameB", true); // promoted

            var exported = detector.ExportAll();

            Assert.DoesNotContain("GameA", exported.Keys);
            Assert.Contains("GameB", exported.Keys);
            Assert.True(exported["GameB"]);
        }

        [Fact]
        public void ExportAll_then_ImportAll_round_trips_promoted_games_and_trusts_them_from_frame_one()
        {
            var detector = new KeyedTelemetrySupport();
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                detector.Observe("GameA", true);

            var exported = detector.ExportAll();

            var restored = new KeyedTelemetrySupport();
            Assert.False(restored.IsSupported("GameA")); // before import: unknown, absent by default
            restored.ImportAll(exported);

            // Trusted immediately, before a single Observe call this "session".
            Assert.True(restored.IsSupported("GameA"));
        }

        [Fact]
        public void Reset_clears_every_games_state()
        {
            var detector = new KeyedTelemetrySupport();
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                detector.Observe("GameA", true);

            detector.Reset();

            Assert.False(detector.IsSupported("GameA"));
            Assert.Empty(detector.ExportAll());
        }

        [Fact]
        public void ImportAll_ignores_null_or_empty_input()
        {
            var detector = new KeyedTelemetrySupport();
            detector.ImportAll(null);
            Assert.Empty(detector.ExportAll());

            detector.ImportAll(new Dictionary<string, bool>());
            Assert.Empty(detector.ExportAll());
        }
    }
}

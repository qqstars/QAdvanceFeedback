using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The restart display rule (owner, 2026-11-19): the settings page, the ShakeIt panels and the
    /// one-time seed show what the engine itself uses - <see cref="KeyedScaleLearner.PublishedCeiling"/>.
    /// After a restart that is the parameters file's value when the EXACT persist key
    /// (game, car, source, surface - whatever <see cref="KeyedGripLearner.MakeKey"/> builds) exists, and the
    /// cold-start default otherwise. Never another key's value.
    /// </summary>
    public class KeyDataDisplayRuleTests
    {
        private const string Game = "fh6", Car = "car1", Raw = "Raw";

        private static NormalizedWheelLockSlipEngine RestartedWith(double persistedSMax, bool isLock)
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var state = new Dictionary<string, ScaleLearnerState>
            {
                [KeyedGripLearner.MakeKey(Game, Car, Raw)] = new ScaleLearnerState { ColdCeiling = persistedSMax, ColdIsPrimaryTier = true },
            };
            (isLock ? engine.LockScaleLearner : engine.SlipScaleLearner).ImportAll(state);
            return engine;
        }

        [Fact]
        public void An_exact_persisted_key_is_shown_after_a_restart()
        {
            NormalizedWheelLockSlipEngine engine = RestartedWith(74.4, isLock: false);

            KeyDataTriple shown = KeyDataPanelModel.LearnedKeyData(engine, isLock: false, Game, Car, Raw);

            Assert.NotNull(shown);
            Assert.Equal(74.4, shown.SMax, 6);
            Assert.Equal(67.0, shown.S90, 6);   // 74.4 x 0.90, one decimal
            Assert.Equal(52.1, shown.S75, 6);   // 74.4 x 0.70, one decimal
        }

        /// <summary>Owner, 2026-11-22: the page shows EXACTLY the S75 / S90 the Lock output builds its curve from
        /// (<see cref="NormalizedWheelLockSlipEngine.TryResolveLockAnchors"/>), so always SMax &gt; S90 &gt; S75.</summary>
        [Fact]
        public void Lock_lower_anchors_are_the_ones_the_output_uses()
        {
            NormalizedWheelLockSlipEngine engine = RestartedWith(74.4, isLock: true);
            KeyDataTriple shown = KeyDataPanelModel.LearnedKeyData(engine, isLock: true, Game, Car, Raw);

            Assert.True(engine.TryResolveLockAnchors(Game, Car, Raw, 74.4, out double s75, out double s90));
            Assert.Equal(74.4, shown.SMax, 6);
            Assert.Equal(System.Math.Round(s90, 1), shown.S90, 6);
            Assert.Equal(System.Math.Round(s75, 1), shown.S75, 6);
            Assert.True(shown.SMax > shown.S90 && shown.S90 > shown.S75);
        }

        [Theory]
        [InlineData(Game, "another-car")]
        [InlineData("another-game", Car)]
        public void Without_the_exact_key_the_value_of_another_key_is_never_shown(string game, string car)
        {
            NormalizedWheelLockSlipEngine engine = RestartedWith(74.4, isLock: false);

            KeyDataTriple shown = KeyDataPanelModel.LearnedKeyData(engine, isLock: false, game, car, Raw);

            // The cold-start default (what the engine itself starts that key with), not 74.4.
            double? engineStart = engine.SlipScaleLearner.PublishedCeiling(game, car, Raw, out _);
            if (shown == null) Assert.False(engineStart.HasValue && engineStart.Value > 0.0);
            else
            {
                Assert.NotEqual(74.4, shown.SMax);
                Assert.Equal(System.Math.Round(engineStart.Value, 1), shown.SMax, 6);
            }
        }

        [Fact]
        public void No_context_shows_nothing_learned()
        {
            NormalizedWheelLockSlipEngine engine = RestartedWith(74.4, isLock: false);
            Assert.Null(KeyDataPanelModel.LearnedKeyData(engine, isLock: false, null, null, Raw));
            Assert.Null(KeyDataPanelModel.LearnedKeyData(null, isLock: false, Game, Car, Raw));
        }

        [Fact]
        public void The_auto_view_shows_the_learned_triple_as_given()
        {
            var ch = WheelChannelSettings.CreateLockDefaults();
            ch.KeyDataPoints.SetAutoGenerate(KeyDataPanelModel.Identity(ch), true);

            KeyDataView v = KeyDataPanelModel.Resolve(ch, true, Game, new KeyDataTriple(74.4, 65.1, 50.3), KeyDataPointDefaults.CreateShipped());

            Assert.Equal(74.4, v.SMax);
            Assert.Equal(65.1, v.S90);
            Assert.Equal(50.3, v.S75);
        }
    }
}

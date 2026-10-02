using QAdvanceFeedback;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A DRIVER-CONFIGURED COLD-START SMax REACHES THE LIVE CALIBRATION (owner, 2026-09-28).
    /// <para/>
    /// <see cref="KnownSourceColdStartReference"/> resolves by identity, and a Custom source is whatever
    /// the driver typed - <see cref="SourceIdentity"/> hashes scripted text, so there is nothing to
    /// classify and no measured number to offer. The reference is configured instead, and this is the
    /// path that carries it into <see cref="KeyedScaleLearner"/> rather than leaving it in the UI.
    /// <para/>
    /// GUARDED BY IDENTITY rather than by a "clear this on switch" call: the value belongs to ONE
    /// source configuration, so a channel that switches source - or falls back to Raw because its own
    /// source cannot work on this title - stops using it automatically.
    /// </summary>
    public class ConfiguredColdStartTests
    {
        private readonly ITestOutputHelper _out;
        public ConfiguredColdStartTests(ITestOutputHelper output) { _out = output; }

        private const string Game = "F12025";
        private const string Car = "Car1";
        private const string CustomIdentity = "NCalc:aaaa~NCalc:bbbb~NCalc:cccc~NCalc:dddd";

        /// <summary>The published ceiling for a key with no evidence at all - which is the cold start,
        /// and therefore exactly what the configured reference is supposed to set.</summary>
        private static double? ColdCeiling(KeyedScaleLearner learner, string identity)
            => learner.LearnedCeiling(Game, Car, identity, out _);

        [Fact]
        public void With_no_override_an_unrecognised_source_still_has_no_cold_reference()
        {
            // The pre-existing contract: a source nobody has measured gets plain identity, not a guess.
            var learner = new KeyedScaleLearner(isLockChannel: true);
            Assert.Null(ColdCeiling(learner, CustomIdentity));
        }

        [Fact]
        public void A_configured_reference_becomes_the_cold_ceiling()
        {
            var learner = new KeyedScaleLearner(isLockChannel: true)
            {
                ConfiguredColdStartIdentity = CustomIdentity,
                ConfiguredColdStartSMax = 42.0,
            };

            double? ceiling = ColdCeiling(learner, CustomIdentity);
            _out.WriteLine($"cold ceiling with configured 42 -> {ceiling}");
            Assert.Equal(42.0, ceiling.Value, 6);
        }

        [Fact]
        public void It_applies_ONLY_to_the_identity_it_was_configured_for()
        {
            // THE POINT OF KEYING IT BY IDENTITY. A channel that switches source must stop using the
            // previous source's number immediately, with no explicit reset to forget.
            var learner = new KeyedScaleLearner(isLockChannel: true)
            {
                ConfiguredColdStartIdentity = CustomIdentity,
                ConfiguredColdStartSMax = 42.0,
            };

            Assert.Equal(42.0, ColdCeiling(learner, CustomIdentity).Value, 6);
            Assert.Null(ColdCeiling(learner, "NCalc:something~NCalc:else~NCalc:again~NCalc:more"));
        }

        [Fact]
        public void A_channel_fallen_back_to_Raw_gets_Raws_reference_not_the_custom_one()
        {
            // The Viper-on-an-unsupported-game case. The plugin swaps the identity to Raw's, so the
            // override stops matching and the shipped Raw number applies - which is the correct scale
            // for the values actually being read.
            var learner = new KeyedScaleLearner(isLockChannel: true)
            {
                ConfiguredColdStartIdentity = CustomIdentity,
                ConfiguredColdStartSMax = 42.0,
            };

            string rawIdentity = Settings.RawSourceFallback.RawIdentity(true);
            double? ceiling = ColdCeiling(learner, rawIdentity);

            _out.WriteLine($"fallen back to Raw -> {ceiling}");
            Assert.Equal(KnownSourceColdStartReference.LockRawSMax, ceiling.Value, 6);
        }

        [Fact]
        public void An_override_takes_precedence_over_the_shipped_table_for_that_identity()
        {
            // A driver who points Custom at something that HAPPENS to look like a known source still
            // gets their own number - they configured it deliberately.
            string rawIdentity = Settings.RawSourceFallback.RawIdentity(true);
            var learner = new KeyedScaleLearner(isLockChannel: true)
            {
                ConfiguredColdStartIdentity = rawIdentity,
                ConfiguredColdStartSMax = 33.0,
            };

            Assert.Equal(33.0, ColdCeiling(learner, rawIdentity).Value, 6);
        }

        [Fact]
        public void A_zero_or_negative_override_is_ignored()
        {
            // "Not configured" must behave exactly as it did before this existed - a zero ceiling would
            // divide the rescale by nothing.
            foreach (double bad in new[] { 0.0, -1.0 })
            {
                var learner = new KeyedScaleLearner(isLockChannel: true)
                {
                    ConfiguredColdStartIdentity = CustomIdentity,
                    ConfiguredColdStartSMax = bad,
                };
                Assert.Null(ColdCeiling(learner, CustomIdentity));
            }
        }

        [Fact]
        public void The_default_learner_behaves_exactly_as_before()
        {
            // Nothing configured anywhere: the three known sources still resolve from the shipped table.
            var learner = new KeyedScaleLearner(isLockChannel: true);
            Assert.Null(learner.ConfiguredColdStartIdentity);
            Assert.Equal(0.0, learner.ConfiguredColdStartSMax, 6);

            string rawIdentity = Settings.RawSourceFallback.RawIdentity(true);
            Assert.Equal(KnownSourceColdStartReference.LockRawSMax, ColdCeiling(learner, rawIdentity).Value, 6);
        }

        [Fact]
        public void The_two_channels_carry_their_own_override()
        {
            var lockLearner = new KeyedScaleLearner(isLockChannel: true)
            {
                ConfiguredColdStartIdentity = CustomIdentity,
                ConfiguredColdStartSMax = 42.0,
            };
            var slipLearner = new KeyedScaleLearner(isLockChannel: false);

            Assert.Equal(42.0, ColdCeiling(lockLearner, CustomIdentity).Value, 6);
            Assert.Null(ColdCeiling(slipLearner, CustomIdentity));
        }
    }
}

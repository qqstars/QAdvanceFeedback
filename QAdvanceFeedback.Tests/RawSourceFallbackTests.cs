using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A CHANNEL THAT CANNOT READ ITS CONFIGURED SOURCE READS RAW INSTEAD (owner, 2026-09-28: "if
    /// supported, use the Viper source; otherwise, use the Raw as the source").
    /// <para/>
    /// The case that forces this: viper4gh's plugin returns before computing on any game it does not
    /// handle, leaving its four properties at the 0 it declared them with for the whole session. That is
    /// a CHANNEL-wide condition, not a wheel going quiet, so it is decided once per channel rather than
    /// per wheel.
    /// </summary>
    public class RawSourceFallbackTests
    {
        private readonly ITestOutputHelper _out;
        public RawSourceFallbackTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void The_fallback_names_are_this_channels_own_raw_properties()
        {
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", RawSourceFallback.PropertyName(true, 0));
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontRight", RawSourceFallback.PropertyName(true, 1));
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.RearLeft", RawSourceFallback.PropertyName(true, 2));
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.RearRight", RawSourceFallback.PropertyName(true, 3));

            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.FrontLeft", RawSourceFallback.PropertyName(false, 0));
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.RearRight", RawSourceFallback.PropertyName(false, 3));

            // A channel must never fall back onto the OTHER channel's Raw - a cross-wiring whose scale
            // nothing has measured.
            Assert.DoesNotContain("WheelSlip", RawSourceFallback.PropertyName(true, 0));
            Assert.DoesNotContain("WheelLock", RawSourceFallback.PropertyName(false, 0));
        }

        [Fact]
        public void The_fallback_identity_is_exactly_what_a_raw_configured_channel_would_key_by()
        {
            // THE POINT OF THIS TEST. A fallen-back channel reads Raw, so it must LEARN as Raw - if the
            // identity stayed on the Viper key its evidence would accumulate against numbers that came
            // from somewhere else, and returning to a supported game would inherit that.
            foreach (bool isLock in new[] { true, false })
            {
                var rawChannel = new WheelChannelSettings();
                rawChannel.ResetSourcesToDefault(isLock);

                string configured = SourceIdentity.Compute(
                    rawChannel.SourceFrontLeft, rawChannel.ScriptTypeFrontLeft.ToString(),
                    rawChannel.SourceFrontRight, rawChannel.ScriptTypeFrontRight.ToString(),
                    rawChannel.SourceRearLeft, rawChannel.ScriptTypeRearLeft.ToString(),
                    rawChannel.SourceRearRight, rawChannel.ScriptTypeRearRight.ToString());

                _out.WriteLine($"isLock={isLock} {RawSourceFallback.RawIdentity(isLock)}");
                Assert.Equal(configured, RawSourceFallback.RawIdentity(isLock));
            }

            // And the two channels never collide.
            Assert.NotEqual(RawSourceFallback.RawIdentity(true), RawSourceFallback.RawIdentity(false));
        }

        [Fact]
        public void The_fallback_identity_is_recognised_by_the_cold_start_table_as_Raw()
        {
            // So a fallen-back channel gets Raw's shipped SMax, not Viper's 15/10 - which are measured
            // against a completely different signal and would mis-scale the whole channel.
            foreach (bool isLock in new[] { true, false })
            {
                string identity = RawSourceFallback.RawIdentity(isLock);
                Assert.Equal(KnownFeedbackSource.QAdvanceFeedbackRaw,
                    KnownSourceColdStartReference.Classify(identity, isLock));

                Assert.True(KnownSourceColdStartReference.TryGetSMax(identity, isLock, out double smax));
                Assert.Equal(isLock ? KnownSourceColdStartReference.LockRawSMax
                                    : KnownSourceColdStartReference.SlipRawSMax, smax, 6);
            }
        }

        [Fact]
        public void Only_the_viper_mode_on_an_unsupported_game_falls_back()
        {
            // The rule the plugin applies per channel. Raw and ShakeIt are unaffected by the game list -
            // they read properties that exist regardless - and Viper on a supported title is left alone.
            var supported = ViperSupportedGames.Shipped;

            Assert.True(ViperSupportedGames.IsSupported("F12025", supported));
            Assert.False(ViperSupportedGames.IsSupported("FH6", supported));

            // The owner's own two sessions: F1 25 works, FH6 was the one that silently produced nothing.
            foreach (string game in new[] { "F12025", "FH6" })
                foreach (SourceMode mode in new[] { SourceMode.Manual, SourceMode.ShakeIt, SourceMode.Viper })
                {
                    bool shouldFallBack = mode == SourceMode.Viper && !ViperSupportedGames.IsSupported(game, supported);
                    _out.WriteLine($"{game,-8} {mode,-8} -> fallback={shouldFallBack}");

                    if (mode == SourceMode.Viper && game == "FH6") Assert.True(shouldFallBack);
                    else Assert.False(shouldFallBack);
                }
        }

        [Fact]
        public void A_channel_with_no_game_running_does_not_claim_viper_works()
        {
            // With nothing running there is nothing for the Viper plugin to compute, so a Viper channel
            // falls back rather than reporting a source that has not been tested.
            Assert.False(ViperSupportedGames.IsSupported(null, ViperSupportedGames.Shipped));
            Assert.False(ViperSupportedGames.IsSupported(string.Empty, ViperSupportedGames.Shipped));
        }

        [Fact]
        public void The_two_channels_fall_back_independently()
        {
            // Lock and Slip each carry their own SourceMode, so one can be on Viper and the other on
            // Raw - the owner's "Lock and Slip MUST be separated".
            var lockChannel = new WheelChannelSettings { SourceMode = SourceMode.Viper };
            var slipChannel = new WheelChannelSettings { SourceMode = SourceMode.Manual };

            bool lockBlocked = lockChannel.SourceMode == SourceMode.Viper
                               && !ViperSupportedGames.IsSupported("FH6", ViperSupportedGames.Shipped);
            bool slipBlocked = slipChannel.SourceMode == SourceMode.Viper
                               && !ViperSupportedGames.IsSupported("FH6", ViperSupportedGames.Shipped);

            Assert.True(lockBlocked);
            Assert.False(slipBlocked);
        }
    }
}

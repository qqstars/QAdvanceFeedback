using QAdvanceFeedback;
using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// TWO OWNER-REPORTED DEFECTS in the Key Data Points panel, both found on the Viper source (v1.1.0):
    /// <list type="number">
    /// <item><b>"Switching Auto off still shows ---"</b>. The manual boxes seed from
    /// <see cref="KeyDataPointSettings.TryResolveShippedDefaults"/>, whose
    /// <see cref="KeyDataPointSettings.ClassifyExact"/> only knew Raw and ShakeIt - so a Viper channel
    /// resolved to Unknown, got no default, and the driver had to click +/- to conjure a number.</item>
    /// <item><b>"Set the numbers, start the game, they go back to ---"</b>. A game switch is a new slot
    /// when Per-Game is on, so the reload correctly finds nothing and blanks the boxes - but the re-seed
    /// that should follow was gated behind "at least one channel is on Auto", which is precisely false
    /// for a driver who has turned Auto off to type their own values.</item>
    /// </list>
    /// The second is NOT Viper-specific - it blanked Raw and ShakeIt channels too - it was simply
    /// invisible there, because those sources always had a shipped default to fall back on.
    /// </summary>
    public class ViperKeyDataPointsTests
    {
        private readonly ITestOutputHelper _out;
        public ViperKeyDataPointsTests(ITestOutputHelper output) { _out = output; }

        private static string IdentityFor(SourceMode mode, bool isLock)
        {
            var channel = new WheelChannelSettings { SourceMode = mode };
            channel.ResetSourcesForCurrentMode(isLock);
            return SourceIdentity.Compute(
                channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                channel.SourceRearRight, channel.ScriptTypeRearRight.ToString());
        }

        [Fact]
        public void DEFECT_A_a_viper_channel_now_resolves_shipped_key_data_points()
        {
            // The exact call the manual-mode seeding path makes. Before the fix this returned false and
            // the three boxes were set to null, which renders as the "---" watermark.
            foreach (bool isLock in new[] { true, false })
            {
                string identity = IdentityFor(SourceMode.Viper, isLock);

                Assert.Equal(KnownFeedbackSource.ViperLngWheelSlip,
                    KeyDataPointSettings.ClassifyExact(identity, isLock));

                Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                    identity, isLock, KeyDataPointDefaults.CreateShipped(),
                    out double sMax, out double s90, out double s75),
                    "a Viper channel must have a shipped default to seed the manual boxes from");

                _out.WriteLine($"isLock={isLock} SMax={sMax} S90={s90} S75={s75}");
                Assert.Equal(isLock ? 15.0 : 10.0, sMax, 6);
                // S90/S75 are DERIVED from SMax by the fixed fractions the UI shows, so they track the
                // owner's number automatically: 15 -> 13.5 / 10.5, 10 -> 9.0 / 7.0.
                Assert.Equal(sMax * KeyDataPointSettings.DerivedS90Fraction, s90, 6);
                Assert.Equal(sMax * KeyDataPointSettings.DerivedS75Fraction, s75, 6);
                Assert.True(KeyDataPointSettings.IsValid(sMax, s90, s75),
                    "the seeded triple must satisfy the ordering the four-range curve needs");
            }
        }

        [Fact]
        public void The_viper_defaults_are_its_own_numbers_not_the_shakeit_ones()
        {
            // The pre-fix TryResolve was "raw ? LockRaw : LockShakeIt", so ANY new source silently
            // inherited the ShakeIt numbers. That is the failure mode this guards.
            KeyDataPointDefaults shipped = KeyDataPointDefaults.CreateShipped();

            shipped.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, true, out double viperLock, out _, out _);
            shipped.TryResolve(KnownFeedbackSource.ShakeItMotorsExport, true, out double shakeItLock, out _, out _);
            shipped.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, false, out double viperSlip, out _, out _);
            shipped.TryResolve(KnownFeedbackSource.ShakeItMotorsExport, false, out double shakeItSlip, out _, out _);

            _out.WriteLine($"lock viper={viperLock} shakeIt={shakeItLock}   slip viper={viperSlip} shakeIt={shakeItSlip}");
            Assert.NotEqual(shakeItLock, viperLock);
            Assert.NotEqual(shakeItSlip, viperSlip);
            Assert.Equal(KnownSourceColdStartReference.LockViperSMax, viperLock, 6);
            Assert.Equal(KnownSourceColdStartReference.SlipViperSMax, viperSlip, 6);
        }

        [Fact]
        public void Changing_the_viper_reference_leaves_every_other_source_untouched()
        {
            // THE OWNER'S EXPLICIT CONSTRAINT: "this reference value is ONLY FOR VIPER SOURCE! NO IMPACT
            // ON ANY OTHER SOURCES!" Viper's numbers are far lower than everything else in the table
            // (15/10 against 85/75), so a leak would be both easy to cause and loud in the output.
            KeyDataPointDefaults shipped = KeyDataPointDefaults.CreateShipped();

            shipped.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, true, out double rawLock, out _, out _);
            shipped.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, false, out double rawSlip, out _, out _);
            shipped.TryResolve(KnownFeedbackSource.ShakeItMotorsExport, true, out double shakeLock, out _, out _);
            shipped.TryResolve(KnownFeedbackSource.ShakeItMotorsExport, false, out double shakeSlip, out _, out _);

            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, rawLock, 6);
            Assert.Equal(KeyDataPointSettings.SlipDefaultSMax, rawSlip, 6);
            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, shakeLock, 6);
            Assert.Equal(KeyDataPointSettings.SlipDefaultSMax, shakeSlip, 6);

            // And the cold-start table's own four constants are equally untouched.
            Assert.Equal(66.0, KnownSourceColdStartReference.LockRawSMax, 6);
            Assert.Equal(71.0, KnownSourceColdStartReference.LockShakeItSMax, 6);
            Assert.Equal(64.0, KnownSourceColdStartReference.SlipRawSMax, 6);
            Assert.Equal(66.0, KnownSourceColdStartReference.SlipShakeItSMax, 6);
        }

        [Fact]
        public void An_unrecognised_source_still_gets_no_default_at_all()
        {
            // The "---" behaviour is correct for a source nobody has measured, and must survive: the fix
            // adds one known source, it does not start inventing numbers for scripts in general.
            const string stranger = "NCalc:deadbeef~NCalc:deadbeef~NCalc:deadbeef~NCalc:deadbeef";
            Assert.Equal(KnownFeedbackSource.Unknown, KeyDataPointSettings.ClassifyExact(stranger, true));
            Assert.False(KeyDataPointSettings.TryResolveShippedDefaults(
                stranger, true, KeyDataPointDefaults.CreateShipped(), out _, out _, out _));

            // And the resolver itself refuses an unknown enum rather than falling through to ShakeIt.
            Assert.False(KeyDataPointDefaults.CreateShipped()
                .TryResolve(KnownFeedbackSource.Unknown, true, out _, out _, out _));
        }

        [Fact]
        public void The_other_two_sources_keep_their_own_defaults()
        {
            foreach (bool isLock in new[] { true, false })
            {
                string raw = IdentityFor(SourceMode.Manual, isLock);
                string shakeIt = IdentityFor(SourceMode.ShakeIt, isLock);

                Assert.Equal(KnownFeedbackSource.QAdvanceFeedbackRaw, KeyDataPointSettings.ClassifyExact(raw, isLock));
                Assert.Equal(KnownFeedbackSource.ShakeItMotorsExport, KeyDataPointSettings.ClassifyExact(shakeIt, isLock));

                Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                    raw, isLock, KeyDataPointDefaults.CreateShipped(), out double rawSMax, out _, out _));
                Assert.Equal(isLock ? KeyDataPointSettings.LockDefaultSMax : KeyDataPointSettings.SlipDefaultSMax, rawSMax, 6);
            }
        }

        [Fact]
        public void DEFECT_B_a_game_switch_leaves_a_manual_slot_empty_so_seeding_must_follow()
        {
            // The mechanism behind "start the game and they go back to ---", at the level this project
            // can test without a WPF host: Per-Game makes the game part of the slot key, so the values
            // typed under one game are genuinely absent under the next. That part is CORRECT - the bug
            // was that nothing re-seeded afterwards, which is fixed at the call site in
            // SettingsControl.UpdateLearnedKeyDataPoints.
            string identity = IdentityFor(SourceMode.Viper, true);
            var k = new KeyDataPointSettings { PerGame = true };
            k.SetManual("FH6", identity, 88.0, 79.2, 66.0, seeded: false);

            Assert.True(k.TryGetManual("FH6", identity, out double sMax, out _, out _));
            Assert.Equal(88.0, sMax, 6);

            // Switching to another title finds nothing - which is why the re-seed matters.
            Assert.False(k.TryGetManual("F12025", identity, out _, out _, out _));

            // And what the re-seed will now put there is the Viper default, not a blank.
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                identity, true, KeyDataPointDefaults.CreateShipped(), out double seeded, out _, out _));
            Assert.Equal(15.0, seeded, 6);
        }

        [Fact]
        public void Global_mode_keeps_manual_values_across_a_game_switch()
        {
            // The other half of the same story: with Per-Game off the slot has no game in it, so the
            // driver's numbers follow them between titles. Pinned so the slot-key shape cannot drift.
            string identity = IdentityFor(SourceMode.Viper, true);
            var k = new KeyDataPointSettings { PerGame = false };
            k.SetManual("FH6", identity, 88.0, 79.2, 66.0, seeded: false);

            Assert.True(k.TryGetManual("F12025", identity, out double sMax, out _, out _));
            Assert.Equal(88.0, sMax, 6);
        }

        [Fact]
        public void A_source_switch_never_reuses_another_signals_numbers()
        {
            // Both slot modes include the source, because a source change is a change of scale.
            var k = new KeyDataPointSettings { PerGame = false };
            k.SetManual("FH6", IdentityFor(SourceMode.Viper, true), 88.0, 79.2, 66.0, seeded: false);
            Assert.False(k.TryGetManual("FH6", IdentityFor(SourceMode.Manual, true), out _, out _, out _));
        }

        [Fact]
        public void Configured_defaults_survive_a_clone()
        {
            // The settings object is cloned on save/load; a field missing from Clone silently reverts to
            // the built-ins, which would look exactly like "my edit did not stick".
            KeyDataPointDefaults edited = KeyDataPointDefaults.CreateShipped();
            edited.LockViper = new KeyDataPointDefaultSet(91.0, 81.9, 68.25);
            edited.SlipViper = new KeyDataPointDefaultSet(64.0, 57.6, 48.0);

            KeyDataPointDefaults copy = edited.Clone();
            Assert.True(copy.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, true, out double lockSMax, out _, out _));
            Assert.True(copy.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, false, out double slipSMax, out _, out _));
            Assert.Equal(91.0, lockSMax, 6);
            Assert.Equal(64.0, slipSMax, 6);
        }

        [Fact]
        public void An_unusable_configured_viper_entry_falls_back_to_the_built_in_numbers()
        {
            // Hand-edited nonsense in the config must not reach the output - same rule the other entries
            // already obey.
            KeyDataPointDefaults broken = KeyDataPointDefaults.CreateShipped();
            broken.LockViper = new KeyDataPointDefaultSet(0.0, 0.0, 0.0);

            Assert.True(broken.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, true, out double sMax, out _, out _));
            Assert.Equal(KnownSourceColdStartReference.LockViperSMax, sMax, 6);
        }
    }
}

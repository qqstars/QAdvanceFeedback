using QAdvanceFeedback;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE CUSTOM SOURCE'S COLD-START REFERENCE (owner, 2026-09-28: "when switch to custom, we need to
    /// provide the Reference Key Data Points for cold start as configurable value. And this value will
    /// be duplicated from whatever the current source being selected").
    /// <para/>
    /// Custom is the one source whose reference cannot be looked up: the other three have identities
    /// this plugin generates, while a custom source is whatever the driver typed and
    /// <see cref="SourceIdentity"/> hashes scripted text. So it is CONFIGURED, seeded by copying the
    /// preset the driver edited their way in from, and resolved by MODE rather than by identity.
    /// </summary>
    public class CustomSourceReferenceTests
    {
        private readonly ITestOutputHelper _out;
        public CustomSourceReferenceTests(ITestOutputHelper output) { _out = output; }

        private static string IdentityFor(SourceMode mode, bool isLock)
        {
            var c = new WheelChannelSettings { SourceMode = mode };
            c.ResetSourcesForCurrentMode(isLock);
            return SourceIdentity.Compute(
                c.SourceFrontLeft, c.ScriptTypeFrontLeft.ToString(),
                c.SourceFrontRight, c.ScriptTypeFrontRight.ToString(),
                c.SourceRearLeft, c.ScriptTypeRearLeft.ToString(),
                c.SourceRearRight, c.ScriptTypeRearRight.ToString());
        }

        [Fact]
        public void Nothing_is_SHIPPED_for_custom_because_the_reference_is_always_seeded_on_arrival()
        {
            // THE MODEL HAS NO CUSTOM ENTRY, AND THE PAGE STILL NEVER SHOWS "---". Those are two
            // different statements and both are deliberate.
            //
            // There is nothing to SHIP: a custom source's scale is unknown to this project, so a
            // hard-coded default here would be a confident guess. The slot is therefore filled at the
            // moment a driver arrives at Custom - from the preset they left, by either route (editing a
            // preset's source text, or picking Custom from the dropdown) - which is the owner's rule
            // that a cold-start reference always carries "a particular value, duplicated from the known
            // source, or the adjusted value by the user".
            KeyDataPointDefaults shipped = KeyDataPointDefaults.CreateShipped();
            Assert.Null(shipped.LockCustom);
            Assert.Null(shipped.SlipCustom);

            Assert.False(KeyDataPointSettings.TryResolveDefaultsForMode(
                "NCalc:whatever", SourceMode.Custom, true, shipped, out _, out _, out _));
        }

        [Fact]
        public void Editing_from_Raw_seeds_Custom_with_Raws_numbers()
        {
            foreach (bool isLock in new[] { true, false })
            {
                KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
                d.SeedCustomFrom(KnownFeedbackSource.QAdvanceFeedbackRaw, isLock);

                d.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, isLock, out double rawSMax, out double rawS90, out double rawS75);
                Assert.True(KeyDataPointSettings.TryResolveDefaultsForMode(
                    "NCalc:anything", SourceMode.Custom, isLock, d, out double sMax, out double s90, out double s75));

                _out.WriteLine($"isLock={isLock} raw {rawSMax}/{rawS90}/{rawS75} -> custom {sMax}/{s90}/{s75}");
                Assert.Equal(rawSMax, sMax, 6);
                Assert.Equal(rawS90, s90, 6);
                Assert.Equal(rawS75, s75, 6);
            }
        }

        [Fact]
        public void Editing_from_Viper_seeds_Custom_with_Vipers_numbers_instead()
        {
            // The owner's worked example: the seed follows whichever source was being edited, and the
            // two are very different numbers here (Raw 85 against Viper 15), so a mix-up would be loud.
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.SeedCustomFrom(KnownFeedbackSource.ViperLngWheelSlip, true);

            Assert.True(KeyDataPointSettings.TryResolveDefaultsForMode(
                "NCalc:anything", SourceMode.Custom, true, d, out double sMax, out _, out _));
            Assert.Equal(KnownSourceColdStartReference.LockViperSMax, sMax, 6);
        }

        [Fact]
        public void Re_entering_Custom_from_a_different_preset_overwrites_the_reference()
        {
            // "if we already have the Custom source applied, change the source from Raw, will still
            // OVERRIDE the cold references into the Raw's one" - last preset edited from wins.
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.SeedCustomFrom(KnownFeedbackSource.ViperLngWheelSlip, true);
            d.SeedCustomFrom(KnownFeedbackSource.QAdvanceFeedbackRaw, true);

            KeyDataPointSettings.TryResolveDefaultsForMode("NCalc:x", SourceMode.Custom, true, d, out double sMax, out _, out _);
            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, sMax, 6);
        }

        [Fact]
        public void A_driver_edit_to_the_custom_reference_survives_editing_the_source_again()
        {
            // The other half of the rule: editing a source that is ALREADY Custom must keep the
            // driver's own reference rather than resetting it.
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.LockCustom = new KeyDataPointDefaultSet(42.0, 37.8, 29.4);

            d.SeedCustomFrom(KnownFeedbackSource.Custom, true);      // editing while already on Custom

            KeyDataPointSettings.TryResolveDefaultsForMode("NCalc:x", SourceMode.Custom, true, d, out double sMax, out _, out _);
            Assert.Equal(42.0, sMax, 6);
        }

        [Fact]
        public void The_mode_is_what_distinguishes_Custom_from_an_unmeasured_source()
        {
            // Both have an unrecognisable identity. Only the mode says which is which, and they must
            // not resolve the same way.
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.SeedCustomFrom(KnownFeedbackSource.QAdvanceFeedbackRaw, true);
            const string stranger = "NCalc:deadbeef~NCalc:deadbeef~NCalc:deadbeef~NCalc:deadbeef";

            Assert.True(KeyDataPointSettings.TryResolveDefaultsForMode(stranger, SourceMode.Custom, true, d, out _, out _, out _));
            Assert.False(KeyDataPointSettings.TryResolveDefaultsForMode(stranger, SourceMode.Manual, true, d, out _, out _, out _));

            Assert.Equal(KnownFeedbackSource.Custom, KeyDataPointSettings.ClassifyForMode(stranger, SourceMode.Custom, true));
            Assert.Equal(KnownFeedbackSource.Unknown, KeyDataPointSettings.ClassifyForMode(stranger, SourceMode.Manual, true));
        }

        [Fact]
        public void Seeding_Custom_never_disturbs_the_three_presets()
        {
            // The owner's standing constraint every time a reference changes: no impact on other
            // sources.
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.SeedCustomFrom(KnownFeedbackSource.ViperLngWheelSlip, true);
            d.SeedCustomFrom(KnownFeedbackSource.ShakeItMotorsExport, false);

            d.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, true, out double rawLock, out _, out _);
            d.TryResolve(KnownFeedbackSource.ShakeItMotorsExport, true, out double shakeLock, out _, out _);
            d.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, true, out double viperLock, out _, out _);

            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, rawLock, 6);
            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, shakeLock, 6);
            Assert.Equal(KnownSourceColdStartReference.LockViperSMax, viperLock, 6);
        }

        [Fact]
        public void Lock_and_Slip_custom_references_are_independent()
        {
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.SeedCustomFrom(KnownFeedbackSource.ViperLngWheelSlip, true);

            Assert.NotNull(d.LockCustom);
            Assert.Null(d.SlipCustom);
            Assert.False(KeyDataPointSettings.TryResolveDefaultsForMode("x", SourceMode.Custom, false, d, out _, out _, out _));
        }

        [Fact]
        public void The_custom_reference_survives_a_clone()
        {
            KeyDataPointDefaults d = KeyDataPointDefaults.CreateShipped();
            d.SeedCustomFrom(KnownFeedbackSource.ViperLngWheelSlip, true);

            KeyDataPointDefaults copy = d.Clone();
            copy.TryResolve(KnownFeedbackSource.Custom, true, out double sMax, out _, out _);
            Assert.Equal(KnownSourceColdStartReference.LockViperSMax, sMax, 6);
        }

        [Fact]
        public void The_three_preset_modes_still_resolve_by_identity()
        {
            // TryResolveDefaultsForMode must not have broken the ordinary path.
            foreach (var pair in new[]
                     {
                         (SourceMode.Manual, KnownFeedbackSource.QAdvanceFeedbackRaw),
                         (SourceMode.ShakeIt, KnownFeedbackSource.ShakeItMotorsExport),
                         (SourceMode.Viper, KnownFeedbackSource.ViperLngWheelSlip),
                     })
            {
                string identity = IdentityFor(pair.Item1, true);
                Assert.Equal(pair.Item2, KeyDataPointSettings.ClassifyForMode(identity, pair.Item1, true));
                Assert.True(KeyDataPointSettings.TryResolveDefaultsForMode(
                    identity, pair.Item1, true, KeyDataPointDefaults.CreateShipped(), out double sMax, out _, out _));
                Assert.True(sMax > 0.0);
            }
        }
    }
}

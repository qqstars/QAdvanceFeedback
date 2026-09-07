using System;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="ShakeFeeling"/> - how the two pads of a pair relate while shaking (v1.0.8).
    /// <para/>
    /// REPLACES <c>GForceShakeBlendTests</c>, which specified the old continuous "Both-sides blend (%)"
    /// pan/common mix. That model is gone: only three points on the slider were distinct to feel, and
    /// the hold setting silently cancelled itself as the slider approached the middle - a driver could
    /// set a 40% hold at the shipped blend and get exactly none of it, with nothing in the UI saying so.
    /// The three named feelings say what each one does, and the hold now behaves identically in all of
    /// them.
    /// </summary>
    public class GForceShakeFeelingTests
    {
        private const double Hz = 10.0;
        private const double Period = 1.0 / Hz;
        private const double Hold = 0.30;

        private static void Pair(double ms, ShakeFeeling feeling, out double l, out double r,
                                 double hold = Hold, bool reversed = false)
            => GForceShake.FeelingPair(Hz, ms / 1000.0, hold, feeling, out l, out r, reversed);

        // ---------------- Opposite phase ----------------

        [Fact]
        public void Opposite_phase_puts_the_pads_half_a_cycle_apart()
        {
            for (int ms = 0; ms <= 100; ms++)
            {
                Pair(ms, ShakeFeeling.OppositePhase, out double l, out double r);
                Pair(ms + 50, ShakeFeeling.OppositePhase, out double shiftedL, out _);
                Assert.Equal(shiftedL, r, 9);
            }
        }

        [Fact]
        public void Opposite_phase_starts_with_one_pad_high_and_the_other_low()
        {
            Pair(0, ShakeFeeling.OppositePhase, out double l, out double r);
            Assert.Equal(1.0, l, 9);
            Assert.Equal(0.0, r, 9);
        }

        [Fact]
        public void Opposite_phase_keeps_the_pairs_total_nearly_constant()
        {
            // The reason this is the QUIETEST feeling despite each pad travelling its whole band: two
            // uncorrelated transducers sum closer to sqrt(L^2 + R^2) than to L + R, so a pair whose
            // total barely moves is a pair the driver barely notices. Documented, not endorsed.
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 1000; i++)
            {
                Pair(100.0 * i / 1000.0, ShakeFeeling.OppositePhase, out double l, out double r);
                min = Math.Min(min, l + r);
                max = Math.Max(max, l + r);
            }

            Assert.True(max - min < 0.05, $"total should hardly move, swung {min:F3}..{max:F3}");
        }

        // ---------------- Same phase ----------------

        [Fact]
        public void Same_phase_moves_both_pads_identically()
        {
            for (int ms = 0; ms <= 100; ms++)
            {
                Pair(ms, ShakeFeeling.SamePhase, out double l, out double r);
                Assert.Equal(l, r, 9);
            }
        }

        [Fact]
        public void Same_phase_swings_the_pairs_total_the_whole_way()
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 1000; i++)
            {
                Pair(100.0 * i / 1000.0, ShakeFeeling.SamePhase, out double l, out double r);
                min = Math.Min(min, l + r);
                max = Math.Max(max, l + r);
            }

            Assert.Equal(0.0, min, 6);
            Assert.Equal(2.0, max, 6);
        }

        // ---------------- Blending ----------------

        [Fact]
        public void Blending_starts_both_pads_at_maximum_with_one_already_descending()
        {
            // The owner's own description: "Starting both on max... another channel directly starting
            // running the first half of the sin curve to ramp down; after [the leader's own opening
            // hold] the first channel start running the first half".
            Pair(0, ShakeFeeling.Blending, out double l, out double r);
            Assert.Equal(1.0, l, 9);
            Assert.Equal(1.0, r, 9);

            // The follower has left the top by the next sample; the leader has not.
            Pair(2, ShakeFeeling.Blending, out double l2, out double r2);
            Assert.Equal(1.0, l2, 9);
            Assert.True(r2 < 1.0, $"the follower should already be descending, was {r2:F4}");
        }

        [Fact]
        public void Blending_offsets_the_follower_by_the_leaders_opening_hold()
        {
            // With the fixed 50% hold that offset is an eighth of the cycle.
            double offset = Period * GForceShake.BlendingHoldFraction / 4.0;
            for (int ms = 0; ms <= 100; ms++)
            {
                Pair(ms, ShakeFeeling.Blending, out double l, out double r);
                double expected = GForceShake.SineHoldWave(
                    Hz, ms / 1000.0 + offset, GForceShake.BlendingHoldFraction);
                Assert.Equal(expected, r, 9);
                Assert.Equal(GForceShake.SineHoldWave(Hz, ms / 1000.0, GForceShake.BlendingHoldFraction), l, 9);
            }
        }

        [Fact]
        public void Blending_ignores_the_hold_setting_entirely()
        {
            // Which is exactly why the UI hides the control for this feeling.
            foreach (double hold in new[] { 0.0, 0.2, 0.9 })
            {
                Pair(30, ShakeFeeling.Blending, out double l, out double r, hold);
                Pair(30, ShakeFeeling.Blending, out double refL, out double refR, GForceShake.BlendingHoldFraction);
                Assert.Equal(refL, l, 9);
                Assert.Equal(refR, r, 9);
            }
        }

        [Fact]
        public void Blending_both_pans_and_swells()
        {
            // The combination that made it worth having: some side-to-side movement AND a moving total.
            double panMax = 0.0, totalMin = double.MaxValue, totalMax = double.MinValue;
            for (int i = 0; i < 1000; i++)
            {
                Pair(100.0 * i / 1000.0, ShakeFeeling.Blending, out double l, out double r);
                panMax = Math.Max(panMax, Math.Abs(l - r));
                totalMin = Math.Min(totalMin, l + r);
                totalMax = Math.Max(totalMax, l + r);
            }

            Assert.True(panMax > 0.5, $"there should be real left/right movement, peak was {panMax:F3}");
            Assert.True(totalMax - totalMin > 1.5, $"the total should swell, swung {totalMin:F2}..{totalMax:F2}");
        }

        // ---------------- shared properties ----------------

        [Theory]
        [InlineData(ShakeFeeling.OppositePhase)]
        [InlineData(ShakeFeeling.SamePhase)]
        [InlineData(ShakeFeeling.Blending)]
        public void Every_feeling_lets_each_pad_travel_the_full_0_to_1(ShakeFeeling feeling)
        {
            double lMin = double.MaxValue, lMax = double.MinValue, rMin = double.MaxValue, rMax = double.MinValue;
            for (int i = 0; i < 4000; i++)
            {
                Pair(100.0 * i / 4000.0, feeling, out double l, out double r);
                lMin = Math.Min(lMin, l); lMax = Math.Max(lMax, l);
                rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r);
            }

            Assert.Equal(0.0, lMin, 6);
            Assert.Equal(1.0, lMax, 6);
            Assert.Equal(0.0, rMin, 6);
            Assert.Equal(1.0, rMax, 6);
        }

        [Theory]
        [InlineData(ShakeFeeling.OppositePhase)]
        [InlineData(ShakeFeeling.SamePhase)]
        [InlineData(ShakeFeeling.Blending)]
        public void Reversing_swaps_which_side_leads(ShakeFeeling feeling)
        {
            for (int ms = 0; ms <= 100; ms += 5)
            {
                Pair(ms, feeling, out double l, out double r);
                Pair(ms, feeling, out double revL, out double revR, Hold, reversed: true);
                Assert.Equal(r, revL, 9);
                Assert.Equal(l, revR, 9);
            }
        }

        [Fact]
        public void The_hold_applies_identically_in_both_phase_locked_feelings()
        {
            // The old blend cancelled the hold as it approached the middle; nothing does that now.
            for (int ms = 0; ms <= 100; ms += 5)
            {
                Pair(ms, ShakeFeeling.OppositePhase, out double oppL, out _);
                Pair(ms, ShakeFeeling.SamePhase, out double sameL, out _);
                Assert.Equal(oppL, sameL, 9);
            }
        }

        [Fact]
        public void EffectiveHold_pins_blending_and_passes_the_others_through()
        {
            Assert.Equal(GForceShake.BlendingHoldFraction, GForceShake.EffectiveHold(0.1, ShakeFeeling.Blending), 9);
            Assert.Equal(0.25, GForceShake.EffectiveHold(0.25, ShakeFeeling.OppositePhase), 9);
            Assert.Equal(0.25, GForceShake.EffectiveHold(0.25, ShakeFeeling.SamePhase), 9);

            Assert.Equal(0.0, GForceShake.EffectiveHold(double.NaN, ShakeFeeling.SamePhase), 9);
            Assert.Equal(GForceShake.MaxSustainFraction, GForceShake.EffectiveHold(5.0, ShakeFeeling.SamePhase), 9);
        }

        // ---------------- shipped defaults ----------------

        [Fact]
        public void The_shipped_frequency_is_the_one_the_shipped_feeling_would_choose()
        {
            // These two used to disagree: the feeling shipped as OppositePhase (10 Hz by
            // DefaultShakeFrequencyFor) while the frequency shipped as a literal 5, so a fresh install
            // opened showing "Opposite phase" at 5 Hz and jumped to 10 the moment the driver touched the
            // dropdown. The default is DERIVED now, so it cannot drift again.
            var settings = new GForceSettings();
            Assert.Equal(GForceSettings.DefaultShakeFeeling, settings.ShakeFeeling);
            Assert.Equal(ShakeFeeling.OppositePhase, GForceSettings.DefaultShakeFeeling);
            Assert.Equal(
                GForceSettings.DefaultShakeFrequencyFor(GForceSettings.DefaultShakeFeeling),
                settings.ShakeFrequencyHz, 9);
            Assert.Equal(5.0, settings.ShakeFrequencyHz, 9);
        }

        [Fact]
        public void Picking_a_feeling_sets_that_feelings_own_frequency()
        {
            // Picking a feeling overwrites even a hand-tuned frequency - the REQUESTED behaviour here,
            // which is exactly what separates it from ShakeApplyMode's scale reset (removed on the
            // owner's instruction). The SPLIT was revised after seat time on 2026-09-06: OppositePhase
            // now takes 5 Hz and the other two 10 Hz, the reverse of the first cut.
            Assert.Equal(5.0, GForceSettings.DefaultShakeFrequencyFor(ShakeFeeling.OppositePhase), 9);
            Assert.Equal(10.0, GForceSettings.DefaultShakeFrequencyFor(ShakeFeeling.SamePhase), 9);
            Assert.Equal(10.0, GForceSettings.DefaultShakeFrequencyFor(ShakeFeeling.Blending), 9);
        }

        [Fact]
        public void Setting_the_feeling_property_does_not_itself_move_the_frequency()
        {
            // The reset is a DROPDOWN action (SelectionChanged only, never the load path or a plain
            // property set), so loading a persisted config cannot silently rewrite a driver's tuned
            // frequency behind their back.
            var settings = new GForceSettings { ShakeFrequencyHz = 7.0 };

            settings.ShakeFeeling = ShakeFeeling.Blending;
            Assert.Equal(7.0, settings.ShakeFrequencyHz, 9);

            settings.ShakeFeeling = ShakeFeeling.SamePhase;
            Assert.Equal(7.0, settings.ShakeFrequencyHz, 9);
        }

        [Fact]
        public void Only_blending_takes_a_feeling_specific_hold_and_it_is_pinned_not_defaulted()
        {
            // The hold does NOT vary per feeling the way the frequency does. The two phase-locked
            // feelings share the one configured value; Blending ignores it entirely and PINS 50% (the
            // owner's "equivalent to set 'Hold on min/max' as 50%"), which is why the UI hides the
            // spinner for it rather than handing the driver a different default to edit.
            Assert.Equal(0.5, GForceShake.BlendingHoldFraction, 9);

            Assert.Equal(0.25, GForceShake.EffectiveHold(0.25, ShakeFeeling.OppositePhase), 9);
            Assert.Equal(0.25, GForceShake.EffectiveHold(0.25, ShakeFeeling.SamePhase), 9);
            Assert.Equal(0.5, GForceShake.EffectiveHold(0.25, ShakeFeeling.Blending), 9);
            Assert.Equal(0.5, GForceShake.EffectiveHold(0.9, ShakeFeeling.Blending), 9);
        }

        [Fact]
        public void The_shipped_scales_come_from_the_shipped_modes_row()
        {
            // Per-mode scale defaults returned on 2026-09-07 (see GForceEngineShakeModeTests for the
            // table and the reversal it represents). The shipped pair is simply the shipped mode's row,
            // derived rather than written out so the two cannot drift.
            var settings = new GForceSettings();
            double expected = GForceSettings.DefaultShakeScaleFor(GForceSettings.DefaultShakeApplyMode);

            Assert.Equal(1.3, expected, 9);
            Assert.Equal(expected, settings.WheelLockShakeScale, 9);
            Assert.Equal(expected, settings.WheelSlipShakeScale, 9);
        }
    }
}

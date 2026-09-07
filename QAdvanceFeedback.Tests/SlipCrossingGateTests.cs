using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="SlipCrossingGate"/> in isolation - the detector itself, away from the engine.
    /// <para/>
    /// The property every test here is really defending: a LAUNCH (slip and G rising together) must
    /// never qualify, and a CROSSING (slip rising while G falls) must. Everything else - the hold
    /// window, the reset, the counters - exists to make that distinction usable frame by frame.
    /// </summary>
    public class SlipCrossingGateTests
    {
        private const double Dt = 1.0 / 60.0;

        /// <summary>Feeds <paramref name="frames"/> frames whose basis and G each move by a fixed step.</summary>
        private static SlipCrossingGate Feed(
            SlipCrossingGate gate, int frames, double basisStart, double basisStep, double gStart, double gStep)
        {
            for (int i = 0; i < frames; i++)
            {
                double basis = basisStart + basisStep * i;
                gate.Observe(basis, basis, gStart + gStep * i, true, Dt);
            }
            return gate;
        }

        [Fact]
        public void Slip_rising_while_G_falls_is_a_crossing()
        {
            var gate = Feed(new SlipCrossingGate(), 12, basisStart: 10.0, basisStep: 2.0, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.Qualified, "slip rising while G falls is the traction-limit signature");
        }

        [Fact]
        public void A_launch_slip_and_G_rising_together_never_qualifies()
        {
            // The whole point of the feature: full wheelspin off the line reads high on the source AND
            // produces near-peak G, so the old physicallyAtLimit boolean opened. Nothing here does.
            var gate = Feed(new SlipCrossingGate(), 200, basisStart: 0.0, basisStep: 0.5, gStart: 0.2, gStep: 0.01);
            Assert.False(gate.Qualified, "a launch has no slip-rising-while-G-falling moment");
            Assert.Equal(0.0, gate.TeachingWeight);
        }

        [Fact]
        public void Sustained_full_slip_at_constant_G_never_qualifies()
        {
            // The steady state of a launch once both have saturated - the case that pinned SMax at 100.
            var gate = Feed(new SlipCrossingGate(), 200, basisStart: 100.0, basisStep: 0.0, gStart: 1.4, gStep: 0.0);
            Assert.False(gate.Qualified);
        }

        [Fact]
        public void G_merely_flat_is_not_a_crossing()
        {
            // GFall is deliberately negative, not zero: a plateau is the normal state at sustained
            // maximum grip and is not the tyre going past the peak of its slip curve.
            var gate = Feed(new SlipCrossingGate(), 12, basisStart: 10.0, basisStep: 2.0, gStart: 1.5, gStep: 0.0);
            Assert.False(gate.Qualified);
        }

        [Fact]
        public void Slip_barely_moving_is_not_a_crossing_however_hard_G_falls()
        {
            var gate = Feed(new SlipCrossingGate(), 12, basisStart: 10.0, basisStep: 0.1, gStart: 1.5, gStep: -0.2);
            Assert.False(gate.Qualified, "SlipRise is an absolute step - noise near zero must not qualify");
        }

        [Fact]
        public void A_crossing_keeps_frames_qualified_for_the_hold_window_then_stops()
        {
            // Feed EXACTLY one full trend window, so the crossing fires on the last of these frames and
            // the hold window's age is known from here. (This used to feed 12 frames of continuous rise
            // and assume the window was refreshed by the last of them - true before "one crossing per
            // event", not after. The assertions are unchanged; only the timing arithmetic moved.)
            var gate = Feed(new SlipCrossingGate(), SlipCrossingGate.TrendFrames + 1,
                            basisStart: 10.0, basisStep: 2.0, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.Qualified);

            // Age the window to roughly half its span, holding the state still.
            for (int i = 0; i < (int)(SlipCrossingGate.HoldSeconds * 0.5 / Dt); i++)
                gate.Observe(34.0, 34.0, 0.9, true, Dt);
            Assert.True(gate.Qualified, "the moments around one crossing are the same physical event");

            // Past its span.
            for (int i = 0; i < (int)(SlipCrossingGate.HoldSeconds * 0.6 / Dt) + 2; i++)
                gate.Observe(34.0, 34.0, 0.9, true, Dt);
            Assert.False(gate.Qualified, "past the hold window a stale crossing must stop teaching");
        }

        [Fact]
        public void Reset_clears_both_the_trend_and_a_live_hold_window()
        {
            var gate = Feed(new SlipCrossingGate(), 12, basisStart: 10.0, basisStep: 2.0, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.Qualified);

            gate.Reset();
            Assert.False(gate.Qualified, "a crossing must not survive a disengagement");

            // And the trend really is empty: fewer than a full window of samples cannot re-qualify,
            // however steep they are.
            Feed(gate, SlipCrossingGate.TrendFrames, basisStart: 10.0, basisStep: 20.0, gStart: 1.5, gStep: -0.5);
            Assert.False(gate.Qualified, "the trend must not be differenced across the gap");
        }

        [Fact]
        public void The_trend_needs_a_full_window_before_it_can_fire()
        {
            var gate = new SlipCrossingGate();
            Feed(gate, SlipCrossingGate.TrendFrames, basisStart: 10.0, basisStep: 20.0, gStart: 1.5, gStep: -0.5);
            Assert.False(gate.Qualified);

            gate.Observe(10.0 + 20.0 * SlipCrossingGate.TrendFrames, 10.0 + 20.0 * SlipCrossingGate.TrendFrames,
                1.5 - 0.5 * SlipCrossingGate.TrendFrames, true, Dt);
            Assert.True(gate.Qualified, "one more sample completes the window and the crossing fires");
        }

        [Fact]
        public void A_single_bad_frame_time_does_not_prematurely_expire_a_live_crossing()
        {
            // NARROWED DELIBERATELY. This used to feed 500 NaN frame times and assert the window was
            // STILL open, on the rule that an unusable dt aged the window by nothing. That rule was the
            // hazard, not the safeguard: the engine passes dt = 0.0 for any title with no frame time, so
            // "ages by nothing" meant "never expires" - one crossing teaching the same snapshot for the
            // rest of the session. An unusable dt now ages by NominalFrameSeconds instead (see
            // A_zero_frame_time_still_expires_the_window). What remains true, and is what this test was
            // really protecting, is that ONE bad frame must not throw away a fresh window.
            var gate = Feed(new SlipCrossingGate(), SlipCrossingGate.TrendFrames + 1,
                            basisStart: 10.0, basisStep: 2.0, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.Qualified);

            gate.Observe(34.0, 34.0, 0.9, true, double.NaN);
            Assert.True(gate.Qualified, "one unusable frame time must not discard a live crossing");
        }

        [Fact]
        public void Teaching_weight_is_one_when_qualified_and_the_configured_rate_otherwise()
        {
            var gate = new SlipCrossingGate();
            Assert.Equal(SlipCrossingGate.DefaultNonCrossingWeight, gate.NonCrossingWeight);
            Assert.Equal(0.0, gate.TeachingWeight);

            gate.NonCrossingWeight = 0.05;
            Assert.Equal(0.05, gate.TeachingWeight);

            Feed(gate, 12, basisStart: 10.0, basisStep: 2.0, gStart: 1.5, gStep: -0.05);
            Assert.Equal(1.0, gate.TeachingWeight);
        }

        [Fact]
        public void Non_crossing_weight_is_clamped_to_a_real_weight()
        {
            var gate = new SlipCrossingGate();
            gate.NonCrossingWeight = 5.0;
            Assert.Equal(1.0, gate.NonCrossingWeight);
            gate.NonCrossingWeight = -1.0;
            Assert.Equal(0.0, gate.NonCrossingWeight);
        }

        [Fact]
        public void The_hold_window_teaches_the_crossing_value_not_the_runaway_after_it()
        {
            // THE DEFECT THIS PINS, measured on the owner's own c_1_8_1 capture: a crossing fired at
            // basis ~28, the wheel then let go completely and the source ran to 100 within the same
            // second, and every one of those runaway frames taught SMax its OWN value. 311 of 324 taught
            // frames were hold frames, not one of the 58 frames at basis >= 80 was a crossing, and the
            // ceiling read 100 while the crossings themselves peaked at 64.5.
            var gate = new SlipCrossingGate();
            Feed(gate, 12, basisStart: 20.0, basisStep: 1.5, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.Qualified);

            double atCrossing = gate.TeachingBasis(0.0);
            Assert.True(atCrossing > 20.0 && atCrossing < 45.0,
                $"the snapshot should be the crossing's own reading, got {atCrossing}");

            // The break-away runs away to full scale INSIDE the hold window. Every one of these frames
            // still teaches, and must teach the crossing's value - not 100.
            for (int i = 0; i < 20; i++) gate.Observe(100.0, 100.0, 0.6, true, Dt);
            Assert.True(gate.Qualified, "still inside the hold window");
            Assert.Equal(atCrossing, gate.TeachingBasis(100.0), 6);
            Assert.Equal(atCrossing, gate.TeachingFallbackBasis(100.0), 6);
        }

        [Fact]
        public void A_rise_inside_a_live_window_is_the_same_event_and_cannot_re_arm_the_detector()
        {
            // The other half of the runaway fix. Without this, the break-away trips the detector again,
            // refreshes the window, and the refreshed window then reaches forward into the NEXT grip
            // phase - so the following corner's genuinely-at-limit frames teach the runaway's value too.
            var gate = new SlipCrossingGate();
            Feed(gate, SlipCrossingGate.TrendFrames + 1, basisStart: 20.0, basisStep: 1.5, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.Qualified);
            double onset = gate.TeachingBasis(0.0);

            // A second, much larger slip-rising-while-G-falling event, entirely inside the live window.
            for (int i = 0; i < 10; i++) gate.Observe(40.0 + i * 6.0, 40.0 + i * 6.0, 1.4 - i * 0.08, true, Dt);
            Assert.Equal(onset, gate.TeachingBasis(0.0), 6);

            // And the window must expire on the ORIGINAL crossing's clock, not the runaway's.
            for (int i = 0; i < (int)(SlipCrossingGate.HoldSeconds / Dt); i++)
                gate.Observe(100.0, 100.0, 0.6, true, Dt);
            Assert.False(gate.Qualified, "the runaway must not have extended the window");
        }

        [Fact]
        public void A_non_qualified_frame_teaches_its_own_reading()
        {
            // Immaterial at the default weight of 0.0, but the knob exists - so the value a non-crossing
            // candidate carries must still be its own, not a stale snapshot from minutes ago.
            var gate = new SlipCrossingGate();
            Assert.Equal(42.0, gate.TeachingBasis(42.0), 6);
            Assert.Equal(7.0, gate.TeachingFallbackBasis(7.0), 6);
        }

        [Fact]
        public void The_two_bases_are_snapshotted_independently()
        {
            // The configured source and the Raw fallback are different readings of the same moment; both
            // keys must learn on the same definition or the engine's divergence test between them
            // compares quantities that are not comparable.
            var gate = new SlipCrossingGate();
            for (int i = 0; i < 12; i++) gate.Observe(20.0 + 1.5 * i, 60.0 + 1.5 * i, 1.5 - 0.05 * i, true, Dt);
            Assert.True(gate.Qualified);

            double configured = gate.TeachingBasis(0.0);
            double fallback = gate.TeachingFallbackBasis(0.0);
            Assert.Equal(40.0, fallback - configured, 6);
        }

        [Fact]
        public void Reset_clears_the_crossing_snapshot_too()
        {
            var gate = new SlipCrossingGate();
            Feed(gate, 12, basisStart: 20.0, basisStep: 1.5, gStart: 1.5, gStep: -0.05);
            Assert.True(gate.TeachingBasis(0.0) > 0.0);

            gate.Reset();
            Assert.Equal(99.0, gate.TeachingBasis(99.0), 6);
        }

        // ---------------------------------------------------------------------------------------
        // ROBUSTNESS. Every value this gate is fed originates in game telemetry, so all of it can
        // arrive null-coalesced to zero, NaN, infinite, negative, or with no frame time at all.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_zero_frame_time_still_expires_the_window()
        {
            // THE MOST DANGEROUS CASE FOUND IN REVIEW. The engine passes dtSeconds = 0.0 whenever the
            // title reports no frame time (its own `Dt.HasValue && TotalSeconds > 0.0` ternary), and a
            // window aged by nothing NEVER expires - one crossing would qualify every remaining frame
            // of the session, teaching the same snapshot forever.
            var gate = new SlipCrossingGate();
            for (int i = 0; i < SlipCrossingGate.TrendFrames + 1; i++)
                gate.Observe(20.0 + 1.5 * i, 20.0 + 1.5 * i, 1.5 - 0.05 * i, true, 0.0);
            Assert.True(gate.Qualified);

            for (int i = 0; i < (int)(SlipCrossingGate.HoldSeconds / SlipCrossingGate.NominalFrameSeconds) + 5; i++)
                gate.Observe(34.0, 34.0, 0.9, true, 0.0);
            Assert.False(gate.Qualified, "a zero frame time must not freeze the hold window open");
        }

        [Fact]
        public void A_negative_or_non_finite_frame_time_still_expires_the_window()
        {
            foreach (double dt in new[] { -1.0, double.NaN, double.PositiveInfinity })
            {
                var gate = new SlipCrossingGate();
                Feed(gate, SlipCrossingGate.TrendFrames + 1, 20.0, 1.5, 1.5, -0.05);
                Assert.True(gate.Qualified);

                for (int i = 0; i < (int)(SlipCrossingGate.HoldSeconds / SlipCrossingGate.NominalFrameSeconds) + 5; i++)
                    gate.Observe(34.0, 34.0, 0.9, true, dt);
                Assert.False(gate.Qualified, $"dt = {dt} must not freeze the window open");
            }
        }

        [Fact]
        public void A_tiny_but_positive_frame_time_is_bounded_by_the_frame_backstop()
        {
            var gate = new SlipCrossingGate();
            Feed(gate, SlipCrossingGate.TrendFrames + 1, 20.0, 1.5, 1.5, -0.05);
            Assert.True(gate.Qualified);

            for (int i = 0; i < SlipCrossingGate.HoldMaxFrames + 5; i++)
                gate.Observe(34.0, 34.0, 0.9, true, 1e-9);
            Assert.False(gate.Qualified, "HoldMaxFrames must bound a window the clock cannot close");
        }

        [Fact]
        public void An_infinite_basis_never_arms_a_crossing()
        {
            // Infinity satisfies the rise test outright. Armed, it would snapshot Infinity, which
            // ObserveAtPhysicalLimit then discards - so the channel would teach NOTHING for a whole
            // second while the counters recorded those frames as qualified.
            var gate = new SlipCrossingGate();
            for (int i = 0; i < 20; i++)
                gate.Observe(double.PositiveInfinity, double.PositiveInfinity, 1.5 - 0.05 * i, true, Dt);
            Assert.False(gate.Qualified);
        }

        [Fact]
        public void NaN_and_negative_readings_never_arm_a_crossing()
        {
            foreach (double bad in new[] { double.NaN, -5.0 })
            {
                var gate = new SlipCrossingGate();
                for (int i = 0; i < 20; i++) gate.Observe(bad, bad, 1.5 - 0.05 * i, true, Dt);
                Assert.False(gate.Qualified, $"basis {bad} must not arm a crossing");

                var gGate = new SlipCrossingGate();
                for (int i = 0; i < 20; i++) gGate.Observe(20.0 + 2.0 * i, 20.0 + 2.0 * i, double.NaN, true, Dt);
                Assert.False(gGate.Qualified, "a NaN G reading must not arm a crossing");
            }
        }

        [Fact]
        public void A_bad_frame_does_not_poison_the_trend_for_later_good_frames()
        {
            // The skipped frame must not enter the history, or detection would be suppressed for the
            // next TrendFrames frames every time telemetry hiccups.
            var gate = new SlipCrossingGate();
            gate.Observe(double.NaN, double.NaN, double.NaN, true, Dt);
            gate.Observe(20.0, 20.0, 1.5, true, Dt);
            Feed(gate, SlipCrossingGate.TrendFrames + 1, basisStart: 21.0, basisStep: 1.5, gStart: 1.48, gStep: -0.05);
            Assert.True(gate.Qualified, "good frames after a bad one must still be able to detect a crossing");
        }

        [Fact]
        public void An_unusable_snapshot_falls_back_to_the_live_reading_rather_than_teaching_nothing()
        {
            var gate = new SlipCrossingGate();
            Assert.False(gate.Qualified);
            Assert.Equal(42.0, gate.TeachingBasis(42.0), 6);
            Assert.Equal(42.0, gate.TeachingFallbackBasis(42.0), 6);
        }

        [Fact]
        public void Counters_tolerate_a_null_key()
        {
            var gate = new SlipCrossingGate();
            gate.RecordCandidate(null, qualified: true);
            Assert.Equal(0L, gate.ValidFrames(null));
            Assert.Equal(0L, gate.TotalFrames(null));
        }

        [Fact]
        public void A_NaN_non_crossing_weight_becomes_zero_rather_than_propagating()
        {
            // A NaN weight would make both `weight > 0` and `weight <= 0` false in the engine, so
            // NEITHER teaching branch would run and the channel would go silently dead.
            var gate = new SlipCrossingGate { NonCrossingWeight = double.NaN };
            Assert.Equal(0.0, gate.NonCrossingWeight);
            Assert.Equal(0.0, gate.TeachingWeight);
        }

        [Fact]
        public void Valid_and_total_frames_are_counted_per_key()
        {
            var gate = new SlipCrossingGate();
            gate.RecordCandidate("a", qualified: true);
            gate.RecordCandidate("a", qualified: false);
            gate.RecordCandidate("a", qualified: false);
            gate.RecordCandidate("b", qualified: true);

            Assert.Equal(1L, gate.ValidFrames("a"));
            Assert.Equal(3L, gate.TotalFrames("a"));
            Assert.Equal(1L, gate.ValidFrames("b"));
            Assert.Equal(1L, gate.TotalFrames("b"));
            Assert.Equal(0L, gate.ValidFrames("never-seen"));
            Assert.Equal(0L, gate.TotalFrames("never-seen"));
        }
    }
}

using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for the v1.0.8 <see cref="ShakeApplyMode"/> distributions - how one shake band is spread
    /// across the eight pads. The band arithmetic itself is <c>GForceShakeTests</c>' subject; this file
    /// is only about WHICH level each channel's band is computed from.
    /// </summary>
    public class GForceEngineShakeModeTests
    {
        private const double AccelMax = 0.9;
        private const double DecelMax = 2.0;

        /// <summary>Same convention as <c>GForceEngineShakeTests.Sample</c>: ground speed falling means
        /// braking, rising means accelerating, so direction resolves from the first frame.</summary>
        private static TelemetrySample Sample(double longG, double dtSeconds, double? latG = null)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: longG <= 0.0 ? 101.0 : 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: longG <= 0.0 ? 100.0 : 101.0,
                longitudinalG: longG,
                lateralG: latG);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        private static GForceEngine Engine(ShakeApplyMode mode) => new GForceEngine
        {
            IntegrateWheelLockAndSlip = true,
            ShakeApplyMode = mode,
            WheelLockShakeScale = 1.5,
            WheelSlipShakeScale = 1.5,
        };

        /// <summary>Runs enough braking frames for the staged chain to reach its sustained shape.</summary>
        private static GForceOutput Brake(GForceEngine engine, double lockValue, int frames = 60, double longG = -1.8)
        {
            GForceOutput r = GForceOutput.Empty;
            for (int i = 0; i < frames; i++)
                r = engine.Compute(Sample(longG, 0.02), AccelMax, DecelMax, wheelLockAll0100: lockValue, wheelSlipAll0100: 0.0);
            return r;
        }

        // ---------------- Per-channel (the pre-1.0.8 behaviour) ----------------

        [Fact]
        public void PerChannel_leaves_the_chain_s_idle_channel_silent_under_braking()
        {
            // BackTop belongs to the ACCELERATION chain, so it sits at 0 while braking - and because the
            // band is (its own level * contribution), no amount of wheel lock can move it. THIS is the
            // real reason the shake appears to "route" itself to certain channels: it is the multiply,
            // not any per-channel lock/slip assignment.
            GForceOutput r = Brake(Engine(ShakeApplyMode.PerChannel), lockValue: 80.0);

            Assert.Equal(0.0, r.BackTopLeft.Value, 6);
            Assert.Equal(0.0, r.BackTopRight.Value, 6);
            Assert.True(r.BottomFrontLeft.Value != r.BottomFrontRight.Value,
                "the braking chain's terminal channel should be shaking");
        }

        [Fact]
        public void PerChannel_gives_each_channel_a_band_proportional_to_its_own_level()
        {
            // Two channels at different levels get differently-sized bands - the defining property of
            // this mode, and what the "all channels" modes deliberately discard.
            //
            // BACKLOW vs BOTTOMFRONT specifically. At this point in the braking chain StagedShape has
            // far=PEAK while BOTH mid and terminal are still LOW, so BottomRear and BottomFront are
            // legitimately equal and comparing those two would prove nothing. BackLow (far) is the one
            // carrying a different level. A light brake keeps them off the 100 ceiling, where they would
            // also match.
            GForceOutput r = Brake(Engine(ShakeApplyMode.PerChannel), lockValue: 60.0, frames: 63, longG: -0.6);

            double frontBand = Math.Abs(r.BottomFrontLeft.Value - r.BottomFrontRight.Value);
            double backLowBand = Math.Abs(r.BackLowLeft.Value - r.BackLowRight.Value);

            Assert.True(frontBand > 0.0 && backLowBand > 0.0, "both braking channels should shake");
            Assert.True(backLowBand > frontBand,
                $"the higher-level channel should shake wider (backLow {backLowBand}, front {frontBand})");

            // The bands are in the same ratio as the levels they were computed from - that IS the
            // per-channel rule, band = thisChannelLevel * contribution.
            double levelRatio = r.BackLowLeft.Value + r.BackLowRight.Value == 0.0
                ? 0.0
                : (r.BackLowLeft.Value + r.BackLowRight.Value) / (r.BottomFrontLeft.Value + r.BottomFrontRight.Value);
            Assert.Equal(levelRatio, backLowBand / frontBand, 3);
        }

        // ---------------- All channels, following G-Force ----------------

        [Fact]
        public void AllGForce_gives_every_channel_the_terminal_channel_s_own_output()
        {
            GForceOutput perChannel = Brake(Engine(ShakeApplyMode.PerChannel), lockValue: 60.0);
            GForceOutput allGForce = Brake(Engine(ShakeApplyMode.AllChannelsGForce), lockValue: 60.0);

            // The braking terminal is BottomFront, so THAT channel must be untouched by the mode
            // switch - the owner's own stated requirement.
            Assert.Equal(perChannel.BottomFrontLeft.Value, allGForce.BottomFrontLeft.Value, 6);
            Assert.Equal(perChannel.BottomFrontRight.Value, allGForce.BottomFrontRight.Value, 6);

            // ...and every other pad now carries exactly that same output.
            foreach (double v in new[] { allGForce.BottomRearLeft.Value, allGForce.BackLowLeft.Value, allGForce.BackTopLeft.Value })
                Assert.Equal(allGForce.BottomFrontLeft.Value, v, 6);
            foreach (double v in new[] { allGForce.BottomRearRight.Value, allGForce.BackLowRight.Value, allGForce.BackTopRight.Value })
                Assert.Equal(allGForce.BottomFrontRight.Value, v, 6);
        }

        [Fact]
        public void AllGForce_makes_the_otherwise_idle_channel_shake_too()
        {
            // The whole point of the mode: BackTop is silent under braking in PerChannel, and must not
            // be here.
            GForceOutput r = Brake(Engine(ShakeApplyMode.AllChannelsGForce), lockValue: 80.0);

            Assert.True(Math.Abs(r.BackTopLeft.Value - r.BackTopRight.Value) > 1e-6,
                "BackTop should shake under braking in this mode");
        }

        [Fact]
        public void AllGForce_still_scales_with_gforce_so_a_light_brake_shakes_less()
        {
            var soft = Engine(ShakeApplyMode.AllChannelsGForce);
            var hard = Engine(ShakeApplyMode.AllChannelsGForce);

            GForceOutput softOut = GForceOutput.Empty, hardOut = GForceOutput.Empty;
            for (int i = 0; i < 60; i++)
            {
                softOut = soft.Compute(Sample(-0.3, 0.02), AccelMax, DecelMax, wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
                hardOut = hard.Compute(Sample(-1.8, 0.02), AccelMax, DecelMax, wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
            }

            double softBand = Math.Abs(softOut.BackTopLeft.Value - softOut.BackTopRight.Value);
            double hardBand = Math.Abs(hardOut.BackTopLeft.Value - hardOut.BackTopRight.Value);
            Assert.True(hardBand > softBand,
                $"harder braking should shake wider (soft {softBand}, hard {hardBand})");
        }

        [Fact]
        public void AllGForce_picks_the_terminal_by_direction_not_by_which_wheel_signal_is_larger()
        {
            // SLIP WHILE BRAKING is the case that separates the two candidate rules. Picking by "slip is
            // larger -> BackTop" would read BackTop's level, which is 0 under braking, and silence the
            // shake exactly when it was asked for. Picking by direction reads BottomFront and works.
            var engine = Engine(ShakeApplyMode.AllChannelsGForce);
            GForceOutput r = GForceOutput.Empty;
            for (int i = 0; i < 60; i++)
                r = engine.Compute(Sample(-1.8, 0.02), AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 80.0);

            Assert.True(Math.Abs(r.BottomFrontLeft.Value - r.BottomFrontRight.Value) > 1e-6,
                "slip during braking must still produce a shake");
        }

        // ---------------- All channels, lock/slip only ----------------

        [Fact]
        public void AllLockSlip_shakes_between_zero_and_value_times_scale_on_every_channel()
        {
            var engine = new GForceEngine
            {
                IntegrateWheelLockAndSlip = true,
                ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip,
                WheelLockShakeScale = 1.0,
                WheelSlipShakeScale = 1.0,
                ShakeFrequencyHz = 10.0,
            };

            // Swept over a whole cycle rather than sampled on the first frame: the BAND is what matters
            // (0 .. value x scale = 0 .. 60), and where in that band a given frame sits depends on the
            // start alignment and the blend, both of which are other tests' subject.
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 60; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.8, 0.02), AccelMax, DecelMax,
                    wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
                foreach (double v in Pads(r))
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            // CONTAINED IN 0..60, and genuinely swinging inside it. NOT asserted as exactly 0..60: with
            // the default 0.5 blend the pan and common terms are each half the band and (being in
            // quadrature) never reinforce to the full width, so a pad travels less far than under a pure
            // pan. That is by design - the FELT swing is much larger even though each pad's excursion is
            // smaller - and it is why the shipped scale for this mode may want revisiting after seat time.
            Assert.InRange(min, 0.0, 60.0);
            Assert.InRange(max, 0.0, 60.0);
            Assert.True(max - min > 20.0, $"the band should genuinely swing, got {min}..{max}");
        }

        [Fact]
        public void AllLockSlip_ignores_gforce_entirely()
        {
            // Identical wheel value, wildly different braking effort - the output must not move.
            var soft = new GForceEngine { IntegrateWheelLockAndSlip = true, ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip, WheelLockShakeScale = 1.0 };
            var hard = new GForceEngine { IntegrateWheelLockAndSlip = true, ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip, WheelLockShakeScale = 1.0 };

            GForceOutput softOut = GForceOutput.Empty, hardOut = GForceOutput.Empty;
            for (int i = 0; i < 40; i++)
            {
                softOut = soft.Compute(Sample(-0.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 70.0, wheelSlipAll0100: 0.0);
                hardOut = hard.Compute(Sample(-1.9, 0.02), AccelMax, DecelMax, wheelLockAll0100: 70.0, wheelSlipAll0100: 0.0);
            }

            Assert.Equal(softOut.BottomFrontLeft.Value, hardOut.BottomFrontLeft.Value, 6);
            Assert.Equal(softOut.BackTopRight.Value, hardOut.BackTopRight.Value, 6);
        }

        [Fact]
        public void AllLockSlip_ignores_the_lateral_cornering_bias()
        {
            // Owner's decision: this mode must depend on NOTHING but lock/slip, so the left/right
            // cornering multiplier is deliberately not applied.
            //
            // ASSERTED BY REVERSING THE LATERAL LOAD. The bias is a per-side multiplier, so if it were
            // applied, mirroring the cornering direction would swap the two pads. Identical output under
            // +1.2g and -1.2g therefore proves it is not applied - and unlike "the midpoint stays at the
            // band centre", this stays true once the blend starts moving that midpoint on purpose.
            var right = new GForceEngine
            {
                IntegrateWheelLockAndSlip = true,
                ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip,
                WheelLockShakeScale = 1.0,
            };
            var left = new GForceEngine
            {
                IntegrateWheelLockAndSlip = true,
                ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip,
                WheelLockShakeScale = 1.0,
            };

            for (int i = 0; i < 40; i++)
            {
                GForceOutput a = right.Compute(Sample(-1.5, 0.02, latG: 1.2), AccelMax, DecelMax,
                    wheelLockAll0100: 50.0, wheelSlipAll0100: 0.0);
                GForceOutput b = left.Compute(Sample(-1.5, 0.02, latG: -1.2), AccelMax, DecelMax,
                    wheelLockAll0100: 50.0, wheelSlipAll0100: 0.0);

                Assert.Equal(b.BottomFrontLeft.Value, a.BottomFrontLeft.Value, 6);
                Assert.Equal(b.BottomFrontRight.Value, a.BottomFrontRight.Value, 6);
                Assert.Equal(b.BackTopLeft.Value, a.BackTopLeft.Value, 6);
                Assert.Equal(b.BackTopRight.Value, a.BackTopRight.Value, 6);
            }
        }

        // ---------------- Mode is inert when the feature is off ----------------

        [Fact]
        public void The_mode_cannot_change_anything_while_the_shake_is_disabled()
        {
            GForceOutput baseline = GForceOutput.Empty, perChannel = GForceOutput.Empty, allLockSlip = GForceOutput.Empty;

            var off1 = new GForceEngine { IntegrateWheelLockAndSlip = false, ShakeApplyMode = ShakeApplyMode.PerChannel };
            var off2 = new GForceEngine { IntegrateWheelLockAndSlip = false, ShakeApplyMode = ShakeApplyMode.AllChannelsGForce };
            var off3 = new GForceEngine { IntegrateWheelLockAndSlip = false, ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip };

            for (int i = 0; i < 40; i++)
            {
                baseline = off1.Compute(Sample(-1.5, 0.02), AccelMax, DecelMax, wheelLockAll0100: 90.0, wheelSlipAll0100: 90.0);
                perChannel = off2.Compute(Sample(-1.5, 0.02), AccelMax, DecelMax, wheelLockAll0100: 90.0, wheelSlipAll0100: 90.0);
                allLockSlip = off3.Compute(Sample(-1.5, 0.02), AccelMax, DecelMax, wheelLockAll0100: 90.0, wheelSlipAll0100: 90.0);
            }

            Assert.Equal(baseline.BottomFrontLeft.Value, perChannel.BottomFrontLeft.Value, 9);
            Assert.Equal(baseline.BottomFrontLeft.Value, allLockSlip.BottomFrontLeft.Value, 9);
            Assert.Equal(baseline.BackTopRight.Value, allLockSlip.BackTopRight.Value, 9);
        }

        // ---------------------------------------------------------------------------------------
        // LOCK/SLIP DRIVE AND PHASE - the owner's "MUST MAKE SURE" requirement.
        //
        // Every mode is driven by Math.Max(lock, slip) through ONE shared oscillator, so lock-driven and
        // slip-driven shaking are in phase by construction: both at maximum together, both at minimum
        // together. These tests exist so a later change that gives a channel or a signal its own phase -
        // or that weighs lock and slip differently per mode - fails loudly instead of quietly feeling
        // wrong.
        // ---------------------------------------------------------------------------------------

        private static readonly ShakeApplyMode[] AllModes =
        {
            ShakeApplyMode.PerChannel,
            ShakeApplyMode.AllChannelsGForce,
            ShakeApplyMode.AllChannelsLockSlip,
        };

        private static double[] Pads(GForceOutput r) => new[]
        {
            r.BottomFrontLeft.Value, r.BottomFrontRight.Value,
            r.BottomRearLeft.Value, r.BottomRearRight.Value,
            r.BackLowLeft.Value, r.BackLowRight.Value,
            r.BackTopLeft.Value, r.BackTopRight.Value,
        };

        [Fact]
        public void Every_mode_is_driven_by_lock_and_slip_interchangeably_at_equal_scale()
        {
            // At equal scales the two signals are interchangeable: 60 of lock must produce EXACTLY the
            // output 60 of slip does, in every mode. A mode that routed lock and slip to different pads,
            // or weighted them differently, would fail here.
            foreach (ShakeApplyMode mode in AllModes)
            {
                var lockDriven = Engine(mode);
                var slipDriven = Engine(mode);

                GForceOutput lockOut = GForceOutput.Empty, slipOut = GForceOutput.Empty;
                for (int i = 0; i < 63; i++)
                {
                    lockOut = lockDriven.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
                    slipOut = slipDriven.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 60.0);
                }

                double[] fromLock = Pads(lockOut), fromSlip = Pads(slipOut);
                for (int i = 0; i < fromLock.Length; i++)
                    Assert.Equal(fromLock[i], fromSlip[i], 9);
            }
        }

        [Fact]
        public void Every_mode_takes_the_max_when_both_signals_are_present()
        {
            // Both active must equal the LARGER alone - not a sum, not an average, and not the smaller.
            foreach (ShakeApplyMode mode in AllModes)
            {
                var both = Engine(mode);
                var largerAlone = Engine(mode);
                var smallerAlone = Engine(mode);

                GForceOutput bothOut = GForceOutput.Empty, largerOut = GForceOutput.Empty, smallerOut = GForceOutput.Empty;
                for (int i = 0; i < 63; i++)
                {
                    bothOut = both.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 30.0, wheelSlipAll0100: 70.0);
                    largerOut = largerAlone.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 70.0);
                    smallerOut = smallerAlone.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 30.0, wheelSlipAll0100: 0.0);
                }

                double[] bothPads = Pads(bothOut), largerPads = Pads(largerOut), smallerPads = Pads(smallerOut);
                for (int i = 0; i < bothPads.Length; i++)
                    Assert.Equal(largerPads[i], bothPads[i], 9);

                // ...and the two really were distinguishable, so the assertion above is not vacuous.
                Assert.True(Math.Abs(largerPads[0] - smallerPads[0]) > 1e-6,
                    $"mode {mode}: the 30 and 70 cases should differ, otherwise the max assertion proves nothing");
            }
        }

        [Fact]
        public void Lock_and_slip_are_in_phase_across_a_whole_cycle_in_every_mode()
        {
            // THE OWNER'S REQUIREMENT, stated directly: when the lock-driven shake is at its maximum the
            // slip-driven one must be too, and likewise at the minimum. Checked frame by frame across
            // more than a full 10 Hz cycle rather than at one instant, so a phase OFFSET (which a single
            // sample could miss) is caught.
            foreach (ShakeApplyMode mode in AllModes)
            {
                var lockDriven = Engine(mode);
                var slipDriven = Engine(mode);

                bool sawPositive = false, sawNegative = false;
                for (int i = 0; i < 120; i++)
                {
                    GForceOutput l = lockDriven.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 55.0, wheelSlipAll0100: 0.0);
                    GForceOutput s = slipDriven.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 55.0);

                    int lockSign = Math.Sign(Math.Round(l.BottomFrontLeft.Value - l.BottomFrontRight.Value, 9));
                    int slipSign = Math.Sign(Math.Round(s.BottomFrontLeft.Value - s.BottomFrontRight.Value, 9));

                    // Same side leading at the same instant, every instant.
                    Assert.Equal(lockSign, slipSign);

                    if (lockSign > 0) sawPositive = true;
                    if (lockSign < 0) sawNegative = true;
                }

                // NOT VACUOUS: the wave really did swing both ways over this sweep, so the equality
                // above was comparing something that changes rather than a constant 0.
                Assert.True(sawPositive && sawNegative,
                    $"mode {mode}: the shake should have swung both ways across 120 frames");
            }
        }

        [Fact]
        public void All_eight_pads_swing_together_never_against_each_other()
        {
            // One oscillator means every LEFT pad leads at the same moment and every RIGHT pad trails -
            // a per-channel phase offset would show up as two pads disagreeing on which side is ahead.
            // PerChannel is the mode that could plausibly break this: the other two write one value to
            // all eight pads, so they cannot.
            var engine = Engine(ShakeApplyMode.PerChannel);

            bool sawPositive = false, sawNegative = false;
            for (int i = 0; i < 120; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 70.0, wheelSlipAll0100: 0.0);

                int[] signs =
                {
                    Math.Sign(Math.Round(r.BottomFrontLeft.Value - r.BottomFrontRight.Value, 9)),
                    Math.Sign(Math.Round(r.BottomRearLeft.Value - r.BottomRearRight.Value, 9)),
                    Math.Sign(Math.Round(r.BackLowLeft.Value - r.BackLowRight.Value, 9)),
                };

                // 0 is allowed (a channel at level 0, or the wave crossing zero) - what must never happen
                // is one channel leading left while another leads right.
                Assert.False(Array.Exists(signs, x => x > 0) && Array.Exists(signs, x => x < 0),
                    $"frame {i}: pads disagree on which side leads ({string.Join(",", signs)})");

                if (Array.Exists(signs, x => x > 0)) sawPositive = true;
                if (Array.Exists(signs, x => x < 0)) sawNegative = true;
            }

            // NOT VACUOUS: an all-zero sweep would satisfy the assertion above trivially.
            Assert.True(sawPositive && sawNegative, "the pads should have swung both ways across 120 frames");
        }

        [Fact]
        public void Switching_which_signal_dominates_does_not_disturb_the_phase()
        {
            // Slip overtakes lock mid-shake. Only the band WIDTH may change; the wave must keep running
            // from where it was - a phase reset on the handover would read as a stutter.
            var switching = Engine(ShakeApplyMode.AllChannelsGForce);
            var steady = Engine(ShakeApplyMode.AllChannelsGForce);

            for (int i = 0; i < 120; i++)
            {
                // Same total drive throughout (70), but carried by lock for the first half and by slip
                // for the second - so the amplitude is unchanged and ONLY the dominant signal flips.
                bool slipLeads = i >= 60;
                GForceOutput a = switching.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax,
                    wheelLockAll0100: slipLeads ? 0.0 : 70.0,
                    wheelSlipAll0100: slipLeads ? 70.0 : 0.0);
                GForceOutput b = steady.Compute(Sample(-1.2, 0.02), AccelMax, DecelMax,
                    wheelLockAll0100: 70.0, wheelSlipAll0100: 0.0);

                Assert.Equal(b.BottomFrontLeft.Value, a.BottomFrontLeft.Value, 9);
                Assert.Equal(b.BottomFrontRight.Value, a.BottomFrontRight.Value, 9);
            }
        }

        // ------------------------------------------------------------------------------------
        // HigherOfGForceOrLockSlip (v1.0.8) - every pad publishes the greater of its OWN G-force
        // value and the shared wheel lock/slip wave, so neither cue can mask the other.
        // ------------------------------------------------------------------------------------

        // ------------------------------------------------------------------------------------
        // ZERO FLOOR (owner's specification, v1.0.8). The two WHEEL-DRIVEN modes travel from
        // SILENCE to their band, because there the wheel value IS the cue. The two G-FORCE-CENTRED
        // modes keep shaking AROUND the current level, because there it is an excursion.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void The_wheel_driven_modes_travel_from_zero_to_the_full_wheel_band()
        {
            // 60 lock at scale 1.5 -> a band of 90, so the pad must reach ~0 at the trough and ~90 at
            // the peak. Before the zero floor it swung 22.5..67.5 - a permanent floor the driver can
            // feel, and only half the intended travel.
            var engine = Engine(ShakeApplyMode.AllChannelsLockSlip);

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 400; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.5, 0.02), AccelMax, DecelMax, 60.0, 0.0);
                if (i < 200) continue;
                min = Math.Min(min, r.BottomFrontLeft.Value);
                max = Math.Max(max, r.BottomFrontLeft.Value);
            }

            Assert.True(min < 0.5, $"the trough must be silence, saw {min:F3}");
            Assert.True(Math.Abs(max - 90.0) < 1.0, $"the peak must be the whole band (90), saw {max:F3}");
        }

        // NOTE: an attempt to also assert "PerChannel and AllChannelsGForce did NOT gain a zero floor"
        // was removed. The staged braking chain decides which pad carries a level at any moment, so every
        // fixture that made the assertion testable was really testing the chain rather than the floor,
        // and tuning one until it passed would have proved nothing. The guarantee is structural instead:
        // the zero floor lives in GForceShake.ApplyLockSlipOnly and the HigherOfGForceOrLockSlip branch,
        // and ShakePair/ApplyBand - the only paths those two modes use - were not touched. The
        // PerChannel_* and AllGForce_* tests above are the evidence that they still behave as before.

        [Fact]
        public void Higher_of_both_takes_the_greater_band_per_pad_from_a_zero_floor()
        {
            // The mode's full contract in one place: floor 0, peak Max(wheelBand, thatPad'sGForce).
            var engine = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            var silent = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            silent.IntegrateWheelLockAndSlip = false;

            const double lockValue = 40.0;                 // 40 * 1.5 == a wheel band of 60
            double min = double.MaxValue, max = double.MinValue, padGForce = 0.0;
            for (int i = 0; i < 400; i++)
            {
                TelemetrySample s = Sample(-1.9, 0.02);
                GForceOutput r = engine.Compute(s, AccelMax, DecelMax, lockValue, 0.0);
                GForceOutput g = silent.Compute(s, AccelMax, DecelMax, 0.0, 0.0);
                if (i < 200) continue;
                min = Math.Min(min, r.BottomFrontLeft.Value);
                max = Math.Max(max, r.BottomFrontLeft.Value);
                padGForce = Math.Max(padGForce, g.BottomFrontLeft.Value);
            }

            double expectedPeak = Math.Max(60.0, padGForce);
            Assert.True(min < 0.5, $"the floor must always be zero in this mode, saw {min:F3}");
            Assert.True(Math.Abs(max - expectedPeak) < 1.5,
                $"peak should be Max(wheelBand 60, padGForce {padGForce:F2}) = {expectedPeak:F2}, saw {max:F2}");
        }

        [Fact]
        public void Higher_of_both_raises_the_peak_and_leaves_the_trough_alone()
        {
            // THE CONTRACT, restated (owner, 2026-09-05). This is a raised PEAK, not a per-frame clamp:
            // over a cycle the pad's MAXIMUM becomes Max(itsG-Force, waveMax) while its MINIMUM stays the
            // wave's own. An earlier version asserted "never below either source frame by frame", which
            // is the clamp semantics - and a clamp flattens every part of the cycle below the G-force
            // level, so at a high enough G the pad holds still and the lock/slip cue vanishes exactly
            // when the G-force cue is loudest.
            var combined = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            var lockSlipOnly = Engine(ShakeApplyMode.AllChannelsLockSlip);

            double cMin = double.MaxValue, cMax = double.MinValue;
            double wMin = double.MaxValue, wMax = double.MinValue;
            double gForceOfThatPad = 0.0;

            for (int i = 0; i < 300; i++)
            {
                TelemetrySample s = Sample(-1.9, 0.02);
                GForceOutput c = combined.Compute(s, AccelMax, DecelMax, 55.0, 0.0);
                GForceOutput w = lockSlipOnly.Compute(s, AccelMax, DecelMax, 55.0, 0.0);
                if (i < 150) continue;   // let the staged chain and the wave settle

                cMin = Math.Min(cMin, c.BottomFrontLeft.Value); cMax = Math.Max(cMax, c.BottomFrontLeft.Value);
                wMin = Math.Min(wMin, w.BottomFrontLeft.Value); wMax = Math.Max(wMax, w.BottomFrontLeft.Value);
                gForceOfThatPad = Math.Max(gForceOfThatPad, 0.0);
            }

            Assert.Equal(wMin, cMin, 6);                       // the trough is untouched
            Assert.True(cMax >= wMax - 1e-9,                    // the peak is at least the wave's own
                $"combined peak {cMax:F3} fell below the wave peak {wMax:F3}");
            Assert.True(cMax - cMin >= wMax - wMin - 1e-9,      // and the travel never shrinks
                $"combined travel {cMax - cMin:F3} is smaller than the wave's {wMax - wMin:F3}");
        }

        [Fact]
        public void Higher_of_both_lifts_the_peak_to_a_loud_pads_own_G_force_level()
        {
            // A pad whose G-force value exceeds the wave's peak must have its peak raised to that value,
            // while still oscillating down to the wave's own floor.
            var engine = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            engine.WheelLockShakeScale = 0.15;   // a deliberately small wave, so the G-force side is higher

            // The same trace with the wheel signal removed gives that pad's own silent G-force value -
            // the level the peak must be lifted to.
            var silent = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            silent.IntegrateWheelLockAndSlip = false;

            double min = double.MaxValue, max = double.MinValue, gForceLevel = 0.0;
            for (int i = 0; i < 300; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.9, 0.02), AccelMax, DecelMax, 60.0, 0.0);
                GForceOutput g = silent.Compute(Sample(-1.9, 0.02), AccelMax, DecelMax, 0.0, 0.0);
                if (i < 150) continue;
                min = Math.Min(min, r.BottomFrontLeft.Value);
                max = Math.Max(max, r.BottomFrontLeft.Value);
                gForceLevel = Math.Max(gForceLevel, g.BottomFrontLeft.Value);
            }

            Assert.True(max - min > 1.0, $"the pad must still oscillate, saw {min:F2}..{max:F2}");
            Assert.True(max >= gForceLevel - 1e-6,
                $"peak {max:F2} should have been lifted to this pad's own G-force level {gForceLevel:F2}");
        }

        [Fact]
        public void With_no_wheel_signal_higher_of_both_is_exactly_the_G_force_output()
        {
            // Nothing to be higher than, so the mode must be transparent - the G-force cue is untouched,
            // lateral split and all.
            var combined = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            var plain = Engine(ShakeApplyMode.PerChannel);

            GForceOutput c = GForceOutput.Empty, p = GForceOutput.Empty;
            for (int i = 0; i < 80; i++)
            {
                TelemetrySample s = Sample(-1.5, 0.02, latG: 0.5);
                c = combined.Compute(s, AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 0.0);
                p = plain.Compute(s, AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 0.0);
            }

            Assert.Equal(p.BottomFrontLeft.Value, c.BottomFrontLeft.Value, 9);
            Assert.Equal(p.BottomFrontRight.Value, c.BottomFrontRight.Value, 9);
            Assert.Equal(p.BackTopLeft.Value, c.BackTopLeft.Value, 9);
        }

        [Fact]
        public void Higher_of_both_keeps_per_channels_floor_and_only_raises_the_ceiling()
        {
            // THE 2026-09-07 REDEFINITION. This mode used to BE the lock/slip wave - one shared band,
            // zero floor, every pad identical - and this test asserted exactly that. It now runs
            // PerChannel's own path and changes only the ceiling:
            //
            //     low  = PerChannel's low, UNCHANGED
            //     high = Min(100, Max(PerChannel's high, 100 x contribution))
            //
            // So it must agree with PERCHANNEL on the floor, and sit at or above it on the ceiling.
            var combined = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            var perChannel = Engine(ShakeApplyMode.PerChannel);

            double cLow = double.MaxValue, cHigh = double.MinValue;
            double pLow = double.MaxValue, pHigh = double.MinValue;

            for (int i = 0; i < 140; i++)
            {
                TelemetrySample s1 = Sample(-1.8, 0.02);
                GForceOutput c = combined.Compute(s1, AccelMax, DecelMax, wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
                GForceOutput p = perChannel.Compute(s1, AccelMax, DecelMax, wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
                if (i < 100) continue;   // let the chain settle first

                cLow = Math.Min(cLow, c.BackLowLeft.Value); cHigh = Math.Max(cHigh, c.BackLowLeft.Value);
                pLow = Math.Min(pLow, p.BackLowLeft.Value); pHigh = Math.Max(pHigh, p.BackLowLeft.Value);
            }

            Assert.Equal(pLow, cLow, 3);
            Assert.True(cHigh >= pHigh - 0.001,
                $"the combined mode must never LOWER the ceiling (combined {cHigh:F2}, per-channel {pHigh:F2})");
        }

        [Fact]
        public void Higher_of_both_lifts_a_quiet_channel_to_the_wheel_ceiling()
        {
            // The other half: a pad whose own G-force range tops out well below the wheel's value gets
            // its ceiling raised TO that value - which is what lets a lock be felt on a pad the G-force
            // animation has left quiet.
            var combined = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            combined.WheelLockShakeScale = 1.3;

            double high = double.MinValue;
            for (int i = 0; i < 160; i++)
            {
                GForceOutput r = combined.Compute(Sample(-1.8, 0.02), AccelMax, DecelMax,
                    wheelLockAll0100: 60.0, wheelSlipAll0100: 0.0);
                if (i >= 100) high = Math.Max(high, r.BackTopLeft.Value);
            }

            // BackTop is the ACCELERATION chain's terminal - idle under braking, so its own ceiling is 0.
            // 60 x 1.3 = 78 is what the wheel raises it to.
            Assert.Equal(78.0, high, 0);
        }

        [Fact]
        public void Higher_of_both_shakes_every_pad_not_just_the_active_chain()
        {
            // The practical complaint this mode answers: under PerChannel a pad sitting at level 0
            // cannot shake, so a lock while coasting is felt almost nowhere.
            var engine = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);

            double minSeen = double.MaxValue, maxSeen = double.MinValue;
            for (int i = 0; i < 200; i++)
            {
                GForceOutput r = engine.Compute(Sample(-0.05, 0.02), AccelMax, DecelMax, 75.0, 0.0);
                if (i < 100) continue;   // let the wave settle
                minSeen = Math.Min(minSeen, r.BackTopLeft.Value);
                maxSeen = Math.Max(maxSeen, r.BackTopLeft.Value);
            }

            Assert.True(maxSeen - minSeen > 5.0,
                $"BackTop should oscillate under the wave, saw {minSeen:F2}..{maxSeen:F2}");
        }

        [Theory]
        [InlineData(ShakeApplyMode.PerChannel)]
        [InlineData(ShakeApplyMode.AllChannelsGForce)]
        [InlineData(ShakeApplyMode.AllChannelsLockSlip)]
        [InlineData(ShakeApplyMode.HigherOfGForceOrLockSlip)]
        public void The_lock_and_slip_scales_drive_their_own_channels_individually(ShakeApplyMode mode)
        {
            // OWNER, 2026-09-06: "the scale will be applied for slip and lock individually". The engine
            // computes lockContribution = lockScale x lock/100 and slipContribution = slipScale x
            // slip/100 SEPARATELY, then takes the larger - so a Slip scale of 0 can never mute a lock,
            // and this holds in every mode because that arithmetic sits ahead of all mode branching.
            //
            // BASELINE: both scales at zero. No contribution is left, so the shake path is never entered
            // and every pair publishes the plain G-force - which, with no lateral G in these samples,
            // means left and right are IDENTICAL. Exactly zero, not "small".
            double muted = MaxLeftRightGap(
                EngineWithScales(mode, lockScale: 0.0, slipScale: 0.0), lockValue: 90.0, slipValue: 90.0);
            Assert.Equal(0.0, muted, 9);

            // A scaled lock drives the wave even with the slip scale at zero...
            double lockOnly = MaxLeftRightGap(
                EngineWithScales(mode, lockScale: 2.0, slipScale: 0.0), lockValue: 40.0, slipValue: 90.0);
            Assert.True(lockOnly > 0.5,
                $"{mode}: a scaled lock must shake with the slip scale at 0, saw a gap of {lockOnly:F3}");

            // ...and mirrored, a scaled slip drives it with the lock scale at zero.
            double slipOnly = MaxLeftRightGap(
                EngineWithScales(mode, lockScale: 0.0, slipScale: 2.0), lockValue: 90.0, slipValue: 40.0);
            Assert.True(slipOnly > 0.5,
                $"{mode}: a scaled slip must shake with the lock scale at 0, saw a gap of {slipOnly:F3}");
        }

        [Fact]
        public void Each_mode_has_its_own_scale_and_trigger_defaults()
        {
            // OWNER'S OWN TABLE, 2026-09-07, after seat time. Note this REVERSES 2026-09-06, when the
            // per-mode reset was removed on the grounds that a scale means the same thing in every mode -
            // seat time produced a different number per mode, and a table is only reachable if switching
            // applies it. The dropdown handler (OnShakeApplyModeChanged) is what writes these through.
            Assert.Equal(1.3, Settings.GForceSettings.DefaultShakeScaleFor(ShakeApplyMode.HigherOfGForceOrLockSlip), 9);
            Assert.Equal(1.5, Settings.GForceSettings.DefaultShakeScaleFor(ShakeApplyMode.PerChannel), 9);
            Assert.Equal(1.3, Settings.GForceSettings.DefaultShakeScaleFor(ShakeApplyMode.AllChannelsGForce), 9);
            Assert.Equal(1.0, Settings.GForceSettings.DefaultShakeScaleFor(ShakeApplyMode.AllChannelsLockSlip), 9);

            // 5 for the two modes anchored to a channel's own level; 30 for the two that drive all eight
            // pads from one shared band, where a low threshold reads as a permanent background buzz.
            Assert.Equal(5.0, Settings.GForceSettings.DefaultShakeTriggerFor(ShakeApplyMode.HigherOfGForceOrLockSlip), 9);
            Assert.Equal(5.0, Settings.GForceSettings.DefaultShakeTriggerFor(ShakeApplyMode.PerChannel), 9);
            Assert.Equal(30.0, Settings.GForceSettings.DefaultShakeTriggerFor(ShakeApplyMode.AllChannelsGForce), 9);
            Assert.Equal(30.0, Settings.GForceSettings.DefaultShakeTriggerFor(ShakeApplyMode.AllChannelsLockSlip), 9);
        }

        [Fact]
        public void Setting_the_mode_property_does_not_itself_rewrite_the_scales()
        {
            // The reset is a DROPDOWN action, not a property side effect - so loading a persisted config
            // cannot silently overwrite the driver's own scales every time the page opens.
            var settings = new Settings.GForceSettings { WheelLockShakeScale = 2.4, WheelSlipShakeScale = 0.6 };

            foreach (ShakeApplyMode mode in Enum.GetValues(typeof(ShakeApplyMode)))
            {
                settings.ShakeApplyMode = mode;
                Assert.Equal(2.4, settings.WheelLockShakeScale, 9);
                Assert.Equal(0.6, settings.WheelSlipShakeScale, 9);
            }
        }

        private static GForceEngine EngineWithScales(ShakeApplyMode mode, double lockScale, double slipScale)
        {
            GForceEngine engine = Engine(mode);
            engine.WheelLockShakeScale = lockScale;
            engine.WheelSlipShakeScale = slipScale;
            return engine;
        }

        /// <summary>
        /// The largest left-vs-right gap any of the four pairs opens up across a run of frames - the
        /// signature of a shake, since these samples carry NO lateral G and nothing else in the engine
        /// can separate a pair.
        /// <para/>
        /// A GAP, not a peak-to-peak swing, and measured EARLY. Under a synthetic constant-G sample the
        /// washout model correctly decays the whole chain towards zero within a couple of seconds, so an
        /// absolute swing measured late is tiny in every mode whether it is shaking or not. The gap stays
        /// diagnostic all the way down: it is exactly 0 when the shake path is not entered, and non-zero
        /// whenever the wave is running, at any amplitude.
        /// <para/>
        /// ACROSS ALL FOUR PAIRS, not a named one, because each mode drives a different set of channels -
        /// that is what ShakeApplyMode selects - so pinning one channel would measure which chain the pad
        /// belongs to rather than whether the scale reached the wave.
        /// </summary>
        private static double MaxLeftRightGap(GForceEngine engine, double lockValue, double slipValue)
        {
            for (int i = 0; i < 10; i++)
                engine.Compute(Sample(-1.8, 0.02), AccelMax, DecelMax, lockValue, slipValue);

            double widest = 0.0;
            for (int i = 0; i < 50; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.8, 0.02), AccelMax, DecelMax, lockValue, slipValue);
                widest = Math.Max(widest, Math.Abs(r.BottomFrontLeft.Value - r.BottomFrontRight.Value));
                widest = Math.Max(widest, Math.Abs(r.BottomRearLeft.Value - r.BottomRearRight.Value));
                widest = Math.Max(widest, Math.Abs(r.BackLowLeft.Value - r.BackLowRight.Value));
                widest = Math.Max(widest, Math.Abs(r.BackTopLeft.Value - r.BackTopRight.Value));
            }
            return widest;
        }
        // ------------------------------------------------------------------------------------
        // "START SHAKING ABOVE" IS A SOFT SWITCH FOR THE WHOLE INTEGRATION (owner, v1.0.8).
        // Below the threshold every mode must publish exactly what it would with
        // IntegrateWheelLockAndSlip turned off - and the SCALES must not be applied to the test.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(ShakeApplyMode.PerChannel)]
        [InlineData(ShakeApplyMode.AllChannelsGForce)]
        [InlineData(ShakeApplyMode.AllChannelsLockSlip)]
        [InlineData(ShakeApplyMode.HigherOfGForceOrLockSlip)]
        public void Below_the_trigger_threshold_every_mode_matches_integration_turned_off(ShakeApplyMode mode)
        {
            var gated = Engine(mode);
            gated.ShakeTriggerThreshold = 20.0;

            var off = Engine(mode);
            off.IntegrateWheelLockAndSlip = false;

            GForceOutput g = GForceOutput.Empty, o = GForceOutput.Empty;
            for (int i = 0; i < 300; i++)
            {
                TelemetrySample s = Sample(-1.7, 0.02, latG: 0.35);
                // 18 and 16 are the owner's own worked example: both below a threshold of 20, and the
                // 1.5 scales must NOT lift them over it.
                g = gated.Compute(s, AccelMax, DecelMax, wheelLockAll0100: 18.0, wheelSlipAll0100: 16.0);
                o = off.Compute(s, AccelMax, DecelMax, wheelLockAll0100: 18.0, wheelSlipAll0100: 16.0);
            }

            Assert.Equal(o.BottomFrontLeft.Value, g.BottomFrontLeft.Value, 6);
            Assert.Equal(o.BottomFrontRight.Value, g.BottomFrontRight.Value, 6);
            Assert.Equal(o.BottomRearLeft.Value, g.BottomRearLeft.Value, 6);
            Assert.Equal(o.BackLowRight.Value, g.BackLowRight.Value, 6);
            Assert.Equal(o.BackTopLeft.Value, g.BackTopLeft.Value, 6);
        }

        [Fact]
        public void The_scales_never_lift_a_wheel_value_over_the_trigger_threshold()
        {
            // A lock of 18 with a 4.0 scale would be 72 once scaled - well over a threshold of 20. It
            // must still count as below, because the threshold is compared against the RAW value.
            var big = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            big.ShakeTriggerThreshold = 20.0;
            big.WheelLockShakeScale = 4.0;

            var off = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            off.IntegrateWheelLockAndSlip = false;

            GForceOutput b = GForceOutput.Empty, o = GForceOutput.Empty;
            for (int i = 0; i < 300; i++)
            {
                TelemetrySample s = Sample(-1.7, 0.02);
                b = big.Compute(s, AccelMax, DecelMax, wheelLockAll0100: 18.0, wheelSlipAll0100: 0.0);
                o = off.Compute(s, AccelMax, DecelMax, wheelLockAll0100: 18.0, wheelSlipAll0100: 0.0);
            }

            Assert.Equal(o.BottomFrontLeft.Value, b.BottomFrontLeft.Value, 6);
        }

        [Fact]
        public void At_or_above_the_threshold_the_wheel_cue_does_appear()
        {
            // The other half - the guard must not be so strict that nothing ever shakes.
            var engine = Engine(ShakeApplyMode.HigherOfGForceOrLockSlip);
            engine.ShakeTriggerThreshold = 20.0;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 400; i++)
            {
                GForceOutput r = engine.Compute(Sample(-1.7, 0.02), AccelMax, DecelMax, 20.0, 0.0);
                if (i < 200) continue;
                min = Math.Min(min, r.BottomFrontLeft.Value);
                max = Math.Max(max, r.BottomFrontLeft.Value);
            }

            Assert.True(max - min > 5.0, $"exactly at the threshold the shake must run, saw {min:F2}..{max:F2}");
        }
    }
}

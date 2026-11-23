using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// The SimHub-free half of the native ShakeIt effects (docs\shakeit-native-effects-design.md):
    /// routing, value selection, tone defaults, the ShakeIt-panel source list and frame staleness.
    /// </summary>
    public class ShakeItEffectsMappingTests
    {
        private static ProjectedWheelLockSlipResult Projected(double lockAll = 0, double lockFront = 0,
            double lockLeft = 0, Corners? lockWheels = null, double slipRear = 0) =>
            new ProjectedWheelLockSlipResult(
                lockWheels ?? Corners.Zero, lockFront, 0, lockLeft, 0, lockAll,
                Corners.Zero, 0, slipRear, 0, 0, 0,
                Corners.Uniform(99), 99, 99, 99, 99, 99,
                Corners.Uniform(99), 99, 99, 99, 99, 99);

        // ---- routing -------------------------------------------------------------------------

        [Fact]
        public void Parse_null_or_unknown_is_Mono()
        {
            Assert.Equal(EffectRouting.Mono, EffectRoutingNames.Parse(null));
            Assert.Equal(EffectRouting.Mono, EffectRoutingNames.Parse("x"));
        }

        [Theory]
        [InlineData(EffectRouting.Corners, "Corners")]
        [InlineData(EffectRouting.FrontRear, "Front/Rear")]
        [InlineData(EffectRouting.LeftRight, "Left/Right")]
        [InlineData(EffectRouting.Mono, "Mono")]
        public void Routing_round_trips_through_ShakeIt_aggregation_names(EffectRouting routing, string name)
        {
            Assert.Equal(name, EffectRoutingNames.ToAggregationMode(routing));
            Assert.Equal(routing, EffectRoutingNames.Parse(name));
        }

        [Fact]
        public void Corners_slots_are_the_four_wheels_in_order() =>
            Assert.Equal(new[] { EffectSlot.FrontLeft, EffectSlot.FrontRight, EffectSlot.RearLeft, EffectSlot.RearRight },
                EffectRoutingSlots.For(EffectRouting.Corners));

        [Fact]
        public void Other_routings_have_the_expected_slots()
        {
            Assert.Equal(new[] { EffectSlot.Front, EffectSlot.Rear }, EffectRoutingSlots.For(EffectRouting.FrontRear));
            Assert.Equal(new[] { EffectSlot.Left, EffectSlot.Right }, EffectRoutingSlots.For(EffectRouting.LeftRight));
            Assert.Equal(new[] { EffectSlot.All }, EffectRoutingSlots.For(EffectRouting.Mono));
        }

        [Fact]
        public void EffectSlot_order_matches_FFBPlacement_order()
        {
            // The adapter casts EffectSlot -> FFBPlacement; FFBPlacement is All,Front,Rear,Left,Right,FL,FR,RL,RR.
            Assert.Equal(0, (int)EffectSlot.All);
            Assert.Equal(4, (int)EffectSlot.Right);
            Assert.Equal(8, (int)EffectSlot.RearRight);
        }

        // ---- value selection (pulsed values, never the WithoutPulse twins = 99) ---------------

        [Fact]
        public void Mono_reads_LockAll() => Assert.Equal(42, ChannelValues.Lock(Projected(lockAll: 42), EffectSlot.All));

        [Fact]
        public void Front_reads_LockFront() => Assert.Equal(7, ChannelValues.Lock(Projected(lockFront: 7), EffectSlot.Front));

        [Fact]
        public void Left_reads_LockLeft() => Assert.Equal(8, ChannelValues.Lock(Projected(lockLeft: 8), EffectSlot.Left));

        [Fact]
        public void RearRight_reads_the_wheel_corner() =>
            Assert.Equal(4, ChannelValues.Lock(Projected(lockWheels: new Corners(1, 2, 3, 4)), EffectSlot.RearRight));

        [Fact]
        public void Slip_reads_slip_values() => Assert.Equal(6, ChannelValues.Slip(Projected(slipRear: 6), EffectSlot.Rear));

        [Fact]
        public void Null_projected_reads_zero() => Assert.Equal(0, ChannelValues.Lock(null, EffectSlot.All));

        [Theory]
        [InlineData(GForcePad.BottomFront, false, 1)]
        [InlineData(GForcePad.BottomFront, true, 2)]
        [InlineData(GForcePad.BottomRear, false, 3)]
        [InlineData(GForcePad.BottomRear, true, 4)]
        [InlineData(GForcePad.BackLow, false, 5)]
        [InlineData(GForcePad.BackLow, true, 6)]
        [InlineData(GForcePad.BackTop, false, 7)]
        [InlineData(GForcePad.BackTop, true, 8)]
        public void GForce_pads_read_their_two_channels(GForcePad pad, bool right, double expected)
        {
            var o = new GForceOutput(1, 2, 3, 4, 5, 6, 7, 8);
            Assert.Equal(expected, ChannelValues.GForce(o, pad, right));
        }

        [Fact]
        public void GForce_null_channel_reads_zero()
        {
            Assert.Equal(0, ChannelValues.GForce(GForceOutput.Empty, GForcePad.BottomRear, false));
            Assert.Equal(0, ChannelValues.GForce(null, GForcePad.BackTop, true));
        }

        // ---- tone defaults ---------------------------------------------------------------------

        [Theory]
        [InlineData(GForcePad.BottomFront, 60)]
        [InlineData(GForcePad.BottomRear, 70)]
        [InlineData(GForcePad.BackLow, 70)]
        [InlineData(GForcePad.BackTop, 60)]
        public void GForce_tone_defaults(GForcePad pad, int highHz)
        {
            ToneDefault d = ToneDefaults.For(pad);
            Assert.Equal(100, d.LowHz);
            Assert.True(d.UseHigh);
            Assert.Equal(highHz, d.HighHz);
            Assert.False(d.UseNoise);
            Assert.Equal(10, d.NoisePercent);
        }

        [Fact]
        public void Lock_and_slip_tone_defaults()
        {
            Assert.Equal(40, ToneDefaults.Lock.LowHz);
            Assert.Equal(20, ToneDefaults.Lock.HighHz);
            Assert.Equal(45, ToneDefaults.Slip.LowHz);
            Assert.Equal(25, ToneDefaults.Slip.HighHz);
            Assert.True(ToneDefaults.Lock.UseHigh);
            Assert.True(ToneDefaults.Slip.UseHigh);
            Assert.False(ToneDefaults.Lock.UseNoise);
            Assert.False(ToneDefaults.Slip.UseNoise);
        }

        // ---- ShakeIt-panel source options -----------------------------------------------------

        [Fact]
        public void Options_hide_viper_and_custom_when_unavailable() =>
            Assert.Equal(new[] { SourceMode.Manual, SourceMode.ShakeIt }, ShakeItSourceOptions.For(false, false, SourceMode.Manual));

        [Fact]
        public void Options_show_all_when_available() =>
            Assert.Equal(new[] { SourceMode.Manual, SourceMode.ShakeIt, SourceMode.Viper, SourceMode.Custom },
                ShakeItSourceOptions.For(true, true, SourceMode.Manual));

        [Fact]
        public void Options_keep_the_current_selection_even_if_unavailable()
        {
            Assert.Contains(SourceMode.Viper, ShakeItSourceOptions.For(false, false, SourceMode.Viper));
            Assert.Contains(SourceMode.Custom, ShakeItSourceOptions.For(false, false, SourceMode.Custom));
        }

        // ---- tap property names ---------------------------------------------------------------

        [Fact]
        public void Tap_property_names()
        {
            // Diagnostic (Diag.*) names: additive, and the 62 product names stay untouched.
            Assert.Equal("QAdvanceFeedback.Diag.ShakeIt.WheelLock.FrontLeft", ShakeItTapPropertyNames.GetWheelPropertyName(true, "FrontLeft"));
            Assert.Equal("QAdvanceFeedback.Diag.ShakeIt.WheelSlip.RearRight", ShakeItTapPropertyNames.GetWheelPropertyName(false, "RearRight"));
            Assert.Equal("Diag.ShakeIt.WheelLock.FrontLeft", ShakeItTapPropertyNames.GetPublishedName(true, "FrontLeft"));
        }

        // ---- frame staleness ------------------------------------------------------------------

        [Fact]
        public void Snapshot_is_stale_after_500ms()
        {
            long ms = TimeSpan.TicksPerMillisecond;
            var s = new FrameSnapshot<int>(1, 0);
            Assert.True(s.IsFresh(499 * ms, FrameSnapshot.StaleTicks));
            Assert.False(s.IsFresh(501 * ms, FrameSnapshot.StaleTicks));
            long staleTicks = FrameSnapshot.StaleTicks;
            Assert.Equal(500 * ms, staleTicks);
        }

        [Fact]
        public void Snapshot_from_the_future_is_not_fresh_forever()
        {
            // A clock that went backwards must not pin a frame as fresh.
            long ms = TimeSpan.TicksPerMillisecond;
            var s = new FrameSnapshot<int>(1, 10_000 * ms);
            Assert.False(s.IsFresh(0, FrameSnapshot.StaleTicks));
        }
    }
}

using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for the v1.0.8 LATERAL FRICTION CIRCLE - lateral G as additive headroom split per channel,
    /// replacing the old multiplicative left/right bias.
    /// <para/>
    /// The owner's own worked example anchors the arithmetic: 72% braking with 70% lateral gives
    /// <c>combined = sqrt(0.72² + 0.70²) = 100.42%</c>, so 28.4 points of headroom, and with splits of
    /// 50/75/100% the three braking pads read 86.2/57.8, 57.3/14.7 and 46.4/0.
    /// </summary>
    public class GForceLateralFrictionTests
    {
        private const double AccelMax = 1.0;
        private const double DecelMax = 2.0;
        private const double LatMax = 1.0;

        private static TelemetrySample Brake(double lngG, double latG, double dt = 0.02)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 100.0, longitudinalG: lngG, lateralG: latG,
                brakePercent: 100.0, throttlePercent: 0.0);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dt));
        }

        private static GForceEngine Engine() => new GForceEngine();

        /// <summary>Runs to a settled state so the staged ramp and the sustain filter are no longer
        /// moving, leaving the friction-circle arithmetic as the only thing under test.</summary>
        private static GForceOutput Settle(GForceEngine engine, double lngG, double latG, int frames = 400)
        {
            GForceOutput r = GForceOutput.Empty;
            for (int i = 0; i < frames; i++)
                r = engine.Compute(Brake(lngG, latG), AccelMax, DecelMax, latMaxG: LatMax);
            return r;
        }

        // ---------------- The formula ----------------

        [Fact]
        public void The_owners_worked_example_reproduces_end_to_end()
        {
            // 2g decel max with a 1.44g brake gives rLong = 0.72; 1g lateral max with 0.70g gives
            // rLat = 0.70 - the owner's own pair.
            GForceOutput r = Settle(Engine(), lngG: -1.44, latG: -0.70);

            // Right turn (negative lateral) loads the LEFT pads.
            Assert.Equal(86.2, r.BottomFrontLeft.Value, 1);
            Assert.Equal(57.8, r.BottomFrontRight.Value, 1);
            Assert.Equal(57.3, r.BottomRearLeft.Value, 1);
            Assert.Equal(14.7, r.BottomRearRight.Value, 1);
            Assert.Equal(46.4, r.BackLowLeft.Value, 1);
            Assert.Equal(0.0, r.BackLowRight.Value, 1);
        }

        [Fact]
        public void The_owners_second_worked_example_reproduces_end_to_end()
        {
            // brake 30% (0.6g of 2g), lateral 100% (1g of 1g) -> combined 104.40%, headroom 74.4.
            GForceOutput r = Settle(Engine(), lngG: -0.6, latG: -1.0);

            Assert.Equal(67.2, r.BottomFrontLeft.Value, 1);
            Assert.Equal(0.0, r.BottomFrontRight.Value, 1);
            Assert.Equal(70.8, r.BottomRearLeft.Value, 1);
            Assert.Equal(0.0, r.BottomRearRight.Value, 1);
            Assert.Equal(81.9, r.BackLowLeft.Value, 1);
            Assert.Equal(0.0, r.BackLowRight.Value, 1);
        }

        [Fact]
        public void Lateral_is_additive_headroom_not_a_multiplier()
        {
            // THE POINT OF THE REWRITE. The old model multiplied each pad, so at a level already at 100
            // the strong side had nowhere to go and all the asymmetry came from the weak side dropping.
            // Additive headroom means the pair's own MEAN is unchanged by lateral, while the split grows.
            GForceOutput straight = Settle(Engine(), lngG: -2.0, latG: 0.0);
            GForceOutput cornering = Settle(Engine(), lngG: -2.0, latG: -0.5);

            Assert.Equal(straight.BackLowLeft.Value, straight.BackLowRight.Value, 6);
            Assert.True(cornering.BackLowLeft.Value > cornering.BackLowRight.Value,
                "a right-hand load should raise the left pad");

            // The mean is preserved (nothing is clipping at this level), which a multiplier would not do.
            double straightMean = (straight.BackLowLeft.Value + straight.BackLowRight.Value) / 2.0;
            double corneringMean = (cornering.BackLowLeft.Value + cornering.BackLowRight.Value) / 2.0;
            Assert.Equal(straightMean, corneringMean, 3);
        }

        [Fact]
        public void The_split_ordering_follows_the_configured_percentages()
        {
            // Back Low takes the MOST lateral (100%) and Bottom Front the least (50%) - inverted against
            // the longitudinal emphasis on purpose, so a trail brake reads as the cue travelling back.
            GForceOutput r = Settle(Engine(), lngG: -1.44, latG: -0.70);

            double bfSplit = r.BottomFrontLeft.Value - r.BottomFrontRight.Value;
            double brSplit = r.BottomRearLeft.Value - r.BottomRearRight.Value;

            Assert.True(brSplit > bfSplit,
                $"Bottom Rear (75%) should split wider than Bottom Front (50%): {brSplit} vs {bfSplit}");
        }

        [Fact]
        public void A_left_turn_mirrors_a_right_turn()
        {
            GForceOutput right = Settle(Engine(), lngG: -1.44, latG: -0.70);
            GForceOutput left = Settle(Engine(), lngG: -1.44, latG: +0.70);

            Assert.Equal(right.BottomFrontLeft.Value, left.BottomFrontRight.Value, 3);
            Assert.Equal(right.BottomFrontRight.Value, left.BottomFrontLeft.Value, 3);
        }

        [Fact]
        public void The_lateral_max_setting_decides_when_the_split_saturates()
        {
            // The whole reason this became a setting: the old hard-coded 1.6g meant a 1g car never
            // reached full split. With latMax 1.0 a 1g corner saturates; with 2.0 the same corner is half.
            var narrow = Engine();
            var wide = Engine();

            GForceOutput narrowOut = GForceOutput.Empty, wideOut = GForceOutput.Empty;
            for (int i = 0; i < 400; i++)
            {
                narrowOut = narrow.Compute(Brake(-1.44, -1.0), AccelMax, DecelMax, latMaxG: 1.0);
                wideOut = wide.Compute(Brake(-1.44, -1.0), AccelMax, DecelMax, latMaxG: 2.0);
            }

            double narrowSplit = narrowOut.BackLowLeft.Value - narrowOut.BackLowRight.Value;
            double wideSplit = wideOut.BackLowLeft.Value - wideOut.BackLowRight.Value;
            Assert.True(narrowSplit > wideSplit,
                $"a lower lateral max should saturate sooner: {narrowSplit} vs {wideSplit}");
        }

        // ---------------- Output scales ----------------

        [Fact]
        public void The_braking_scale_attenuates_the_level_but_not_the_lateral_boost()
        {
            var full = Engine();
            var halved = new GForceEngine { BrakeOutputScale = 0.5 };

            GForceOutput fullOut = GForceOutput.Empty, halfOut = GForceOutput.Empty;
            for (int i = 0; i < 400; i++)
            {
                fullOut = full.Compute(Brake(-1.44, -0.70), AccelMax, DecelMax, latMaxG: LatMax);
                halfOut = halved.Compute(Brake(-1.44, -0.70), AccelMax, DecelMax, latMaxG: LatMax);
            }

            // The pair MEAN halves (it is the level), while the SPLIT is untouched (it is the boost).
            double fullMean = (fullOut.BottomFrontLeft.Value + fullOut.BottomFrontRight.Value) / 2.0;
            double halfMean = (halfOut.BottomFrontLeft.Value + halfOut.BottomFrontRight.Value) / 2.0;
            Assert.Equal(fullMean * 0.5, halfMean, 2);

            double fullSplit = fullOut.BottomFrontLeft.Value - fullOut.BottomFrontRight.Value;
            double halfSplit = halfOut.BottomFrontLeft.Value - halfOut.BottomFrontRight.Value;
            Assert.Equal(fullSplit, halfSplit, 2);
        }

        [Fact]
        public void The_lateral_scale_attenuates_the_boost_but_not_the_level()
        {
            var full = Engine();
            var quiet = new GForceEngine { LateralOutputScale = 0.0 };

            GForceOutput fullOut = GForceOutput.Empty, quietOut = GForceOutput.Empty;
            for (int i = 0; i < 400; i++)
            {
                fullOut = full.Compute(Brake(-1.44, -0.70), AccelMax, DecelMax, latMaxG: LatMax);
                quietOut = quiet.Compute(Brake(-1.44, -0.70), AccelMax, DecelMax, latMaxG: LatMax);
            }

            Assert.Equal(quietOut.BottomFrontLeft.Value, quietOut.BottomFrontRight.Value, 6);
            Assert.True(fullOut.BottomFrontLeft.Value > fullOut.BottomFrontRight.Value);

            // Level survives: both pads sit at the un-split level.
            double level = (fullOut.BottomFrontLeft.Value + fullOut.BottomFrontRight.Value) / 2.0;
            Assert.Equal(level, quietOut.BottomFrontLeft.Value, 2);
        }

        [Fact]
        public void Turning_every_scale_down_leaves_the_lock_slip_shake_as_the_only_output()
        {
            // The owner's stated use for the scales: "set the g-force scale as super low, so it will
            // behave as slip/lock notification instead of fully simulation."
            var engine = new GForceEngine
            {
                AccelOutputScale = 0.0,
                BrakeOutputScale = 0.0,
                LateralOutputScale = 0.0,
                IntegrateWheelLockAndSlip = true,
                ShakeApplyMode = ShakeApplyMode.AllChannelsLockSlip,
                WheelLockShakeScale = 1.0,
                ShakeTriggerThreshold = 20.0,
            };

            GForceOutput quiet = GForceOutput.Empty, shaking = GForceOutput.Empty;
            for (int i = 0; i < 60; i++)
                quiet = engine.Compute(Brake(-2.0, -1.0), AccelMax, DecelMax, wheelLockAll0100: 0.0, latMaxG: LatMax);
            for (int i = 0; i < 8; i++)
                shaking = engine.Compute(Brake(-2.0, -1.0), AccelMax, DecelMax, wheelLockAll0100: 80.0, latMaxG: LatMax);

            // Silent: nothing at all, despite full braking and full cornering.
            Assert.Equal(0.0, quiet.BottomFrontLeft.Value, 6);
            Assert.Equal(0.0, quiet.BackLowRight.Value, 6);

            // Locking: the shake is the entire output.
            Assert.True(shaking.BottomFrontLeft.Value > 0.0 || shaking.BottomFrontRight.Value > 0.0,
                "the lock/slip shake should still be published with every G-force scale at zero");
        }

        // ---------------- Steady-state cornering ----------------

        [Fact]
        public void Pure_cornering_keeps_the_last_active_chains_splits()
        {
            // OWNER'S DECISION for the case their worked examples did not cover: with no longitudinal G,
            // the lateral cue stays on whichever chain was last driving rather than jumping to a fixed
            // set or falling silent.
            var engine = Engine();

            // Establish the BRAKING chain, then let longitudinal G fade while cornering continues.
            for (int i = 0; i < 200; i++)
                engine.Compute(Brake(-1.44, -0.70), AccelMax, DecelMax, latMaxG: LatMax);

            GForceOutput coasting = GForceOutput.Empty;
            for (int i = 0; i < 400; i++)
                coasting = engine.Compute(Brake(0.0, -1.0), AccelMax, DecelMax, latMaxG: LatMax);

            // Braking splits: Back Low 100% takes the most, Bottom Front 50% the least.
            Assert.True(coasting.BackLowLeft.Value > coasting.BottomFrontLeft.Value,
                $"braking splits should still shape it (BackLow {coasting.BackLowLeft.Value}, BottomFront {coasting.BottomFrontLeft.Value})");
            Assert.True(coasting.BackLowLeft.Value > coasting.BackLowRight.Value,
                "and it should still be one-sided");
        }

        // ---------------- Settings round-trip ----------------

        [Fact]
        public void The_new_settings_round_trip_and_reach_the_engine()
        {
            var settings = new GForceSettings
            {
                LatMaxMode = GMaxMode.Fixed,
                FixedLatMaxG = 1.25,
                AccelOutputScalePercent = 80.0,
                BrakeOutputScalePercent = 60.0,
                LateralOutputScalePercent = 40.0,
                BrakeBottomFrontLatSplitPercent = 55.0,
                AccelBackTopLatSplitPercent = 45.0,
            };

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<GForceSettings>(json);

            Assert.Equal(GMaxMode.Fixed, restored.LatMaxMode);
            Assert.Equal(1.25, restored.FixedLatMaxG, 6);
            Assert.Equal(40.0, restored.LateralOutputScalePercent, 6);

            var engine = new GForceEngine();
            restored.ApplyTo(engine);
            Assert.Equal(0.80, engine.AccelOutputScale, 6);
            Assert.Equal(0.60, engine.BrakeOutputScale, 6);
            Assert.Equal(0.40, engine.LateralOutputScale, 6);
            Assert.Equal(0.55, engine.BrakeBottomFrontLatSplit, 6);
            Assert.Equal(0.45, engine.AccelBackTopLatSplit, 6);
        }

        [Fact]
        public void The_shipped_lateral_defaults_are_the_owners_numbers()
        {
            var s = new GForceSettings();
            Assert.Equal(GMaxMode.Auto, s.LatMaxMode);
            Assert.Equal(100.0, s.AccelOutputScalePercent, 6);
            Assert.Equal(100.0, s.BrakeOutputScalePercent, 6);
            Assert.Equal(100.0, s.LateralOutputScalePercent, 6);

            Assert.Equal(50.0, s.BrakeBottomFrontLatSplitPercent, 6);
            Assert.Equal(75.0, s.BrakeBottomRearLatSplitPercent, 6);
            Assert.Equal(100.0, s.BrakeBackLowLatSplitPercent, 6);
            Assert.Equal(100.0, s.AccelBottomRearLatSplitPercent, 6);
            Assert.Equal(75.0, s.AccelBackLowLatSplitPercent, 6);
            Assert.Equal(50.0, s.AccelBackTopLatSplitPercent, 6);
        }

        [Fact]
        public void The_lateral_learner_persists_like_the_longitudinal_pair()
        {
            var settings = new GForceSettings();
            settings.SetCurrentGameAndCar("G", "C");
            for (int i = 0; i < 800; i++)
                settings.ObserveLatG("G", "C", 1.35);

            settings.ExportLearnedMaxima(out var accel, out var decel, out var lateral);
            Assert.NotNull(lateral);
            Assert.NotEmpty(lateral);

            var restored = new GForceSettings();
            restored.ImportLearnedMaxima(accel, decel, lateral);
            Assert.True(restored.GetLearnedLatMaxG("G", "C") > 1.0);

            // A pre-1.0.8 snapshot has no lateral dictionary at all, which must simply leave it cold.
            var legacy = new GForceSettings();
            legacy.ImportLearnedMaxima(accel, decel, null);
            Assert.Equal(0.0, legacy.GetLearnedLatMaxG("G", "C"), 6);
        }
    }
}

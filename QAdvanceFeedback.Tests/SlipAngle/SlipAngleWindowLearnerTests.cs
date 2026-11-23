using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Slip Angle window learner (docs\slip-angle-design.md 9.5.4-6, 9.5a, 9.5b).</summary>
    public class SlipAngleWindowLearnerTests
    {
        private const double Dt = 1.0 / 60.0;
        private const double PeakG = 1.5;
        private const double Gravity = 9.80665;
        private const double SpeedMps = 30.0;
        private static readonly ImpactVerdict Allowed = new ImpactVerdict(true, false, false, 0.0, 0.0);

        // ---- curves --------------------------------------------------------------------------------

        /// <summary>Peaks at 8 deg (1.5 g), then falls 0.15 g/deg.</summary>
        private static double TrueCurve(double a) =>
            a <= 8.0 ? PeakG * Math.Sin(Math.PI / 2.0 * a / 8.0) : Math.Max(0.3, PeakG - 0.15 * (a - 8.0));

        /// <summary>A plateau at 1.5 g from 3 to 14 deg, so synthetic crossings can sit anywhere on it.</summary>
        private static double FlatCurve(double a) =>
            a < 3.0 ? PeakG * a / 3.0 : a <= 14.0 ? PeakG : Math.Max(0.3, PeakG - 0.1 * (a - 14.0));

        /// <summary>Still rising up to <paramref name="peakDeg"/>.</summary>
        private static Func<double, double> RisingCurve(double peakDeg) =>
            a => PeakG * Math.Sin(Math.PI / 2.0 * Math.Min(a, peakDeg) / peakDeg);

        /// <summary>A learned curve: <paramref name="perBin"/> exact samples in every bin centred below
        /// <paramref name="toDeg"/>.</summary>
        private static GripCurveBinsState CurveState(Func<double, double> curve, double toDeg = 15.0, int perBin = 40)
        {
            var bins = new GripCurveBins();
            for (int k = 0; k < perBin; k++)
                for (int i = 0; GripCurveBins.BinCenterDeg(i) < toDeg; i++)
                {
                    double a = (i + (k + 0.5) / perBin) * GripCurveBins.BinWidthDeg;
                    bins.Observe(a, curve(a));
                }
            return bins.Export();
        }

        private static SlipAngleWindowLearner WithCurve(Func<double, double> curve)
        {
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(new SlipAngleLearnerState { Bins = CurveState(curve) });
            return l;
        }

        // ---- drivers -------------------------------------------------------------------------------

        private static Action<double, double> Plain(SlipAngleWindowLearner l, bool learn = true) =>
            (a, g) => l.Observe(a, g, Allowed, learn, Dt);

        /// <summary>Frames whose verdict comes from a real <see cref="ImpactGuard"/>; the yaw rate is
        /// chosen so the yaw-consistency residual is exactly <paramref name="yawResidualG"/> (null: no yaw).</summary>
        private static Action<double, double> Guarded(SlipAngleWindowLearner l, ImpactGuard guard, double? yawResidualG = null) =>
            (a, g) =>
            {
                double? yaw = yawResidualG.HasValue ? (g - yawResidualG.Value) * Gravity / SpeedMps : (double?)null;
                double? dVlat = yawResidualG.HasValue ? 0.0 : (double?)null;
                l.Observe(a, g, guard.Observe(g, 0.0, 1.0, SpeedMps, yaw, dVlat, Dt), true, Dt);
            };

        /// <summary>Leaves the corner: 1.2 s (longer than the 1 s hold) at low alpha and G under 40% of peak.</summary>
        private static void Exit(Action<double, double> f)
        {
            for (int i = 0; i < 72; i++) f(1.0, 0.2);
        }

        /// <summary>Sweeps alpha through the curve's peak at 0.12 deg/frame (a real crossing), then exits.</summary>
        private static void CornerThroughPeak(Action<double, double> f, Func<double, double> curve)
        {
            for (int i = 0; i <= 33; i++) { double a = 6.0 + 0.12 * i; f(a, curve(a)); }
            Exit(f);
        }

        /// <summary>2 s of near-limit cornering, then <see cref="CornerThroughPeak"/>.</summary>
        private static void FullCorner(Action<double, double> f, Func<double, double> curve)
        {
            for (int i = 0; i < 120; i++) f(6.0, curve(6.0));
            CornerThroughPeak(f, curve);
        }

        /// <summary>Alpha rises 0.12 deg/frame; G holds at <paramref name="gHold"/> until alpha reaches
        /// <paramref name="target"/>, then falls 0.015 g/frame - a crossing whose onset snapshot is
        /// <paramref name="target"/>.</summary>
        private static void SyntheticCrossing(Action<double, double> f, double target, double gHold = 1.45)
        {
            for (int i = 0; i <= 20; i++)
            {
                double a = target - 1.2 + 0.12 * i;
                double g = i <= 10 ? gHold : gHold - 0.015 * (i - 10);
                f(a, g);
            }
            Exit(f);
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static string Snapshot(SlipAngleWindowLearner l) => JsonSerializer.Serialize(l.Export(), Json);

        private static SlipAngleWindowLearner ConfirmedAtEight()
        {
            var l = WithCurve(TrueCurve);
            for (int i = 0; i < 3; i++) FullCorner(Plain(l), TrueCurve);
            Assert.True(l.PeakConfirmed);
            return l;
        }

        // ---- crash / contact protection (9.5b) -------------------------------------------------------

        [Fact]
        public void Owner_crash_scenario_is_not_a_crossing()
        {
            // True curve peaks at 8 deg. At 4 deg the car is hit: lateral G jumps +3.5 g in one frame, then
            // alpha keeps rising to 9 deg while lateral G ramps back down over 1 s - a fake "crossing".
            var l = WithCurve(TrueCurve);
            var guard = new ImpactGuard(ImpactGuardParameters.SlipAngle);
            var f = Guarded(l, guard);
            for (int i = 0; i < 30; i++) f(4.0, TrueCurve(4.0));
            SlipAngleWindow before = l.Published;
            string stateBeforeHit = Snapshot(l);

            double hitG = TrueCurve(4.0) + 3.5;
            f(4.0, hitG);
            for (int i = 1; i <= 60; i++)
            {
                double t = i / 60.0;
                f(4.0 + 5.0 * t, hitG + (TrueCurve(9.0) - hitG) * t);
            }

            Assert.Equal(0, l.CrossingCount);
            Assert.Equal(before.Max, l.Published.Max);
            Assert.False(l.PeakConfirmed);
            // Nothing learned at all inside the exclusion window: no curve, no maturity, no crossing state.
            Assert.Equal(stateBeforeHit, Snapshot(l));
        }

        [Fact]
        public void Soft_contact_blocked_by_yaw_residual()
        {
            // Control: the same corner with a consistent yaw rate is a real crossing.
            var control = WithCurve(TrueCurve);
            CornerThroughPeak(Guarded(control, new ImpactGuard(ImpactGuardParameters.SlipAngle), 0.0), TrueCurve);
            Assert.Equal(1, control.CrossingCount);

            // Soft contact: no G jump at all, but lateral G is 0.5 g away from what V*r explains.
            var l = WithCurve(TrueCurve);
            string before = Snapshot(l);
            CornerThroughPeak(Guarded(l, new ImpactGuard(ImpactGuardParameters.SlipAngle), 0.5), TrueCurve);
            Assert.Equal(0, l.CrossingCount);
            Assert.Equal(before, Snapshot(l));
        }

        [Fact]
        public void Single_fake_crossing_cannot_lower_max()
        {
            var l = ConfirmedAtEight();
            double max = l.Published.Max;

            SyntheticCrossing(Plain(l), 5.0, gHold: 1.40);

            Assert.Equal(4, l.CrossingCount);          // it WAS a crossing...
            Assert.Equal(max, l.Published.Max);        // ...but one uncorroborated event moves nothing
            Assert.True(l.PeakConfirmed);
        }

        [Fact]
        public void Two_separate_low_crossings_can_lower_max()
        {
            var l = ConfirmedAtEight();
            double max = l.Published.Max;

            SyntheticCrossing(Plain(l), 5.0, gHold: 1.40);
            SyntheticCrossing(Plain(l), 5.0, gHold: 1.40);

            Assert.Equal(5, l.CrossingCount);
            Assert.True(l.Published.Max < max - 0.1, $"Max {l.Published.Max} vs {max}");
        }

        [Fact]
        public void Curve_plausibility_blocks_grip_above_the_curve()
        {
            // 1.7 g at 5 deg is far more than 115% of what this curve makes there (~1.25 g). The last few
            // frames of the fall do come back within 115% of the curve, but the trend the gate compares
            // still contains implausible frames, so it is never armed.
            var l = WithCurve(TrueCurve);
            SyntheticCrossing(Plain(l), 5.0, gHold: 1.70);
            Assert.Equal(0, l.CrossingCount);
        }

        [Fact]
        public void Cold_curve_has_no_candidates()
        {
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            SyntheticCrossing(Plain(l), 8.0);
            Assert.Equal(0, l.CrossingCount);
        }

        private static SlipAngleWindowLearner ConfirmedOnFlatAtEight()
        {
            var l = WithCurve(FlatCurve);
            for (int i = 0; i < 3; i++) SyntheticCrossing(Plain(l), 8.0);
            Assert.True(l.PeakConfirmed);
            Assert.InRange(l.Published.Max, 7.8, 8.2);
            return l;
        }

        [Theory]
        [InlineData(9.6)]
        [InlineData(10.0)]
        [InlineData(7.2)]
        public void Single_crossing_near_the_cluster_cannot_move_max(double at)
        {
            // Within the raise (30%) / lower (15%) band of the cluster at 8 deg - but the cluster sits on the
            // level, not on the new snapshot's side of it, so it cannot corroborate a move.
            var l = ConfirmedOnFlatAtEight();
            double max = l.Published.Max;

            SyntheticCrossing(Plain(l), at);

            Assert.Equal(4, l.CrossingCount);
            Assert.Equal(max, l.Published.Max);
        }

        [Fact]
        public void Two_separate_high_crossings_raise_max()
        {
            var l = ConfirmedOnFlatAtEight();
            double max = l.Published.Max;

            SyntheticCrossing(Plain(l), 10.0);
            SyntheticCrossing(Plain(l), 10.0);

            Assert.Equal(5, l.CrossingCount);
            Assert.True(l.Published.Max > max + 0.5, $"Max {l.Published.Max} vs {max}");
        }

        [Fact]
        public void Impact_during_hold_discards_the_event_and_its_teaching()
        {
            var l = ConfirmedOnFlatAtEight();
            var f = Plain(l);
            SyntheticCrossing(f, 10.0);
            Assert.Equal(4, l.CrossingCount);
            double max = l.Published.Max;

            // A second, corroborated crossing at 10 deg (it would raise Max - see the test above), hit well
            // inside its hold window, after it has started teaching.
            for (int i = 0; i <= 20; i++) f(8.8 + 0.12 * i, i <= 10 ? 1.45 : 1.45 - 0.015 * (i - 10));
            for (int i = 0; i < 15; i++) f(10.5, 1.3);
            l.Observe(10.5, 1.3, new ImpactVerdict(false, true, true, 1.5, 0.0), true, Dt);
            Exit(f);

            Assert.Equal(4, l.CrossingCount);
            Assert.Equal(max, l.Published.Max);
        }

        [Fact]
        public void Impact_spike_followed_by_a_crossing_shape_is_excluded()
        {
            // Discriminating twin of the owner's scenario: here ONLY the impact guard stands between the
            // spike and a crossing (the plateau makes the crossing plausible and the rise is fast enough).
            void Run(SlipAngleWindowLearner l, Func<double, ImpactVerdict> verdict)
            {
                void F(double a, double g) => l.Observe(a, g, verdict(g), true, Dt);
                for (int i = 0; i < 30; i++) F(4.0, FlatCurve(4.0));
                F(4.0, FlatCurve(4.0) + 3.5);
                SyntheticCrossing(F, 5.0);
            }

            var control = WithCurve(FlatCurve);
            Run(control, g => Allowed);
            Assert.Equal(1, control.CrossingCount);

            var l = WithCurve(FlatCurve);
            double max = l.Published.Max;
            var guard = new ImpactGuard(ImpactGuardParameters.SlipAngle);
            Run(l, g => guard.Observe(g, 0.0, 1.0, SpeedMps, null, null, Dt));
            Assert.Equal(0, l.CrossingCount);
            Assert.Equal(max, l.Published.Max);
        }

        [Fact]
        public void Frames_just_before_an_impact_never_reach_the_curve()
        {
            const int coldBin = 80;   // 20.0 - 20.25 deg: nothing learned there yet
            int Run(bool hit)
            {
                var l = WithCurve(TrueCurve);
                var f = Guarded(l, new ImpactGuard(ImpactGuardParameters.SlipAngle));
                for (int i = 0; i < 30; i++) f(6.0, TrueCurve(6.0));
                for (int i = 0; i < 5; i++) f(20.1, 1.0 + 0.1 * i);   // the contact builds...
                if (hit) f(20.1, 1.4 + 3.2);                           // ...and ends in a > 3 g jump
                // Push the delay line through (Export leaves pending frames out); excluded after a hit.
                for (int i = 0; i < 5; i++) f(6.0, TrueCurve(6.0));
                return l.CurveSampleCount(coldBin);
            }

            Assert.Equal(5, Run(hit: false));
            Assert.Equal(0, Run(hit: true));
        }

        [Fact]
        public void Confirmation_is_latched_against_shape_check_noise()
        {
            var l = ConfirmedAtEight();
            double max = l.Published.Max;
            double priorScale = l.Export().PriorScale;

            // Noisy data that makes the curve "still rising at its top" again (14 -> 18 deg, climbing).
            var f = Plain(l);
            var rng = new Random(7);
            for (int sweep = 0; sweep < 60; sweep++)
                for (int i = 0; i <= 80; i++)
                {
                    f(14.0 + 0.05 * i, 1.30 + 0.25 * i / 80.0 + 0.01 * (rng.NextDouble() - 0.5));
                    Assert.True(l.PeakConfirmed);
                    Assert.Equal(max, l.Published.Max);
                }

            Assert.True(l.CurveStillRisingAtTop, "the live shape check would have un-confirmed");
            Assert.Equal(priorScale, l.Export().PriorScale);
        }

        [Fact]
        public void Window_quality_rises_monotonically_without_steps()
        {
            var l = ConfirmedAtEight();
            SlipAngleLearnerState s = l.Export();
            s.BestCount = 0;          // no Best samples yet...
            s.WindowQuality = 0.0;    // ...so the quality shown at the save was 0
            s.BestSampleWaitSeconds = 0.0;
            l.Import(s);

            var f = Plain(l);
            double previous = l.WindowQuality;
            Assert.Equal(0.0, previous);
            for (int i = 0; i < 1200; i++)
            {
                f(6.0, TrueCurve(6.0));
                double q = l.WindowQuality;
                Assert.True(q >= previous - 1e-12, $"frame {i}: {q} < {previous}");
                Assert.True(q - previous <= 1.0 / SlipAngleWindowLearner.BestHistoryCapacity + 1e-9, $"frame {i}: step {q - previous}");
                previous = q;
            }
            Assert.True(previous > 0.9, previous.ToString());
        }

        [Fact]
        public void Unconfirmed_quality_is_capped_not_scaled()
        {
            SlipAngleLearnerState s = ConfirmedAtEight().Export();
            s.PeakConfirmed = false;
            s.LearnedMax = 0.0;   // nothing shown is saved (an older file): the quality is computed

            // Coverage 1 across the prior's [5, 15]; 4 of 16 identical Best samples -> raw 0.25, below the cap.
            s.BestCount = 4;
            s.BestMean = 8.0;
            s.BestSpread = 0.0;
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(s);
            Assert.False(l.PeakConfirmed);
            Assert.Equal(0.25, l.WindowQuality, 12);

            // A full stable history -> raw 1, capped at 0.6 (a min, not a 0.6 multiplier).
            s.BestCount = SlipAngleWindowLearner.BestHistoryCapacity;
            l.Import(s);
            Assert.Equal(0.6, l.WindowQuality, 12);
        }

        /// <summary>The frames of <see cref="SyntheticCrossing"/> up to the end of its fall, then
        /// <paramref name="holdFrames"/> more frames inside the hold window - no exit.</summary>
        private static void CrossingIntoHold(Action<double, double> f, double target, int holdFrames)
        {
            for (int i = 0; i <= 20; i++) f(target - 1.2 + 0.12 * i, i <= 10 ? 1.45 : 1.45 - 0.015 * (i - 10));
            for (int i = 0; i < holdFrames; i++) f(target + 0.5, 1.3);
        }

        private static readonly ImpactVerdict Impact = new ImpactVerdict(false, true, true, 1.5, 0.0);

        [Fact]
        public void Rolled_back_teaching_never_latches_confirmation()
        {
            // Restored with three consistent snapshots but a corrupt (sanitised away) Max level.
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(new SlipAngleLearnerState
            {
                Bins = CurveState(FlatCurve),
                CrossingSnapshots = new[] { 8.0, 8.0, 8.0 },
                Max = new CorroboratedLevelState { Level = double.NaN },
            });
            Assert.Equal(0.0, l.Export().Max.Level);

            // A corroborated crossing (no level yet: any earlier event counts) teaches inside its hold...
            var f = Plain(l);
            CrossingIntoHold(f, 8.0, 15);
            Assert.False(l.PeakConfirmed);
            // ...and is hit before the hold expires: its teaching is rolled back.
            l.Observe(8.5, 1.3, Impact, true, Dt);
            Exit(f);

            Assert.False(l.PeakConfirmed);
            Assert.True(l.Published.IsValid, l.Published.ToString());
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
            Assert.Equal(0.0, l.Export().Max.Level);
        }

        [Fact]
        public void Export_does_not_change_subsequent_behaviour()
        {
            void Drive(SlipAngleWindowLearner l, bool export)
            {
                Action<double, double> f = (a, g) =>
                {
                    l.Observe(a, g, Allowed, true, Dt);
                    if (export) l.Export();   // every frame: mid-hold, mid-delay-line, mid-teaching
                };
                FullCorner(f, TrueCurve);
                FullCorner(f, TrueCurve);
                FullCorner(f, TrueCurve);
                SyntheticCrossing(f, 5.0, gHold: 1.40);
                SyntheticCrossing(f, 5.0, gHold: 1.40);
            }

            var exported = WithCurve(TrueCurve);
            var untouched = WithCurve(TrueCurve);
            Drive(exported, export: true);
            Drive(untouched, export: false);

            Assert.True(untouched.PeakConfirmed);
            Assert.Equal(Snapshot(untouched), Snapshot(exported));
            Assert.Equal(untouched.Published, exported.Published);
            Assert.Equal(untouched.WindowQuality, exported.WindowQuality);
        }

        [Fact]
        public void Export_leaves_out_teaching_still_inside_its_hold()
        {
            var l = ConfirmedOnFlatAtEight();
            var f = Plain(l);
            SyntheticCrossing(f, 10.0);
            double max = l.Published.Max;

            CrossingIntoHold(f, 10.0, 15);   // corroborated by the first 10 deg event: teaching live

            Assert.True(l.Published.Max > max, "the live level is being taught");
            Assert.Equal(max, l.Export().Max.Level);
        }

        [Fact]
        public void Cluster_scatter_cannot_corroborate_a_fake_crossing()
        {
            var l = WithCurve(FlatCurve);
            var f = Plain(l);
            SyntheticCrossing(f, 7.5);
            SyntheticCrossing(f, 8.0);
            SyntheticCrossing(f, 8.5);
            Assert.True(l.PeakConfirmed);
            double max = l.Published.Max;
            Assert.InRange(max, 7.8, 8.2);

            // 7.5 is below the level and within 15% of 7.0 - but it belongs to the cluster that SET the level.
            SyntheticCrossing(f, 7.0);
            Assert.Equal(max, l.Published.Max);

            // A second, separate new event at 7.0 is real corroboration.
            SyntheticCrossing(f, 7.0);
            Assert.True(l.Published.Max < max - 0.1, $"Max {l.Published.Max} vs {max}");
        }

        // ---- the prior (9.5.5) -----------------------------------------------------------------------

        [Fact]
        public void Cautious_driver_never_lowers_prior()
        {
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            var f = Plain(l);
            var curve = RisingCurve(8.0);
            for (int sweep = 0; sweep < 100; sweep++)
                for (int i = 0; i <= 100; i++) { double a = 0.05 * i; f(a, curve(a)); }

            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
            Assert.False(l.PeakConfirmed);
            Assert.True(l.WindowQuality <= 0.6);
            Assert.True(l.Maturity > 0.0, "a driver who stays under the limit still matures");
        }

        [Fact]
        public void Still_rising_beyond_prior_best_raises_window()
        {
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            var f = Plain(l);
            var curve = RisingCurve(12.0);
            for (int sweep = 0; sweep < 100; sweep++)
                for (int i = 0; i <= 195; i++) { double a = 0.05 * i; f(a, curve(a)); }

            SlipAngleWindow w = l.Published;
            Assert.False(l.PeakConfirmed);
            Assert.True(w.Best >= 9.5, w.ToString());
            Assert.Equal(10.0 / 8.0, w.Max / w.Best, 9);
            Assert.Equal(5.0 / 8.0, w.Min / w.Best, 9);
            Assert.True(l.WindowQuality <= 0.6);

            // Never lowered: later cautious driving keeps the raised window.
            var cautious = RisingCurve(8.0);
            for (int sweep = 0; sweep < 50; sweep++)
                for (int i = 0; i <= 100; i++) { double a = 0.05 * i; f(a, cautious(a)); }
            Assert.Equal(w, l.Published);
        }

        // ---- confirmation (9.5.5) --------------------------------------------------------------------

        [Fact]
        public void Three_consistent_crossings_confirm()
        {
            var l = WithCurve(TrueCurve);
            var f = Plain(l);
            CornerThroughPeak(f, TrueCurve);
            CornerThroughPeak(f, TrueCurve);
            Assert.Equal(2, l.CrossingCount);
            Assert.False(l.PeakConfirmed);
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
            Assert.True(l.WindowQuality <= 0.6);

            CornerThroughPeak(f, TrueCurve);
            Assert.Equal(3, l.CrossingCount);
            Assert.True(l.PeakConfirmed);
            SlipAngleWindow w = l.Published;
            Assert.True(w.IsValid, w.ToString());
            Assert.InRange(w.Max, 7.5, 8.5);
            Assert.True(w.Best < w.Max && w.Min < w.Best, w.ToString());
            // No longer capped once confirmed; it reaches full quality as Best proves stable (16 s of samples).
            for (int i = 0; i < 1200; i++) f(6.0, TrueCurve(6.0));
            Assert.True(l.WindowQuality > 0.9, l.WindowQuality.ToString());
        }

        [Fact]
        public void Inconsistent_crossings_do_not_confirm()
        {
            var l = WithCurve(FlatCurve);
            var f = Plain(l);
            SyntheticCrossing(f, 8.0);
            SyntheticCrossing(f, 12.0);
            SyntheticCrossing(f, 5.0);

            Assert.Equal(3, l.CrossingCount);
            Assert.False(l.PeakConfirmed);
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
        }

        [Fact]
        public void Onset_snapshot_taught_not_runaway()
        {
            var l = WithCurve(TrueCurve);
            var f = Plain(l);
            for (int corner = 0; corner < 3; corner++)
            {
                for (int i = 0; i <= 22; i++) { double a = 6.0 + 0.12 * i; f(a, TrueCurve(a)); }
                // Alpha runs away from ~8.6 to 20 deg inside the hold window while G keeps falling.
                for (int i = 1; i <= 46; i++) { double a = 8.64 + 0.25 * i; f(a, TrueCurve(a)); }
                Exit(f);
            }

            Assert.Equal(3, l.CrossingCount);
            Assert.True(l.PeakConfirmed);
            Assert.InRange(l.Published.Max, 7.5, 8.5);
        }

        // ---- maturity (9.5.6) ------------------------------------------------------------------------

        [Fact]
        public void Matures_in_three_corners()
        {
            var l = WithCurve(TrueCurve);
            var f = Plain(l);
            void Corner()
            {
                for (int i = 0; i < 120; i++) f(5.5, 0.9 * PeakG);
                for (int i = 0; i < 30; i++) f(1.0, 0.2);
            }

            Corner();
            Assert.Equal(1, l.Corners);
            Assert.True(l.Maturity > 0.0 && l.Maturity <= 0.34, l.Maturity.ToString());

            Corner();
            Corner();
            Assert.Equal(3, l.Corners);
            Assert.True(l.Observations >= 50, l.Observations.ToString());
            Assert.Equal(1.0, l.Maturity);
        }

        [Fact]
        public void Observations_are_counted_at_most_ten_per_second()
        {
            var l = WithCurve(TrueCurve);
            var f = Plain(l);
            for (int i = 0; i < 600; i++) f(5.5, 0.9 * PeakG);   // 10 s at 60 Hz
            Assert.InRange(l.Observations, 99, 101);
            Assert.Equal(0, l.Corners);                          // one long sweeper is not a corner
        }

        [Fact]
        public void Learning_continues_after_maturity()
        {
            var l = ConfirmedAtEight();
            Assert.Equal(1.0, l.Maturity);
            double best = l.Published.Best;
            long observations = l.Observations;

            // The car changes: more grip, peaking earlier (6.5 deg).
            Func<double, double> shifted = a => a <= 6.5 ? 1.7 * Math.Sin(Math.PI / 2.0 * a / 6.5) : 1.7 - 0.2 * (a - 6.5);
            var f = Plain(l);
            for (int sweep = 0; sweep < 300; sweep++)
                for (int i = 0; i <= 150; i++) { double a = 0.05 * i; f(a, shifted(a)); }

            Assert.True(l.PeakConfirmed);
            Assert.True(Math.Abs(l.Published.Best - best) > 0.3, $"Best {l.Published.Best} vs {best}");
            Assert.True(l.Observations > observations);
        }

        [Fact]
        public void Landing_wobble_frames_never_teach()
        {
            var l = ConfirmedAtEight();
            string before = Snapshot(l);
            SlipAngleWindow published = l.Published;

            var wobble = Plain(l, learn: false);
            FullCorner(wobble, TrueCurve);
            SyntheticCrossing(wobble, 5.0, gHold: 1.40);

            Assert.Equal(before, Snapshot(l));
            Assert.Equal(published, l.Published);
            Assert.Equal(3, l.CrossingCount);
        }

        [Fact]
        public void Retro_invalidate_cancels_a_crossing_in_its_first_frames()
        {
            var l = WithCurve(TrueCurve);
            var f = Plain(l);
            // Up to the detection frame of the corner (alpha ~8.4), then an impact two frames later.
            for (int i = 0; i <= 22; i++) { double a = 6.0 + 0.12 * i; f(a, TrueCurve(a)); }
            var impact = new ImpactVerdict(false, true, true, 1.5, 0.0);
            l.Observe(8.76, TrueCurve(8.76), impact, true, Dt);
            Exit(f);
            Assert.Equal(0, l.CrossingCount);
        }

        // ---- persistence ----------------------------------------------------------------------------

        [Fact]
        public void Export_import_round_trips_exactly()
        {
            var l = ConfirmedAtEight();
            SyntheticCrossing(Plain(l), 5.0, gHold: 1.40);
            for (int i = 0; i < 10; i++) Plain(l)(6.0, 1.3);   // mid-excursion, observation wait running

            SlipAngleLearnerState state = l.Export();
            var restored = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            restored.Import(state);

            Assert.Equal(JsonSerializer.Serialize(state, Json), Snapshot(restored));
            Assert.Equal(l.Published, restored.Published);
            Assert.Equal(l.PeakConfirmed, restored.PeakConfirmed);
            Assert.Equal(l.Maturity, restored.Maturity);
            Assert.Equal(l.WindowQuality, restored.WindowQuality);
            Assert.Equal(l.CrossingCount, restored.CrossingCount);
            Assert.Equal(l.Observations, restored.Observations);
            Assert.Equal(l.Corners, restored.Corners);
        }

        [Fact]
        public void A_load_then_save_with_no_driving_writes_back_exactly_what_it_read()
        {
            // Repeated restarts must never drift the rebuilt curve or the rebuilt rings.
            var l = ConfirmedAtEight();
            SyntheticCrossing(Plain(l), 5.0, gHold: 1.40);
            string first = Snapshot(l);
            var a = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            a.Import(l.Export());
            var b = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            b.Import(a.Export());

            Assert.Equal(first, Snapshot(a));
            Assert.Equal(first, Snapshot(b));
        }

        [Fact]
        public void The_save_holds_single_values_only()
        {
            var l = ConfirmedAtEight();
            SyntheticCrossing(Plain(l), 5.0, gHold: 1.40);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(l.Export());

            Assert.DoesNotContain("[", json);
            Assert.True(json.Length < 3000, json.Length + " chars");
        }

        [Fact]
        public void An_older_file_with_arrays_continues_identically()
        {
            // The arrays of an earlier version still load exactly (Bins, CrossingSnapshots, BestHistory).
            var original = WithCurve(TrueCurve);
            FullCorner(Plain(original), TrueCurve);
            var restored = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            restored.Import(new SlipAngleLearnerState { Bins = CurveState(TrueCurve) });
            FullCorner(Plain(restored), TrueCurve);

            Assert.Equal(Snapshot(original), Snapshot(restored));
        }

        [Fact]
        public void Restored_learner_continues_close_to_an_uninterrupted_one()
        {
            var original = WithCurve(TrueCurve);
            FullCorner(Plain(original), TrueCurve);
            FullCorner(Plain(original), TrueCurve);

            var restored = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            restored.Import(original.Export());
            Assert.Equal(original.Published, restored.Published);   // exact at the restart
            Assert.Equal(original.WindowQuality, restored.WindowQuality);
            Assert.Equal(original.MaxMaturity, restored.MaxMaturity);

            foreach (var l in new[] { original, restored })
            {
                // The gate's trend and the retro delay line are transient (not persisted): one
                // retro-invalidating frame clears both on both, after which they must evolve bit-identically.
                l.Observe(1.0, 0.2, new ImpactVerdict(false, true, true, 0.0, 0.0), true, Dt);
                var f = Plain(l);
                FullCorner(f, TrueCurve);
                SyntheticCrossing(f, 5.0, gHold: 1.40);
                SyntheticCrossing(f, 5.0, gHold: 1.40);
                for (int i = 0; i <= 150; i++) { double a = 0.05 * i; f(a, TrueCurve(a)); }
            }

            // The rebuilt curve is close to, not identical with, the original (docs\parameters-file-format-report.md).
            Assert.True(original.PeakConfirmed);
            Assert.Equal(original.PeakConfirmed, restored.PeakConfirmed);
            Assert.Equal(original.CrossingCount, restored.CrossingCount);
            Assert.InRange(restored.Published.Max, original.Published.Max - 0.25, original.Published.Max + 0.25);
            Assert.InRange(restored.Published.Best, original.Published.Best - 0.25, original.Published.Best + 0.25);
            Assert.InRange(restored.Published.Min, original.Published.Min - 0.25, original.Published.Min + 0.25);
        }

        [Fact]
        public void Import_sanitizes_and_saturates()
        {
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(new SlipAngleLearnerState
            {
                Bins = CurveState(FlatCurve),
                CrossingCount = int.MaxValue,
                Observations = long.MaxValue,
                Corners = -5,
                PriorScale = double.NaN,
                ObservationWaitSeconds = double.PositiveInfinity,
                CrossingSnapshots = new[] { double.NaN, -1.0, 8.0, 99.0 },
                Max = new CorroboratedLevelState { Level = double.NaN, RaiseHits = -3 },
            });

            Assert.Equal(SlipAngleWindowLearner.CounterSaturationCap, l.CrossingCount);
            Assert.Equal(SlipAngleWindowLearner.CounterSaturationCap, l.Observations);
            Assert.Equal(0, l.Corners);
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
            SlipAngleLearnerState s = l.Export();
            Assert.Equal(1, s.SnapshotCount);
            Assert.Equal(8.0, s.OlderSnapshotLow);
            Assert.Equal(8.0, s.OlderSnapshotHigh);
            Assert.Equal(0.0, s.Max.Level);
            Assert.Equal(0, s.Max.RaiseHits);
            Assert.Equal(0.0, s.ObservationWaitSeconds);
            Assert.Equal(1.0, s.PriorScale);

            // Saturated counters keep working without overflow.
            var f = Plain(l);
            for (int i = 0; i < 5; i++) SyntheticCrossing(f, 8.0);
            Assert.Equal(SlipAngleWindowLearner.CounterSaturationCap, l.CrossingCount);

            l.Import(null);
            Assert.Equal(0, l.CrossingCount);
            Assert.Equal(0L, l.Observations);
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
        }

        [Fact]
        public void Constructor_rejects_an_invalid_prior()
        {
            Assert.Throws<ArgumentException>(() => new SlipAngleWindowLearner(new SlipAngleWindow(5, 4, 3)));
        }

        // ---- maturity indicator (docs\learning-maturity-indicator-report.md) -----------------------

        [Fact]
        public void Max_maturity_counts_consistent_crossings_and_is_full_once_confirmed()
        {
            var l = WithCurve(TrueCurve);
            Assert.Equal(0.0, l.MaxMaturity);

            FullCorner(Plain(l), TrueCurve);
            Assert.Equal(1.0 / 3.0, l.MaxMaturity, 6);
            FullCorner(Plain(l), TrueCurve);
            Assert.Equal(2.0 / 3.0, l.MaxMaturity, 6);
            FullCorner(Plain(l), TrueCurve);

            Assert.True(l.PeakConfirmed);
            Assert.Equal(1.0, l.MaxMaturity);
        }

        [Fact]
        public void Max_maturity_survives_a_restart()
        {
            SlipAngleWindowLearner confirmed = ConfirmedAtEight();
            var restarted = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            restarted.Import(confirmed.Export());

            Assert.Equal(1.0, restarted.MaxMaturity);
            Assert.Equal(confirmed.ConsistentCrossings, restarted.ConsistentCrossings);
        }

        [Fact]
        public void Before_confirmation_keyed_best_and_min_report_the_same_progress_as_max()
        {
            var l = WithCurve(TrueCurve);
            FullCorner(Plain(l), TrueCurve);
            Assert.False(l.PeakConfirmed);
            var keyed = new KeyedSlipAngleLearner();
            keyed.Import(new Dictionary<string, SlipAngleKeyState>
            {
                [KeyedSlipAngleLearner.MakeKey("g", "c", "s", SlipAngleTarget.All, SurfaceBucket.Sealed)] = new SlipAngleKeyState { Learner = l.Export() },
            });

            Assert.True(keyed.TryWindowMaturity("g", "c", "s", SlipAngleTarget.All, SurfaceBucket.Sealed, out double max, out double curve));
            Assert.Equal(1.0 / 3.0, max, 6);
            Assert.Equal(max, curve);
        }

        [Fact]
        public void Keyed_maturity_keeps_best_and_min_at_or_below_max()
        {
            var keyed = new KeyedSlipAngleLearner();
            Assert.False(keyed.TryWindowMaturity("g", "c", "s", SlipAngleTarget.All, SurfaceBucket.Sealed, out _, out _));

            keyed.Import(new Dictionary<string, SlipAngleKeyState>
            {
                [KeyedSlipAngleLearner.MakeKey("g", "c", "s", SlipAngleTarget.All, SurfaceBucket.Sealed)] =
                    new SlipAngleKeyState { Learner = ConfirmedAtEight().Export() },
            });
            Assert.True(keyed.TryWindowMaturity("g", "c", "s", SlipAngleTarget.All, SurfaceBucket.Sealed, out double max, out double curve));
            Assert.Equal(1.0, max);
            Assert.InRange(curve, 0.0, max);
        }
    }
}

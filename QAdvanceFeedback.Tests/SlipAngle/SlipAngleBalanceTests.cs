using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// THE FRONT / REAR BALANCE k (owner, 2026-11-22): the median rear / front axle angle near the limit, pulled toward 1
    /// until enough evidence; never taught by contact, a slide, a refused frame, an implausible G or a spin; never used
    /// by a source without both axles; saved with the key so a restart carries on.
    /// </summary>
    public class SlipAngleBalanceTests
    {
        private const double Dt = 1.0 / 60.0;
        private const double Cap = 20.0;
        private const string Game = "G", Car = "C", Src = "S";
        private static readonly ImpactVerdict Allowed = new ImpactVerdict(true, false, false, 0.0, 0.0);
        private static readonly ImpactVerdict Contact = new ImpactVerdict(false, true, false, 0.5, 0.0);
        private static readonly SurfaceBlend Sealed = SurfaceBlend.Only(SurfaceBucket.Sealed);

        private static void Feed(SlipAngleBalanceEstimator e, double front, double rear, double g, int frames, bool ok = true)
        {
            for (int i = 0; i < frames; i++) e.Observe(front, rear, g, Cap, ok);
        }

        /// <summary>Matures the G reference at 3 g with every frame near the limit.</summary>
        private static SlipAngleBalanceEstimator Balanced(double front, double rear, int frames)
        {
            var e = new SlipAngleBalanceEstimator();
            Feed(e, front, rear, 3.0, frames);
            return e;
        }

        [Fact]
        public void K_is_the_median_rear_to_front_ratio_near_the_limit()
        {
            Assert.Equal(1.0, new SlipAngleBalanceEstimator().K(0.3, 2.0));
            Assert.Equal(0.5, Balanced(8.0, 4.0, 600).K(0.3, 2.0), 3);
            Assert.Equal(1.5, Balanced(4.0, 6.0, 600).K(0.3, 2.0), 3);
        }

        [Fact]
        public void Early_evidence_is_pulled_toward_one_until_enough_frames()
        {
            // The G reference needs MinGripSamples frames first; after it, n near-limit frames give exp(n/120 x ln 0.5).
            var e = Balanced(8.0, 4.0, SlipAngleBalanceEstimator.MinGripSamples);
            long n = e.NearLimitFrames;
            Feed(e, 8.0, 4.0, 3.0, 60 - (int)n);
            Assert.Equal(60, e.NearLimitFrames);
            Assert.Equal(Math.Exp(0.5 * Math.Log(0.5)), e.K(0.3, 2.0), 3);
            Feed(e, 8.0, 4.0, 3.0, 200);
            Assert.Equal(0.5, e.K(0.3, 2.0), 3);
        }

        [Fact]
        public void Moderate_cornering_below_the_near_limit_share_does_not_count()
        {
            var e = Balanced(8.0, 4.0, 600);
            long before = e.NearLimitFrames;
            Feed(e, 2.0, 2.0, 3.0 * 0.6, 600);   // 60% of the grip: both axles read alike, not the limit balance
            Assert.Equal(before, e.NearLimitFrames);
            Assert.Equal(0.5, e.K(0.3, 2.0), 3);
        }

        [Fact]
        public void Refused_frames_implausible_g_spins_and_a_near_zero_front_never_teach()
        {
            var e = Balanced(8.0, 4.0, 600);
            long before = e.NearLimitFrames;
            Feed(e, 4.0, 8.0, 3.0, 300, ok: false);          // the caller refused it (gate, contact, slide)
            Feed(e, 4.0, 8.0, 9.0, 300);                     // a 9 g spike is contact, not grip
            Feed(e, 4.0, 25.0, 3.0, 300);                    // an axle beyond the source's cap is a spin
            Feed(e, 0.1, 3.0, 3.0, 300);                     // a near-zero front makes the ratio meaningless
            Feed(e, double.NaN, 4.0, 3.0, 300);              // an unavailable axle
            Assert.Equal(before, e.NearLimitFrames);
            Assert.Equal(0.5, e.K(0.3, 2.0), 3);
        }

        [Fact]
        public void The_keyed_learner_refuses_contact_a_slide_and_a_refused_frame()
        {
            var k = new KeyedSlipAngleLearner();
            var sliding = new SlipAngleEarlyContext(3.0, 3.0, 2.0, true, false, true, 8.0, false);
            var grip = new SlipAngleEarlyContext(3.0, 3.0, 2.0, true, false, false, 8.0, false);
            for (int i = 0; i < 600; i++)
            {
                k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Contact, true, Dt, grip);       // contact (light or heavy)
                k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, true, Dt, sliding);    // a drift / slide
                k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, false, Dt, grip);      // gate: pit, replay, airborne...
            }
            Assert.Equal(0, k.BalanceFrames(Game, Car, Src, SurfaceBucket.Sealed));
            Assert.Equal(1.0, k.K(Game, Car, Src, Sealed));

            for (int i = 0; i < 600; i++) k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, true, Dt, grip);
            Assert.True(k.BalanceFrames(Game, Car, Src, SurfaceBucket.Sealed) > 0);
            Assert.Equal(0.5, k.K(Game, Car, Src, Sealed), 2);
        }

        /// <summary>A source without both axles (the body-angle fallback, a Custom source with one axle) never feeds k,
        /// so its k stays 1 and its output is untouched.</summary>
        [Fact]
        public void A_source_without_both_axles_is_never_affected_by_k()
        {
            var k = new KeyedSlipAngleLearner();
            for (int i = 0; i < 900; i++) k.Observe(Game, Car, Src, Sealed, double.NaN, 4.0, 4.0, 3.0, Allowed, true, Dt);
            for (int i = 0; i < 900; i++) k.Observe(Game, Car, Src, Sealed, 8.0, double.NaN, 8.0, 3.0, Allowed, true, Dt);
            Assert.Equal(1.0, k.K(Game, Car, Src, Sealed));
            Assert.Equal(0, k.BalanceFrames(Game, Car, Src, SurfaceBucket.Sealed));
            foreach (SlipAngleKeyState s in k.Export().Values) Assert.Null(s.Balance);
        }

        /// <summary>Per surface, never borrowed (owner): a new surface - or another car - starts again from 1.</summary>
        [Fact]
        public void Another_surface_or_car_starts_from_one()
        {
            var k = new KeyedSlipAngleLearner();
            for (int i = 0; i < 900; i++) k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, true, Dt);
            Assert.Equal(0.5, k.K(Game, Car, Src, Sealed), 2);
            Assert.Equal(0, k.BalanceFrames(Game, Car, Src, SurfaceBucket.Loose));
            Assert.Equal(1.0, k.K(Game, "OtherCar", Src, Sealed));
        }

        /// <summary>RESTART = CONTINUE (owner): the balance evidence is saved with the bucket's All key as single values,
        /// restored exactly, and keeps learning; an older file without it starts k from 1.</summary>
        [Fact]
        public void The_balance_survives_a_restart_and_keeps_learning()
        {
            var k = new KeyedSlipAngleLearner();
            for (int i = 0; i < 150; i++) k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, true, Dt);   // not fully trusted yet
            // Frames that teach nothing let the live (slewed) k settle on its target; a restored learner starts there.
            for (int i = 0; i < 300; i++) k.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, false, Dt);
            double before = k.K(Game, Car, Src, Sealed);
            Assert.True(before > 0.5 + 0.05 && before < 0.95, "fixture: half-way, not done - " + before);

            Dictionary<string, SlipAngleKeyState> saved = k.Export();
            string json = JsonConvert.SerializeObject(saved);
            Assert.DoesNotContain("\"Balance\":[", json);
            SlipAngleKeyState all = saved[KeyedSlipAngleLearner.MakeKey(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Sealed)];
            Assert.NotNull(all.Balance);
            Assert.Null(saved[KeyedSlipAngleLearner.MakeKey(Game, Car, Src, SlipAngleTarget.Front, SurfaceBucket.Sealed)].Balance);
            string balanceJson = JsonConvert.SerializeObject(all.Balance);
            Assert.DoesNotContain("[", balanceJson);   // single values only

            var restarted = new KeyedSlipAngleLearner();
            restarted.Import(JsonConvert.DeserializeObject<Dictionary<string, SlipAngleKeyState>>(json));
            Assert.Equal(before, restarted.K(Game, Car, Src, Sealed), 3);

            for (int i = 0; i < 600; i++) restarted.Observe(Game, Car, Src, Sealed, 8.0, 4.0, 8.0, 3.0, Allowed, true, Dt);
            Assert.Equal(0.5, restarted.K(Game, Car, Src, Sealed), 2);

            foreach (SlipAngleKeyState s in saved.Values) s.Balance = null;   // an older file
            var older = new KeyedSlipAngleLearner();
            older.Import(saved);
            Assert.Equal(1.0, older.K(Game, Car, Src, Sealed));
        }
    }
}

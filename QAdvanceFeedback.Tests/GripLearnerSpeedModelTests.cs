using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// SPEED-DEPENDENT GRIP MODEL (speed-aware-grip-report.md) - THE BUG: <see cref="GripLearner.LearnedPeakG"/>
    /// was a single scalar mixing every speed a car has ever braked/accelerated at, so an aero car's
    /// genuine low-speed lockup (physically at ITS OWN, much lower, low-speed limit) could never cross
    /// the physical-limit ratio threshold, because the learned reference was dominated by high-speed
    /// braking evidence. These tests cover the fix: <c>peak_decel(v) = g_mech + k * v^2</c>.
    /// </summary>
    public class GripLearnerSpeedModelTests
    {
        [Fact]
        public void Speed_unaware_callers_are_bit_identical_to_before_this_feature()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 300; i++) learner.Observe(3.0 + 0.1 * (i % 7)); // never passes a speed

            // k must stay exactly 0 forever - nothing ever taught the speed dimension.
            Assert.Equal(0.0, learner.K, 12);
            Assert.Equal(learner.PublishedPeakG, learner.GMech, 12);

            // SpeedAwarePeakG must equal PublishedPeakG for ANY speed, and Ratio(with speed) must equal
            // Ratio(without speed) exactly, at several different speeds.
            foreach (double speedKmh in new[] { 0.0, 60.0, 150.0, 300.0 })
            {
                Assert.Equal(learner.PublishedPeakG, learner.SpeedAwarePeakG(speedKmh), 12);
                Assert.Equal(learner.Ratio(2.5), learner.Ratio(2.5, speedKmh: speedKmh), 12);
                Assert.Equal(learner.Ratio(2.5, applyColdStartCeiling: false), learner.Ratio(2.5, applyColdStartCeiling: false, speedKmh: speedKmh), 12);
            }
        }

        [Fact]
        public void Narrow_speed_band_falls_back_to_k_zero_rather_than_extrapolating()
        {
            var learner = new GripLearner();
            // Every observation at essentially the same speed (a single, repeated corner) - no genuine
            // speed spread to fit an aero curve from.
            for (int i = 0; i < 300; i++) learner.Observe(3.0, 120.0 + (i % 3));

            Assert.Equal(0.0, learner.K, 9);
            Assert.Equal(learner.PublishedPeakG, learner.SpeedAwarePeakG(120.0), 6);
        }

        [Fact]
        public void Wide_speed_spread_with_a_genuine_aero_shape_fits_a_positive_k_and_fixes_the_low_speed_dead_zone()
        {
            var learner = new GripLearner();
            // Simulate an aero car: peak_decel(v) = 1.5 + 0.0006 * v_mps^2, sampled across a wide speed
            // range, mirroring the real captured-log shape (near-zero "at limit" hit-rate at low speed,
            // high hit-rate at high speed) - see the report.
            var rnd = new System.Random(42);
            foreach (double speedKmh in new[] { 20.0, 70.0, 120.0, 170.0, 220.0, 280.0 })
            {
                double vMps = speedKmh / 3.6;
                double truePeak = 1.5 + 0.0006 * vMps * vMps;
                for (int i = 0; i < 60; i++)
                {
                    double noisy = truePeak * (0.97 + 0.06 * rnd.NextDouble());
                    learner.Observe(noisy, speedKmh);
                }
            }

            Assert.True(learner.K > 0.0, $"expected a positive fitted aero term, got {learner.K}");

            // THE FIX ITSELF: a genuine low-speed lockup (achieved g close to the car's OWN low-speed
            // limit, ~1.5g) must now read close to "at the limit" when speed is supplied - the exact
            // scenario that was silently impossible before this task (a flat peak learned mostly from
            // high-speed evidence would have made this ratio read far below 1.0).
            double lowSpeedRatio = learner.Ratio(1.45, applyColdStartCeiling: false, speedKmh: 20.0);
            Assert.True(lowSpeedRatio > 0.8, $"a genuine low-speed lockup should read near its own limit, got ratio={lowSpeedRatio}");

            // And the SAME achieved g at high speed must read as comfortably UNDER the (much higher)
            // high-speed limit - proving the reference is now genuinely speed-appropriate, not just raised
            // globally.
            double highSpeedRatioSameG = learner.Ratio(1.45, applyColdStartCeiling: false, speedKmh: 280.0);
            Assert.True(highSpeedRatioSameG < lowSpeedRatio,
                $"the same 1.45g reading should look far less severe at high speed than at low speed, got low={lowSpeedRatio} high={highSpeedRatioSameG}");
        }

        [Fact]
        public void A_falling_with_speed_shape_never_fits_a_negative_k()
        {
            var learner = new GripLearner();
            // Mirrors the REAL Slip-channel shape measured on the captured logs: achieved g is HIGHEST at
            // low speed (torque/traction-limited launch) and falls at high speed (power/drag-limited, not
            // grip-limited) - the opposite sign a naive regression would otherwise fit.
            foreach (double speedKmh in new[] { 20.0, 70.0, 120.0, 170.0, 220.0, 280.0 })
            {
                double truePeak = 3.0 - 0.004 * speedKmh;
                for (int i = 0; i < 60; i++) learner.Observe(System.Math.Max(0.2, truePeak), speedKmh);
            }

            Assert.Equal(0.0, learner.K, 9);
            Assert.True(learner.GMech > 0.1, $"g_mech must not collapse toward zero, got {learner.GMech}");

            // THE UNIVERSAL "k=0 implies bit-identical" INVARIANT: a negative-k fit rejected back to zero
            // must land on EXACTLY the same gMech as the fully-degenerate (never-taught-speed) path - a
            // single flat fallback value, not a second, differently-computed one.
            Assert.Equal(learner.LearnedPeakG, learner.GMech, 12);
        }

        [Fact]
        public void Cold_start_with_no_persisted_value_has_zero_k_and_gmech_equal_to_learned_peak()
        {
            var learner = new GripLearner();
            learner.Observe(2.0, 100.0);

            Assert.Equal(learner.LearnedPeakG, learner.GMech, 9);
            Assert.Equal(0.0, learner.K, 9);
        }

        [Fact]
        public void Load_from_a_pre_speed_model_document_falls_back_to_the_flat_peak_with_k_zero()
        {
            var learner = new GripLearner();
            // The 2-arg overload is exactly what a document written before this feature existed produces
            // via GripLearnerState's own default (GMech=0.0, K=0.0) - Load must reproduce today's flat
            // behaviour exactly, not a broken/zeroed one.
            learner.Load(2.4, 500);

            Assert.Equal(2.4, learner.PublishedGMech, 12);
            Assert.Equal(0.0, learner.PublishedK, 12);
            Assert.Equal(2.4, learner.SpeedAwarePeakG(999.0), 12); // any speed - k=0 means constant
        }

        [Fact]
        public void Load_round_trips_a_persisted_speed_model()
        {
            var learner = new GripLearner();
            learner.Load(2.4, 500, gMech: 1.6, k: 0.0005);

            Assert.Equal(1.6, learner.PublishedGMech, 12);
            Assert.Equal(0.0005, learner.PublishedK, 12);

            // Zero new observations this session - identical to the persisted values, exactly like
            // PublishedPeakG's own "restart with no new driving reproduces the previous mapping" guarantee.
            double vMps = 250.0 / 3.6;
            Assert.Equal(1.6 + 0.0005 * vMps * vMps, learner.SpeedAwarePeakG(250.0), 9);
        }

        [Fact]
        public void A_negative_persisted_k_is_rejected_back_to_zero()
        {
            var learner = new GripLearner();
            learner.Load(2.4, 500, gMech: 1.6, k: -0.001);

            Assert.Equal(0.0, learner.PublishedK, 12);
        }

        [Fact]
        public void KeyedGripLearner_persists_and_restores_gmech_and_k()
        {
            var keyed = new KeyedGripLearner();
            var rnd = new System.Random(7);
            foreach (double speedKmh in new[] { 20.0, 80.0, 140.0, 200.0, 260.0 })
            {
                double vMps = speedKmh / 3.6;
                double truePeak = 1.4 + 0.0007 * vMps * vMps;
                for (int i = 0; i < 60; i++)
                    keyed.Observe("GameA", "Car1", truePeak * (0.98 + 0.04 * rnd.NextDouble()), speedKmh: speedKmh);
            }

            var exported = keyed.ExportAll();
            string key = KeyedGripLearner.MakeKey("GameA", "Car1");
            Assert.True(exported.ContainsKey(key));
            Assert.True(exported[key].K > 0.0, $"expected the exported K to be positive, got {exported[key].K}");

            var restored = new KeyedGripLearner();
            restored.ImportAll(exported);

            // Zero new observations - restored ratio at a fresh speed must match the exported model
            // exactly (cold, no hot evidence yet).
            double probeSpeedKmh = 300.0;
            double before = keyed.Ratio("GameA", "Car1", 2.0, applyColdStartCeiling: false, speedKmh: probeSpeedKmh);
            double after = restored.Ratio("GameA", "Car1", 2.0, applyColdStartCeiling: false, speedKmh: probeSpeedKmh);
            Assert.Equal(before, after, 6);
        }

        [Fact]
        public void Importing_a_pre_version_5_document_is_bit_identical_via_the_gmech_zero_sentinel()
        {
            // Simulates exactly what RuntimeStore.LoadFromDisk hands KeyedGripLearner.ImportAll for a file
            // written before this feature existed: GripLearnerState.GMech/K are absent from the JSON, so
            // Newtonsoft leaves them at their field-initialiser default (0.0).
            var legacyState = new GripLearnerState { PeakG = 3.1, Samples = 400 };
            Assert.Equal(0.0, legacyState.GMech, 12);
            Assert.Equal(0.0, legacyState.K, 12);

            var keyed = new KeyedGripLearner();
            keyed.ImportAll(new System.Collections.Generic.Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = legacyState
            });

            Assert.Equal(3.1, keyed.PublishedPeakG("GameA", "Car1"), 9);
            // k defaulted to 0 (the legacy sentinel) - the speed-aware reference at ANY speed must equal
            // the flat imported peak exactly, so Ratio(magnitude == peak) reads exactly 1.0.
            Assert.Equal(1.0, keyed.Ratio("GameA", "Car1", 3.1, applyColdStartCeiling: false, speedKmh: 250.0), 6);
        }
    }
}

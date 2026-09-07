using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// ROBUSTNESS of the Slip crossing gate and the cross-channel fallback as reached through the REAL
    /// engine, against telemetry that is absent, null-coalesced, non-finite, or out of range.
    /// <para/>
    /// Everything the gate consumes comes from a game via SimHub, so none of it can be assumed present
    /// or sane: a title may report no frame time, no G, no speed, or a slip ratio that has gone
    /// non-finite mid-corner. None of that may throw, and none of it may leave the channel silently
    /// dead.
    /// </summary>
    public class SlipCrossingRobustnessTests
    {
        private const string Game = "G", Car = "C", Source = "Src";

        private static ITelemetrySample Sample(
            double? oldSpeed, double? newSpeed, double? g, double? throttle, TimeSpan? dt)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: newSpeed, longitudinalG: g, throttlePercent: throttle),
                new TelemetryFrame(groundSpeedKmh: oldSpeed),
                DateTime.UtcNow, dt);

        [Fact]
        public void Telemetry_with_no_frame_time_at_all_does_not_throw_and_does_not_pin_the_ceiling()
        {
            // dt == null makes the engine pass dtSeconds = 0.0 to the gate on every single frame.
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 600; i++)
            {
                double g = Math.Min(1.5, 0.2 + 1.3 * i / 200.0);
                engine.Compute(Sample(100.0, 101.0, g, 90.0, null),
                    Corners.Zero, Corners.Uniform(100.0), Game, Car,
                    lockSourceIdentity: Source, slipSourceIdentity: Source);
            }

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.True(!ceiling.HasValue || ceiling.Value < 95.0,
                $"with no frame time the launch must still not pin the ceiling (got {ceiling})");
        }

        [Fact]
        public void Non_finite_and_out_of_range_source_values_do_not_throw()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            double[] nasty = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -50.0, 1e9 };

            for (int i = 0; i < nasty.Length; i++)
                for (int f = 0; f < 40; f++)
                    engine.Compute(
                        Sample(100.0, 101.0, 1.5 - f * 0.02, 90.0, TimeSpan.FromMilliseconds(16)),
                        Corners.Uniform(nasty[i]), Corners.Uniform(nasty[i]), Game, Car,
                        lockSourceIdentity: Source, slipSourceIdentity: Source);

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.True(!ceiling.HasValue || ClampMath.IsFinite(ceiling.Value),
                $"a non-finite source must never produce a non-finite ceiling (got {ceiling})");
        }

        [Fact]
        public void Non_finite_G_does_not_throw_or_produce_a_non_finite_ceiling()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            foreach (double g in new[] { double.NaN, double.PositiveInfinity })
                for (int f = 0; f < 60; f++)
                    engine.Compute(
                        Sample(100.0, 101.0, g, 90.0, TimeSpan.FromMilliseconds(16)),
                        Corners.Zero, Corners.Uniform(40.0), Game, Car,
                        lockSourceIdentity: Source, slipSourceIdentity: Source);

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.True(!ceiling.HasValue || ClampMath.IsFinite(ceiling.Value));
        }

        [Fact]
        public void Absent_telemetry_fields_do_not_throw()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            for (int f = 0; f < 60; f++)
                engine.Compute(Sample(null, null, null, null, null),
                    Corners.Zero, Corners.Uniform(40.0), Game, Car,
                    lockSourceIdentity: Source, slipSourceIdentity: Source);

            Assert.Equal(0L, engine.SlipCrossingValidFrames(Game, Car, Source));
        }

        [Fact]
        public void Null_identifiers_do_not_throw_anywhere_on_the_gate_path()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            for (int f = 0; f < 40; f++)
                engine.Compute(
                    Sample(100.0, 101.0, 1.5 - f * 0.02, 90.0, TimeSpan.FromMilliseconds(16)),
                    Corners.Zero, Corners.Uniform(40.0), null, null,
                    lockSourceIdentity: null, slipSourceIdentity: null);

            // The accessors must key consistently with what Compute wrote, nulls included.
            Assert.True(engine.SlipCrossingTotalFrames(null, null, null) >= 0);
            Assert.True(engine.SlipCrossingValidFrames(null, null, null) >= 0);
        }

        [Fact]
        public void The_teaching_weight_knob_rejects_nonsense_without_disabling_the_channel()
        {
            // A NaN here would make both `weight > 0` and `weight <= 0` false in ComputeChannel, so
            // neither teaching branch would run and Slip would learn nothing at all, silently.
            var engine = new NormalizedWheelLockSlipEngine { SlipNonCrossingTeachingWeight = double.NaN };
            Assert.Equal(0.0, engine.SlipNonCrossingTeachingWeight);

            engine.SlipNonCrossingTeachingWeight = 42.0;
            Assert.Equal(1.0, engine.SlipNonCrossingTeachingWeight);

            engine.SlipNonCrossingTeachingWeight = -3.0;
            Assert.Equal(0.0, engine.SlipNonCrossingTeachingWeight);
        }


        [Fact]
        public void An_absurd_out_of_range_source_cannot_inflate_the_ceiling()
        {
            // A source reading 1e9 would, if it reached the learner, become the ceiling - and Rescale
            // dividing by it would silence the channel outright. Measured: the upstream layers clamp to
            // 0-100 before ComputeChannel ever sees it, so the at-limit distribution stays EMPTY and the
            // ceiling holds at the canonical anchor. Pinned here because that clamp is what makes a
            // basis guard inside the gate unnecessary.
            var engine = new NormalizedWheelLockSlipEngine();
            for (int f = 0; f < 400; f++)
                engine.Compute(
                    Sample(100.0, 101.0, 1.5 - (f % 20) * 0.02, 90.0, TimeSpan.FromMilliseconds(16)),
                    Corners.Zero, Corners.Uniform(1e9), Game, Car,
                    lockSourceIdentity: Source, slipSourceIdentity: Source);

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(ceiling);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, ceiling.Value, 6);
            Assert.True(engine.SlipCarLevelSeverity <= 100.0, "published severity must stay clamped");
        }

        [Fact]
        public void Attaching_a_null_cross_channel_reference_is_a_no_op()
        {
            var slip = new KeyedScaleLearner(isLockChannel: false);
            slip.AttachCrossChannelLockReference(null);
            slip.SetCrossChannelLockSourceIdentity(null);
            slip.SetCrossChannelLockSourceIdentity(string.Empty);

            for (int i = 0; i < 400; i++) slip.ObserveNonQualifyingAtPhysicalLimit(Game, Car, Source);
            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, ceiling.Value, 6);
        }

        [Fact]
        public void Non_qualifying_observations_tolerate_null_keys_and_never_produce_a_bad_ceiling()
        {
            var slip = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 100; i++) slip.ObserveNonQualifyingAtPhysicalLimit(null, null, null);

            double? ceiling = slip.LearnedCeiling(null, null, null, out _);
            Assert.True(!ceiling.HasValue || ClampMath.IsFinite(ceiling.Value));
        }

        [Fact]
        public void A_starved_key_is_not_persisted_as_primary_tier_evidence()
        {
            // ExportAll's isPrimaryTier label is what the NEXT session borrows. Zero-weight fold-ins
            // advance Count without contributing any evidence, so labelling off Count would hand a cold
            // channel a "primary tier" reference built from nothing.
            var slip = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 5000; i++) slip.ObserveNonQualifyingAtPhysicalLimit(Game, Car, Source);

            foreach (var kv in slip.ExportAll())
                Assert.False(kv.Value.ColdIsPrimaryTier,
                    "a key with no weight-bearing evidence must not be persisted as primary tier");
        }

        [Fact]
        public void Half_a_million_non_qualifying_observations_keep_the_learner_sane()
        {
            // The zero-weight path advances the histogram's decay scale, which previously only ever
            // moved when a real value arrived. Renormalisation must still trip and reset it.
            var slip = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 600000; i++) slip.ObserveNonQualifyingAtPhysicalLimit(Game, Car, Source);

            // A real crossing after all that must still be learnable.
            for (int i = 0; i < 400; i++) slip.ObserveAtPhysicalLimit(Game, Car, Source, 45.0, 1.0);

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(ceiling);
            Assert.True(ClampMath.IsFinite(ceiling.Value), $"ceiling went non-finite: {ceiling}");
            Assert.Equal(45.0, ceiling.Value, 0);
        }
    }
}

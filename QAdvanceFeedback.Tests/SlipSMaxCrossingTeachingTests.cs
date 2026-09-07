using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE SLIP TRACTION-CROSSING GATE, END TO END through the engine
    /// (docs\slip-smax-crossing-gate-design.md).
    /// <para/>
    /// The behaviour under test is the owner's own field report: a standing start used to pin Slip's
    /// learned SMax at 100 for the rest of the session, because launch G is genuinely near the car's
    /// peak while the source reads full wheelspin - so the old <c>physicallyAtLimit</c> boolean opened
    /// and taught the ceiling from a moment that has nothing to do with the traction limit.
    /// </summary>
    public class SlipSMaxCrossingTeachingTests
    {
        private readonly ITestOutputHelper _out;
        public SlipSMaxCrossingTeachingTests(ITestOutputHelper output) { _out = output; }

        private const string Game = "G", Car = "C", Source = "Src";

        /// <summary>SpeedingUp from the first frame - the Slip channel's engaged direction.</summary>
        private static ITelemetrySample Accelerating(double gMagnitude, double speedKmh = 100.0)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: speedKmh + 1.0, longitudinalG: gMagnitude, throttlePercent: 90.0),
                new TelemetryFrame(groundSpeedKmh: speedKmh),
                DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static ITelemetrySample Braking(double gMagnitude, double speedKmh = 100.0)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: speedKmh - 1.0, longitudinalG: -gMagnitude, brakePercent: 80.0),
                new TelemetryFrame(groundSpeedKmh: speedKmh),
                DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static void Feed(NormalizedWheelLockSlipEngine engine, ITelemetrySample sample, double slipSource)
            => engine.Compute(sample, Corners.Zero, Corners.Uniform(slipSource), Game, Car,
                              lockSourceIdentity: Source, slipSourceIdentity: Source);

        /// <summary>
        /// A STANDING START / hard low-speed pull-away: full wheelspin on the source while G climbs to
        /// the car's peak and stays there. Slip and G rise TOGETHER, then both hold - at no point does
        /// slip rise while G falls, so nothing here is a traction crossing.
        /// </summary>
        private static void DriveLaunch(NormalizedWheelLockSlipEngine engine, int frames = 900)
        {
            for (int i = 0; i < frames; i++)
            {
                double g = Math.Min(1.5, 0.2 + 1.3 * i / 300.0);
                Feed(engine, Accelerating(g, 5.0 + i * 0.2), 100.0);
            }
        }

        /// <summary>
        /// Corner exits that DO cross the traction limit: the source climbs from 10 to ~60 while G
        /// falls away from the car's peak - the tyre past the peak of its slip curve.
        /// </summary>
        private static void DriveCrossings(NormalizedWheelLockSlipEngine engine, int corners = 40)
        {
            for (int c = 0; c < corners; c++)
            {
                for (int i = 0; i < 30; i++) Feed(engine, Accelerating(1.5), 10.0 + i * 1.7);          // build-up
                for (int i = 0; i < 30; i++) Feed(engine, Accelerating(1.5 - i * 0.015), 60.0 + i * 0.4); // crossing
                for (int i = 0; i < 30; i++) Feed(engine, Accelerating(1.5), 8.0);                     // recover
            }
        }

        [Fact]
        public void A_standing_start_no_longer_pins_slip_SMax_near_full_scale()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            DriveLaunch(engine);

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            _out.WriteLine($"launch-only Slip SMax: {ceiling}");

            Assert.NotNull(ceiling);
            Assert.True(ceiling.Value < 90.0,
                $"a launch pinned Slip SMax at {ceiling.Value:F1} - the crossing gate should have refused to learn from it");
        }

        [Fact]
        public void A_launch_leaves_the_ceiling_at_plain_identity()
        {
            // Sharper than the threshold test above. This source has no shipped reference, so the cold
            // state is plain identity - which IS a ceiling of CanonicalAtLimitAnchor, because Rescale
            // maps a raw value equal to the ceiling onto exactly that anchor. A launch must leave the
            // channel there: not merely "below 90", but exactly where it started.
            //
            // A brand-new engine reports null for the same state (nothing observed at all), so the
            // comparison is against the anchor rather than against that null.
            var engine = new NormalizedWheelLockSlipEngine();
            DriveLaunch(engine);

            double? afterLaunch = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            _out.WriteLine($"after launch: {afterLaunch}");

            Assert.NotNull(afterLaunch);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, afterLaunch.Value, 3);
        }

        [Fact]
        public void Crossings_do_teach_the_ceiling()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            DriveCrossings(engine);

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            double confidence = engine.SlipScaleLearner.CeilingHandoverConfidence(Game, Car, Source);
            _out.WriteLine($"crossings-only Slip SMax: {ceiling}  confidence {confidence:F2}");

            Assert.NotNull(ceiling);
            Assert.True(confidence > 0.5,
                $"40 crossing corners should build real confidence, got {confidence:F2}");
        }

        [Fact]
        public void A_launch_before_the_crossings_does_not_contaminate_what_they_teach()
        {
            // The actual field scenario end to end: leave the pits from a standstill, then drive.
            var launchedFirst = new NormalizedWheelLockSlipEngine();
            DriveLaunch(launchedFirst, 600);
            DriveCrossings(launchedFirst);

            var crossingsOnly = new NormalizedWheelLockSlipEngine();
            DriveCrossings(crossingsOnly);

            double withLaunch = launchedFirst.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
            double without = crossingsOnly.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
            _out.WriteLine($"with launch: {withLaunch:F2}   without: {without:F2}");

            Assert.True(Math.Abs(withLaunch - without) < 5.0,
                $"the launch shifted the learned ceiling by {Math.Abs(withLaunch - without):F1} ({withLaunch:F1} vs {without:F1})");
        }

        [Fact]
        public void Setting_the_non_crossing_weight_to_one_restores_the_old_contaminated_behaviour()
        {
            // Proves the gate is what does the work rather than some incidental change, and documents
            // exactly what the adjustable rate re-admits.
            var gated = new NormalizedWheelLockSlipEngine();
            DriveLaunch(gated);

            var ungated = new NormalizedWheelLockSlipEngine { SlipNonCrossingTeachingWeight = 1.0 };
            DriveLaunch(ungated);

            double withGate = gated.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
            double withoutGate = ungated.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
            _out.WriteLine($"gate on: {withGate:F2}   gate off (weight 1.0): {withoutGate:F2}");

            Assert.True(withoutGate > withGate + 10.0,
                $"weight 1.0 should re-admit the launch: gated {withGate:F1}, ungated {withoutGate:F1}");
        }

        /// <summary>
        /// The c_1_8_1 shape: a genuine crossing at a modest source value, immediately followed - inside
        /// the same 1 s hold window - by the wheel letting go completely and the source running to full
        /// scale. Before the onset snapshot this drove the learned ceiling to 100.
        /// </summary>
        private static void DriveCrossingsThatRunAway(NormalizedWheelLockSlipEngine engine, int corners = 40)
        {
            for (int c = 0; c < corners; c++)
            {
                for (int i = 0; i < 30; i++) Feed(engine, Accelerating(1.5), 8.0);                      // gripping
                for (int i = 0; i < 10; i++) Feed(engine, Accelerating(1.5 - i * 0.02), 20.0 + i * 1.5); // the crossing
                for (int i = 0; i < 25; i++) Feed(engine, Accelerating(1.0), 100.0);                    // full break-away
            }
        }

        [Fact]
        public void The_break_away_after_a_crossing_does_not_set_the_ceiling()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            DriveCrossingsThatRunAway(engine);

            double? ceiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            double? atLimitP90 = engine.SlipScaleLearner.PhysicalAnchorLevel(Game, Car, Source, 90.0);
            _out.WriteLine($"runaway session: ceiling {ceiling}  at-limit P90 {atLimitP90}");

            Assert.NotNull(atLimitP90);
            Assert.True(atLimitP90.Value < 60.0,
                $"the at-limit pool should hold crossing-time values (~35), not the 100 the source ran to - got {atLimitP90.Value:F1}");
            Assert.NotNull(ceiling);
            Assert.True(ceiling.Value < 70.0,
                $"the published ceiling should not be set by the break-away - got {ceiling.Value:F1}");
        }

        [Fact]
        public void The_gate_leaves_the_lock_channel_alone()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            for (int c = 0; c < 30; c++)
                for (int i = 0; i < 60; i++)
                    engine.Compute(Braking(1.6 - i * 0.01), Corners.Uniform(20.0 + i * 0.9), Corners.Zero,
                                   Game, Car, lockSourceIdentity: Source, slipSourceIdentity: Source);

            double? lockCeiling = engine.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            double lockConfidence = engine.LockScaleLearner.CeilingHandoverConfidence(Game, Car, Source);
            _out.WriteLine($"Lock SMax {lockCeiling} confidence {lockConfidence:F2}");

            Assert.NotNull(lockCeiling);
            Assert.True(lockConfidence > 0.0, "Lock still learns on its own corner-local detector");
            Assert.Equal(0L, engine.SlipCrossingTotalFrames(Game, Car, Source));
        }

        [Fact]
        public void Lock_learns_exactly_the_same_ceiling_whatever_the_slip_gate_is_doing()
        {
            // THE ISOLATION GUARANTEE, asserted numerically rather than by inspection. Two engines see
            // IDENTICAL Lock telemetry; one of them additionally has the Slip channel driven hard
            // through crossings and break-aways, and one has the non-crossing weight cranked to 1.0.
            // Lock's learned ceiling and confidence must be bit-identical in all three.
            //
            // Lock shares ComputeChannel with Slip and its own teaching basis flows through the same
            // locals the crossing gate now writes to, so "Slip-only" is a property that has to be
            // pinned, not assumed.
            var lockOnly = new NormalizedWheelLockSlipEngine();
            var withSlipTraffic = new NormalizedWheelLockSlipEngine();
            var withGateDisabled = new NormalizedWheelLockSlipEngine { SlipNonCrossingTeachingWeight = 1.0 };

            for (int c = 0; c < 30; c++)
            {
                for (int i = 0; i < 60; i++)
                {
                    ITelemetrySample braking = Braking(1.6 - i * 0.01);
                    Corners lockSource = Corners.Uniform(20.0 + i * 0.9);

                    lockOnly.Compute(braking, lockSource, Corners.Zero, Game, Car,
                        lockSourceIdentity: Source, slipSourceIdentity: Source);
                    // Same Lock telemetry, but a busy Slip source alongside it.
                    withSlipTraffic.Compute(braking, lockSource, Corners.Uniform(100.0), Game, Car,
                        lockSourceIdentity: Source, slipSourceIdentity: Source);
                    withGateDisabled.Compute(braking, lockSource, Corners.Uniform(100.0), Game, Car,
                        lockSourceIdentity: Source, slipSourceIdentity: Source);
                }
            }

            double baseline = lockOnly.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0;
            double baselineConf = lockOnly.LockScaleLearner.CeilingHandoverConfidence(Game, Car, Source);
            _out.WriteLine($"Lock baseline {baseline:F4} conf {baselineConf:F4}");

            Assert.Equal(baseline, withSlipTraffic.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0, 10);
            Assert.Equal(baseline, withGateDisabled.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _) ?? 0.0, 10);
            Assert.Equal(baselineConf, withSlipTraffic.LockScaleLearner.CeilingHandoverConfidence(Game, Car, Source), 10);
            Assert.Equal(baselineConf, withGateDisabled.LockScaleLearner.CeilingHandoverConfidence(Game, Car, Source), 10);
        }

        [Fact]
        public void Valid_and_total_candidate_frames_are_recorded_for_the_current_key()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            DriveCrossings(engine, corners: 20);

            long valid = engine.SlipCrossingValidFrames(Game, Car, Source);
            long total = engine.SlipCrossingTotalFrames(Game, Car, Source);
            _out.WriteLine($"valid {valid} / total {total}");

            Assert.True(total > 0, "at-limit candidates should have been seen");
            Assert.True(valid > 0, "some of them should have been crossings");
            Assert.True(valid <= total, "valid frames are a subset of candidates");
        }

        [Fact]
        public void A_launch_records_candidates_but_qualifies_none_of_them()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            DriveLaunch(engine);

            long valid = engine.SlipCrossingValidFrames(Game, Car, Source);
            long total = engine.SlipCrossingTotalFrames(Game, Car, Source);
            _out.WriteLine($"launch: valid {valid} / total {total}");

            Assert.True(total > 0, "a launch DOES trip the old at-limit boolean - that was the problem");
            Assert.Equal(0L, valid);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Task G (docs\internal-source-learning-design.md): our internal source (Plugin Internal = Layer 3 Raw)
    /// learns every frame whichever source is active; every other source learns only while active; the
    /// internal key's learned state is the same whether it was selected or not; it is fed once per frame.
    /// </summary>
    public class InternalSourceLearningTests
    {
        private const string Game = "G";
        private const string Car = "C";
        private static string LockRaw => RawSourceFallback.RawIdentity(true);
        private static string SlipRaw => RawSourceFallback.RawIdentity(false);

        // ---- frame sequences --------------------------------------------------------------------------

        private static ITelemetrySample Braking(double g, double brake = 80.0)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -g, brakePercent: brake),
                new TelemetryFrame(groundSpeedKmh: 101.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static ITelemetrySample Accelerating(double g, double throttle = 90.0)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: 101.0, longitudinalG: g, throttlePercent: throttle),
                new TelemetryFrame(groundSpeedKmh: 100.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static ITelemetrySample Coasting()
            => new TelemetrySample(new TelemetryFrame(groundSpeedKmh: 100.0), new TelemetryFrame(groundSpeedKmh: 100.0),
                DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        /// <summary>One frame: the telemetry, the active source's four values and Layer 3 Raw's four values.</summary>
        private struct Step
        {
            public ITelemetrySample Sample;
            public Corners Active;
            public Corners Raw;
        }

        /// <summary>
        /// Repeated "past the friction peak" events (G falling while the source rises) separated by a
        /// recovery and a gap - the shape both crossing gates accept - with the ACTIVE source and Raw on
        /// different scales and different per-wheel shapes, so the two keys see genuinely different values.
        /// </summary>
        private static List<Step> Sequence(bool braking, int cycles = 30)
        {
            var steps = new List<Step>();
            double peak = braking ? 3.0 : 1.2;
            Func<double, ITelemetrySample> make = g => braking ? Braking(g) : Accelerating(g);
            for (int i = 0; i < 30; i++)
                steps.Add(new Step { Sample = make(peak), Active = Corners.Uniform(30.0), Raw = new Corners(50, 52, 45, 47) });

            for (int c = 0; c < cycles; c++)
            {
                double activeTop = 35.0 + (c % 5);
                double rawTop = 70.0 + (c % 7);
                for (int i = 0; i < 8; i++)
                {
                    double t = i / 7.0;
                    steps.Add(new Step
                    {
                        Sample = make(peak * (1.0 - 0.006 * i)),
                        Active = new Corners(activeTop * (0.85 + 0.15 * t), activeTop * (0.80 + 0.2 * t), activeTop * 0.7, activeTop * 0.72),
                        Raw = new Corners(rawTop * (0.82 + 0.18 * t), rawTop * (0.86 + 0.14 * t), rawTop * (0.6 + 0.1 * t), rawTop * 0.65),
                    });
                }
                for (int i = 0; i < 70; i++)
                    steps.Add(new Step { Sample = make(peak), Active = Corners.Uniform(activeTop), Raw = new Corners(rawTop, rawTop, rawTop * 0.7, rawTop * 0.7) });
                for (int i = 0; i < 5; i++)
                    steps.Add(new Step { Sample = Coasting(), Active = Corners.Zero, Raw = Corners.Zero });
            }
            return steps;
        }

        private static List<NormalizedWheelLockSlipResult> Run(NormalizedWheelLockSlipEngine engine, List<Step> steps,
            bool lockChannel, string activeIdentity, bool activeReadsRaw)
        {
            var outputs = new List<NormalizedWheelLockSlipResult>(steps.Count);
            foreach (Step s in steps)
            {
                Corners active = activeReadsRaw ? s.Raw : s.Active;
                outputs.Add(lockChannel
                    ? engine.Compute(s.Sample, active, Corners.Zero, Game, Car,
                        lockSourceIdentity: activeIdentity, slipSourceIdentity: SlipRaw, layer3RawLockWheels: s.Raw)
                    : engine.Compute(s.Sample, Corners.Zero, active, Game, Car,
                        lockSourceIdentity: LockRaw, slipSourceIdentity: activeIdentity, layer3RawSlipWheels: s.Raw));
            }
            return outputs;
        }

        // ---- exported state -------------------------------------------------------------------------

        private static string Fields(object o)
        {
            if (o == null) return "null";
            var sb = new StringBuilder();
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
                sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(o), System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }

        private static string SourceSegment(string key)
        {
            string[] parts = key.Split(new[] { "|#|" }, StringSplitOptions.None);
            return parts.Length > 2 ? QAdvanceFeedback.Core.KeyRules.SourceKeys.FromKey(parts[2]) : string.Empty;
        }

        /// <summary>Every persisted family's entries for one source, plus its crossing counters and
        /// readouts, as one comparable string.</summary>
        private static string StateOf(NormalizedWheelLockSlipEngine engine, bool lockChannel, string identity)
        {
            var sb = new StringBuilder();
            KeyedGripLearner grip = lockChannel ? engine.LockLearners : engine.SlipLearners;
            KeyedScaleLearner scale = lockChannel ? engine.LockScaleLearner : engine.SlipScaleLearner;
            foreach (var kv in grip.ExportAll().Where(kv => SourceSegment(kv.Key) == identity).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("grip ").Append(kv.Key).Append(' ').Append(Fields(kv.Value)).Append('\n');
            foreach (var kv in scale.ExportAll().Where(kv => SourceSegment(kv.Key) == identity).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("scale ").Append(kv.Key).Append(' ').Append(Fields(kv.Value)).Append('\n');
            if (lockChannel)
                foreach (var kv in engine.LockAnchors.ExportAll().Where(kv => SourceSegment(kv.Key) == identity).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.Append("anchor ").Append(kv.Key).Append(' ').Append(Fields(kv.Value)).Append('\n');

            sb.Append("ceiling ").Append(scale.LearnedCeiling(Game, Car, identity, out bool primary)).Append(primary).Append('\n');
            foreach (string bucket in new[] { NormalizedWheelLockSlipEngine.SealedSurfaceBucket, NormalizedWheelLockSlipEngine.LooseSurfaceBucket })
                sb.Append("ceiling-").Append(bucket).Append(' ').Append(scale.LearnedCeiling(Game, Car, identity, out _, bucket)).Append('\n');
            sb.Append("crossings ")
              .Append(lockChannel ? engine.LockCrossingValidFrames(Game, Car, identity) : engine.SlipCrossingValidFrames(Game, Car, identity)).Append('/')
              .Append(lockChannel ? engine.LockCrossingTotalFrames(Game, Car, identity) : engine.SlipCrossingTotalFrames(Game, Car, identity)).Append('\n');
            sb.Append("manual ").Append(engine.ManualGateElapsedSeconds(Game, Car, identity).ToString("R")).Append('\n');
            return sb.ToString();
        }

        private static int SealedSamples(NormalizedWheelLockSlipEngine engine, bool lockChannel, string identity)
            => (lockChannel ? engine.LockLearners : engine.SlipLearners).Samples(Game, Car, identity, NormalizedWheelLockSlipEngine.SealedSurfaceBucket);

        public static IEnumerable<object[]> OtherSources()
        {
            foreach (bool lockChannel in new[] { true, false })
            {
                yield return new object[] { lockChannel, ShakeItIdentity(lockChannel) };
                yield return new object[] { lockChannel, "Plain:viper4gh.WheelData~Plain:viper4gh.FR~Plain:viper4gh.RL~Plain:viper4gh.RR" };
                yield return new object[] { lockChannel, "JavaScript:1a2b3c4d~JavaScript:1a2b3c4d~JavaScript:1a2b3c4d~JavaScript:1a2b3c4d" };
            }
        }

        private static string ShakeItIdentity(bool lockChannel)
            => Core.ShakeItEffects.ShakeItSourceResolution.ShakeItIdentity(lockChannel);

        // ---- the rule -------------------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(OtherSources))]
        public void The_internal_key_grows_while_another_source_is_active(bool lockChannel, string other)
        {
            var engine = new NormalizedWheelLockSlipEngine();
            string internalIdentity = lockChannel ? LockRaw : SlipRaw;

            Run(engine, Sequence(braking: lockChannel), lockChannel, other, activeReadsRaw: false);

            Assert.True(SealedSamples(engine, lockChannel, internalIdentity) > 0);
            KeyedScaleLearner scale = lockChannel ? engine.LockScaleLearner : engine.SlipScaleLearner;
            Assert.NotNull(scale.LearnedCeiling(Game, Car, internalIdentity, out _));
            Assert.True((lockChannel ? engine.LockCrossingTotalFrames(Game, Car, internalIdentity)
                                     : engine.SlipCrossingTotalFrames(Game, Car, internalIdentity)) > 0);
            Assert.Contains(scale.ExportAll().Keys, k => SourceSegment(k) == internalIdentity);
            // ...and the active source learns under its own key, beside it.
            Assert.True(SealedSamples(engine, lockChannel, other) > 0);
        }

        [Theory]
        [MemberData(nameof(OtherSources))]
        public void The_internal_state_is_identical_with_and_without_being_selected(bool lockChannel, string other)
        {
            string internalIdentity = lockChannel ? LockRaw : SlipRaw;
            List<Step> steps = Sequence(braking: lockChannel);

            var selected = new NormalizedWheelLockSlipEngine();
            Run(selected, steps, lockChannel, internalIdentity, activeReadsRaw: true);
            var notSelected = new NormalizedWheelLockSlipEngine();
            Run(notSelected, steps, lockChannel, other, activeReadsRaw: false);

            string expected = StateOf(selected, lockChannel, internalIdentity);
            Assert.Contains("scale ", expected);   // the comparison is not vacuous
            Assert.Equal(expected, StateOf(notSelected, lockChannel, internalIdentity));
        }

        [Theory]
        [MemberData(nameof(OtherSources))]
        public void A_source_that_is_not_active_never_grows(bool lockChannel, string other)
        {
            string internalIdentity = lockChannel ? LockRaw : SlipRaw;
            List<Step> steps = Sequence(braking: lockChannel, cycles: 10);

            // Never selected: nothing at all under its key.
            var engine = new NormalizedWheelLockSlipEngine();
            Run(engine, steps, lockChannel, internalIdentity, activeReadsRaw: true);
            Assert.Equal(0, SealedSamples(engine, lockChannel, other));
            KeyedScaleLearner scale = lockChannel ? engine.LockScaleLearner : engine.SlipScaleLearner;
            Assert.DoesNotContain(scale.ExportAll().Keys, k => SourceSegment(k) == other);
            Assert.DoesNotContain(engine.LockAnchors.ExportAll().Keys, k => SourceSegment(k) == other);

            // Learned while active, then frozen while another source drives.
            var switched = new NormalizedWheelLockSlipEngine();
            Run(switched, steps, lockChannel, other, activeReadsRaw: false);
            string frozen = StateOf(switched, lockChannel, other);
            Run(switched, steps, lockChannel, internalIdentity, activeReadsRaw: true);
            Assert.Equal(frozen, StateOf(switched, lockChannel, other));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_internal_key_is_fed_once_per_frame_whether_selected_or_not(bool lockChannel)
        {
            string internalIdentity = lockChannel ? LockRaw : SlipRaw;
            string other = ShakeItIdentity(lockChannel);
            List<Step> steps = Sequence(braking: lockChannel, cycles: 8);

            var selected = new NormalizedWheelLockSlipEngine();
            Run(selected, steps, lockChannel, internalIdentity, activeReadsRaw: true);
            var notSelected = new NormalizedWheelLockSlipEngine();
            Run(notSelected, steps, lockChannel, other, activeReadsRaw: false);

            KeyedGripLearner physical = lockChannel ? selected.LockPhysicalReference : selected.SlipPhysicalReference;
            int qualifyingFrames = physical.Samples(Game, Car, "", NormalizedWheelLockSlipEngine.SealedSurfaceBucket);
            Assert.True(qualifyingFrames > 0);
            // One grip observation per qualifying frame for the internal key - selected or fed alongside -
            // and the same count for the active source on its own key.
            Assert.Equal(qualifyingFrames, SealedSamples(selected, lockChannel, internalIdentity));
            Assert.Equal(qualifyingFrames, SealedSamples(notSelected, lockChannel, internalIdentity));
            Assert.Equal(qualifyingFrames, SealedSamples(notSelected, lockChannel, other));
            // The shared physical reference is still observed exactly once per frame.
            KeyedGripLearner physicalNotSelected = lockChannel ? notSelected.LockPhysicalReference : notSelected.SlipPhysicalReference;
            Assert.Equal(qualifyingFrames, physicalNotSelected.Samples(Game, Car, "", NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
        }

        [Fact]
        public void A_channel_fallen_back_to_raw_is_the_internal_learner_fed_once()
        {
            // Task C's Raw fallback route hands the engine Raw's values under the Raw identity - which is
            // exactly "Plugin Internal selected" from the engine's point of view.
            List<Step> steps = Sequence(braking: true, cycles: 8);
            var fallenBack = new NormalizedWheelLockSlipEngine();
            Run(fallenBack, steps, true, LockRaw, activeReadsRaw: true);
            int frames = fallenBack.LockPhysicalReference.Samples(Game, Car, "", NormalizedWheelLockSlipEngine.SealedSurfaceBucket);
            Assert.Equal(frames, SealedSamples(fallenBack, true, LockRaw));
        }

        [Fact]
        public void Shared_non_source_state_is_the_same_whichever_source_is_active()
        {
            // Surface support and the flat physical peak learn from the car's own motion only: the extra
            // internal pass must not observe them a second time.
            List<Step> steps = Sequence(braking: true, cycles: 8);
            var selected = new NormalizedWheelLockSlipEngine();
            Run(selected, steps, true, LockRaw, activeReadsRaw: true);
            var notSelected = new NormalizedWheelLockSlipEngine();
            Run(notSelected, steps, true, ShakeItIdentity(true), activeReadsRaw: false);

            string Surface(NormalizedWheelLockSlipEngine e) => string.Join(",", e.SurfaceSupport.ExportAll().Select(kv => kv.Key + "=" + kv.Value));
            Assert.Equal(Surface(selected), Surface(notSelected));
            Assert.Equal(selected.LockPhysicalReference.LearnedPeakG(Game, Car, "", NormalizedWheelLockSlipEngine.SealedSurfaceBucket),
                notSelected.LockPhysicalReference.LearnedPeakG(Game, Car, "", NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_internal_pass_does_not_change_the_active_output_or_the_active_key(bool lockChannel)
        {
            // Raw stays under the comparison floor (no "configured source went quiet" evidence), so the
            // published output can only come from the active source. The internal pass still runs on every
            // frame (grip learner, gate, anchors, manual gate) - and must leave no trace on the output or
            // on the active key, whatever Raw reads.
            string other = ShakeItIdentity(lockChannel);
            List<Step> steps = Sequence(braking: lockChannel, cycles: 10);
            List<Step> quietRaw = steps.Select(s => new Step { Sample = s.Sample, Active = s.Active, Raw = Corners.Uniform(s.Raw.FrontLeft > 0 ? 8.0 : 0.0) }).ToList();
            List<Step> deadRaw = steps.Select(s => new Step { Sample = s.Sample, Active = s.Active, Raw = Corners.Zero }).ToList();

            var withInternalData = new NormalizedWheelLockSlipEngine();
            List<NormalizedWheelLockSlipResult> a = Run(withInternalData, quietRaw, lockChannel, other, activeReadsRaw: false);
            var withoutInternalData = new NormalizedWheelLockSlipEngine();
            List<NormalizedWheelLockSlipResult> b = Run(withoutInternalData, deadRaw, lockChannel, other, activeReadsRaw: false);

            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(lockChannel ? b[i].LockAll : b[i].SlipAll, lockChannel ? a[i].LockAll : a[i].SlipAll);
                Assert.Equal(lockChannel ? b[i].LockFront : b[i].SlipFront, lockChannel ? a[i].LockFront : a[i].SlipFront);
            }
            Assert.Equal(StateOf(withoutInternalData, lockChannel, other), StateOf(withInternalData, lockChannel, other));
        }

        [Fact]
        public void The_quiet_source_comparison_reads_the_internal_key()
        {
            // A configured source that reads far below Raw for a sustained stretch engages the relative
            // fallback - which now compares against the internal key's own calibration.
            var engine = new NormalizedWheelLockSlipEngine();
            List<Step> steps = Sequence(braking: true, cycles: 20)
                .Select(s => new Step { Sample = s.Sample, Active = new Corners(s.Raw.FrontLeft * 0.1, s.Raw.FrontRight * 0.1, s.Raw.RearLeft * 0.1, s.Raw.RearRight * 0.1), Raw = s.Raw })
                .ToList();
            Run(engine, steps, true, "QuietSource", activeReadsRaw: false);
            for (int i = 0; i < 120; i++)
                engine.Compute(Braking(2.9), Corners.Uniform(2.0), Corners.Zero, Game, Car,
                    lockSourceIdentity: "QuietSource", layer3RawLockWheels: Corners.Uniform(70.0));

            Assert.True(engine.LockSourceFallbackActive);
            Assert.NotNull(engine.LockScaleLearner.LearnedCeiling(Game, Car, LockRaw, out _));
        }

        // ---- fix round 1: shared state is a function of car motion + the internal source only --------

        private static ITelemetrySample BrakingAt(double g, double speedKmh)
            => new TelemetrySample(
                new TelemetryFrame(groundSpeedKmh: speedKmh, longitudinalG: -g, brakePercent: 80.0),
                new TelemetryFrame(groundSpeedKmh: speedKmh + 1.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        /// <summary>Braking events across several speed buckets; the active source is a flat, quantised
        /// series (the ShakeIt failure shape the review named) while Raw rises through each event.</summary>
        private static List<Step> SpeedSweepWithFlatActive(int cycles = 30)
        {
            var steps = new List<Step>();
            for (int c = 0; c < cycles; c++)
            {
                double speed = 60.0 + 25.0 * (c % 8);
                double rawTop = 70.0 + (c % 7);
                for (int i = 0; i < 8; i++)
                {
                    double t = i / 7.0;
                    steps.Add(new Step
                    {
                        Sample = BrakingAt(3.0 * (1.0 - 0.006 * i), speed - i),
                        Active = Corners.Uniform(i % 2 == 0 ? 40.0 : 20.0),   // quantised, stepping down every other frame
                        Raw = new Corners(rawTop * (0.82 + 0.18 * t), rawTop * (0.86 + 0.14 * t), rawTop * (0.6 + 0.1 * t), rawTop * 0.65),
                    });
                }
                for (int i = 0; i < 70; i++)
                    steps.Add(new Step { Sample = BrakingAt(3.0, speed - 8 - i * 0.2), Active = Corners.Uniform(i % 2 == 0 ? 40.0 : 20.0), Raw = new Corners(rawTop, rawTop, rawTop * 0.7, rawTop * 0.7) });
                for (int i = 0; i < 5; i++)
                    steps.Add(new Step { Sample = Coasting(), Active = Corners.Zero, Raw = Corners.Zero });
            }
            return steps;
        }

        private static string PhysicalState(NormalizedWheelLockSlipEngine e)
        {
            var sb = new StringBuilder(Newtonsoft.Json.JsonConvert.SerializeObject(
                e.LockPhysicalReference.ExportAll().OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList()));
            // The live speed buckets (session state, not exported) - the part the admission weight decides.
            for (int i = 0; i < KeyedGripLearner.SpeedBucketCount; i++)
            {
                e.LockPhysicalReference.SnapshotSpeedBucket(Game, Car, "", NormalizedWheelLockSlipEngine.SealedSurfaceBucket, i,
                    out bool live, out double peak, out double speed, out int samples);
                sb.Append('\n').Append(i).Append(' ').Append(live).Append(' ').Append(peak.ToString("R"))
                  .Append(' ').Append(speed.ToString("R")).Append(' ').Append(samples);
            }
            return sb.ToString();
        }

        [Fact]
        public void The_physical_reference_and_internal_anchors_do_not_depend_on_the_active_source()
        {
            List<Step> steps = SpeedSweepWithFlatActive();
            var selected = new NormalizedWheelLockSlipEngine();
            Run(selected, steps, true, LockRaw, activeReadsRaw: true);
            var flatShakeIt = new NormalizedWheelLockSlipEngine();
            Run(flatShakeIt, steps, true, ShakeItIdentity(true), activeReadsRaw: false);

            Assert.Equal(PhysicalState(selected), PhysicalState(flatShakeIt));
            string expected = StateOf(selected, true, LockRaw);
            Assert.Contains("scale ", expected);
            Assert.Equal(expected, StateOf(flatShakeIt, true, LockRaw));
        }

        /// <summary>Continuously engaged braking with repeated rises - no disengagement anywhere, so only an
        /// identity change can clear the shared gate.</summary>
        private static List<Step> EngagedRises(int frames, double activeScale, int phaseOffset = 0)
        {
            var steps = new List<Step>();
            for (int n = 0; n < frames; n++)
            {
                int p = (n + phaseOffset) % 40;
                double rising = p < 8 ? p / 7.0 : 1.0;
                double g = p < 8 ? 3.0 * (1.0 - 0.006 * p) : 3.0;
                steps.Add(new Step
                {
                    Sample = Braking(g),
                    Active = Corners.Uniform(activeScale * (30.0 + 10.0 * rising)),
                    Raw = Corners.Uniform(55.0 + 15.0 * rising),
                });
            }
            return steps;
        }

        private static (long valid, long total) Counters(NormalizedWheelLockSlipEngine e, string identity)
            => (e.LockCrossingValidFrames(Game, Car, identity), e.LockCrossingTotalFrames(Game, Car, identity));

        [Fact]
        public void A_switch_between_two_other_sources_carries_no_trend_or_snapshot()
        {
            const string viper = "Plain:viper4gh.WheelLock.FL~Plain:viper4gh.FR~Plain:viper4gh.RL~Plain:viper4gh.RR";
            // Switch in the middle of a rise: ShakeIt's trend (in ShakeIt's units) must not reach Viper.
            List<Step> before = EngagedRises(300 + 4, 2.0);
            List<Step> after = EngagedRises(400, 0.5, phaseOffset: 4);

            var switched = new NormalizedWheelLockSlipEngine();
            Run(switched, before, true, ShakeItIdentity(true), activeReadsRaw: false);
            Run(switched, after, true, viper, activeReadsRaw: false);

            // Reference: the same history with Plugin Internal in front - the shared state (physical
            // reference, internal key) is identical by construction, and the shared gate was never touched.
            var reference = new NormalizedWheelLockSlipEngine();
            Run(reference, before, true, LockRaw, activeReadsRaw: true);
            Run(reference, after, true, viper, activeReadsRaw: false);

            Assert.Equal(StateOf(reference, true, viper), StateOf(switched, true, viper));
        }

        [Fact]
        public void A_source_resuming_after_a_raw_fallback_does_not_resume_its_pre_gap_trend()
        {
            List<Step> first = EngagedRises(300 + 4, 2.0);
            List<Step> gap = EngagedRises(200, 1.0, phaseOffset: 4);
            List<Step> resume = EngagedRises(400, 0.5, phaseOffset: 4 + 200 + 1);   // resumes on a lower scale
            string shakeIt = ShakeItIdentity(true);

            var engine = new NormalizedWheelLockSlipEngine();
            Run(engine, first, true, shakeIt, activeReadsRaw: false);
            (long valid, long total) atGap = Counters(engine, shakeIt);
            Run(engine, gap, true, LockRaw, activeReadsRaw: true);           // ShakeIt went quiet: Raw fallback route
            Run(engine, resume, true, shakeIt, activeReadsRaw: false);
            (long valid, long total) end = Counters(engine, shakeIt);

            // Reference: Plugin Internal drove the first stretch, so no other source's series
            // sits in the shared gate before the gap; with it cleared on every identity change, ShakeIt's crossings
            // after resuming are the same.
            var reference = new NormalizedWheelLockSlipEngine();
            Run(reference, first, true, LockRaw, activeReadsRaw: true);   // Plugin Internal never touches the shared gate
            Run(reference, gap, true, LockRaw, activeReadsRaw: true);
            Run(reference, resume, true, shakeIt, activeReadsRaw: false);

            Assert.True(end.total - atGap.total > 0);
            Assert.Equal(Counters(reference, shakeIt), (end.valid - atGap.valid, end.total - atGap.total));
        }

        /// <summary>Braking corners whose G builds up through 75% and 90% of the car's peak while Raw rises
        /// with it - the shape the Lock anchor learner brackets S75/S90 from.</summary>
        private static List<Step> AnchorCorners(int cycles)
        {
            var steps = new List<Step>();
            for (int i = 0; i < 30; i++)
                steps.Add(new Step { Sample = Braking(3.0), Active = Corners.Uniform(30.0), Raw = Corners.Uniform(75.0) });
            for (int c = 0; c < cycles; c++)
            {
                for (int i = 0; i < 40; i++)
                {
                    double t = i / 39.0;
                    steps.Add(new Step { Sample = Braking(0.6 + 2.4 * t), Active = Corners.Uniform(10.0 + 25.0 * t), Raw = Corners.Uniform(20.0 + 55.0 * t) });
                }
                for (int i = 0; i < 8; i++)
                    steps.Add(new Step { Sample = Braking(3.0 * (1.0 - 0.006 * i)), Active = Corners.Uniform(35.0 + i), Raw = Corners.Uniform(75.0 + i) });
                for (int i = 0; i < 20; i++)
                    steps.Add(new Step { Sample = Braking(3.0), Active = Corners.Uniform(40.0), Raw = Corners.Uniform(80.0) });
                for (int i = 0; i < 5; i++)
                    steps.Add(new Step { Sample = Coasting(), Active = Corners.Zero, Raw = Corners.Zero });
            }
            return steps;
        }

        [Fact]
        public void Switching_away_from_plugin_internal_mid_braking_does_not_split_the_internal_corner()
        {
            // Raw fallback while braking, then ShakeIt returns while STILL braking: the internal key's corner
            // run must continue on the internal pass, not be closed at the switch.
            List<Step> steps = AnchorCorners(30);
            var alwaysRaw = new NormalizedWheelLockSlipEngine();
            Run(alwaysRaw, steps, true, LockRaw, activeReadsRaw: true);

            var switching = new NormalizedWheelLockSlipEngine();
            string shakeIt = ShakeItIdentity(true);
            for (int n = 0; n < steps.Count; n++)
            {
                // Each cycle: Raw (fallback) through most of the build-up, ShakeIt returning mid-corner.
                int p = n < 30 ? -1 : (n - 30) % (40 + 8 + 20 + 5);
                bool raw = p < 25;   // Raw for the first part of the build-up, ShakeIt from there on
                Step s = steps[n];
                switching.Compute(s.Sample, raw ? s.Raw : s.Active, Corners.Zero, Game, Car,
                    lockSourceIdentity: raw ? LockRaw : shakeIt, slipSourceIdentity: SlipRaw, layer3RawLockWheels: s.Raw);
            }

            string expected = StateOf(alwaysRaw, true, LockRaw);
            Assert.Contains("anchor ", expected);
            Assert.Equal(expected, StateOf(switching, true, LockRaw));
        }

        [Fact]
        public void A_car_and_source_change_on_the_same_frame_closes_the_run_under_the_previous_car()
        {
            // The last corner on Car1 (ShakeIt) is still open when the very next frame arrives on Car2 with
            // Viper. ShakeIt's Car1 run must be closed under CAR1 - exactly as a normal end of braking on
            // Car1 would close it.
            string shakeIt = ShakeItIdentity(true);
            const string viper = "Plain:viper4gh.WheelLock.FL~Plain:viper4gh.FR~Plain:viper4gh.RL~Plain:viper4gh.RR";
            List<Step> corners = AnchorCorners(12);
            List<Step> openLastCorner = corners.Take(corners.Count - 5).ToList();   // drop the final coast

            string Car1ShakeItAnchors(NormalizedWheelLockSlipEngine e) => string.Join("\n", e.LockAnchors.ExportAll()
                .Where(kv => kv.Key.StartsWith(KeyedGripLearner.MakeKey(Game, "Car1", shakeIt, ""), StringComparison.Ordinal))
                .Select(kv => kv.Key + " " + Fields(kv.Value)));
            void Feed(NormalizedWheelLockSlipEngine e, IEnumerable<Step> steps, string car, string identity)
            {
                foreach (Step s in steps)
                    e.Compute(s.Sample, s.Active, Corners.Zero, Game, car, lockSourceIdentity: identity,
                        slipSourceIdentity: SlipRaw, layer3RawLockWheels: s.Raw);
            }

            var switched = new NormalizedWheelLockSlipEngine();
            Feed(switched, openLastCorner, "Car1", shakeIt);
            Feed(switched, new[] { new Step { Sample = Braking(3.0), Active = Corners.Uniform(20.0), Raw = Corners.Uniform(70.0) } }, "Car2", viper);

            var endedNormally = new NormalizedWheelLockSlipEngine();
            Feed(endedNormally, openLastCorner, "Car1", shakeIt);
            Feed(endedNormally, new[] { new Step { Sample = Coasting(), Active = Corners.Zero, Raw = Corners.Zero } }, "Car1", shakeIt);

            string expected = Car1ShakeItAnchors(endedNormally);
            Assert.False(string.IsNullOrEmpty(expected));
            Assert.Equal(expected, Car1ShakeItAnchors(switched));
        }

        [Fact]
        public void The_cross_channel_pairing_is_safe_to_read_from_another_thread_while_lock_switches()
        {
            string lockShakeIt = ShakeItIdentity(true);
            string slipShakeIt = ShakeItIdentity(false);
            const string lockViper = "NCalc:00000000~NCalc:00000000~NCalc:00000000~NCalc:00000000";
            string lockViperReal = KnownSourceColdStartReference.ViperSourceIdentity(true);
            var engine = new NormalizedWheelLockSlipEngine();
            Exception readerFault = null;
            bool stop = false;
            var reader = new System.Threading.Thread(() =>
            {
                try
                {
                    while (!System.Threading.Volatile.Read(ref stop))
                    {
                        engine.ResolveCrossChannelPairingForTesting(slipShakeIt);
                        engine.ResolveCrossChannelPairingForTesting(SlipRaw);
                        engine.ResolveCrossChannelPairingForTesting(lockViper);
                    }
                }
                catch (Exception e) { readerFault = e; }
            });
            reader.Start();
            try
            {
                for (int i = 0; i < 4000; i++)
                    engine.Compute(Braking(1.0), Corners.Zero, Corners.Zero, Game, Car,
                        lockSourceIdentity: i % 2 == 0 ? lockShakeIt : lockViperReal, slipSourceIdentity: slipShakeIt);
            }
            finally
            {
                System.Threading.Volatile.Write(ref stop, true);
                reader.Join();
            }
            Assert.Null(readerFault);

            // No stale pairing after a switch, whatever the reader cached in between.
            engine.Compute(Braking(1.0), Corners.Zero, Corners.Zero, Game, Car, lockSourceIdentity: lockViperReal, slipSourceIdentity: slipShakeIt);
            Assert.Null(engine.ResolveCrossChannelPairingForTesting(slipShakeIt));
            engine.Compute(Braking(1.0), Corners.Zero, Corners.Zero, Game, Car, lockSourceIdentity: lockShakeIt, slipSourceIdentity: slipShakeIt);
            Assert.Equal(lockShakeIt, engine.ResolveCrossChannelPairingForTesting(slipShakeIt));
            Assert.Equal(LockRaw, engine.ResolveCrossChannelPairingForTesting(SlipRaw));
        }

        [Fact]
        public void The_internal_slip_key_borrows_only_from_the_internal_lock_key()
        {
            // Lock driven by Plugin Internal in one engine and by a differently-scaled ShakeIt in the other;
            // Slip is Plugin Internal in both. The internal Slip key's state (ceilings included - its cold
            // anchor borrows Lock evidence) must not depend on which Lock source was selected.
            List<Step> lockSteps = Sequence(braking: true, cycles: 20);
            List<Step> slipSteps = Sequence(braking: false, cycles: 3);
            var lockRaw = new NormalizedWheelLockSlipEngine();
            var lockShakeIt = new NormalizedWheelLockSlipEngine();
            string shakeIt = ShakeItIdentity(true);
            foreach (Step s in lockSteps)
            {
                lockRaw.Compute(s.Sample, s.Raw, Corners.Zero, Game, Car, lockSourceIdentity: LockRaw, slipSourceIdentity: SlipRaw, layer3RawLockWheels: s.Raw);
                lockShakeIt.Compute(s.Sample, s.Active, Corners.Zero, Game, Car, lockSourceIdentity: shakeIt, slipSourceIdentity: SlipRaw, layer3RawLockWheels: s.Raw);
            }
            foreach (Step s in slipSteps)
            {
                lockRaw.Compute(s.Sample, Corners.Zero, s.Raw, Game, Car, lockSourceIdentity: LockRaw, slipSourceIdentity: SlipRaw, layer3RawSlipWheels: s.Raw);
                lockShakeIt.Compute(s.Sample, Corners.Zero, s.Raw, Game, Car, lockSourceIdentity: shakeIt, slipSourceIdentity: SlipRaw, layer3RawSlipWheels: s.Raw);
            }

            Assert.Equal(StateOf(lockRaw, false, SlipRaw), StateOf(lockShakeIt, false, SlipRaw));
        }

        // ---- Custom matching Raw -------------------------------------------------------------------

        private static WheelChannelSettings RawRows(bool lockChannel)
        {
            var c = new WheelChannelSettings
            {
                SourceMode = SourceMode.Custom,
                SourceFrontLeft = RawSourceFallback.PropertyName(lockChannel, 0),
                SourceFrontRight = RawSourceFallback.PropertyName(lockChannel, 1),
                SourceRearLeft = RawSourceFallback.PropertyName(lockChannel, 2),
                SourceRearRight = RawSourceFallback.PropertyName(lockChannel, 3),
                ScriptTypeFrontLeft = ScriptType.Plain,
                ScriptTypeFrontRight = ScriptType.Plain,
                ScriptTypeRearLeft = ScriptType.Plain,
                ScriptTypeRearRight = ScriptType.Plain,
            };
            return c;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_custom_channel_of_exactly_our_raw_rows_is_plugin_internal(bool lockChannel)
        {
            WheelChannelSettings c = RawRows(lockChannel);
            Assert.True(RawSourceFallback.MatchesRaw(c, lockChannel));
            // ...which learns under the internal key: the identity is the same string.
            Assert.Equal(RawSourceFallback.RawIdentity(lockChannel), SourceIdentity.Compute(
                c.SourceFrontLeft, c.ScriptTypeFrontLeft.ToString(), c.SourceFrontRight, c.ScriptTypeFrontRight.ToString(),
                c.SourceRearLeft, c.ScriptTypeRearLeft.ToString(), c.SourceRearRight, c.ScriptTypeRearRight.ToString()));
            // The other channel's Raw rows are not this channel's.
            Assert.False(RawSourceFallback.MatchesRaw(c, !lockChannel));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Any_other_custom_row_is_not_plugin_internal(bool lockChannel)
        {
            WheelChannelSettings scripted = RawRows(lockChannel);
            scripted.ScriptTypeRearRight = ScriptType.JavaScript;
            Assert.False(RawSourceFallback.MatchesRaw(scripted, lockChannel));

            WheelChannelSettings edited = RawRows(lockChannel);
            edited.SourceFrontLeft = "viper4gh.WheelLock.FrontLeft";
            Assert.False(RawSourceFallback.MatchesRaw(edited, lockChannel));

            Assert.False(RawSourceFallback.MatchesRaw(null, lockChannel));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_custom_channel_reproducing_a_shipped_preset_is_that_source(bool lockChannel)
        {
            // Four plain rows of the legacy ShakeIt export names: the canonical ShakeIt identity.
            string shakeItRows = SourceIdentity.Compute(
                Core.MotorsExport.MotorsExportPropertyNames.GetWheelPropertyName(lockChannel, Core.MotorsExport.MotorsExportPropertyNames.FrontLeft), "Plain",
                Core.MotorsExport.MotorsExportPropertyNames.GetWheelPropertyName(lockChannel, Core.MotorsExport.MotorsExportPropertyNames.FrontRight), "Plain",
                Core.MotorsExport.MotorsExportPropertyNames.GetWheelPropertyName(lockChannel, Core.MotorsExport.MotorsExportPropertyNames.RearLeft), "Plain",
                Core.MotorsExport.MotorsExportPropertyNames.GetWheelPropertyName(lockChannel, Core.MotorsExport.MotorsExportPropertyNames.RearRight), "Plain");
            Assert.True(RawSourceFallback.MatchesShippedPreset(shakeItRows, lockChannel));
            // ...also with an algorithm fingerprint (compared on the base identity).
            Assert.True(RawSourceFallback.MatchesShippedPreset(
                Core.ShakeItEffects.ShakeItSourceResolution.ShakeItIdentity(lockChannel, "#alg=legacy:1,sens:50,lockOnly:1"), lockChannel));
            Assert.True(RawSourceFallback.MatchesShippedPreset(RawSourceFallback.RawIdentity(lockChannel), lockChannel));
            Assert.True(RawSourceFallback.MatchesShippedPreset(KnownSourceColdStartReference.ViperSourceIdentity(lockChannel), lockChannel));

            // Not this channel's presets, and not a genuine Custom source.
            Assert.False(RawSourceFallback.MatchesShippedPreset(shakeItRows, !lockChannel));
            Assert.False(RawSourceFallback.MatchesShippedPreset(RawSourceFallback.RawIdentity(!lockChannel), lockChannel));
            Assert.False(RawSourceFallback.MatchesShippedPreset("Plain:Some.Plugin.FL~Plain:Some.Plugin.FR~Plain:Some.Plugin.RL~Plain:Some.Plugin.RR", lockChannel));
            Assert.False(RawSourceFallback.MatchesShippedPreset(null, lockChannel));
        }

        [Fact]
        public void The_custom_cold_start_is_not_applied_to_a_custom_channel_reproducing_a_shipped_preset()
        {
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "QAdvanceFeedback", "QAdvanceFeedback.cs"));
            int start = code.IndexOf("private static void ApplyCustomColdStart", StringComparison.Ordinal);
            string body = code.Substring(start, code.IndexOf("ConfiguredColdStartSMax", start, StringComparison.Ordinal) - start);
            Assert.Contains("!RawSourceFallback.MatchesShippedPreset(sourceIdentity, isLockChannel)", body);
        }

        [Fact]
        public void The_custom_cold_start_is_not_applied_to_a_custom_channel_matching_raw()
        {
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "QAdvanceFeedback", "QAdvanceFeedback.cs"));
            int start = code.IndexOf("private static void ApplyCustomColdStart", StringComparison.Ordinal);
            Assert.True(start >= 0);
            string body = code.Substring(start, code.IndexOf("ConfiguredColdStartSMax", start, StringComparison.Ordinal) - start);
            Assert.Contains("!RawSourceFallback.MatchesRaw(channel, isLockChannel)", body);
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "QAdvanceFeedback", "QAdvanceFeedback.cs")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_engines_internal_identity_is_the_settings_raw_identity(bool lockChannel)
        {
            Assert.Equal(RawSourceFallback.RawIdentity(lockChannel), PluginInternalSourceIdentity.For(lockChannel));
            var engine = new NormalizedWheelLockSlipEngine();
            Assert.Equal(RawSourceFallback.RawIdentity(lockChannel),
                lockChannel ? engine.LockInternalSourceIdentity : engine.SlipInternalSourceIdentity);
        }
    }
}

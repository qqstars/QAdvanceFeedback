using System;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Tests.Frozen;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// BEHAVIOUR-PRESERVATION PROOF for the shared <see cref="CrossingGate"/> extraction (slip-angle plan,
    /// Task 7; docs\slip-angle-design.md section 9.5a). <see cref="SlipCrossingGate"/> and
    /// <see cref="LockCrossingGate"/> are now wrappers over one parameterised <see cref="CrossingGate"/>;
    /// each wrapper - AND a bare <see cref="CrossingGate"/> built from the matching shipped parameter set -
    /// must be BIT-IDENTICAL (exact bit pattern on every double, exact equality on every bool/long, after
    /// every single frame) to the frozen verbatim copies of the pre-extraction gates in
    /// <see cref="FrozenSlipCrossingGate"/> / <see cref="FrozenLockCrossingGate"/>.
    /// <para/>
    /// The randomised streams exercise every branch: genuine crossings (basis rising while G falls),
    /// launches (both rising), plateaus, a source held at / above the ceiling, runaways with a G collapse,
    /// single-frame source jumps (walk-back band), back-to-back crossings, 0-4 saturated wheels, dt in
    /// {1/60, 0, NaN, 1e-9, 0.5, +inf, negative} including long tiny-dt runs that reach HoldMaxFrames,
    /// NaN / infinite / negative basis and G, non-finite and above-ceiling fallbacks, low / zero / negative
    /// G, Reset mid-stream, candidate counting on several keys (null included), and every tunable both at
    /// its default and at a random non-default value (including disabled and out-of-range settings).
    /// </summary>
    public class CrossingGateEquivalenceTests
    {
        private const int Seeds = 200;
        private const int FramesPerSeed = 3000;

        private static readonly double[] Confidences = { 0.0, 0.37, 1.0, double.NaN, -0.5, double.PositiveInfinity, 2.0 };
        private static readonly double[] ProbeBases = { double.NaN, 150.0, 100.0000000001, 0.0, -3.0, double.PositiveInfinity, 42.5 };
        private static readonly string[] Keys = { "Configured", "RawFallback", null, "Unused" };

        // ---------------------------------------------------------------------------------------------
        // Defaults
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void SlipShipped_and_LockShipped_pin_the_pre_extraction_constants()
        {
            var s = CrossingGateParameters.SlipShipped;
            Assert.Equal(FrozenSlipCrossingGate.TrendFrames, s.TrendFrames);
            Assert.Equal(FrozenSlipCrossingGate.SourceCeiling, s.SourceCeiling);
            Assert.Equal(FrozenSlipCrossingGate.MinFrameRise, s.MinFrameRise);
            Assert.Equal(FrozenSlipCrossingGate.SlipRise, s.Rise);
            Assert.Equal(FrozenSlipCrossingGate.GFall, s.GFall);
            Assert.Equal(FrozenSlipCrossingGate.HoldSeconds, s.HoldSeconds);
            Assert.Equal(FrozenSlipCrossingGate.NominalFrameSeconds, s.NominalFrameSeconds);
            Assert.Equal(FrozenSlipCrossingGate.HoldMaxFrames, s.HoldMaxFrames);
            Assert.Equal(FrozenSlipCrossingGate.MinGForCollapseTest, s.MinGForCollapseTest);
            Assert.Equal(TeachingWeightMode.Fixed, s.WeightMode);

            var l = CrossingGateParameters.LockShipped;
            Assert.Equal(FrozenLockCrossingGate.TrendFrames, l.TrendFrames);
            Assert.Equal(FrozenLockCrossingGate.SourceCeiling, l.SourceCeiling);
            Assert.Equal(FrozenLockCrossingGate.MinFrameRise, l.MinFrameRise);
            Assert.Equal(FrozenLockCrossingGate.LockRise, l.Rise);
            Assert.Equal(FrozenLockCrossingGate.GFall, l.GFall);
            Assert.Equal(FrozenLockCrossingGate.HoldSeconds, l.HoldSeconds);
            Assert.Equal(FrozenLockCrossingGate.NominalFrameSeconds, l.NominalFrameSeconds);
            Assert.Equal(FrozenLockCrossingGate.HoldMaxFrames, l.HoldMaxFrames);
            Assert.Equal(FrozenLockCrossingGate.MinGForCollapseTest, l.MinGForCollapseTest);
            Assert.Equal(TeachingWeightMode.MultiplyConfidence, l.WeightMode);

            // The literal values from the brief, so the frozen copy cannot drift with them.
            foreach (var p in new[] { s, l })
            {
                Assert.Equal(5, p.TrendFrames);
                Assert.Equal(100.0, p.SourceCeiling);
                Assert.Equal(1e-6, p.MinFrameRise);
                Assert.Equal(1.0, p.Rise);
                Assert.Equal(-0.05, p.GFall);
                Assert.Equal(1.0, p.HoldSeconds);
                Assert.Equal(1.0 / 60.0, p.NominalFrameSeconds);
                Assert.Equal(500, p.HoldMaxFrames);
                Assert.Equal(0.05, p.MinGForCollapseTest);
            }

            // Each getter hands out a fresh object, so a caller mutating one cannot retune the shipped set.
            Assert.NotSame(CrossingGateParameters.SlipShipped, CrossingGateParameters.SlipShipped);
            Assert.NotSame(CrossingGateParameters.LockShipped, CrossingGateParameters.LockShipped);
        }

        [Fact]
        public void SlipAngle_parameters_are_the_briefed_values()
        {
            var p = CrossingGateParameters.SlipAngle;
            Assert.Equal(5, p.TrendFrames);
            Assert.Equal(45.0, p.SourceCeiling);
            Assert.Equal(1e-6, p.MinFrameRise);
            Assert.Equal(0.5, p.Rise);
            Assert.Equal(-0.05, p.GFall);
            Assert.Equal(1.0, p.HoldSeconds);
            Assert.Equal(1.0 / 60.0, p.NominalFrameSeconds);
            Assert.Equal(500, p.HoldMaxFrames);
            Assert.Equal(0.05, p.MinGForCollapseTest);
            Assert.Equal(TeachingWeightMode.Fixed, p.WeightMode);
        }

        [Fact]
        public void Wrapper_public_constants_and_tunable_defaults_are_unchanged()
        {
            Assert.Equal(FrozenSlipCrossingGate.TrendFrames, SlipCrossingGate.TrendFrames);
            Assert.Equal(FrozenSlipCrossingGate.SourceCeiling, SlipCrossingGate.SourceCeiling);
            Assert.Equal(FrozenSlipCrossingGate.MinFrameRise, SlipCrossingGate.MinFrameRise);
            Assert.Equal(FrozenSlipCrossingGate.DefaultMaxGCollapseFractionPerSecond, SlipCrossingGate.DefaultMaxGCollapseFractionPerSecond);
            Assert.Equal(FrozenSlipCrossingGate.MinGForCollapseTest, SlipCrossingGate.MinGForCollapseTest);
            Assert.Equal(FrozenSlipCrossingGate.SlipRise, SlipCrossingGate.SlipRise);
            Assert.Equal(FrozenSlipCrossingGate.GFall, SlipCrossingGate.GFall);
            Assert.Equal(FrozenSlipCrossingGate.HoldSeconds, SlipCrossingGate.HoldSeconds);
            Assert.Equal(FrozenSlipCrossingGate.DefaultNonCrossingWeight, SlipCrossingGate.DefaultNonCrossingWeight);
            Assert.Equal(FrozenSlipCrossingGate.NominalFrameSeconds, SlipCrossingGate.NominalFrameSeconds);
            Assert.Equal(FrozenSlipCrossingGate.HoldMaxFrames, SlipCrossingGate.HoldMaxFrames);
            Assert.Equal(FrozenSlipCrossingGate.DefaultMinSaturatedWheelsForRejection, SlipCrossingGate.DefaultMinSaturatedWheelsForRejection);
            Assert.Equal(FrozenSlipCrossingGate.SaturatedWheelThreshold, SlipCrossingGate.SaturatedWheelThreshold);
            Assert.Equal(FrozenSlipCrossingGate.DefaultWalkBackGBandFraction, SlipCrossingGate.DefaultWalkBackGBandFraction);

            Assert.Equal(FrozenLockCrossingGate.TrendFrames, LockCrossingGate.TrendFrames);
            Assert.Equal(FrozenLockCrossingGate.SourceCeiling, LockCrossingGate.SourceCeiling);
            Assert.Equal(FrozenLockCrossingGate.MinFrameRise, LockCrossingGate.MinFrameRise);
            Assert.Equal(FrozenLockCrossingGate.DefaultMaxGCollapseFractionPerSecond, LockCrossingGate.DefaultMaxGCollapseFractionPerSecond);
            Assert.Equal(FrozenLockCrossingGate.MinGForCollapseTest, LockCrossingGate.MinGForCollapseTest);
            Assert.Equal(FrozenLockCrossingGate.DefaultMinSaturatedWheelsForRejection, LockCrossingGate.DefaultMinSaturatedWheelsForRejection);
            Assert.Equal(FrozenLockCrossingGate.SaturatedWheelThreshold, LockCrossingGate.SaturatedWheelThreshold);
            Assert.Equal(FrozenLockCrossingGate.DefaultWalkBackGBandFraction, LockCrossingGate.DefaultWalkBackGBandFraction);
            Assert.Equal(FrozenLockCrossingGate.LockRise, LockCrossingGate.LockRise);
            Assert.Equal(FrozenLockCrossingGate.GFall, LockCrossingGate.GFall);
            Assert.Equal(FrozenLockCrossingGate.HoldSeconds, LockCrossingGate.HoldSeconds);
            Assert.Equal(FrozenLockCrossingGate.DefaultNonCrossingWeightFactor, LockCrossingGate.DefaultNonCrossingWeightFactor);
            Assert.Equal(FrozenLockCrossingGate.NominalFrameSeconds, LockCrossingGate.NominalFrameSeconds);
            Assert.Equal(FrozenLockCrossingGate.HoldMaxFrames, LockCrossingGate.HoldMaxFrames);

            var fs = new FrozenSlipCrossingGate();
            var ws = new SlipCrossingGate();
            Assert.Equal(fs.MaxGCollapseFractionPerSecond, ws.MaxGCollapseFractionPerSecond);
            Assert.Equal(fs.MinSaturatedWheelsForRejection, ws.MinSaturatedWheelsForRejection);
            Assert.Equal(fs.WalkBackGBandFraction, ws.WalkBackGBandFraction);
            Assert.Equal(fs.NonCrossingWeight, ws.NonCrossingWeight);

            var fl = new FrozenLockCrossingGate();
            var wl = new LockCrossingGate();
            Assert.Equal(fl.MaxGCollapseFractionPerSecond, wl.MaxGCollapseFractionPerSecond);
            Assert.Equal(fl.MinSaturatedWheelsForRejection, wl.MinSaturatedWheelsForRejection);
            Assert.Equal(fl.WalkBackGBandFraction, wl.WalkBackGBandFraction);
            Assert.Equal(fl.NonCrossingWeightFactor, wl.NonCrossingWeightFactor);

            // A bare shared gate starts with the same tunables as both shipped gates.
            var bare = new CrossingGate(CrossingGateParameters.SlipShipped);
            Assert.Equal(fs.MaxGCollapseFractionPerSecond, bare.MaxGCollapseFractionPerSecond);
            Assert.Equal(fs.MinSaturatedWheelsForRejection, bare.MinSaturatedWheelsForRejection);
            Assert.Equal(fs.WalkBackGBandFraction, bare.WalkBackGBandFraction);
            Assert.Equal(fs.NonCrossingWeight, bare.NonCrossingWeight);
        }

        [Fact]
        public void Constructor_rejects_null_parameters()
        {
            Assert.Throws<ArgumentNullException>(() => new CrossingGate(null));
        }

        [Fact]
        public void Gate_copies_its_parameters_so_later_mutation_does_not_retune_it()
        {
            var p = CrossingGateParameters.SlipShipped;
            var gate = new CrossingGate(p);
            var frozen = new FrozenSlipCrossingGate();
            p.Rise = 1000.0;
            p.HoldSeconds = 0.0;
            var rng = new Random(7);
            var state = new StreamState();
            for (int i = 0; i < FramesPerSeed; i++)
            {
                Frame f = NextFrame(rng, i, state);
                Assert.Equal(frozen.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated),
                             gate.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated));
                Assert.Equal(frozen.Qualified, gate.Qualified);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // The bit-identical proof
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void Slip_and_Lock_wrappers_are_bit_identical_to_frozen_gates()
        {
            long slipFrames = 0, lockFrames = 0, slipCrossings = 0, lockCrossings = 0;
            for (int seed = 0; seed < Seeds; seed++)
            {
                RunSlip(seed, ref slipFrames, ref slipCrossings);
                RunLock(seed, ref lockFrames, ref lockCrossings);
            }

            Assert.Equal((long)Seeds * FramesPerSeed, slipFrames);
            Assert.Equal((long)Seeds * FramesPerSeed, lockFrames);
            // The streams must actually exercise the arming path, or the proof is vacuous.
            Assert.True(slipCrossings > Seeds * 5, $"only {slipCrossings} Slip crossings");
            Assert.True(lockCrossings > Seeds * 5, $"only {lockCrossings} Lock crossings");
        }

        /// <summary>Random doubles essentially never land EXACTLY on a threshold, so the strict-versus-
        /// inclusive comparisons are pinned here with scripted frames that hit each boundary exactly: a
        /// G-collapse rate equal to the limit, a previous G equal to MinGForCollapseTest, a trend rise equal
        /// to Rise, a frame rise equal to MinFrameRise, a saturated count equal to the guard, and a walk-back
        /// G exactly on the band edge and exactly equal to the best G.</summary>
        [Fact]
        public void Bit_identical_on_exact_threshold_boundaries()
        {
            const double dt = 1.0 / 60.0;
            // (basis, G) ramps ending in a crossing; the last frame is varied per scenario.
            double[] basis = { 10.0, 10.25, 10.5, 10.75, 10.875, 0.0 };
            double[] g = { 1.0, 1.0, 1.0, 1.0, 1.0, 0.5 };

            for (int scenario = 0; scenario < 8; scenario++)
            {
                var fs = new FrozenSlipCrossingGate();
                var ws = new SlipCrossingGate();
                var bs = new CrossingGate(CrossingGateParameters.SlipShipped);
                var fl = new FrozenLockCrossingGate();
                var wl = new LockCrossingGate();
                var bl = new CrossingGate(CrossingGateParameters.LockShipped);

                double lastBasis = 20.0, lastG = 0.5, previousG = 1.0;
                int saturated = 0;
                double[] gs = (double[])g.Clone();
                double[] bases = (double[])basis.Clone();
                double collapse = 0.0, walk = SlipCrossingGate.DefaultWalkBackGBandFraction;
                switch (scenario)
                {
                    case 0: collapse = ((previousG - lastG) / previousG) / dt; break;               // rate == limit
                    case 1: gs[4] = 0.05; lastG = 0.0; collapse = (0.05 / 0.05) / dt; break;        // prev G == MinG
                    case 2: lastBasis = 11.0; break;                                               // rise == Rise
                    case 3: lastBasis = 10.875 + 1e-6; bases[0] = lastBasis - 5.0; break;          // frame rise ~ MinFrameRise
                    case 4: saturated = SlipCrossingGate.DefaultMinSaturatedWheelsForRejection; break; // count == guard
                    case 5: saturated = SlipCrossingGate.DefaultMinSaturatedWheelsForRejection - 1; break;
                    case 6: gs[4] = 0.5625; walk = 0.125; lastG = 0.5; break;                      // |dG| == band edge (exact binary)
                    default: gs[3] = 0.6; gs[4] = 0.6; walk = 0.5; break;                          // equal best G
                }
                bases[5] = lastBasis;
                gs[5] = lastG;

                foreach (var x in new object[] { fs, ws, bs, fl, wl, bl }) SetTunables(x, collapse, walk);
                for (int i = 0; i < bases.Length; i++)
                {
                    int sat = i == bases.Length - 1 ? saturated : 0;
                    Assert.Equal(fs.Observe(bases[i], bases[i] * 0.5, gs[i], true, dt, sat), ws.Observe(bases[i], bases[i] * 0.5, gs[i], true, dt, sat));
                    bs.Observe(bases[i], bases[i] * 0.5, gs[i], true, dt, sat);
                    fl.Observe(bases[i], bases[i] * 0.5, gs[i], true, dt, sat);
                    wl.Observe(bases[i], bases[i] * 0.5, gs[i], true, dt, sat);
                    bl.Observe(bases[i], bases[i] * 0.5, gs[i], true, dt, sat);
                    AssertSlipSame(fs, ws, bs, scenario, i, bases[i], bases[i] * 0.5);
                    AssertLockSame(fl, wl, bl, scenario, i, bases[i], bases[i] * 0.5);
                }
            }
        }

        private static void SetTunables(object gate, double collapse, double walk)
        {
            switch (gate)
            {
                case FrozenSlipCrossingGate f: f.MaxGCollapseFractionPerSecond = collapse; f.WalkBackGBandFraction = walk; break;
                case SlipCrossingGate w: w.MaxGCollapseFractionPerSecond = collapse; w.WalkBackGBandFraction = walk; break;
                case FrozenLockCrossingGate f: f.MaxGCollapseFractionPerSecond = collapse; f.WalkBackGBandFraction = walk; break;
                case LockCrossingGate w: w.MaxGCollapseFractionPerSecond = collapse; w.WalkBackGBandFraction = walk; break;
                case CrossingGate b: b.MaxGCollapseFractionPerSecond = collapse; b.WalkBackGBandFraction = walk; break;
            }
        }

        private static void RunSlip(int seed, ref long frames, ref long crossings)
        {
            var rng = new Random(1_000 + seed);
            var frozen = new FrozenSlipCrossingGate();
            var wrapper = new SlipCrossingGate();
            var bare = new CrossingGate(CrossingGateParameters.SlipShipped);
            var state = new StreamState();

            if (seed % 2 == 1) RandomiseSlipTunables(rng, frozen, wrapper, bare);
            AssertSlipSame(frozen, wrapper, bare, seed, -1, 50.0, 50.0);

            for (int i = 0; i < FramesPerSeed; i++)
            {
                if (rng.Next(1000) == 0) RandomiseSlipTunables(rng, frozen, wrapper, bare);
                if (rng.Next(400) == 0) { frozen.Reset(); wrapper.Reset(); bare.Reset(); }

                Frame f = NextFrame(rng, i, state);
                bool rf = frozen.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated);
                bool rw = wrapper.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated);
                bool rb = bare.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated);
                if (rf != rw || rf != rb) Assert.Fail($"Slip seed {seed} frame {i}: Observe returned {rf}/{rw}/{rb}");
                if (frozen.CrossedThisFrame) crossings++;

                RecordCandidates(rng, rf, (k, q) => frozen.RecordCandidate(k, q), (k, q) => wrapper.RecordCandidate(k, q),
                    (k, q) => bare.RecordCandidate(k, q));

                AssertSlipSame(frozen, wrapper, bare, seed, i, f.Basis, f.Fallback);
                frames++;
            }
        }

        private static void RunLock(int seed, ref long frames, ref long crossings)
        {
            var rng = new Random(2_000_000 + seed);
            var frozen = new FrozenLockCrossingGate();
            var wrapper = new LockCrossingGate();
            var bare = new CrossingGate(CrossingGateParameters.LockShipped);
            var state = new StreamState();

            if (seed % 2 == 1) RandomiseLockTunables(rng, frozen, wrapper, bare);
            AssertLockSame(frozen, wrapper, bare, seed, -1, 50.0, 50.0);

            for (int i = 0; i < FramesPerSeed; i++)
            {
                if (rng.Next(1000) == 0) RandomiseLockTunables(rng, frozen, wrapper, bare);
                if (rng.Next(400) == 0) { frozen.Reset(); wrapper.Reset(); bare.Reset(); }

                Frame f = NextFrame(rng, i, state);
                bool rf = frozen.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated);
                bool rw = wrapper.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated);
                bool rb = bare.Observe(f.Basis, f.Fallback, f.G, f.AtLimit, f.Dt, f.Saturated);
                if (rf != rw || rf != rb) Assert.Fail($"Lock seed {seed} frame {i}: Observe returned {rf}/{rw}/{rb}");
                if (frozen.CrossedThisFrame) crossings++;

                RecordCandidates(rng, rf, (k, q) => frozen.RecordCandidate(k, q), (k, q) => wrapper.RecordCandidate(k, q),
                    (k, q) => bare.RecordCandidate(k, q));

                AssertLockSame(frozen, wrapper, bare, seed, i, f.Basis, f.Fallback);
                frames++;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Observables
        // ---------------------------------------------------------------------------------------------

        private static void AssertSlipSame(FrozenSlipCrossingGate f, SlipCrossingGate w, CrossingGate b, int seed, int i,
            double liveBasis, double liveFallback)
        {
            string at = $"Slip seed {seed} frame {i}";
            Same(f.Qualified, w.Qualified, b.Qualified, at + " Qualified");
            Same(f.CrossedThisFrame, w.CrossedThisFrame, b.CrossedThisFrame, at + " CrossedThisFrame");
            foreach (double c in Confidences)
                Bits(f.TeachingWeight, w.TeachingWeight, b.TeachingWeight(c), at + $" TeachingWeight(c={c})");
            Bits(f.TeachingWeight, w.TeachingWeight, b.TeachingWeight(), at + " TeachingWeight()");
            bool q = w.Qualified;
            Taught(f.TeachingBasis(liveBasis), w.TeachingBasis(liveBasis), b.TeachingBasis(liveBasis), q, at + " TeachingBasis(live)");
            Taught(f.TeachingFallbackBasis(liveFallback), w.TeachingFallbackBasis(liveFallback), b.TeachingFallbackBasis(liveFallback), q, at + " TeachingFallbackBasis(live)");
            foreach (double x in ProbeBases)
            {
                Taught(f.TeachingBasis(x), w.TeachingBasis(x), b.TeachingBasis(x), q, at + $" TeachingBasis({x})");
                Taught(f.TeachingFallbackBasis(x), w.TeachingFallbackBasis(x), b.TeachingFallbackBasis(x), q, at + $" TeachingFallbackBasis({x})");
            }
            foreach (string k in Keys)
            {
                Same(f.ValidFrames(k), w.ValidFrames(k), b.ValidFrames(k), at + $" ValidFrames({k})");
                Same(f.TotalFrames(k), w.TotalFrames(k), b.TotalFrames(k), at + $" TotalFrames({k})");
            }
            Bits(f.NonCrossingWeight, w.NonCrossingWeight, b.NonCrossingWeight, at + " NonCrossingWeight");
            Bits(f.MaxGCollapseFractionPerSecond, w.MaxGCollapseFractionPerSecond, b.MaxGCollapseFractionPerSecond, at + " MaxGCollapse");
            Same(f.MinSaturatedWheelsForRejection, w.MinSaturatedWheelsForRejection, b.MinSaturatedWheelsForRejection, at + " MinSaturated");
            Bits(f.WalkBackGBandFraction, w.WalkBackGBandFraction, b.WalkBackGBandFraction, at + " WalkBack");
        }

        private static void AssertLockSame(FrozenLockCrossingGate f, LockCrossingGate w, CrossingGate b, int seed, int i,
            double liveBasis, double liveFallback)
        {
            string at = $"Lock seed {seed} frame {i}";
            Same(f.Qualified, w.Qualified, b.Qualified, at + " Qualified");
            Same(f.CrossedThisFrame, w.CrossedThisFrame, b.CrossedThisFrame, at + " CrossedThisFrame");
            foreach (double c in Confidences)
                Bits(f.TeachingWeight(c), w.TeachingWeight(c), b.TeachingWeight(c), at + $" TeachingWeight(c={c})");
            bool q = w.Qualified;
            Taught(f.TeachingBasis(liveBasis), w.TeachingBasis(liveBasis), b.TeachingBasis(liveBasis), q, at + " TeachingBasis(live)");
            Taught(f.TeachingFallbackBasis(liveFallback), w.TeachingFallbackBasis(liveFallback), b.TeachingFallbackBasis(liveFallback), q, at + " TeachingFallbackBasis(live)");
            foreach (double x in ProbeBases)
            {
                Taught(f.TeachingBasis(x), w.TeachingBasis(x), b.TeachingBasis(x), q, at + $" TeachingBasis({x})");
                Taught(f.TeachingFallbackBasis(x), w.TeachingFallbackBasis(x), b.TeachingFallbackBasis(x), q, at + $" TeachingFallbackBasis({x})");
            }
            foreach (string k in Keys)
            {
                Same(f.ValidFrames(k), w.ValidFrames(k), b.ValidFrames(k), at + $" ValidFrames({k})");
                Same(f.TotalFrames(k), w.TotalFrames(k), b.TotalFrames(k), at + $" TotalFrames({k})");
            }
            Bits(f.NonCrossingWeightFactor, w.NonCrossingWeightFactor, b.NonCrossingWeight, at + " NonCrossingWeightFactor");
            Bits(f.MaxGCollapseFractionPerSecond, w.MaxGCollapseFractionPerSecond, b.MaxGCollapseFractionPerSecond, at + " MaxGCollapse");
            Same(f.MinSaturatedWheelsForRejection, w.MinSaturatedWheelsForRejection, b.MinSaturatedWheelsForRejection, at + " MinSaturated");
            Bits(f.WalkBackGBandFraction, w.WalkBackGBandFraction, b.WalkBackGBandFraction, at + " WalkBack");
        }

        /// <summary>Exact BIT-PATTERN equality - stricter than <c>==</c> (distinguishes -0.0 from 0.0) and,
        /// unlike <c>==</c>, treats NaN as equal to the same NaN, which a NaN probe basis legitimately
        /// returns.</summary>
        private static void Bits(double frozen, double wrapper, double bare, string what)
        {
            long f = BitConverter.DoubleToInt64Bits(frozen);
            if (f == BitConverter.DoubleToInt64Bits(wrapper) && f == BitConverter.DoubleToInt64Bits(bare)) return;
            Assert.Fail($"{what}: frozen {frozen:R} wrapper {wrapper:R} bare {bare:R}");
        }

        /// <summary>
        /// THE TAUGHT BASIS - a DELIBERATE divergence from the frozen oracles (SMax 100/90/70 fix,
        /// 2026-11-19; the oracles stay untouched as the pre-fix record, as their header requires).
        /// Wrapper and bare shared gate stay bit-identical. Outside a window the only change is the learn
        /// ceiling (the live value is capped at 95 instead of 100). Inside a window the snapshot itself is
        /// new - the walk-back stays inside the rise and never falls back to the live value - and is pinned
        /// by CrossingGateSmaxFixTests and CrossingGateWalkBackTests instead. Window OPENING is unchanged
        /// (every snapshot the fixed walk can pick is a rising frame, so it is always usable), which is why
        /// Qualified, CrossedThisFrame and every TeachingWeight above are still compared bit for bit.
        /// </summary>
        private static void Taught(double frozen, double wrapper, double bare, bool qualified, string what)
        {
            long w = BitConverter.DoubleToInt64Bits(wrapper);
            if (w != BitConverter.DoubleToInt64Bits(bare))
                Assert.Fail($"{what}: wrapper {wrapper:R} bare {bare:R}");
            if (qualified) return;
            double expected = !double.IsNaN(frozen) && !double.IsInfinity(frozen) && frozen > LockCrossingGate.LearnCeiling ? LockCrossingGate.LearnCeiling : frozen;
            if (w != BitConverter.DoubleToInt64Bits(expected))
                Assert.Fail($"{what}: frozen {frozen:R} (capped {expected:R}) wrapper {wrapper:R}");
        }

        private static void Same<T>(T frozen, T wrapper, T bare, string what) where T : IEquatable<T>
        {
            if (frozen.Equals(wrapper) && frozen.Equals(bare)) return;
            Assert.Fail($"{what}: frozen {frozen} wrapper {wrapper} bare {bare}");
        }

        // ---------------------------------------------------------------------------------------------
        // Tunables
        // ---------------------------------------------------------------------------------------------

        private static void RandomiseSlipTunables(Random rng, FrozenSlipCrossingGate f, SlipCrossingGate w, CrossingGate b)
        {
            double collapse = RandomCollapse(rng), walk = RandomWalkBack(rng), weight = RandomWeight(rng);
            int sat = RandomSaturation(rng);
            f.MaxGCollapseFractionPerSecond = w.MaxGCollapseFractionPerSecond = b.MaxGCollapseFractionPerSecond = collapse;
            f.MinSaturatedWheelsForRejection = w.MinSaturatedWheelsForRejection = b.MinSaturatedWheelsForRejection = sat;
            f.WalkBackGBandFraction = w.WalkBackGBandFraction = b.WalkBackGBandFraction = walk;
            f.NonCrossingWeight = weight;
            w.NonCrossingWeight = weight;
            b.NonCrossingWeight = weight;
        }

        private static void RandomiseLockTunables(Random rng, FrozenLockCrossingGate f, LockCrossingGate w, CrossingGate b)
        {
            double collapse = RandomCollapse(rng), walk = RandomWalkBack(rng), weight = RandomWeight(rng);
            int sat = RandomSaturation(rng);
            f.MaxGCollapseFractionPerSecond = w.MaxGCollapseFractionPerSecond = b.MaxGCollapseFractionPerSecond = collapse;
            f.MinSaturatedWheelsForRejection = w.MinSaturatedWheelsForRejection = b.MinSaturatedWheelsForRejection = sat;
            f.WalkBackGBandFraction = w.WalkBackGBandFraction = b.WalkBackGBandFraction = walk;
            f.NonCrossingWeightFactor = weight;
            w.NonCrossingWeightFactor = weight;
            b.NonCrossingWeight = weight;
        }

        private static double RandomCollapse(Random rng)
        {
            switch (rng.Next(5))
            {
                case 0: return 0.0;
                case 1: return -1.0;
                case 2: return 15.0;
                default: return 0.5 + rng.NextDouble() * 40.0;
            }
        }

        private static double RandomWalkBack(Random rng)
        {
            switch (rng.Next(5))
            {
                case 0: return 0.0;
                case 1: return -0.2;
                case 2: return 0.10;
                default: return rng.NextDouble() * 0.6;
            }
        }

        private static double RandomWeight(Random rng)
        {
            switch (rng.Next(6))
            {
                case 0: return 0.0;
                case 1: return 1.0;
                case 2: return -0.3;
                case 3: return 1.7;
                case 4: return double.NaN;
                default: return rng.NextDouble();
            }
        }

        private static int RandomSaturation(Random rng) => rng.Next(-1, 6);

        private static void RecordCandidates(Random rng, bool observed, Action<string, bool> a, Action<string, bool> b, Action<string, bool> c)
        {
            int n = rng.Next(3);
            for (int k = 0; k < n; k++)
            {
                string key = Keys[rng.Next(3)];
                bool qualified = rng.Next(4) == 0 ? !observed : observed;
                a(key, qualified);
                b(key, qualified);
                c(key, qualified);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Stream generator
        // ---------------------------------------------------------------------------------------------

        private struct Frame
        {
            public double Basis, Fallback, G, Dt;
            public bool AtLimit;
            public int Saturated;
        }

        private sealed class StreamState
        {
            public int Kind = -1;
            public int Remaining;
            public double Basis = 20.0;
            public double G = 1.0;
            public double BasisStep;
            public double GStep;
            public int DtMode; // 0 normal, 1 all zero, 2 all tiny, 3 mixed
        }

        private static Frame NextFrame(Random rng, int index, StreamState s)
        {
            if (s.Remaining <= 0)
            {
                s.Kind = rng.Next(100);
                s.Remaining = 3 + rng.Next(30);
                s.BasisStep = 0.5 + rng.NextDouble() * 8.0;
                s.GStep = 0.01 + rng.NextDouble() * 0.12;
                int dtPick = rng.Next(20);
                s.DtMode = dtPick < 14 ? 0 : dtPick < 16 ? 1 : dtPick < 18 ? 2 : 3;
                if (s.DtMode == 1 || s.DtMode == 2) s.Remaining = 100 + rng.Next(600); // long enough to hit HoldMaxFrames
                if (s.Kind < 30)
                {
                    // A crossing starts from a gripping state.
                    s.Basis = 5.0 + rng.NextDouble() * 60.0;
                    s.G = 0.3 + rng.NextDouble() * 3.0;
                }
            }
            s.Remaining--;

            var f = new Frame { AtLimit = rng.Next(100) < 75, Saturated = 0 };

            if (s.Kind < 30)
            {
                // CROSSING: basis rising while G falls - and, with some noise, back-to-back.
                s.Basis += s.BasisStep * (0.5 + rng.NextDouble());
                s.G -= s.GStep * (0.5 + rng.NextDouble());
                f.AtLimit = rng.Next(100) < 90;
                f.Saturated = rng.Next(100) < 70 ? 0 : rng.Next(5);
            }
            else if (s.Kind < 42)
            {
                // LAUNCH / grip build: both rising.
                s.Basis += s.BasisStep * rng.NextDouble();
                s.G += s.GStep * rng.NextDouble();
            }
            else if (s.Kind < 54)
            {
                // PLATEAU with noise (including exact repeats).
                if (rng.Next(3) != 0)
                {
                    s.Basis += (rng.NextDouble() - 0.5) * 2.0;
                    s.G += (rng.NextDouble() - 0.5) * 0.04;
                }
            }
            else if (s.Kind < 62)
            {
                // HELD AT / ABOVE THE CEILING while G falls.
                switch (rng.Next(4))
                {
                    case 0: s.Basis = 100.0; break;
                    case 1: s.Basis = 100.0000000001; break;
                    case 2: s.Basis = 150.0; break;
                    default: s.Basis = 99.9 + rng.NextDouble() * 0.2; break;
                }
                s.G -= s.GStep;
                f.Saturated = rng.Next(5);
            }
            else if (s.Kind < 70)
            {
                // RUNAWAY with a G collapse (upshift / lift / airborne).
                s.Basis += 10.0 + rng.NextDouble() * 30.0;
                s.G *= 0.2 + rng.NextDouble() * 0.6;
                f.Saturated = rng.Next(5);
            }
            else if (s.Kind < 76)
            {
                // SINGLE-FRAME SOURCE JUMP (the walk-back band case), then rising.
                s.Basis = rng.Next(4) == 0 ? 10.0 + rng.NextDouble() * 10.0 : s.Basis + 30.0 + rng.NextDouble() * 40.0;
                s.G -= s.GStep * 0.5;
            }
            else if (s.Kind < 82)
            {
                // LOW / ZERO / NEGATIVE G.
                s.Basis += s.BasisStep * (rng.NextDouble() - 0.3);
                switch (rng.Next(4))
                {
                    case 0: s.G = 0.0; break;
                    case 1: s.G = -0.2 * rng.NextDouble(); break;
                    case 2: s.G = 0.05; break;
                    default: s.G = rng.NextDouble() * 0.1; break;
                }
            }
            else if (s.Kind < 88)
            {
                // BASIS FALLING while G recovers.
                s.Basis -= s.BasisStep;
                s.G += s.GStep;
            }
            else
            {
                // Random walk, wide.
                s.Basis += (rng.NextDouble() - 0.45) * 15.0;
                s.G += (rng.NextDouble() - 0.5) * 0.3;
            }

            if (s.Basis > 160.0) s.Basis = 20.0 + rng.NextDouble() * 40.0;
            if (s.Basis < -5.0) s.Basis = rng.NextDouble() * 10.0;
            if (s.G > 6.0) s.G = 1.0 + rng.NextDouble();
            if (s.G < -1.0) s.G = 0.5 + rng.NextDouble();

            f.Basis = s.Basis;
            f.G = s.G;
            f.Fallback = s.Basis * (0.6 + rng.NextDouble() * 0.8);

            // Invalid telemetry on a few frames.
            int bad = rng.Next(100);
            if (bad < 2) f.Basis = rng.Next(2) == 0 ? double.NaN : double.PositiveInfinity;
            else if (bad < 3) f.Basis = -rng.NextDouble() * 5.0;
            else if (bad < 4) f.G = rng.Next(2) == 0 ? double.NaN : double.NegativeInfinity;
            else if (bad < 5) f.Basis = double.NegativeInfinity;
            int badFallback = rng.Next(100);
            if (badFallback < 3) f.Fallback = double.NaN;
            else if (badFallback < 5) f.Fallback = double.PositiveInfinity;
            else if (badFallback < 8) f.Fallback = 100.0 + rng.NextDouble() * 50.0;
            else if (badFallback < 10) f.Fallback = rng.Next(2) == 0 ? 0.0 : -rng.NextDouble();

            f.Dt = NextDt(rng, s.DtMode);
            if (rng.Next(50) == 0) f.Saturated = rng.Next(2) == 0 ? -1 : 6;
            return f;
        }

        private static double NextDt(Random rng, int mode)
        {
            switch (mode)
            {
                case 1: return 0.0;
                case 2: return 1e-9;
                case 3:
                    switch (rng.Next(8))
                    {
                        case 0: return 0.0;
                        case 1: return double.NaN;
                        case 2: return 1e-9;
                        case 3: return 0.5;
                        case 4: return double.PositiveInfinity;
                        case 5: return -1.0 / 60.0;
                        case 6: return 1.0 / 30.0;
                        default: return 1.0 / 60.0;
                    }
                default:
                    int r = rng.Next(100);
                    if (r < 2) return 0.0;
                    if (r < 3) return double.NaN;
                    if (r < 4) return 1e-9;
                    if (r < 5) return 0.5;
                    return 1.0 / 60.0;
            }
        }
    }
}

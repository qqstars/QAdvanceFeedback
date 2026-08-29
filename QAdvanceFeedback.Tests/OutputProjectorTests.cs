using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Ported from the sibling ReliableWheelLockSlip project's
    /// ReliableWheelLockSlip.Tests/OutputProjectorTests.cs, per the brief ("port the sibling
    /// project's output projector wholesale ... port the tests too"). Only the namespace and the
    /// channel enum changed: <c>SlipDirection.Braking/Traction</c> -&gt;
    /// <see cref="ProjectionChannel.Lock"/>/<see cref="ProjectionChannel.Slip"/>, matching this
    /// plugin's own WheelLock/WheelSlip terminology - see <see cref="ProjectionChannel"/>'s remarks.</summary>
    public class OutputProjectorTests
    {
        private static ProjectorSettings Defaults() => new ProjectorSettings();

        private static ProjectorSettings LinearSettings()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Linear);
            return s;
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(25.0)]
        [InlineData(50.0)]
        [InlineData(75.0)]
        [InlineData(100.0)]
        public void Linear_is_identity(double x)
        {
            var projector = new OutputProjector(LinearSettings());
            Assert.Equal(x, projector.Project(x), 9);
        }

        [Fact]
        public void Linear_yields_exact_anchor_positions_as_outputs()
        {
            var projector = new OutputProjector(LinearSettings());
            Assert.Equal(30.0, projector.Project(30.0), 9);
            Assert.Equal(60.0, projector.Project(60.0), 9);
            Assert.Equal(80.0, projector.Project(80.0), 9);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void ApplyPreset_linear_preserves_the_start_instead_of_overwriting_it(ProjectionChannel channel)
        {
            var s = Defaults();
            s.StartInput = 35.0;
            s.ApplyPreset(ProjectorPreset.Linear, channel);

            Assert.Equal(35.0, s.StartInput);
        }

        [Fact]
        public void Linear_with_a_start_of_20_runs_straight_from_the_start_to_full_output()
        {
            var s = Defaults();
            s.StartInput = 20.0;
            s.ApplyPreset(ProjectorPreset.Linear);

            var projector = new OutputProjector(s);

            Assert.Equal(0.0, projector.Project(20.0), 9);
            Assert.Equal(50.0, projector.Project(60.0), 9);
            Assert.Equal(100.0, projector.Project(100.0), 9);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void Linear_places_every_anchor_on_the_line_from_the_start_to_full_output(ProjectionChannel channel)
        {
            var s = Defaults();
            s.StartInput = 20.0;
            s.ApplyPreset(ProjectorPreset.Linear, channel);

            var projector = new OutputProjector(s);

            foreach (AnchorSlot slot in new[] { AnchorSlot.Slightly, AnchorSlot.Moderate, AnchorSlot.Critical })
            {
                double raw = ProjectorAnchorEditor.GetRaw(s, slot);
                double expected = (raw - 20.0) / 80.0 * 100.0;

                Assert.Equal(expected, ProjectorAnchorEditor.GetOutput(s, slot), 9);
                Assert.Equal(expected, projector.Project(raw), 9);
            }
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void Linear_with_a_start_of_zero_is_still_the_exact_identity_mapping(ProjectionChannel channel)
        {
            var s = Defaults();
            s.StartInput = 0.0;
            s.ApplyPreset(ProjectorPreset.Linear, channel);

            var projector = new OutputProjector(s);

            foreach (double x in new[] { 0.0, 7.5, 12.0, 25.0, 33.3, 50.0, 66.7, 75.0, 99.0, 100.0 })
                Assert.Equal(x, projector.Project(x), 9);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void A_non_zero_start_is_honoured_even_under_the_linear_preset(ProjectionChannel channel)
        {
            var s = Defaults();
            s.ApplyPreset(ProjectorPreset.Linear, channel);
            s.StartInput = 25.0;

            var projector = new OutputProjector(s);

            Assert.Equal(0.0, projector.Project(24.9));
            Assert.Equal(0.0, projector.Project(25.0));
            Assert.True(projector.Project(27.0) > 0.0);
            Assert.Equal(s.ModerateOutput, projector.Project(s.ModerateInput), 9);
        }

        [Fact]
        public void Curve_default_passes_through_named_anchors()
        {
            var projector = new OutputProjector(Defaults());
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            // Pre-release Change 2c: Ideal/Max Grip thresholds moved 60/80 -> 62/78 (paired with
            // flatten ranges of 2 so the plateau EDGES still land on 60/80).
            Assert.InRange(projector.Project(62.0), 29.5, 30.5);
            // 1.0.6.0 (docs\release-1060-report.md, Part 4d): Lock's Max Grip OUTPUT moved 80 -> 60.
            Assert.InRange(projector.Project(78.0), 59.5, 60.5);
        }

        [Fact]
        public void Curve_default_at_seventy_lands_in_moderate_shaking_band()
        {
            var projector = new OutputProjector(Defaults());
            double y = projector.Project(70.0);
            Assert.InRange(y, 40.0, 60.0);
        }

        /// <summary>
        /// Pre-release Change 2b (configurable per-setpoint flatten ranges): REPLACES the old
        /// "Curve_default_rises_steeply_between_moderate_and_critical" test, which pinned exactly the
        /// sharp-ramp-either-side-of-an-anchor shape the owner asked to have removed ("around the set
        /// points like threshold 30, 60, 80, the curve on the 3 points should be like flatten, like a
        /// platform"). The owner's own later, explicit instruction replaced an implicit curve-shape fix
        /// with EXPLICIT, per-setpoint flatten ranges (defaults 3/2/2 at 30/62/78) and six hidden control
        /// points at the range edges - verified numerically here at all three default ranges, both
        /// channels.
        /// </summary>
        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void Curve_default_flattens_into_a_plateau_around_every_setpoints_own_range(ProjectionChannel channel)
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, channel);
            var projector = new OutputProjector(s);

            void AssertPlateau(double setpointInput, double setpointOutput, double range, double maxDeviation)
            {
                Assert.InRange(projector.Project(setpointInput - range), setpointOutput - maxDeviation, setpointOutput + maxDeviation);
                Assert.InRange(projector.Project(setpointInput), setpointOutput - 0.05, setpointOutput + 0.05);
                Assert.InRange(projector.Project(setpointInput + range), setpointOutput - maxDeviation, setpointOutput + maxDeviation);
            }

            AssertPlateau(s.SlightlyInput, s.SlightlyOutput, s.SlightlyFlattenRange, 1.0);
            AssertPlateau(s.ModerateInput, s.ModerateOutput, s.ModerateFlattenRange, 2.0);
            AssertPlateau(s.CriticalInput, s.CriticalOutput, s.CriticalFlattenRange, 3.0);
        }

        /// <summary>Pinned regression at the plugin's own SHIPPED anchor values (Lock: 10/30/60 at
        /// 30/62/78 - 1.0.6.0 (docs\release-1060-report.md, Part 4d) moved Lock's Critical OUTPUT
        /// 80 -> 60, matching the sibling 1.0.6.2 branch's own value; Slip: 10/35/70 at 30/62/78 -
        /// Critical OUTPUT further softened 75 -> 70, owner-confirmed, direct response to ShakeIt
        /// over-shaking on WheelSlip) and SHIPPED default flatten ranges (3/2/2) - pre-release Change 2c
        /// moved the Ideal/Max Grip
        /// THRESHOLDS to 62/78 (from 60/80) specifically so that, paired with the narrowed ranges (2),
        /// the plateau EDGES still land exactly on the shared band boundaries 60/80 (the owner's own
        /// explicit "smooth 60-80, flatten on the edge of 60 and 80" intent) - the exact numbers
        /// measured for this branch's own acceptance table (27/30/33, 60/62/64, 76/78/80).</summary>
        [Fact]
        public void Curve_default_plateau_numbers_lock_channel_match_the_measured_report_table()
        {
            var projector = new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock));

            Assert.Equal(9.4000, projector.Project(27.0), 3);
            Assert.Equal(10.0000, projector.Project(30.0), 3);
            Assert.Equal(10.3750, projector.Project(33.0), 3);

            Assert.Equal(29.7500, projector.Project(60.0), 3); // Ideal plateau's own lower edge
            Assert.Equal(30.0000, projector.Project(62.0), 3); // Ideal anchor itself
            // Right-side slope uses Critical's own output (60 on this branch, since 1.0.6.0's Part 4d
            // change) as the right neighbour - this plateau edge moved from 31.25 (Critical output 80)
            // to 30.75 now that Critical output is 60.
            Assert.Equal(30.7500, projector.Project(64.0), 3); // Ideal plateau's own upper edge

            Assert.Equal(59.2500, projector.Project(76.0), 3); // Max Grip plateau's own lower edge
            Assert.Equal(60.0000, projector.Project(78.0), 3); // Max Grip anchor itself
            Assert.Equal(60.727, projector.Project(80.0), 3); // Max Grip plateau's own upper edge
        }

        [Fact]
        public void Curve_default_plateau_numbers_slip_channel_match_the_measured_report_table()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);
            var projector = new OutputProjector(s);

            Assert.Equal(9.4000, projector.Project(27.0), 3);
            Assert.Equal(10.0000, projector.Project(30.0), 3);
            Assert.Equal(10.4688, projector.Project(33.0), 3);

            Assert.Equal(34.6875, projector.Project(60.0), 3);
            Assert.Equal(35.0000, projector.Project(62.0), 3);
            // Right-side slope uses Critical's own output (70 on this build, softened from 75 -
            // owner-confirmed response to ShakeIt over-shaking on WheelSlip) as the right neighbour -
            // this plateau edge moved from 36.0 (Critical output 75) to 35.875 now that Critical
            // output is 70.
            Assert.Equal(35.875, projector.Project(64.0), 3);

            Assert.Equal(69.125, projector.Project(76.0), 3); // Max Grip plateau's own lower edge
            Assert.Equal(70.0000, projector.Project(78.0), 3); // Max Grip anchor itself - now 70, not 75
            Assert.Equal(70.5455, projector.Project(80.0), 2);
        }

        /// <summary>RANGE = 0 must degrade gracefully to the un-flattened curve (no hidden points
        /// inserted at all) - see <see cref="OutputProjector.AcceptSetpointWithFlatten"/>'s own
        /// remarks on why no special-cased branch is needed for this.</summary>
        [Fact]
        public void A_flatten_range_of_zero_degrades_to_the_unflattened_curve()
        {
            var flattened = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            var unflattened = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            unflattened.SlightlyFlattenRange = 0.0;
            unflattened.ModerateFlattenRange = 0.0;
            unflattened.CriticalFlattenRange = 0.0;

            var flatProjector = new OutputProjector(flattened);
            var plainProjector = new OutputProjector(unflattened);

            // Away from any setpoint the two must still broadly agree (both pass through the same real
            // anchors), but AT a probe 3 short of an anchor the un-flattened curve must be noticeably
            // FARTHER from the anchor than the flattened one - proving the range actually did something.
            double anchorOutput = flattened.CriticalOutput;
            double flatDeviation = Math.Abs(flatProjector.Project(flattened.CriticalInput - 3.0) - anchorOutput);
            double plainDeviation = Math.Abs(plainProjector.Project(flattened.CriticalInput - 3.0) - anchorOutput);
            Assert.True(flatDeviation < plainDeviation,
                $"flattened deviation ({flatDeviation}) should be smaller than the un-flattened curve's own ({plainDeviation})");
        }

        /// <summary>
        /// PRECISION requirement from the owner: a flatten range of exactly 0 for a given anchor must
        /// not merely "degrade toward" the un-flattened curve (the looser check above) - it must
        /// OMIT that anchor's two hidden control points ENTIRELY, never create them at a zero offset
        /// (which would perturb the monotone-cubic fit via duplicate/near-duplicate x values even
        /// though they coincide with the anchor). This is the strict, bit-identical version of that
        /// requirement: with the ORIGINAL pre-Change-2c anchor inputs (30/60/80 - NOT today's shipped
        /// 62/78, which would legitimately produce a different curve even at range 0) and all three
        /// ranges forced to 0, the projector's own evaluated curve must match, to the LAST BIT, a curve
        /// built independently from the plain (non-flattened) control-point list - i.e. exactly the
        /// curve this project produced before the flatten-range feature (Change 2b) existed at all.
        /// Checked across a dense 0.5-step sweep, for both channels, with exact (not approximate)
        /// equality - see <see cref="OutputProjector.AcceptSetpointWithFlatten"/>'s own
        /// <c>range &gt; 0.0</c> guard, which is what actually skips hidden-point creation.
        /// </summary>
        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void All_flatten_ranges_at_zero_with_the_original_30_60_80_inputs_is_bit_identical_to_the_pre_flatten_curve(ProjectionChannel channel)
        {
            var s = ProjectorSettings.CreateShippedDefault(channel);
            s.ModerateInput = 60.0;
            s.CriticalInput = 80.0;
            s.SlightlyFlattenRange = 0.0;
            s.ModerateFlattenRange = 0.0;
            s.CriticalFlattenRange = 0.0;

            var projector = new OutputProjector(s);
            var (xs, ys) = BuildPlainControlPointsIndependently(s);
            var referenceCurve = new MonotoneCubicCurve(xs, ys);

            for (double x = 0.0; x <= 100.0; x += 0.5)
            {
                double expected = ClampMath.To0100(referenceCurve.Evaluate(x));
                double actual = projector.Project(x);
                Assert.True(expected == actual,
                    $"x={x}: expected exactly {expected} (pre-flatten curve) but projector produced {actual} - " +
                    "a flatten range of 0 must omit the hidden points entirely, not create them at a zero offset.");
            }
        }

        /// <summary>Per-anchor, not global: only the anchor whose OWN range is 0 loses its two hidden
        /// control points - a sibling anchor with a non-zero range keeps both of its own. Verified
        /// directly against the actual control-point list <see cref="OutputProjector"/> builds (via
        /// reflection into its own private <c>BuildControlPoints</c> - deliberately NOT inferred
        /// indirectly from evaluated curve values, since the monotone-cubic tangent at one knot also
        /// depends on its immediate neighbours, so an evaluated-value comparison near one anchor can be
        /// perturbed by a SIBLING anchor's own hidden points even when that sibling's range did not
        /// change - a real subtlety of this interpolant, not a bug).</summary>
        [Fact]
        public void A_zero_range_on_one_anchor_does_not_suppress_a_sibling_anchors_own_nonzero_range()
        {
            var mixed = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            mixed.SlightlyFlattenRange = 0.0; // only Slightly loses its hidden points
            var plain = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            plain.SlightlyFlattenRange = 0.0;
            plain.ModerateFlattenRange = 0.0;
            plain.CriticalFlattenRange = 0.0;

            double[] mixedXs = InvokeBuildControlPointsXs(mixed);
            double[] plainXs = InvokeBuildControlPointsXs(plain);

            // Slightly's own two hidden points (at 30-3=27 and 30+3=33) are absent from BOTH lists -
            // its range is 0 in both "mixed" and "plain".
            Assert.DoesNotContain(27.0, mixedXs);
            Assert.DoesNotContain(33.0, mixedXs);
            Assert.DoesNotContain(27.0, plainXs);
            Assert.DoesNotContain(33.0, plainXs);

            // Moderate's own two hidden points (62-2=60 and 62+2=64) are present in "mixed" (its range
            // is still the shipped default, 2) but ABSENT from "plain" (forced to 0 there).
            Assert.Contains(60.0, mixedXs);
            Assert.Contains(64.0, mixedXs);
            Assert.DoesNotContain(60.0, plainXs);
            Assert.DoesNotContain(64.0, plainXs);
        }

        /// <summary>Reflection helper: invokes <see cref="OutputProjector"/>'s own private static
        /// <c>BuildControlPoints</c> against the SAME clamped/sorted settings the real constructor
        /// uses, and returns just the x-coordinates - so control-point-list assertions check the
        /// actual production algorithm, not a re-implementation of it.</summary>
        private static double[] InvokeBuildControlPointsXs(ProjectorSettings settings)
        {
            var clamped = settings.WithClampedSortedAnchors();
            var method = typeof(OutputProjector).GetMethod("BuildControlPoints",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var args = new object[] { clamped, null, null };
            method.Invoke(null, args);
            return (double[])args[1];
        }

        /// <summary>A negative configured range is CLAMPED to 0 (never rejected/thrown, never treated
        /// as a magnitude) - see <see cref="ProjectorSettings.WithClampedSortedAnchors"/>'s own
        /// non-negative clamp. Confirmed bit-identical to an explicit 0, not merely "close".</summary>
        [Fact]
        public void A_negative_flatten_range_clamps_to_zero_bit_identically()
        {
            var zero = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            zero.SlightlyFlattenRange = 0.0;
            zero.ModerateFlattenRange = 0.0;
            zero.CriticalFlattenRange = 0.0;

            var negative = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            negative.SlightlyFlattenRange = -5.0;
            negative.ModerateFlattenRange = -1.0;
            negative.CriticalFlattenRange = -0.001;

            var zeroProjector = new OutputProjector(zero);
            var negativeProjector = new OutputProjector(negative);

            for (double x = 0.0; x <= 100.0; x += 0.5)
                Assert.True(zeroProjector.Project(x) == negativeProjector.Project(x), $"x={x} differed");
        }

        /// <summary>A fractional range (e.g. 1.5) is used EXACTLY as given - no rounding to a whole
        /// number anywhere. Pinned against the hand-derived formula (see
        /// <see cref="OutputProjector.AcceptSetpointWithFlatten"/>'s own remarks: hidden output =
        /// setpoint output +/- FlattenBleedFraction*slope*effectiveRange) at Lock's shipped Slightly
        /// anchor (30 -&gt; 10), whose left neighbour is Start (20 -&gt; 0, slope 1.0) and right
        /// neighbour is Moderate (62 -&gt; 30, slope 0.625): 28.5 -&gt; 10 - 0.2*1.0*1.5 = 9.7, and
        /// 31.5 -&gt; 10 + 0.2*0.625*1.5 = 10.1875.</summary>
        [Fact]
        public void A_fractional_flatten_range_is_honoured_exactly_with_no_rounding()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.SlightlyFlattenRange = 1.5;

            var projector = new OutputProjector(s);

            Assert.Equal(9.7, projector.Project(28.5), 6);
            Assert.Equal(10.1875, projector.Project(31.5), 6);
        }

        /// <summary>Independent reproduction of the PRE-Change-2b control-point list (no flatten
        /// mechanism at all) for <see cref="All_flatten_ranges_at_zero_with_the_original_30_60_80_inputs_is_bit_identical_to_the_pre_flatten_curve"/>
        /// - deliberately written from the documented algorithm rather than by calling into
        /// <see cref="OutputProjector"/>'s own private helpers, so the test does not just check that
        /// the production code agrees with itself.</summary>
        private static (double[] xs, double[] ys) BuildPlainControlPointsIndependently(ProjectorSettings s)
        {
            var px = new System.Collections.Generic.List<double> { 0.0 };
            var py = new System.Collections.Generic.List<double> { s.StartOutput };

            void Accept(double x, double y)
            {
                double lastX = px[px.Count - 1];
                if (x <= lastX) return;
                double lastY = py[py.Count - 1];
                px.Add(x);
                py.Add(Math.Max(y, lastY));
            }

            if (s.StartInput > 0.0) Accept(s.StartInput, s.StartOutput);
            if (s.SlightlyInput < s.EndInput) Accept(s.SlightlyInput, s.SlightlyOutput);
            if (s.ModerateInput < s.EndInput) Accept(s.ModerateInput, s.ModerateOutput);
            if (s.CriticalInput < s.EndInput) Accept(s.CriticalInput, s.CriticalOutput);
            Accept(s.EndInput, s.EndOutput);

            if (px[px.Count - 1] < 100.0)
            {
                px.Add(100.0);
                py.Add(Math.Max(s.EndOutput, py[py.Count - 1]));
            }

            return (px.ToArray(), py.ToArray());
        }

        /// <summary>A very large configured range must clamp (never overlap a neighbouring setpoint's
        /// own plateau, never break monotonicity) rather than distorting or breaking the curve - see
        /// <see cref="OutputProjector.AcceptSetpointWithFlatten"/>'s own half-gap clamp. At the shipped
        /// 62/78 thresholds the Ideal-Max Grip gap is 78-62=16, so the half-gap clamp caps EITHER one's
        /// own range at 16/2=8 independently once increased past that - even pushing both ranges above
        /// 8 simultaneously, each is clamped to 8 on its own side, so the two plateaus can meet at the
        /// exact midpoint (70) but never cross past each other.</summary>
        [Fact]
        public void A_very_large_flatten_range_clamps_instead_of_breaking_the_curve()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.SlightlyFlattenRange = 1000.0;
            s.ModerateFlattenRange = 1000.0;
            s.CriticalFlattenRange = 1000.0;

            var projector = new OutputProjector(s);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 0.5)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                Assert.InRange(y, 0.0, 100.0);
                previous = y;
            }

            // The anchors themselves must still land exactly on their own configured outputs even
            // though the range is absurdly large.
            Assert.Equal(s.SlightlyOutput, projector.Project(s.SlightlyInput), 6);
            Assert.Equal(s.ModerateOutput, projector.Project(s.ModerateInput), 6);
            Assert.Equal(s.CriticalOutput, projector.Project(s.CriticalInput), 6);
        }

        /// <summary>The Linear preset must remain an EXACT straight line even though flatten ranges
        /// default to non-zero (3/2/2) on every fresh settings object - flattening is skipped entirely
        /// under Linear (see <see cref="OutputProjector.AcceptSetpointWithFlatten"/>'s own remarks).</summary>
        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void Linear_preset_stays_an_exact_straight_line_despite_nonzero_default_flatten_ranges(ProjectionChannel channel)
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Linear, channel);
            Assert.True(s.SlightlyFlattenRange > 0.0 && s.ModerateFlattenRange > 0.0 && s.CriticalFlattenRange > 0.0);

            var projector = new OutputProjector(s);
            foreach (double x in new[] { 0.0, 10.0, 25.0, 33.3, 50.0, 66.7, 75.0, 90.0, 100.0 })
                Assert.Equal(x, projector.Project(x), 9);
        }

        [Fact]
        public void Moved_anchor_inputs_are_honoured()
        {
            var s = Defaults();
            s.SlightlyInput = 20.0;
            s.ModerateInput = 45.0;

            var projector = new OutputProjector(s);
            Assert.InRange(projector.Project(20.0), 9.5, 10.5);
            Assert.InRange(projector.Project(45.0), 29.5, 30.5);
        }

        [Fact]
        public void Start_point_forty_zeroes_everything_below_it()
        {
            var s = Defaults();
            s.StartInput = 40.0;

            var projector = new OutputProjector(s);
            for (double x = 0.0; x <= 40.0; x += 5.0)
                Assert.Equal(0.0, projector.Project(x), 9);

            Assert.True(projector.Project(45.0) > 0.0);
            // Pre-release Change 2c: the Ideal anchor's own threshold is 62 now (not 60) - 60 is only
            // the plateau's own lower edge (a slightly different value, not exactly 30).
            Assert.Equal(30.0, projector.Project(62.0), 6);
        }

        [Fact]
        public void Start_point_above_every_anchor_still_produces_a_valid_monotone_curve()
        {
            var s = Defaults();
            s.StartInput = 85.0;

            var projector = new OutputProjector(s);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 1.0)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                Assert.InRange(y, 0.0, 100.0);
                previous = y;
            }

            Assert.Equal(100.0, projector.Project(100.0), 6);
        }

        [Fact]
        public void Anchors_entered_out_of_order_are_sorted_not_rejected()
        {
            var s = Defaults();
            s.SlightlyInput = 70.0;
            s.ModerateInput = 30.0;

            var projector = new OutputProjector(s);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 1.0)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        [Fact]
        public void Duplicate_inputs_produce_a_valid_curve_instead_of_throwing()
        {
            var s = Defaults();
            s.SlightlyInput = 60.0;
            s.ModerateInput = 60.0;

            var ex = Record.Exception(() => new OutputProjector(s));
            Assert.Null(ex);

            var projector = new OutputProjector(s);
            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 1.0)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        [Fact]
        public void Reversed_outputs_produce_a_non_decreasing_curve_not_a_dip()
        {
            var s = Defaults();
            s.SlightlyOutput = 50.0;
            s.ModerateOutput = 20.0;

            var projector = new OutputProjector(s);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 1.0)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        [Fact]
        public void Start_input_positive_infinity_does_not_silence_the_shaker()
        {
            var s = Defaults();
            s.StartInput = double.PositiveInfinity;

            var projector = new OutputProjector(s);
            Assert.True(projector.Project(80.0) > 50.0, "shaker went silent for a non-finite StartInput");
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
        }

        [Fact]
        public void Output_always_within_0_100_and_nan_yields_zero()
        {
            var projector = new OutputProjector(Defaults());
            for (double x = -50.0; x <= 150.0; x += 10.0)
                Assert.InRange(projector.Project(x), 0.0, 100.0);

            Assert.Equal(0.0, projector.Project(double.NaN));
        }

        [Fact]
        public void Different_presets_produce_different_curves()
        {
            var linear = new OutputProjector(LinearSettings());
            var curve = new OutputProjector(Defaults());

            Assert.NotEqual(linear.Project(30.0), curve.Project(30.0), 3);
        }

        [Fact]
        public void Anchor_at_input_exactly_the_end_point_is_dropped_in_favour_of_the_end()
        {
            var s = Defaults();
            s.CriticalInput = 100.0;
            s.CriticalOutput = 90.0;

            var projector = new OutputProjector(s);
            Assert.Equal(100.0, projector.Project(100.0), 6);
        }

        [Fact]
        public void Start_input_exactly_equal_to_an_anchor_input_drops_the_anchor()
        {
            var s = Defaults();
            s.StartInput = 30.0;

            var projector = new OutputProjector(s);
            Assert.Equal(0.0, projector.Project(30.0), 6);
            Assert.Equal(0.0, projector.Project(0.0), 6);
        }

        [Fact]
        public void ApplyPreset_custom_leaves_existing_values_untouched()
        {
            var s = Defaults();
            s.StartInput = 5.0;
            s.SlightlyInput = 22.0; s.SlightlyOutput = 11.0;
            s.ModerateInput = 48.0; s.ModerateOutput = 33.0;
            s.CriticalInput = 91.0; s.CriticalOutput = 77.0;

            s.ApplyPreset(ProjectorPreset.Custom);

            Assert.Equal(ProjectorPreset.Custom, s.Preset);
            Assert.Equal(5.0, s.StartInput);
            Assert.Equal(22.0, s.SlightlyInput);
            Assert.Equal(11.0, s.SlightlyOutput);
            Assert.Equal(48.0, s.ModerateInput);
            Assert.Equal(33.0, s.ModerateOutput);
            Assert.Equal(91.0, s.CriticalInput);
            Assert.Equal(77.0, s.CriticalOutput);
        }

        [Fact]
        public void ApplyPreset_curve_slip_produces_slip_defaults_not_lock_defaults()
        {
            var slip = Defaults();
            slip.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);

            var lockChannel = Defaults();
            lockChannel.ApplyPreset(ProjectorPreset.Curve); // no channel -> Lock default, unchanged

            // Same anchor INPUT positions as Lock (30/62/78/100 - owner's shared band boundaries,
            // see docs\refinements-report.md; thresholds moved 60/80 -> 62/78 by pre-release Change 2c
            // so the flatten-range plateau edges land on the shared 60/80 boundaries) - only the OUTPUT
            // values (the actual feel) differ. REVISED (docs\slip-source-consistency-report.md - a
            // second round of owner seat-testing): 8/20/75 -> 10/35/75; FURTHER REVISED (this build,
            // owner-confirmed): Critical (Max Grip) output softened 75 -> 70, direct response to the
            // owner's in-game report that ShakeIt "shakes much harder than using Raw" on WheelSlip -
            // WheelLock's own Critical output is deliberately UNCHANGED (stays 60, see below).
            Assert.Equal(20.0, slip.StartInput);
            Assert.Equal(30.0, slip.SlightlyInput);
            Assert.Equal(10.0, slip.SlightlyOutput);
            Assert.Equal(62.0, slip.ModerateInput);
            Assert.Equal(35.0, slip.ModerateOutput);
            Assert.Equal(78.0, slip.CriticalInput);
            Assert.Equal(70.0, slip.CriticalOutput);

            Assert.Equal(20.0, lockChannel.StartInput);
            Assert.Equal(30.0, lockChannel.SlightlyInput);
            Assert.Equal(10.0, lockChannel.SlightlyOutput);
            Assert.Equal(62.0, lockChannel.ModerateInput);
            Assert.Equal(30.0, lockChannel.ModerateOutput);
            Assert.Equal(78.0, lockChannel.CriticalInput);
            // 1.0.6.0 (docs\release-1060-report.md, Part 4d): Lock's Critical OUTPUT moved 80 -> 60,
            // now matching the sibling 1.0.6.2 branch's own value - see ProjectorSettings.CriticalOutput's
            // own remarks.
            Assert.Equal(60.0, lockChannel.CriticalOutput);

            Assert.Equal(lockChannel.ModerateInput, slip.ModerateInput); // shared band boundary
            Assert.Equal(lockChannel.CriticalInput, slip.CriticalInput); // shared band boundary
            // REVISED (docs\slip-source-consistency-report.md): the SlightlyOutput anchors now happen to
            // COINCIDE (both 10.0) under the owner's second round of seat-testing - no longer a
            // discriminator between the two channels, unlike Moderate/Critical which still differ.
            Assert.Equal(lockChannel.SlightlyOutput, slip.SlightlyOutput);
            Assert.NotEqual(lockChannel.ModerateOutput, slip.ModerateOutput);
            Assert.NotEqual(lockChannel.CriticalOutput, slip.CriticalOutput);
        }

        [Fact]
        public void CreateShippedDefault_lock_matches_ApplyPreset_curve_lock()
        {
            var expected = Defaults();
            expected.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Lock);

            var actual = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);

            Assert.Equal(expected.StartInput, actual.StartInput);
            Assert.Equal(expected.EndInput, actual.EndInput);
            Assert.Equal(expected.SlightlyInput, actual.SlightlyInput);
            Assert.Equal(expected.SlightlyOutput, actual.SlightlyOutput);
            Assert.Equal(expected.ModerateInput, actual.ModerateInput);
            Assert.Equal(expected.ModerateOutput, actual.ModerateOutput);
            Assert.Equal(expected.CriticalInput, actual.CriticalInput);
            Assert.Equal(expected.CriticalOutput, actual.CriticalOutput);
            Assert.Equal(expected.StartOutput, actual.StartOutput);
            Assert.Equal(expected.EndOutput, actual.EndOutput);
            Assert.Equal(expected.SlightlyFlattenRange, actual.SlightlyFlattenRange);
            Assert.Equal(expected.ModerateFlattenRange, actual.ModerateFlattenRange);
            Assert.Equal(expected.CriticalFlattenRange, actual.CriticalFlattenRange);
        }

        [Fact]
        public void CreateShippedDefault_slip_matches_ApplyPreset_curve_slip()
        {
            var expected = Defaults();
            expected.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);

            var actual = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Slip);

            Assert.Equal(expected.StartInput, actual.StartInput);
            Assert.Equal(expected.EndInput, actual.EndInput);
            Assert.Equal(expected.SlightlyInput, actual.SlightlyInput);
            Assert.Equal(expected.SlightlyOutput, actual.SlightlyOutput);
            Assert.Equal(expected.ModerateInput, actual.ModerateInput);
            Assert.Equal(expected.ModerateOutput, actual.ModerateOutput);
            Assert.Equal(expected.CriticalInput, actual.CriticalInput);
            Assert.Equal(expected.CriticalOutput, actual.CriticalOutput);
            Assert.Equal(expected.StartOutput, actual.StartOutput);
            Assert.Equal(expected.EndOutput, actual.EndOutput);
            Assert.Equal(expected.SlightlyFlattenRange, actual.SlightlyFlattenRange);
            Assert.Equal(expected.ModerateFlattenRange, actual.ModerateFlattenRange);
            Assert.Equal(expected.CriticalFlattenRange, actual.CriticalFlattenRange);
        }

        [Fact]
        public void ApplyPreset_linear_slip_is_identity_at_slip_anchors()
        {
            var s = Defaults();
            s.ApplyPreset(ProjectorPreset.Linear, ProjectionChannel.Slip);

            Assert.Equal(0.0, s.StartInput);
            Assert.Equal(30.0, s.SlightlyInput);
            Assert.Equal(30.0, s.SlightlyOutput);
            Assert.Equal(60.0, s.ModerateInput);
            Assert.Equal(60.0, s.ModerateOutput);
            Assert.Equal(80.0, s.CriticalInput);
            Assert.Equal(80.0, s.CriticalOutput);
        }

        [Fact]
        public void Projector_from_slip_curve_settings_passes_through_slip_anchors()
        {
            var s = Defaults();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);

            var projector = new OutputProjector(s);
            // REVISED (docs\slip-source-consistency-report.md): 8/20/75 -> 10/35/75; FURTHER REVISED
            // (this build, owner-confirmed): Critical (Max Grip) output softened 75 -> 70, direct
            // response to ShakeIt over-shaking on WheelSlip.
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            Assert.InRange(projector.Project(60.0), 34.5, 35.5);
            Assert.InRange(projector.Project(80.0), 70.0, 71.0);
        }

        // ------------------------------------------------------------------------------------
        // Band-semantics verification (owner's Task 3): numerically confirm, for BOTH channels'
        // shipped Curve defaults, that "at the limit" (75-80 raw) reads a strong-but-not-maximum
        // output and 100 reads exactly maximum - see docs\refinements-report.md for the full
        // numeric table (0/30/60/75/80/100) and the independent Python cross-check.
        // ------------------------------------------------------------------------------------
        /// <summary>RENAMED from "..._puts_near_the_limit_around_75_to_80_and_fully_locked_at_100" -
        /// this test's own PREMISE changed under 1.0.6.0 (docs\release-1060-report.md, Part 4d): Lock's
        /// Max Grip OUTPUT moved 80 -> 60 (owner's explicit request: "otherwise it will shake too strong
        /// when reaches the best braking force"), so the 78-input anchor is no longer near the top of the
        /// 0-100 output range - it now reads a moderate 60, matching the sibling 1.0.6.2 branch's own
        /// value. Only input==100 (EndInput, fully locked) still unconditionally reads the true
        /// maximum - that is what is actually verified here now, truthfully re-expressed rather than
        /// forcing the old 75-80 "near the limit" framing to still hold.</summary>
        [Fact]
        public void Lock_curve_default_reaches_its_own_max_grip_output_at_78_and_fully_locked_at_100()
        {
            var s = new ProjectorSettings(); // Lock's own field defaults
            var projector = new OutputProjector(s);

            // Pre-release Change 2c: Ideal/Max Grip THRESHOLDS moved 60/80 -> 62/78 (paired with
            // flatten ranges of 2) so the plateau EDGES still land on the shared band boundaries 60/80.
            Assert.InRange(projector.Project(0.0), 0.0, 0.0);
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);   // 30-62 ideal zone starts weak
            Assert.InRange(projector.Project(62.0), 29.5, 30.5);  // ideal zone's own anchor
            Assert.InRange(projector.Project(75.0), 50.0, 65.0);  // ramping toward Max Grip's own plateau
            Assert.InRange(projector.Project(78.0), 59.5, 60.5);  // Max Grip anchor itself - now 60, not near-max
            Assert.Equal(100.0, projector.Project(100.0), 6);     // fully locked - always exactly max
        }

        [Fact]
        public void Slip_curve_default_puts_near_the_limit_around_78_to_80_and_fully_spinning_at_100()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);
            var projector = new OutputProjector(s);

            // REVISED (docs\slip-source-consistency-report.md): 8/20/75 -> 10/35/75; FURTHER REVISED
            // (this build, owner-confirmed): Critical (Max Grip) output softened 75 -> 70, direct
            // response to the owner's in-game report that ShakeIt "shakes much harder than using Raw"
            // on WheelSlip - the 75-input checkpoint's range is re-centred below since the curve's own
            // Critical anchor output moved down (verified against the real MonotoneCubicCurve output,
            // not hand-derived, via a diagnostic dump: 75 => 67.493, 80 => 70.545). Checkpoints stay at
            // 60/80 (not the internal 62/78 thresholds) - 60/80 are the shared band boundaries and,
            // under the shipped flatten ranges of 2, exactly coincide with the Ideal/Max Grip plateaus'
            // own edges either way.
            Assert.InRange(projector.Project(0.0), 0.0, 0.0);
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            Assert.InRange(projector.Project(60.0), 34.5, 35.5);
            // 75 sits on the ramp leading up to the Critical (78) setpoint's own flatten-range plateau
            // (76-80), below that plateau's own output (now ~69-70.5, down from ~74-75.5 before this
            // build's softening).
            Assert.InRange(projector.Project(75.0), 67.0, 68.0);
            Assert.InRange(projector.Project(80.0), 70.0, 71.0);
            Assert.Equal(100.0, projector.Project(100.0), 6);
        }

        [Fact]
        public void Preset_custom_with_curve_default_anchors_behaves_like_curve()
        {
            var s = Defaults();
            s.Preset = ProjectorPreset.Custom;

            var projector = new OutputProjector(s);
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            Assert.InRange(projector.Project(62.0), 29.5, 30.5); // Pre-release Change 2c: 60 -> 62
            // 1.0.6.0 (docs\release-1060-report.md, Part 4d): Lock's Max Grip OUTPUT moved 80 -> 60.
            Assert.InRange(projector.Project(78.0), 59.5, 60.5);
        }

        [Fact]
        public void Project_allocates_nothing()
        {
            var projector = new OutputProjector(Defaults());
            projector.Project(45.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
                projector.Project(i % 100);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void Default_curve_settings_are_silent_at_or_below_20_and_positive_above_it(ProjectionChannel channel)
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, channel);

            var projector = new OutputProjector(s);
            for (double x = 0.0; x <= 20.0; x += 5.0)
                Assert.Equal(0.0, projector.Project(x), 9);

            Assert.True(projector.Project(21.0) > 0.0,
                $"{channel} channel produced no output just above the start point");
        }

        [Theory]
        [InlineData(ProjectorPreset.Linear, ProjectionChannel.Lock)]
        [InlineData(ProjectorPreset.Curve, ProjectionChannel.Lock)]
        [InlineData(ProjectorPreset.Linear, ProjectionChannel.Slip)]
        [InlineData(ProjectorPreset.Curve, ProjectionChannel.Slip)]
        public void Every_configured_anchor_survives_into_the_built_curve(ProjectorPreset preset, ProjectionChannel channel)
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(preset, channel);

            var projector = new OutputProjector(s);

            Assert.InRange(projector.Project(s.SlightlyInput), s.SlightlyOutput - 0.5, s.SlightlyOutput + 0.5);
            Assert.InRange(projector.Project(s.ModerateInput), s.ModerateOutput - 0.5, s.ModerateOutput + 0.5);
            Assert.InRange(projector.Project(s.CriticalInput), s.CriticalOutput - 0.5, s.CriticalOutput + 0.5);
        }

        [Fact]
        public void Default_curve_preset_sends_expected_values_lock_channel()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve);

            var projector = new OutputProjector(s);
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            Assert.InRange(projector.Project(62.0), 29.5, 30.5); // Pre-release Change 2c: 60 -> 62
            // 1.0.6.0 (docs\release-1060-report.md, Part 4d): Lock's Max Grip OUTPUT moved 80 -> 60.
            Assert.InRange(projector.Project(78.0), 59.5, 60.5);
        }

        [Fact]
        public void Default_curve_preset_sends_expected_values_slip_channel()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);

            var projector = new OutputProjector(s);
            // REVISED (docs\slip-source-consistency-report.md): 8/20/75 -> 10/35/75; FURTHER REVISED
            // (this build, owner-confirmed): Critical (Max Grip) output softened 75 -> 70, direct
            // response to ShakeIt over-shaking on WheelSlip. Checkpoints stay at 60/80 (the shared band
            // boundaries, which coincide with the plateau edges either way).
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            Assert.InRange(projector.Project(60.0), 34.5, 35.5);
            Assert.InRange(projector.Project(80.0), 70.0, 71.0);
        }

        [Fact]
        public void ApplyPreset_linear_slip_projector_is_identity_at_its_anchors()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Linear, ProjectionChannel.Slip);

            var projector = new OutputProjector(s);
            Assert.Equal(30.0, projector.Project(30.0), 9);
            Assert.Equal(60.0, projector.Project(60.0), 9);
            Assert.Equal(80.0, projector.Project(80.0), 9);
        }

        [Fact]
        public void EndInput_defaults_to_100_on_a_fresh_settings_object()
        {
            Assert.Equal(100.0, new ProjectorSettings().EndInput);
        }

        [Theory]
        [InlineData(ProjectorPreset.Linear, ProjectionChannel.Lock)]
        [InlineData(ProjectorPreset.Linear, ProjectionChannel.Slip)]
        // Pre-release Change 2b: the Curve preset cases are deliberately REMOVED here - the real
        // OutputProjector now inserts hidden flatten-range control points for Curve (see
        // OutputProjector.AcceptSetpointWithFlatten), which this test's own hand-rolled "legacy"
        // reconstruction below intentionally does NOT reproduce (it is the plain, pre-flatten-range
        // control-point rule). Linear is unaffected (flattening is skipped for Linear by design - see
        // AcceptSetpointWithFlatten's own remarks) and remains an exact match, still verified here.
        public void Default_curve_and_linear_presets_are_byte_identical_to_the_reimplemented_legacy_shape(
            ProjectorPreset preset, ProjectionChannel channel)
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(preset, channel);
            Assert.Equal(100.0, s.EndInput);

            ProjectorSettings clamped = s.WithClampedSortedAnchors();

            var px = new System.Collections.Generic.List<double> { 0.0 };
            var py = new System.Collections.Generic.List<double> { 0.0 };
            void Accept(double x, double y)
            {
                double lastX = px[px.Count - 1];
                if (x <= lastX) return;
                double lastY = py[py.Count - 1];
                px.Add(x);
                py.Add(Math.Max(y, lastY));
            }
            if (clamped.StartInput > 0.0) Accept(clamped.StartInput, 0.0);
            Accept(clamped.SlightlyInput, clamped.SlightlyOutput);
            Accept(clamped.ModerateInput, clamped.ModerateOutput);
            Accept(clamped.CriticalInput, clamped.CriticalOutput);
            if (px[px.Count - 1] < 100.0) { px.Add(100.0); py.Add(100.0); }
            double[] xs = px.Count < 2 ? new[] { 0.0, 100.0 } : px.ToArray();
            double[] ys = py.Count < 2 ? new[] { 0.0, 100.0 } : py.ToArray();
            var legacyCurve = new MonotoneCubicCurve(xs, ys);

            var projector = new OutputProjector(s);

            for (double x = 0.0; x <= 100.0; x += 0.5)
                Assert.Equal(legacyCurve.Evaluate(x), projector.Project(x), 9);
        }

        [Fact]
        public void End_of_80_saturates_at_input_80_and_never_exceeds_100()
        {
            var s = Defaults();
            s.EndInput = 80.0;

            var projector = new OutputProjector(s);
            Assert.Equal(100.0, projector.Project(80.0), 6);
            Assert.Equal(100.0, projector.Project(90.0), 6);
            Assert.Equal(100.0, projector.Project(100.0), 6);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 80.0; x += 1.0)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                Assert.True(y <= 100.0 + 1e-9, $"exceeded 100 at x={x}");
                previous = y;
            }
        }

        [Fact]
        public void Linear_with_start_20_and_end_80_runs_the_line_exactly_between_them()
        {
            var s = Defaults();
            s.StartInput = 20.0;
            s.EndInput = 80.0;
            s.ApplyPreset(ProjectorPreset.Linear);

            var projector = new OutputProjector(s);
            Assert.Equal(0.0, projector.Project(20.0), 6);
            Assert.Equal(50.0, projector.Project(50.0), 6);
            Assert.Equal(100.0, projector.Project(80.0), 6);
        }

        [Fact]
        public void An_anchor_at_or_above_the_end_is_dropped_and_the_curve_stays_monotone()
        {
            var s = Defaults();
            s.EndInput = 70.0;
            s.CriticalInput = 90.0;
            s.CriticalOutput = 5.0;

            var projector = new OutputProjector(s);

            Assert.Equal(100.0, projector.Project(70.0), 6);
            Assert.Equal(100.0, projector.Project(90.0), 6);
            Assert.Equal(100.0, projector.Project(100.0), 6);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 1.0)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        [Fact]
        public void EndInput_at_or_below_StartInput_is_ignored_and_the_curve_runs_to_100_100()
        {
            var s = Defaults();
            s.StartInput = 50.0;
            s.EndInput = 30.0;

            var ex = Record.Exception(() => new OutputProjector(s));
            Assert.Null(ex);

            var projector = new OutputProjector(s);

            Assert.Equal(0.0, projector.Project(0.0), 6);
            Assert.Equal(0.0, projector.Project(50.0), 6);
            Assert.True(projector.Project(99.0) < 100.0, "end point took effect despite EndInput <= StartInput");
            Assert.Equal(100.0, projector.Project(100.0), 6);
        }

        [Fact]
        public void EndInput_equal_to_StartInput_is_also_ignored()
        {
            var s = Defaults();
            s.StartInput = 40.0;
            s.EndInput = 40.0;

            var ex = Record.Exception(() => new OutputProjector(s));
            Assert.Null(ex);

            var projector = new OutputProjector(s);
            Assert.Equal(0.0, projector.Project(40.0), 6);
            Assert.Equal(100.0, projector.Project(100.0), 6);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void ApplyPreset_curve_sets_end_to_100_on_both_channels(ProjectionChannel channel)
        {
            var s = Defaults();
            s.EndInput = 55.0;

            s.ApplyPreset(ProjectorPreset.Curve, channel);

            Assert.Equal(100.0, s.EndInput);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void ApplyPreset_linear_preserves_a_non_default_end_on_both_channels(ProjectionChannel channel)
        {
            var s = Defaults();
            s.EndInput = 88.0;

            s.ApplyPreset(ProjectorPreset.Linear, channel);

            Assert.Equal(88.0, s.EndInput);
        }

        // ================================================================================
        // PRE-RELEASE ADDITION: configurable StartOutput/EndOutput (owner's own request -
        // "what is the maximum output they want to have, and what the start value they want
        // to build"). See ProjectorSettings' and OutputProjector.BuildControlPoints' own
        // remarks for the continuous floor/ceiling design and the monotonicity policy for
        // the four edge-case combinations the brief explicitly asked to be tested.
        // ================================================================================

        /// <summary>PRE-RELEASE ADDITION: <see cref="ProjectorAnchorEditor"/>'s Start/End slots used to
        /// be hard-fixed at 0/100 and reject writes outright - both now read/write the real
        /// <see cref="ProjectorSettings.StartOutput"/>/<see cref="ProjectorSettings.EndOutput"/>
        /// fields like every other slot, including switching the preset to Custom on a real
        /// edit.</summary>
        [Fact]
        public void ProjectorAnchorEditor_reads_and_writes_the_real_start_and_end_output_and_switches_to_custom()
        {
            // RE-SPECIFIED: an edit now records WHICH preset it departed from, rather than collapsing
            // every edit into one anonymous "Custom" - editing a Linear preset used to look identical to
            // editing a Curve one in the dropdown.
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            Assert.Equal(ProjectorPreset.Curve, s.Preset);
            Assert.Equal(0.0, ProjectorAnchorEditor.GetOutput(s, AnchorSlot.Start));
            Assert.Equal(100.0, ProjectorAnchorEditor.GetOutput(s, AnchorSlot.End));

            ProjectorAnchorEditor.SetOutput(s, AnchorSlot.Start, 15.0);
            Assert.Equal(15.0, s.StartOutput);
            Assert.Equal(ProjectorPreset.CurveCustom, s.Preset);

            s.Preset = ProjectorPreset.Curve; // reset to prove the End edit also switches it
            ProjectorAnchorEditor.SetOutput(s, AnchorSlot.End, 80.0);
            Assert.Equal(80.0, s.EndOutput);
            Assert.Equal(ProjectorPreset.CurveCustom, s.Preset);

            Assert.Equal(15.0, ProjectorAnchorEditor.GetOutput(s, AnchorSlot.Start));
            Assert.Equal(80.0, ProjectorAnchorEditor.GetOutput(s, AnchorSlot.End));
        }

        [Fact]
        public void An_edited_Linear_preset_stays_Linear_flavoured()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.ApplyPreset(ProjectorPreset.Linear, ProjectionChannel.Lock);
            Assert.Equal(ProjectorPreset.Linear, s.Preset);

            ProjectorAnchorEditor.SetOutput(s, AnchorSlot.Start, 12.0);
            Assert.Equal(ProjectorPreset.LinearCustom, s.Preset);

            // Editing again does not walk it on to some other state.
            ProjectorAnchorEditor.SetOutput(s, AnchorSlot.End, 90.0);
            Assert.Equal(ProjectorPreset.LinearCustom, s.Preset);
        }

        [Fact]
        public void A_legacy_Custom_from_an_older_save_is_left_alone()
        {
            // Saves written before the two variants existed carry a bare Custom. There is no base shape
            // to recover, so guessing one would be worse than keeping what is there.
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.Preset = ProjectorPreset.Custom;

            ProjectorAnchorEditor.SetOutput(s, AnchorSlot.Start, 5.0);

            Assert.Equal(ProjectorPreset.Custom, s.Preset);
        }

        [Fact]
        public void Defaults_still_ship_zero_and_a_hundred_for_start_and_end_output()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            Assert.Equal(0.0, s.StartOutput);
            Assert.Equal(100.0, s.EndOutput);
        }

        /// <summary>A non-zero StartOutput and a reduced EndOutput are both a CONTINUOUS floor/
        /// ceiling: every input at/below StartInput reads exactly StartOutput (no gap, no jump from
        /// 0), and every input at/above EndInput reads exactly EndOutput. The whole curve stays
        /// monotone non-decreasing throughout.</summary>
        [Fact]
        public void Nonzero_start_output_and_reduced_end_output_are_a_continuous_floor_and_ceiling()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.StartOutput = 15.0;
            s.EndOutput = 80.0;

            var projector = new OutputProjector(s);

            // Continuous floor: every input at/below StartInput (20) reads exactly 15, not 0.
            for (double x = 0.0; x <= 20.0; x += 5.0)
                Assert.Equal(15.0, projector.Project(x), 6);

            // Continuous ceiling: every input at/above EndInput (100) reads exactly 80, not 100.
            Assert.Equal(80.0, projector.Project(100.0), 6);

            // The shaped anchors in between are unaffected (their own outputs sit inside [15, 80]).
            Assert.Equal(s.ModerateOutput, projector.Project(s.ModerateInput), 4);
            Assert.Equal(Math.Min(s.CriticalOutput, 80.0), projector.Project(s.CriticalInput), 4);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 0.5)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        /// <summary>Edge case 1 of 4: StartOutput set ABOVE the first shaped anchor's own output
        /// (StartOutput=30 &gt; SlightlyOutput=10). Per the documented policy this is NOT rejected -
        /// it silently raises every following anchor's own EFFECTIVE output up to at least itself (a
        /// longer flat start), never causing a decrease. Slightly's own configured output (10) is
        /// therefore never actually seen; the curve reads 30 straight through to Moderate.</summary>
        [Fact]
        public void Start_output_above_the_first_anchors_own_output_absorbs_it_into_a_longer_flat_start()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.StartOutput = 30.0; // > SlightlyOutput (10)

            var projector = new OutputProjector(s);

            Assert.Equal(30.0, projector.Project(0.0), 6);
            Assert.Equal(30.0, projector.Project(s.SlightlyInput), 6);
            // Moderate's own output (30) coincides with the raised floor here, so it stays 30 too.
            Assert.Equal(30.0, projector.Project(s.ModerateInput), 6);
            // Beyond Moderate the curve resumes climbing toward Critical/End as normal.
            Assert.Equal(s.CriticalOutput, projector.Project(s.CriticalInput), 4);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 0.5)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        /// <summary>Edge case 2 of 4: EndOutput set BELOW the last shaped anchor's own output
        /// (EndOutput=50 &lt; CriticalOutput). Per the documented policy "cap the maximum" only
        /// actually caps when the requested cap is at or above every anchor's own output - a too-low
        /// EndOutput is itself raised up to whatever height the curve already reached by EndInput; it
        /// can never pull the curve back down. So the curve still reaches CriticalOutput at Critical and
        /// stays there (not 50) all the way to 100.</summary>
        [Fact]
        public void End_output_below_the_last_anchors_own_output_cannot_pull_the_curve_back_down()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.EndOutput = 50.0; // < CriticalOutput (80 on this branch)

            var projector = new OutputProjector(s);

            Assert.Equal(s.CriticalOutput, projector.Project(s.CriticalInput), 4);
            Assert.Equal(s.CriticalOutput, projector.Project(90.0), 6);
            Assert.Equal(s.CriticalOutput, projector.Project(100.0), 6);

            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 0.5)
            {
                double y = projector.Project(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}");
                previous = y;
            }
        }

        /// <summary>Edge case 3 of 4: StartOutput == EndOutput (both 50). Every anchor in between sits
        /// below that shared height, so all of them get absorbed the same way as edge case 1 - the
        /// ENTIRE curve degenerates to a flat line at 50 up until whichever anchor's own configured
        /// output would exceed 50 (none do here, since Critical's own output &gt; 50 only kicks in
        /// after Critical's input). This is a well-defined, non-crashing, trivially-monotone outcome,
        /// not an error.</summary>
        [Fact]
        public void Start_output_equal_to_end_output_degenerates_gracefully_rather_than_crashing()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.StartOutput = 50.0;
            s.EndOutput = 50.0;

            var ex = Record.Exception(() => new OutputProjector(s));
            Assert.Null(ex);

            var projector = new OutputProjector(s);
            Assert.Equal(50.0, projector.Project(0.0), 6);
            Assert.Equal(50.0, projector.Project(s.SlightlyInput), 6);
            Assert.Equal(50.0, projector.Project(s.ModerateInput), 6);
            // Critical's own configured output exceeds the shared 50 floor, so it is honoured.
            Assert.Equal(s.CriticalOutput, projector.Project(s.CriticalInput), 6);
            Assert.Equal(s.CriticalOutput, projector.Project(100.0), 6);
        }

        /// <summary>Edge case 4 of 4: EndOutput &lt; StartOutput (EndOutput=20, StartOutput=80) -
        /// explicitly "invalid" input the brief asked to be documented rather than silently producing
        /// nonsense. Per the documented policy this is resolved the SAME way as every other conflict:
        /// the non-decreasing clamp folds every anchor (all of which sit below 80) up to at least 80,
        /// and the closing (EndInput, EndOutput) point is itself folded up to at least whatever height
        /// the curve already reached - so the WHOLE curve degenerates to a flat line at StartOutput's
        /// own height (80), never dipping to the nominally-configured EndOutput (20). No exception, no
        /// crash, still trivially monotone.</summary>
        [Fact]
        public void End_output_below_start_output_flattens_the_entire_curve_at_the_start_output_height()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.StartOutput = 80.0;
            s.EndOutput = 20.0;

            var ex = Record.Exception(() => new OutputProjector(s));
            Assert.Null(ex);

            var projector = new OutputProjector(s);
            foreach (double x in new[] { 0.0, s.SlightlyInput, s.ModerateInput, s.CriticalInput, 100.0 })
                Assert.Equal(80.0, projector.Project(x), 6);
        }

        /// <summary>The per-anchor flatten-range plateaus (Change 2b) must never push the curve
        /// outside a configured non-default Start/End output band - a plateau near either end is still
        /// just hidden CONTROL POINTS folded through the SAME non-decreasing clamp as everything else,
        /// so it cannot escape the [StartOutput, EndOutput] envelope the driver configured.</summary>
        [Fact]
        public void A_flatten_plateau_near_either_end_never_escapes_a_configured_start_end_output_band()
        {
            var s = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            s.StartOutput = 15.0;
            s.EndOutput = 90.0;
            // Widen the ranges so the plateaus reach close to Start/End.
            s.SlightlyFlattenRange = 5.0;
            s.CriticalFlattenRange = 5.0;

            var projector = new OutputProjector(s);

            for (double x = 0.0; x <= 100.0; x += 0.5)
                Assert.InRange(projector.Project(x), 15.0 - 1e-9, 90.0 + 1e-9);
        }

        [Theory]
        [InlineData(ProjectionChannel.Lock)]
        [InlineData(ProjectionChannel.Slip)]
        public void ApplyPreset_resets_start_and_end_output_to_zero_and_a_hundred_on_both_channels(ProjectionChannel channel)
        {
            var s = Defaults();
            s.StartOutput = 42.0;
            s.EndOutput = 55.0;

            s.ApplyPreset(ProjectorPreset.Curve, channel);

            Assert.Equal(0.0, s.StartOutput);
            Assert.Equal(100.0, s.EndOutput);
        }
    }
}

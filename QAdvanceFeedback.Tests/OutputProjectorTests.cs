using System;
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
            Assert.InRange(projector.Project(60.0), 29.5, 30.5);
            Assert.InRange(projector.Project(80.0), 79.5, 80.5);
        }

        [Fact]
        public void Curve_default_at_seventy_lands_in_moderate_shaking_band()
        {
            var projector = new OutputProjector(Defaults());
            double y = projector.Project(70.0);
            Assert.InRange(y, 40.0, 60.0);
        }

        [Fact]
        public void Curve_default_rises_steeply_between_moderate_and_critical()
        {
            var projector = new OutputProjector(Defaults());
            double atThirty = projector.Project(30.0);
            double atSixty = projector.Project(60.0);
            double atEighty = projector.Project(80.0);

            double lightRise = atSixty - atThirty;
            double steepRise = atEighty - atSixty;

            Assert.True(steepRise >= 2 * lightRise,
                $"steep rise {steepRise} was not at least twice the light rise {lightRise}");
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
            Assert.Equal(30.0, projector.Project(60.0), 6);
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

            // Same anchor INPUT positions as Lock (30/60/80/100 - owner's shared band boundaries,
            // see docs\refinements-report.md) - only the OUTPUT values (the actual feel) differ.
            Assert.Equal(20.0, slip.StartInput);
            Assert.Equal(30.0, slip.SlightlyInput);
            Assert.Equal(8.0, slip.SlightlyOutput);
            Assert.Equal(60.0, slip.ModerateInput);
            Assert.Equal(20.0, slip.ModerateOutput);
            Assert.Equal(80.0, slip.CriticalInput);
            Assert.Equal(75.0, slip.CriticalOutput);

            Assert.Equal(20.0, lockChannel.StartInput);
            Assert.Equal(30.0, lockChannel.SlightlyInput);
            Assert.Equal(10.0, lockChannel.SlightlyOutput);
            Assert.Equal(60.0, lockChannel.ModerateInput);
            Assert.Equal(30.0, lockChannel.ModerateOutput);
            Assert.Equal(80.0, lockChannel.CriticalInput);
            Assert.Equal(80.0, lockChannel.CriticalOutput);

            Assert.Equal(lockChannel.ModerateInput, slip.ModerateInput); // shared band boundary
            Assert.Equal(lockChannel.CriticalInput, slip.CriticalInput); // shared band boundary
            Assert.NotEqual(lockChannel.SlightlyOutput, slip.SlightlyOutput);
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
            Assert.InRange(projector.Project(30.0), 7.5, 8.5);
            Assert.InRange(projector.Project(60.0), 19.5, 20.5);
            Assert.InRange(projector.Project(80.0), 74.5, 75.5);
        }

        // ------------------------------------------------------------------------------------
        // Band-semantics verification (owner's Task 3): numerically confirm, for BOTH channels'
        // shipped Curve defaults, that "at the limit" (75-80 raw) reads a strong-but-not-maximum
        // output and 100 reads exactly maximum - see docs\refinements-report.md for the full
        // numeric table (0/30/60/75/80/100) and the independent Python cross-check.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Lock_curve_default_puts_near_the_limit_around_75_to_80_and_fully_locked_at_100()
        {
            var s = new ProjectorSettings(); // Lock's own field defaults
            var projector = new OutputProjector(s);

            Assert.InRange(projector.Project(0.0), 0.0, 0.0);
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);   // 30-60 ideal zone starts weak
            Assert.InRange(projector.Project(60.0), 29.5, 30.5);  // ideal zone's own top edge
            Assert.InRange(projector.Project(75.0), 65.0, 72.0);  // 60-80: clearly ramping up already
            Assert.InRange(projector.Project(80.0), 79.5, 80.5);  // at the limit - strong, not yet max
            Assert.Equal(100.0, projector.Project(100.0), 6);     // fully locked - always exactly max
        }

        [Fact]
        public void Slip_curve_default_puts_near_the_limit_around_75_to_80_and_fully_spinning_at_100()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);
            var projector = new OutputProjector(s);

            Assert.InRange(projector.Project(0.0), 0.0, 0.0);
            Assert.InRange(projector.Project(30.0), 7.5, 8.5);
            Assert.InRange(projector.Project(60.0), 19.5, 20.5);
            Assert.InRange(projector.Project(75.0), 55.0, 65.0);
            Assert.InRange(projector.Project(80.0), 74.5, 75.5);
            Assert.Equal(100.0, projector.Project(100.0), 6);
        }

        [Fact]
        public void Preset_custom_with_curve_default_anchors_behaves_like_curve()
        {
            var s = Defaults();
            s.Preset = ProjectorPreset.Custom;

            var projector = new OutputProjector(s);
            Assert.InRange(projector.Project(30.0), 9.5, 10.5);
            Assert.InRange(projector.Project(60.0), 29.5, 30.5);
            Assert.InRange(projector.Project(80.0), 79.5, 80.5);
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
            Assert.InRange(projector.Project(60.0), 29.5, 30.5);
            Assert.InRange(projector.Project(80.0), 79.5, 80.5);
        }

        [Fact]
        public void Default_curve_preset_sends_expected_values_slip_channel()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.Slip);

            var projector = new OutputProjector(s);
            Assert.InRange(projector.Project(30.0), 7.5, 8.5);
            Assert.InRange(projector.Project(60.0), 19.5, 20.5);
            Assert.InRange(projector.Project(80.0), 74.5, 75.5);
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
        [InlineData(ProjectorPreset.Curve, ProjectionChannel.Lock)]
        [InlineData(ProjectorPreset.Linear, ProjectionChannel.Slip)]
        [InlineData(ProjectorPreset.Curve, ProjectionChannel.Slip)]
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
    }
}

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>Named shaping presets for <see cref="OutputProjector"/>. Ported verbatim from the
    /// sibling ReliableWheelLockSlip project's Core/OutputProjector.cs.</summary>
    /// <summary>Preset helpers - see <see cref="ProjectorPreset"/>.</summary>
    public static class ProjectorPresetExtensions
    {
        /// <summary>
        /// Whether this preset is a LINEAR shape, customised or not.
        /// <para/>
        /// Everything that used to test <c>Preset != Linear</c> has to ask this instead: once editing a
        /// Linear preset produced <see cref="ProjectorPreset.LinearCustom"/> rather than a bare Custom,
        /// a plain inequality started treating an edited straight line as a curve - which turned the
        /// flatten plateaus back on and stopped it being linear at all.
        /// </summary>
        public static bool IsLinearShape(this ProjectorPreset preset)
            => preset == ProjectorPreset.Linear || preset == ProjectorPreset.LinearCustom;
    }

    public enum ProjectorPreset
    {
        Linear,
        Curve,

        /// <summary>Legacy single "edited" state. Still deserialises from saves written before the two
        /// preset-specific variants below existed, and is treated as <see cref="CurveCustom"/> for
        /// display; nothing writes it any more.</summary>
        Custom,

        /// <summary>Linear, then edited. Kept distinct from <see cref="CurveCustom"/> so the dropdown
        /// still says which shape the driver started from - editing one anchor of a Linear preset used to
        /// collapse it to a bare "Custom", losing that.</summary>
        LinearCustom,

        /// <summary>Curve, then edited - see <see cref="LinearCustom"/>.</summary>
        CurveCustom
    }

    /// <summary>
    /// Which of this plugin's two channels a <see cref="ProjectorSettings"/> belongs to - the
    /// direct equivalent of the sibling project's <c>SlipDirection</c> (Braking/Traction), renamed
    /// to match THIS plugin's own published names (WheelLock/WheelSlip) rather than importing the
    /// sibling's terminology verbatim.
    /// </summary>
    public enum ProjectionChannel { Lock, Slip }

    /// <summary>
    /// User-editable output-shaping settings: an optional start point below which nothing is felt,
    /// and three named anchors (Slightly/Ideal/Critical - user-visible label "Ideal"; the backing
    /// properties below keep their original "Moderate" C# names deliberately, since renaming them
    /// would rename the persisted JSON keys a driver's saved config.json already uses - see
    /// docs\refinements-report.md) whose input positions are independently
    /// movable -- the named labels travel with wherever the driver puts them.
    /// <para/>
    /// Ported verbatim from the sibling ReliableWheelLockSlip project's
    /// Core/OutputProjector.cs::ProjectorSettings (per the brief: "port the sibling project's output
    /// projector wholesale ... bring across ... the established defaults"), with only
    /// <c>SlipDirection</c> renamed to <see cref="ProjectionChannel"/> (Braking -&gt; Lock, Traction
    /// -&gt; Slip) to match this plugin's own channel names.
    /// </summary>
    public sealed class ProjectorSettings
    {
        public ProjectorPreset Preset { get; set; } = ProjectorPreset.Curve;

        public double StartInput { get; set; } = 0.0;

        /// <summary>The input at and above which the output is always full (<see cref="EndOutput"/>) --
        /// the mirror image of <see cref="StartInput"/>.</summary>
        public double EndInput { get; set; } = 100.0;

        // ---- CONFIGURABLE START/END OUTPUTS (pre-release addition, owner's own explicit request: "what
        // is the maximum output they want to have, and what the start value they want to build"). Used
        // to be hard-fixed at 0/100; now driver-editable, defaulting to the same 0/100. CONTINUOUS FLOOR/
        // CEILING, not a step: every input AT OR BELOW StartInput reads exactly StartOutput (not 0 then a
        // jump), and every input AT OR ABOVE EndInput reads exactly EndOutput - see
        // OutputProjector.BuildControlPoints' own remarks for why a discontinuous "0 then jump" reading
        // was rejected (this project has repeatedly required continuity with no steps, and a jump would
        // be felt as a click exactly where the effect begins). CONSEQUENCE A DRIVER MUST UNDERSTAND: the
        // pedal trigger threshold still gates engagement entirely (no output at all when not braking/
        // accelerating), but ONCE engaged, a non-zero StartOutput is a PERMANENT baseline hum for the
        // entire time the channel is engaged, not merely a raised floor on the ramp - see this class's
        // own help-text string for the exact wording surfaced to the driver.
        /// <summary>The output for every input at or below <see cref="StartInput"/> - a continuous
        /// floor, not a step (see this class's own remarks). Defaults to 0 (today's shipped behaviour).
        /// Useful when a motor has a dead zone and a small value would otherwise be inaudible.</summary>
        public double StartOutput { get; set; } = 0.0;

        /// <summary>The output for every input at or above <see cref="EndInput"/> - a continuous
        /// ceiling, not a step. Defaults to 100 (today's shipped behaviour). Caps the maximum strength
        /// so the effect never becomes harsher than the driver wants.</summary>
        public double EndOutput { get; set; } = 100.0;

        public double SlightlyInput { get; set; } = 30.0;
        public double SlightlyOutput { get; set; } = 10.0;

        // Pre-release Change 2c - threshold moved 60 -> 62 (owner's own explicit band-alignment
        // request: with a flatten range of 2, this puts the Ideal plateau's own LOWER edge at exactly
        // 60, the shared band boundary, rather than straddling it). See ApplyPreset(Curve)'s own
        // remarks for the full "smooth 60-80, steep outside it" intent.
        public double ModerateInput { get; set; } = 62.0;
        public double ModerateOutput { get; set; } = 30.0;

        // Pre-release Change 2c - threshold moved 80 -> 78 (same band-alignment reasoning as
        // ModerateInput above: with a flatten range of 2, the Max Grip plateau's own UPPER edge lands
        // at exactly 80).
        public double CriticalInput { get; set; } = 78.0;

        // 1.0.6.0 (docs\release-1060-report.md, Part 4d) - CHANGED to 60, matching the sibling 1.0.6.2
        // pre-release branch's own value ("otherwise it will shake too strong when reaches the best
        // braking force"). Previously (1.0.6.8 and its own curve/G-Force lineage) this stayed at 80 as a
        // deliberate, separate decision on that branch; the owner's own 1.0.6.0 brief explicitly asks for
        // this branch's Max Grip output to become 60 now, matching 1.0.6.2.
        public double CriticalOutput { get; set; } = 60.0;

        // ---- CONFIGURABLE FLATTEN RANGES (pre-release Change 2b - the owner's own explicit,
        // per-setpoint plateau-width request, replacing an earlier implicit-curve-shape instruction).
        // Each of the three named setpoints gets its OWN input-range half-width; <see cref="OutputProjector"/>
        // uses it to insert two HIDDEN control points (setpointInput-Range, setpointInput+Range) with
        // outputs derived from the setpoint's own neighbouring anchors, so the input band
        // setpointInput+/-Range reads as a near-flat plateau. Defaults (3/2/2) and their derivation are
        // the owner's own worked examples - see OutputProjector.BuildControlPoints' own remarks for the
        // exact formula. PRECISION on the range-0 case (owner's own explicit follow-up): a range of 0
        // means this setpoint's two hidden control points are OMITTED ENTIRELY, not created at a
        // zero offset (i.e. both sitting on top of the setpoint's own input) - a zero-offset point is
        // NOT equivalent, since duplicate/near-duplicate x values perturb the monotone-cubic fit's own
        // computed tangents even though the points coincide with the anchor. With ALL THREE ranges at
        // 0 AND the anchor inputs at their pre-Change-2c positions (30/60/80), the evaluated curve is
        // therefore BIT-IDENTICAL to the curve this project produced before this flatten-range feature
        // existed at all - see OutputProjector.AcceptSetpointWithFlatten's own `range > 0.0` guard,
        // which is what actually skips the hidden-point creation, and
        // OutputProjectorTests.All_flatten_ranges_at_zero_with_the_original_30_60_80_inputs_is_bit_identical_to_the_pre_flatten_curve
        // for the pinned regression. PER-ANCHOR, NOT GLOBAL: each of the three ranges is independent -
        // one setpoint's range being 0 has no effect on whether a SIBLING setpoint's own hidden points
        // are created (see OutputProjectorTests.A_zero_range_on_one_anchor_does_not_suppress_a_sibling_anchors_own_nonzero_range).
        // NEGATIVE ranges are CLAMPED to 0 (never rejected/thrown, never treated as a magnitude) by
        // ClampNonNegative below - bit-identical to an explicit 0, not merely "close". FRACTIONAL
        // ranges (e.g. 1.5) are honoured EXACTLY as given, with no rounding to a whole number anywhere -
        // the range is a plain double used directly in OutputProjector's own arithmetic.
        // Pre-release Change 2c - Ideal/Max Grip ranges narrowed 5/7 -> 2/2 (owner's own explicit
        // revision, paired with the ModerateInput/CriticalInput threshold moves above): "we will get
        // the smooth range between 60 to 80, with flatten on the edge of 60 and 80, when out of the 60
        // to 80 range, the value will ramp up/down quick."
        public double SlightlyFlattenRange { get; set; } = 3.0;
        public double ModerateFlattenRange { get; set; } = 2.0;
        public double CriticalFlattenRange { get; set; } = 2.0;

        /// <summary>
        /// Sets both input and output columns for the named presets. <see cref="ProjectorPreset.Custom"/>
        /// only flags the preset and leaves whatever the driver already entered untouched.
        /// <paramref name="channel"/> defaults to <see cref="ProjectionChannel.Lock"/>; the Slip
        /// channel has its own, deliberately gentler/earlier anchor defaults (see
        /// <see cref="Settings.WheelChannelSettings"/>'s field initialisers) because a traction cue
        /// wants to come in earlier and land more gently than a braking one.
        /// </summary>
        public void ApplyPreset(ProjectorPreset preset, ProjectionChannel channel = ProjectionChannel.Lock)
        {
            Preset = preset;

            if (channel == ProjectionChannel.Slip)
            {
                switch (preset)
                {
                    case ProjectorPreset.Linear:
                        // Owner-verified band semantics (see docs\refinements-report.md): the SAME
                        // three anchor INPUT positions (30/60/80) apply to both channels - 0-30
                        // light/margin, 30-60 the ideal working range, 60-80 losing traction (not yet
                        // spinning, ease off now), 100 fully spinning. Previously this used 30/45/75,
                        // which did not match the owner's stated bands - fixed here.
                        SlightlyInput = 30.0; SlightlyOutput = OnLineFromStartToEnd(30.0);
                        ModerateInput = 60.0; ModerateOutput = OnLineFromStartToEnd(60.0);
                        CriticalInput = 80.0; CriticalOutput = OnLineFromStartToEnd(80.0);
                        ResetFlattenRangesToDefault();
                        break;

                    case ProjectorPreset.Curve:
                        // Same anchor INPUT positions as Lock (30/62/78/100 - owner's shared band
                        // boundaries, thresholds moved 60/80 -> 62/78 by pre-release Change 2c so the
                        // flatten-range plateau edges land on the shared 60/80 boundaries); only the
                        // OUTPUT values differ. REVISED, docs\slip-source-consistency-report.md (a
                        // second round of owner seat-testing): 8/20/75 -> 10/35/75 - the "Ideal"
                        // (60-input) output rose the most (20 -> 35), no longer dramatically gentler
                        // than Lock's own (10/30/80) in the working range, while Critical (80-input ->
                        // 75) is unchanged and still sits below Lock's own Critical output - full spin
                        // is still not the same intensity as full lock by the owner's own choice, only
                        // the mid-range softness was reduced. Slip's OUTPUTS (10/35/75) were UNCHANGED
                        // by that port - only the threshold positions moved, matching Lock's own
                        // 62/78 band-alignment.
                        //
                        // CHANGE (this build, owner-confirmed after explicit follow-up question):
                        // Critical (Max Grip) OUTPUT softened 75 -> 70. Direct, deliberate response to
                        // the owner's own in-game report that with the ShakeIt source, WheelSlip
                        // "shakes much harder than using Raw" - lowering the ceiling on the strongest
                        // Slip cue reduces how hard the top of the curve can ever hit, on both sources,
                        // without touching WheelLock (WheelLock's own Critical output stays 60 - the
                        // owner was explicit that only Slip's max-grip output should move here). Slip's
                        // severity FORMULA itself is deliberately untouched (still 1.0.6.3's, already
                        // validated "generally good" on both Raw and ShakeIt) - this is an output-curve
                        // ceiling change only, not a severity-math change.
                        StartInput = 20.0;
                        EndInput = 100.0;
                        SlightlyInput = 30.0; SlightlyOutput = 10.0;
                        ModerateInput = 62.0; ModerateOutput = 35.0;
                        CriticalInput = 78.0; CriticalOutput = 70.0;
                        ResetFlattenRangesToDefault();
                        break;

                    case ProjectorPreset.Custom:
                    default:
                        break;
                }
                return;
            }

            switch (preset)
            {
                case ProjectorPreset.Linear:
                    // Linear means "a straight line from the start point to the end point", so the
                    // anchor outputs are placed ON that line rather than mirroring their inputs.
                    // StartInput/EndInput are deliberately left ALONE -- this preset adapts to
                    // whatever range the driver has set rather than overwriting it.
                    SlightlyInput = 30.0; SlightlyOutput = OnLineFromStartToEnd(30.0);
                    ModerateInput = 60.0; ModerateOutput = OnLineFromStartToEnd(60.0);
                    CriticalInput = 80.0; CriticalOutput = OnLineFromStartToEnd(80.0);
                    ResetFlattenRangesToDefault();
                    break;

                case ProjectorPreset.Curve:
                    // Curve is an opinionated, complete shape: it pins BOTH ends of the range
                    // (StartInput to 20, EndInput to 100) rather than adapting to whatever the
                    // driver had set, unlike Linear above.
                    //
                    // Pre-release Change 2c - Ideal/Max Grip THRESHOLDS moved 60/80 -> 62/78 (paired
                    // with flatten ranges of 2 each below) so the plateau EDGES still land on the
                    // shared band boundaries 60/80: "we will get the smooth range between 60 to 80,
                    // with flatten on the edge of 60 and 80". CriticalOutput is 60 (docs\
                    // release-1060-report.md, Part 4d) - matching the sibling 1.0.6.2 branch's own
                    // value, per the owner's own 1.0.6.0 request.
                    StartInput = 20.0;
                    EndInput = 100.0;
                    SlightlyInput = 30.0; SlightlyOutput = 10.0;
                    ModerateInput = 62.0; ModerateOutput = 30.0;
                    CriticalInput = 78.0; CriticalOutput = 60.0;
                    ResetFlattenRangesToDefault();
                    break;

                case ProjectorPreset.Custom:
                default:
                    break;
            }
        }

        /// <summary>Restores the three flatten ranges AND the Start/End outputs to their shipped
        /// defaults (3/2/2 and 0/100) - called by every non-Custom <see cref="ApplyPreset"/> branch
        /// (both channels, both Linear and Curve) so "restore all default settings" resets these too,
        /// not just the anchor inputs/outputs.</summary>
        private void ResetFlattenRangesToDefault()
        {
            SlightlyFlattenRange = 3.0;
            ModerateFlattenRange = 2.0;
            CriticalFlattenRange = 2.0;
            StartOutput = 0.0;
            EndOutput = 100.0;
        }

        /// <summary>
        /// The output at <paramref name="input"/> on the straight line running from
        /// (<see cref="StartInput"/>, 0) to (<see cref="EndInput"/>, 100) -- the shape the
        /// <see cref="ProjectorPreset.Linear"/> preset places its anchors on.
        /// </summary>
        private double OnLineFromStartToEnd(double input)
        {
            double start = ClampedOrZero(StartInput);
            double end = ClampedOrZero(EndInput);
            double span = end - start;
            if (span <= 0.0) return 0.0;

            double output = (input - start) / span * 100.0;
            return ClampedOrZero(output);
        }

        /// <summary>
        /// Returns a copy with every value clamped to [0,100] -- non-finite (NaN, +/-Infinity)
        /// becomes 0 -- and the three anchors reordered by input. Does NOT enforce strictly-
        /// increasing inputs, force outputs non-decreasing, or append a closing (100,100) point;
        /// <see cref="OutputProjector"/> applies those rules when it builds control points.
        /// </summary>
        public ProjectorSettings WithClampedSortedAnchors()
        {
            double start = ClampedOrZero(StartInput);
            double end = ClampedOrZero(EndInput);
            double startOutput = ClampedOrZero(StartOutput);
            double endOutput = ClampedOrZero(EndOutput);

            double ax0 = ClampedOrZero(SlightlyInput), ay0 = ClampedOrZero(SlightlyOutput), ar0 = ClampNonNegative(SlightlyFlattenRange);
            double ax1 = ClampedOrZero(ModerateInput), ay1 = ClampedOrZero(ModerateOutput), ar1 = ClampNonNegative(ModerateFlattenRange);
            double ax2 = ClampedOrZero(CriticalInput), ay2 = ClampedOrZero(CriticalOutput), ar2 = ClampNonNegative(CriticalFlattenRange);

            var xs = new[] { ax0, ax1, ax2 };
            var ys = new[] { ay0, ay1, ay2 };
            var rs = new[] { ar0, ar1, ar2 };

            for (int i = 1; i < xs.Length; i++)
            {
                double kx = xs[i], ky = ys[i], kr = rs[i];
                int j = i - 1;
                while (j >= 0 && xs[j] > kx)
                {
                    xs[j + 1] = xs[j];
                    ys[j + 1] = ys[j];
                    rs[j + 1] = rs[j];
                    j--;
                }
                xs[j + 1] = kx;
                ys[j + 1] = ky;
                rs[j + 1] = kr;
            }

            return new ProjectorSettings
            {
                Preset = Preset,
                StartInput = start,
                EndInput = end,
                StartOutput = startOutput,
                EndOutput = endOutput,
                SlightlyInput = xs[0],
                SlightlyOutput = ys[0],
                SlightlyFlattenRange = rs[0],
                ModerateInput = xs[1],
                ModerateOutput = ys[1],
                ModerateFlattenRange = rs[1],
                CriticalInput = xs[2],
                CriticalOutput = ys[2],
                CriticalFlattenRange = rs[2],
            };
        }

        private static double ClampedOrZero(double v) => ClampMath.IsFinite(v) ? ClampMath.To0100(v) : 0.0;

        /// <summary>Non-negative clamp for a flatten range - same convention as
        /// <see cref="Core.AggregationWeights"/>'s own weight clamping (never negative; non-finite
        /// becomes 0, the "no flattening" identity).</summary>
        private static double ClampNonNegative(double v) => ClampMath.IsFinite(v) && v > 0.0 ? v : 0.0;

        /// <summary>
        /// A fresh <see cref="ProjectorSettings"/> stamped with this channel's own SHIPPED default
        /// curve (the <see cref="ProjectorPreset.Curve"/> preset - what a brand new install, or
        /// "Restore all default settings", actually ships - see <see cref="Settings.WheelChannelSettings.CreateDefaults"/>,
        /// which calls this exact preset). Exists so the settings UI's help text can quote the real
        /// shipped numbers by reading them from THIS single source of truth rather than re-typing them
        /// a second time - the defaults have drifted more than once during this project (see
        /// docs\refinements-report.md, docs\curve-help-text-report.md), and a hand-copied number in a
        /// help string cannot be caught by the compiler when that happens again.
        /// </summary>
        public static ProjectorSettings CreateShippedDefault(ProjectionChannel channel)
        {
            var settings = new ProjectorSettings();
            settings.ApplyPreset(ProjectorPreset.Curve, channel);
            return settings;
        }
    }
}

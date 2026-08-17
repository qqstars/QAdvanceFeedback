namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>Named shaping presets for <see cref="OutputProjector"/>. Ported verbatim from the
    /// sibling ReliableWheelLockSlip project's Core/OutputProjector.cs.</summary>
    public enum ProjectorPreset
    {
        Linear,
        Curve,
        Custom
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

        /// <summary>The input at and above which the output is always full (100) -- the mirror
        /// image of <see cref="StartInput"/>. Defaults to 100, which is the top of the output range
        /// by definition, exactly as <see cref="StartInput"/>'s output is always 0 by definition.</summary>
        public double EndInput { get; set; } = 100.0;

        public double SlightlyInput { get; set; } = 30.0;
        public double SlightlyOutput { get; set; } = 10.0;

        public double ModerateInput { get; set; } = 60.0;
        public double ModerateOutput { get; set; } = 30.0;

        public double CriticalInput { get; set; } = 80.0;
        public double CriticalOutput { get; set; } = 80.0;

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
                        break;

                    case ProjectorPreset.Curve:
                        // Same anchor INPUT positions as Lock (30/60/80/100 - see the Linear case's
                        // own remarks on why); only the OUTPUT values differ, kept gentler/lower than
                        // Lock's own (10/30/80) throughout so slip still comes in more softly, matching
                        // the sibling project's own traction-curve character.
                        StartInput = 20.0;
                        EndInput = 100.0;
                        SlightlyInput = 30.0; SlightlyOutput = 8.0;
                        ModerateInput = 60.0; ModerateOutput = 20.0;
                        CriticalInput = 80.0; CriticalOutput = 75.0;
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
                    break;

                case ProjectorPreset.Curve:
                    // Curve is an opinionated, complete shape: it pins BOTH ends of the range
                    // (StartInput to 20, EndInput to 100) rather than adapting to whatever the
                    // driver had set, unlike Linear above.
                    StartInput = 20.0;
                    EndInput = 100.0;
                    SlightlyInput = 30.0; SlightlyOutput = 10.0;
                    ModerateInput = 60.0; ModerateOutput = 30.0;
                    CriticalInput = 80.0; CriticalOutput = 80.0;
                    break;

                case ProjectorPreset.Custom:
                default:
                    break;
            }
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

            double ax0 = ClampedOrZero(SlightlyInput), ay0 = ClampedOrZero(SlightlyOutput);
            double ax1 = ClampedOrZero(ModerateInput), ay1 = ClampedOrZero(ModerateOutput);
            double ax2 = ClampedOrZero(CriticalInput), ay2 = ClampedOrZero(CriticalOutput);

            var xs = new[] { ax0, ax1, ax2 };
            var ys = new[] { ay0, ay1, ay2 };

            for (int i = 1; i < xs.Length; i++)
            {
                double kx = xs[i], ky = ys[i];
                int j = i - 1;
                while (j >= 0 && xs[j] > kx)
                {
                    xs[j + 1] = xs[j];
                    ys[j + 1] = ys[j];
                    j--;
                }
                xs[j + 1] = kx;
                ys[j + 1] = ky;
            }

            return new ProjectorSettings
            {
                Preset = Preset,
                StartInput = start,
                EndInput = end,
                SlightlyInput = xs[0],
                SlightlyOutput = ys[0],
                ModerateInput = xs[1],
                ModerateOutput = ys[1],
                CriticalInput = xs[2],
                CriticalOutput = ys[2],
            };
        }

        private static double ClampedOrZero(double v) => ClampMath.IsFinite(v) ? ClampMath.To0100(v) : 0.0;

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

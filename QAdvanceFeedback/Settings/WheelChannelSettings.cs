using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeIt;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Everything Layers 4/5 need for ONE channel (Wheel Lock or Wheel Slip), independently of the
    /// other - one instance per channel (see <see cref="QAdvanceFeedbackSettings.Lock"/>/
    /// <see cref="QAdvanceFeedbackSettings.Slip"/>), not a single shared instance, exactly per the
    /// brief ("PER CHANNEL (lock and slip independently)").
    /// <para/>
    /// Holds three independent things:
    /// <list type="bullet">
    /// <item>Layer 4's four per-wheel SOURCE fields (only FrontLeft/FrontRight/RearLeft/RearRight are
    /// configurable - All/Front/Rear/Left/Right are always aggregated from them, confirmed in the
    /// brief: "the algorithm does not provide them natively") plus each field's own
    /// <see cref="ScriptType"/> (always <see cref="Settings.ScriptType.Plain"/> under either shipped
    /// default) and <see cref="SourceMode"/> (default <see cref="Settings.SourceMode.ShakeIt"/> - see
    /// that property's own remarks; <see cref="Settings.SourceMode.Manual"/>'s own default is a PLAIN
    /// reference to the matching Layer 3 Raw property - see <see cref="DefaultWheelSources"/>).</item>
    /// <item>Layer 5's curve (<see cref="Projector"/>, a <see cref="ProjectorSettings"/> - ported
    /// wholesale from the sibling project, see that class's remarks).</item>
    /// <item>Layer 5's pulse (<see cref="Pulse"/>, a <see cref="PulseSettings"/>).</item>
    /// </list>
    /// Public settable properties with a parameterless constructor (Newtonsoft convention, matching
    /// every other settings POCO in this plugin family) - <see cref="CreateLockDefaults"/>/
    /// <see cref="CreateSlipDefaults"/> are what actually stamp the two channels' DIFFERENT shipped
    /// numbers (a plain parameterless <c>new WheelChannelSettings()</c> alone cannot, since one class
    /// is reused for both channels and C# field initialisers cannot vary per call site) - the root
    /// <see cref="QAdvanceFeedbackSettings"/> object calls these, never relies on the bare constructor
    /// for its own field defaults.
    /// </summary>
    public sealed class WheelChannelSettings
    {
        public string SourceFrontLeft { get; set; }
        public string SourceFrontRight { get; set; }
        public string SourceRearLeft { get; set; }
        public string SourceRearRight { get; set; }

        public ScriptType ScriptTypeFrontLeft { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeFrontRight { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeRearLeft { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeRearRight { get; set; } = ScriptType.Plain;

        /// <summary>
        /// Manual (this channel's four Source*/ScriptType* fields above, pointed at Layer 3's own Raw
        /// property) vs. ShakeIt (SimHub's own ShakeIt Motors export - see
        /// <see cref="ShakeItPropertyNames"/>).
        /// <para/>
        /// DEFAULT IS <see cref="Settings.SourceMode.ShakeIt"/> - per the owner's explicit instruction
        /// ("Default, globally: ShakeIt Plugin Output Properties"), NOT Manual. A fresh install (and
        /// "Restore all default settings") therefore ships both channels already pointed at the four
        /// confirmed ShakeIt Motors export names (see <see cref="ApplyShakeItDefaults"/>) - the settings
        /// UI always shows the toggle and, when the export is not yet configured, an inline note
        /// explaining how to produce it (see <c>docs\shakeit-export-guide.md</c>); the plugin never
        /// silently reads a missing export as 0 either way (see <see cref="WheelSourceResolver"/>'s
        /// fallback to this channel's own Raw value).
        /// </summary>
        public SourceMode SourceMode { get; set; } = SourceMode.ShakeIt;

        private double _brakeThresholdPercent;
        private double _throttleThresholdPercent;

        /// <summary>Brake-pedal-pressed threshold (0-100) gating this channel - see
        /// <see cref="Core.LegacyThresholds"/>. For the Lock channel this is its own, only pedal gate;
        /// for the Slip channel it is checked FIRST, with priority over
        /// <see cref="ThrottleThresholdPercent"/> (see <see cref="Core.LegacyThresholds"/>'s own
        /// remarks on why). Applies in BOTH <see cref="Settings.SourceMode.Manual"/> and
        /// <see cref="Settings.SourceMode.ShakeIt"/> - it gates Layer 3's own algorithm, upstream of
        /// which Layer 4 source is selected.</summary>
        public double BrakeThresholdPercent
        {
            get => _brakeThresholdPercent;
            set => _brakeThresholdPercent = ClampMath.To0100(value);
        }

        /// <summary>Throttle-pedal-pressed threshold (0-100) - only meaningful for the Slip channel
        /// (Lock never reads it); see <see cref="BrakeThresholdPercent"/>'s remarks on priority.</summary>
        public double ThrottleThresholdPercent
        {
            get => _throttleThresholdPercent;
            set => _throttleThresholdPercent = ClampMath.To0100(value);
        }

        public ProjectorSettings Projector { get; set; } = new ProjectorSettings();

        public PulseSettings Pulse { get; set; } = new PulseSettings();

        /// <summary>
        /// The Wheel Lock channel's shipped defaults: sources point at Layer 3's own
        /// <c>WheelLock.Raw.*</c> properties (plain references), and the curve is the brief's lock
        /// preset (start 20, end 100, 30-&gt;10, 60-&gt;30, 80-&gt;80). Pulse ships disabled - see
        /// <see cref="PulseSettings.Enabled"/>'s remarks.
        /// </summary>
        public static WheelChannelSettings CreateLockDefaults() => CreateDefaults(isLockChannel: true);

        /// <summary>
        /// The Wheel Slip channel's shipped defaults: sources point at Layer 3's own
        /// <c>WheelSlip.Raw.*</c> properties, and the curve is the brief's slip preset (start 20, end
        /// 100, 30-&gt;8, 45-&gt;25, 75-&gt;75) - earlier and gentler than the lock channel's, exactly
        /// as the sibling project's own traction curve is.
        /// </summary>
        public static WheelChannelSettings CreateSlipDefaults() => CreateDefaults(isLockChannel: false);

        private static WheelChannelSettings CreateDefaults(bool isLockChannel)
        {
            var settings = new WheelChannelSettings();
            // Global shipped default is ShakeIt (see SourceMode's own remarks) - NOT
            // ResetSourcesToDefault (which would force Manual/Raw).
            settings.ApplyShakeItDefaults(isLockChannel);
            settings.Projector.ApplyPreset(
                ProjectorPreset.Curve,
                isLockChannel ? ProjectionChannel.Lock : ProjectionChannel.Slip);

            // Owner-requested pedal thresholds (see Core.LegacyThresholds) - Lock only ever reads its
            // own BrakeThresholdPercent; Slip reads both (brake checked first, throttle only if the
            // brake condition is not satisfied - see LegacyThresholds' own remarks). These equal
            // LegacyThresholds.Defaults exactly, duplicated here (rather than read from there) so this
            // class stays a plain settings POCO with no dependency direction back onto the algorithm
            // struct beyond the plain doubles it already needs.
            if (isLockChannel)
            {
                settings.BrakeThresholdPercent = 20.0;
                settings.ThrottleThresholdPercent = 40.0; // unused by Lock, kept at a sane value
            }
            else
            {
                settings.BrakeThresholdPercent = 100.0;   // effectively off - throttle-only by default
                settings.ThrottleThresholdPercent = 40.0;
            }

            return settings;
        }

        /// <summary>Resets all four source fields (and their script types) back to a plain
        /// reference to this channel's Layer 3 Raw property, and switches this channel back to
        /// <see cref="Settings.SourceMode.Manual"/> - the model-level equivalent of the sibling
        /// project's "Reset to default" button (which the settings UI calls).</summary>
        public void ResetSourcesToDefault(bool isLockChannel)
        {
            SourceFrontLeft = DefaultWheelSources.RawPropertyName(isLockChannel, "FrontLeft");
            SourceFrontRight = DefaultWheelSources.RawPropertyName(isLockChannel, "FrontRight");
            SourceRearLeft = DefaultWheelSources.RawPropertyName(isLockChannel, "RearLeft");
            SourceRearRight = DefaultWheelSources.RawPropertyName(isLockChannel, "RearRight");

            ScriptTypeFrontLeft = ScriptType.Plain;
            ScriptTypeFrontRight = ScriptType.Plain;
            ScriptTypeRearLeft = ScriptType.Plain;
            ScriptTypeRearRight = ScriptType.Plain;

            SourceMode = SourceMode.Manual;
        }

        /// <summary>Switches this channel to <see cref="Settings.SourceMode.ShakeIt"/> and points all
        /// four source fields at SimHub's own ShakeIt Motors export names
        /// (<see cref="ShakeItPropertyNames"/>), forcing <see cref="ScriptType.Plain"/> on all four
        /// (a ShakeIt export is always read as a plain property, never scripted).
        /// <para/>
        /// This does NOT check availability, and never has to: the settings UI's toggle is ALWAYS
        /// visible and switchable regardless of whether SimHub currently reports the four expected
        /// properties (the owner's explicit correction - a previous version of this toggle hid itself
        /// whenever availability could not be confirmed, which is why the owner could not find it at
        /// all) - if the export is not yet configured, the UI shows an inline note explaining the setup
        /// steps instead of hiding anything. Either way, <see cref="WheelSourceResolver"/>'s existing
        /// per-source fallback to this channel's own Raw value keeps the plugin's output safe whenever
        /// a configured source (ShakeIt or otherwise) fails to resolve.</summary>
        public void ApplyShakeItDefaults(bool isLockChannel)
        {
            SourceFrontLeft = ShakeItPropertyNames.GetWheelPropertyName(isLockChannel, ShakeItPropertyNames.FrontLeft);
            SourceFrontRight = ShakeItPropertyNames.GetWheelPropertyName(isLockChannel, ShakeItPropertyNames.FrontRight);
            SourceRearLeft = ShakeItPropertyNames.GetWheelPropertyName(isLockChannel, ShakeItPropertyNames.RearLeft);
            SourceRearRight = ShakeItPropertyNames.GetWheelPropertyName(isLockChannel, ShakeItPropertyNames.RearRight);

            ScriptTypeFrontLeft = ScriptType.Plain;
            ScriptTypeFrontRight = ScriptType.Plain;
            ScriptTypeRearLeft = ScriptType.Plain;
            ScriptTypeRearRight = ScriptType.Plain;

            SourceMode = SourceMode.ShakeIt;
        }

        /// <summary>
        /// THE PER-SOURCE "Reset to default" button's actual logic - deliberately distinct from the
        /// GLOBAL "Restore all default settings" button (<see cref="QAdvanceFeedbackSettings.RestoreDefaults"/>),
        /// which always restores the shipped ShakeIt default regardless of what was there before.
        /// <para/>
        /// This one instead respects whichever <see cref="SourceMode"/> is CURRENTLY selected and
        /// restores THAT mode's own defaults, without changing the mode itself - the owner's explicit
        /// requirement: a driver who is in ShakeIt mode and has typed over one of the four property
        /// names gets the four ShakeIt names back (still in ShakeIt mode); a driver in Manual mode who
        /// has customised a source gets this channel's own Raw property names back (still in Manual
        /// mode). It never silently switches the driver to a different mode than the one they were
        /// already using.
        /// </summary>
        public void ResetSourcesForCurrentMode(bool isLockChannel)
        {
            if (SourceMode == SourceMode.ShakeIt) ApplyShakeItDefaults(isLockChannel);
            else ResetSourcesToDefault(isLockChannel);
        }
    }
}

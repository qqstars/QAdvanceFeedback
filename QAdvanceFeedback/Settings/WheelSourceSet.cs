namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// One source mode's own four per-wheel configurations, ARCHIVED so switching the source dropdown
    /// does not destroy what the previous mode was set to.
    /// <para/>
    /// THE REQUIREMENT (owner, 2026-09-28): "when switch to other sources, like Raw, ShakeIt or Viper,
    /// the settings should be dedicated to each source, switching between the sources will NOT derive
    /// the settings from each other."
    /// <para/>
    /// WHY AN ARCHIVE BESIDE THE ACTIVE FIELDS RATHER THAN REPLACING THEM. <see cref="WheelChannelSettings"/>'s
    /// own <c>SourceFrontLeft</c>..<c>ScriptTypeRearRight</c> stay exactly what they were - the single
    /// thing the engine reads every frame, unchanged and untouched by this. These sets hold only the
    /// modes that are NOT currently selected, and <see cref="WheelChannelSettings.SwitchSourceMode"/>
    /// moves values between the two. Three things fall out of that which matter:
    /// <list type="bullet">
    /// <item>the frame loop is completely unaffected - no new indirection on the hot path;</item>
    /// <item>an existing config file migrates for free, because its active fields are already in the
    /// right place and the archives simply start empty;</item>
    /// <item>every existing reader of <c>SourceFrontLeft</c> keeps working, so this cannot break the
    /// engine, the identity computation, or the settings page by omission.</item>
    /// </list>
    /// <para/>
    /// KEY DATA POINTS ARE NOT HERE, and deliberately so: they are ALREADY per-source, because
    /// <see cref="KeyDataPointSettings.MakeSlotKey"/> puts the source identity in the slot key. Copying
    /// them into this type as well would create a second, competing home for the same fact.
    /// </summary>
    public sealed class WheelSourceSet
    {
        public string FrontLeft { get; set; }
        public string FrontRight { get; set; }
        public string RearLeft { get; set; }
        public string RearRight { get; set; }

        public ScriptType ScriptTypeFrontLeft { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeFrontRight { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeRearLeft { get; set; } = ScriptType.Plain;
        public ScriptType ScriptTypeRearRight { get; set; } = ScriptType.Plain;

        /// <summary>Whether this set holds anything worth restoring. An archive that has never been
        /// written reads as empty, and the caller applies that mode's shipped preset instead - which is
        /// what makes a config from before this existed behave correctly on the first switch.</summary>
        public bool HasContent()
            => !string.IsNullOrWhiteSpace(FrontLeft)
            || !string.IsNullOrWhiteSpace(FrontRight)
            || !string.IsNullOrWhiteSpace(RearLeft)
            || !string.IsNullOrWhiteSpace(RearRight);

        public WheelSourceSet Clone() => new WheelSourceSet
        {
            FrontLeft = FrontLeft,
            FrontRight = FrontRight,
            RearLeft = RearLeft,
            RearRight = RearRight,
            ScriptTypeFrontLeft = ScriptTypeFrontLeft,
            ScriptTypeFrontRight = ScriptTypeFrontRight,
            ScriptTypeRearLeft = ScriptTypeRearLeft,
            ScriptTypeRearRight = ScriptTypeRearRight,
        };

        /// <summary>Snapshot a channel's currently-active four wheels into a new set.</summary>
        public static WheelSourceSet CaptureFrom(WheelChannelSettings channel) => new WheelSourceSet
        {
            FrontLeft = channel.SourceFrontLeft,
            FrontRight = channel.SourceFrontRight,
            RearLeft = channel.SourceRearLeft,
            RearRight = channel.SourceRearRight,
            ScriptTypeFrontLeft = channel.ScriptTypeFrontLeft,
            ScriptTypeFrontRight = channel.ScriptTypeFrontRight,
            ScriptTypeRearLeft = channel.ScriptTypeRearLeft,
            ScriptTypeRearRight = channel.ScriptTypeRearRight,
        };

        /// <summary>Write this set back over a channel's active four wheels.</summary>
        public void ApplyTo(WheelChannelSettings channel)
        {
            channel.SourceFrontLeft = FrontLeft;
            channel.SourceFrontRight = FrontRight;
            channel.SourceRearLeft = RearLeft;
            channel.SourceRearRight = RearRight;
            channel.ScriptTypeFrontLeft = ScriptTypeFrontLeft;
            channel.ScriptTypeFrontRight = ScriptTypeFrontRight;
            channel.ScriptTypeRearLeft = ScriptTypeRearLeft;
            channel.ScriptTypeRearRight = ScriptTypeRearRight;
        }
    }
}

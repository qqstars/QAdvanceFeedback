namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Per-channel pulse configuration (one instance for Wheel Lock, a separate independent instance
    /// for Wheel Slip - see <see cref="Settings.WheelChannelSettings"/>). When <see cref="Enabled"/>,
    /// a channel that reaches the top of its range (100) pulses between 100 and <see cref="MinValue"/>
    /// instead of holding flat at 100 - see <see cref="PulseGenerator"/> for the waveform.
    /// <para/>
    /// <see cref="GapMs"/> is enforced (not merely validated) to never read below
    /// <see cref="MinGapMs"/> (200 ms / 5 Hz) - the brief is explicit that this floor must live in the
    /// model, not only in a settings-UI spinner's Minimum, so a hand-edited config file or a future
    /// caller that never goes through a UI at all cannot smuggle in a faster-than-5Hz pulse. The
    /// setter clamps up to the floor rather than throwing, consistent with every other user-facing
    /// numeric setting in this plugin family (<see cref="ProjectorSettings"/>'s own clamp-not-throw
    /// convention).
    /// </summary>
    public sealed class PulseSettings
    {
        /// <summary>5 Hz - the fastest a pulse is allowed to cycle. Enforced by <see cref="GapMs"/>'s
        /// own setter, not merely documented.</summary>
        public const double MinGapMs = 200.0;

        /// <summary>A reasonable, clearly-a-default starting point - the brief only pins the FLOOR
        /// (200 ms); it does not mandate a specific shipped default gap.</summary>
        public const double DefaultGapMs = 500.0;

        public const double DefaultMinValue = 50.0;

        public bool Enabled { get; set; } = false;

        private double _gapMs = DefaultGapMs;

        /// <summary>
        /// Milliseconds for ONE HALF of a pulse cycle (100-&gt;min or min-&gt;100); a full cycle is
        /// therefore <c>2 * GapMs</c> - e.g. gap=500ms means 100-&gt;min-&gt;100-&gt;min with each
        /// move taking exactly 500ms, a 1000ms full period, exactly as the brief specifies. Reading
        /// back a value below <see cref="MinGapMs"/>, or a non-finite one (NaN/Infinity - a spinner
        /// left mid-edit, or a hand-edited config file), is impossible: the setter floors it to
        /// <see cref="MinGapMs"/> instead.
        /// </summary>
        public double GapMs
        {
            get => _gapMs;
            set => _gapMs = (!ClampMath.IsFinite(value) || value < MinGapMs) ? MinGapMs : value;
        }

        private double _minValue = DefaultMinValue;

        /// <summary>The floor the pulse dips to at the bottom of each cycle - clamped to [0,100]
        /// like every other 0-100 value this plugin publishes.</summary>
        public double MinValue
        {
            get => _minValue;
            set => _minValue = ClampMath.To0100(value);
        }
    }
}

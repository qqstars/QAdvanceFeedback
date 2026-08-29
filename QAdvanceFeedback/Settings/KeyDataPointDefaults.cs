using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Settings
{
    /// <summary>One source type's shipped starting points, as three source-scale values.</summary>
    public sealed class KeyDataPointDefaultSet
    {
        public double SMax { get; set; }
        public double S90 { get; set; }
        public double S75 { get; set; }

        public KeyDataPointDefaultSet() { }

        public KeyDataPointDefaultSet(double sMax, double s90, double s75)
        {
            SMax = sMax; S90 = s90; S75 = s75;
        }

        /// <summary>Whether this set is usable - same ordering and range rule the manual values obey.
        /// An unusable set (blank, or hand-edited into nonsense) is ignored in favour of the built-in
        /// numbers rather than allowed to reach the output.</summary>
        public bool IsUsable() => KeyDataPointSettings.IsValid(SMax, S90, S75);

        public KeyDataPointDefaultSet Clone() => new KeyDataPointDefaultSet(SMax, S90, S75);
    }

    /// <summary>
    /// The shipped starting points for every SOURCE TYPE this plugin recognises, written into the
    /// configuration file so they can be retuned without a rebuild.
    /// <para/>
    /// WHY THIS IS IN THE CONFIG AND NOT ONLY IN CODE. These are the numbers a channel publishes against
    /// before it has learned anything of its own - on a first run, and on every source the driver has
    /// never used. They are the one part of the calibration a driver may reasonably want to correct from
    /// the outside, because the right value depends on the title and the car class, and the shipped
    /// numbers were measured on a single capture.
    /// <para/>
    /// PER SOURCE TYPE, NOT PER GAME. A source's scale is a property of the signal, not of the title
    /// producing it - our own Raw means the same thing everywhere, and so does a ShakeIt export. Keying
    /// these by game would multiply the same answer across every title for no gain.
    /// <para/>
    /// AN UNRECOGNISED SOURCE HAS NO ENTRY HERE, deliberately: a script or an expression has a range
    /// nobody has measured, so there is no honest default to offer and the channel waits for real
    /// evidence instead.
    /// </summary>
    public sealed class KeyDataPointDefaults
    {
        public KeyDataPointDefaultSet LockRaw { get; set; }
        public KeyDataPointDefaultSet LockShakeIt { get; set; }
        public KeyDataPointDefaultSet SlipRaw { get; set; }
        public KeyDataPointDefaultSet SlipShakeIt { get; set; }

        /// <summary>
        /// The built-in numbers, used when the config carries no section yet (a first run, or a save
        /// written before this existed) and as the fallback for any entry edited into something unusable.
        /// <para/>
        /// Raw and ShakeIt currently ship the SAME values on each channel. They are separate entries so
        /// they can diverge by editing one place if measurement ever shows they should - the mechanism is
        /// the point, not today's coincidence.
        /// </summary>
        public static KeyDataPointDefaults CreateShipped()
        {
            double slipS90, slipS75;
            KeyDataPointSettings.DeriveLowerAnchors(KeyDataPointSettings.SlipDefaultSMax, out slipS90, out slipS75);

            return new KeyDataPointDefaults
            {
                LockRaw = new KeyDataPointDefaultSet(
                    KeyDataPointSettings.LockDefaultSMax,
                    KeyDataPointSettings.LockDefaultS90,
                    KeyDataPointSettings.LockDefaultS75),
                LockShakeIt = new KeyDataPointDefaultSet(
                    KeyDataPointSettings.LockDefaultSMax,
                    KeyDataPointSettings.LockDefaultS90,
                    KeyDataPointSettings.LockDefaultS75),
                SlipRaw = new KeyDataPointDefaultSet(
                    KeyDataPointSettings.SlipDefaultSMax, slipS90, slipS75),
                SlipShakeIt = new KeyDataPointDefaultSet(
                    KeyDataPointSettings.SlipDefaultSMax, slipS90, slipS75),
            };
        }

        /// <summary>
        /// The set for one (source type, channel), falling back to the built-in numbers whenever the
        /// configured entry is missing or unusable. Returns false only for an unrecognised source, which
        /// has no default by design.
        /// </summary>
        public bool TryResolve(KnownFeedbackSource source, bool isLockChannel,
            out double sMax, out double s90, out double s75)
        {
            sMax = s90 = s75 = 0.0;
            if (source == KnownFeedbackSource.Unknown) return false;

            bool raw = source == KnownFeedbackSource.QAdvanceFeedbackRaw;
            KeyDataPointDefaultSet configured = isLockChannel
                ? (raw ? LockRaw : LockShakeIt)
                : (raw ? SlipRaw : SlipShakeIt);

            if (configured != null && configured.IsUsable())
            {
                sMax = configured.SMax; s90 = configured.S90; s75 = configured.S75;
                return true;
            }

            KeyDataPointDefaults shipped = CreateShipped();
            KeyDataPointDefaultSet fallback = isLockChannel
                ? (raw ? shipped.LockRaw : shipped.LockShakeIt)
                : (raw ? shipped.SlipRaw : shipped.SlipShakeIt);
            sMax = fallback.SMax; s90 = fallback.S90; s75 = fallback.S75;
            return true;
        }

        public KeyDataPointDefaults Clone() => new KeyDataPointDefaults
        {
            LockRaw = LockRaw?.Clone(),
            LockShakeIt = LockShakeIt?.Clone(),
            SlipRaw = SlipRaw?.Clone(),
            SlipShakeIt = SlipShakeIt?.Clone(),
        };
    }
}

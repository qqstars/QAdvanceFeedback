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

        /// <summary>viper4gh's CalcLngWheelSlip, read through this plugin's shipped NCalc preset. Unlike
        /// Raw and ShakeIt - which coincide today - these are genuinely different numbers, measured from
        /// their own capture: see <see cref="KnownSourceColdStartReference.LockViperSMax"/>.</summary>
        public KeyDataPointDefaultSet LockViper { get; set; }

        /// <summary>See <see cref="LockViper"/>.</summary>
        public KeyDataPointDefaultSet SlipViper { get; set; }

        /// <summary>
        /// Wheel Lock fed by a source the driver configured themselves.
        /// <para/>
        /// EDITABLE, UNLIKE THE OTHERS, and that is the point. The three presets have references this
        /// project measured; a custom source has a scale only the driver knows, so there is nothing to
        /// ship and no honest guess to make. The value here is seeded by DUPLICATING whichever preset
        /// they edited their way in from (owner: "if we edit source from Raw, the SMax for lock should
        /// be dumped as the Raw's SMax cold start reference"), and is then theirs to correct.
        /// <para/>
        /// Entering Custom again from a DIFFERENT preset overwrites it with that one's numbers - also
        /// the owner's own rule, and the reason this is a plain value rather than something that tries
        /// to remember its own history.
        /// <para/>
        /// NULL UNTIL CONFIGURED. <see cref="CreateShipped"/> deliberately leaves it null, so an
        /// unconfigured custom source reports "no default" and the page shows "---" rather than
        /// borrowing a number measured against a completely different signal.
        /// </summary>
        public KeyDataPointDefaultSet LockCustom { get; set; }

        /// <summary>See <see cref="LockCustom"/>.</summary>
        public KeyDataPointDefaultSet SlipCustom { get; set; }

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
            // Viper's lower anchors are DERIVED from its own measured SMax by the same fixed fractions the
            // UI shows, rather than measured separately - the capture behind those constants had a source
            // that only ever read 0 or full scale, so it carries no information about where 90% and 75%
            // of the way to the limit actually sit. Derived is honest here; invented numbers would not be.
            double lockViperS90, lockViperS75;
            KeyDataPointSettings.DeriveLowerAnchors(KnownSourceColdStartReference.LockViperSMax, out lockViperS90, out lockViperS75);
            double slipViperS90, slipViperS75;
            KeyDataPointSettings.DeriveLowerAnchors(KnownSourceColdStartReference.SlipViperSMax, out slipViperS90, out slipViperS75);

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
                LockViper = new KeyDataPointDefaultSet(
                    KnownSourceColdStartReference.LockViperSMax, lockViperS90, lockViperS75),
                SlipViper = new KeyDataPointDefaultSet(
                    KnownSourceColdStartReference.SlipViperSMax, slipViperS90, slipViperS75),
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

            // THREE SOURCES NOW, SO THIS IS A SWITCH RATHER THAN A raw/not-raw TERNARY. The old form read
            // "raw ? LockRaw : LockShakeIt", which silently hands any NEW source the ShakeIt numbers -
            // exactly the kind of wrong-but-plausible default that is hard to notice. Select returns null
            // for anything it does not know, and the caller reports that as "no default".
            KeyDataPointDefaultSet configured = Select(this, source, isLockChannel);

            if (configured != null && configured.IsUsable())
            {
                sMax = configured.SMax; s90 = configured.S90; s75 = configured.S75;
                return true;
            }

            KeyDataPointDefaults shipped = CreateShipped();
            KeyDataPointDefaultSet fallback = Select(shipped, source, isLockChannel);
            if (fallback == null) return false;
            sMax = fallback.SMax; s90 = fallback.S90; s75 = fallback.S75;
            return true;
        }

        /// <summary>
        /// Copy <paramref name="source"/>'s reference into the Custom slot for this channel - what
        /// happens the moment a driver edits a preset's source text and the page moves them to Custom.
        /// <para/>
        /// A PLAIN OVERWRITE, deliberately. The owner's rule is that entering Custom from Raw seeds
        /// Raw's numbers and entering it again from Viper seeds Viper's, "even if we already have a
        /// Custom source applied" - so the last preset edited from always wins, and there is nothing to
        /// merge or remember.
        /// <para/>
        /// Copying <see cref="KnownFeedbackSource.Custom"/> onto itself is a no-op rather than an
        /// error: editing a source that is ALREADY Custom must keep the driver's own reference, which
        /// is the other half of the same rule.
        /// </summary>
        public void SeedCustomFrom(KnownFeedbackSource source, bool isLockChannel)
        {
            if (source == KnownFeedbackSource.Custom || source == KnownFeedbackSource.Unknown) return;
            if (!TryResolve(source, isLockChannel, out double sMax, out double s90, out double s75)) return;

            var seeded = new KeyDataPointDefaultSet(sMax, s90, s75);
            if (isLockChannel) LockCustom = seeded;
            else SlipCustom = seeded;
        }

        /// <summary>The one set matching (source, channel), or null for a source with no entry.</summary>
        private static KeyDataPointDefaultSet Select(KeyDataPointDefaults from, KnownFeedbackSource source, bool isLockChannel)
        {
            switch (source)
            {
                case KnownFeedbackSource.QAdvanceFeedbackRaw: return isLockChannel ? from.LockRaw : from.SlipRaw;
                case KnownFeedbackSource.ShakeItMotorsExport: return isLockChannel ? from.LockShakeIt : from.SlipShakeIt;
                case KnownFeedbackSource.ViperLngWheelSlip: return isLockChannel ? from.LockViper : from.SlipViper;
                case KnownFeedbackSource.Custom: return isLockChannel ? from.LockCustom : from.SlipCustom;
                default: return null;
            }
        }

        public KeyDataPointDefaults Clone() => new KeyDataPointDefaults
        {
            LockRaw = LockRaw?.Clone(),
            LockShakeIt = LockShakeIt?.Clone(),
            SlipRaw = SlipRaw?.Clone(),
            SlipShakeIt = SlipShakeIt?.Clone(),
            LockViper = LockViper?.Clone(),
            SlipViper = SlipViper?.Clone(),
            LockCustom = LockCustom?.Clone(),
            SlipCustom = SlipCustom?.Clone(),
        };
    }
}

using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// One stored set of key data points, for one slot - see <see cref="KeyDataPointSettings.MakeSlotKey"/>
    /// for what a slot is.
    /// </summary>
    public sealed class KeyDataPointEntry
    {
        public double SMax { get; set; }
        public double S90 { get; set; }
        public double S75 { get; set; }

        /// <summary>Whether this slot has already had the learned values written in once. Per SLOT, not
        /// per channel, which is what makes "a game you have never played seeds again" work.</summary>
        public bool Seeded { get; set; }
    }

    /// <summary>
    /// The driver-facing "Key Data Points" for one channel: the source values the Normalized layer treats
    /// as max grip (SMax), 90% grip (S90) and 75% grip (S75) - or, for Slip, the Perfect, Great and Good
    /// points.
    /// <para/>
    /// LEARNING NEVER STOPS. <see cref="AutoGenerate"/> decides only whether the LEARNED values or these
    /// MANUAL ones are applied to the published output. <see cref="KeyedScaleLearner"/> and
    /// <see cref="LockAnchorLearner"/> keep observing regardless, which is what makes the
    /// "[Learned Value: xx.x]" hint meaningful in manual mode and what lets a driver toggle back to Auto
    /// and get a current answer rather than a stale one.
    /// <para/>
    /// VALUES ARE KEYED BY SLOT = (mode, game, source). A number that is right for a ShakeIt export is
    /// not right for our own Raw, and a number that is right for one title is not right for another -
    /// so switching ANY of those three must load a different set rather than carry the old one across.
    /// In global mode the game is deliberately excluded from the key, which is what "global" means: one
    /// set per source, shared by every title.
    /// <para/>
    /// SEEDING - why manual mode starts from the learned value rather than from a shipped constant.
    /// Dropping a driver into manual mode with a canned number would be a step change in feel the moment
    /// they untoggle. Instead, the first time a SLOT has a usable learned value it is written in, once,
    /// and persisted immediately; thereafter the driver's own edits are authoritative. Because the latch
    /// is per slot, a new game (in per-game mode) or a newly selected source seeds again on its own.
    /// </summary>
    public sealed class KeyDataPointSettings
    {
        /// <summary>Ships ON: the learned values drive the output and the driver need not think about
        /// any of this.</summary>
        public bool AutoGenerate { get; set; } = true;

        /// <summary>Manual mode only: keep a separate set per game rather than one set shared across all
        /// of them. Meaningless while <see cref="AutoGenerate"/> is on, and the row is hidden then.</summary>
        public bool PerGame { get; set; }

        /// <summary>
        /// Every stored set, keyed by <see cref="MakeSlotKey"/>. One dictionary rather than "a global
        /// triple plus a per-game table", because global and per-game are the same kind of thing
        /// differing only in whether the game forms part of the key - and because a driver who switches
        /// global -> per-game -> global must find their global numbers exactly as they left them.
        /// </summary>
        public Dictionary<string, KeyDataPointEntry> Values { get; set; }
            = new Dictionary<string, KeyDataPointEntry>(StringComparer.OrdinalIgnoreCase);

        // ---- SHIPPED DEFAULTS, PER SOURCE TYPE ----

        public const double LockDefaultSMax = 85.0;
        public const double LockDefaultS90 = 75.0;
        public const double LockDefaultS75 = 60.0;

        public const double SlipDefaultSMax = 75.0;

        /// <summary>Slip has no native 90%/75% grip concept, so its Great/Good points are derived from
        /// its Perfect point by these fractions. Also used for Lock under Max-Grip-Only, to keep the two
        /// hidden anchors self-consistent behind the scenes.</summary>
        public const double DerivedS90Fraction = 0.90;
        public const double DerivedS75Fraction = 0.70;

        public const double MinValue = 0.0;
        public const double MaxValue = 100.0;

        /// <summary>
        /// The slot a given (mode, game, source) combination stores under.
        /// <para/>
        /// Global mode omits the game entirely - that is precisely what makes it global. Both modes
        /// include the source, because a source change is a change of scale and must never silently
        /// reuse another signal's numbers. The prefix keeps the two namespaces from ever colliding (a
        /// game literally named the same as a source identity would otherwise alias).
        /// </summary>
        public static string MakeSlotKey(bool perGame, string gameId, string sourceIdentity)
        {
            string source = sourceIdentity ?? string.Empty;
            return perGame
                ? "game:" + (gameId ?? string.Empty) + "|src:" + source
                : "global|src:" + source;
        }

        /// <summary>
        /// The shipped defaults for a channel fed by <paramref name="source"/>.
        /// <para/>
        /// An UNKNOWN source (a script, an NCalc expression, a property this plugin does not recognise)
        /// deliberately gets NO shipped default - there is no honest guess for a signal whose scale has
        /// never been seen. Such a channel keeps publishing its learned values until a slot is seeded.
        /// </summary>
        public static bool TryResolveDefaults(KnownFeedbackSource source, bool isLockChannel,
            out double sMax, out double s90, out double s75)
        {
            sMax = s90 = s75 = 0.0;
            if (source == KnownFeedbackSource.Unknown) return false;

            if (isLockChannel)
            {
                sMax = LockDefaultSMax; s90 = LockDefaultS90; s75 = LockDefaultS75;
            }
            else
            {
                sMax = SlipDefaultSMax;
                DeriveLowerAnchors(sMax, out s90, out s75);
            }
            return true;
        }

        /// <summary>
        /// Whether this channel is fed by one of the two configurations this plugin actually ships:
        /// all four wheels on its own <c>Raw</c> properties, or all four on ShakeIt's
        /// <c>WheelLock/WheelSlip.IRacing</c> export. Compared EXACTLY, against the identity string those
        /// configurations produce.
        /// <para/>
        /// Deliberately stricter than <see cref="KnownSourceColdStartReference.Classify"/>, which matches
        /// on a substring: that treats <c>ShakeITMotorsV3Plugin.Export.WheelLock.MyOwn.FrontLeft</c> as a
        /// ShakeIt source, because the plugin name is in there. For cold-start seeding that leniency is
        /// harmless - the scale is probably similar. For SHIPPED DEFAULTS it is not: a driver who exported
        /// their own effect under their own name has a signal whose range nobody has measured, and handing
        /// them our numbers for a different effect would be a guess dressed up as a default. A scripted or
        /// NCalc source never matches either, since <see cref="SourceIdentity"/> hashes those.
        /// </summary>
        public static bool IsExactShippedSource(string sourceIdentity, bool isLockChannel)
            => ClassifyExact(sourceIdentity, isLockChannel) != KnownFeedbackSource.Unknown;

        private static string ShippedRawIdentity(bool isLockChannel)
        {
            return SourceIdentity.Compute(
                DefaultWheelSources.RawPropertyName(isLockChannel, MotorsExportPropertyNames.FrontLeft), "Plain",
                DefaultWheelSources.RawPropertyName(isLockChannel, MotorsExportPropertyNames.FrontRight), "Plain",
                DefaultWheelSources.RawPropertyName(isLockChannel, MotorsExportPropertyNames.RearLeft), "Plain",
                DefaultWheelSources.RawPropertyName(isLockChannel, MotorsExportPropertyNames.RearRight), "Plain");
        }

        private static string ShippedShakeItIdentity(bool isLockChannel)
        {
            return SourceIdentity.Compute(
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontLeft), "Plain",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontRight), "Plain",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearLeft), "Plain",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearRight), "Plain");
        }

        /// <summary>
        /// Which of the two shipped configurations this channel is on, or
        /// <see cref="KnownFeedbackSource.Unknown"/> for anything else. Exact, not the substring match
        /// <see cref="KnownSourceColdStartReference.Classify"/> uses - see
        /// <see cref="IsExactShippedSource"/>.
        /// </summary>
        public static KnownFeedbackSource ClassifyExact(string sourceIdentity, bool isLockChannel)
        {
            if (string.IsNullOrWhiteSpace(sourceIdentity)) return KnownFeedbackSource.Unknown;

            if (string.Equals(sourceIdentity, ShippedRawIdentity(isLockChannel), StringComparison.OrdinalIgnoreCase))
                return KnownFeedbackSource.QAdvanceFeedbackRaw;
            if (string.Equals(sourceIdentity, ShippedShakeItIdentity(isLockChannel), StringComparison.OrdinalIgnoreCase))
                return KnownFeedbackSource.ShakeItMotorsExport;
            return KnownFeedbackSource.Unknown;
        }

        /// <summary>
        /// The starting points for this channel's current source, taken from the CONFIGURED defaults so a
        /// driver's retuning is honoured, and falling back to the built-in numbers when the config has
        /// none or carries something unusable.
        /// </summary>
        public static bool TryResolveShippedDefaults(string sourceIdentity, bool isLockChannel,
            KeyDataPointDefaults defaults, out double sMax, out double s90, out double s75)
        {
            sMax = s90 = s75 = 0.0;
            KnownFeedbackSource source = ClassifyExact(sourceIdentity, isLockChannel);
            if (source == KnownFeedbackSource.Unknown) return false;

            return (defaults ?? KeyDataPointDefaults.CreateShipped())
                .TryResolve(source, isLockChannel, out sMax, out s90, out s75);
        }

        /// <summary>S90/S75 derived from a given SMax - see <see cref="DerivedS90Fraction"/>.</summary>
        public static void DeriveLowerAnchors(double sMax, out double s90, out double s75)
        {
            s90 = sMax * DerivedS90Fraction;
            s75 = sMax * DerivedS75Fraction;
        }

        /// <summary>
        /// Whether a triple is usable: strictly positive, within the enforced source scale, and correctly
        /// ordered. The ordering is what the four-range curve needs to stay monotone.
        /// </summary>
        public static bool IsValid(double sMax, double s90, double s75)
            => sMax > MinValue && sMax <= MaxValue
            && s90 > MinValue && s90 <= MaxValue
            && s75 > MinValue && s75 <= MaxValue
            && sMax >= s90 && s90 >= s75;

        /// <summary>The stored values for this exact (mode, game, source), or false when this slot has
        /// nothing configured yet.</summary>
        public bool TryGetManual(string gameId, string sourceIdentity,
            out double sMax, out double s90, out double s75)
        {
            sMax = s90 = s75 = 0.0;
            if (Values == null) return false;

            KeyDataPointEntry entry;
            if (!Values.TryGetValue(MakeSlotKey(PerGame, gameId, sourceIdentity), out entry) || entry == null)
                return false;

            sMax = entry.SMax; s90 = entry.S90; s75 = entry.S75;
            return IsValid(sMax, s90, s75);
        }

        /// <summary>Write values into this exact slot.</summary>
        public void SetManual(string gameId, string sourceIdentity,
            double sMax, double s90, double s75, bool seeded)
        {
            if (Values == null)
                Values = new Dictionary<string, KeyDataPointEntry>(StringComparer.OrdinalIgnoreCase);

            string slot = MakeSlotKey(PerGame, gameId, sourceIdentity);
            KeyDataPointEntry entry;
            if (!Values.TryGetValue(slot, out entry) || entry == null)
                Values[slot] = entry = new KeyDataPointEntry();

            entry.SMax = sMax; entry.S90 = s90; entry.S75 = s75;
            if (seeded) entry.Seeded = true;
        }

        /// <summary>
        /// Forget this slot entirely - values AND the seeded latch. Re-arms the one-time seed, so the
        /// next valid learned value for this context writes itself in and persists, exactly as it would
        /// have on a fresh install. Used by the manual-mode reset for a source that has no shipped
        /// default to fall back on.
        /// </summary>
        public void ClearSlot(string gameId, string sourceIdentity)
        {
            if (Values == null) return;
            Values.Remove(MakeSlotKey(PerGame, gameId, sourceIdentity));
        }

        /// <summary>Whether the one-time learned-value write has already happened for this exact slot.
        /// A never-played game, or a newly selected source, reports false and therefore seeds again.</summary>
        public bool IsSeeded(string gameId, string sourceIdentity)
        {
            if (Values == null) return false;
            KeyDataPointEntry entry;
            return Values.TryGetValue(MakeSlotKey(PerGame, gameId, sourceIdentity), out entry)
                && entry != null && entry.Seeded;
        }

        public KeyDataPointSettings Clone()
        {
            var copy = new KeyDataPointSettings
            {
                AutoGenerate = AutoGenerate,
                PerGame = PerGame,
                Values = new Dictionary<string, KeyDataPointEntry>(StringComparer.OrdinalIgnoreCase),
            };
            if (Values != null)
            {
                foreach (KeyValuePair<string, KeyDataPointEntry> pair in Values)
                {
                    if (pair.Value == null) continue;
                    copy.Values[pair.Key] = new KeyDataPointEntry
                    {
                        SMax = pair.Value.SMax,
                        S90 = pair.Value.S90,
                        S75 = pair.Value.S75,
                        Seeded = pair.Value.Seeded,
                    };
                }
            }
            return copy;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// One source's own Auto/Manual and Global/Per-Game choice - see
    /// <see cref="KeyDataPointSettings.SourceFlags"/> for why these belong to the source rather than to
    /// the channel.
    /// </summary>
    public sealed class KeyDataSourceFlags
    {
        public bool AutoGenerate { get; set; } = true;
        public bool PerGame { get; set; }
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
        /// <summary>
        /// THE CHANNEL-WIDE FALLBACK, and nothing more. Read only for a source that has no entry of its
        /// own in <see cref="SourceFlags"/> - see <see cref="GetAutoGenerate"/>.
        /// <para/>
        /// This used to BE the setting, one flag for the whole channel. That was wrong: the owner's own
        /// case (2026-09-29) is "using Raw might use AutoGenerate, but Viper using manual", which is
        /// exactly right - Raw is a signal this plugin measures itself and can learn well, while a
        /// plugin-supplied source on a known 0-1 scale may be better pinned by hand. One flag forced the
        /// two sources to agree.
        /// <para/>
        /// KEPT, NOT DELETED, because it IS the migration: an existing settings file has only these two
        /// values, and every source falls back to them until the driver changes one. No conversion pass,
        /// no version bump, and a file written by this build still reads correctly on the previous one.
        /// </summary>
        public bool AutoGenerate { get; set; } = true;

        /// <summary>The channel-wide fallback for per-game mode - see <see cref="AutoGenerate"/> for why
        /// this is a fallback rather than the setting, and <see cref="GetPerGame"/> for the real one.</summary>
        public bool PerGame { get; set; }

        /// <summary>
        /// Auto/Manual and Global/Per-Game PER SOURCE, keyed by source identity.
        /// <para/>
        /// A source is a different signal on a different scale, so how it should be calibrated is a
        /// property of that source, not of the channel that happens to be reading it. Keeping these
        /// beside <see cref="Values"/> - which has always been source-keyed - means switching source
        /// restores the whole calibration the driver left there, not just its numbers.
        /// <para/>
        /// A source absent from this map inherits <see cref="AutoGenerate"/>/<see cref="PerGame"/>, so
        /// an untouched channel behaves exactly as it did before this existed.
        /// </summary>
        public Dictionary<string, KeyDataSourceFlags> SourceFlags { get; set; }
            = new Dictionary<string, KeyDataSourceFlags>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether this SOURCE generates its points automatically, falling back to the
        /// channel-wide flag for a source nobody has configured yet.</summary>
        public bool GetAutoGenerate(string sourceIdentity)
        {
            KeyDataSourceFlags flags = FindFlags(sourceIdentity);
            return flags != null ? flags.AutoGenerate : AutoGenerate;
        }

        /// <summary>Whether this SOURCE keeps a separate set per game - see <see cref="GetAutoGenerate"/>.</summary>
        public bool GetPerGame(string sourceIdentity)
        {
            KeyDataSourceFlags flags = FindFlags(sourceIdentity);
            return flags != null ? flags.PerGame : PerGame;
        }

        /// <summary>Record this source's own Auto/Manual choice, creating its entry on first use.</summary>
        public void SetAutoGenerate(string sourceIdentity, bool value) => EnsureFlags(sourceIdentity).AutoGenerate = value;

        /// <summary>Record this source's own Global/Per-Game choice - see <see cref="SetAutoGenerate"/>.</summary>
        public void SetPerGame(string sourceIdentity, bool value) => EnsureFlags(sourceIdentity).PerGame = value;

        /// <summary>The shipped Auto/Manual choice - learning drives the output until the driver says
        /// otherwise.</summary>
        public const bool DefaultAutoGenerate = true;

        /// <summary>The shipped Global/Per-Game choice - one set shared by every title.</summary>
        public const bool DefaultPerGame = false;

        /// <summary>
        /// Whether this source's WHOLE key data configuration - Auto/Manual, Global/Per-Game, and every
        /// stored number - is still exactly what ships. Drives whether the dedicated "restore to
        /// default" button appears at all (owner, 2026-09-30: "ONLY if the key points settings (include
        /// the auto/manual, per games, and the numbers) are different with the default, then will show
        /// the button").
        /// <para/>
        /// A SOURCE WITH NO SHIPPED DEFAULT ALWAYS "MATCHES", which is how the Custom source ends up
        /// with no button: there is no default to restore it TO, so offering the action would be a lie.
        /// That is the owner's own rule for it, and it falls out of this check rather than needing a
        /// special case at the call site.
        /// <para/>
        /// A stored slot holding EXACTLY the shipped triple counts as matching. Otherwise a driver who
        /// typed the shipped numbers by hand - or whose slot was seeded with them - would be offered a
        /// restore that visibly changes nothing.
        /// </summary>
        public bool MatchesDefaults(string sourceIdentity, bool isLockChannel, KeyDataPointDefaults defaults)
        {
            double defSMax, defS90, defS75;
            if (!TryResolveShippedDefaults(sourceIdentity, isLockChannel, defaults, out defSMax, out defS90, out defS75))
                return true;   // nothing to restore to - see the remarks above

            if (GetAutoGenerate(sourceIdentity) != DefaultAutoGenerate) return false;
            if (GetPerGame(sourceIdentity) != DefaultPerGame) return false;
            if (Values == null) return true;

            string suffix = SlotSuffixFor(sourceIdentity);
            foreach (KeyValuePair<string, KeyDataPointEntry> pair in Values)
            {
                if (!pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                KeyDataPointEntry e = pair.Value;
                if (e == null) continue;
                if (Math.Abs(e.SMax - defSMax) > 1e-6
                    || Math.Abs(e.S90 - defS90) > 1e-6
                    || Math.Abs(e.S75 - defS75) > 1e-6) return false;
            }
            return true;
        }

        /// <summary>
        /// Put ONE SOURCE's key data configuration back to shipped: Auto on, Global, and every stored
        /// number for that source discarded - including its whole per-game table.
        /// <para/>
        /// SCOPED TO THE SOURCE, not the channel, because that is the unit the driver is looking at and
        /// the unit everything else here is keyed by. Resetting Viper must not touch what Raw learned.
        /// <para/>
        /// Discarding the numbers is the point of the button, and is why it is separate from the global
        /// "Restore all defaults" - which deliberately preserves them (see
        /// <see cref="QAdvanceFeedbackSettings.RestoreDefaults"/>, owner's v1.0.7.2 rule). A driver who
        /// wants that accumulated record gone now has a control that says so, instead of the global
        /// button quietly not doing it.
        /// </summary>
        public void RestoreSourceDefaults(string sourceIdentity)
        {
            if (string.IsNullOrEmpty(sourceIdentity)) return;

            SetAutoGenerate(sourceIdentity, DefaultAutoGenerate);
            SetPerGame(sourceIdentity, DefaultPerGame);

            if (Values == null) return;
            string suffix = SlotSuffixFor(sourceIdentity);
            foreach (string key in Values.Keys
                         .Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                Values.Remove(key);
        }

        /// <summary>The tail every one of a source's slot keys ends with - see <see cref="MakeSlotKey"/>,
        /// where both the global and the per-game form end in "|src:&lt;identity&gt;".</summary>
        private static string SlotSuffixFor(string sourceIdentity) => "|src:" + (sourceIdentity ?? string.Empty);

        private KeyDataSourceFlags FindFlags(string sourceIdentity)
        {
            if (SourceFlags == null || string.IsNullOrEmpty(sourceIdentity)) return null;
            KeyDataSourceFlags flags;
            return SourceFlags.TryGetValue(sourceIdentity, out flags) ? flags : null;
        }

        private KeyDataSourceFlags EnsureFlags(string sourceIdentity)
        {
            if (SourceFlags == null)
                SourceFlags = new Dictionary<string, KeyDataSourceFlags>(StringComparer.OrdinalIgnoreCase);

            // A channel with no source identity yet (before the page has resolved one) must not create a
            // phantom "" entry that every unnamed source would then share - write the fallback instead.
            if (string.IsNullOrEmpty(sourceIdentity))
                return new KeyDataSourceFlags { AutoGenerate = AutoGenerate, PerGame = PerGame };

            KeyDataSourceFlags flags;
            if (!SourceFlags.TryGetValue(sourceIdentity, out flags) || flags == null)
            {
                // SEEDED FROM THE CHANNEL FALLBACK, not from the type's defaults: a driver who had the
                // channel on Manual before this was per-source must not find a newly touched source
                // silently flipped back to Auto.
                SourceFlags[sourceIdentity] = flags = new KeyDataSourceFlags { AutoGenerate = AutoGenerate, PerGame = PerGame };
            }
            return flags;
        }

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
            // VIPER (v1.1.0). Ordinal, not OrdinalIgnoreCase like its two neighbours: the other identities
            // are property names, where case is incidental, but this one is an FNV-1a hex hash that
            // SourceIdentity always emits in one case - comparing it case-insensitively would widen the
            // match for no reason.
            if (string.Equals(sourceIdentity, KnownSourceColdStartReference.ViperSourceIdentity(isLockChannel), StringComparison.Ordinal))
                return KnownFeedbackSource.ViperLngWheelSlip;
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

        /// <summary>
        /// <see cref="TryResolveShippedDefaults"/>, but aware of which SOURCE MODE the channel is on.
        /// <para/>
        /// WHY THE MODE HAS TO BE PASSED IN. Every other source is recognised from its identity, because
        /// this plugin generates that identity itself. A custom source is whatever the driver typed, and
        /// <see cref="SourceIdentity"/> HASHES scripted text - so there is nothing in the identity to
        /// recognise, and <see cref="ClassifyExact"/> correctly reports Unknown. The mode is the only
        /// thing that distinguishes "a custom source with a configured reference" from "a source nobody
        /// has measured", and those two must not resolve the same way: the first has an answer, the
        /// second must keep showing "---".
        /// </summary>
        public static bool TryResolveDefaultsForMode(string sourceIdentity, SourceMode mode, bool isLockChannel,
            KeyDataPointDefaults defaults, out double sMax, out double s90, out double s75)
        {
            sMax = s90 = s75 = 0.0;

            KnownFeedbackSource source = mode == SourceMode.Custom
                ? KnownFeedbackSource.Custom
                : ClassifyExact(sourceIdentity, isLockChannel);

            if (source == KnownFeedbackSource.Unknown) return false;

            return (defaults ?? KeyDataPointDefaults.CreateShipped())
                .TryResolve(source, isLockChannel, out sMax, out s90, out s75);
        }

        /// <summary>Which known source a channel is on, taking its MODE into account - see
        /// <see cref="TryResolveDefaultsForMode"/> for why the identity alone cannot answer this for a
        /// custom source.</summary>
        public static KnownFeedbackSource ClassifyForMode(string sourceIdentity, SourceMode mode, bool isLockChannel)
            => mode == SourceMode.Custom
                ? KnownFeedbackSource.Custom
                : ClassifyExact(sourceIdentity, isLockChannel);

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
            if (!Values.TryGetValue(MakeSlotKey(GetPerGame(sourceIdentity), gameId, sourceIdentity), out entry) || entry == null)
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

            string slot = MakeSlotKey(GetPerGame(sourceIdentity), gameId, sourceIdentity);
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
            Values.Remove(MakeSlotKey(GetPerGame(sourceIdentity), gameId, sourceIdentity));
        }

        /// <summary>Whether the one-time learned-value write has already happened for this exact slot.
        /// A never-played game, or a newly selected source, reports false and therefore seeds again.</summary>
        public bool IsSeeded(string gameId, string sourceIdentity)
        {
            if (Values == null) return false;
            KeyDataPointEntry entry;
            return Values.TryGetValue(MakeSlotKey(GetPerGame(sourceIdentity), gameId, sourceIdentity), out entry)
                && entry != null && entry.Seeded;
        }

        public KeyDataPointSettings Clone()
        {
            var copy = new KeyDataPointSettings
            {
                AutoGenerate = AutoGenerate,
                PerGame = PerGame,
                Values = new Dictionary<string, KeyDataPointEntry>(StringComparer.OrdinalIgnoreCase),
                SourceFlags = new Dictionary<string, KeyDataSourceFlags>(StringComparer.OrdinalIgnoreCase),
            };
            if (SourceFlags != null)
            {
                foreach (KeyValuePair<string, KeyDataSourceFlags> pair in SourceFlags)
                {
                    if (pair.Value == null) continue;
                    copy.SourceFlags[pair.Key] = new KeyDataSourceFlags
                    {
                        AutoGenerate = pair.Value.AutoGenerate,
                        PerGame = pair.Value.PerGame,
                    };
                }
            }
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

using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// One-time load migration for the retired hidden Layer-3-Raw key
    /// (docs\internal-source-learning-design.md section 3.1).
    /// <para/>
    /// Until Task G the engine kept a separate, always-fed copy of the Raw calibration under the source
    /// segment <see cref="RetiredHiddenSourceIdentity"/>, used only for the "configured source went quiet"
    /// comparison and never read by Plugin Internal itself. That comparison now reads our internal
    /// source's real key, which learns every frame, so the hidden entries are folded into it once:
    /// <list type="bullet">
    /// <item>the internal key (same game / car / surface, source = the channel's internal identity) has no
    /// evidence - the hidden state becomes the internal key's state;</item>
    /// <item>both exist - the internal key's own data is kept ([call] in the design);</item>
    /// <item>the hidden entries are then dropped. No other key is touched.</item>
    /// </list>
    /// Pure: a new dictionary is returned and the input is left as it was. Call it on whatever
    /// <c>RuntimeStore.Load*</c> returned, before <c>ImportAll</c>, once per channel with that channel's
    /// own internal identity (Lock and Slip map to different strings).
    /// </summary>
    public static class InternalSourceKeyMigration
    {
        /// <summary>The retired hidden source segment. Recognised on load only - never written again.</summary>
        public const string RetiredHiddenSourceIdentity = "__layer3_raw_fallback__";

        private const string Separator = "|#|";
        private const int SourceSegment = 2;

        /// <summary>Scale learner (SMax) entries: evidence is a usable persisted ceiling.</summary>
        public static Dictionary<string, ScaleLearnerState> MigrateScale(
            IDictionary<string, ScaleLearnerState> data, string internalIdentity)
            => Migrate(data, internalIdentity, s => s != null && ClampMath.IsFinite(s.ColdCeiling) && s.ColdCeiling > 0.0);

        /// <summary>Lock anchor (S75/S90) entries: evidence is any learned level.</summary>
        public static Dictionary<string, LockAnchorState> MigrateAnchors(
            IDictionary<string, LockAnchorState> data, string internalIdentity)
            => Migrate(data, internalIdentity, s => s != null
                && (s.S75 > 0.0 || s.S90 > 0.0 || s.RatioLevel75 > 0.0 || s.RatioLevel90 > 0.0
                    || s.PhysicalRatioLevel75 > 0.0 || s.PhysicalRatioLevel90 > 0.0));

        /// <summary>Grip learner entries (the hidden key was never written here, but the rule is the same
        /// for every source-keyed family): evidence is at least one sample.</summary>
        public static Dictionary<string, GripLearnerState> MigrateGrip(
            IDictionary<string, GripLearnerState> data, string internalIdentity)
            => Migrate(data, internalIdentity, s => s != null && s.Samples > 0);

        /// <summary>The rule itself, for any persisted family keyed by <see cref="KeyedGripLearner.MakeKey"/>.
        /// Null in, null out.</summary>
        public static Dictionary<string, T> Migrate<T>(IDictionary<string, T> data, string internalIdentity, Func<T, bool> hasEvidence)
            where T : class
        {
            if (data == null) return null;
            Func<T, bool> evidence = hasEvidence ?? (v => v != null);

            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            var hidden = new List<KeyValuePair<string, T>>();
            foreach (KeyValuePair<string, T> pair in data)
            {
                if (pair.Key == null) continue;
                if (IsHiddenKey(pair.Key)) hidden.Add(pair);
                else result[KeyRules.SourceKeys.RekeyLearnerKey(pair.Key)] = pair.Value;   // Version 21: hashed source
            }

            // An empty internal identity has nowhere to move the hidden state to: it is still dropped.
            if (string.IsNullOrEmpty(internalIdentity)) return result;

            foreach (KeyValuePair<string, T> pair in hidden)
            {
                if (pair.Value == null || !evidence(pair.Value)) continue;
                string target = WithSource(pair.Key, internalIdentity);
                if (result.TryGetValue(target, out T existing) && existing != null && evidence(existing)) continue;
                result[target] = pair.Value;
            }
            return result;
        }

        /// <summary>Whether <paramref name="key"/>'s source segment is the retired hidden identity.</summary>
        public static bool IsHiddenKey(string key)
        {
            string[] parts = (key ?? string.Empty).Split(new[] { Separator }, StringSplitOptions.None);
            return parts.Length > SourceSegment
                && string.Equals(KeyRules.SourceKeys.FromKey(parts[SourceSegment]), RetiredHiddenSourceIdentity, StringComparison.Ordinal);
        }

        private static string WithSource(string key, string sourceIdentity)
        {
            string[] parts = key.Split(new[] { Separator }, StringSplitOptions.None);
            parts[SourceSegment] = KeyRules.SourceKeys.ToKey(parts[0], sourceIdentity);   // Version 21: hashed source
            return string.Join(Separator, parts);
        }
    }
}

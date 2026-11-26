using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QAdvanceFeedback.Core.KeyRules
{
    // =====================================================================================================
    // VERSIONED SOURCE KEYS (owner, 2026-11-25; parameters Version 21).
    //
    // A learner key keeps its borrowable parts readable - game |#| car |#| SOURCE |#| surface - and only the SOURCE
    // becomes "<version>:<number>": the stable hash of the FULL source configuration under that version's rule (a
    // Slip Angle source keeps its axle readable after it: "21:123.../all"). Every version's rule is kept for good: a
    // key written under an older rule is still recognised by Matches (the factory builds the rule its version names),
    // and GetKey always writes the latest. The parameters file's own Version is LatestVersion.
    // =====================================================================================================

    /// <summary>
    /// A hash that depends ONLY on the string: 64-bit FNV-1a over its UTF-8 bytes. The same string gives the same
    /// number on any machine, process, thread or .NET version - unlike <see cref="string.GetHashCode"/>, which is
    /// randomised per process. 64 bits, not 32: with thousands of keys a 32-bit hash has a real chance (about 1 in 100)
    /// of two sources colliding; 64 bits make that negligible.
    /// </summary>
    public static class StableHash
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Fnv1a64(string text)
        {
            ulong hash = OffsetBasis;
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            unchecked
            {
                foreach (byte b in bytes)
                {
                    hash ^= b;
                    hash *= Prime;
                }
            }
            return hash;
        }

        /// <summary>The hash as a decimal number string (invariant culture).</summary>
        public static string Decimal(string text) => Fnv1a64(text).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The source of a Lock / Slip or Clutch learner: its composite identity (each wheel's script type and
    /// source - script text already reduced to a stable hash - plus the ShakeIt algorithm settings when it is ShakeIt;
    /// <c>GameClutch</c> / <c>Axis:&lt;name&gt;</c> / <c>None</c> for the Clutch).</summary>
    public sealed class LearnerSourceKeyConfig
    {
        public LearnerSourceKeyConfig(string identity) { Identity = identity ?? string.Empty; }
        public string Identity { get; }
    }

    /// <summary>The source of a Slip Angle learner: its identity (Native / NativeRatio / car-angle fallback) and the
    /// game's full source definition (transform, angle sources, surface sources, unit, vertical acceleration).</summary>
    public sealed class SlipAngleSourceKeyConfig
    {
        public SlipAngleSourceKeyConfig(string identity, string definition)
        {
            Identity = identity ?? string.Empty;
            Definition = definition ?? string.Empty;
        }

        public string Identity { get; }
        public string Definition { get; }
    }

    /// <summary>
    /// One version's rule: how a configuration object becomes the text that is hashed. Derive one per version and
    /// register it in <see cref="KeyRuleFactory"/>; never change a released rule (its keys would stop matching).
    /// </summary>
    public abstract class KeyRule
    {
        /// <summary>The newest rule's version - also the parameters file's Version.</summary>
        public const string LatestVersion = "21";

        /// <summary>The version of a source part written with NO version prefix - the readable identity of every file
        /// before Version 21 (owner, 2026-11-25).</summary>
        public const string LegacyVersion = "20";

        /// <summary><see cref="LatestVersion"/> as the parameters file's integer Version.</summary>
        public static int LatestVersionNumber => int.Parse(LatestVersion, CultureInfo.InvariantCulture);

        /// <summary>This rule's version (the key's prefix).</summary>
        public abstract string Version { get; }

        /// <summary>The text hashed for <paramref name="config"/>; null when this rule has no form for that type.</summary>
        protected abstract string Describe(object config);

        /// <summary>"<see cref="Version"/>:&lt;hash&gt;" for <paramref name="config"/>.</summary>
        public virtual string GetKey<T>(T config)
        {
            string text = Describe(config);
            if (text == null) throw new ArgumentException("Key rule " + Version + " has no form for " + typeof(T).Name);
            return Version + ":" + StableHash.Decimal(text);
        }
    }

    /// <summary>
    /// Version 20 and earlier: the source part was the READABLE identity itself, with no version prefix (e.g.
    /// <c>Plain:QAdvanceFeedback.WheelLock.Raw.FrontLeft~...</c>, <c>SlipAngle.PluginInternal.Native</c>). A source part
    /// without a prefix is read under this rule (<see cref="Keys.VersionOf"/>).
    /// </summary>
    public sealed class KeyRule20 : KeyRule
    {
        public override string Version => LegacyVersion;

        protected override string Describe(object config)
        {
            switch (config)
            {
                case LearnerSourceKeyConfig c: return c.Identity;
                case SlipAngleSourceKeyConfig c: return c.Identity;   // no definition: Version 20 keyed by identity only
                default: return null;
            }
        }

        /// <summary>The identity as it is - no prefix, no hash.</summary>
        public override string GetKey<T>(T config)
        {
            string text = Describe(config);
            if (text == null) throw new ArgumentException("Key rule " + Version + " has no form for " + typeof(T).Name);
            return text;
        }
    }

    /// <summary>Version 21 (1.2.2): the first hashed rule.</summary>
    public sealed class KeyRule21 : KeyRule
    {
        public override string Version => "21";

        protected override string Describe(object config)
        {
            switch (config)
            {
                case LearnerSourceKeyConfig c: return "v21|learner|" + c.Identity;
                case SlipAngleSourceKeyConfig c: return "v21|slipangle|" + c.Identity + "|" + c.Definition;
                default: return null;
            }
        }
    }

    /// <summary>Builds the rule a version names. Every released version stays here.</summary>
    public static class KeyRuleFactory
    {
        private static readonly Dictionary<string, Func<KeyRule>> Rules = new Dictionary<string, Func<KeyRule>>(StringComparer.Ordinal)
        {
            [KeyRule.LegacyVersion] = () => new KeyRule20(),
            ["21"] = () => new KeyRule21(),
        };

        /// <summary>The rule of <paramref name="version"/>; null when no such version exists.</summary>
        public static KeyRule CreateKeyRule(string version) =>
            version != null && Rules.TryGetValue(version, out Func<KeyRule> make) ? make() : null;
    }

    /// <summary>The helpers the rest of the plugin uses.</summary>
    public static class Keys
    {
        private static readonly KeyRule Latest = KeyRuleFactory.CreateKeyRule(KeyRule.LatestVersion);

        /// <summary>The latest-version key of <paramref name="config"/>.</summary>
        public static string GetKey<T>(T config) => Latest.GetKey(config);

        /// <summary>
        /// Whether <paramref name="key"/> is <paramref name="config"/>'s key under the rule of the version the key
        /// itself names (so an older key still matches the configuration it was made from).
        /// </summary>
        public static bool Matches<T>(string key, T config)
        {
            KeyRule rule = KeyRuleFactory.CreateKeyRule(VersionOf(key));
            if (rule == null) return false;
            try { return string.Equals(rule.GetKey(config), key, StringComparison.Ordinal); }
            catch (ArgumentException) { return false; }
        }

        /// <summary>
        /// The version a source part was written under: "21" of "21:123"; <see cref="KeyRule.LegacyVersion"/> ("20") for a
        /// part with no version prefix (the readable identity of an older file); null for an empty one.
        /// </summary>
        public static string VersionOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return IsKey(key) ? key.Substring(0, key.IndexOf(':')) : KeyRule.LegacyVersion;
        }

        /// <summary>True for "&lt;digits&gt;:&lt;digits&gt;" (the hashed form).</summary>
        public static bool IsKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int colon = text.IndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (i == colon) continue;
                if (text[i] < '0' || text[i] > '9') return false;
            }
            return true;
        }
    }

    /// <summary>
    /// THE SOURCE PART OF A LEARNER KEY. Identities stay readable in memory - every check that classifies a source
    /// (Raw / ShakeIt / Viper, the Slip Angle scale) keeps reading them - and only the key holds the hash:
    /// <see cref="ToKey"/> hashes a source segment when a key is built (and remembers which identity a hash came
    /// from), <see cref="FromKey"/> turns a parsed segment back into that identity. A hash not seen yet this session
    /// (a source not in use) parses as itself, and <see cref="ToKey"/> leaves an already hashed segment alone - so
    /// rebuilding a parsed key always gives the same key back.
    /// </summary>
    public static class SourceKeys
    {
        /// <summary>The prefix of every Slip Angle identity (its segment is "identity/axle").</summary>
        public const string SlipAngleIdentityPrefix = "SlipAngle.";

        private const char AxleSeparator = '/';

        private static readonly ConcurrentDictionary<string, string> IdentityByKey = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> KeyByLearnerIdentity = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, CachedSlipAngleKey> KeyBySlipAngle = new ConcurrentDictionary<string, CachedSlipAngleKey>(StringComparer.Ordinal);

        private sealed class CachedSlipAngleKey
        {
            public int Revision;
            public string Key;
        }

        /// <summary>
        /// The game's Slip Angle source definition (transform, angle sources, surface sources, unit, vertical
        /// acceleration) as one text - set by the plugin; empty by default (tests, no plugin).
        /// </summary>
        public static Func<string, string> SlipAngleDefinition { get; set; } = _ => string.Empty;

        private static int _slipAngleDefinitionRevision;

        /// <summary>Bumped whenever a game's Slip Angle definition may have changed (the source file, a unit, the
        /// vertical flag): its keys are formed again from the new definition.</summary>
        public static void SlipAngleDefinitionChanged() => System.Threading.Interlocked.Increment(ref _slipAngleDefinitionRevision);

        /// <summary>The source segment as stored in a key - empty stays empty.</summary>
        public static string ToKey(string gameId, string source)
        {
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;
            // Already a hashed part (the latest rule, or an older hashed one only Matches can recognise): kept. A part with
            // no version prefix is Version 20 - its text IS the identity (KeyRule20), hashed under the latest rule here.
            if (IsHashedSegment(source)) return source;

            if (source.StartsWith(SlipAngleIdentityPrefix, StringComparison.Ordinal))
            {
                int cut = source.LastIndexOf(AxleSeparator);
                string identity = cut < 0 ? source : source.Substring(0, cut);
                string axle = cut < 0 ? null : source.Substring(cut + 1);
                string key = SlipAngleKey(gameId ?? string.Empty, identity);
                return axle == null ? key : key + AxleSeparator + axle;
            }

            return KeyByLearnerIdentity.GetOrAdd(source, s =>
            {
                string k = Keys.GetKey(new LearnerSourceKeyConfig(s));
                IdentityByKey[k] = s;
                return k;
            });
        }

        /// <summary>The identity a stored source segment came from (the segment itself when unknown).</summary>
        public static string FromKey(string segment)
        {
            if (string.IsNullOrEmpty(segment) || !IsHashedSegment(segment)) return segment ?? string.Empty;
            int cut = segment.IndexOf(AxleSeparator);
            string head = cut < 0 ? segment : segment.Substring(0, cut);
            if (!IdentityByKey.TryGetValue(head, out string identity)) return segment;
            return cut < 0 ? identity : identity + segment.Substring(cut);
        }

        /// <summary>A hashed segment: "&lt;version&gt;:&lt;hash&gt;", optionally followed by "/axle".</summary>
        public static bool IsHashedSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            int cut = segment.IndexOf(AxleSeparator);
            return Keys.IsKey(cut < 0 ? segment : segment.Substring(0, cut));
        }

        private static string SlipAngleKey(string gameId, string identity)
        {
            int revision = System.Threading.Volatile.Read(ref _slipAngleDefinitionRevision);
            string cacheKey = gameId + "\u0001" + identity;
            if (KeyBySlipAngle.TryGetValue(cacheKey, out CachedSlipAngleKey cached) && cached.Revision == revision) return cached.Key;
            string definition = SlipAngleDefinition?.Invoke(gameId) ?? string.Empty;
            string key = Keys.GetKey(new SlipAngleSourceKeyConfig(identity, definition));
            IdentityByKey[key] = identity;
            KeyBySlipAngle[cacheKey] = new CachedSlipAngleKey { Revision = revision, Key = key };
            return key;
        }

        // ---------------------------------------------------------------- loading older keys

        private const string KeySeparator = "|#|";
        private const int SourcePart = 2;

        /// <summary>
        /// One stored learner key with its source part in the latest form (game |#| car |#| SOURCE |#| ...): a readable
        /// identity (a Version 20 file) is hashed; an already hashed one is kept. Only the source part changes - the key
        /// keeps its shape. A key with fewer parts, or no source, comes back as it was.
        /// </summary>
        public static string RekeyLearnerKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string[] parts = key.Split(new[] { KeySeparator }, StringSplitOptions.None);
            if (parts.Length <= SourcePart || string.IsNullOrEmpty(parts[SourcePart]) || IsHashedSegment(parts[SourcePart])) return key;
            parts[SourcePart] = ToKey(parts[0], parts[SourcePart]);
            return string.Join(KeySeparator, parts);
        }

        /// <summary>
        /// Every key of a loaded learner section in the latest form (see <see cref="RekeyLearnerKey"/>). Two old keys
        /// can only meet on one new key if they had the same source - then the first one read is kept. Null stays null.
        /// </summary>
        public static Dictionary<string, T> RekeyLearners<T>(Dictionary<string, T> data)
        {
            if (data == null) return null;
            var result = new Dictionary<string, T>(data.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, T> kv in data)
            {
                if (kv.Key == null) continue;
                string key = RekeyLearnerKey(kv.Key);
                if (!result.ContainsKey(key)) result[key] = kv.Value;
            }
            return result;
        }

        /// <summary>Tests only: forgets every hash seen.</summary>
        public static void ResetForTests()
        {
            IdentityByKey.Clear();
            KeyByLearnerIdentity.Clear();
            KeyBySlipAngle.Clear();
            SlipAngleDefinition = _ => string.Empty;
            SlipAngleDefinitionChanged();
        }
    }
}

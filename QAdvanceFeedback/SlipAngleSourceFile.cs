using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Runtime;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback
{
    /// <summary>
    /// A row's <c>id</c> (owner, 2026-11-23), matched case-insensitively against SimHub's whole game id:
    /// <list type="bullet">
    /// <item><c>|</c> separates alternatives: <c>ACC|AssettoCorsaCompetizione</c>.</item>
    /// <item>A term with <c>*</c> (any run of characters) or <c>?</c> (exactly one) is a wildcard: <c>FM?|FH?</c>.</item>
    /// <item><c>regex:</c> starts a regular expression that runs to the END of the id (so it may use <c>|</c> itself):
    /// <c>regex:^F120(2[3-9]|[3-9]\d)$</c>. It must match the whole game id.</item>
    /// <item>Any other term is an exact game id. An exact game id may appear in only ONE row of the file.</item>
    /// </list>
    /// WHICH ROW A GAME GETS (owner, 2026-11-24) - the best LEVEL any of a row's terms matches it at, then file order:
    /// 1 the id is exactly that one name (<c>F12024</c>); 2 the name inside a list (<c>F12019|F12024</c>,
    /// <c>F12024|regex:...</c>); 3 a wildcard with <c>?</c> only (<c>F1202?</c>); 4 a wildcard with <c>*</c>
    /// (<c>F1*</c>, <c>F1?2*</c>); 5 a regex. A name being unique, levels 1 and 2 never compete for one game.
    /// </summary>
    public sealed class SlipAngleGameIdPattern
    {
        public const string RegexPrefix = "regex:";
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary>The match levels (lower wins): see the class remarks.</summary>
        public const int LevelSingleName = 1, LevelListedName = 2, LevelQuestionMark = 3, LevelStar = 4, LevelRegex = 5;

        private readonly List<string> _terms;          // exact names and wildcards, in file order
        private readonly HashSet<string> _exact;
        private readonly List<Regex> _question;        // wildcards with ? only
        private readonly List<Regex> _star;            // wildcards with *
        private readonly Regex _regex;
        private readonly string _regexText;

        private SlipAngleGameIdPattern(List<string> terms, string regexText)
        {
            _terms = terms;
            _regexText = regexText;
            _exact = new HashSet<string>(terms.Where(t => !IsWildcard(t)), StringComparer.OrdinalIgnoreCase);
            _question = terms.Where(t => IsWildcard(t) && t.IndexOf('*') < 0).Select(Glob).ToList();
            _star = terms.Where(t => t.IndexOf('*') >= 0).Select(Glob).ToList();
            if (regexText != null)
                _regex = new Regex("^(?:" + regexText + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }

        private static Regex Glob(string term) =>
            new Regex("^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        /// <summary>The best level any term matches <paramref name="gameId"/> at (<see cref="LevelSingleName"/> ...
        /// <see cref="LevelRegex"/>); 0 when none does.</summary>
        public int Level(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return 0;
            if (_exact.Contains(gameId)) return _terms.Count + (_regex != null ? 1 : 0) == 1 ? LevelSingleName : LevelListedName;
            if (Any(_question, gameId)) return LevelQuestionMark;
            if (Any(_star, gameId)) return LevelStar;
            if (_regex != null && Any(new[] { _regex }, gameId)) return LevelRegex;
            return 0;
        }

        private static bool Any(IEnumerable<Regex> patterns, string gameId)
        {
            foreach (Regex r in patterns)
            {
                try { if (r.IsMatch(gameId)) return true; }
                catch (RegexMatchTimeoutException) { }
            }
            return false;
        }

        /// <summary>The exact game ids this id names.</summary>
        public IReadOnlyCollection<string> ExactNames => _exact;

        /// <summary>True when the id has a wildcard or a regex.</summary>
        public bool HasPatterns => _question.Count > 0 || _star.Count > 0 || _regex != null;

        public bool IsExact(string gameId) => !string.IsNullOrEmpty(gameId) && _exact.Contains(gameId);

        /// <summary>True when a wildcard or the regex (not an exact name) matches <paramref name="gameId"/>.</summary>
        public bool MatchesPattern(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            return Any(_question, gameId) || Any(_star, gameId) || (_regex != null && Any(new[] { _regex }, gameId));
        }

        /// <summary>The pattern level only (3, 4 or 5; 0 when no wildcard or regex matches) - exact names ignored.</summary>
        public int PatternLevel(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return 0;
            if (Any(_question, gameId)) return LevelQuestionMark;
            if (Any(_star, gameId)) return LevelStar;
            if (_regex != null && Any(new[] { _regex }, gameId)) return LevelRegex;
            return 0;
        }

        public bool Matches(string gameId) => IsExact(gameId) || MatchesPattern(gameId);

        /// <summary>Parses an id; throws <see cref="FormatException"/> for an empty term or a bad regex.</summary>
        public static SlipAngleGameIdPattern Parse(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new FormatException("\"id\" is missing.");
            string text = id.Trim();
            string regex = null;
            int at = text.IndexOf(RegexPrefix, StringComparison.OrdinalIgnoreCase);
            while (at > 0 && text[at - 1] != '|') at = text.IndexOf(RegexPrefix, at + 1, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                regex = text.Substring(at + RegexPrefix.Length);
                if (regex.Trim().Length == 0) throw new FormatException("\"" + id + "\": the regex after \"regex:\" is empty.");
                text = at == 0 ? string.Empty : text.Substring(0, at - 1);
            }
            var terms = new List<string>();
            if (text.Length > 0 || regex == null)
            {
                foreach (string t in text.Split('|'))
                {
                    if (t.Trim().Length == 0) throw new FormatException("\"" + id + "\" has an empty name between \"|\".");
                    terms.Add(t.Trim());
                }
            }
            try { return new SlipAngleGameIdPattern(terms, regex); }
            catch (ArgumentException e) { throw new FormatException("\"" + id + "\": " + e.Message, e); }
        }

        /// <summary>The id with <paramref name="exactName"/> added (before any regex, which runs to the end).</summary>
        public string With(string exactName) => Compose(_terms.Concat(new[] { exactName }), _regexText);

        /// <summary>The id without the exact name <paramref name="exactName"/>; empty when nothing is left.</summary>
        public string Without(string exactName) =>
            Compose(_terms.Where(t => IsWildcard(t) || !string.Equals(t, exactName, StringComparison.OrdinalIgnoreCase)), _regexText);

        private static string Compose(IEnumerable<string> terms, string regex)
        {
            var parts = terms.ToList();
            if (regex != null) parts.Add(RegexPrefix + regex);
            return string.Join("|", parts);
        }

        private static bool IsWildcard(string term) => term.IndexOf('*') >= 0 || term.IndexOf('?') >= 0;
    }

    /// <summary>One row in <c>QAdvanceFeedback.SlipAngleSource.json</c> (docs\slip-angle-source-json-report.md): the
    /// games its <see cref="Id"/> matches (<see cref="SlipAngleGameIdPattern"/>) and their settings. Every path is a full
    /// SimHub property name; the four-path lists are front left, front right, rear left, rear right. A row with an exact
    /// id and nothing else means "no per-wheel source for this game" (it overrides a wildcard row). Fields this version
    /// does not know are kept (<see cref="Extra"/>).</summary>
    public sealed class SlipAngleSourceGame
    {
        [JsonProperty("id")] public string Id { get; set; }
        /// <summary>The per-wheel slip angle (or, for <c>atan2PatchVelocity</c>, the lateral patch velocity); null for
        /// a surface-only game.</summary>
        [JsonProperty("slipAngle", NullValueHandling = NullValueHandling.Ignore)] public List<string> SlipAngle { get; set; }
        /// <summary>degrees, radians, ratio1 or ratio100; null = detected while driving.</summary>
        [JsonProperty("unit", NullValueHandling = NullValueHandling.Ignore)] public string Unit { get; set; }
        /// <summary>direct (default), atanOfTangent or atan2PatchVelocity.</summary>
        [JsonProperty("transform", NullValueHandling = NullValueHandling.Ignore)] public string Transform { get; set; }
        [JsonProperty("longitudinal", NullValueHandling = NullValueHandling.Ignore)] public List<string> Longitudinal { get; set; }
        [JsonProperty("surface", NullValueHandling = NullValueHandling.Ignore)] public List<string> Surface { get; set; }
        /// <summary>What the <see cref="Surface"/> values mean (owner, 2026-11-23): raw value (as SimHub publishes it - a
        /// number or an enum name, case-insensitive) to sealed, loose or lowGrip. A value not listed is unknown. Without
        /// it the surface fields are read but not used, and SimHub's own grass/gravel flag is used instead.</summary>
        [JsonProperty("surfaceValues", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string> SurfaceValues { get; set; }
        [JsonProperty("provisional", NullValueHandling = NullValueHandling.Ignore)] public bool? Provisional { get; set; }
        /// <summary>True when the game really writes SimHub's vertical acceleration (<c>FeedbackData.VerticalAcceleration</c>,
        /// owner 2026-11-24): bumps and hops then pause Slip Angle learning. Most games leave that field at 0, so it is read
        /// only for a row that says so.</summary>
        [JsonProperty("verticalAcceleration", NullValueHandling = NullValueHandling.Ignore)] public bool? VerticalAcceleration { get; set; }
        /// <summary>Free text (which games a pattern matches, wheel order remarks). Never read by the plugin.</summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        /// <summary>True when the row has a per-wheel slip-angle source (not surface-only, not an empty override).</summary>
        [JsonIgnore] public bool HasSlipAngle => SlipAngle != null && SlipAngle.Count > 0;

        /// <summary>True when the four slip-angle paths are read as they are (the only kind the settings page edits).</summary>
        [JsonIgnore] public bool IsDirect => string.IsNullOrEmpty(Transform) || Same(Transform, "direct");

        /// <summary>True when the row holds no settings at all (an exact "no source" override).</summary>
        [JsonIgnore] public bool IsEmpty => SameSettings(this, null);

        public SlipAngleSourceGame Clone() => JToken.FromObject(this).ToObject<SlipAngleSourceGame>();

        /// <summary>A copy with only the settings (no id, note or unknown fields).</summary>
        public SlipAngleSourceGame SettingsOnly() => new SlipAngleSourceGame
        {
            SlipAngle = SlipAngle?.ToList(), Unit = Unit, Transform = Transform, Longitudinal = Longitudinal?.ToList(),
            Surface = Surface?.ToList(), Provisional = Provisional, VerticalAcceleration = VerticalAcceleration,
            SurfaceValues = SurfaceValues == null ? null : new Dictionary<string, string>(SurfaceValues),
        };

        /// <summary>
        /// THE MERGE KEY (owner, 2026-11-23): two rows define the same thing when slipAngle, longitudinal, surface,
        /// surfaceValues, unit, transform, provisional and verticalAcceleration agree (case ignored; no transform = direct, no provisional = false). The id and the
        /// note are not part of it. Null is the empty row.
        /// </summary>
        public static bool SameSettings(SlipAngleSourceGame a, SlipAngleSourceGame b) =>
            JToken.DeepEquals(Key(a), Key(b));

        private static JObject Key(SlipAngleSourceGame g)
        {
            var o = new JObject();
            void List(string name, List<string> v) { if (v != null && v.Count > 0) o[name] = new JArray(v.Select(x => (x ?? string.Empty).Trim().ToLowerInvariant())); }
            void Text(string name, string v) { if (!string.IsNullOrWhiteSpace(v)) o[name] = v.Trim().ToLowerInvariant(); }
            if (g == null) return o;
            List("slipAngle", g.SlipAngle);
            List("longitudinal", g.Longitudinal);
            List("surface", g.Surface);
            if (g.SurfaceValues != null && g.SurfaceValues.Count > 0)
            {
                var map = new JObject();
                foreach (KeyValuePair<string, string> kv in g.SurfaceValues
                    .Select(kv => new KeyValuePair<string, string>((kv.Key ?? string.Empty).Trim().ToLowerInvariant(), (kv.Value ?? string.Empty).Trim().ToLowerInvariant()))
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    map[kv.Key] = kv.Value;
                o["surfaceValues"] = map;
            }
            // A velocity row is always radians: a unit written on it (an older file's) means nothing.
            if (!Same(g.Transform, "atan2PatchVelocity")) Text("unit", g.Unit);
            if (!g.IsDirect) Text("transform", g.Transform);
            if (g.Provisional == true) o["provisional"] = true;
            if (g.VerticalAcceleration == true) o["verticalAcceleration"] = true;
            return o;
        }

        internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The whole file.</summary>
    public sealed class SlipAngleSourceDocument
    {
        [JsonProperty("about", NullValueHandling = NullValueHandling.Ignore)] public string About { get; set; }
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("games")] public List<SlipAngleSourceGame> Games { get; set; } = new List<SlipAngleSourceGame>();
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        public static SlipAngleSourceDocument FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("The file is empty.");
            SlipAngleSourceDocument doc;
            try { doc = JsonConvert.DeserializeObject<SlipAngleSourceDocument>(json); }
            catch (JsonException e) { throw new FormatException(e.Message, e); }
            if (doc == null) throw new FormatException("The file holds no JSON object.");
            if (doc.Games == null) throw new FormatException("\"games\" is missing.");
            return doc;
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented) + Environment.NewLine;
    }

    /// <summary>
    /// The checked, read-only form of a <see cref="SlipAngleSourceDocument"/> the per-frame reader uses
    /// (<see cref="SlipAngleRawSourceTable"/>). WHICH ROW A GAME GETS: the row that names it (a name is in one row only);
    /// else the row whose best wildcard / regex level is best - ? before * before regex - and, at one level, the first
    /// in file order (<see cref="SlipAngleGameIdPattern"/>); else none. <see cref="Parse"/> refuses a file with any invalid
    /// row, naming it, so a typo never half-applies. Lookups are cached per game id (the catalog never changes).
    /// </summary>
    public sealed class SlipAngleSourceCatalog
    {
        private readonly List<Row> _rows;
        private readonly Dictionary<string, Row> _exact = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Row> _resolved = new ConcurrentDictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

        private sealed class Row
        {
            public SlipAngleSourceGame Game;
            public SlipAngleGameIdPattern Pattern;
            public SlipAngleRawSourceEntry Entry;
            public string[] Surface;
            public Dictionary<string, SurfaceBucket> SurfaceValues;
            public bool VerticalAcceleration;
        }

        public static readonly SlipAngleSourceCatalog Empty = new SlipAngleSourceCatalog(new List<Row>());

        private SlipAngleSourceCatalog(List<Row> rows)
        {
            _rows = rows;
            foreach (Row r in rows)
                foreach (string name in r.Pattern.ExactNames) _exact[name] = r;
            NativeGameIds = rows.Where(r => r.Entry != null).Select(r => r.Game.Id).ToArray();
        }

        /// <summary>The ids (as written, patterns included) of the rows with a per-wheel slip-angle source.</summary>
        public IReadOnlyCollection<string> NativeGameIds { get; }

        /// <summary>Copies of every row, in file order.</summary>
        public IReadOnlyList<SlipAngleSourceGame> Games => _rows.Select(r => r.Game.Clone()).ToList();

        public bool TryGetNative(string gameId, out SlipAngleRawSourceEntry entry)
        {
            entry = Resolve(gameId)?.Entry;
            return entry != null;
        }

        public string[] SurfacePaths(string gameId) => Resolve(gameId)?.Surface;

        /// <summary>What the game's surface values mean (raw value, case-insensitive, to bucket); null when its row has
        /// no translation.</summary>
        public IReadOnlyDictionary<string, SurfaceBucket> SurfaceValues(string gameId) => Resolve(gameId)?.SurfaceValues;

        /// <summary>True when the game's row says SimHub's vertical acceleration is real for it (<c>verticalAcceleration</c>).</summary>
        public bool ReportsVerticalAcceleration(string gameId) => Resolve(gameId)?.VerticalAcceleration == true;

        /// <summary>True when the game's row translates some surface value to Low-grip.</summary>
        public bool ReportsLowGrip(string gameId) => Resolve(gameId)?.SurfaceValues?.ContainsValue(SurfaceBucket.LowGrip) == true;

        /// <summary>A copy of the row <paramref name="gameId"/> gets; null when there is none.</summary>
        public SlipAngleSourceGame Find(string gameId) => Resolve(gameId)?.Game.Clone();

        private Row Resolve(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            return _resolved.GetOrAdd(gameId, id =>
            {
                if (_exact.TryGetValue(id, out Row exact)) return exact;
                Row best = null;
                int bestLevel = int.MaxValue;
                foreach (Row r in _rows)   // the best pattern level; the first row at that level
                {
                    int level = r.Pattern.PatternLevel(id);
                    if (level > 0 && level < bestLevel) { best = r; bestLevel = level; }
                }
                return best;
            });
        }

        /// <summary>The row <paramref name="gameId"/> gets in a document that is being edited (same rule).</summary>
        internal static int ResolveIndex(IList<SlipAngleSourceGame> games, string gameId)
        {
            var patterns = games.Select(g => SlipAngleGameIdPattern.Parse(g.Id)).ToList();
            int exact = patterns.FindIndex(p => p.IsExact(gameId));
            if (exact >= 0) return exact;
            int best = -1, bestLevel = int.MaxValue;
            for (int i = 0; i < patterns.Count; i++)
            {
                int level = patterns[i].PatternLevel(gameId);
                if (level > 0 && level < bestLevel) { best = i; bestLevel = level; }
            }
            return best;
        }

        /// <summary>Parses and checks a whole file. Throws <see cref="FormatException"/> naming the first problem.</summary>
        public static SlipAngleSourceCatalog Parse(string json) => FromDocument(SlipAngleSourceDocument.FromJson(json));

        public static SlipAngleSourceCatalog FromDocument(SlipAngleSourceDocument doc)
        {
            if (doc?.Games == null) throw new FormatException("\"games\" is missing.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<Row>();
            for (int i = 0; i < doc.Games.Count; i++)
            {
                SlipAngleSourceGame g = doc.Games[i];
                string where = "games[" + i + "]" + (string.IsNullOrWhiteSpace(g?.Id) ? string.Empty : " (" + g.Id + ")");
                if (g == null || string.IsNullOrWhiteSpace(g.Id)) throw new FormatException(where + ": \"id\" is missing.");
                SlipAngleGameIdPattern pattern;
                try { pattern = SlipAngleGameIdPattern.Parse(g.Id); }
                catch (FormatException e) { throw new FormatException(where + ": " + e.Message, e); }
                foreach (string name in pattern.ExactNames)
                    if (!seen.Add(name)) throw new FormatException(where + ": \"" + name + "\" is named by two rows.");
                CheckFour(where, "slipAngle", g.SlipAngle, required: false);
                CheckFour(where, "surface", g.Surface, required: false);
                if (!g.HasSlipAngle && g.Surface == null && g.VerticalAcceleration != true && pattern.HasPatterns)
                    throw new FormatException(where + ": a wildcard or regex row needs \"slipAngle\", \"surface\" or \"verticalAcceleration\".");
                if (g.Unit != null && !TryParseUnit(g.Unit, out _))
                    throw new FormatException(where + ": \"unit\" must be degrees, radians, ratio1 or ratio100.");
                if (g.Transform != null && !TryParseTransform(g.Transform, out _))
                    throw new FormatException(where + ": \"transform\" must be direct, atanOfTangent or atan2PatchVelocity.");
                bool atan2 = ParseTransform(g.Transform) == SlipAngleTransform.Atan2PatchVelocity;
                if (atan2 && !g.HasSlipAngle) throw new FormatException(where + ": atan2PatchVelocity needs \"slipAngle\".");
                CheckFour(where, "longitudinal", g.Longitudinal, required: atan2);
                Dictionary<string, SurfaceBucket> values = null;
                if (g.SurfaceValues != null)
                {
                    if (g.Surface == null) throw new FormatException(where + ": \"surfaceValues\" needs \"surface\".");
                    values = new Dictionary<string, SurfaceBucket>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, string> kv in g.SurfaceValues)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || !SurfaceBucketMap.TryParseName(kv.Value, out SurfaceBucket bucket))
                            throw new FormatException(where + ": \"surfaceValues\" maps each value to sealed, loose or lowGrip.");
                        values[kv.Key.Trim()] = bucket;
                    }
                }

                SlipAngleSourceGame copy = g.Clone();
                string[] surface = copy.Surface?.ToArray();
                rows.Add(new Row
                {
                    Game = copy,
                    Pattern = pattern,
                    Surface = surface,
                    SurfaceValues = values,
                    VerticalAcceleration = copy.VerticalAcceleration == true,
                    Entry = copy.HasSlipAngle
                        ? new SlipAngleRawSourceEntry(copy.Id, copy.SlipAngle.ToArray(), copy.Longitudinal?.ToArray(),
                            // atan2 of the two velocities is always radians (owner, 2026-11-24: no unit field for it).
                            ParseTransform(copy.Transform) == SlipAngleTransform.Atan2PatchVelocity ? SlipAngleUnit.Radians : ParseUnit(copy.Unit),
                            ParseTransform(copy.Transform), surface, copy.Provisional == true, copy.Note)
                        : null,
                });
            }
            return new SlipAngleSourceCatalog(rows);
        }

        private static void CheckFour(string where, string name, List<string> paths, bool required)
        {
            if (paths == null)
            {
                if (required) throw new FormatException(where + ": \"" + name + "\" is missing.");
                return;
            }
            if (paths.Count != 4 || paths.Any(string.IsNullOrWhiteSpace))
                throw new FormatException(where + ": \"" + name + "\" needs four property names (FL, FR, RL, RR).");
        }

        internal static bool TryParseUnit(string text, out SlipAngleUnit unit)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "degrees": unit = SlipAngleUnit.Degrees; return true;
                case "radians": unit = SlipAngleUnit.Radians; return true;
                case "ratio1": unit = SlipAngleUnit.Ratio1; return true;
                case "ratio100": unit = SlipAngleUnit.Ratio100; return true;
                default: unit = SlipAngleUnit.Unverified; return false;
            }
        }

        private static SlipAngleUnit ParseUnit(string text) => TryParseUnit(text, out SlipAngleUnit u) ? u : SlipAngleUnit.Unverified;

        private static bool TryParseTransform(string text, out SlipAngleTransform t)
        {
            switch ((text ?? "direct").Trim().ToLowerInvariant())
            {
                case "direct": t = SlipAngleTransform.Direct; return true;
                case "atanoftangent": t = SlipAngleTransform.AtanOfTangent; return true;
                case "atan2patchvelocity": t = SlipAngleTransform.Atan2PatchVelocity; return true;
                default: t = SlipAngleTransform.Direct; return false;
            }
        }

        private static SlipAngleTransform ParseTransform(string text) => TryParseTransform(text, out SlipAngleTransform t) ? t : SlipAngleTransform.Direct;
    }

    /// <summary>The copy compiled into the DLL (the solution root's <c>QAdvanceFeedback.SlipAngleSource.json</c>,
    /// an embedded resource). It seeds a missing file and is what "Restore default" goes back to.</summary>
    public static class SlipAngleSourceCompiled
    {
        public const string ResourceName = "QAdvanceFeedback.SlipAngleSource.json";

        private static readonly Lazy<string> TextValue = new Lazy<string>(ReadResource);
        private static readonly Lazy<SlipAngleSourceCatalog> CatalogValue = new Lazy<SlipAngleSourceCatalog>(() =>
        {
            try { return SlipAngleSourceCatalog.Parse(Text); }
            catch (FormatException) { return SlipAngleSourceCatalog.Empty; }
        });

        public static string Text => TextValue.Value;
        public static SlipAngleSourceCatalog Catalog => CatalogValue.Value;

        private static string ReadResource()
        {
            using (Stream s = typeof(SlipAngleSourceCompiled).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) return string.Empty;
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }
    }

    /// <summary>What a game's per-wheel slip-angle telemetry is (the page's Telemetry Type dropdown).</summary>
    public enum SlipAngleTelemetryType
    {
        /// <summary>No per-wheel slip angle: the car angle is used.</summary>
        None = 0,
        /// <summary>Four angle properties (<c>slipAngle</c>), read as they are (or a tangent, LFS).</summary>
        AngleOnly = 1,
        /// <summary>Lateral + longitudinal velocity per wheel: angle = atan2(lateral, |longitudinal|) (<c>atan2PatchVelocity</c>).</summary>
        LateralLongitudinal = 2,
    }

    /// <summary>One game's per-wheel source as the settings page edits it (FL, FR, RL, RR in every list).</summary>
    public sealed class SlipAngleSourceEdit
    {
        public SlipAngleTelemetryType Type { get; set; }
        /// <summary>Per-Wheel Angle's Source Unit (owner, 2026-11-24): the row's <c>unit</c> - degrees, radians, ratio1 or
        /// ratio100 - or null for Auto Detect. Not used for Lateral + Longitudinal Velocity (always radians, no field).</summary>
        public string Unit { get; set; }
        public IReadOnlyList<string> Angle { get; set; }
        public IReadOnlyList<string> Lateral { get; set; }
        public IReadOnlyList<string> Longitudinal { get; set; }
        public bool HasSurface { get; set; }
        public IReadOnlyList<string> Surface { get; set; }
        /// <summary>"Has Vertical Acceleration Data?" - the row's <c>verticalAcceleration</c>.</summary>
        public bool HasVerticalAcceleration { get; set; }
        /// <summary>Leave the row's surface and vertical-acceleration flag as they are in the file (the angle-only
        /// <c>Save</c> overload).</summary>
        public bool KeepSurface { get; set; }

        /// <summary>The page's editor state for a row (null: a game with no row).</summary>
        public static SlipAngleSourceEdit From(SlipAngleSourceGame row)
        {
            var e = new SlipAngleSourceEdit
            {
                Type = TypeOf(row), HasSurface = row?.Surface != null, Surface = row?.Surface,
                HasVerticalAcceleration = row?.VerticalAcceleration == true,
            };
            if (e.Type == SlipAngleTelemetryType.LateralLongitudinal) { e.Lateral = row.SlipAngle; e.Longitudinal = row.Longitudinal; }
            else if (e.Type == SlipAngleTelemetryType.AngleOnly) { e.Angle = row.SlipAngle; e.Unit = NormalUnit(row.Unit); }
            return e;
        }

        /// <summary>A unit as the file writes it (degrees, radians, ratio1, ratio100); null for none or one not known.</summary>
        public static string NormalUnit(string unit)
        {
            string u = (unit ?? string.Empty).Trim().ToLowerInvariant();
            return u == "degrees" || u == "radians" || u == "ratio1" || u == "ratio100" ? u : null;
        }

        public static SlipAngleTelemetryType TypeOf(SlipAngleSourceGame row) =>
            row == null || !row.HasSlipAngle ? SlipAngleTelemetryType.None
            : string.Equals(row.Transform, "atan2PatchVelocity", StringComparison.OrdinalIgnoreCase) ? SlipAngleTelemetryType.LateralLongitudinal
            : SlipAngleTelemetryType.AngleOnly;
    }

    /// <summary>What <see cref="SlipAngleSourceStore.Save"/> did.</summary>
    public enum SlipAngleSourceSaveResult
    {
        /// <summary>The game already got this; nothing written.</summary>
        Unchanged,
        /// <summary>The game's four paths were written (added, changed or restored).</summary>
        Saved,
        /// <summary>All four were empty: the game's slip-angle source was taken out.</summary>
        Removed,
        /// <summary>One to three paths were given: refused, nothing written.</summary>
        Incomplete,
        /// <summary>No game, or the file cannot be read as valid JSON (fix it by hand first).</summary>
        Unavailable,
        /// <summary>Writing the file failed (reported to Health).</summary>
        Failed,
    }

    /// <summary>
    /// The per-wheel slip-angle source file in SimHub's PluginsData\Common folder (owner, 2026-11-23): created from
    /// the compiled copy when missing, read at start and again when the running game changes (so a hand edit applies
    /// without restarting SimHub), and written by the settings page's Apply. THE FILE IS THE ONLY DEFINITION: the
    /// reader's table is built from it alone. A file that does not parse is never overwritten - the compiled copy is
    /// used for the session, Health says why, and the page's editor stays off until the file is fixed.
    /// Thread-safe (the data thread reloads, the UI thread saves).
    /// </summary>
    public sealed class SlipAngleSourceStore
    {
        public const string FileName = "QAdvanceFeedback.SlipAngleSource.json";

        private readonly object _gate = new object();
        private readonly Action<string> _logWarning;
        private readonly Action<SlipAngleSourceCatalog> _use;
        private DateTime _loadedWriteUtc;
        private long _loadedLength = -1;
        private SlipAngleSourceCatalog _active = SlipAngleSourceCompiled.Catalog;

        /// <param name="use">Where a loaded catalog goes; <see cref="SlipAngleRawSourceTable.Use"/> by default.</param>
        public SlipAngleSourceStore(string path, Action<string> logWarning = null, Action<SlipAngleSourceCatalog> use = null)
        {
            Path = path;
            _logWarning = logWarning;
            _use = use ?? SlipAngleRawSourceTable.Use;
        }

        public string Path { get; }

        /// <summary>The file exists but is not valid; the compiled copy is in use and the editor is off.</summary>
        public bool IsBroken { get; private set; }

        /// <summary>Why the file was refused (empty when it is fine).</summary>
        public string BrokenReason { get; private set; } = string.Empty;

        /// <summary>Raised after the catalog in use changed (any thread).</summary>
        public event Action Changed;

        /// <summary>The row <paramref name="gameId"/> gets now (a copy); null when the game has none.</summary>
        public SlipAngleSourceGame Current(string gameId) { lock (_gate) return _active.Find(gameId); }

        /// <summary>The row <paramref name="gameId"/> gets in the compiled copy; null when the plugin ships none.</summary>
        public SlipAngleSourceGame Compiled(string gameId) => SlipAngleSourceCompiled.Catalog.Find(gameId);

        /// <summary>True when the plugin ships a row for <paramref name="gameId"/> (the compiled copy names or matches it
        /// with some setting) - the only games "Restore this game's source" is shown for (owner, 2026-11-24).</summary>
        public bool HasCompiledDefault(string gameId)
        {
            SlipAngleSourceGame compiled = Compiled(gameId);
            return compiled != null && !compiled.IsEmpty;
        }

        /// <summary>The file's settings for a shipped game are not the compiled ones any more (however the ids are
        /// written): "Restore this game's source" is enabled.</summary>
        public bool CanRestore(string gameId) =>
            HasCompiledDefault(gameId) && !IsBroken && !SlipAngleSourceGame.SameSettings(Current(gameId), Compiled(gameId));

        /// <summary>Reads the file (creating it from the compiled copy when missing) and puts it in use.</summary>
        public void Load()
        {
            lock (_gate) LoadLocked();
        }

        /// <summary>Re-reads the file when it changed on disk, or creates it again when it was deleted. True when reloaded.</summary>
        public bool ReloadIfChanged()
        {
            lock (_gate)
            {
                try
                {
                    var info = new FileInfo(Path);
                    if (info.Exists && info.LastWriteTimeUtc == _loadedWriteUtc && info.Length == _loadedLength) return false;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                {
                    return false;
                }
                LoadLocked();
                return true;
            }
        }

        /// <summary>
        /// The settings page's Apply for one game. Four non-empty paths set its slip-angle source (keeping the unit,
        /// surface and provisional of the row it had; a computed row becomes a plain read); four empty ones take the
        /// source out (surface paths stay); anything in between is refused. <paramref name="restoreCompiled"/> gives it
        /// the compiled copy's settings. Re-reads the file first, so a hand edit made since is kept.
        /// <para/>
        /// MERGING (owner, 2026-11-23 / 2026-11-24): the game leaves the row that names it (a game is named in one row
        /// only; a row left with no name is deleted), then is ALWAYS named again: added to the id of the first row with
        /// exactly these settings (<c>F1202?|F1203?|F12024</c>), else given a row of its own. A game that should have
        /// nothing gets an exact empty row (<c>{"id":"FH6"}</c>, or joins one) only when a pattern still covers it.
        /// </summary>
        public SlipAngleSourceSaveResult Save(string gameId, IReadOnlyList<string> paths, bool restoreCompiled = false)
        {
            string[] p = (paths ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim()).ToArray();
            bool any = p.Any(x => x.Length > 0);
            return Save(gameId, new SlipAngleSourceEdit
            {
                Type = any ? SlipAngleTelemetryType.AngleOnly : SlipAngleTelemetryType.None,
                Angle = p,
                KeepSurface = true,
            }, restoreCompiled);
        }

        /// <summary>
        /// The settings page's Apply for one game (owner, 2026-11-23), from its Telemetry Type / Has Surface editor:
        /// <list type="bullet">
        /// <item><see cref="SlipAngleTelemetryType.None"/>: the game's slip-angle source is taken out.</item>
        /// <item><see cref="SlipAngleTelemetryType.AngleOnly"/>: <c>slipAngle</c> = the four angle properties, read as
        /// they are (a row that was a tangent - LFS - stays one); <c>unit</c> = the Source Unit (none for Auto Detect).</item>
        /// <item><see cref="SlipAngleTelemetryType.LateralLongitudinal"/>: <c>slipAngle</c> = the lateral velocities,
        /// <c>longitudinal</c> = the longitudinal ones, <c>transform</c> = atan2PatchVelocity, no <c>unit</c> (always
        /// radians).</item>
        /// <item>Has Surface: <c>surface</c> = the four properties (the row's <c>surfaceValues</c> are kept); unchecked,
        /// both are taken out.</item>
        /// </list>
        /// Every group shown must have all four properties, or nothing is written (<see cref="SlipAngleSourceSaveResult.Incomplete"/>).
        /// Then merged as <see cref="Place"/> says.
        /// </summary>
        public SlipAngleSourceSaveResult Save(string gameId, SlipAngleSourceEdit edit, bool restoreCompiled = false)
        {
            if (string.IsNullOrEmpty(gameId) || edit == null) return SlipAngleSourceSaveResult.Unavailable;
            string[] angle = Four(edit.Angle), lat = Four(edit.Lateral), lng = Four(edit.Longitudinal), surface = Four(edit.Surface);
            if (!restoreCompiled)
            {
                bool complete =
                    (edit.Type != SlipAngleTelemetryType.AngleOnly || Filled(angle))
                    && (edit.Type != SlipAngleTelemetryType.LateralLongitudinal || (Filled(lat) && Filled(lng)))
                    && (edit.KeepSurface || !edit.HasSurface || Filled(surface));
                if (!complete) return SlipAngleSourceSaveResult.Incomplete;
            }

            lock (_gate)
            {
                SlipAngleSourceDocument doc;
                try
                {
                    doc = SlipAngleSourceDocument.FromJson(File.Exists(Path) ? File.ReadAllText(Path) : SlipAngleSourceCompiled.Text);
                    SlipAngleSourceCatalog.FromDocument(doc);   // a file that is not valid as it stands is never rewritten
                }
                catch (FormatException e) { MarkBroken(e.Message); return SlipAngleSourceSaveResult.Unavailable; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Fail(e); return SlipAngleSourceSaveResult.Failed; }

                int index = SlipAngleSourceCatalog.ResolveIndex(doc.Games, gameId);
                SlipAngleSourceGame current = index >= 0 ? doc.Games[index] : null;
                SlipAngleSourceGame desired;
                if (restoreCompiled)
                {
                    SlipAngleSourceGame compiled = Compiled(gameId);
                    if (compiled == null) return SlipAngleSourceSaveResult.Unavailable;
                    desired = compiled.SettingsOnly();
                }
                else desired = Desired(current, edit, angle, lat, lng, surface);
                if (SlipAngleSourceGame.SameSettings(current, desired)) return SlipAngleSourceSaveResult.Unchanged;
                SlipAngleSourceSaveResult result = current != null && current.HasSlipAngle && !desired.HasSlipAngle
                    ? SlipAngleSourceSaveResult.Removed : SlipAngleSourceSaveResult.Saved;

                Place(doc.Games, gameId, desired);

                string text = doc.ToJson();
                SlipAngleSourceCatalog catalog;
                try { catalog = SlipAngleSourceCatalog.Parse(text); }
                catch (FormatException e) { Fail(e); return SlipAngleSourceSaveResult.Failed; }
                try { AtomicFile.WriteAllText(Path, text); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Fail(e); return SlipAngleSourceSaveResult.Failed; }
                RememberFile();
                UseLocked(catalog, broken: null);
                return result;
            }
        }

        private static SlipAngleSourceGame Desired(SlipAngleSourceGame current, SlipAngleSourceEdit edit,
            string[] angle, string[] lat, string[] lng, string[] surface)
        {
            SlipAngleSourceGame d = current?.SettingsOnly() ?? new SlipAngleSourceGame();
            bool wasAtan2 = d.HasSlipAngle && Same(d.Transform, "atan2PatchVelocity");
            switch (edit.Type)
            {
                case SlipAngleTelemetryType.AngleOnly:
                    if (wasAtan2 || !d.HasSlipAngle) { d.Unit = d.HasSlipAngle ? null : d.Unit; d.Provisional = null; }
                    if (!Same(d.Transform, "atanOfTangent")) d.Transform = null;   // LFS's tangent stays a tangent
                    // The Source Unit (Auto Detect = no field); the angle-only overload keeps the row's own.
                    if (!edit.KeepSurface) d.Unit = SlipAngleSourceEdit.NormalUnit(edit.Unit);
                    d.SlipAngle = angle.ToList();
                    d.Longitudinal = null;
                    break;
                case SlipAngleTelemetryType.LateralLongitudinal:
                    if (!wasAtan2) d.Provisional = null;
                    d.SlipAngle = lat.ToList();
                    d.Longitudinal = lng.ToList();
                    d.Transform = "atan2PatchVelocity";
                    d.Unit = null;   // atan2 gives radians - always; the file has no unit for it
                    break;
                default:
                    d.SlipAngle = null;
                    d.Unit = null;
                    d.Transform = null;
                    d.Longitudinal = null;
                    d.Provisional = null;
                    break;
            }
            if (!edit.KeepSurface)
            {
                if (edit.HasSurface) d.Surface = surface.ToList();
                else { d.Surface = null; d.SurfaceValues = null; }
                d.VerticalAcceleration = edit.HasVerticalAcceleration ? true : (bool?)null;
            }
            return d;
        }

        private static string[] Four(IReadOnlyList<string> paths)
        {
            var p = new string[4];
            for (int i = 0; i < 4; i++) p[i] = paths != null && i < paths.Count ? (paths[i] ?? string.Empty).Trim() : string.Empty;
            return p;
        }

        private static bool Filled(string[] p) => p.All(x => x.Length > 0);

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Gives <paramref name="gameId"/> the settings <paramref name="desired"/>, merging (see <see cref="Save"/>).</summary>
        internal static void Place(List<SlipAngleSourceGame> games, string gameId, SlipAngleSourceGame desired)
        {
            // 1. Out of the row that names it exactly.
            for (int i = games.Count - 1; i >= 0; i--)
            {
                SlipAngleGameIdPattern pattern = SlipAngleGameIdPattern.Parse(games[i].Id);
                if (!pattern.IsExact(gameId)) continue;
                string rest = pattern.Without(gameId);
                if (rest.Length == 0) games.RemoveAt(i);
                else games[i].Id = rest;
            }

            // 2. No source, and no wildcard / regex row covers it: nothing to write (no row means no source).
            if (SlipAngleSourceGame.SameSettings(desired, null) && SlipAngleSourceCatalog.ResolveIndex(games, gameId) < 0)
                return;

            // 3. THE GAME IS ALWAYS NAMED (owner, 2026-11-24): it joins the first row with exactly these settings - even
            // when a wildcard / regex of another row already covers it with them - else it gets a row of its own.
            int same = games.FindIndex(g => SlipAngleSourceGame.SameSettings(g, desired));
            if (same >= 0)
            {
                games[same].Id = SlipAngleGameIdPattern.Parse(games[same].Id).With(gameId);
                return;
            }
            SlipAngleSourceGame row = desired.SettingsOnly();
            row.Id = gameId;
            games.Add(row);
        }

        private void LoadLocked()
        {
            string text;
            try
            {
                if (!File.Exists(Path))
                {
                    AtomicFile.WriteAllText(Path, SlipAngleSourceCompiled.Text);
                    _logWarning?.Invoke("QAdvanceFeedback: created " + FileName + " from the plugin's own copy.");
                }
                text = File.ReadAllText(Path);
                RememberFile();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                Fail(e);
                UseLocked(SlipAngleSourceCompiled.Catalog, broken: null);
                return;
            }

            try { UseLocked(SlipAngleSourceCatalog.Parse(text), broken: null); }
            catch (FormatException e) { UseLocked(SlipAngleSourceCompiled.Catalog, broken: e.Message); }
        }

        private void UseLocked(SlipAngleSourceCatalog catalog, string broken)
        {
            bool wasBroken = IsBroken;
            IsBroken = broken != null;
            BrokenReason = broken ?? string.Empty;
            if (IsBroken) MarkBroken(broken);
            else if (wasBroken) HealthRegistry.Clear(HealthSubsystems.SlipAngleSourceFile);
            _active = catalog;
            _use(catalog);
            Changed?.Invoke();
        }

        private void MarkBroken(string reason)
        {
            IsBroken = true;
            BrokenReason = reason ?? string.Empty;
            _logWarning?.Invoke("QAdvanceFeedback: " + FileName + " is not valid (" + reason + "); using the plugin's own copy until it is fixed.");
            HealthRegistry.Report(HealthSubsystems.SlipAngleSourceFile, HealthSeverity.Degraded, "Health.Impact.SlipAngleSourceFile", reason);
        }

        private void Fail(Exception e)
        {
            _logWarning?.Invoke("QAdvanceFeedback: " + FileName + " - " + e.Message);
            HealthRegistry.Report(HealthSubsystems.SlipAngleSourceFile, HealthSeverity.Degraded, "Health.Impact.SlipAngleSourceFile", e.ToString());
        }

        private void RememberFile()
        {
            try
            {
                var info = new FileInfo(Path);
                _loadedWriteUtc = info.Exists ? info.LastWriteTimeUtc : default(DateTime);
                _loadedLength = info.Exists ? info.Length : -1;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _loadedLength = -1;
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The settings page's draft / baseline / live model (docs\full-mvvm-plan.md §3.1). <c>Live</c> is the
    /// plugin's settings object; the <see cref="Settings"/> draft is a deep copy the page edits;
    /// <c>Baseline</c> is the live state the draft was last synchronised with. They are compared and merged
    /// at JSON-leaf level, so no per-field table can fall out of date when a setting is added:
    /// <list type="bullet">
    /// <item><see cref="IsDirty"/>: any leaf where Draft differs from Baseline.</item>
    /// <item><see cref="AdoptExternal"/>: every leaf where Live differs from Baseline goes into Draft and
    /// Baseline - the ShakeIt side wins, even over an unapplied page edit of the same leaf (owner
    /// decision); every other unapplied edit stays.</item>
    /// <item><see cref="Apply"/>: only the leaves the page edited are written into Live, in place (the
    /// plugin holds that reference), so Apply can never revert a change made elsewhere.</item>
    /// </list>
    /// UI thread only. Pure (no WPF).
    /// </summary>
    public sealed class SettingsDraft
    {
        private readonly Func<QAdvanceFeedbackSettings> _live;
        private JToken _baseline;
        private bool _isDirty;

        /// <param name="live">The plugin's live settings; may return null (the plugin is not running) -
        /// the draft then holds shipped defaults and adopts the live object once it appears.</param>
        public SettingsDraft(Func<QAdvanceFeedbackSettings> live)
        {
            _live = live ?? throw new ArgumentNullException(nameof(live));
            QAdvanceFeedbackSettings current = _live();
            Settings = current != null ? SettingsJsonLeaves.DeepCopy(current) : QAdvanceFeedbackSettings.CreateDefault();
            _baseline = SettingsJsonLeaves.ToJson(Settings);
        }

        /// <summary>The draft the page edits. The same instance for the draft's whole life (adopt and
        /// reset mutate it in place), but read it each time rather than caching parts of it: a sub-object
        /// can be replaced when a whole subtree changed.</summary>
        public QAdvanceFeedbackSettings Settings { get; }

        public bool IsDirty => _isDirty;

        public event EventHandler DirtyChanged;

        /// <summary>After <see cref="AdoptExternal"/> or <see cref="ReplaceDraft"/> changed the draft.</summary>
        public event EventHandler<DraftReloadedEventArgs> Reloaded;

        /// <summary>A view model wrote the draft: recompute <see cref="IsDirty"/>.</summary>
        public void MarkEdited() => RecomputeDirty(SettingsJsonLeaves.ToJson(Settings));

        /// <summary>Copies every leaf the live settings changed since the last sync into the draft and the
        /// baseline. Returns the adopted paths (empty when nothing changed or there is no live object).</summary>
        public IReadOnlyList<string> AdoptExternal()
        {
            QAdvanceFeedbackSettings live = _live();
            if (live == null) return Array.Empty<string>();

            JToken liveJson = SettingsJsonLeaves.ToJson(live);
            List<SettingsJsonLeaves.LeafChange> changes = SettingsJsonLeaves.Diff(_baseline, liveJson);
            if (changes.Count == 0) return Array.Empty<string>();

            SettingsJsonLeaves.Write(Settings, changes, liveJson);
            _baseline = liveJson;
            return Reload(changes);
        }

        /// <summary>Writes every leaf the page edited into the live settings, in place; the baseline then
        /// equals the draft. Leaves changed live but not edited here are left as they are (a later
        /// <see cref="AdoptExternal"/> brings them in). Returns the written paths.</summary>
        public IReadOnlyList<string> Apply()
        {
            QAdvanceFeedbackSettings live = _live();
            if (live == null) return Array.Empty<string>();

            JToken draftJson = SettingsJsonLeaves.ToJson(Settings);
            List<SettingsJsonLeaves.LeafChange> changes = SettingsJsonLeaves.Diff(_baseline, draftJson);
            if (changes.Count > 0) SettingsJsonLeaves.Write(live, changes, draftJson);
            _baseline = draftJson;
            RecomputeDirty(draftJson);
            return Paths(changes);
        }

        /// <summary>Makes the draft equal <paramref name="values"/> (copied, the caller's object is not
        /// kept), in place. Pending until applied, like any edit.</summary>
        public void ReplaceDraft(QAdvanceFeedbackSettings values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            JToken target = SettingsJsonLeaves.ToJson(values);
            List<SettingsJsonLeaves.LeafChange> changes = SettingsJsonLeaves.Diff(SettingsJsonLeaves.ToJson(Settings), target);
            if (changes.Count == 0) return;
            SettingsJsonLeaves.Write(Settings, changes, target);
            Reload(changes);
        }

        private IReadOnlyList<string> Reload(List<SettingsJsonLeaves.LeafChange> changes)
        {
            IReadOnlyList<string> paths = Paths(changes);
            RecomputeDirty(SettingsJsonLeaves.ToJson(Settings));
            Reloaded?.Invoke(this, new DraftReloadedEventArgs(paths));
            return paths;
        }

        private void RecomputeDirty(JToken draftJson)
        {
            bool dirty = !JToken.DeepEquals(_baseline, draftJson);
            if (dirty == _isDirty) return;
            _isDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }

        private static IReadOnlyList<string> Paths(List<SettingsJsonLeaves.LeafChange> changes)
        {
            var paths = new List<string>(changes.Count);
            foreach (SettingsJsonLeaves.LeafChange c in changes) paths.Add(c.Path);
            return paths;
        }
    }

    /// <summary>The draft was reloaded; <see cref="Paths"/> are the changed JSON leaf paths, dotted from
    /// the settings root (e.g. <c>Lock.SourceMode</c>, <c>Lock.KeyDataPoints.Values.iracing|car</c>).</summary>
    public sealed class DraftReloadedEventArgs : EventArgs
    {
        public DraftReloadedEventArgs(IReadOnlyList<string> paths) { Paths = paths ?? Array.Empty<string>(); }

        public IReadOnlyList<string> Paths { get; }

        /// <summary>Whether any changed path is <paramref name="path"/>, lies under it, or is an ancestor
        /// of it (a whole subtree replaced), for each of the given paths.</summary>
        public bool Touches(params string[] paths)
        {
            foreach (string changed in Paths)
                foreach (string p in paths)
                    if (Related(changed, p)) return true;
            return false;
        }

        private static bool Related(string a, string b) =>
            string.Equals(a, b, StringComparison.Ordinal)
            || (a.Length > b.Length && a.StartsWith(b, StringComparison.Ordinal) && a[b.Length] == '.')
            || (b.Length > a.Length && b.StartsWith(a, StringComparison.Ordinal) && b[a.Length] == '.');
    }

    /// <summary>
    /// The JSON-leaf diff and in-place write behind <see cref="SettingsDraft"/>. A leaf is a JSON value or
    /// array (arrays are compared and copied whole); objects (POCOs and dictionaries) recurse; a key on one
    /// side only is a leaf change (added or removed). Writes go into the existing .NET object graph through
    /// Newtonsoft's own contracts, so objects keep their identity (and their non-serialised runtime state,
    /// and a dictionary its comparer); only the changed leaf is replaced.
    /// </summary>
    internal static class SettingsJsonLeaves
    {
        // Newtonsoft's defaults (as ConfigStore uses them), except that get-only VALUE properties are left
        // out: they are runtime reads (e.g. GForceSettings.CurrentLearned*MaxG reading the live learners), not
        // settings - the draft can never hold them, so they would read as a change forever (final review C1).
        // Get-only properties holding objects/dictionaries stay: Newtonsoft populates those in place.
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new SettingsOnlyContractResolver(),
        });

        private sealed class SettingsOnlyContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override IList<Newtonsoft.Json.Serialization.JsonProperty> CreateProperties(
                Type type, MemberSerialization memberSerialization)
            {
                IList<Newtonsoft.Json.Serialization.JsonProperty> all = base.CreateProperties(type, memberSerialization);
                var kept = new List<Newtonsoft.Json.Serialization.JsonProperty>();
                foreach (var p in all) if (p.Writable || !IsValueLeaf(p.PropertyType)) kept.Add(p);
                return kept;
            }

            private static bool IsValueLeaf(Type t)
            {
                Type u = Nullable.GetUnderlyingType(t) ?? t;
                return u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(decimal)
                    || u == typeof(DateTime) || u == typeof(DateTimeOffset) || u == typeof(TimeSpan) || u == typeof(Guid);
            }
        }

        internal sealed class LeafChange
        {
            public LeafChange(IReadOnlyList<string> segments, bool removed)
            {
                Segments = segments;
                Removed = removed;
                Path = string.Join(".", segments);
            }

            public IReadOnlyList<string> Segments { get; }
            /// <summary>The key exists only in the "from" side: remove it.</summary>
            public bool Removed { get; }
            public string Path { get; }
        }

        /// <summary>Serialises <paramref name="o"/> the way <c>JsonConvert.SerializeObject</c> (and
        /// ConfigStore) does. A live object the data thread is writing (a dictionary mid-insert) can throw;
        /// it is retried a few times before the error reaches the caller.</summary>
        public static JToken ToJson(object o)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return o == null ? JValue.CreateNull() : JToken.FromObject(o, Serializer); }
                catch (InvalidOperationException) when (attempt < 3) { }
            }
        }

        /// <summary>An exact copy: a fresh instance (as its own constructor builds it) with every leaf that
        /// differs from <paramref name="o"/> written in. Unlike a plain deserialise, an initialiser-created
        /// list is replaced rather than appended to, and an initialiser-created dictionary keeps its comparer.</summary>
        public static T DeepCopy<T>(T o) where T : class => o == null ? null : (T)Materialize(ToJson(o), o.GetType());

        /// <summary>A new object of <paramref name="type"/> equal to <paramref name="value"/>: objects and
        /// dictionaries are created by their default constructor and filled leaf by leaf; anything else
        /// (values, arrays, types without a default constructor) is deserialised directly.</summary>
        private static object Materialize(JToken value, Type type)
        {
            if (value is JObject obj
                && Serializer.ContractResolver.ResolveContract(type) is JsonContainerContract cc
                && (cc is JsonObjectContract || cc is JsonDictionaryContract)
                && cc.DefaultCreator != null && !cc.DefaultCreatorNonPublic)
            {
                object instance = cc.DefaultCreator();
                Write(instance, Diff(ToJson(instance), obj), obj);
                return instance;
            }
            return value.ToObject(type, Serializer);
        }

        /// <summary>The leaf changes that turn <paramref name="from"/> into <paramref name="to"/>, in
        /// <paramref name="to"/>'s document order (removals of an object's keys before its sets).</summary>
        public static List<LeafChange> Diff(JToken from, JToken to)
        {
            var result = new List<LeafChange>();
            Diff(from, to, new List<string>(), result);
            return result;
        }

        private static void Diff(JToken from, JToken to, List<string> path, List<LeafChange> result)
        {
            if (from is JObject a && to is JObject b)
            {
                foreach (JProperty p in a.Properties())
                    if (b.Property(p.Name, StringComparison.Ordinal) == null)
                        result.Add(new LeafChange(With(path, p.Name), removed: true));
                foreach (JProperty p in b.Properties())
                {
                    JProperty old = a.Property(p.Name, StringComparison.Ordinal);
                    if (old == null) result.Add(new LeafChange(With(path, p.Name), removed: false));
                    else
                    {
                        path.Add(p.Name);
                        Diff(old.Value, p.Value, path, result);
                        path.RemoveAt(path.Count - 1);
                    }
                }
                return;
            }
            if (!JToken.DeepEquals(from, to)) result.Add(new LeafChange(path.ToArray(), removed: false));
        }

        private static string[] With(List<string> path, string name)
        {
            var s = new string[path.Count + 1];
            path.CopyTo(s);
            s[path.Count] = name;
            return s;
        }

        /// <summary>Applies <paramref name="changes"/> to <paramref name="root"/> in place, taking each new
        /// value from <paramref name="source"/> (the JSON the changes lead to). Where the target lacks a
        /// parent the source has, the whole subtree is written at the deepest parent that exists.</summary>
        public static void Write(object root, IEnumerable<LeafChange> changes, JToken source)
        {
            foreach (LeafChange change in changes) Write(root, change, source);
        }

        private static void Write(object root, LeafChange change, JToken source)
        {
            IReadOnlyList<string> segs = change.Segments;
            if (segs.Count == 0) return;   // the root itself is never replaced

            object parent = root;
            for (int i = 0; i < segs.Count - 1; i++)
            {
                if (!TryGetChild(parent, segs[i], out object child) || child == null)
                {
                    if (!change.Removed) SetChild(parent, segs[i], At(source, segs, i + 1));
                    return;
                }
                parent = child;
            }

            string last = segs[segs.Count - 1];
            if (change.Removed) RemoveChild(parent, last);
            else SetChild(parent, last, At(source, segs, segs.Count));
        }

        private static JToken At(JToken source, IReadOnlyList<string> segs, int count)
        {
            JToken t = source;
            for (int i = 0; i < count && t != null; i++) t = (t as JObject)?.Property(segs[i], StringComparison.Ordinal)?.Value;
            return t ?? JValue.CreateNull();
        }

        private static bool TryGetChild(object parent, string name, out object child)
        {
            child = null;
            JsonContract contract = Serializer.ContractResolver.ResolveContract(parent.GetType());
            if (contract is JsonObjectContract oc)
            {
                JsonProperty p = oc.Properties.GetClosestMatchProperty(name);
                if (p == null || !p.Readable || p.Ignored) return false;
                child = p.ValueProvider.GetValue(parent);
                return true;
            }
            if (contract is JsonDictionaryContract dc && parent is IDictionary d)
            {
                object key = Key(dc, name);
                if (!d.Contains(key)) return false;
                child = d[key];
                return true;
            }
            return false;
        }

        private static void SetChild(object parent, string name, JToken value)
        {
            JsonContract contract = Serializer.ContractResolver.ResolveContract(parent.GetType());
            if (contract is JsonObjectContract oc)
            {
                JsonProperty p = oc.Properties.GetClosestMatchProperty(name);
                if (p == null || p.Ignored) return;
                if (p.Writable)
                    p.ValueProvider.SetValue(parent, Materialize(value, p.PropertyType));
                else if (p.Readable && value is JObject obj && p.ValueProvider.GetValue(parent) is object existing)
                    using (JsonReader r = obj.CreateReader()) Serializer.Populate(r, existing);   // get-only object: fill in place
                return;
            }
            if (contract is JsonDictionaryContract dc && parent is IDictionary d)
                d[Key(dc, name)] = Materialize(value, dc.DictionaryValueType ?? typeof(object));
        }

        private static void RemoveChild(object parent, string name)
        {
            // POCO properties are always serialised, so only a dictionary key can disappear.
            if (Serializer.ContractResolver.ResolveContract(parent.GetType()) is JsonDictionaryContract dc && parent is IDictionary d)
                d.Remove(Key(dc, name));
        }

        private static object Key(JsonDictionaryContract dc, string name)
        {
            Type t = dc.DictionaryKeyType ?? typeof(string);
            if (t == typeof(string) || t == typeof(object)) return name;
            if (t.IsEnum) return Enum.Parse(t, name, ignoreCase: false);
            return Convert.ChangeType(name, t, CultureInfo.InvariantCulture);
        }
    }
}

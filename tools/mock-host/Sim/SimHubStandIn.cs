using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;
using GameReaderCommon;
using SimHub.Plugins;

namespace MockHost.Sim
{
    /// <summary>
    /// SimHub's own objects, made usable without SimHub.
    /// <para/>
    /// PluginManager's real constructor loads WoteverCommon.dll, which only a SimHub install has, so it is created
    /// WITHOUT running the constructor and its collection fields are filled in by hand. The plugin only uses the
    /// property table (AttachDelegate / GetPropertyValue), GetCommonStoragePath (a plain relative path:
    /// PluginsData\Common\file) and GameManager (null-guarded), so that is all a stand-in needs.
    /// </summary>
    internal static class SimHubStandIn
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static PluginManager CreatePluginManager(IGameManager gameManager)
        {
            var pm = (PluginManager)FormatterServices.GetUninitializedObject(typeof(PluginManager));
            foreach (FieldInfo field in typeof(PluginManager).GetFields(AnyInstance))
            {
                Type t;
                // A field whose type lives in a SimHub-only DLL (WoteverCommon...) cannot even be inspected: skip it.
                try { t = field.FieldType; if (field.GetValue(pm) != null) continue; }
                catch (Exception) { continue; }
                // Only the BCL's own collections (and lock objects): a SimHub type could load a SimHub-only DLL.
                bool bcl = t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal);
                if (!bcl || t.IsAbstract || t.IsInterface) continue;
                if (t == typeof(object)) { field.SetValue(pm, new object()); continue; }
                if (!typeof(IEnumerable).IsAssignableFrom(t) || t == typeof(string)) continue;
                ConstructorInfo comparerCtor = t.GetConstructor(new[] { typeof(StringComparer) })
                    ?? t.GetConstructor(new[] { typeof(System.Collections.Generic.IEqualityComparer<string>) });
                object value = comparerCtor != null
                    ? comparerCtor.Invoke(new object[] { StringComparer.OrdinalIgnoreCase })
                    : (t.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(t) : null);
                if (value != null) field.SetValue(pm, value);
            }
            SetField(pm, "_manager", gameManager);
            // IPluginExtensions.AttachDelegate (how a plugin publishes its properties) goes through this static.
            typeof(PluginManager).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, pm);
            return pm;
        }

        /// <summary>Adds (or replaces) a SimHub property whose value is read from <paramref name="value"/> on every
        /// GetPropertyValue, as a plugin's AttachDelegate property is. The name is the full name
        /// ("DataCorePlugin.GameRawData.X"); SimHub keys the table by its lower-case form.</summary>
        public static void SetProperty(PluginManager pm, string fullName, Func<object> value)
        {
            var table = (ConcurrentDictionary<string, PropertyEntry>)GetField(pm, "GeneratedProperties");
            var entry = new PropertyEntry(fullName, typeof(object), value, "Mock host property");
            SetProperty(entry, "IsDelegate", true);
            table[fullName.ToLowerInvariant()] = entry;
        }

        public static void RemoveProperty(PluginManager pm, string fullName)
        {
            var table = (ConcurrentDictionary<string, PropertyEntry>)GetField(pm, "GeneratedProperties");
            table.TryRemove(fullName.ToLowerInvariant(), out _);
        }

        public static int PropertyCount(PluginManager pm) =>
            ((ConcurrentDictionary<string, PropertyEntry>)GetField(pm, "GeneratedProperties")).Count;

        /// <summary>Sets a property whatever its setter's visibility (SimHub's data classes use internal setters).</summary>
        public static void SetProperty(object target, string name, object value)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
                MethodInfo setter = p?.GetSetMethod(true);
                if (setter != null) { setter.Invoke(target, new[] { value }); return; }
            }
            throw new MissingMemberException(target.GetType().Name, name);
        }

        public static void SetField(object target, string name, object value) => FindField(target.GetType(), name).SetValue(target, value);

        public static object GetField(object target, string name) => FindField(target.GetType(), name).GetValue(target);

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            throw new MissingFieldException(type.Name, name);
        }
    }
}

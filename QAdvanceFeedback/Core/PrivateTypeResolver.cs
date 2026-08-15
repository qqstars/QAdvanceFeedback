using System;
using System.Reflection;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The pure, SimHub-free reflection core of how this plugin resolves a withheld
    /// <c>QAdvanceFeedback\Private\*.cs</c> implementation at runtime: look for a type BY NAME in a
    /// given assembly, and only ever return it as <typeparamref name="T"/> if it both exists and
    /// actually implements/extends <typeparamref name="T"/>. Never a compile-time reference to the
    /// concrete Private type (there cannot be one - see <c>AlgorithmFactory</c>'s own remarks on why
    /// that is the whole point), so the assembly that calls this still compiles whether or not the
    /// named type happens to be present in it this build.
    /// <para/>
    /// Every failure mode - the name does not resolve to any type, the type is abstract/otherwise not
    /// constructible, it exists but does not implement <typeparamref name="T"/>, or construction
    /// itself throws (e.g. a Private replacement with no public parameterless constructor) - is
    /// treated identically: fall back to <paramref name="fallback"/>. A missing or malformed Private
    /// implementation must degrade the plugin, never crash it.
    /// </summary>
    public static class PrivateTypeResolver
    {
        /// <summary>
        /// Looks for <paramref name="typeName"/> (a full type name, e.g.
        /// <c>"QAdvanceFeedback.Core.LegacyWheelLockSlipEngine"</c>) in <paramref name="assembly"/>. If
        /// found and it is a concrete type assignable to <typeparamref name="T"/>, constructs and
        /// returns one instance via its public parameterless constructor. Otherwise invokes
        /// <paramref name="fallback"/> and returns its result.
        /// </summary>
        public static T CreateOrFallback<T>(Assembly assembly, string typeName, Func<T> fallback) where T : class
        {
            if (fallback == null) throw new ArgumentNullException(nameof(fallback));

            try
            {
                Type type = assembly?.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (type != null && !type.IsAbstract && !type.IsInterface && typeof(T).IsAssignableFrom(type))
                {
                    if (Activator.CreateInstance(type) is T instance)
                    {
                        return instance;
                    }
                }
            }
            catch
            {
                // Any reflection/activation failure (no parameterless constructor, a static
                // initialiser that throws, a partial trust restriction, ...) - fall through to the
                // safe fallback below rather than letting a broken/half-supplied Private
                // implementation crash plugin composition.
            }

            return fallback();
        }
    }
}

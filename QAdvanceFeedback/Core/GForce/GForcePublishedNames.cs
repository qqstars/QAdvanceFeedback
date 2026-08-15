using System.Collections.Generic;

namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// The exact, verbatim eight property-name suffixes the G-force channels publish. Deliberately
    /// kept in its OWN file under Core/GForce rather than added to the Layer 1-3
    /// <c>QAdvanceFeedback.Core.PublishedPropertyNames</c> class - this task's file ownership is
    /// scoped to Core/GForce/** and Settings/GForceSettings.cs only, and that file belongs to nobody
    /// in particular but is outside this scope, so it is not touched.
    /// <para/>
    /// SimHub's own PluginManager.GetName(name, pluginType) is hard-coded as
    /// <c>pluginType.Name + "." + name</c> (decompiled and confirmed for Layers 1-3, see
    /// docs\layer123-report.md) - so the FULL registered name is "QAdvanceFeedback." + one of
    /// <see cref="AllNames"/>, e.g. "QAdvanceFeedback.GForce.Bottom.FrontLeft". Neither this class nor
    /// the plugin class itself repeats "QAdvanceFeedback." - GetName supplies it exactly once.
    /// <para/>
    /// Spelled "Bottom" (correct English), not  the "Buttom" spelling in the requirement text - per
    /// the standing rule that published names and identifiers use correct spelling regardless of how
    /// guidance text spelled them.
    /// </summary>
    public static class GForcePublishedNames
    {
        public const string BottomPrefix = "GForce.Bottom.";
        public const string BackPrefix = "GForce.Back.";

        public static readonly string[] BottomTargets = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
        public static readonly string[] BackTargets = { "LowLeft", "LowRight", "TopLeft", "TopRight" };

        /// <summary>All 8 names this model publishes, Bottom first then Back, each in the order
        /// above - matching <see cref="GForceOutput"/>'s property order.</summary>
        public static IEnumerable<string> AllNames()
        {
            foreach (string t in BottomTargets) yield return BottomPrefix + t;
            foreach (string t in BackTargets) yield return BackPrefix + t;
        }
    }
}

using System.Collections.Generic;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The exact, verbatim set of property-name suffixes this plugin publishes for Layers 1-3 - pure
    /// and SimHub-free so the actual name list is unit-tested directly, independent of the SimHub
    /// AttachDelegate plumbing that consumes it (see the main project's QAdvanceFeedback.cs).
    /// <para/>
    /// SimHub's own PluginManager.GetName(name, pluginType) is hard-coded as
    /// <c>pluginType.Name + "." + name</c> (confirmed by decompiling SimHub.Plugins.dll) - so the
    /// FULL registered name is the plugin class's own name ("QAdvanceFeedback") plus "." plus one of
    /// <see cref="AllNames"/>, e.g. "QAdvanceFeedback.WheelLock.Raw.All". Neither this class nor the
    /// main plugin class repeats "QAdvanceFeedback." themselves - GetName supplies it exactly once.
    /// </summary>
    public static class PublishedPropertyNames
    {
        public static readonly string[] Targets =
        {
            "FrontLeft", "FrontRight", "RearLeft", "RearRight",
            "Front", "Rear", "Left", "Right", "All"
        };

        public const string LockPrefix = "WheelLock.Raw.";
        public const string SlipPrefix = "WheelSlip.Raw.";

        /// <summary>All 18 names this plugin publishes (9 targets x 2 channels), Lock first then
        /// Slip, each in <see cref="Targets"/> order.</summary>
        public static IEnumerable<string> AllNames()
        {
            foreach (string t in Targets) yield return LockPrefix + t;
            foreach (string t in Targets) yield return SlipPrefix + t;
        }
    }
}

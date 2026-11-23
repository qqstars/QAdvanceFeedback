using QAdvanceFeedback.Core.MotorsExport;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// The learning identity of our own internal Wheel Lock / Wheel Slip source (Plugin Internal = this
    /// plugin's Layer 3 Raw properties as four Plain rows) - the key that ALWAYS learns, selected or not
    /// (docs\internal-source-learning-design.md).
    /// <para/>
    /// Core cannot reach <c>Settings.RawSourceFallback</c> (it sits a layer above), so the same string is
    /// built here from the same parts. A test pins the two as equal; if the published property prefix
    /// ever changes, <c>Settings.DefaultWheelSources.PluginPublishedPrefix</c> and
    /// <see cref="PluginPublishedPrefix"/> must change together.
    /// </summary>
    public static class PluginInternalSourceIdentity
    {
        /// <summary>See <c>Settings.DefaultWheelSources.PluginPublishedPrefix</c>.</summary>
        public const string PluginPublishedPrefix = "QAdvanceFeedback.";

        private static readonly string LockIdentity = Build(PublishedPropertyNames.LockPrefix);
        private static readonly string SlipIdentity = Build(PublishedPropertyNames.SlipPrefix);

        /// <summary>The internal source's identity for one channel.</summary>
        public static string For(bool isLockChannel) => isLockChannel ? LockIdentity : SlipIdentity;

        private static string Build(string channelPrefix)
        {
            string p = PluginPublishedPrefix + channelPrefix;
            return SourceIdentity.Compute(
                p + MotorsExportPropertyNames.FrontLeft, "Plain",
                p + MotorsExportPropertyNames.FrontRight, "Plain",
                p + MotorsExportPropertyNames.RearLeft, "Plain",
                p + MotorsExportPropertyNames.RearRight, "Plain");
        }
    }
}

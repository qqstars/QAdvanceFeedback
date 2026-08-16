using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// One-time migration for <see cref="KeyedGripLearner"/>'s persisted keys: a runtime file written
    /// BEFORE source-keyed learning existed has keys shaped "gameId|#|carId" (two segments) -
    /// this class upgrades every such key to "gameId|#|carId|#|__source_unknown__" (three segments,
    /// <see cref="KeyedGripLearner.LegacySourcelessSourceIdentity"/>) so the driver's already-learned
    /// profile is NEVER silently discarded, while still fitting the new three-part key shape
    /// <see cref="KeyedGripLearner.Find"/>/<see cref="KeyedGripLearner.GetOrCreate"/> now expect.
    /// <para/>
    /// A DRIVER UPGRADING MUST NOT LOSE A STINT'S WORTH OF LEARNING SILENTLY (this task's own explicit
    /// requirement): <see cref="KeyedGripLearner.GetOrCreate"/> seeds the FIRST source it sees for a
    /// given (game,car) from that same (game,car)'s migrated-sourceless profile, if one exists (exactly
    /// the same non-destructive "adopt once, for the very first new key" pattern this class already
    /// uses for the even-older flat <see cref="KeyedGripLearner.LegacyImportKey"/>) - so a driver who
    /// upgrades and keeps using the SAME source they always used (the overwhelmingly common case) sees
    /// their prior learning carried over immediately, with no gap; a driver who happens to configure a
    /// GENUINELY DIFFERENT source right after upgrading starts that new source's own session fresh,
    /// which is the entire point of source-keying in the first place.
    /// <para/>
    /// Call this on whatever raw dictionary <c>RuntimeStore.LoadLockLearners</c>/<c>LoadSlipLearners</c>
    /// returns, BEFORE handing it to <see cref="KeyedGripLearner.ImportAll"/> - see
    /// <c>QAdvanceFeedback.cs</c>'s own Init.
    /// </summary>
    public static class GripLearnerKeyMigration
    {
        private const string Separator = "|#|";

        /// <summary>
        /// Pads every key up to the current 4-segment (gameId,carId,sourceIdentity,surfaceBucket) shape,
        /// appending exactly the sentinel(s) needed for however far short it falls:
        /// <list type="bullet">
        /// <item>2 segments (pre-source-keying, pre-surface-keying) -&gt; appends BOTH
        /// <see cref="KeyedGripLearner.LegacySourcelessSourceIdentity"/> AND
        /// <see cref="KeyedGripLearner.LegacyPreSurfaceSplitBucket"/>.</item>
        /// <item>3 segments (source-keyed, pre-surface-keying) -&gt; appends ONLY
        /// <see cref="KeyedGripLearner.LegacyPreSurfaceSplitBucket"/>.</item>
        /// <item>4+ segments (already current, or written by a future format) -&gt; copied through
        /// completely unchanged.</item>
        /// </list>
        /// <see cref="KeyedGripLearner.LegacyImportKey"/> (the even-older flat/non-keyed format's own
        /// seed pseudo-key) is also copied through unchanged - it is not a (game,car) pair at all, and
        /// is handled entirely separately by <see cref="KeyedGripLearner.SeedLegacy"/>.
        /// <para/>
        /// Never throws, never drops data: a null/empty source returns an empty (not null) dictionary;
        /// a null key or null value in the source is skipped (defensively - should not happen from a
        /// well-formed persisted file, but this is migration code reading arbitrary disk content, so it
        /// must not crash on a hand-edited or corrupted entry).
        /// </summary>
        public static Dictionary<string, GripLearnerState> MigrateLegacyKeys(IDictionary<string, GripLearnerState> persisted)
        {
            var migrated = new Dictionary<string, GripLearnerState>(StringComparer.Ordinal);
            if (persisted == null) return migrated;

            foreach (KeyValuePair<string, GripLearnerState> pair in persisted)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;

                string key = pair.Key;
                if (key != KeyedGripLearner.LegacyImportKey)
                {
                    int segments = SegmentCount(key);
                    if (segments <= 2) key += Separator + KeyedGripLearner.LegacySourcelessSourceIdentity;
                    if (segments <= 3) key += Separator + KeyedGripLearner.LegacyPreSurfaceSplitBucket;
                }

                migrated[key] = pair.Value;
            }

            return migrated;
        }

        private static int SegmentCount(string key)
        {
            int count = 1;
            int index = 0;
            while ((index = key.IndexOf(Separator, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += Separator.Length;
            }
            return count;
        }
    }
}

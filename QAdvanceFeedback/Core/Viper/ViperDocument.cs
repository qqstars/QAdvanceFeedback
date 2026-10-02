using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core.Viper
{
    /// <summary>
    /// The persisted shape of <c>QAdvanceFeedback.Viper.json</c> - this plugin's own notes about
    /// viper4gh's CalcLngWheelSlip plugin, kept beside the config and parameters files in SimHub's
    /// common storage.
    /// <para/>
    /// WHY A SEPARATE FILE (the owner's own scoping). The supported-game list belongs to the VIPER
    /// PLUGIN, not to this one: it changes when viper4gh ships a release, on their schedule. Holding it
    /// in an editable file means a driver whose title the plugin has just started supporting can add it
    /// themselves, without waiting for a build of this plugin. Key data points are OURS and stay in the
    /// config file - the owner called that out explicitly, and it is the right line: this file describes
    /// somebody else's plugin, nothing about our own calibration.
    /// <para/>
    /// VERSIONED FROM THE START, for the same reason the runtime document is: the moment a second field
    /// is needed there has to be a way to tell an old file from a new one, and retrofitting a version
    /// onto an unversioned file means guessing.
    /// </summary>
    public sealed class ViperDocument
    {
        /// <summary>Bumped only when the SHAPE changes in a way a reader must know about. Adding a new
        /// optional field does not need a bump; changing or removing an existing one does.</summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// SimHub game IDs the Viper plugin computes for. Seeded from
        /// <see cref="ViperSupportedGames.Shipped"/> the first time the file is written, then owned by
        /// the driver - the Add/Remove button on the settings page edits exactly this.
        /// <para/>
        /// Case-insensitive in use (see <see cref="ViperSupportedGames"/>), but stored verbatim so the
        /// file stays readable and a hand edit keeps the casing SimHub itself uses.
        /// </summary>
        public List<string> SupportedGames { get; set; } = new List<string>();

        /// <summary>A copy carrying no shared references, so a caller can edit a working copy and only
        /// commit it on Apply - the same discipline the settings model uses.</summary>
        public ViperDocument Clone() => new ViperDocument
        {
            Version = Version,
            SupportedGames = SupportedGames == null
                ? new List<string>()
                : new List<string>(SupportedGames),
        };

        /// <summary>
        /// A document safe to use: never null, never a null list, no blanks, no duplicates.
        /// <para/>
        /// A file hand-edited into something odd - a null entry, the same game twice, a stray blank line
        /// - must not be able to make the plugin misbehave, and must not be silently rewritten either:
        /// this normalises what is READ, and only a genuine Apply writes anything back.
        /// </summary>
        public static ViperDocument Sanitise(ViperDocument document)
        {
            var source = document?.SupportedGames ?? new List<string>();
            return new ViperDocument
            {
                Version = document?.Version > 0 ? document.Version : CurrentVersion,
                SupportedGames = source
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }
    }
}

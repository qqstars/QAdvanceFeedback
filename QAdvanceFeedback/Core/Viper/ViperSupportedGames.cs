using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core.Viper
{
    /// <summary>
    /// Which SimHub games viper4gh's CalcLngWheelSlip plugin actually computes for, and the rules for
    /// asking that question.
    /// <para/>
    /// WHY THE LIST IS HARDCODED HERE RATHER THAN READ OUT OF THE PLUGIN. It is not data in that
    /// assembly - it is control flow. <c>ViperDataPlugin.DataUpdate</c> opens with a chain of
    /// <c>curGame == "..."</c> comparisons and returns early when none match, so ordinary reflection
    /// (types, methods, fields, properties) cannot see it at all.
    /// <para/>
    /// IL DISASSEMBLY WOULD WORK, AND WAS REJECTED. <c>MethodBody.GetILAsByteArray</c> plus
    /// <c>Module.ResolveString</c> is BCL-only and the names do survive as <c>ldstr</c> operands - that
    /// was checked, not assumed. But <c>DataUpdate</c> holds 192 string literals, of which these 23 are
    /// a minority among <c>DataCorePlugin.GameRawData.*</c> property paths and log messages, so
    /// separating them needs a heuristic ("no dots, no spaces"). That heuristic is correct for v1.5.2
    /// and would break silently on a restructure - the switch already compiles through a
    /// <c>ComputeStringHash</c> jump table, and one refactor to a HashSet would end recoverable literals
    /// entirely. A scraper whose failure mode is "quietly returns the wrong game list" is the wrong
    /// trade for a plugin whose whole diagnostic history has been silent failures.
    /// <para/>
    /// SO THE LIST IS WRITTEN DOWN, AND THEN HANDED TO THE DRIVER. It seeds
    /// <c>QAdvanceFeedback.Viper.json</c> once; after that the file is authoritative and the settings
    /// page can add or remove a game. A Viper release that adds a title therefore needs no build of
    /// this plugin. Being wrong here is also wrong in the SAFE direction: a game we have not heard of
    /// reads as unsupported, which falls back to Raw rather than to silence.
    /// <para/>
    /// TRANSCRIBED FROM v1.5.2, decompiled 2026-09-28. The F1 entries are two groups in the plugin's own
    /// code (<c>F1_18_22</c> and <c>F1_23_x</c>) and are flattened here.
    /// </summary>
    public static class ViperSupportedGames
    {
        /// <summary>The 23 SimHub game IDs v1.5.2 computes for. Order follows the plugin's own switch,
        /// so a future diff against a newer version reads cleanly.</summary>
        public static readonly IReadOnlyList<string> Shipped = new[]
        {
            // TyreRPS families - these need the plugin's own tyre-diameter detection first
            "PCars", "PCars2", "Automobilista2",
            "RRRE",
            "RFactor2", "RFactor2Spectator", "LMU",
            "AssettoCorsa", "AssettoCorsaCompetizione", "AssettoCorsaEVO", "AssettoCorsaRally",
            "GranTurismo7",
            // already-linear wheel speed - no diameter detection needed
            "F12018", "F12019", "F12020", "F12021", "F12022",
            "F12023", "F12024", "F12025",
            "EAWRC23",
            "ProjectMotorRacing",
            "CodemastersDirtRally2",
        };

        /// <summary>The plugin version <see cref="Shipped"/> was transcribed from - shown in the UI so a
        /// driver can tell whether their Viper install is newer than our list.</summary>
        public const string ShippedFromPluginVersion = "1.5.2";

        /// <summary>
        /// Whether <paramref name="gameId"/> is in <paramref name="supportedGames"/>.
        /// <para/>
        /// Case-insensitive, and a null/blank game is NOT supported: with no game running there is
        /// nothing for the Viper plugin to compute, so reporting "supported" would light the UI up with
        /// a claim nothing has tested.
        /// </summary>
        /// <summary>
        /// Whether Viper computes for this title: it is one this project SHIPS knowledge of, or one the
        /// driver has added themselves.
        /// <para/>
        /// THE SHIPPED LIST IS NOT REMOVABLE (owner, 2026-10-01: "for the Viper source, when in the F125
        /// game, which is the internal supported game, it will still shows the Remove from Support List
        /// button which is INCORRECT"). It is this project's own record of what viper4gh's plugin
        /// handles, not a preference, so it is OR-ed in here rather than merely seeding the file. That
        /// also repairs a document that already has a shipped title removed - which was reachable,
        /// because the button used to offer Remove for one - without any migration: the entry is simply
        /// ignored, and the title reads as supported again on the next frame.
        /// <para/>
        /// The file therefore only ever ADDS to what ships.
        /// </summary>
        public static bool IsSupported(string gameId, IEnumerable<string> supportedGames)
            => IsListed(gameId, Shipped) || IsListed(gameId, supportedGames);

        /// <summary>Plain membership, with no shipped-list fallback - the test
        /// <see cref="IsSupported"/> is built from, and what <see cref="WithGame"/> uses to decide
        /// whether the FILE needs changing.</summary>
        private static bool IsListed(string gameId, IEnumerable<string> games)
        {
            if (string.IsNullOrWhiteSpace(gameId) || games == null) return false;
            return games.Any(g => string.Equals(g, gameId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Whether this game is in OUR OWN hardcoded list, regardless of what the file says.
        /// The settings page uses this to decide which way the Add/Remove button points: a game we ship
        /// knowledge of offers Remove, one we do not offers Add.</summary>
        /// <summary>Whether this title is one THIS PROJECT ships knowledge of, as opposed to one the
        /// driver added. The settings page hides the Add/Remove button for these: there is nothing to
        /// add, and removing is not offered because the shipped list is not a preference.</summary>
        public static bool IsShipped(string gameId) => IsListed(gameId, Shipped);

        /// <summary>A document seeded from <see cref="Shipped"/> - what gets written the first time
        /// Viper is applied and no file exists yet.</summary>
        public static ViperDocument CreateShippedDocument() => new ViperDocument
        {
            Version = ViperDocument.CurrentVersion,
            SupportedGames = Shipped.ToList(),
        };

        /// <summary>
        /// <paramref name="document"/> with <paramref name="gameId"/> present. Returns the SAME instance
        /// when it already is, so a caller can use reference equality to decide whether anything needs
        /// saving.
        /// </summary>
        public static ViperDocument WithGame(ViperDocument document, string gameId)
        {
            if (document == null || string.IsNullOrWhiteSpace(gameId)) return document;
            if (IsSupported(gameId, document.SupportedGames)) return document;

            ViperDocument next = document.Clone();
            next.SupportedGames.Add(gameId.Trim());
            return next;
        }

        /// <summary><paramref name="document"/> without <paramref name="gameId"/>. Returns the SAME
        /// instance when it was not there - see <see cref="WithGame"/>.</summary>
        public static ViperDocument WithoutGame(ViperDocument document, string gameId)
        {
            if (document == null || string.IsNullOrWhiteSpace(gameId)) return document;

            // ONLY THE DRIVER'S OWN ADDITIONS CAN BE REMOVED. Tested against the FILE's list, not
            // IsSupported - which now ORs in the shipped list, so asking it here would report a shipped
            // title as removable and produce a document that cannot take effect. The settings page
            // hides the button for shipped titles anyway; this is the model refusing it outright.
            if (!IsListed(gameId, document.SupportedGames)) return document;

            ViperDocument next = document.Clone();
            next.SupportedGames.RemoveAll(g => string.Equals(g, gameId, StringComparison.OrdinalIgnoreCase));
            return next;
        }
    }
}

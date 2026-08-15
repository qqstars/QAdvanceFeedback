using System.Collections.Generic;
using System.Globalization;

namespace QAdvanceFeedback.Core.Localization
{
    /// <summary>
    /// Compiled-in localisation: no RESX satellite assemblies, so a user who copies only the single
    /// plugin DLL still gets every string, in whichever language matches SimHub's UI culture, instead
    /// of silently falling back to English because a satellite folder was left behind. Ported from the
    /// sibling ReliableWheelLockSlip project's identically-named class.
    /// </summary>
    public static class Strings
    {
        private static readonly Dictionary<string, string> En = StringTableEn.Create();
        private static readonly Dictionary<string, string> ZhHans = StringTableZhHans.Create();

        private static CultureInfo _culture = CultureInfo.CurrentUICulture;

        /// <summary>Settable so tests can pin a culture without touching thread-global state.</summary>
        public static CultureInfo Culture
        {
            get => _culture;
            set => _culture = value ?? CultureInfo.InvariantCulture;
        }

        /// <summary>The full key set - both tables are required to match it exactly (see the
        /// key-parity test).</summary>
        public static IEnumerable<string> Keys => En.Keys;

        /// <summary>
        /// Looks up <paramref name="key"/> in the table for <see cref="Culture"/>. A missing key
        /// returns the key itself rather than null or throwing - a visible identifier degrades
        /// gracefully, a blank control or a crash in the settings UI does not.
        /// </summary>
        public static string Get(string key)
        {
            if (key == null) return null;
            var table = TableFor(_culture.Name);
            return table.TryGetValue(key, out var value) ? value : key;
        }

        /// <summary>
        /// Resolves a culture name to a table by two-letter language code, so "zh-CN", "zh-Hans",
        /// "zh-SG" and "zh" all resolve to Simplified Chinese. Anything else - including a two-letter
        /// code we do not carry a table for, or an invalid/invariant name - falls back to English.
        /// </summary>
        public static IReadOnlyDictionary<string, string> TableFor(string cultureName)
        {
            if (!string.IsNullOrEmpty(cultureName))
            {
                string twoLetter = null;
                try
                {
                    twoLetter = new CultureInfo(cultureName).TwoLetterISOLanguageName;
                }
                catch (CultureNotFoundException)
                {
                    // Falls through to English below.
                }

                if (twoLetter == "zh") return ZhHans;
            }

            return En;
        }
    }
}

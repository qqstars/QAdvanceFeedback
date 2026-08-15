using System.Globalization;
using System.Linq;
using QAdvanceFeedback.Core.Localization;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Key-parity test: English and Simplified Chinese must carry EXACTLY the same key set, so a
    /// missing translation never silently falls back to a raw key string in production - see
    /// Strings.Get's own remarks for why a missing key degrades to the key itself rather than
    /// throwing (a deliberate UI-safety net, not a substitute for actually keeping both tables
    /// complete).
    /// </summary>
    public class LocalizationTests
    {
        [Fact]
        public void English_and_Simplified_Chinese_have_exactly_the_same_key_set()
        {
            var en = StringTableEn.Create();
            var zh = StringTableZhHans.Create();

            var missingFromZh = en.Keys.Except(zh.Keys).ToArray();
            var extraInZh = zh.Keys.Except(en.Keys).ToArray();

            Assert.True(missingFromZh.Length == 0, "Missing from zh-Hans: " + string.Join(", ", missingFromZh));
            Assert.True(extraInZh.Length == 0, "Extra keys only in zh-Hans: " + string.Join(", ", extraInZh));
            Assert.Equal(en.Count, zh.Count);
        }

        [Fact]
        public void No_value_in_either_table_is_null_or_whitespace()
        {
            var en = StringTableEn.Create();
            var zh = StringTableZhHans.Create();

            foreach (var pair in en) Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"English key '{pair.Key}' is blank");
            foreach (var pair in zh) Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"zh-Hans key '{pair.Key}' is blank");
        }

        [Fact]
        public void Strings_Get_resolves_English_by_default_culture_and_zh_Hans_for_Chinese_cultures()
        {
            var original = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal("Wheel Lock", Strings.Get("Tab.WheelLock"));

                Strings.Culture = CultureInfo.GetCultureInfo("zh-CN");
                Assert.Equal("车轮抱死", Strings.Get("Tab.WheelLock"));

                Strings.Culture = CultureInfo.GetCultureInfo("zh-Hans");
                Assert.Equal("车轮抱死", Strings.Get("Tab.WheelLock"));
            }
            finally
            {
                Strings.Culture = original;
            }
        }

        [Fact]
        public void An_unknown_key_degrades_to_the_key_itself_rather_than_throwing_or_returning_null()
        {
            Assert.Equal("Some.Missing.Key", Strings.Get("Some.Missing.Key"));
        }

        [Fact]
        public void A_culture_with_no_dedicated_table_falls_back_to_English()
        {
            var original = Strings.Culture;
            try
            {
                Strings.Culture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.Equal("Wheel Lock", Strings.Get("Tab.WheelLock"));
            }
            finally
            {
                Strings.Culture = original;
            }
        }
    }
}

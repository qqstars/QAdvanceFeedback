using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// GUARDS THE APPLY BUTTON'S DIRTY TRACKING against the failure that actually happened: 52 of the
    /// page's 105 named inputs had no dirty path at all, so editing them left Apply greyed out and the
    /// change was silently discarded.
    /// <para/>
    /// <c>SettingsControl.xaml.cs</c> is deliberately NOT link-compiled into this project (it needs WPF
    /// and SimHub - see the .csproj's own remarks), and a WPF control cannot be instantiated from a
    /// plain xunit run anyway. So these read the XAML and the code-behind AS TEXT. That is a weaker
    /// instrument than exercising the control, and it is chosen knowingly: it can still catch the exact
    /// regression class that bit us, which is a control type the wiring does not handle.
    /// </summary>
    public class SettingsControlDirtyTrackingTests
    {
        private readonly ITestOutputHelper _out;
        public SettingsControlDirtyTrackingTests(ITestOutputHelper output) { _out = output; }

        /// <summary>The four control types <c>WireDirtyTracking</c>'s reflective sweep subscribes to.</summary>
        private static readonly HashSet<string> HandledTypes =
            new HashSet<string>(StringComparer.Ordinal) { "NumericUpDown", "ToggleSwitch", "ComboBox", "TextBox" };

        /// <summary>Named elements that take user input but are NOT settings - nothing to mark dirty.</summary>
        private static readonly HashSet<string> NotSettings =
            new HashSet<string>(StringComparer.Ordinal) { "ComboBoxItem" };

        private static string SettingsDirectory([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)), "QAdvanceFeedback", "Settings");

        private static string ReadXaml() => File.ReadAllText(Path.Combine(SettingsDirectory(), "SettingsControl.xaml"));
        private static string ReadCodeBehind() => File.ReadAllText(Path.Combine(SettingsDirectory(), "SettingsControl.xaml.cs"));

        /// <summary>Every x:Name'd element in the XAML, as (elementType, name).</summary>
        private static List<KeyValuePair<string, string>> NamedElements(string xaml)
            => Regex.Matches(xaml, @"<(?:mah:)?(\w+)[^>]*?x:Name=""([A-Za-z0-9_]+)""", RegexOptions.Singleline)
                    .Cast<Match>()
                    .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value))
                    .ToList();

        [Fact]
        public void Nothing_in_the_test_effect_panel_marks_the_page_dirty()
        {
            // THE ONE THAT GOT THROUGH. The panel was excluded from WireDirtyTracking's reflective sweep,
            // but its toggle ALSO had hand-wired `Checked += (s, e) => { MarkDirty(); ... }` from before
            // that exclusion existed - so switching the test on still lit up Apply. Excluding a control
            // from the sweep is not enough; nothing in the harness may call MarkDirty at all.
            //
            // Comment lines are stripped first, so the prose explaining this rule cannot satisfy it.
            string[] lines = File.ReadAllLines(Path.Combine(SettingsDirectory(), "SettingsControl.GForceTest.cs"));
            var offenders = lines
                .Select((text, index) => new { text = text.Trim(), line = index + 1 })
                .Where(l => !l.text.StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.text.StartsWith("///", StringComparison.Ordinal))
                .Where(l => l.text.Contains("MarkDirty("))
                .ToList();

            foreach (var o in offenders) _out.WriteLine($"line {o.line}: {o.text}");

            Assert.True(offenders.Count == 0,
                "the Test Effect partial must never mark the page dirty: "
                + string.Join(" | ", offenders.Select(o => $"line {o.line}")));
        }

        [Fact]
        public void The_test_effect_panel_is_excluded_from_dirty_tracking_and_from_persistence()
        {
            // OWNER: "ANY OPTIONS RELATED TO THE TEST EFFECT WILL NOT BE SAVED". The panel is a
            // diagnostic harness - its toggle and rate spinner must not light up Apply, and must never
            // reach SaveToSettings/LoadFromSettings. Excluded BY PREFIX so a control added to the panel
            // later is excluded automatically, which is the same reasoning that made the sweep itself
            // reflective.
            string code = ReadCodeBehind();

            Assert.Contains("IsTestEffectControl(field.Name)", code);
            Assert.Contains("StartsWith(\"GForceTest\"", code);

            // Nothing in the panel may be read into or written from the settings object.
            string save = Between(code, "private void SaveToSettings()", "\n        private void ");
            string load = Between(code, "private void LoadFromSettings()", "\n        private void ");
            Assert.DoesNotContain("GForceTest", save);
            Assert.DoesNotContain("GForceTest", load);

            // And every named control in that panel really does carry the prefix the exclusion keys off.
            var panelControls = NamedElements(ReadXaml())
                .Where(e => HandledTypes.Contains(e.Key))
                .Where(e => e.Value.IndexOf("Test", StringComparison.Ordinal) >= 0)
                .ToList();

            Assert.NotEmpty(panelControls);
            foreach (KeyValuePair<string, string> e in panelControls)
                Assert.StartsWith("GForceTest", e.Value, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_named_input_control_is_a_type_the_dirty_sweep_handles()
        {
            // THE ONE ASSUMPTION the reflective sweep makes. Adding, say, a Slider or a RadioButton to
            // the page would silently reintroduce the original defect for that control; this fails
            // instead, and names the type to add.
            string xaml = ReadXaml();

            var inputLike = new[]
            {
                "NumericUpDown", "ToggleSwitch", "ComboBox", "TextBox",
                "CheckBox", "Slider", "RadioButton", "ToggleButton", "PasswordBox", "DatePicker", "ListBox"
            };

            List<KeyValuePair<string, string>> unhandled = NamedElements(xaml)
                .Where(e => inputLike.Contains(e.Key))
                .Where(e => !NotSettings.Contains(e.Key))
                .Where(e => !HandledTypes.Contains(e.Key))
                .ToList();

            foreach (KeyValuePair<string, string> e in unhandled)
                _out.WriteLine($"unhandled input: {e.Value} ({e.Key})");

            Assert.True(unhandled.Count == 0,
                "these named inputs are types WireDirtyTracking's sweep does not subscribe to, so editing "
                + "them would leave Apply disabled: "
                + string.Join(", ", unhandled.Select(e => $"{e.Value} ({e.Key})")));
        }

        [Fact]
        public void The_dirty_wiring_is_reflective_rather_than_an_enumerated_list()
        {
            // The regression was an enumerated array falling behind the XAML. Nothing fails when such a
            // list is incomplete, which is exactly why it stayed wrong - so pin the mechanism, not the
            // membership.
            string code = ReadCodeBehind();
            string body = Between(code, "private void WireDirtyTracking()", "\n        private void ");

            Assert.Contains("GetFields(", body);
            Assert.Contains("NumericUpDown spinner", body);
            Assert.Contains("ToggleSwitch toggle", body);
            Assert.Contains("ComboBox combo", body);
            Assert.Contains("TextBox box", body);
        }

        [Fact]
        public void The_page_still_has_the_inputs_this_guard_is_protecting()
        {
            // Keeps the two tests above honest: if the regex ever stops matching (a XAML refactor, a
            // namespace prefix change), they would both pass vacuously.
            List<KeyValuePair<string, string>> named = NamedElements(ReadXaml());
            int inputs = named.Count(e => HandledTypes.Contains(e.Key) && !NotSettings.Contains(e.Key));

            _out.WriteLine($"named inputs found: {inputs}");
            Assert.True(inputs > 80, $"expected the settings page's ~100 named inputs, found {inputs}");
        }

        [Fact]
        public void Controls_that_were_silently_unwired_are_covered_by_the_sweep()
        {
            // A named sample of the real casualties, so the specific report ("changing some config does
            // not enable Apply") stays documented against the controls it actually affected.
            string[] wereBroken =
            {
                "GForceAccelScale", "GForceBrakeScale", "GForceLateralScale", "GForceFixedLatMax",
                // GForceShakeBlend was here too until v1.0.8 replaced it with the Shake feeling
                // dropdown - the guard caught its removal, which is the point of naming them.
                "GForceShakeTrigger", "GForceShakeSustain", "GForceShakeFrequency",
                "GForceAccelBackTopLatSplit", "GForceBrakeBottomFrontLatSplit",
                "LockKeyDataSMax", "SlipKeyDataSMax", "LockKeyDataAutoToggle",
                "LockCriticalFlattenRange", "ShakeItImportOverrideCheckBox",
            };

            List<KeyValuePair<string, string>> named = NamedElements(ReadXaml());
            foreach (string name in wereBroken)
            {
                KeyValuePair<string, string> match = named.FirstOrDefault(e => e.Value == name);
                Assert.True(match.Value == name, $"{name} is no longer on the page - update this test");
                Assert.True(HandledTypes.Contains(match.Key),
                    $"{name} is a {match.Key}, which the dirty sweep does not handle");
            }
        }

        private static string Between(string text, string startMarker, string endMarker)
        {
            int start = text.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"could not find '{startMarker}' in the code-behind");
            int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
        }
    }
}

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

        /// <summary>The four control types <c>WireDirtyTracking</c>'s reflective sweep subscribes to.
        /// NumericEditor is this project's own wrapper around MahApps' NumericUpDown - see that control
        /// for why every numeric input on the page is wrapped rather than raw.</summary>
        private static readonly HashSet<string> HandledTypes =
            new HashSet<string>(StringComparer.Ordinal) { "NumericEditor", "ToggleSwitch", "ComboBox", "TextBox" };

        /// <summary>Named elements that take user input but are NOT settings - nothing to mark dirty.</summary>
        private static readonly HashSet<string> NotSettings =
            new HashSet<string>(StringComparer.Ordinal) { "ComboBoxItem" };

        private static string SettingsDirectory([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)), "QAdvanceFeedback", "Settings");

        [Fact]
        public void There_is_exactly_ONE_suppression_mechanism_for_programmatic_writes()
        {
            // THE DEFECT THIS CLOSES (owner-reported, v1.1.0): "after relaunching SimHub the apply button
            // might not be right". The page had TWO suppression flags - ApplyDirtyState's loading guard
            // and a private _suppressKeyDataEvents bool - and the reflective wiring sweep hooks MarkDirty
            // to EVERY NumericUpDown, the six key-data spinners included. That handler only honours
            // _dirty.IsLoading, so a programmatic seed (which runs on the FRAME LOOP, on the first
            // learned-value push after SimHub starts) suppressed the business-logic handler but not the
            // sweep's - and Apply lit up on a freshly opened page with nothing to apply.
            //
            // The sweep's own remarks make the argument: "a list cannot be kept correct by discipline
            // alone, because nothing fails when it is wrong". Two parallel guards are the same hazard, so
            // there is now one - _dirty.BeginLoading() - and this test keeps it that way.
            string code = ReadCodeBehind();

            foreach (Match m in Regex.Matches(code, @"private\s+bool\s+(_suppress\w*)"))
                Assert.Fail(
                    $"'{m.Groups[1].Value}' is a second suppression flag. The reflective sweep's MarkDirty "
                    + "handler honours ONLY _dirty.IsLoading, so anything guarded by a separate flag still "
                    + "marks the page dirty. Wrap programmatic writes in _dirty.BeginLoading() instead.");
        }

        [Fact]
        public void Programmatic_key_data_writes_go_through_the_loading_guard()
        {
            // The three helpers that write the key-data spinners from code rather than from a user edit.
            // Each must sit inside a BeginLoading scope, or the sweep marks the page dirty behind them.
            string code = ReadCodeBehind();
            foreach (string helper in new[]
                     { "SeedManualBoxesFromLearnedIfNeeded", "ReloadKeyDataForCurrentContext", "LoadKeyDataPoints" })
            {
                int at = code.IndexOf("private void " + helper, StringComparison.Ordinal);
                Assert.True(at >= 0, $"{helper} not found - rename it here too.");

                // Look only at the helper's own opening lines, not the rest of the file.
                string head = code.Substring(at, Math.Min(400, code.Length - at));
                Assert.True(head.Contains("_dirty.BeginLoading()"),
                    $"{helper} writes controls programmatically but does not open a _dirty.BeginLoading() "
                    + "scope, so the reflective sweep will mark the page dirty on every seed.");
            }
        }

        [Fact]
        public void Every_numeric_input_is_the_wrapped_NumericEditor_control()
        {
            // THE OWNER'S REQUIREMENT (2026-09-28): "if you disable the numeric editor, both of the
            // textbox and +- will be disabled ... wrap the Textbox and the +- button as a control, to
            // control the disable together, so ALL controls will apply the same thing."
            //
            // NumericEditor hosts exactly one MahApps NumericUpDown, so WPF coerces IsEnabled onto both
            // halves and they cannot disagree - the guarantee is structural rather than a convention
            // every call site has to remember. A raw <mah:NumericUpDown> left on the page would sit
            // outside it, so none may remain.
            //
            // IsReadOnly is the specific trap the wrapper also closes: it stops typing while leaving the
            // spin buttons live, which is a half-disabled editor. The control exposes no such property,
            // and it must not reappear on the page either.
            string xaml = ReadXaml();

            Assert.DoesNotContain("<mah:NumericUpDown", xaml);
            Assert.True(Regex.Matches(xaml, "<local:NumericEditor").Count > 50,
                "expected the page's numeric inputs to be the wrapped control");
            Assert.DoesNotContain("IsReadOnly", xaml);
            Assert.DoesNotContain("IsReadOnly", ReadCodeBehind());
        }

        private static string ReadXaml() => File.ReadAllText(Path.Combine(SettingsDirectory(), "SettingsControl.xaml"));
        private static string ReadCodeBehind() => File.ReadAllText(Path.Combine(SettingsDirectory(), "SettingsControl.xaml.cs"));

        /// <summary>Every x:Name'd element in the XAML, as (elementType, name).</summary>
        /// <summary>Every x:Name'd element, as (type name, control name). The prefix is optional and
        /// discarded - the page uses <c>mah:</c> for MahApps, <c>local:</c> for this project's own
        /// NumericEditor wrapper, and none for stock WPF.</summary>
        private static List<KeyValuePair<string, string>> NamedElements(string xaml)
            => Regex.Matches(xaml, @"<(?:\w+:)?(\w+)[^>]*?x:Name=""([A-Za-z0-9_]+)""", RegexOptions.Singleline)
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
                "NumericEditor", "ToggleSwitch", "ComboBox", "TextBox",
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
            Assert.Contains("NumericEditor spinner", body);
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
                "LockCriticalFlattenRange", "ShakeItImportOverrideCheckBox",
                // LockKeyDataSMax / SlipKeyDataSMax / LockKeyDataAutoToggle WERE listed here, and have
                // deliberately moved OUT (2026-09-29): the key data controls now apply and persist
                // themselves the moment they change, so arming a button that means "not applied yet"
                // would be false. They are excluded from the sweep by IsImmediateApplyControl, which
                // PerSourceKeyDataFlagsTests guards - the exclusion is named and tested, not a hole.
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

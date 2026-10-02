using System.IO;
using System.Text.RegularExpressions;
using QAdvanceFeedback;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// "RESTORE ALL DEFAULTS" LEAVES THE CUSTOM SOURCE ALONE (owner, 2026-09-28: "Restore to Default
    /// will ONLY override the settings for the known sources, say, Raw, ShakeIt and Viper. The Custom
    /// source settings will NOT be overriden").
    /// <para/>
    /// Raw, ShakeIt and Viper are this plugin's own presets, and restoring them to shipped values is
    /// exactly what the button promises. A hand-written source configuration is the driver's work and
    /// there is no default to restore it TO.
    /// <para/>
    /// Also covers the settings page's own new invariants, which can only be checked by reading the
    /// source - the WPF control cannot be instantiated from this project.
    /// </summary>
    public class CustomSourceRestoreTests
    {
        private readonly ITestOutputHelper _out;
        public CustomSourceRestoreTests(ITestOutputHelper output) { _out = output; }

        private static string SettingsDirectory()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings")))
                dir = dir.Parent;
            return Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings");
        }

        private static string CodeBehind() => File.ReadAllText(Path.Combine(SettingsDirectory(), "SettingsControl.xaml.cs"));
        private static string Xaml() => File.ReadAllText(Path.Combine(SettingsDirectory(), "SettingsControl.xaml"));

        [Fact]
        public void A_restore_keeps_the_custom_source_configuration()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.SourceFrontLeft = "my.custom.lock.fl";
            settings.Lock.SwitchSourceMode(SourceMode.Custom, true);
            settings.Slip.SourceFrontLeft = "my.custom.slip.fl";
            settings.Slip.SwitchSourceMode(SourceMode.Custom, false);
            settings.Lock.ArchiveCurrentSources();
            settings.Slip.ArchiveCurrentSources();

            settings.RestoreDefaults();

            Assert.Equal("my.custom.lock.fl", settings.Lock.CustomSources.FrontLeft);
            Assert.Equal("my.custom.slip.fl", settings.Slip.CustomSources.FrontLeft);
        }

        [Fact]
        public void A_restore_keeps_the_custom_cold_start_reference_too()
        {
            // Half a configuration would be worse than either outcome: the source text and its
            // reference have to survive together or not at all.
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.KeyDataPointDefaults.LockCustom = new KeyDataPointDefaultSet(42.0, 37.8, 29.4);

            settings.RestoreDefaults();

            Assert.NotNull(settings.KeyDataPointDefaults.LockCustom);
            Assert.Equal(42.0, settings.KeyDataPointDefaults.LockCustom.SMax, 6);
        }

        [Fact]
        public void A_restore_DOES_reset_the_three_presets()
        {
            // The other half of the promise - the button must actually restore what it says it does.
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.SourceFrontLeft = "vandalised";
            settings.Lock.ArchiveCurrentSources();

            settings.RestoreDefaults();

            Assert.DoesNotContain("vandalised", settings.Lock.SourceFrontLeft ?? string.Empty);
            Assert.Equal(SourceMode.Manual, settings.Lock.SourceMode);
        }

        [Fact]
        public void A_shadowed_channel_adopts_Raws_identity_explicitly()
        {
            // THE BUG THIS CLOSES, found by auditing the Apply paths. ApplyRawShadowToSourceRows writes
            // the source boxes inside a loading scope - it must, or the edit hook reads them as the
            // driver typing and flips the channel to Custom - but OnSourceConfigurationChanged stands
            // down while loading, so the automatic "text changed, recompute the identity" path never
            // ran. The rows LOOKED right while _currentLockSource still pointed at the blocked source,
            // and that identity is what the key data points are keyed by: the panel would have shown,
            // and SAVED, the wrong source's numbers.
            string code = CodeBehind();

            Assert.Contains("AdoptSourceIdentity(isLock, RawSourceFallback.RawIdentity(isLock))", code);

            // And the way back matters as much: a game becoming supported must restore the configured
            // text, or an enabled Viper channel is left showing Raw properties.
            Assert.Contains("_lockShadowing", code);
            Assert.Contains("AdoptSourceIdentity(isLock, ComputeCurrentSourceIdentity(isLock))", code);
        }

        [Fact]
        public void A_plain_mode_switch_adopts_the_new_identity_even_with_no_shadow_involved()
        {
            // THE BUG THIS CLOSES, reported from the Viper Slip screenshot: the key data points showed
            // 75 / 67.5 / 52.5 - Slip's RAW reference - while the dropdown said Viper, whose reference
            // is 10 / 9 / 7.
            //
            // The adopt used to sit behind `else if (was)`, so it only ran when coming BACK from a
            // shadowed state. A plain Manual -> Viper switch shadows on neither side, so nothing ran:
            // ApplySourceDefaultsForMode writes the source boxes inside a loading scope (it must, or
            // the edit hook flips the dropdown to Custom), and OnSourceConfigurationChanged stands
            // down while loading. The identity stayed on the previous source, and the panel read and
            // wrote that source's key data points.
            string code = CodeBehind();

            int method = code.IndexOf("private void ApplyRawShadowToSourceRows", System.StringComparison.Ordinal);
            Assert.True(method > 0, "ApplyRawShadowToSourceRows not found");
            int end = code.IndexOf("\n        private ", method + 1, System.StringComparison.Ordinal);
            string body = code.Substring(method, end - method);

            Assert.DoesNotContain("else if (was)", body);
            Assert.Contains("if (was)", body);          // the TEXT restore is still conditional
            Assert.Contains("AdoptSourceIdentity(isLock, ComputeCurrentSourceIdentity(isLock))", body);
        }

        [Fact]
        public void The_viper_references_the_panel_should_have_shown_are_the_small_ones()
        {
            // The values the screenshot should have displayed, pinned so the numbers behind the bug
            // above cannot drift: Viper reads a 0-1 slip ratio scaled to 0-100, so its grip limit sits
            // far below Raw's. Raw's 75 / 85 appearing on a Viper channel is the visible symptom.
            KeyDataPointDefaults shipped = KeyDataPointDefaults.CreateShipped();

            Assert.True(shipped.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, false,
                out double slipSMax, out double slipS90, out double slipS75));
            Assert.Equal(10.0, slipSMax, 6);
            Assert.Equal(9.0, slipS90, 6);
            Assert.Equal(7.0, slipS75, 6);

            Assert.True(shipped.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, true,
                out double lockSMax, out double lockS90, out double lockS75));
            Assert.Equal(15.0, lockSMax, 6);
            Assert.Equal(13.5, lockS90, 6);
            Assert.Equal(10.5, lockS75, 6);

            // And what the screenshot actually showed, so the diagnosis stays legible.
            Assert.Equal(75.0, KeyDataPointSettings.SlipDefaultSMax, 6);
            Assert.Equal(67.5, KeyDataPointSettings.SlipDefaultSMax * KeyDataPointSettings.DerivedS90Fraction, 6);
            Assert.Equal(52.5, KeyDataPointSettings.SlipDefaultSMax * KeyDataPointSettings.DerivedS75Fraction, 6);
        }

        [Fact]
        public void Key_data_points_are_keyed_by_the_identity_the_page_is_showing()
        {
            // Read and write both go through _currentLockSource, so adopting Raw's identity above is
            // what makes "showing and saving Plugin Internal's values" true for BOTH halves rather
            // than only the display.
            string code = CodeBehind();
            int persist = code.IndexOf("private bool PersistChannelIfManual", System.StringComparison.Ordinal);
            Assert.True(persist > 0, "PersistChannelIfManual not found");
            Assert.Contains("_currentLockSource", code.Substring(0, persist));
            Assert.Contains("k.SetManual(_currentGameId, sourceIdentity", code);
        }

        [Fact]
        public void The_apply_button_and_the_unapplied_banner_move_together()
        {
            // They are one fact, and a banner that could disagree with the button would be worse than
            // no banner - so nothing may set ApplyButton.IsEnabled except RefreshApplyState.
            string code = CodeBehind();

            foreach (Match m in Regex.Matches(code, @"ApplyButton\.IsEnabled\s*="))
            {
                int lineStart = code.LastIndexOf('\n', m.Index) + 1;
                int methodStart = code.LastIndexOf("private void RefreshApplyState", m.Index, System.StringComparison.Ordinal);
                int otherMethod = code.LastIndexOf("private ", m.Index, System.StringComparison.Ordinal);
                Assert.True(methodStart >= 0 && methodStart == otherMethod,
                    "ApplyButton.IsEnabled is assigned outside RefreshApplyState at: "
                    + code.Substring(lineStart, 80).Split('\n')[0].Trim());
            }

            Assert.Contains("UnappliedChangesBanner.Visibility", code);
        }

        [Fact]
        public void Editing_a_preset_source_moves_the_channel_to_Custom()
        {
            // The owner's rule: the dropdown switches on the EDIT, before Apply, so a preset's own
            // configuration is never altered by typing in its boxes.
            string code = CodeBehind();
            Assert.Contains("MoveToCustomIfPresetEdited", code);
            Assert.Contains("SeedCustomFrom", code);
        }

        [Fact]
        public void The_cold_start_reference_sits_under_the_key_data_points_and_lines_up_with_them()
        {
            // OWNER, 2026-09-29: "move custom source Cold-start reference UNDER the Key Data Points
            // Settings ... and make sure the numeric setting control is vertically aligned with key
            // data points settings ones."
            //
            // The order is the argument: Key Data Points report what has been LEARNED, the cold-start
            // reference is what is assumed UNTIL then, so the reference reads as the footnote to the
            // block above rather than as an unrelated control stranded up in the source list.
            string xaml = Xaml();

            foreach (string ch in new[] { "Lock", "Slip" })
            {
                int keyData = xaml.IndexOf("x:Name=\"" + ch + "KeyDataSMax\"", System.StringComparison.Ordinal);
                int customRef = xaml.IndexOf("x:Name=\"" + ch + "CustomRefPanel\"", System.StringComparison.Ordinal);
                int sourceRows = xaml.IndexOf("x:Name=\"" + ch + "SourceFl\"", System.StringComparison.Ordinal);

                Assert.True(keyData > 0 && customRef > 0 && sourceRows > 0, ch + ": elements missing");
                Assert.True(customRef > keyData,
                    ch + ": the cold-start reference must come AFTER the key data points, not before");
                Assert.True(customRef > sourceRows,
                    ch + ": the cold-start reference must not sit inside the source rows any more");

                // Alignment is the shared-size column, not hand-tuned margins - the same group the key
                // data points use, so the two sets of editors cannot drift apart when a label changes.
                int panelEnd = xaml.IndexOf("</StackPanel>", customRef, System.StringComparison.Ordinal);
                string panel = xaml.Substring(customRef, panelEnd - customRef);
                Assert.Contains("SharedSizeGroup=\"" + ch + "Align\"", panel);
            }
        }

        [Fact]
        public void The_cold_start_reference_editors_have_no_watermark_so_they_cannot_read_dashes()
        {
            // OWNER, 2026-09-29: "the Cold-start reference shows as '---' looks not right, as the
            // cold-start reference should ALWAYS have particular value ... The Key Data Points Settings
            // can show '---' or a particular learned value depending on the actual learning result."
            //
            // Two controls that look identical mean opposite things, and the watermark is what
            // distinguishes them: the key data editors KEEP theirs, because "not learned yet" is a real
            // state they must be able to report. A blank cold-start reference would mean "assume
            // nothing", which the projection cannot do.
            string xaml = Xaml();

            foreach (string ch in new[] { "Lock", "Slip" })
                foreach (string field in new[] { "SMax", "S90", "S75" })
                {
                    Assert.DoesNotContain("Watermark", LineDeclaring(xaml, ch + "CustomRef" + field));
                    Assert.Contains("Watermark=\"---\"", LineDeclaring(xaml, ch + "KeyData" + field));
                }

            // And the code-behind backstop, so no arrival path can leave one empty even if a future
            // seeding route is added and forgotten.
            string code = CodeBehind();
            Assert.Contains("KnownFeedbackSource.QAdvanceFeedbackRaw", code);
            Assert.Contains("SeedCustomReferenceFromCurrentSource", code);
        }

        /// <summary>The single XAML line declaring <paramref name="name"/>, for asserting on its
        /// attributes without matching some other element's.</summary>
        private static string LineDeclaring(string xaml, string name)
        {
            foreach (string line in xaml.Split('\n'))
                if (line.Contains("x:Name=\"" + name + "\"")) return line;
            throw new Xunit.Sdk.XunitException("no element named " + name);
        }

        [Fact]
        public void The_viper_game_button_spans_the_source_label_and_the_dropdown()
        {
            // OWNER, 2026-09-29: "left aligned with 'Source:' text left, and right aligned with the
            // dropdown control right." That is a Grid with the button spanning both columns - a
            // horizontal StackPanel, which is what this was, cannot express either edge.
            string xaml = Xaml();

            foreach (string ch in new[] { "Lock", "Slip" })
            {
                int panel = xaml.IndexOf("x:Name=\"" + ch + "SourceModePanel\"", System.StringComparison.Ordinal);
                Assert.True(panel > 0, ch + "SourceModePanel missing");

                int gridStart = xaml.LastIndexOf("<Grid ", panel, System.StringComparison.Ordinal);
                Assert.True(gridStart > 0 && panel - gridStart < 40,
                    ch + ": the source-mode panel must be a Grid so the button can span its columns");

                int gridEnd = xaml.IndexOf("</Grid>", panel, System.StringComparison.Ordinal);
                string grid = xaml.Substring(gridStart, gridEnd - gridStart);

                Assert.Contains("x:Name=\"" + ch + "ViperGamePanel\"", grid);
                Assert.Contains("Grid.ColumnSpan=\"2\"", grid);

                // The dropdown must STRETCH, or the button's right edge overshoots it the moment the
                // caption is the wider of the two - which it is, in both languages.
                Assert.Contains("x:Name=\"" + ch + "SourceModeCombo\" HorizontalAlignment=\"Stretch\"", grid);
            }
        }

        [Fact]
        public void The_page_declares_every_new_element_for_both_channels()
        {
            // A notice or a button added for one channel only is a silent half-feature - the two
            // channels are required to behave identically.
            string xaml = Xaml();
            foreach (string name in new[]
                     {
                         "SourceModeCustom", "CustomNote", "ViperUnsupportedNote",
                         "ViperGamePanel", "ViperGameButton", "KeyDataShadowNote",
                     })
            {
                Assert.Contains("x:Name=\"Lock" + name + "\"", xaml);
                Assert.Contains("x:Name=\"Slip" + name + "\"", xaml);
            }

            Assert.Contains("x:Name=\"UnappliedChangesBanner\"", xaml);
        }

        [Fact]
        public void Every_new_string_key_exists_in_both_languages()
        {
            string dir = Path.Combine(Directory.GetParent(SettingsDirectory()).FullName, "Core", "Localization");
            string en = File.ReadAllText(Path.Combine(dir, "StringTableEn.cs"));
            string zh = File.ReadAllText(Path.Combine(dir, "StringTableZhHans.cs"));

            foreach (string key in new[]
                     {
                         "Sources.Mode.Custom", "Sources.Mode.CustomNote", "Sources.ViperUnsupported.Note",
                         "Sources.ViperGame.Add", "Sources.ViperGame.Remove",
                         "KeyData.ShadowingRaw.Note", "Apply.Unapplied.Banner",
                     })
            {
                Assert.Contains("\"" + key + "\"", en);
                Assert.Contains("\"" + key + "\"", zh);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The page shell view (Ui\Views\SettingsPageView.xaml) and the General tab view
    /// (Ui\Views\GeneralPageView.xaml) - WPF cannot run here, so their XAML is read as XML: every binding
    /// names a real view-model member, the tab structure / names the screenshot harness uses are today's,
    /// and the page's implicit styles are exactly the old page's.
    /// </summary>
    public class SettingsPageViewXamlTests
    {
        private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace V = "clr-namespace:QAdvanceFeedbackViews";
        private static readonly XNamespace Mah = "clr-namespace:MahApps.Metro.Controls;assembly=MahApps.Metro";

        private static string PluginDir()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(dir.FullName, "QAdvanceFeedback");
        }

        private static XDocument Load(params string[] parts) => XDocument.Load(Path.Combine(new[] { PluginDir() }.Concat(parts).ToArray()));
        private static XDocument Shell() => Load("Ui", "Views", "SettingsPageView.xaml");
        private static XDocument General() => Load("Ui", "Views", "GeneralPageView.xaml");
        private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string)e.Attribute(X + "Name") == name);

        /// <summary>SettingsPageViewModel members still to be added by the coordinator (requested by agent C).</summary>
        private static readonly string[] RequestedShellMembers = { "SlipAngleTab", "ClutchTab" };

        // ------------------------------------------------------------ shell

        [Fact]
        public void The_shell_activates_its_view_model()
        {
            Assert.Equal("True", (string)Shell().Root.Attribute(V + "ViewModelLifetime.Activate"));
        }

        [Fact]
        public void The_tabs_keep_their_names_order_headers_scroll_viewers_and_views()
        {
            // Names: tools\screenshot-harness (FindName("MainTabs"), tab names). Order and headers:
            // SettingsControl.xaml / LocalizeStaticText (xaml.cs:1759-1764).
            var expected = new[]
            {
                ("WheelLockTab", "Tab.WheelLock", "WheelChannelPageView", "LockTab", true),
                ("WheelSlipTab", "Tab.WheelSlip", "WheelChannelPageView", "SlipTab", true),
                ("SlipAngleTab", "Tab.SlipAngle", "SlipAngleTabView", "SlipAngleTab", false),
                ("GForceTab", "Tab.GForce", "GForcePageView", "GForceTab", true),
                ("ClutchTab", "Tab.Clutch", "ClutchBiteView", "ClutchTab", false),
                ("GeneralTab", "Tab.General", "GeneralPageView", "GeneralTab", false),
            };
            XElement tabs = Named(Shell(), "MainTabs");
            Assert.Equal(P + "TabControl", tabs.Name);
            List<XElement> items = tabs.Elements(P + "TabItem").ToList();
            Assert.Equal(expected.Length, items.Count);

            for (int i = 0; i < expected.Length; i++)
            {
                var (name, header, view, path, sharedScope) = expected[i];
                XElement item = items[i];
                Assert.Equal(name, (string)item.Attribute(X + "Name"));
                Assert.Equal("{v:Loc " + header + "}", (string)item.Attribute("Header"));
                XElement scroll = item.Elements().Single();
                Assert.Equal(P + "ScrollViewer", scroll.Name);
                Assert.Equal("Auto", (string)scroll.Attribute("VerticalScrollBarVisibility"));
                XElement content = scroll.Elements().Single();
                Assert.Equal(V + view, content.Name);
                Assert.Equal("{Binding " + path + "}", (string)content.Attribute("DataContext"));
                // The shared-size scope stays tab-level, where the old page had it.
                Assert.Equal(sharedScope ? "True" : null, (string)content.Attribute("Grid.IsSharedSizeScope"));
            }
        }

        [Fact]
        public void The_old_named_tab_views_keep_their_names()
        {
            Assert.Equal(V + "SlipAngleTabView", Named(Shell(), "SlipAngleTabView").Name);
            Assert.Equal(V + "ClutchBiteView", Named(Shell(), "ClutchBiteView").Name);
        }

        [Fact]
        public void Apply_and_restore_are_bound_commands_below_the_banner()
        {
            // Old: ApplyButton / RestoreAllDefaultsButton (SettingsControl.xaml:92-95), the only writer of
            // ApplyButton.IsEnabled was RefreshApplyState (xaml.cs:1572-1576): now the command's CanExecute.
            XDocument shell = Shell();
            XElement apply = Named(shell, "ApplyButton");
            Assert.Equal("{Binding ApplyCommand}", (string)apply.Attribute("Command"));
            Assert.Null(apply.Attribute("IsEnabled"));   // CanExecute (IsDirty) is the only source
            Assert.Equal("16,4", (string)apply.Attribute("Padding"));
            XElement restore = Named(shell, "RestoreAllDefaultsButton");
            Assert.Equal("{Binding RestoreDefaultsCommand}", (string)restore.Attribute("Command"));
            Assert.Same(apply.Parent, restore.Parent);
            Assert.Equal("Bottom", (string)apply.Parent.Attribute("DockPanel.Dock"));
            Assert.Equal("8,8,8,0", (string)apply.Parent.Attribute("Margin"));
        }

        [Fact]
        public void The_unapplied_banner_shows_exactly_while_the_page_is_dirty()
        {
            // Old: UnappliedChangesBanner (SettingsControl.xaml:99-100), visible iff dirty
            // (RefreshApplyState, xaml.cs:1575) - one fact with the Apply button.
            XDocument shell = Shell();
            XElement banner = Named(shell, "UnappliedChangesBanner");
            Assert.Equal("{Binding IsDirty, Converter={StaticResource VisibleWhen}}", (string)banner.Attribute("Visibility"));
            Assert.Equal("{Binding UnappliedBannerText, Mode=OneWay}", (string)banner.Attribute("Text"));
            Assert.Equal("#FFCC4400", (string)banner.Attribute("Foreground"));
            Assert.Equal("Bold", (string)banner.Attribute("FontWeight"));
            Assert.Equal("12,8,8,0", (string)banner.Attribute("Margin"));
            Assert.Equal("Bottom", (string)banner.Attribute("DockPanel.Dock"));

            // Dock=Bottom stacks upward: the buttons first, then the banner directly above them, then the tabs.
            List<XElement> dock = banner.Parent.Elements().ToList();
            Assert.Equal(new[] { 0, 1, 2 }, new[]
            {
                dock.IndexOf(Named(shell, "ApplyButton").Parent), dock.IndexOf(banner), dock.IndexOf(Named(shell, "MainTabs")),
            });
        }

        [Fact]
        public void The_shell_carries_the_old_pages_implicit_styles_exactly()
        {
            // Old: SettingsControl.xaml UserControl.Resources (lines 10-51). Nested views rely on them.
            Dictionary<string, string> Implicit(XDocument doc) => doc.Root.Element(P + "UserControl.Resources")
                .Elements(P + "Style").Where(s => s.Attribute(X + "Key") == null)
                .ToDictionary(s => (string)s.Attribute("TargetType"), s => s.ToString(SaveOptions.DisableFormatting));

            // Wave 2: the old page is now a thin host with no resources of its own, so the shell is the one
            // home of these styles (compared leaf by leaf against the old page before it was deleted). Their
            // values are pinned here so a later edit is a visible decision.
            Dictionary<string, string> now = Implicit(Shell());
            Assert.Equal(new[] { "CheckBox", "ComboBox", "GroupBox", "TabItem", "TextBlock", "TextBox", "mah:NumericUpDown", "Button" }.OrderBy(s => s),
                now.Keys.OrderBy(s => s));
            Assert.Null(Load("Settings", "SettingsControl.xaml").Root.Element(P + "UserControl.Resources"));
            string all = string.Join("\n", now.Values);
            foreach (string setter in new[]
                     {
                         "Property=\"MinWidth\" Value=\"200\"", "Property=\"MinWidth\" Value=\"160\"", "Property=\"Padding\" Value=\"8\"",
                         "Property=\"Margin\" Value=\"8\"", "Property=\"Padding\" Value=\"10,4\"", "Property=\"Width\" Value=\"80\"",
                         "Property=\"StringFormat\" Value=\"0.#\"", "Property=\"VerticalAlignment\" Value=\"Center\"",
                     })
                Assert.Contains(setter, all);
        }

        [Fact]
        public void Every_shell_binding_names_a_page_view_model_member()
        {
            foreach (string path in BindingPaths(Shell().Root))
            {
                if (RequestedShellMembers.Contains(path)) continue;
                Assert.True(typeof(SettingsPageViewModel).GetProperty(path) != null, "SettingsPageViewModel has no " + path);
            }
        }

        [Fact]
        public void The_requested_tab_members_have_the_agreed_types_once_added()
        {
            // Requests to the coordinator: SlipAngleTab (SlipAngleTabViewModel), ClutchTab (ClutchBiteViewModel).
            PropertyInfo slipAngle = typeof(SettingsPageViewModel).GetProperty("SlipAngleTab");
            if (slipAngle != null) Assert.Equal(typeof(SlipAngleTabViewModel), slipAngle.PropertyType);
            PropertyInfo clutch = typeof(SettingsPageViewModel).GetProperty("ClutchTab");
            if (clutch != null) Assert.Equal(typeof(ClutchBiteViewModel), clutch.PropertyType);
        }

        // ------------------------------------------------------------ General tab view

        [Fact]
        public void The_general_view_activates_its_view_model()
        {
            Assert.Equal("True", (string)General().Root.Attribute(V + "ViewModelLifetime.Activate"));
        }

        [Fact]
        public void Every_general_binding_names_a_view_model_member()
        {
            // Root: GeneralViewModel; the Health group: HealthListViewModel; an issue block: HealthIssueViewModel.
            XElement root = General().Root;
            XElement health = root.Descendants().Single(e => (string)e.Attribute(X + "Name") == "HealthGroup");
            Assert.Equal("{Binding Health}", (string)health.Attribute("DataContext"));
            XElement template = health.Descendants(P + "DataTemplate").Single();

            var outside = BindingPaths(root, skip: health).ToList();
            var inHealth = BindingPaths(health, skip: template, includeSelf: false).ToList();
            var inTemplate = BindingPaths(template).ToList();
            Assert.NotEmpty(outside);
            Assert.NotEmpty(inHealth);
            Assert.NotEmpty(inTemplate);

            foreach (string p in outside) Assert.True(typeof(GeneralViewModel).GetProperty(p) != null, "GeneralViewModel has no " + p);
            foreach (string p in inHealth) Assert.True(typeof(HealthListViewModel).GetProperty(p) != null, "HealthListViewModel has no " + p);
            foreach (string p in inTemplate) Assert.True(typeof(HealthIssueViewModel).GetProperty(p) != null, "HealthIssueViewModel has no " + p);
        }

        [Fact]
        public void The_general_controls_keep_their_names_and_bindings()
        {
            XDocument g = General();
            var expected = new Dictionary<string, (string attr, string value)>
            {
                ["EnableDiagnosticsCheckBox"] = ("IsChecked", "{Binding EnableDiagnostics}"),
                ["ExportCsvCheckBox"] = ("IsChecked", "{Binding ExportCsv}"),
                ["ShakeItImportOverrideCheckBox"] = ("IsChecked", "{Binding ShakeItImportOverride}"),
                ["ShakeItImportButton"] = ("Command", "{Binding ShakeItImportCommand}"),
                ["ShakeItImportResultText"] = ("Visibility", "{Binding ShowShakeItImportResult, Converter={StaticResource VisibleWhen}}"),
                ["GeneralVersionText"] = ("Text", "{Binding VersionText, Mode=OneWay}"),
                ["HealthAllGoodText"] = ("Visibility", "{Binding IsHealthy, Converter={StaticResource VisibleWhen}}"),
                ["HealthIssuesPanel"] = ("ItemsSource", "{Binding Issues}"),
                ["HealthCopyDetailsButton"] = ("Command", "{Binding CopyDetailsCommand}"),
                ["HealthCopyDetailsDoneText"] = ("Visibility", "{Binding ShowCopyDone, Converter={StaticResource VisibleWhen}}"),
            };
            foreach (var pair in expected)
                Assert.Equal(pair.Value.value, (string)Named(g, pair.Key).Attribute(pair.Value.attr));
        }

        [Fact]
        public void Every_general_switch_spells_out_on_and_off()
        {
            // Old: ApplyOnOffLabelsToEverySwitch (SettingsControl.xaml.cs:285-303) - a toggle's state is
            // never inferred from a highlight alone.
            List<XElement> switches = General().Descendants(Mah + "ToggleSwitch").ToList();
            Assert.Equal(3, switches.Count);
            foreach (XElement s in switches)
            {
                Assert.Equal("{v:Loc GForce.Shake.On}", (string)s.Attribute("OnLabel"));
                Assert.Equal("{v:Loc GForce.Shake.Off}", (string)s.Attribute("OffLabel"));
            }
        }

        [Fact]
        public void A_failed_subsystem_block_is_firebrick_and_a_degraded_one_dark_orange()
        {
            XElement template = General().Descendants(P + "DataTemplate").Single();
            XElement block = template.Elements(P + "TextBlock").Single();
            Assert.Equal("DarkOrange", (string)block.Attribute("Foreground"));
            Assert.Equal("Bold", (string)block.Attribute("FontWeight"));
            Assert.Equal("Wrap", (string)block.Attribute("TextWrapping"));
            Assert.Equal("4,0,4,8", (string)block.Attribute("Margin"));
            XElement trigger = template.Descendants(P + "DataTrigger").Single();
            Assert.Equal("{Binding IsFailed}", (string)trigger.Attribute("Binding"));
            XElement setter = trigger.Elements(P + "Setter").Single();
            Assert.Equal("Firebrick", (string)setter.Attribute("Value"));
        }

        [Fact]
        public void Every_localised_key_both_views_use_exists()
        {
            var keys = new HashSet<string>(Strings.Keys);
            foreach (XDocument doc in new[] { Shell(), General() })
                foreach (Match m in Regex.Matches(doc.ToString(), @"\{v:Loc ([^}]+)\}"))
                    Assert.Contains(m.Groups[1].Value, keys);
        }

        // ------------------------------------------------------------ helpers

        /// <summary>The <c>{Binding Path}</c> first path segments on <paramref name="root"/> and below, not
        /// descending into <paramref name="skip"/> nor into a nested view whose DataContext is rebound.</summary>
        private static IEnumerable<string> BindingPaths(XElement root, XElement skip = null, bool includeSelf = true)
        {
            IEnumerable<XElement> Walk(XElement e, bool self)
            {
                if (e == skip) yield break;
                if (self) yield return e;
                // A child with its own DataContext binding starts a new context: only that attribute belongs here.
                foreach (XElement c in e.Elements())
                {
                    if (c != skip && c.Attribute("DataContext") != null) { yield return new XElement("ctx", c.Attribute("DataContext")); continue; }
                    foreach (XElement d in Walk(c, true)) yield return d;
                }
            }

            foreach (XElement e in Walk(root, includeSelf))
                foreach (XAttribute a in e.Attributes())
                {
                    if (!includeSelf && e == root) continue;
                    Match m = Regex.Match(a.Value, @"^\{Binding (?:Path=)?([A-Za-z_]\w*)");
                    if (m.Success) yield return m.Groups[1].Value;
                }
        }
    }
}

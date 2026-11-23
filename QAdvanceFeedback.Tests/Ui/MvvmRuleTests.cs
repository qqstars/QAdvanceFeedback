using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The project-wide MVVM rule (docs\architecture.md, "UI: MVVM"), pinned for the view folder:
    /// <list type="bullet">
    /// <item>a view's code-behind only wires: <c>InitializeComponent</c>, <c>DataContext = viewModel</c>,
    /// and handlers that call a view-model method - never an assignment to a control's property;</item>
    /// <item>no XAML event-handler attributes;</item>
    /// <item>the view models stay free of WPF and SimHub.</item>
    /// </list>
    /// </summary>
    public class MvvmRuleTests
    {
        private static string PluginDir()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(dir.FullName, "QAdvanceFeedback");
        }

        [Fact]
        public void View_code_behind_only_wires_the_view_model()
        {
            string[] files = Directory.GetFiles(Path.Combine(PluginDir(), "Ui", "Views"), "*.xaml.cs");
            Assert.NotEmpty(files);
            var report = files
                .Select(f => new { Name = Path.GetFileName(f), Offenders = CodeBehindOffenders(File.ReadAllText(f)) })
                .Where(r => r.Offenders.Count > 0)
                .Select(r => r.Name + ": " + string.Join(" | ", r.Offenders))
                .ToList();
            Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
        }

        /// <summary>
        /// The plugin page itself (docs\full-mvvm-plan.md 3.3, Wave 2): <c>Settings\SettingsControl</c> - the
        /// type SimHub's GetWPFSettingsControl returns - is a thin host with the same wiring-only code-behind
        /// and no XAML event handlers; everything else lives in SettingsPageView and its view models.
        /// </summary>
        [Fact]
        public void The_plugin_page_host_follows_the_same_rule()
        {
            string code = File.ReadAllText(Path.Combine(PluginDir(), "Settings", "SettingsControl.xaml.cs"));
            var offenders = CodeBehindOffenders(code);
            Assert.True(offenders.Count == 0, string.Join(" | ", offenders));
            Assert.Contains("DataContext = viewModel;", code);

            string xaml = File.ReadAllText(Path.Combine(PluginDir(), "Settings", "SettingsControl.xaml"));
            Assert.False(Regex.IsMatch(xaml, EventAttributePattern), "SettingsControl.xaml wires an event in XAML.");
            Assert.Contains("<v:SettingsPageView", xaml);

            // The old page's partials are gone for good.
            Assert.DoesNotContain(Directory.GetFiles(Path.Combine(PluginDir(), "Settings"), "SettingsControl.*.cs"),
                f => !f.EndsWith("SettingsControl.xaml.cs", StringComparison.Ordinal));
        }

        private const string EventAttributePattern =
            @"\s(Click|Checked|Unchecked|SelectionChanged|ValueChanged|Loaded|Unloaded|TextChanged|MouseLeftButtonDown|DropDownOpened|DropDownClosed|PreviewMouseDown|KeyDown|LostFocus|GotFocus)=";

        [Fact]
        public void The_code_behind_rule_rejects_event_wiring_methods_and_fields()
        {
            const string ok = "namespace N { /// <summary>x</summary>\n public partial class V : UserControl {\n" +
                              " public V() { InitializeComponent(); }\n" +
                              " public V(Vm viewModel) : this() { DataContext = viewModel; } } }";
            Assert.Empty(CodeBehindOffenders(ok));
            const string oneCtor = "namespace N { public partial class V : UserControl { public V(Vm viewModel)\n" +
                                   " { InitializeComponent(); DataContext = viewModel; } } }";
            Assert.Empty(CodeBehindOffenders(oneCtor));

            Assert.NotEmpty(CodeBehindOffenders("namespace N { public partial class V { public V(Vm viewModel) {" +
                " InitializeComponent(); DataContext = viewModel; Loaded += (s, e) => ViewGuard.Run(viewModel.Activate); } } }"));
            Assert.NotEmpty(CodeBehindOffenders("namespace N { public partial class V { public V() { InitializeComponent(); }" +
                " private void Go() => (DataContext as Vm)?.Activate(); } }"));
            Assert.NotEmpty(CodeBehindOffenders("namespace N { public partial class V { public V() { InitializeComponent(); }" +
                " private void Go() { Foo(); } } }"));
            Assert.NotEmpty(CodeBehindOffenders("namespace N { public partial class V { private int _x; public V() { InitializeComponent(); } } }"));
            Assert.NotEmpty(CodeBehindOffenders("namespace N { public partial class V { public V() { InitializeComponent(); Box.Text = \"a\"; } } }"));
            Assert.NotEmpty(CodeBehindOffenders("namespace N { public partial class V { public V(Vm vm) : base(vm) { InitializeComponent(); } } }"));
        }

        /// <summary>
        /// The tightened rule (docs\full-mvvm-plan.md 3.3): a view's class holds only constructors, and a
        /// constructor body holds only <c>InitializeComponent();</c> and <c>DataContext = &lt;expr&gt;;</c>,
        /// optionally chained with <c>: this()</c>. No <c>+=</c>, no other members with bodies, no fields.
        /// </summary>
        private static System.Collections.Generic.List<string> CodeBehindOffenders(string source)
        {
            var offenders = new System.Collections.Generic.List<string>();
            string code = Regex.Replace(source, @"//[^\n]*", "");
            code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);

            Match cls = Regex.Match(code, @"\bclass\s+(\w+)[^{]*\{");
            if (!cls.Success) { offenders.Add("no class"); return offenders; }
            string name = cls.Groups[1].Value;
            int start = cls.Index + cls.Length;
            int end = MatchingBrace(code, start - 1);
            string body = code.Substring(start, end - start);
            if (Regex.Matches(code, @"\bclass\s+\w+").Count > 1) offenders.Add("more than one class");

            // Split the class body into members at depth 0: "signature { body }" or "signature;".
            int i = 0;
            while (i < body.Length)
            {
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length) break;
                int brace = body.IndexOf('{', i), semi = body.IndexOf(';', i);
                if (brace < 0 || (semi >= 0 && semi < brace))
                {
                    offenders.Add("member without a constructor body: " + body.Substring(i, (semi < 0 ? body.Length : semi + 1) - i).Trim());
                    i = semi < 0 ? body.Length : semi + 1;
                    continue;
                }
                string signature = Regex.Replace(body.Substring(i, brace - i), @"\s+", " ").Trim();
                int close = MatchingBrace(body, brace);
                string ctorBody = body.Substring(brace + 1, close - brace - 1);
                i = close + 1;

                if (!Regex.IsMatch(signature, @"^(public|internal|private|protected)( \w+)? " + name + @"\([^)]*\)( : this\(\))?$"))
                {
                    offenders.Add("not a wiring-only constructor: " + signature);
                    continue;
                }
                foreach (string raw in ctorBody.Split(';'))
                {
                    string statement = Regex.Replace(raw, @"\s+", " ").Trim();
                    if (statement.Length == 0) continue;
                    if (statement == "InitializeComponent()") continue;
                    if (Regex.IsMatch(statement, @"^DataContext = [\w.]+$")) continue;
                    offenders.Add(statement);
                }
            }
            return offenders;
        }

        private static int MatchingBrace(string s, int open)
        {
            int depth = 0;
            for (int k = open; k < s.Length; k++)
            {
                if (s[k] == '{') depth++;
                else if (s[k] == '}' && --depth == 0) return k;
            }
            return s.Length;
        }

        [Fact]
        public void Views_declare_no_xaml_event_handlers()
        {
            foreach (string file in Directory.GetFiles(Path.Combine(PluginDir(), "Ui", "Views"), "*.xaml"))
            {
                string xaml = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(xaml, EventAttributePattern),
                    Path.GetFileName(file) + " wires an event in XAML; bind to a view-model property or command instead.");
            }
        }

        [Fact]
        public void View_models_reference_neither_WPF_nor_SimHub()
        {
            foreach (string folder in new[] { "Mvvm", "ViewModels" })
                foreach (string file in Directory.GetFiles(Path.Combine(PluginDir(), "Ui", folder), "*.cs"))
                {
                    string code = File.ReadAllText(file);
                    Assert.DoesNotContain("using System.Windows.Controls", code);
                    Assert.DoesNotContain("using System.Windows.Media", code);
                    Assert.DoesNotContain("using SimHub", code);
                    Assert.DoesNotContain("using MahApps", code);
                }
        }
    }
}

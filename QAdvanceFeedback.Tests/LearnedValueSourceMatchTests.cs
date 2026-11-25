using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A LEARNED CEILING BELONGS TO THE SOURCE IT WAS LEARNED FOR (owner-reported, 2026-10-02: Viper
    /// learns 13.5/11.5, switch to Raw, drive, switch back to Viper, and the page shows ~50/45).
    /// <para/>
    /// The 1 Hz push carries the identities the ENGINE is reading, which come from the APPLIED
    /// settings. The page's own identity can legitimately differ - the driver may have moved the
    /// dropdown without pressing Apply, and the page deliberately does not let the push overwrite that.
    /// The learned NUMBERS were taken regardless, so during that window Raw's ceiling (50-85 by nature)
    /// was displayed as Viper's.
    /// <para/>
    /// THE FIX IS DISPLAY-ONLY BY CONSTRUCTION, which matters because the previous attempt at this area
    /// stopped learning altogether. The four fields guarded here feed the "[Learned Value: x]" hint,
    /// the Auto readout, and the curve plot - no engine path reads them.
    /// </summary>
    public class LearnedValueSourceMatchTests
    {
        private readonly ITestOutputHelper _out;
        public LearnedValueSourceMatchTests(ITestOutputHelper output) { _out = output; }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            return dir.FullName;
        }

        // Wave 2 (docs\full-mvvm-plan.md): the page no longer receives a push it could mis-file - it ASKS the
        // plugin for one identity's learned values and gate (KeyDataPageViewModelTests
        // Learned_values_belong_to_the_source_they_were_learned_for_only,
        // Learned_hints_show_only_in_Manual_and_for_an_unknown_source_only_once_the_gate_opened,
        // A_game_arriving_re_resolves_the_per_game_slot pin the old push rules). What stays here is the
        // display-only guarantee.

        [Fact]
        public void No_engine_path_reads_the_display_only_learned_values()
        {
            // The regression guard: the display reads stay confined to the UI hosts, so a change there can
            // never reach learning or output.
            string root = RepoRoot();
            foreach (string file in Directory.GetFiles(Path.Combine(root, "QAdvanceFeedback"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                if (file.Contains("\\obj\\") || file.Contains("\\bin\\")) continue;
                string name = Path.GetFileName(file);
                if (name == "PluginPageHost.cs" || name == "PluginPanelHost.cs" || name.StartsWith("QAdvanceFeedback.", System.StringComparison.Ordinal)) continue;

                string text = File.ReadAllText(file);
                foreach (string member in new[] { "GetLearnedKeyDataForDisplay", "ManualGateReadyForDisplay" })
                    Assert.False(text.Contains(member), $"{name} reads {member} - display only, the UI hosts' alone");
            }
            // ...and the plugin itself only defines them: no frame-loop path calls them.
            string plugin = File.ReadAllText(Path.Combine(root, "QAdvanceFeedback", "QAdvanceFeedback.cs"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(plugin, "GetLearnedKeyDataForDisplay\\("));
            Assert.DoesNotContain("ManualGateReadyForDisplay(", plugin);
        }

        [Fact]
        public void The_shipped_version_is_1_2_1_0()
        {
            // 1.2.1.0: per-wheel slip-angle sources from QAdvanceFeedback.SlipAngleSource.json.
            string csproj = File.ReadAllText(Path.Combine(
                RepoRoot(), "QAdvanceFeedback", "QAdvanceFeedback.csproj"));
            Assert.Contains("<AssemblyVersion>1.2.1.0</AssemblyVersion>", csproj);
            Assert.Contains("<FileVersion>1.2.1.0</FileVersion>", csproj);
        }
    }
}

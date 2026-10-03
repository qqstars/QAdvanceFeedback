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

        private static string CodeBehind() => File.ReadAllText(Path.Combine(
            RepoRoot(), "QAdvanceFeedback", "Settings", "SettingsControl.xaml.cs"));

        private static string PushBody()
        {
            string code = CodeBehind();
            int start = code.IndexOf("public void UpdateLearnedKeyDataPoints", System.StringComparison.Ordinal);
            Assert.True(start > 0, "UpdateLearnedKeyDataPoints not found");
            int end = code.IndexOf("\n        /// <summary>", start, System.StringComparison.Ordinal);
            if (end < 0) end = code.Length;
            return code.Substring(start, end - start);
        }

        [Fact]
        public void The_pushed_values_are_only_taken_when_the_identity_matches_the_page()
        {
            string body = PushBody();
            _out.WriteLine(body);

            Assert.Contains("string.Equals(lockSourceIdentity, _currentLockSource", body);
            Assert.Contains("string.Equals(slipSourceIdentity, _currentSlipSource", body);

            // Every one of the six learned fields is gated, not just SMax - a mismatched S90/S75 would
            // put the other source's lower anchors on screen just as wrongly.
            foreach (string field in new[]
                     {
                         "_lockLearnedSMax = lockMatches", "_lockLearnedS90 = lockMatches",
                         "_lockLearnedS75 = lockMatches", "_slipLearnedSMax = slipMatches",
                         "_slipLearnedS90 = slipMatches", "_slipLearnedS75 = slipMatches",
                     })
                Assert.Contains(field, body);
        }

        [Fact]
        public void The_manual_gate_is_gated_by_the_same_match()
        {
            // "This source has learned enough to seed a manual value" says nothing about a source the
            // page is not showing, and it decides whether the learned hint is offered at all.
            string body = PushBody();
            Assert.Contains("_lockManualLive = lockMatches && lockManualLive", body);
            Assert.Contains("_slipManualLive = slipMatches && slipManualLive", body);
        }

        [Fact]
        public void The_game_id_is_still_taken_unconditionally()
        {
            // The game can ONLY come from the plugin, and it is not source-specific - gating it would
            // reintroduce the Per-Game and Viper-support defects fixed on 2026-09-30.
            string body = PushBody();
            Assert.Contains("_currentGameId = gameId;", body);
            Assert.DoesNotContain("_currentGameId = lockMatches", body);
        }

        [Fact]
        public void No_engine_path_reads_the_display_only_learned_fields()
        {
            // The regression guard. These fields must stay confined to the settings page, so a change
            // here can never reach learning or output.
            string root = RepoRoot();
            foreach (string file in Directory.GetFiles(Path.Combine(root, "QAdvanceFeedback"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                if (file.Contains("\\obj\\") || file.Contains("\\bin\\")) continue;
                if (Path.GetFileName(file) == "SettingsControl.xaml.cs") continue;

                string text = File.ReadAllText(file);
                foreach (string field in new[] { "_lockLearnedSMax", "_slipLearnedSMax", "_lockManualLive", "_slipManualLive" })
                    Assert.False(text.Contains(field),
                        $"{Path.GetFileName(file)} reads {field} - these are display-only fields");
            }
        }

        [Fact]
        public void The_shipped_version_is_1_1_0_1()
        {
            string csproj = File.ReadAllText(Path.Combine(
                RepoRoot(), "QAdvanceFeedback", "QAdvanceFeedback.csproj"));
            Assert.Contains("<AssemblyVersion>1.1.0.1</AssemblyVersion>", csproj);
            Assert.Contains("<FileVersion>1.1.0.1</FileVersion>", csproj);
        }
    }
}

using System.IO;
using QAdvanceFeedback.Core.Viper;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE SHIPPED GAME LIST IS THIS PROJECT'S RECORD, NOT A PREFERENCE (owner, 2026-10-01: "when in
    /// the F125 game, which is the internal supported game, it will still shows the Remove from Support
    /// List button which is INCORRECT. ONLY the game not in the internal support lists will display the
    /// button").
    /// <para/>
    /// Offering Remove on a shipped title produced a trap: removing F12025 dropped the channel to Raw,
    /// and re-adding it did not restore anything useful. The file now only ever ADDS to what ships.
    /// </summary>
    public class ShippedGameListTests
    {
        private readonly ITestOutputHelper _out;
        public ShippedGameListTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void A_shipped_title_is_supported_even_if_the_file_does_not_list_it()
        {
            // The self-repair for a document that already had a shipped title removed - reachable with
            // the old button, and otherwise unfixable once the button is hidden.
            var stripped = new ViperDocument
            {
                Version = ViperDocument.CurrentVersion,
                SupportedGames = new System.Collections.Generic.List<string>(),
            };

            Assert.True(ViperSupportedGames.IsShipped("F12025"));
            Assert.True(ViperSupportedGames.IsSupported("F12025", stripped.SupportedGames));
        }

        [Fact]
        public void A_shipped_title_cannot_be_removed_from_the_document()
        {
            ViperDocument shipped = ViperSupportedGames.CreateShippedDocument();
            ViperDocument after = ViperSupportedGames.WithoutGame(shipped, "F12025");

            // It may be dropped from the FILE, but it stays supported - so the operation can never
            // leave the plugin unable to read a title viper4gh actually handles.
            Assert.True(ViperSupportedGames.IsSupported("F12025", after?.SupportedGames));
        }

        [Fact]
        public void A_driver_added_title_can_still_be_added_and_removed()
        {
            // The case the button exists for is untouched.
            ViperDocument doc = ViperSupportedGames.CreateShippedDocument();
            Assert.False(ViperSupportedGames.IsShipped("FH6"));
            Assert.False(ViperSupportedGames.IsSupported("FH6", doc.SupportedGames));

            ViperDocument added = ViperSupportedGames.WithGame(doc, "FH6");
            Assert.NotSame(doc, added);
            Assert.True(ViperSupportedGames.IsSupported("FH6", added.SupportedGames));

            ViperDocument removed = ViperSupportedGames.WithoutGame(added, "FH6");
            Assert.NotSame(added, removed);
            Assert.False(ViperSupportedGames.IsSupported("FH6", removed.SupportedGames));
        }

        [Fact]
        public void Adding_a_shipped_title_is_a_no_op_so_nothing_is_written()
        {
            // Reference equality is how the caller decides whether to save.
            ViperDocument doc = ViperSupportedGames.CreateShippedDocument();
            Assert.Same(doc, ViperSupportedGames.WithGame(doc, "F12025"));
        }

        [Fact]
        public void The_button_is_hidden_for_a_shipped_title()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings")))
                dir = dir.Parent;
            string code = File.ReadAllText(Path.Combine(
                dir.FullName, "QAdvanceFeedback", "Settings", "SettingsControl.xaml.cs"));

            Assert.Contains("!Core.Viper.ViperSupportedGames.IsShipped(_currentGameId)", code);
        }
    }
}

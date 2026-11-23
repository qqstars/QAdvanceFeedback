using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The key-data identity tier (final review, finding 1): pausing / a menu clears the debounced
    /// broadcaster tier to Unknown at once (right for the warning), but the identity the tab, the panel
    /// and the hub read and write must stay on the game's in-session tier - otherwise an edit made while
    /// paused on a fallback game lands in the Native slot and never applies on resume.
    /// </summary>
    public class SlipAngleIdentityTierTests
    {
        private static SlipAngleWindow W(double min, double best, double max) => new SlipAngleWindow(min, best, max);

        /// <summary>Drives the broadcaster + identity tier exactly like the plugin's PublishSlipAngle.</summary>
        private sealed class Pipeline
        {
            public readonly NativenessBroadcaster Broadcaster = new NativenessBroadcaster(null);
            public readonly SlipAngleIdentityTier Identity = new SlipAngleIdentityTier();

            public void Running(string game, SourceNativeness tier, int frames = 60)
            {
                for (int i = 0; i < frames; i++)
                {
                    Broadcaster.Update(tier, SourceNativeness.Unknown, 1.0 / 60.0);
                    Identity.Observe(game, Broadcaster.Lock);
                }
            }

            public void Paused()
            {
                Broadcaster.Clear();
                Identity.NoteCleared();
            }
        }

        [Fact]
        public void Pause_keeps_the_fallback_identity_while_the_warning_tier_drops_to_unknown()
        {
            var p = new Pipeline();
            p.Running("G", SourceNativeness.Fallback);
            Assert.Equal(SourceNativeness.Fallback, p.Broadcaster.Lock);
            Assert.Equal(SourceNativeness.Fallback, p.Identity.Tier);

            p.Paused();
            Assert.Equal(SourceNativeness.Unknown, p.Broadcaster.Lock);   // the warning tier clears at once
            Assert.Equal(SourceNativeness.Fallback, p.Identity.Tier);     // the identity does not

            // Resume: the broadcaster re-debounces (Unknown for 0.5 s) - the identity never flickers.
            for (int i = 0; i < 60; i++)
            {
                p.Running("G", SourceNativeness.Fallback, frames: 1);
                Assert.Equal(SourceNativeness.Fallback, p.Identity.Tier);
            }
        }

        [Fact]
        public void A_confirmed_in_session_tier_change_moves_the_identity()
        {
            var p = new Pipeline();
            p.Running("G", SourceNativeness.Fallback);
            p.Running("G", SourceNativeness.Native);
            Assert.Equal(SourceNativeness.Native, p.Identity.Tier);
            p.Running("G", SourceNativeness.Unknown);   // confirmed, while running
            Assert.Equal(SourceNativeness.Unknown, p.Identity.Tier);
        }

        [Fact]
        public void A_game_switch_resets_the_identity()
        {
            var p = new Pipeline();
            p.Running("G", SourceNativeness.Fallback);
            p.Paused();

            p.Identity.Reset();                           // the plugin's game-switch hook
            Assert.Equal(SourceNativeness.Unknown, p.Identity.Tier);
            p.Running("H", SourceNativeness.Native);
            Assert.Equal(SourceNativeness.Native, p.Identity.Tier);

            // A new game id is a switch even without the explicit hook.
            p.Paused();
            p.Running("K", SourceNativeness.Unknown, frames: 1);
            Assert.Equal(SourceNativeness.Unknown, p.Identity.Tier);
        }

        [Fact]
        public void Tab_edit_while_paused_commits_to_the_fallback_slot()
        {
            var host = new FakeSlipAngleHost { GameId = "G" };
            var ticker = new ManualTicker();
            var p = new Pipeline();
            p.Running("G", SourceNativeness.Fallback);
            host.IdentityTier = p.Identity.Tier;
            host.Settings.SlipAngle.KeyData.SetAutoGenerate(SlipAngleFrameProcessor.FallbackIdentity, false);
            host.Settings.SlipAngle.KeyData.TryCommitManual("G", SlipAngleFrameProcessor.FallbackIdentity, 3.0, 6.0, 8.0);

            var vm = new SlipAngleTabViewModel(host, ticker);
            vm.Load(host.Settings.SlipAngle);
            vm.Activate();

            p.Paused();
            host.IdentityTier = p.Identity.Tier;
            host.KeyDataRevision++;                      // any refresh while paused re-reads the identity
            ticker.Advance(TimeSpan.FromSeconds(1));

            Assert.False(vm.Auto);                       // still the Fallback slot (Native is Auto by default)
            Assert.Equal(new double?[] { 3.0, 6.0, 8.0 }, new[] { vm.Min, vm.Best, vm.Max });

            vm.Min = 4.0; vm.Best = 7.0; vm.Max = 9.0;
            ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.True(host.Settings.SlipAngle.KeyData.TryGetSlot("G", SlipAngleFrameProcessor.FallbackIdentity, out SlipAngleWindow w));
            Assert.Equal(W(4.0, 7.0, 9.0), w);
            Assert.False(host.Settings.SlipAngle.KeyData.TryGetSlot("G", SlipAngleFrameProcessor.NativeIdentity, out _));
        }

        [Fact]
        public void Hub_write_while_paused_targets_the_fallback_slot_and_a_game_switch_resets_it()
        {
            var s = new QAdvanceFeedbackSettings();
            string game = "G";
            var p = new Pipeline();
            var hub = new SettingsHub(() => s, _ => { }, () => game, () => p.Identity.Tier);
            p.Running("G", SourceNativeness.Fallback);
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel));

            p.Paused();
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel));
            Assert.False(s.SlipAngle.KeyData.GetAutoGenerate(SlipAngleFrameProcessor.FallbackIdentity));
            Assert.True(s.SlipAngle.KeyData.GetAutoGenerate(SlipAngleFrameProcessor.NativeIdentity));
            Assert.True(s.SlipAngle.KeyData.TryGetSlot("G", SlipAngleFrameProcessor.FallbackIdentity, out _));
            Assert.False(s.SlipAngle.KeyData.TryGetSlot("G", SlipAngleFrameProcessor.NativeIdentity, out _));

            p.Identity.Reset();
            game = "H";
            Assert.Equal(SlipAngleFrameProcessor.NativeIdentity, SettingsHub.SlipAngleIdentity(s.SlipAngle, p.Identity.Tier));
        }
    }
}

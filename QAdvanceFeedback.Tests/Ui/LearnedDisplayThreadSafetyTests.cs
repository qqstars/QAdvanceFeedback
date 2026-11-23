using System;
using System.IO;
using System.Threading;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// OWNER-REPORTED, 2026-11-22: same source, FH6 learned some data, then F1 (never run) in the same session - after
    /// a full lap Slip and Slip Angle moved on but the Wheel Lock SMax never changed and its LED stayed "orange, 1%";
    /// restarting SimHub showed the learned SMax at 100%. The settings page and the ShakeIt panels read the learners
    /// from the UI thread while the data thread writes them: unsynchronised, a read walking a histogram the frame is
    /// filling throws "collection was modified" on practically every read while Lock learns, and the maturity read fell
    /// back to its first cached answer. The fix: every frame and every UI read take one engine lock.
    /// </summary>
    public class LearnedDisplayThreadSafetyTests
    {
        private const string Source = "S";

        private static void LearnFh6(NormalizedWheelLockSlipEngine engine)
        {
            var rng = new Random(1);
            for (int i = 0; i < 3000; i++) engine.LockScaleLearner.ObserveAtPhysicalLimit("FH6", "C1", Source, 60 + rng.NextDouble() * 20);
        }

        /// <summary>The owner's sequence, with the data thread learning F1 while the UI reads the display. Without a
        /// shared lock the reads fail; with the lock the plugin now uses, every read succeeds and follows the learning.</summary>
        [Fact]
        public void Reading_the_display_while_lock_learns_a_new_game_fails_without_the_shared_lock_and_follows_it_with()
        {
            Assert.True(Run(useLock: false).Failures > 0);   // the defect, reproduced

            (int failures, int reads, double first, double last) = Run(useLock: true);
            Assert.Equal(0, failures);
            Assert.True(reads > 0);
            Assert.True(last > first);   // the LED follows the learning live - it does not freeze at its first answer
        }

        private static (int Failures, int Reads, double First, double Last) Run(bool useLock)
        {
            var engine = new NormalizedWheelLockSlipEngine();
            LearnFh6(engine);
            var gate = new object();
            int stop = 0, written = 0;
            var writer = new Thread(() =>
            {
                var r = new Random(2);
                while (Volatile.Read(ref stop) == 0)
                {
                    if (useLock) { lock (gate) Observe(engine, r); Thread.Sleep(1); }   // about frame rate
                    else Observe(engine, r);
                    Interlocked.Increment(ref written);
                }
            });
            writer.Start();
            int failures = 0, reads = 0;
            double first = double.NaN, last = double.NaN;
            DateTime until = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < until && (useLock || failures < 200))
            {
                try
                {
                    KeyDataMaturity m;
                    if (useLock)
                    {
                        lock (gate)
                        {
                            m = KeyDataPanelModel.LearnedKeyDataMaturity(engine, true, "F1", "C9", Source);
                            KeyDataPanelModel.LearnedKeyData(engine, true, "F1", "C9", Source);
                        }
                    }
                    else
                    {
                        m = KeyDataPanelModel.LearnedKeyDataMaturity(engine, true, "F1", "C9", Source);
                        KeyDataPanelModel.LearnedKeyData(engine, true, "F1", "C9", Source);
                    }
                    if (m != null && Volatile.Read(ref written) > 0)
                    {
                        if (double.IsNaN(first)) first = m.SMax;
                        last = m.SMax;
                    }
                    reads++;
                }
                catch (InvalidOperationException) { failures++; }
                Thread.Sleep(useLock ? 5 : 0);   // the UI reads about once a second; the locked run samples faster
            }
            Volatile.Write(ref stop, 1);
            writer.Join();
            return (failures, reads, first, last);
        }

        private static void Observe(NormalizedWheelLockSlipEngine engine, Random r)
            => engine.LockScaleLearner.ObserveAtPhysicalLimit("F1", "C9", Source, 50 + r.NextDouble() * 30);

        /// <summary>The plugin itself: the frame and every display read share the one engine lock, and the stale
        /// "last answer" cache that froze the LED is gone.</summary>
        [Fact]
        public void The_plugin_runs_each_frame_and_every_display_read_under_one_engine_lock()
        {
            string plugin = Source_("QAdvanceFeedback.cs");
            string page = Source_("QAdvanceFeedback.SettingsPage.cs");
            Assert.Contains("lock (_engineLock) DataUpdateUnderEngineLock(pluginManager, ref data);", plugin);
            Assert.Equal(2, Count(Between(plugin, "public KeyDataTriple GetLearnedKeyDataForDisplay", "public KeyDataMaturity GetLearnedKeyDataMaturityForDisplay")
                + Between(plugin, "public KeyDataMaturity GetLearnedKeyDataMaturityForDisplay", "private readonly NormalizedWheelLockSlipEngine"), "lock (_engineLock)"));
            Assert.Contains("lock (_engineLock)", Between(page, "public bool ManualGateReadyForDisplay", "}\n    }"));
            Assert.DoesNotContain("_maturityCache", plugin);
        }

        /// <summary>DISPLAY FOLLOWS THE KEY AT ONCE (owner, 2026-11-22): the game / car SimHub reports moves the key data
        /// display before the learning gate - at the menu or paused - so a cold key shows its borrowed half LED
        /// immediately. Display only: it sits before the gate and touches no learner.</summary>
        [Fact]
        public void The_display_context_follows_the_reported_game_before_the_learning_gate()
        {
            string plugin = Source_("QAdvanceFeedback.cs");
            int follow = plugin.IndexOf("FollowKeyDataDisplayContext(_currentGameId, data.NewData?.CarId);", StringComparison.Ordinal);
            int gate = plugin.IndexOf("if (!data.GameRunning || data.GamePaused || data.GameInMenu", StringComparison.Ordinal);
            Assert.True(follow > 0 && gate > follow);
            string body = Between(plugin, "private void FollowKeyDataDisplayContext", "public KeyDataMaturity GetLearnedKeyDataMaturityForDisplay");
            Assert.DoesNotContain("_normalizedEngine", body);
            Assert.DoesNotContain("Observe", body);
        }

        private static string Between(string text, string from, string to)
        {
            int a = text.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a >= 0, from);
            int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            return b < 0 ? text.Substring(a) : text.Substring(a, b - a);
        }

        private static int Count(string text, string value)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0) { n++; i += value.Length; }
            return n;
        }

        private static string Source_(string file)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", file)).Replace("\r\n", "\n");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE BACKGROUND FLUSH MUST NOT QUEUE WITHOUT BOUND, AND MUST NOT OUTLIVE THE FINAL WRITE.
    /// <para/>
    /// <c>FlushTick</c> fired <c>Task.Run</c> every interval with nothing preventing overlap. Each
    /// queued task pinned a full document clone AND the multi-megabyte JSON serialised from it - the
    /// owner's real parameters file is 3.25 MB - and they then queued behind each other on the file
    /// lock. One write slower than the interval (a multi-megabyte file being scanned by antivirus on
    /// every save will do it) and the queue grows for as long as the session lasts. That is the only
    /// mechanism in this plugin capable of exhausting a machine, which is what the owner reported.
    /// <para/>
    /// The second defect in the same place: a snapshot queued just before <c>Flush</c> could land
    /// AFTER it, silently reverting the end of a session with no crash and no error.
    /// </summary>
    public class RuntimeStoreFlushSafetyTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public RuntimeStoreFlushSafetyTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "qaf-flush-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Path_() => Path.Combine(_dir, "QAdvanceFeedback.Parameters.json");

        private static Dictionary<string, ScaleLearnerState> Learners(string key, double ceiling)
            => new Dictionary<string, ScaleLearnerState>
            {
                [key] = new ScaleLearnerState { ColdCeiling = ceiling },
            };

        [Fact]
        public void The_final_flush_wins_over_anything_the_timer_queued()
        {
            // The ordering guarantee Plugin.End depends on: Flush waits for an in-flight background
            // write before taking its own snapshot, so the last thing on disk is the newest state.
            using (var store = new RuntimeStore(Path_(), flushInterval: TimeSpan.FromMilliseconds(15)))
            {
                for (int i = 1; i <= 60; i++)
                {
                    store.SaveLockScaleLearners(Learners("k", i));
                    Thread.Sleep(2);
                }

                store.SaveLockScaleLearners(Learners("k", 999.0));
                store.Flush();
            }

            string json = File.ReadAllText(Path_());
            Assert.Contains("999", json);
            _out.WriteLine(json.Length + " bytes written");
        }

        [Fact]
        public void Disposing_leaves_no_temporary_file_and_a_complete_document()
        {
            using (var store = new RuntimeStore(Path_(), flushInterval: TimeSpan.FromMilliseconds(10)))
            {
                for (int i = 1; i <= 100; i++) store.SaveLockScaleLearners(Learners("k", i));
                Thread.Sleep(120);   // let several background ticks fire
                store.SaveLockScaleLearners(Learners("k", 1234.0));
                store.Flush();
            }

            Assert.False(File.Exists(Path_() + ".tmp"), "a temporary file was left behind");
            string json = File.ReadAllText(Path_());
            Assert.StartsWith("{", json.TrimStart());
            Assert.EndsWith("}", json.TrimEnd());
            Assert.Contains("1234", json);
        }

        [Fact]
        public void A_skipped_tick_never_loses_the_data_it_skipped()
        {
            // The claim is taken BEFORE SnapshotIfDirty, so skipping leaves the dirty flag set and the
            // next write carries the newer state. Taking it the other way round would clear the flag
            // and drop the snapshot on the floor.
            string source = StoreSource();
            int claim = source.IndexOf("Interlocked.CompareExchange(ref _backgroundWriteInFlight", StringComparison.Ordinal);
            int snapshot = source.IndexOf("_cache.SnapshotIfDirty()", claim, StringComparison.Ordinal);

            Assert.True(claim > 0, "the in-flight claim is missing");
            Assert.True(snapshot > claim,
                "the in-flight claim must be taken BEFORE the snapshot, or a skipped tick loses data");
        }

        [Fact]
        public void The_claim_is_released_on_every_path_including_failure()
        {
            // A leaked claim would silently stop all background flushing for the rest of the session.
            string source = StoreSource();
            int tick = source.IndexOf("private void FlushTick", StringComparison.Ordinal);
            int end = source.IndexOf("\n        private void WriteAtomic", tick, StringComparison.Ordinal);
            string body = source.Substring(tick, end - tick);

            Assert.Contains("finally { Interlocked.Exchange(ref _backgroundWriteInFlight, 0); }", body);
            // ...and on the "nothing was dirty" early return too.
            Assert.Equal(2, CountOccurrences(body, "Interlocked.Exchange(ref _backgroundWriteInFlight, 0)"));
        }

        [Fact]
        public void Both_Flush_and_Dispose_wait_for_the_background_write()
        {
            string source = StoreSource();

            foreach (string method in new[] { "public void Flush()", "public void Dispose()" })
            {
                int start = source.IndexOf(method, StringComparison.Ordinal);
                Assert.True(start > 0, method + " not found");
                int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
                Assert.Contains("WaitForBackgroundWrite()", source.Substring(start, end - start));
            }

            // Bounded, so a wedged disk degrades rather than hanging the host's shutdown.
            Assert.Contains("inFlight.Wait(BackgroundWriteWaitTimeout)", source);
        }

        private static int CountOccurrences(string text, string value)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0) { n++; i += value.Length; }
            return n;
        }

        private static string StoreSource()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "RuntimeStore.cs"));
        }
    }
}

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
        public void The_timer_is_one_shot_and_re_armed_only_after_its_write_finishes()
        {
            // Owner's pattern (2026-11-21): due 5 s, period infinite; the tick writes inside try/finally and the
            // finally re-arms it - so writes never overlap and the next one counts from the end of the last.
            string source = StoreSource();
            Assert.Contains("new Timer(_ => FlushTick(), null, _interval, Timeout.InfiniteTimeSpan)", source);
            int tick = source.IndexOf("private void FlushTick", StringComparison.Ordinal);
            int end = source.IndexOf("\n        private void WriteIfDirty", tick, StringComparison.Ordinal);
            string body = source.Substring(tick, end - tick);
            int write = body.IndexOf("WriteIfDirty();", StringComparison.Ordinal);
            int rearm = body.IndexOf("_timer?.Change(_interval, Timeout.InfiniteTimeSpan)", StringComparison.Ordinal);
            int fin = body.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(write > 0 && fin > write && rearm > fin, "the re-arm must sit in the finally after the write");
            Assert.DoesNotContain("Task.Run", source);
        }

        [Fact]
        public void A_failed_write_leaves_everything_dirty_for_the_next_tick()
        {
            string source = StoreSource();
            int start = source.IndexOf("private void WriteIfDirty", StringComparison.Ordinal);
            int end = source.IndexOf("\n        /// <summary>", start, StringComparison.Ordinal);
            string body = source.Substring(start, end - start);
            Assert.Contains("lock (_writeLock)", body);
            Assert.Contains("_cache.MarkDirty();", body);
            Assert.Contains("_fileStale = true;", body);
        }

        [Fact]
        public void Shutdown_stops_and_disposes_the_timer_then_writes_the_last_time_and_hooks_the_host()
        {
            string source = StoreSource();
            int start = source.IndexOf("private void Shutdown()", StringComparison.Ordinal);
            int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
            string body = source.Substring(start, end - start);
            int stop = body.IndexOf("_timer.Change(Timeout.Infinite, Timeout.Infinite)", StringComparison.Ordinal);
            int dispose = body.IndexOf("_timer.Dispose(done)", StringComparison.Ordinal);
            int write = body.IndexOf("WriteIfDirty();", StringComparison.Ordinal);
            Assert.True(stop > 0 && dispose > stop && write > dispose, "stop, then dispose (waiting), then the last write");
            Assert.Contains("AppDomain.CurrentDomain.DomainUnload += OnHostUnloading;", source);
            Assert.Contains("AppDomain.CurrentDomain.ProcessExit += OnHostUnloading;", source);
        }

        [Fact]
        public void Dispose_alone_writes_the_last_seconds()
        {
            using (var store = new RuntimeStore(Path_(), flushInterval: TimeSpan.FromMinutes(10)))
                store.SaveLockScaleLearners(Learners("k", 4321.0));   // no Flush

            Assert.Contains("4321", File.ReadAllText(Path_()));
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

using System;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The config file is never written on the data thread (owner, 2026-11-25): a request arms a one-shot timer for
    /// 100 ms (a burst ends in one write), the tick writes under one lock (a second tick waits for the first), and the
    /// finally re-arms for 5 s - or 100 ms again when another request came in meanwhile.
    /// </summary>
    public sealed class ConfigSaveSchedulerTests
    {
        private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        [Fact]
        public void A_request_returns_at_once_and_the_write_happens_on_another_thread_about_100_ms_later()
        {
            int writerThread = 0, writes = 0;
            var watch = new Stopwatch();
            long writtenAfterMs = -1;
            using (var saver = new ConfigSaveScheduler(() =>
            {
                writerThread = Thread.CurrentThread.ManagedThreadId;
                writtenAfterMs = watch.ElapsedMilliseconds;
                Interlocked.Increment(ref writes);
            }))
            {
                watch.Start();
                saver.RequestSave();
                long requestCostMs = watch.ElapsedMilliseconds;
                Assert.True(requestCostMs < 50, requestCostMs + " ms");
                Assert.Equal(0, writes);

                Assert.True(WaitFor(() => writes == 1, TimeSpan.FromSeconds(3)));
                Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, writerThread);
                Assert.True(writtenAfterMs >= 80, writtenAfterMs + " ms");
            }
        }

        [Fact]
        public void A_burst_of_requests_closer_than_the_delay_ends_in_one_write()
        {
            int writes = 0;
            using (var saver = new ConfigSaveScheduler(() => Interlocked.Increment(ref writes),
                coalesce: TimeSpan.FromMilliseconds(100), idle: TimeSpan.FromMinutes(10)))
            {
                for (int i = 0; i < 10; i++)
                {
                    saver.RequestSave();
                    Thread.Sleep(10);   // a surface flickering every 10 ms
                }
                Assert.True(WaitFor(() => writes >= 1, TimeSpan.FromSeconds(3)));
                Thread.Sleep(300);
                Assert.Equal(1, writes);
            }
        }

        [Fact]
        public void Requests_further_apart_than_the_delay_each_write()
        {
            int writes = 0;
            using (var saver = new ConfigSaveScheduler(() => Interlocked.Increment(ref writes),
                coalesce: TimeSpan.FromMilliseconds(30), idle: TimeSpan.FromMinutes(10)))
            {
                saver.RequestSave();
                Assert.True(WaitFor(() => writes == 1, TimeSpan.FromSeconds(3)));
                saver.RequestSave();
                Assert.True(WaitFor(() => writes == 2, TimeSpan.FromSeconds(3)));
            }
        }

        [Fact]
        public void A_second_write_waits_for_the_first_and_none_is_lost()
        {
            int inside = 0, overlapped = 0, writes = 0;
            using (var saver = new ConfigSaveScheduler(() =>
            {
                if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlapped);
                Thread.Sleep(150);   // a slow disk
                Interlocked.Decrement(ref inside);
                Interlocked.Increment(ref writes);
            }, coalesce: TimeSpan.FromMilliseconds(20), idle: TimeSpan.FromMinutes(10)))
            {
                saver.RequestSave();
                Assert.True(WaitFor(() => Volatile.Read(ref inside) == 1, TimeSpan.FromSeconds(3)));
                saver.RequestSave();   // while the first write is still running
                Assert.True(WaitFor(() => writes == 2, TimeSpan.FromSeconds(3)));
                Assert.Equal(0, overlapped);
                Assert.False(saver.IsRequested);
            }
        }

        [Fact]
        public void A_failed_write_stays_requested_and_is_retried()
        {
            int attempts = 0;
            using (var saver = new ConfigSaveScheduler(() =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("Collection was modified");
            }, coalesce: TimeSpan.FromMilliseconds(20), idle: TimeSpan.FromMilliseconds(50)))
            {
                saver.RequestSave();
                Assert.True(WaitFor(() => saver.WritesDone == 1, TimeSpan.FromSeconds(3)));
                Assert.Equal(2, attempts);
            }
        }

        [Fact]
        public void An_idle_tick_writes_nothing()
        {
            int writes = 0;
            using (var saver = new ConfigSaveScheduler(() => Interlocked.Increment(ref writes),
                coalesce: TimeSpan.FromMilliseconds(10), idle: TimeSpan.FromMilliseconds(20)))
            {
                saver.RequestSave();
                Assert.True(WaitFor(() => writes == 1, TimeSpan.FromSeconds(3)));
                Thread.Sleep(200);   // several idle ticks
                Assert.Equal(1, writes);
            }
        }

        [Fact]
        public void SaveNow_writes_on_the_caller_and_Dispose_writes_a_pending_request()
        {
            int writerThread = 0, writes = 0;
            var saver = new ConfigSaveScheduler(() =>
            {
                writerThread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref writes);
            }, coalesce: TimeSpan.FromMinutes(10), idle: TimeSpan.FromMinutes(10));
            saver.SaveNow();
            Assert.Equal(1, writes);
            Assert.Equal(Thread.CurrentThread.ManagedThreadId, writerThread);

            saver.RequestSave();   // its tick is ten minutes away
            saver.Dispose();
            Assert.Equal(2, writes);
            saver.RequestSave();   // after Dispose: nothing is armed any more
            Assert.Equal(2, writes);
        }
    }
}

using System;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    public static class FrameSnapshot
    {
        /// <summary>500 ms, in <see cref="TimeSpan"/> ticks. Older frames read as "nothing".</summary>
        public const long StaleTicks = 500 * TimeSpan.TicksPerMillisecond;

        /// <summary>Monotonic "now" in <see cref="TimeSpan"/> ticks (Stopwatch-based).</summary>
        public static long NowTicks() =>
            (long)(System.Diagnostics.Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency));
    }

    /// <summary>
    /// An immutable value plus the time it was produced. Handed between SimHub's plugin thread and
    /// ShakeIt's evaluation by reference swap (Volatile.Write/Read), so readers never see a torn value.
    /// </summary>
    public sealed class FrameSnapshot<T>
    {
        public FrameSnapshot(T value, long timestampTicks)
        {
            Value = value;
            TimestampTicks = timestampTicks;
        }

        public T Value { get; }
        public long TimestampTicks { get; }

        /// <summary>True when <paramref name="nowTicks"/> is within <paramref name="staleTicks"/> after
        /// the timestamp. A timestamp in the future (clock went backwards) is not fresh.</summary>
        public bool IsFresh(long nowTicks, long staleTicks)
        {
            long age = nowTicks - TimestampTicks;
            return age >= 0 && age <= staleTicks;
        }
    }
}

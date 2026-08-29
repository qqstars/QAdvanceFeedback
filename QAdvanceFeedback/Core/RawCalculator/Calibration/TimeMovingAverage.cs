using System.Collections.Generic;

namespace QAdvanceFeedback.Core.RawCalculator.Calibration
{
    /// <summary>
    /// A fixed-size moving average - the per-gear wheel-speed-delta reference
    /// <c>WheelSlipEffect.GetWheelSpeedSlip</c> keeps.
    /// <para/>
    /// NOT DECOMPILED, AND THE ONLY PART OF THIS PORT THAT ISN'T. SimHub's own
    /// <c>TimeMovingAverage</c> lives in <c>WoteverCommon</c>, an assembly this project does not ship,
    /// so it cannot be read. Its public surface is known exactly, from how ShakeIt constructs and calls
    /// it:
    /// <code>
    ///   new TimeMovingAverage { MaxSamples = 1500.0, MaxTimeMs = 2000000000.0 }
    ///   average.Enqueue(value);   average.CurrentAverage;   average.Count;
    /// </code>
    /// <para/>
    /// WHY THE MISSING IMPLEMENTATION DOES NOT MATTER HERE. ShakeIt sets <c>MaxTimeMs</c> to two billion
    /// milliseconds - about 23 days - at this call site. That is unambiguously "never trim on time", so
    /// the only bound that can ever act is <c>MaxSamples</c>, and a plain 1500-sample moving average is
    /// exactly what ShakeIt gets. This type therefore implements the sample bound only, rather than
    /// half-implementing a time window that would never be exercised and could mislead a later reader.
    /// <para/>
    /// TWO RESIDUAL ASSUMPTIONS, both bounded:
    /// <list type="bullet">
    /// <item><see cref="CurrentAverage"/> is taken to be the arithmetic mean of the retained samples
    /// rather than a duration-weighted one. Those two coincide whenever sample spacing is uniform, and
    /// this is fed once per telemetry frame at a fixed rate, so they coincide here.</item>
    /// <item><see cref="Count"/> is taken to be the RETAINED count. Its only consumer tests
    /// <c>Count &gt; 10</c>, and retained-versus-cumulative cannot differ below 1500 samples, so the two
    /// readings are indistinguishable at that gate.</item>
    /// </list>
    /// </summary>
    public sealed class TimeMovingAverage
    {
        private readonly Queue<double> _samples = new Queue<double>();
        private double _sum;

        /// <summary>Maximum retained samples. Double rather than int to mirror SimHub's own property
        /// type, which is what the observed object initialiser assigns.</summary>
        public double MaxSamples { get; set; } = 1500.0;

        /// <summary>Carried so the object initialiser at the call site reads identically to SimHub's.
        /// Not acted upon - see this class's own remarks for why it provably cannot matter here.</summary>
        public double MaxTimeMs { get; set; } = 2000000000.0;

        public int Count => _samples.Count;

        /// <summary>Arithmetic mean of the retained samples, or 0 when empty. Zero rather than null
        /// because the consuming formula gates on <see cref="Count"/> before ever reading this.</summary>
        public double CurrentAverage => _samples.Count == 0 ? 0.0 : _sum / _samples.Count;

        public void Enqueue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            _samples.Enqueue(value);
            _sum += value;

            while (_samples.Count > MaxSamples) _sum -= _samples.Dequeue();

            // Re-derived whenever the queue empties - the one point at which the running sum must be
            // exactly zero, so accumulated floating-point drift cannot survive across a reset.
            if (_samples.Count == 0) _sum = 0.0;
        }
    }
}

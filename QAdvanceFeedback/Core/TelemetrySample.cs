using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>Plain, immutable implementation of <see cref="ITelemetrySample"/>. See
    /// <see cref="TelemetryFrame"/>'s remarks - same reasoning for living in Core.</summary>
    public sealed class TelemetrySample : ITelemetrySample
    {
        public ITelemetryFrame New { get; }
        public ITelemetryFrame Old { get; }
        public DateTime FrameTime { get; }
        public TimeSpan? Dt { get; }

        public TelemetrySample(ITelemetryFrame newFrame, ITelemetryFrame oldFrame, DateTime frameTime, TimeSpan? dt = null)
        {
            New = newFrame ?? TelemetryFrame.Empty;
            Old = oldFrame ?? TelemetryFrame.Empty;
            FrameTime = frameTime;
            Dt = dt;
        }
    }
}

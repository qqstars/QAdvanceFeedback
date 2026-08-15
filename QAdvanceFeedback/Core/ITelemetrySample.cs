using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// One telemetry sample: the current ("New") frame, the previous ("Old") frame, and when it was
    /// captured. The legacy lock/slip algorithm is fundamentally a two-frame comparison (old RPM vs
    /// new RPM, old gear vs new gear), so both frames are first-class here rather than the caller
    /// keeping its own "previous frame" bookkeeping - mirrors SimHub's own GameData.NewData/OldData
    /// shape (Layer 2 maps directly onto this), without this interface itself knowing SimHub exists.
    /// </summary>
    public interface ITelemetrySample
    {
        ITelemetryFrame New { get; }
        ITelemetryFrame Old { get; }

        /// <summary>
        /// Wall-clock time this sample was captured (SimHub: GameData.FrameTime). Always present -
        /// unlike the per-channel readings on <see cref="ITelemetryFrame"/>, SimHub itself always
        /// stamps a frame time, so this is a plain DateTime rather than nullable.
        /// </summary>
        DateTime FrameTime { get; }

        /// <summary>
        /// Elapsed time since the previous sample, if known. Null on the very first sample of a
        /// session (there is no previous FrameTime to subtract yet).
        /// </summary>
        TimeSpan? Dt { get; }
    }
}

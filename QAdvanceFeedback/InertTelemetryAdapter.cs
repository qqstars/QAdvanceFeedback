using GameReaderCommon;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback
{
    /// <summary>
    /// The PUBLIC fallback for <see cref="ITelemetryAdapter"/>, used only when
    /// <c>Private\QAdvanceFeedback\SimHubTelemetryAdapter.cs</c> (the withheld implementation) is not
    /// present in this build - see <c>..\Private\README.md</c>.
    /// <para/>
    /// Deliberately inert and deliberately trivial: it never even looks inside
    /// <paramref name="data"/>, so it cannot throw regardless of what SimHub hands it (null, a game
    /// mid-transition, anything) - every reading on the returned sample is absent (see
    /// <see cref="TelemetryFrame.Empty"/>'s own remarks: absent, not a lie-as-zero), which downstream
    /// Layer 3 correctly reads as "cannot tell" rather than a real, if lifeless, telemetry frame.
    /// </summary>
    public sealed class InertTelemetryAdapter : ITelemetryAdapter
    {
        public void Reset()
        {
            // No bookkeeping to clear - this adapter never accumulates any frame-to-frame state.
        }

        public TelemetrySample Read(GameData data)
        {
            return new TelemetrySample(TelemetryFrame.Empty, TelemetryFrame.Empty, System.DateTime.UtcNow, null);
        }
    }
}

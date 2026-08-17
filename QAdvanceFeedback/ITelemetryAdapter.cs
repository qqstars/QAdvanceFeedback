using GameReaderCommon;
using QAdvanceFeedback.Core;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// PUBLIC CONTRACT for Layer 2 - the SimHub telemetry adapter
    /// (<see cref="SimHubTelemetryAdapter"/> is the concrete implementation). Kept as an interface so
    /// Layer 3 (<c>Core\</c>) and everything above it depends only on this game-agnostic shape, never
    /// on <see cref="SimHubTelemetryAdapter"/> by name.
    /// <para/>
    /// CONTRACT:
    /// <list type="bullet">
    /// <item><see cref="Read"/> must never return null, even when <paramref name="data"/> itself is
    /// null or has null <c>NewData</c>/<c>OldData</c> - return a sample built from
    /// <see cref="TelemetryFrame.Empty"/> frames instead (the honest "no data" default - see that
    /// class's own remarks), never throw.</item>
    /// <item><see cref="Reset"/> clears any frame-to-frame bookkeeping (e.g. the previous frame's
    /// timestamp used to compute <see cref="ITelemetrySample.Dt"/>) so a game switch/session restart
    /// does not report a huge <c>Dt</c> spanning the gap. Called by
    /// <c>QAdvanceFeedback.ResetOnGameSwitch</c>.</item>
    /// </list>
    /// </summary>
    public interface ITelemetryAdapter
    {
        void Reset();

        TelemetrySample Read(GameData data);

        /// <summary>
        /// DIAGNOSTICS ONLY - never consumed by Layer 3's own Lock/Slip algorithm and never affects any
        /// product-tier published property. Captures the raw per-wheel wheel-rotation/wheel-speed/
        /// slip-ratio arrays SimHub's own telemetry holds this frame, gated against real zero vs "not
        /// supplied" by the matching capability flag (see <see cref="RawWheelTelemetryBuilder"/>), plus
        /// the capability flags themselves - the single most direct signal for which Raw-layer branch
        /// the running title actually supports.
        /// <para/>
        /// <paramref name="pluginManager"/> is needed ONLY to reach the live capability object - there
        /// is no path to it from <see cref="GameData"/> alone. Must never throw - same contract as
        /// <see cref="Read"/>: on any missing/unreachable input, return a snapshot with the affected
        /// readings absent (null), never a fabricated zero.
        /// </summary>
        RawWheelTelemetrySnapshot CaptureRawTelemetry(GameData data, PluginManager pluginManager);
    }
}

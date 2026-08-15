using GameReaderCommon;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback
{
    /// <summary>
    /// PUBLIC CONTRACT for Layer 2 - the withheld telemetry adapter. This interface ships in the
    /// open-source repository; the concrete implementation that actually reads SimHub's own
    /// <see cref="GameData"/> shape lives in <c>QAdvanceFeedback\Private\SimHubTelemetryAdapter.cs</c>,
    /// which is gitignored (see <c>Private\README.md</c>).
    /// <para/>
    /// Note this interface itself is allowed to name <see cref="GameData"/> - the constraint isn't
    /// "no SimHub types anywhere outside Private", it is "no code outside Private references the
    /// WITHHELD CONCRETE TYPE by name" (see <c>AlgorithmFactory</c>). <see cref="GameData"/> comes from
    /// <c>GameReaderCommon.dll</c>, which every build of this plugin already references (it is not
    /// part of what is being withheld) - only the mapping logic from it onto Layer 1's
    /// <see cref="TelemetrySample"/> is.
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
    }
}

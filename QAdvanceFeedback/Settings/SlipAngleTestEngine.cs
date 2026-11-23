using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The Slip Angle Test Effect's calculation (docs\slip-angle-design.md 9.11). Pure; the Slip Angle
    /// counterpart of <see cref="LockSlipTestEngine"/>.
    /// <list type="number">
    /// <item>The slider value is Raw.All in degrees, normalized with the window the graph shows
    /// (<see cref="SlipAngleNormalizer.Normalize"/> / <see cref="SlipAngleNormalizer.NormalizeUnclamped"/>).</item>
    /// <item>It runs through the channel's real projection curve, the real <see cref="DriftAttenuator"/>
    /// with the configured <see cref="DriftSettings"/>, and the pulse - the same
    /// <see cref="SlipAngleProjectedEngine"/> the live channel uses.</item>
    /// </list>
    /// Full confidence (maturity 1, no cold-start damping), no pedal gate, no airborne / landing logic and
    /// no learner: a neutral gate verdict. A test therefore never moves the learned window.
    /// </summary>
    public sealed class SlipAngleTestEngine
    {
        /// <summary>Grounded, pedal open, no fades - the gate never shapes a test frame.</summary>
        private static readonly SlipAngleGateVerdict NeutralGate =
            new SlipAngleGateVerdict(true, 1.0, false, 1.0, false, false, false);

        private readonly SlipAngleProjectedEngine _projected;

        public SlipAngleTestEngine(SlipAngleChannelSettings channel)
        {
            channel = channel ?? new SlipAngleChannelSettings();
            ProjectorSettings projector = channel.Projector ?? ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);
            _projected = new SlipAngleProjectedEngine(new OutputProjector(projector),
                channel.Pulse ?? new PulseSettings(), channel.Drift ?? new DriftSettings());
        }

        /// <summary>One frame: <paramref name="sourceDeg"/> is Raw.All in degrees (any value &gt;= 0; past End
        /// the unclamped normalization keeps rising, which is what the drift detector sees).</summary>
        public SlipAngleProjectedResult Step(double sourceDeg, SlipAngleWindow window, double dtSeconds)
        {
            double deg = ClampMath.IsFinite(sourceDeg) && sourceDeg > 0.0 ? sourceDeg : 0.0;
            double n = SlipAngleNormalizer.Normalize(deg, window);
            double nUnclamped = SlipAngleNormalizer.NormalizeUnclamped(deg, window);
            // The real drift start; no throttle, so the throttle clear never fires here.
            return _projected.Compute(n, nUnclamped, 1.0, ColdStartTier.Tier1, NeutralGate, dtSeconds,
                SlipAngleDriftStart.Nu(window), null);
        }
    }
}

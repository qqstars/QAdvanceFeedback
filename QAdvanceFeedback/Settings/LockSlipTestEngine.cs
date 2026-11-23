using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The WheelLock/WheelSlip Test Effect's calculation (docs\shakeit-test-effect-mvvm-design.md).
    /// <list type="number">
    /// <item>Each wheel's slider source value is normalized with the key data the graph shows
    /// (<see cref="KeyDataNormalization"/>).</item>
    /// <item>Front/Rear/Left/Right/All are combined with the channel's own weights
    /// (<see cref="Aggregator.Compute"/>), exactly as the engine does.</item>
    /// <item>The result runs through the channel's real projection curve and pulse, with full confidence
    /// (no cold-start damping).</item>
    /// </list>
    /// There is no pedal gate in this path - brake and throttle count as fully pressed - and no telemetry
    /// or learner is involved, so a test can never move SMax/S90/S75. The other channel is 0.
    /// </summary>
    public sealed class LockSlipTestEngine
    {
        private readonly bool _isLock;
        private readonly WheelChannelSettings _channel;
        private readonly ProjectedWheelLockSlipEngine _projected;

        public LockSlipTestEngine(bool isLock, WheelChannelSettings channel)
        {
            _isLock = isLock;
            _channel = channel ?? new WheelChannelSettings();
            ProjectorSettings own = _channel.Projector ?? ProjectorSettings.CreateShippedDefault(isLock ? ProjectionChannel.Lock : ProjectionChannel.Slip);
            var ownProjector = new OutputProjector(own);
            var otherProjector = new OutputProjector(ProjectorSettings.CreateShippedDefault(isLock ? ProjectionChannel.Slip : ProjectionChannel.Lock));
            PulseSettings ownPulse = _channel.Pulse ?? new PulseSettings();
            _projected = isLock
                ? new ProjectedWheelLockSlipEngine(ownProjector, ownPulse, otherProjector, new PulseSettings())
                : new ProjectedWheelLockSlipEngine(otherProjector, new PulseSettings(), ownProjector, ownPulse);
        }

        public ProjectedWheelLockSlipResult Step(Corners sources0100, KeyDataView keyData, double dtSeconds)
        {
            bool maxGripOnly = _channel.NormalizePattern == NormalizePattern.MaxGripOnly;
            double? sMax = keyData?.SMax, s90 = keyData?.S90, s75 = keyData?.S75;
            double N(double source) =>
                ClampMath.To0100(KeyDataNormalization.Normalize(ClampMath.To0100(source), _isLock, maxGripOnly, sMax, s90, s75));

            var wheels = new Corners(N(sources0100.FrontLeft), N(sources0100.FrontRight), N(sources0100.RearLeft), N(sources0100.RearRight));
            WheelAggregate agg = Aggregator.Compute(wheels, _channel.ToAggregationWeights());

            var zero = new Corners(0, 0, 0, 0);
            NormalizedWheelLockSlipResult normalized = _isLock
                ? new NormalizedWheelLockSlipResult(wheels, agg.Front, agg.Rear, agg.Left, agg.Right, agg.All, zero, 0, 0, 0, 0, 0)
                : new NormalizedWheelLockSlipResult(zero, 0, 0, 0, 0, 0, wheels, agg.Front, agg.Rear, agg.Left, agg.Right, agg.All);

            return _projected.Compute(normalized, dtSeconds, 1.0, 1.0, 1.0, 1.0);
        }
    }
}

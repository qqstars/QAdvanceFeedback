using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Holds the published Wheel Lock / Wheel Slip branch nativeness and calls <c>notify</c> once per
    /// change. A new pair is published only after it has been stable for <see cref="HoldSeconds"/> of
    /// caller-supplied (data-thread) time, so a branch flipping frame to frame never broadcasts. Pure: no
    /// wall clock. <see cref="Clear"/> applies immediately (no game running).
    /// </summary>
    public sealed class NativenessBroadcaster
    {
        public const double HoldSeconds = 0.5;

        private readonly Action _notify;
        private volatile SourceNativeness _lock;
        private volatile SourceNativeness _slip;
        private SourceNativeness _pendingLock;
        private SourceNativeness _pendingSlip;
        private double _pendingFor;
        private bool _hasPending;

        public NativenessBroadcaster(Action notify) { _notify = notify; }

        public SourceNativeness Lock => _lock;
        public SourceNativeness Slip => _slip;

        /// <param name="dtSeconds">Data-thread time since the previous call.</param>
        public void Update(SourceNativeness lockNativeness, SourceNativeness slipNativeness, double dtSeconds)
        {
            if (lockNativeness == _lock && slipNativeness == _slip) { _hasPending = false; return; }

            if (!_hasPending || lockNativeness != _pendingLock || slipNativeness != _pendingSlip)
            {
                _hasPending = true;
                _pendingLock = lockNativeness;
                _pendingSlip = slipNativeness;
                _pendingFor = 0;
                return;
            }

            _pendingFor += dtSeconds > 0 ? dtSeconds : 0;
            if (_pendingFor < HoldSeconds) return;
            Publish(lockNativeness, slipNativeness);
        }

        /// <summary>Back to Unknown at once (no game running).</summary>
        public void Clear()
        {
            _hasPending = false;
            if (_lock == SourceNativeness.Unknown && _slip == SourceNativeness.Unknown) return;
            Publish(SourceNativeness.Unknown, SourceNativeness.Unknown);
        }

        private void Publish(SourceNativeness l, SourceNativeness s)
        {
            _hasPending = false;
            _lock = l;
            _slip = s;
            _notify?.Invoke();
        }
    }
}

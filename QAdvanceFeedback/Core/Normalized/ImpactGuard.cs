using System;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// The fixed thresholds of one <see cref="ImpactGuard"/> (docs\slip-angle-design.md section 9.5b,
    /// points 1-3). The guard COPIES these at construction.
    /// </summary>
    public sealed class ImpactGuardParameters
    {
        /// <summary>When false the guard allows everything and keeps no state.</summary>
        public bool Enabled;

        /// <summary>Any axis above this magnitude (G) is physically implausible: that frame never
        /// teaches, but no exclusion window opens.</summary>
        public double PlausibleCapG;

        /// <summary>A frame-over-frame change above this (G) on any axis is an impact.</summary>
        public double MaxJumpG;

        /// <summary>How many frames back a caller discards on <see cref="ImpactVerdict.RetroInvalidate"/>.</summary>
        public int RetroFrames;

        /// <summary>How long learning stays blocked after an impact.</summary>
        public double ExclusionSeconds;

        /// <summary>Largest allowed |latG - (speed * yaw + dVlat/dt) / g| before the frame is blocked.</summary>
        public double YawResidualG;

        /// <summary>Frame time assumed when the caller's dt is unusable.</summary>
        public double NominalFrameSeconds;

        /// <summary>The slip-angle channel: true, 8.0, 3.0, 5, 1.5, 0.3, 1/60. A fresh object on every call.</summary>
        public static ImpactGuardParameters SlipAngle => new ImpactGuardParameters
        {
            Enabled = true,
            PlausibleCapG = 8.0,
            MaxJumpG = 3.0,
            RetroFrames = 5,
            ExclusionSeconds = 1.5,
            YawResidualG = 0.3,
            NominalFrameSeconds = 1.0 / 60.0,
        };

        /// <summary>Shipped OFF for Lock and Slip. A fresh object on every call.</summary>
        public static ImpactGuardParameters Disabled => new ImpactGuardParameters
        {
            Enabled = false,
            PlausibleCapG = 8.0,
            MaxJumpG = 3.0,
            RetroFrames = 5,
            ExclusionSeconds = 1.5,
            YawResidualG = 0.3,
            NominalFrameSeconds = 1.0 / 60.0,
        };
    }

    /// <summary>One frame's <see cref="ImpactGuard"/> decision.</summary>
    public readonly struct ImpactVerdict
    {
        public ImpactVerdict(bool learnAllowed, bool impactThisFrame, bool retroInvalidate,
            double exclusionRemaining, double yawResidualG)
        {
            LearnAllowed = learnAllowed;
            ImpactThisFrame = impactThisFrame;
            RetroInvalidate = retroInvalidate;
            ExclusionRemaining = exclusionRemaining;
            YawResidualG = yawResidualG;
        }

        /// <summary>False when this frame must not teach anything.</summary>
        public bool LearnAllowed { get; }

        /// <summary>True only on the frame a jump was detected.</summary>
        public bool ImpactThisFrame { get; }

        /// <summary>True on the impact frame: callers drop their last
        /// <see cref="ImpactGuardParameters.RetroFrames"/> frames (gate: <c>Reset()</c>).</summary>
        public bool RetroInvalidate { get; }

        /// <summary>Seconds of exclusion window left (0 when none).</summary>
        public double ExclusionRemaining { get; }

        /// <summary>The yaw-consistency residual in G; 0 when yaw or dVlat/dt was unavailable.</summary>
        public double YawResidualG { get; }
    }

    /// <summary>
    /// Crash / contact protection for the slip-angle learner (docs\slip-angle-design.md section 9.5b,
    /// points 1-3): a plausibility cap, a frame-over-frame jump detector that opens a timed exclusion
    /// window, and a yaw-consistency check. Pure and time-injected: no wall clock, no SimHub types.
    /// Never throws on non-finite input.
    /// </summary>
    public sealed class ImpactGuard
    {
        /// <summary>Standard gravity, m/s^2.</summary>
        private const double Gravity = 9.80665;

        private readonly bool _enabled;
        private readonly double _plausibleCapG;
        private readonly double _maxJumpG;
        private readonly double _exclusionSeconds;
        private readonly double _yawResidualG;
        private readonly double _nominalFrameSeconds;

        private double _previousLat = double.NaN;
        private double _previousLong = double.NaN;
        private double _previousVert = double.NaN;
        private double _exclusionRemaining;

        public ImpactGuard(ImpactGuardParameters p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            _enabled = p.Enabled;
            _plausibleCapG = p.PlausibleCapG;
            _maxJumpG = p.MaxJumpG;
            _exclusionSeconds = p.ExclusionSeconds;
            _yawResidualG = p.YawResidualG;
            _nominalFrameSeconds = p.NominalFrameSeconds;
        }

        /// <summary>Clears the previous values and any exclusion window.</summary>
        public void Reset()
        {
            _previousLat = double.NaN;
            _previousLong = double.NaN;
            _previousVert = double.NaN;
            _exclusionRemaining = 0.0;
        }

        public ImpactVerdict Observe(double latG, double longG, double vertG, double speedMps,
            double? yawRateRadPerSec, double? dVlatDt, double dtSeconds)
        {
            if (!_enabled) return new ImpactVerdict(true, false, false, 0.0, 0.0);

            // Age the window first and always; an unusable dt ages by a nominal frame, never by nothing.
            double aged = ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0 ? dtSeconds : _nominalFrameSeconds;
            if (_exclusionRemaining > 0.0)
            {
                _exclusionRemaining -= aged;
                if (_exclusionRemaining < 0.0) _exclusionRemaining = 0.0;
            }

            // Non-finite or implausible: this frame never teaches, no window, previous values untouched.
            if (!ClampMath.IsFinite(latG) || !ClampMath.IsFinite(longG) || !ClampMath.IsFinite(vertG)
                || Math.Abs(latG) > _plausibleCapG
                || Math.Abs(longG) > _plausibleCapG
                || Math.Abs(vertG) > _plausibleCapG)
            {
                return new ImpactVerdict(false, false, false, _exclusionRemaining, 0.0);
            }

            bool impact = IsJump(latG, _previousLat) || IsJump(longG, _previousLong) || IsJump(vertG, _previousVert);
            _previousLat = latG;
            _previousLong = longG;
            _previousVert = vertG;
            if (impact) _exclusionRemaining = _exclusionSeconds;

            double residual = 0.0;
            if (yawRateRadPerSec.HasValue && dVlatDt.HasValue
                && ClampMath.IsFinite(yawRateRadPerSec.Value) && ClampMath.IsFinite(dVlatDt.Value)
                && ClampMath.IsFinite(speedMps))
            {
                double predictedG = (speedMps * yawRateRadPerSec.Value + dVlatDt.Value) / Gravity;
                residual = Math.Abs(latG - predictedG);
                if (!ClampMath.IsFinite(residual)) residual = 0.0;
            }

            bool allowed = _exclusionRemaining <= 0.0 && !(residual > _yawResidualG);
            return new ImpactVerdict(allowed, impact, impact, _exclusionRemaining, residual);
        }

        private bool IsJump(double value, double previous)
            => ClampMath.IsFinite(previous) && Math.Abs(value - previous) > _maxJumpG;
    }
}

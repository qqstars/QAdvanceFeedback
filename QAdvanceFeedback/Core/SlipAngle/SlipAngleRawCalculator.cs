using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Pure Slip Angle raw calculator (docs\slip-angle-design.md §9.1, §9.3): per-wheel native angles →
    /// axle means (kerb wheels excluded) → front rescaled by k → max/min blend into <c>Raw.All</c>. When
    /// the native source is unavailable it falls back to body slip <c>|atan2(Vlat, max(Vfwd, 1 m/s))|</c>
    /// as the rear angle (the yaw term was dropped: yaw sign per game is unverified).
    /// </summary>
    public static class SlipAngleRawCalculator
    {
        public const double MinForwardSpeedMps = 1.0;
        public const double MinK = KeyedSlipAngleLearner.KMin;   // owner, 2026-11-22: 0.3 (was 0.5)
        public const double MaxK = 2.0;

        private const double RadToDeg = 180.0 / Math.PI;

        public static SlipAngleRawResult Compute(
            SlipAngleInput input, ITelemetryFrame frame, bool isNative, double k, double wMax, double wMin)
        {
            k = ClampMath.IsFinite(k) ? ClampMath.Clamp(k, MinK, MaxK) : 1.0;

            Corners? native = input?.NativeWheelDeg;
            if (isNative && native.HasValue && AllFinite(native.Value))
            {
                return ComputeNative(native.Value, input.KerbWheels ?? frame?.WheelsOnKerbs, k, wMax, wMin);
            }

            return ComputeFallback(frame);
        }

        private static SlipAngleRawResult ComputeNative(Corners wheels, bool[] kerbs, double k, double wMax, double wMin)
        {
            var abs = new Corners(
                Math.Abs(wheels.FrontLeft), Math.Abs(wheels.FrontRight),
                Math.Abs(wheels.RearLeft), Math.Abs(wheels.RearRight));

            double? front = AxleMean(abs.FrontLeft, abs.FrontRight, IsKerb(kerbs, Corners.FL), IsKerb(kerbs, Corners.FR));
            double? rear = AxleMean(abs.RearLeft, abs.RearRight, IsKerb(kerbs, Corners.RL), IsKerb(kerbs, Corners.RR));

            double? all;
            if (front.HasValue && rear.HasValue)
            {
                double f = front.Value * k;
                double r = rear.Value;
                all = Math.Max(f, r) * wMax + Math.Min(f, r) * wMin;
            }
            else if (rear.HasValue)
            {
                all = rear;
            }
            else if (front.HasValue)
            {
                all = front.Value * k;
            }
            else
            {
                all = null;
            }

            return new SlipAngleRawResult(front, rear, all,
                all.HasValue ? SourceNativeness.Native : SourceNativeness.Unknown, abs);
        }

        private static SlipAngleRawResult ComputeFallback(ITelemetryFrame frame)
        {
            double? vlat = frame?.LateralLocalVelocity;
            double? vfwd = frame?.ForwardLocalVelocity;
            if (!vlat.HasValue || !vfwd.HasValue || !ClampMath.IsFinite(vlat.Value) || !ClampMath.IsFinite(vfwd.Value))
            {
                return SlipAngleRawResult.None;
            }

            double rear = Math.Abs(Math.Atan2(vlat.Value, Math.Max(vfwd.Value, MinForwardSpeedMps))) * RadToDeg;
            return new SlipAngleRawResult(null, rear, rear, SourceNativeness.Fallback, null);
        }

        /// <summary>A wheel reading at or below this (degrees) while its partner reads at least
        /// <see cref="LiftedWheelPartnerDeg"/> is a LIFTED wheel - see <see cref="AxleMean"/>.</summary>
        public const double LiftedWheelDeg = 0.05;

        /// <summary>The partner reading (degrees) that makes a near-zero wheel a lifted one rather than an axle
        /// running straight.</summary>
        public const double LiftedWheelPartnerDeg = 2.0;

        /// <summary>
        /// The axle angle: the mean of its two wheels, a kerb wheel left out - and, owner 2026-11-22, a LIFTED wheel
        /// left out the same way. A game reports 0 for a wheel off the ground (over a bump, a kerb not flagged as one,
        /// the inside wheel lifting), so the plain mean halved the axle: measured on the owner's FH6 log, FL 0 with
        /// FR 12.7 read as a 6.3 front axle, on 46 front and 32 rear frames (about 2%). The loaded partner is what the
        /// axle is doing. Within the axle the mean is otherwise kept: on the same logs the two wheels move together
        /// (one-wheel jumps were 21 / 17 frames on FH6, none on F1), so the larger, smaller or a jump-holding variant
        /// changed the noise by under 5%.
        /// </summary>
        private static double? AxleMean(double left, double right, bool leftKerb, bool rightKerb)
        {
            if (leftKerb && rightKerb) return null;
            if (leftKerb) return right;
            if (rightKerb) return left;
            if (left <= LiftedWheelDeg && right >= LiftedWheelPartnerDeg) return right;
            if (right <= LiftedWheelDeg && left >= LiftedWheelPartnerDeg) return left;
            return (left + right) * 0.5;
        }

        private static bool IsKerb(bool[] kerbs, int index)
            => kerbs != null && index < kerbs.Length && kerbs[index];

        private static bool AllFinite(Corners c)
            => ClampMath.IsFinite(c.FrontLeft) && ClampMath.IsFinite(c.FrontRight)
            && ClampMath.IsFinite(c.RearLeft) && ClampMath.IsFinite(c.RearRight);
    }
}

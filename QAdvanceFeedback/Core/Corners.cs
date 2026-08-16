using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Four per-wheel values, in the wheel-index order SimHub's own
    /// EffectHelper.PlacementToIndex uses (confirmed by decompiling SimHub.Plugins.dll):
    /// FrontLeft=0, FrontRight=1, RearLeft=2, RearRight=3. The legacy algorithm's left/right
    /// halving test (<c>wheelIndex % 2</c>) depends on this exact order - see
    /// <c>BrakeSpeedSlipModel</c>.
    /// <para/>
    /// A struct so per-frame maths allocates nothing (ported from the sibling
    /// ReliableWheelLockSlip project's Core/Corners.cs, which follows the same convention).
    /// </summary>
    public readonly struct Corners : IEquatable<Corners>
    {
        public const int FL = 0;
        public const int FR = 1;
        public const int RL = 2;
        public const int RR = 3;

        public readonly double FrontLeft;
        public readonly double FrontRight;
        public readonly double RearLeft;
        public readonly double RearRight;

        public Corners(double frontLeft, double frontRight, double rearLeft, double rearRight)
        {
            FrontLeft = frontLeft;
            FrontRight = frontRight;
            RearLeft = rearLeft;
            RearRight = rearRight;
        }

        public static readonly Corners Zero = new Corners(0.0, 0.0, 0.0, 0.0);

        public static Corners Uniform(double value) => new Corners(value, value, value, value);

        public double this[int index]
        {
            get
            {
                switch (index)
                {
                    case FL: return FrontLeft;
                    case FR: return FrontRight;
                    case RL: return RearLeft;
                    case RR: return RearRight;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
        }

        public bool Equals(Corners other)
            => FrontLeft.Equals(other.FrontLeft) && FrontRight.Equals(other.FrontRight)
            && RearLeft.Equals(other.RearLeft) && RearRight.Equals(other.RearRight);

        public override bool Equals(object obj) => obj is Corners other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = FrontLeft.GetHashCode();
                h = (h * 397) ^ FrontRight.GetHashCode();
                h = (h * 397) ^ RearLeft.GetHashCode();
                h = (h * 397) ^ RearRight.GetHashCode();
                return h;
            }
        }

        public override string ToString()
            => $"FL={FrontLeft:F3} FR={FrontRight:F3} RL={RearLeft:F3} RR={RearRight:F3}";
    }
}

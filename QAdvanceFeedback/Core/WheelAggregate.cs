using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The five values <see cref="Aggregator.Compute"/> derives from one channel's four per-wheel
    /// <see cref="Corners"/> this frame - Front/Rear/Left/Right/All, already clamped to 0-100 (see that
    /// method's own remarks).
    /// </summary>
    public readonly struct WheelAggregate : IEquatable<WheelAggregate>
    {
        public readonly double Front;
        public readonly double Rear;
        public readonly double Left;
        public readonly double Right;
        public readonly double All;

        public WheelAggregate(double front, double rear, double left, double right, double all)
        {
            Front = front;
            Rear = rear;
            Left = left;
            Right = right;
            All = all;
        }

        public bool Equals(WheelAggregate other)
            => Front.Equals(other.Front) && Rear.Equals(other.Rear) && Left.Equals(other.Left)
            && Right.Equals(other.Right) && All.Equals(other.All);

        public override bool Equals(object obj) => obj is WheelAggregate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Front.GetHashCode();
                h = (h * 397) ^ Rear.GetHashCode();
                h = (h * 397) ^ Left.GetHashCode();
                h = (h * 397) ^ Right.GetHashCode();
                h = (h * 397) ^ All.GetHashCode();
                return h;
            }
        }

        public override string ToString()
            => $"Front={Front:F3} Rear={Rear:F3} Left={Left:F3} Right={Right:F3} All={All:F3}";
    }
}

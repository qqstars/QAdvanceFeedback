using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// How a native QAdvanceFeedback WheelLock/WheelSlip ShakeIt effect routes its output
    /// (docs\shakeit-native-effects-design.md, "WheelLock / WheelSlip routing"). Replaces ShakeIt's
    /// own max-based aggregation so the plugin's weighted Front/Rear/Left/Right/All values are used.
    /// </summary>
    public enum EffectRouting
    {
        Corners,
        FrontRear,
        LeftRight,
        Mono
    }

    /// <summary>
    /// Same members, same order as SimHub's <c>GameReaderCommon.Enums.FFBPlacement</c>, so the
    /// SimHub adapter converts with a plain cast. Kept here so Core stays SimHub-free.
    /// </summary>
    public enum EffectSlot
    {
        All,
        Front,
        Rear,
        Left,
        Right,
        FrontLeft,
        FrontRight,
        RearLeft,
        RearRight
    }

    /// <summary>The four G-Force ShakeIt effects, two channels (Left/Right) each.</summary>
    public enum GForcePad
    {
        BottomFront,
        BottomRear,
        BackLow,
        BackTop
    }

    /// <summary>
    /// Maps <see cref="EffectRouting"/> to the string a ShakeIt container persists as its
    /// <c>AggregationMode</c>. The names are ShakeIt's own, so a profile reads naturally, and storing
    /// the routing there is what lets the container's effect list swap (the setter raises
    /// <c>Effects</c>, which the channel-mapping UI listens for).
    /// </summary>
    public static class EffectRoutingNames
    {
        public const string Corners = "Corners";
        public const string FrontRear = "Front/Rear";
        public const string LeftRight = "Left/Right";
        public const string Mono = "Mono";

        public static string ToAggregationMode(EffectRouting routing)
        {
            switch (routing)
            {
                case EffectRouting.Corners: return Corners;
                case EffectRouting.FrontRear: return FrontRear;
                case EffectRouting.LeftRight: return LeftRight;
                default: return Mono;
            }
        }

        /// <summary>Null or anything unrecognised is <see cref="EffectRouting.Mono"/> (the default).</summary>
        public static EffectRouting Parse(string aggregationMode)
        {
            if (string.Equals(aggregationMode, Corners, StringComparison.Ordinal)) return EffectRouting.Corners;
            if (string.Equals(aggregationMode, FrontRear, StringComparison.Ordinal)) return EffectRouting.FrontRear;
            if (string.Equals(aggregationMode, LeftRight, StringComparison.Ordinal)) return EffectRouting.LeftRight;
            return EffectRouting.Mono;
        }
    }

    public static class EffectRoutingSlots
    {
        private static readonly EffectSlot[] CornersSlots = { EffectSlot.FrontLeft, EffectSlot.FrontRight, EffectSlot.RearLeft, EffectSlot.RearRight };
        private static readonly EffectSlot[] FrontRearSlots = { EffectSlot.Front, EffectSlot.Rear };
        private static readonly EffectSlot[] LeftRightSlots = { EffectSlot.Left, EffectSlot.Right };
        private static readonly EffectSlot[] MonoSlots = { EffectSlot.All };

        public static IReadOnlyList<EffectSlot> For(EffectRouting routing)
        {
            switch (routing)
            {
                case EffectRouting.Corners: return CornersSlots;
                case EffectRouting.FrontRear: return FrontRearSlots;
                case EffectRouting.LeftRight: return LeftRightSlots;
                default: return MonoSlots;
            }
        }
    }

    /// <summary>
    /// Picks the 0..100 value a ShakeIt effect slot outputs. Lock/Slip use the PULSED projected
    /// values - the same ones published as <c>WheelLock.Projected.*</c>/<c>WheelSlip.Projected.*</c>,
    /// which the property-based Custom Effect profiles bind today.
    /// </summary>
    public static class ChannelValues
    {
        public static double Lock(ProjectedWheelLockSlipResult r, EffectSlot slot)
        {
            if (r == null) return 0;
            return Select(slot, r.LockWheels, r.LockFront, r.LockRear, r.LockLeft, r.LockRight, r.LockAll);
        }

        public static double Slip(ProjectedWheelLockSlipResult r, EffectSlot slot)
        {
            if (r == null) return 0;
            return Select(slot, r.SlipWheels, r.SlipFront, r.SlipRear, r.SlipLeft, r.SlipRight, r.SlipAll);
        }

        /// <summary>The slot's pulse-free projected value (where the Test Effect draws its ball).</summary>
        public static double LockWithoutPulse(ProjectedWheelLockSlipResult r, EffectSlot slot)
        {
            if (r == null) return 0;
            return Select(slot, r.LockWheelsWithoutPulse, r.LockFrontWithoutPulse, r.LockRearWithoutPulse,
                r.LockLeftWithoutPulse, r.LockRightWithoutPulse, r.LockAllWithoutPulse);
        }

        /// <summary>See <see cref="LockWithoutPulse"/>.</summary>
        public static double SlipWithoutPulse(ProjectedWheelLockSlipResult r, EffectSlot slot)
        {
            if (r == null) return 0;
            return Select(slot, r.SlipWheelsWithoutPulse, r.SlipFrontWithoutPulse, r.SlipRearWithoutPulse,
                r.SlipLeftWithoutPulse, r.SlipRightWithoutPulse, r.SlipAllWithoutPulse);
        }

        public static double GForce(GForceOutput o, GForcePad pad, bool right)
        {
            if (o == null) return 0;
            double? v;
            switch (pad)
            {
                case GForcePad.BottomFront: v = right ? o.BottomFrontRight : o.BottomFrontLeft; break;
                case GForcePad.BottomRear: v = right ? o.BottomRearRight : o.BottomRearLeft; break;
                case GForcePad.BackLow: v = right ? o.BackLowRight : o.BackLowLeft; break;
                default: v = right ? o.BackTopRight : o.BackTopLeft; break;
            }
            return v ?? 0;
        }

        private static double Select(EffectSlot slot, Corners wheels, double front, double rear, double left, double right, double all)
        {
            switch (slot)
            {
                case EffectSlot.Front: return front;
                case EffectSlot.Rear: return rear;
                case EffectSlot.Left: return left;
                case EffectSlot.Right: return right;
                case EffectSlot.FrontLeft: return wheels.FrontLeft;
                case EffectSlot.FrontRight: return wheels.FrontRight;
                case EffectSlot.RearLeft: return wheels.RearLeft;
                case EffectSlot.RearRight: return wheels.RearRight;
                default: return all;
            }
        }
    }
}

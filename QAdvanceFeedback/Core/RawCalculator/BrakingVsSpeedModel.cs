using System;

namespace QAdvanceFeedback.Core.RawCalculator
{
    /// <summary>
    /// A car-level (not per-wheel) estimate of braking lock / throttle-induced slip from just two
    /// signals: pedal position and ground speed. Used as the Raw-layer formula for titles that expose
    /// little more than pedal and speed telemetry (no wheel-level rotation/slip data at all) - see
    /// <see cref="WheelSlipBranchNames.BrakingVsSpeed"/> and <see cref="RawCalculatorEngine"/> for when
    /// this branch is selected.
    /// <para/>
    /// SHAPE: a brake factor and a speed factor (each 0-1, saturating at their own configured ceiling)
    /// are multiplied together, then reshaped through a sensitivity threshold so a driver can decide how
    /// early a nonzero reading should appear. <see cref="Compute"/> is a faithful port of SimHub's own
    /// against a structural gap in that reshaping at low speed - see its own remarks.
    /// </summary>
    internal static class BrakingVsSpeedModel
    {
        /// <summary>Brake pedal position, 0-100, above which the brake factor is considered fully
        /// saturated (1.0) - a driver mashing the pedal past this point gets no additional effect from
        /// pressing harder.</summary>
        public const double BrakeFullPercent = 60.0;

        /// <summary>Ground speed, km/h, above which the speed factor is considered fully saturated.
        /// Deliberately well below highway speed: this formula is meant to respond fully across the
        /// ordinary braking-zone speed range, not only at very high speed. Together with the sensitivity
        /// threshold this ceiling is also what makes the branch silent below the dead zone
        /// (threshold x this value, 15 km/h at default sensitivity) - a real blind spot, and a
        /// deliberate one since 1.0.7.1: see <see cref="Compute"/> for why the floor that used to cover
        /// it was removed.</summary>
        public const double SpeedFullKmh = 30.0;

        /// <summary>The reshaping threshold (as a percentage of full scale) when the driver-facing lock
        /// sensitivity is at its minimum (0) - a low sensitivity setting demands a strong combined
        /// brake+speed reading before anything is reported at all.</summary>
        public const double SensitivityThresholdAtMinSensitivity = 95.0;

        /// <summary>The reshaping threshold when lock sensitivity is at its maximum (100) - a high
        /// sensitivity setting reports a nonzero reading from only a small combined brake+speed
        /// signal.</summary>
        public const double SensitivityThresholdAtMaxSensitivity = 5.0;

        /// <summary>The shipped default sensitivity - chosen so the formula's own ceiling still reaches
        /// a genuine 100 (see <see cref="Reshape"/>'s remarks: only a threshold at or below 50 lets the
        /// numerator clamp reach its own denominator).</summary>
        public const double DefaultLockSensibility = 50.0;

        private static double BrakeFactor(double? brakePercent)
            => MathHelpers.Clamp(brakePercent ?? 0.0, 0.0, BrakeFullPercent) / BrakeFullPercent;

        private static double SpeedFactor(double? speedKmh)
            => MathHelpers.Clamp(speedKmh ?? 0.0, 0.0, SpeedFullKmh) / SpeedFullKmh;

        /// <summary>Maps the driver-facing 0-100 sensitivity onto a 0-1 reshaping threshold - higher
        /// sensitivity gives a LOWER threshold, i.e. a nonzero reading appears sooner.</summary>
        private static double SensitivityThreshold(double lockSensibility)
            => MathHelpers.Map(lockSensibility, 0.0, 100.0,
                SensitivityThresholdAtMinSensitivity, SensitivityThresholdAtMaxSensitivity, false) / 100.0;

        /// <summary>
        /// Rescales <paramref name="combined"/> so that everything at/below <paramref name="threshold"/>
        /// reads as exactly 0 and the remaining span above it stretches back out to fill 0-1. This is
        /// what makes the sensitivity setting behave as a true "how early does this engage" control
        /// rather than merely capping the top of the range.
        /// </summary>
        private static double Reshape(double combined, double threshold)
        {
            double span = 1.0 - threshold;
            if (span <= 1e-9) return combined >= threshold ? 1.0 : 0.0;
            return MathHelpers.Clamp(combined - threshold, 0.0, threshold) / span;
        }

        /// <summary>
        /// The brake x speed model, reshaped by sensitivity - car-level, 0-1 native scale. A faithful
        /// port of SimHub's own <c>WheelSlipEffect.GetSimpleBraking</c> Lock path, and since 1.0.7.1 the
        /// ONLY thing this branch calls.
        /// <para/>
        /// A "LOW-SPEED FIX" USED TO SIT ON TOP OF THIS, AND WAS REMOVED - do not reintroduce it without
        /// re-reading this. Because brake and speed are multiplied BEFORE reshaping, a fully-committed
        /// brake at the default sensitivity reads exactly 0 at or below 15 km/h (threshold 0.5 x
        /// SpeedFullKmh 30). That looks like a blind spot, and an earlier revision added a brake-only
        /// floor ramped across the same dead zone, taking Math.Max of the two.
        /// <para/>
        /// It was removed because it is not what ShakeIt does, and the divergence was severe rather than
        /// cosmetic - measured across the range, at 15 km/h under braking SimHub publishes 0 where the
        /// floor published 100, and the two only reconverge at 30 km/h:
        /// <code>
        ///   speed km/h :   5     10     15     20     25     30
        ///   SimHub     : 0.0    0.0    0.0   33.3   66.7  100.0
        ///   with floor : 33.3  66.7  100.0  100.0  100.0  100.0
        /// </code>
        /// In effect the floor removed SPEED from the Lock formula below 30 km/h. Layer 3's contract is
        /// to reproduce ShakeIt exactly; a perceived improvement that makes Raw and ShakeIt disagree by
        /// up to 100 points belongs in the Projected layer's own curve, not here.
        /// </summary>
        public static double Compute(double? brakePercent, double? speedKmh, double lockSensibility)
            => Reshape(BrakeFactor(brakePercent) * SpeedFactor(speedKmh), SensitivityThreshold(lockSensibility));


        /// <summary>
        /// The Slip-channel counterpart: a plain brake x speed product with NO sensitivity reshaping -
        /// Slip has no equivalent driver-facing sensitivity control for this branch, so the raw product
        /// is published directly.
        /// </summary>
        public static double ComputeSlipBrakingVsSpeed(double? brakePercent, double? speedKmh)
            => BrakeFactor(brakePercent) * SpeedFactor(speedKmh);
    }
}

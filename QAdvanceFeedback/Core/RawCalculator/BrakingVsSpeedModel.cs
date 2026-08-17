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
    /// early a nonzero reading should appear. <see cref="ComputeWithLowSpeedFix"/> additionally guards
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
        /// ordinary braking-zone speed range, not only at very high speed. This ceiling is also the
        /// reason a genuine low-speed lockup needs the dedicated fix below - see
        /// <see cref="ComputeWithLowSpeedFix"/>.</summary>
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
        /// The plain brake x speed model, reshaped by sensitivity - car-level, 0-1 native scale.
        /// Deliberately not what <see cref="RawCalculatorEngine"/> actually calls for the Lock channel
        /// - see <see cref="ComputeWithLowSpeedFix"/> for the one deviation this Raw layer applies.
        /// </summary>
        public static double Compute(double? brakePercent, double? speedKmh, double lockSensibility)
            => Reshape(BrakeFactor(brakePercent) * SpeedFactor(speedKmh), SensitivityThreshold(lockSensibility));

        /// <summary>
        /// THE LOW-SPEED FIX. Because <see cref="Compute"/> multiplies the brake and speed factors
        /// together BEFORE reshaping, a fully-committed brake (factor 1.0) at the DEFAULT sensitivity
        /// (threshold 0.5) still reads exactly 0 for any speed at or below 15 km/h (0.5 x 30) - a real,
        /// hard, low-speed lockup would otherwise never register at all, regardless of brake pressure.
        /// <para/>
        /// FIX: alongside the faithful brake x speed reading, also reshape the BRAKE FACTOR ALONE
        /// (i.e. as if speed were already at or above <see cref="SpeedFullKmh"/>), then ramp that
        /// brake-only reading up from zero as speed approaches the SAME dead-zone boundary the sensitivity
        /// threshold implies (<c>threshold * SpeedFullKmh</c>) - not an arbitrary extra constant, but the
        /// boundary already embedded in the existing formula. Taking the larger of the two readings means
        /// this NEVER reduces what the plain model alone would already produce; it only adds coverage
        /// exactly where the plain model is structurally blind.
        /// <para/>
        /// CONTINUITY: the low-speed floor ramps linearly from 0 at a standstill up to the brake-only
        /// reshaped value at the dead-zone boundary, then holds flat rather than cutting back to zero -
        /// a hard cutoff would produce a one-frame "click" right at the boundary, which is exactly the
        /// kind of discontinuity a haptic cue must avoid. The floor and the faithful reading converge
        /// exactly at <see cref="SpeedFullKmh"/> itself, so the two hand off with no seam.
        /// <para/>
        /// The floor is gated by the SAME sensitivity reshaping as the faithful reading, so a light dab
        /// on the brake still produces no floor contribution at any speed - only a firmly-committed
        /// brake does.
        /// </summary>
        public static double ComputeWithLowSpeedFix(double? brakePercent, double? speedKmh, double lockSensibility)
        {
            double threshold = SensitivityThreshold(lockSensibility);

            double faithfulReading = Reshape(BrakeFactor(brakePercent) * SpeedFactor(speedKmh), threshold);
            double brakeOnlyReading = Reshape(BrakeFactor(brakePercent), threshold);

            double deadZoneKmh = threshold * SpeedFullKmh;
            double lowSpeedFloor = deadZoneKmh > 1e-9
                ? brakeOnlyReading * MathHelpers.Clamp(speedKmh ?? 0.0, 0.0, deadZoneKmh) / deadZoneKmh
                : brakeOnlyReading;

            return Math.Max(faithfulReading, lowSpeedFloor);
        }

        /// <summary>
        /// The Slip-channel counterpart: a plain brake x speed product with NO sensitivity reshaping -
        /// Slip has no equivalent driver-facing sensitivity control for this branch, so the raw product
        /// is published directly.
        /// </summary>
        public static double ComputeSlipBrakingVsSpeed(double? brakePercent, double? speedKmh)
            => BrakeFactor(brakePercent) * SpeedFactor(speedKmh);
    }
}

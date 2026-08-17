using System;

namespace QAdvanceFeedback.Core.RawCalculator
{
    /// <summary>
    /// Estimates how locked ONE wheel is from how fast it is physically rotating compared to how fast
    /// the car is actually travelling: a wheel that has stopped rotating while the car is still moving
    /// is fully locked; a wheel rotating in step with ground travel is not locked at all. Smoothed
    /// across frames (see <see cref="SmoothingRate"/>) so a single noisy reading cannot make the
    /// published value jump.
    /// <para/>
    /// STATEFUL BY DESIGN: one instance belongs to exactly one wheel of one channel for the lifetime of
    /// a session (<see cref="RawCalculatorEngine"/> keeps four long-lived instances, never recreated
    /// per frame), because the smoothing in <see cref="Compute"/> blends this call's raw estimate with
    /// the PREVIOUS call's own smoothed result.
    /// </summary>
    internal sealed class WheelRotationLockFilter
    {
        /// <summary>Assumed effective wheel circumference in metres, used to turn a rotation rate
        /// (revolutions/second) into the linear speed that rotation implies. A precise per-car wheel
        /// radius is not available from telemetry, so a single representative passenger/race tyre size
        /// (roughly 65 cm diameter) is used for every car.</summary>
        private const double AssumedWheelCircumferenceM = 2.05;

        /// <summary>Below this ground speed the rotation-to-speed ratio this filter depends on becomes
        /// numerically unstable (a small denominator dominates the result) and physically meaningless
        /// (a near-stationary car is not "locked", it is simply stopped) - the estimate resets to zero
        /// rather than reporting a spurious lock reading.</summary>
        private const double MinimumMeaningfulSpeedMps = 1.0;

        /// <summary>The rotation-to-speed ratio treated as "fully locked" - deliberately a small,
        /// nonzero floor rather than exactly 0, both to avoid a divide-by-zero in the ratio's own span
        /// calculation below and to absorb ordinary sensor noise at a near-stopped wheel.</summary>
        private const double FullyLockedRatioFloor = 0.05;

        /// <summary>How much weight this frame's own raw estimate gets against the running smoothed
        /// value (a plain exponential blend) AT THE NOMINAL FRAME INTERVAL
        /// (<see cref="BrakeSpeedSlipModel.NominalDtSeconds"/>) - tuned to settle within a handful of
        /// nominal-length frames without visibly snapping frame to frame. Historically applied as a
        /// literal per-frame alpha regardless of how much wall-clock time that frame actually spanned -
        /// FRAME-RATE INDEPENDENCE FIX (telemetry-integrity pass): a fixed per-frame alpha is itself a
        /// dt artefact (the same physical settling behaviour would take twice as long in wall-clock
        /// terms at half the frame rate, and half as long at double the frame rate) - see
        /// <see cref="Compute"/>, which now derives an equivalent TIME CONSTANT from this rate at the
        /// nominal interval and re-derives the per-frame alpha from the ACTUAL dt each call, so the
        /// filter's wall-clock responsiveness no longer depends on frame rate. A frame at exactly the
        /// nominal interval (or a caller that supplies no dt at all - see <see cref="Compute"/>'s own
        /// remarks) reproduces this exact rate, unchanged.</summary>
        private const double SmoothingRate = 0.25;

        /// <summary>The time constant <see cref="SmoothingRate"/> implies at the nominal frame interval
        /// (<c>tau = -dt / ln(1 - rate)</c>) - the wall-clock quantity that is actually frame-rate
        /// INDEPENDENT, unlike a raw per-frame alpha. Computed once, from named constants, rather than
        /// hard-coded a second time.</summary>
        private static readonly double SmoothingTimeConstantSeconds =
            -BrakeSpeedSlipModel.NominalDtSeconds / Math.Log(1.0 - SmoothingRate);

        /// <summary>Ground speed (m/s) above which this filter's rotation-based reading is trusted at
        /// full strength; below it, confidence ramps down linearly toward zero (see
        /// <see cref="Compute"/>) since the rotation/speed ratio is noisiest at low speed. ~20 km/h.</summary>
        private const double FullConfidenceSpeedMps = 5.56;

        /// <summary>Smoothed values below this are snapped to exactly zero so residual smoothing noise
        /// at a genuinely unlocked wheel does not linger as a perpetual, barely-nonzero reading.</summary>
        private const double ZeroSnapThreshold = 0.01;

        private double _smoothedLockLevel;

        /// <summary>
        /// Produces this wheel's current lock estimate, 0 (rolling freely) to 1 (fully locked).
        /// </summary>
        /// <param name="wheelRotationRateHz">This wheel's own rotation rate (revolutions/second),
        /// already expressed as a magnitude by the caller.</param>
        /// <param name="vehicleSpeedMps">Car ground speed, metres/second.</param>
        /// <param name="unlockedAtRatio">The rotation-to-speed ratio at/above which the wheel is
        /// considered not locked at all - the caller's own tuned baseline (0.6 by convention), adjusted
        /// per <paramref name="lockSensitivity"/> below.</param>
        /// <param name="lockSensitivity">0-100 driver-facing sensitivity - higher values widen the band
        /// that reads as "locked", making this filter respond sooner.</param>
        /// <param name="dt">Elapsed time since this filter's own previous call, if known - see
        /// <see cref="SmoothingTimeConstantSeconds"/>. Null (the default, and every pre-existing call
        /// site) reproduces the ORIGINAL fixed <see cref="SmoothingRate"/> per-frame alpha exactly, the
        /// same "no usable dt -> behave as before" fallback <c>BrakeSpeedSlipModel</c> uses, including
        /// for a dt outside <see cref="TelemetryLearningGate.MaxPlausibleDtSeconds"/> (a stall/loading
        /// screen, not a real interval to smooth across).</param>
        public double Compute(double wheelRotationRateHz, double vehicleSpeedMps, double unlockedAtRatio = 0.6, double lockSensitivity = 50.0, TimeSpan? dt = null)
        {
            double sensitivityAdjustment = MathHelpers.Map(lockSensitivity, 0.0, 100.0, -0.3, 0.3, true);
            double unlockedRatio = unlockedAtRatio + sensitivityAdjustment;

            if (vehicleSpeedMps < MinimumMeaningfulSpeedMps)
            {
                _smoothedLockLevel = 0.0;
                return 0.0;
            }

            double rotationImpliedSpeedRatio = wheelRotationRateHz * AssumedWheelCircumferenceM / Math.Max(1e-6, vehicleSpeedMps);

            double ratioSpan = Math.Max(1e-6, unlockedRatio - FullyLockedRatioFloor);
            double rawLockLevel = MathHelpers.Clamp((unlockedRatio - rotationImpliedSpeedRatio) / ratioSpan, 0.0, 1.0);

            double alpha = DtCorrectedAlpha(dt);
            _smoothedLockLevel = _smoothedLockLevel * (1.0 - alpha) + rawLockLevel * alpha;
            _smoothedLockLevel = MathHelpers.Clamp(_smoothedLockLevel, 0.0, 1.0);
            if (_smoothedLockLevel < ZeroSnapThreshold) _smoothedLockLevel = 0.0;

            double lowSpeedConfidence = MathHelpers.Clamp(vehicleSpeedMps / FullConfidenceSpeedMps, 0.0, 1.0);
            return _smoothedLockLevel * lowSpeedConfidence;
        }

        /// <summary>Re-derives <see cref="SmoothingRate"/>'s own per-frame alpha from the ACTUAL dt via
        /// <see cref="SmoothingTimeConstantSeconds"/> (<c>alpha = 1 - exp(-dt/tau)</c>, the same
        /// dt-correct exponential-smoothing shape <c>NormalizedWheelLockSlipEngine</c>'s own washout-style
        /// filters already use) - falls back to the original fixed <see cref="SmoothingRate"/> when dt is
        /// unknown or implausible (see <see cref="Compute"/>'s own remarks), reusing
        /// <see cref="TelemetryLearningGate.MaxPlausibleDtSeconds"/> as the SAME plausibility bound this
        /// plugin already established rather than a second one.</summary>
        private static double DtCorrectedAlpha(TimeSpan? dt)
        {
            if (!dt.HasValue) return SmoothingRate;
            double seconds = dt.Value.TotalSeconds;
            if (!ClampMath.IsFinite(seconds) || seconds <= 0.0 || seconds > TelemetryLearningGate.MaxPlausibleDtSeconds) return SmoothingRate;
            return MathHelpers.Clamp(1.0 - Math.Exp(-seconds / SmoothingTimeConstantSeconds), 0.0, 1.0);
        }

        /// <summary>Clears the smoothed state back to a cold start - called when the engine detects a
        /// game/car switch, so a lingering lock reading from the previous car cannot bleed into the
        /// next one.</summary>
        public void Reset() => _smoothedLockLevel = 0.0;
    }
}

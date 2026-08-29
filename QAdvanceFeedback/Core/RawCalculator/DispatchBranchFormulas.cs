using System;

namespace QAdvanceFeedback.Core.RawCalculator
{
    /// <summary>
    /// The per-branch Raw-layer formulas <see cref="RawCalculatorEngine"/> dispatches to once
    /// <see cref="WheelSlipBranchSelector"/> has decided which signal shape this frame's title actually
    /// supports (wheel rotation rate, wheel linear speed, a precalibrated slip ratio, a learned
    /// rotation/speed distribution, or overall wheel-speed-vs-ground-speed delta). Each method here is
    /// pure - no shared mutable state beyond what is passed in explicitly - so
    /// <see cref="RawCalculatorEngine"/> is the only place that owns the stateful pieces (the four
    /// <see cref="WheelRotationLockFilter"/> instances, the <see cref="IValueDistributionLearner"/>
    /// pools). Every return value here is on the plugin's native ~[0,1] working scale (unclamped -
    /// scaling/clamping to the published 0-100 range happens once, at the engine's own publish
    /// boundary).
    /// </summary>
    internal static class DispatchBranchFormulas
    {
        /// <summary>
        /// Lock-only: delegates to <paramref name="filter"/>'s own smoothed rotation-vs-speed estimate
        /// for this one wheel (see <see cref="WheelRotationLockFilter"/>) - used when a title exposes
        /// wheel rotation rate and a dummy/assumed wheel radius is good enough to estimate lock from it.
        /// </summary>
        public static double LockFromWheelRotation(
            WheelRotationLockFilter filter, double? wheelRotationRateHz, double? groundSpeedMps, double lockSensibility, TimeSpan? dt = null)
        {
            if (filter == null || !wheelRotationRateHz.HasValue || !groundSpeedMps.HasValue) return 0.0;
            return filter.Compute(Math.Abs(wheelRotationRateHz.Value), groundSpeedMps.Value, 0.6, lockSensibility, dt);
        }

        /// <summary>
        /// Lock-only, memoryless: compares this wheel's own linear speed reading directly against
        /// ground speed - used when a title exposes wheel linear speed but not rotation rate.
        /// <paramref name="groundSpeedOrFallback"/> is the ground-speed-with-fallback value the caller
        /// already resolved (this plugin's convention: Layer 2 exposes both readings, and the algorithm
        /// applies the fallback itself rather than Layer 2 pre-merging them).
        /// </summary>
        public static double LockFromWheelSpeed(double? wheelSpeed, double? groundSpeedOrFallback)
        {
            if (!wheelSpeed.HasValue || !groundSpeedOrFallback.HasValue) return 0.0;
            double gs = groundSpeedOrFallback.Value;
            if (!(gs > 1.0)) return 0.0;

            // A wheel travelling meaningfully slower than the car itself (accounting for a small
            // headroom margin on the wheel-speed side) reads as increasingly locked; the result is
            // additionally scaled down at low ground speed, where the comparison is least reliable.
            double lockShare = (1.0 - wheelSpeed.Value * 1.2 / gs) * MathHelpers.Offset(0.0, 30.0, gs, true);
            return MathHelpers.Offset(0.0, 1.0, lockShare, true, false);
        }

        /// <summary>
        /// Slip-only: a per-wheel slip RATIO already on a known, title-supplied scale
        /// (<paramref name="slipScale"/>) needs no learned reference at all - just a plain proportion of
        /// that scale. Used when a title's own precalibrated slip-ratio provider is available.
        /// </summary>
        public static double DirectSlip(double? wheelSlipRatio, double? slipScale)
        {
            if (!wheelSlipRatio.HasValue || !slipScale.HasValue) return 0.0;
            return MathHelpers.Offset(0.0, slipScale.Value, Math.Abs(wheelSlipRatio.Value), true, false);
        }

        /// <summary>
        /// Lock-only: compares this wheel's rotation-rate-to-speed ratio against a LEARNED cruise
        /// average (<paramref name="rotationToSpeedLearner"/>, fed by the caller from ordinary
        /// light-throttle cruising) - a ratio well below what cruising looks like indicates the wheel
        /// has slowed relative to the car, i.e. it is locking, but only while the car is actively
        /// decelerating (this method's own guard).
        /// </summary>
        public static double LockFromLearnedRotationRatio(
            double? wheelRotationRateHz, double? speedKmh, double? previousSpeedKmh,
            Calibration.ICalibrationData rotationToSpeedCalibration, double lockSensibility)
        {
            if (!wheelRotationRateHz.HasValue || !speedKmh.HasValue || !previousSpeedKmh.HasValue) return 0.0;
            if (!(Math.Abs(speedKmh.Value) > 1.0)) return 0.0;
            if (!(speedKmh.Value < previousSpeedKmh.Value)) return 0.0; // only meaningful while decelerating

            double liveRatio = wheelRotationRateHz.Value / speedKmh.Value;
            // 1.0.7.1: sourced from the ported ShakeIt calibration (persisted, keyed track;car;RPSToSpeed
            // {Front|Rear}) instead of a session-scoped learner - see Calibration.CalibrationData. The
            // ARITHMETIC below is unchanged and was already a faithful port of SimHub's GetRpsLock; only
            // where the cruise mean comes from has changed. GetAverage() is null only when nothing has
            // been observed at all, so this branch keeps its "responds from the first frame" property.
            double? cruiseAverage = rotationToSpeedCalibration?.GetAverage();
            if (!cruiseAverage.HasValue || !(cruiseAverage.Value > 0.0)) return 0.0;
            if (!(Math.Abs(liveRatio) < cruiseAverage.Value)) return 0.0;

            // The sensitivity setting reshapes the output band in two pieces - a shallower response
            // below the setting's own midpoint, a steeper one above it - giving a driver fine control
            // near the default without losing headroom at the extremes.
            double bandLow = MathHelpers.MapPiecewise(lockSensibility, 0.0, 50.0, 100.0, 90.0, 50.0, 5.0);
            double bandHigh = Math.Min(bandLow + 50.0 - 40.0 * lockSensibility / 100.0, 100.0);

            double deviationFromCruise = (1.0 - Math.Abs(liveRatio / cruiseAverage.Value)) * 100.0;
            double reading = MathHelpers.Offset(bandLow, bandHigh, deviationFromCruise, true, false);

            // Below ~20 km/h the ratio itself becomes an unreliable estimator of lock (small speed
            // changes swing the ratio disproportionately), so confidence ramps down toward zero there.
            double speedConfidence = MathHelpers.Offset(0.0, 20.0, Math.Abs(speedKmh.Value), true);
            return reading * speedConfidence;
        }

        /// <summary>
        /// Shared (reached by both Lock and Slip when no more specific signal is available): reads a
        /// per-wheel slip ratio against a LEARNED percentile band of that same signal
        /// (<paramref name="slipRatioLearner"/>), optionally boosted by how spread apart the four
        /// wheels' rotation rates are under braking - a wide spread with one or more wheels braking
        /// hard is itself evidence of an active lock/spin event even before the ratio band responds.
        /// </summary>
        public static double SlipFromLearnedDistribution(
            double? wheelSlipRatio, Calibration.ICalibrationData slipCalibration,
            double? brakePercent, bool wheelRotationCapability,
            double? wheelRotationFrontLeft, double? wheelRotationFrontRight, double? wheelRotationRearLeft, double? wheelRotationRearRight)
        {
            if (slipCalibration == null) return 0.0;

            // 1.0.7.1 - THE FIX THIS RELEASE EXISTS FOR. These two calls can no longer return null: the
            // ported ShakeIt calibration answers from the very first sample, synthesising a band from its
            // running maximum until it has enough evidence for a real percentile (see
            // Calibration.CalibrationData.GetPercentile). The previous implementation returned null here
            // for the first 200 positive samples and this method then published a literal 0 - which both
            // made WheelSlip arrive late AND, because BranchHasSignalForWheel still reported "signal
            // present", published that 0 as a genuine measured zero rather than as absent.
            //
            // The guard is now SimHub's own `percentile != percentile2` and nothing more. Note this also
            // restores the rotation-spread term below during that early window: it needs no calibration
            // at all, and the old early return was discarding it for no reason.
            double lowPercentile = slipCalibration.GetPercentile(15.0);
            double highPercentile = slipCalibration.GetPercentile(99.0);
            if (lowPercentile == highPercentile) return 0.0;

            double rotationSpreadTerm = 0.0;
            if ((brakePercent ?? 0.0) > 20.0 && wheelRotationCapability
                && wheelRotationFrontLeft.HasValue && wheelRotationFrontRight.HasValue
                && wheelRotationRearLeft.HasValue && wheelRotationRearRight.HasValue)
            {
                double a = Math.Abs(wheelRotationFrontLeft.Value), b = Math.Abs(wheelRotationFrontRight.Value);
                double c = Math.Abs(wheelRotationRearLeft.Value), d = Math.Abs(wheelRotationRearRight.Value);
                double spread = Math.Max(Math.Max(a, b), Math.Max(c, d)) - Math.Min(Math.Min(a, b), Math.Min(c, d));

                // Normalises the raw rotation-rate spread against the magnitude this plugin treats as a
                // fully-developed spin/lock event for this signal, then squares it so a small, ordinary
                // spread contributes little while a large spread contributes disproportionately more.
                const double fullyDevelopedSpread = 30.0;
                rotationSpreadTerm = Math.Pow(spread / fullyDevelopedSpread, 2.0);
            }

            if (!wheelSlipRatio.HasValue) return rotationSpreadTerm;
            double fromDistribution = MathHelpers.Offset(lowPercentile, highPercentile, Math.Abs(wheelSlipRatio.Value), false, false);
            return Math.Max(rotationSpreadTerm, fromDistribution);
        }

        /// <summary>
        /// Shared: estimates slip from how far this wheel's linear speed has diverged from ground
        /// speed, using a DIFFERENT tolerance band depending on regime - a light, coasting/driven wheel
        /// under gentle power gets a narrow, learned-cruise-relative band (via
        /// <paramref name="gearAverageDelta"/>/<paramref name="gearAverageSampleCount"/>, this plugin's
        /// own substitute for a per-gear cruise reference), while braking or any other regime gets a
        /// wider, fixed band. Lock never reads the light-coasting regime (a coasting wheel is not
        /// locking) and uses its own, wider band in every other regime.
        /// </summary>
        public static double WheelSpeedVsGroundSpeedSlip(
            bool isLockChannel,
            double? wheelSpeedThisWheel, double? wheelSpeedOppositeWheel,
            double? groundSpeedOrFallback, double? brakePercent, double? throttlePercent, double? clutchPercent,
            double gearAverageDelta, int gearAverageSampleCount,
            Calibration.GameCalibrationBounds bounds = null, int isFlying = 0)
        {
            if (!wheelSpeedThisWheel.HasValue || !wheelSpeedOppositeWheel.HasValue || !groundSpeedOrFallback.HasValue)
                return 0.0;

            double gs = groundSpeedOrFallback.Value;
            if (Math.Abs(gs) < 1e-9) return 0.0;

            double thisWheelDelta = wheelSpeedThisWheel.Value / Math.Abs(gs) - 1.0;

            // SHIPPED PER-GAME BOUNDS (1.0.7.1). SimHub seeds these two LOCALLY and overrides them only
            // when the running title has a shipped GameCalibration entry. The 0.15 seed is NOT the same
            // number as GameCalibration.WheelSpeedDeltaHighbound's own 0.2 default - an earlier revision
            // of this method used the property default as the no-entry fallback, which was a different
            // structure that happened to produce the same output only because both seeds are overwritten
            // before use on every reachable path. Written SimHub's way so it stays correct if that
            // changes.
            double lowBound = 0.02;
            double highBound = 0.15;
            if (bounds != null)
            {
                lowBound = isLockChannel ? bounds.WheelSpeedDeltaLowLockbound : bounds.WheelSpeedDeltaLowbound;
                highBound = bounds.WheelSpeedDeltaHighbound;
            }

            // AIRBORNE - SimHub returns before ANY regime handling. Airborne wheels spin freely, so
            // wheelSpeed/groundSpeed diverges wildly and this branch would otherwise publish a large,
            // meaningless reading. That matters most on precisely the titles that select this branch: a
            // rally car over a crest is exactly this case, and this guard is what keeps a jump from
            // reading as maximum slip. Ordered here, after the bounds resolve and before the regime
            // split, as ShakeIt orders it.
            if (isFlying > 0) return 0.0;

            // ABSENT-BRAKE FIX (telemetry-integrity pass): an UNKNOWN brake reading must never be
            // treated as "definitely not braking" - that would silently promote an ambiguous frame
            // into the light-coasting regime (a materially different, narrower band) exactly as if a
            // title had confidently reported 0% brake. Only a KNOWN brake reading below the coasting
            // threshold may enter that regime; a missing reading falls through to the wider, more
            // conservative regime below instead, the same "cannot tell, so don't guess" rule
            // ITelemetryFrame's own remarks describe.
            bool lightCoastingRegime = brakePercent.HasValue && brakePercent.Value < 10.0 && thisWheelDelta > 0.0;
            if (lightCoastingRegime)
            {
                if (isLockChannel) return 0.0; // a coasting/driven wheel spinning up is not a lock event

                // Once enough cruise evidence has accumulated for this gear, the learned average delta
                // (scaled up slightly for headroom) replaces the fixed low bound - a car-specific
                // reference beats a fixed guess once one is available.
                if (gearAverageSampleCount > 10) lowBound = gearAverageDelta * 1.1;

                // SimHub re-reads the shipped high bound here with its OWN 0.6 fallback, which is a
                // DIFFERENT default from the 0.2 seeded above - so this line is not redundant, and the
                // former unconditional 0.6 was only correct for a title with no shipped bounds at all.
                highBound = bounds?.WheelSpeedDeltaHighbound ?? 0.6;
            }
            else
            {
                lowBound = 0.2;
                highBound = 1.0;
                if (isLockChannel)
                {
                    lowBound = 0.4;
                    highBound = 1.0;
                }
            }

            return MathHelpers.Offset(lowBound, highBound, Math.Abs(thisWheelDelta), true, false);
        }

        /// <summary>
        /// Whether this frame's driving state qualifies as a "cruise" sample worth folding into the
        /// gear-keyed learner <see cref="WheelSpeedVsGroundSpeedSlip"/>'s light-coasting regime reads
        /// from - a light, steady, moderate-speed drive with no braking/clutch input and a modest,
        /// positive wheel-speed delta.
        /// </summary>
        public static bool QualifiesAsGearCruiseSample(
            double? brakePercent, double? clutchPercent, double? throttlePercent, double? groundSpeedOrFallback,
            double largestWheelDelta, double? yawChangePerSecond = null)
        {
            double brake = brakePercent ?? double.MaxValue;
            double clutch = clutchPercent ?? double.MaxValue;
            double throttle = throttlePercent ?? 0.0;
            double gs = groundSpeedOrFallback ?? 0.0;
            return brake <= 0.1 && clutch <= 10.0 && gs > 10.0
                && largestWheelDelta < 0.3 && largestWheelDelta > 0.0 && throttle > 10.0
                // NOT CORNERING (1.0.7.1). SimHub also requires OrientationYawChangePerSecond < 1.5: in
                // a corner the inner and outer wheels genuinely travel different distances, so the delta
                // measured there describes the corner rather than the car's straight-line reference.
                // A title that does not report yaw rate is treated as not cornering, so its behaviour is
                // unchanged from before this gate existed.
                && (yawChangePerSecond ?? 0.0) < 1.5;
        }
    }
}

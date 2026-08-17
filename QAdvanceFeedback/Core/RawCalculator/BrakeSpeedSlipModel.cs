using System;

namespace QAdvanceFeedback.Core.RawCalculator
{
    /// <summary>
    /// A per-wheel Lock/Slip estimate derived purely from RPM and ground speed changing together
    /// (plus lateral velocity for a small cornering-only floor) - the Raw-layer formula used for titles
    /// whose telemetry gives nothing more specific than speed and RPM. See
    /// <see cref="WheelSlipBranchNames.RpmVsSpeedLegacy"/>/<see cref="RpmVsSpeedAssumedLegacy"/> for
    /// when <see cref="RawCalculatorEngine"/> selects this branch.
    /// <para/>
    /// SHAPE: every wheel starts from a small cornering-only floor (lateral velocity, halved on the
    /// inside wheel of the turn). While actively braking, a brake-driven term - built from how much the
    /// RPM-implied speed has moved between the two frames - can raise Lock above that floor. While NOT
    /// braking (and not riding the clutch), a throttle-driven term can raise Slip the same way instead.
    /// Braking takes priority over throttle for Slip: once the brake condition is met, throttle is never
    /// even evaluated for that frame.
    /// <para/>
    /// SCALING: every term here stays on the plugin's native ~[0,1] working scale, UNCLAMPED above 1 -
    /// the gain applied to the brake/throttle terms below is intentionally generous so it saturates
    /// comfortably before the pedal itself reaches its own gain-ramp ceiling; the *100 + clamp to
    /// [0,100] happens once, at <see cref="RawCalculatorEngine"/>'s own publish boundary, not here.
    /// </summary>
    internal static class BrakeSpeedSlipModel
    {
        /// <summary>Scales the small per-revolution speed/RPM mismatch (typically a fraction well under
        /// 1) up into this model's working range - the size of the wheel event that reads as "fully
        /// engaged" once the pedal-driven ramp below is also at its own ceiling.</summary>
        private const double SpeedRpmMismatchGain = 4000.0;

        /// <summary>Brake pedal range, 0-100, over which the brake-driven term ramps from nothing to
        /// full strength - below this, a light trail-braking touch contributes nothing.</summary>
        private const double BrakeGainRampLowPercent = 55.0;

        /// <summary>See <see cref="BrakeGainRampLowPercent"/> - by this pedal position the brake-driven
        /// term is fully engaged.</summary>
        private const double BrakeGainRampHighPercent = 90.0;

        /// <summary>Throttle pedal range, 0-100, over which the throttle-driven term ramps in - a light
        /// throttle blip below this contributes nothing.</summary>
        private const double ThrottleGainRampLowPercent = 70.0;

        /// <summary>See <see cref="ThrottleGainRampLowPercent"/> - by this pedal position the
        /// throttle-driven term is fully engaged.</summary>
        private const double ThrottleGainRampHighPercent = 100.0;

        /// <summary>At/above this clutch position the pedal is considered pressed enough that engine
        /// RPM no longer tracks wheel speed, so the throttle-driven term (which depends on that
        /// coupling) is suppressed entirely.</summary>
        private const double ClutchDisengagedThresholdPercent = 5.0;

        /// <summary>Lateral velocity range (m/s) over which the cornering-only floor ramps in - below
        /// this there is no floor contribution at all; by this magnitude it is fully engaged.</summary>
        private const double LateralFloorLowMps = 0.5;

        private const double LateralFloorHighMps = 2.0;

        /// <summary>The inside wheel of a turn carries proportionally less of the cornering-only floor
        /// than the outside wheel.</summary>
        private const double InsideWheelLateralFloorFactor = 0.5;

        /// <summary>Below this ground speed magnitude (km/h) the whole model is skipped outright - a
        /// near-stationary car has nothing meaningful to say here regardless of pedal input.</summary>
        private const double MinimumGroundSpeedForEvaluationKmh = 1.0;

        /// <summary>
        /// FRAME-RATE INDEPENDENCE (telemetry-integrity pass). <see cref="ComputeBrakeTerm"/>/
        /// <see cref="ComputeThrottleTerm"/> compare each frame's own ground-speed/RPM reading against
        /// the OTHER frame's - a plain finite difference across whatever <c>dt</c> the two samples
        /// happen to be separated by. Measured directly from a captured session
        /// (<c>QAdvanceFeedback.session-20260815-230140.csv</c>, 8984 consecutive frame gaps): median dt
        /// 0.0166s (~60Hz), the middle 80% falling between 0.0158s and 0.0176s, with rare excursions up
        /// to several seconds (a loading screen/pause, not a real driving interval - already outside
        /// <see cref="TelemetryLearningGate.MaxPlausibleDtSeconds"/>). Left un-normalised, the SAME
        /// physical RPM/speed divergence sampled at a coarser dt accumulates over a longer interval and
        /// reads as a LARGER delta - a faster rig (or a stuttering one) would feel a different-strength
        /// cue for an identical lock-up. This constant is the nominal interval the ORIGINAL, un-normalised
        /// formula was implicitly tuned against (this project's own captured median) - dividing the raw
        /// delta by the ACTUAL dt and re-multiplying by this nominal value turns the comparison into a
        /// genuine rate (native units/second) before rescaling it back to "what a nominal-interval frame
        /// would have shown", so a frame at exactly this interval reproduces today's numbers exactly
        /// (<see cref="DtNormalisationFactor"/> returns 1.0 there), while a faster/slower rig no longer
        /// reads a different magnitude for the same underlying rate of change.
        /// </summary>
        public const double NominalDtSeconds = 0.0166;

        /// <summary>
        /// <c>NominalDtSeconds / actualDtSeconds</c> - multiplying a raw frame-to-frame delta by this
        /// factor converts "how much did this change between these two particular frames" into "how much
        /// would this have changed across one NOMINAL-length frame", removing the frame-rate artefact
        /// described on <see cref="NominalDtSeconds"/>. Falls back to an IDENTITY factor (1.0, i.e. the
        /// pre-normalisation formula, unchanged) in exactly the cases this plugin's own dt handling
        /// already treats as "no usable dt": <c>dt</c> unknown (the very first sample of a session -
        /// <see cref="ITelemetrySample.Dt"/>'s own remarks), non-finite/non-positive, or beyond
        /// <see cref="TelemetryLearningGate.MaxPlausibleDtSeconds"/> - deliberately the SAME plausibility
        /// bound that gate already uses, rather than a second, independently-tuned notion of "too big a
        /// gap to trust" (this task's own explicit instruction). An implausible dt therefore HOLDS this
        /// term at its un-normalised reading rather than dividing by (or blowing up against) a gap that
        /// was never a real driving interval to begin with.
        /// </summary>
        private static double DtNormalisationFactor(TimeSpan? dt)
        {
            if (!dt.HasValue) return 1.0;
            double seconds = dt.Value.TotalSeconds;
            if (!ClampMath.IsFinite(seconds) || seconds <= 0.0 || seconds > TelemetryLearningGate.MaxPlausibleDtSeconds) return 1.0;
            return NominalDtSeconds / seconds;
        }

        /// <summary>
        /// Computes one wheel's Lock and Slip readings.
        /// </summary>
        /// <param name="sample">The current + previous telemetry frame.</param>
        /// <param name="wheelIndex">Front-left/right, rear-left/right index order - see
        /// <see cref="Corners"/>. The left/right lateral-floor halving below depends on this exact
        /// order.</param>
        /// <param name="thresholds">Driver-configurable pedal-pressed thresholds (see
        /// <see cref="LegacyThresholds"/>) - null (the default) uses
        /// <see cref="LegacyThresholds.Defaults"/>.</param>
        public static WheelLegacyResult Compute(ITelemetrySample sample, int wheelIndex, LegacyThresholds? thresholds = null)
        {
            LegacyThresholds t = thresholds ?? LegacyThresholds.Defaults;
            ITelemetryFrame newFrame = sample.New;
            ITelemetryFrame oldFrame = sample.Old;
            TimeSpan? dt = sample.Dt;

            if (!PassesMinimumSpeedGate(newFrame)) return WheelLegacyResult.Zero;
            if (!HasUsableSpeedAndRpm(newFrame, oldFrame)) return WheelLegacyResult.Zero;
            if (!GearIsUnchanged(oldFrame, newFrame)) return WheelLegacyResult.Zero;

            double lateralFloor = ComputeLateralFloor(newFrame, wheelIndex);
            double lockRaw = lateralFloor;
            double slipRaw = lateralFloor;

            // In neutral there is no meaningful RPM/speed coupling to read a brake or throttle event
            // from, so only the cornering-only floor is published - not suppressed to zero outright.
            if (newFrame.Gear != "N")
            {
                double brakePercent = newFrame.BrakePercent ?? 0.0;
                double throttlePercent = newFrame.ThrottlePercent ?? 0.0;
                double clutchPercent = newFrame.ClutchPercent ?? 0.0;
                double newRpm = newFrame.Rpm.Value;
                double oldRpm = oldFrame.Rpm.Value;

                if (newFrame.BrakePercent > t.LockBrakeThresholdPercent)
                {
                    double brakeTerm = ComputeBrakeTerm(oldFrame, newFrame, oldRpm, newRpm, brakePercent, dt);
                    lockRaw = Math.Max(lateralFloor, brakeTerm);
                }

                // Slip: brake takes priority over throttle - once the brake condition is satisfied,
                // throttle is never evaluated for this frame, matching this model's own single-priority
                // design (a wheel cannot be read as both a braking event and a throttle event on the
                // same frame).
                if (newFrame.BrakePercent > t.SlipBrakeThresholdPercent)
                {
                    double brakeTerm = ComputeBrakeTerm(oldFrame, newFrame, oldRpm, newRpm, brakePercent, dt);
                    slipRaw = Math.Max(lateralFloor, brakeTerm);
                }
                else if (newFrame.ThrottlePercent > t.SlipThrottleThresholdPercent && clutchPercent < ClutchDisengagedThresholdPercent)
                {
                    double throttleTerm = ComputeThrottleTerm(newFrame, oldRpm, newRpm, throttlePercent, dt);
                    slipRaw = Math.Max(lateralFloor, throttleTerm);
                }
            }

            return new WheelLegacyResult(lockRaw, slipRaw);
        }

        /// <summary>
        /// Outer "is there enough ground speed to evaluate this wheel at all" gate - a frame with
        /// neither ground-speed reading present resolves to 0.0 here, which correctly fails the gate
        /// (a missing reading must never pass as if it were a real, sufficient speed).
        /// </summary>
        internal static bool PassesMinimumSpeedGate(ITelemetryFrame newFrame)
        {
            double groundSpeed = newFrame.GroundSpeedKmh ?? newFrame.SpeedKmh ?? 0.0;
            return Math.Abs(groundSpeed) > MinimumGroundSpeedForEvaluationKmh;
        }

        /// <summary>Both frames need a usable speed AND a usable RPM - a nullable comparison
        /// (e.g. <c>newFrame.SpeedKmh > 1.0</c>) evaluates false when the reading is missing, which is
        /// exactly "not satisfied", never a silent pass on absent data.</summary>
        internal static bool HasUsableSpeedAndRpm(ITelemetryFrame newFrame, ITelemetryFrame oldFrame)
            => newFrame.SpeedKmh > 1.0 && newFrame.Rpm > 0.0 && oldFrame.SpeedKmh > 1.0 && oldFrame.Rpm > 0.0;

        /// <summary>A gear change between the two frames is its own kind of transient this model
        /// refuses to read as a lock/slip event. Null on either side means "unknown", which must never
        /// be treated as a match.</summary>
        internal static bool GearIsUnchanged(ITelemetryFrame oldFrame, ITelemetryFrame newFrame)
            => oldFrame.Gear != null && newFrame.Gear != null
            && string.Equals(oldFrame.Gear, newFrame.Gear, StringComparison.Ordinal);

        /// <summary>The cornering-only floor: present on every wheel regardless of gear/brake/throttle,
        /// halved on the inside wheel of the turn (parity 0 = left wheels, parity 1 = right wheels -
        /// see <see cref="Corners"/>'s own index-order remarks).</summary>
        private static double ComputeLateralFloor(ITelemetryFrame newFrame, int wheelIndex)
        {
            double lateralVelocity = -(newFrame.LateralLocalVelocity ?? 0.0);
            double floor = MathHelpers.Offset(LateralFloorLowMps, LateralFloorHighMps, Math.Abs(lateralVelocity), true);

            int parity = wheelIndex % 2;
            bool turningLeftInsideWheel = lateralVelocity < 0.0 && parity == 0;
            bool turningRightInsideWheel = lateralVelocity > 0.0 && parity == 1;
            if (turningLeftInsideWheel || turningRightInsideWheel) floor *= InsideWheelLateralFloorFactor;

            return floor;
        }

        /// <summary>
        /// THE CORRECTED BRAKING TERM. Ground speed is read from EACH frame's OWN reading - the frame
        /// that supplied a given RPM sample is also the frame whose own ground speed is divided by it -
        /// so the delta genuinely compares "what ground speed did this RPM imply, then vs. now". An
        /// earlier draft of this model divided a single current-frame ground speed by both the old and
        /// the new RPM, which collapses the comparison to "how has RPM changed at today's speed" rather
        /// than tracking each frame's own reading - understating a braking event that spans a real speed
        /// change, and is corrected here.
        /// </summary>
        private static double ComputeBrakeTerm(ITelemetryFrame oldFrame, ITelemetryFrame newFrame, double oldRpm, double newRpm, double brakePercent, TimeSpan? dt)
        {
            double oldGroundSpeed = oldFrame.GroundSpeedKmh ?? oldFrame.SpeedKmh ?? 0.0;
            double newGroundSpeed = newFrame.GroundSpeedKmh ?? newFrame.SpeedKmh ?? 0.0;

            double impliedSpeedDelta = Math.Abs(oldGroundSpeed / oldRpm - newGroundSpeed / newRpm);
            double brakeGainRamp = MathHelpers.Offset(BrakeGainRampLowPercent, BrakeGainRampHighPercent, brakePercent, true);
            return impliedSpeedDelta * DtNormalisationFactor(dt) * SpeedRpmMismatchGain * brakeGainRamp;
        }

        /// <summary>
        /// The throttle-driven term - unlike <see cref="ComputeBrakeTerm"/>, this compares the CURRENT
        /// frame's own ground speed against both RPM readings (no old-frame ground speed lookup). This
        /// asymmetry between the brake and throttle terms is intentional and unchanged: only the braking
        /// term's cross-frame ground-speed pairing was identified as needing correction; the throttle
        /// term's own shape is preserved as-is.
        /// </summary>
        private static double ComputeThrottleTerm(ITelemetryFrame newFrame, double oldRpm, double newRpm, double throttlePercent, TimeSpan? dt)
        {
            double currentGroundSpeed = newFrame.GroundSpeedKmh ?? newFrame.SpeedKmh ?? 0.0;

            double impliedSpeedDelta = Math.Max(0.0, currentGroundSpeed / newRpm - currentGroundSpeed / oldRpm);
            double throttleGainRamp = MathHelpers.Offset(ThrottleGainRampLowPercent, ThrottleGainRampHighPercent, throttlePercent, true);
            return impliedSpeedDelta * DtNormalisationFactor(dt) * SpeedRpmMismatchGain * throttleGainRamp;
        }
    }
}

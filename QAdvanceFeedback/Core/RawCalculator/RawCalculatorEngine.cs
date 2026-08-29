using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.RawCalculator
{
    /// <summary>
    /// Layer 3's own implementation of <see cref="ILegacyWheelLockSlipEngine"/> - the concrete engine
    /// that turns one telemetry sample into published Wheel Lock/Wheel Slip Raw values.
    /// <para/>
    /// Each frame, <see cref="WheelSlipBranchSelector"/> (pure boolean priority over this frame's own
    /// capability snapshot) decides which SIGNAL SHAPE this title actually supports - wheel rotation
    /// rate, wheel linear speed, a precalibrated slip ratio, a learned rotation/speed distribution, or
    /// overall wheel-speed-vs-ground-speed delta - and <see cref="ComputeBranchValue"/> dispatches to
    /// the matching formula in <see cref="DispatchBranchFormulas"/>/<see cref="BrakingVsSpeedModel"/>/
    /// <see cref="BrakeSpeedSlipModel"/>. A title that exposes none of those falls back to the
    /// pedal+speed-only <see cref="BrakeSpeedSlipModel"/>.
    /// <para/>
    /// BACKWARD COMPATIBILITY: <paramref name="rawTelemetry"/> (see <see cref="Compute"/>) defaults to
    /// null. Every caller that does not supply a capability snapshot gets this engine's own
    /// pre-dispatch fixed behaviour (Lock via <see cref="BrakingVsSpeedModel.Compute"/>,
    /// Slip via <see cref="BrakeSpeedSlipModel"/>) - only <c>QAdvanceFeedback.cs</c>'s own
    /// <c>DataUpdate</c>, which always captures and passes a real snapshot, reaches the full dispatch.
    /// <para/>
    /// STATE THIS INSTANCE OWNS: four long-lived <see cref="WheelRotationLockFilter"/> instances (one
    /// per wheel, each with its own smoothing state, reset only by process restart), a gear-keyed
    /// dictionary of learners standing in for a per-gear cruise reference used by the wheel-speed-delta
    /// branch, and - since 1.0.7.1 - a <see cref="Calibration.CalibrationDataProvider"/>, which is a
    /// faithful port of SimHub's own ShakeIt calibration store and is PERSISTED between runs. The four
    /// ad-hoc distribution learners this engine previously owned (two axle-pooled rotation-to-speed, two
    /// axle-pooled slip-ratio, four per-wheel Lock slip-ratio) are gone; see the provider's own remarks
    /// for what replaced them and why the per-wheel Lock split in particular had to go.
    /// </summary>
    internal sealed class RawCalculatorEngine : ILegacyWheelLockSlipEngine
    {
        private readonly WheelRotationLockFilter[] _rotationLockFilters =
            { new WheelRotationLockFilter(), new WheelRotationLockFilter(), new WheelRotationLockFilter(), new WheelRotationLockFilter() };

        // ---- SHAKEIT CALIBRATION (1.0.7.1). Replaces the four ad-hoc distribution learners this engine
        // used to own (two axle-pooled rotation-to-speed, two axle-pooled slip-ratio, four per-wheel
        // Lock slip-ratio) with a faithful port of SimHub's own calibration store - see
        // Calibration.CalibrationDataProvider. Three consequences worth stating plainly:
        //
        //  - THE PER-WHEEL LOCK LEARNERS ARE GONE. SimHub pools all four wheels into ONE "Slip"
        //    calibration (its GetSlipCalibration takes a `front` flag and ignores it) while splitting
        //    RPSToSpeed by axle. Matching ShakeIt exactly was the owner's explicit instruction once it
        //    was established that SimHub's own ongoing LEARNING is pooled too, not just its reads.
        //  - CALIBRATION IS NOW KEYED track;car;metric, SimHub's own scoping - not (game, car, surface).
        //  - IT PERSISTS. SimHub serialises its equivalent as CalibrationDataV5 and simply resumes
        //    accumulating into the same histogram next run; there is deliberately no reference, blend or
        //    handover here, which is what makes resuming free of any transition artefact.
        private readonly Calibration.CalibrationDataProvider _calibration = new Calibration.CalibrationDataProvider();

        private string _trackIdWithConfig = string.Empty;
        private string _carModel = string.Empty;

        /// <summary>The live calibration store, for persistence and for the ShakeIt precalibration
        /// converter. Held by reference - the caller saves whatever this contains.</summary>
        public Calibration.CalibrationDataProvider Calibration => _calibration;

        /// <summary>Names the (track, car) whose telemetry follows. SimHub keys its calibrations this
        /// way, so this is what makes ours line up with a converted ShakeIt file.</summary>
        public void SetContext(string trackIdWithConfig, string carModel)
        {
            _trackIdWithConfig = trackIdWithConfig ?? string.Empty;
            _carModel = carModel ?? string.Empty;
        }


        /// <summary>
        /// The per-gear wheel-speed-delta cruise reference for the WheelsSpeed branch, keyed PER WHEEL
        /// AND PER GEAR - SimHub holds this on the effect instance, and each wheel is its own effect, so
        /// its scope is per wheel. The previous implementation shared one learner across all four wheels,
        /// which is a different reference.
        /// <para/>
        /// A <see cref="Calibration.TimeMovingAverage"/> rather than a distribution learner, with
        /// SimHub's own 1500-sample / effectively-unbounded-time settings.
        /// </summary>
        private readonly Dictionary<string, Calibration.TimeMovingAverage> _gearWheelSpeedDelta =
            new Dictionary<string, Calibration.TimeMovingAverage>(StringComparer.Ordinal);

        private Calibration.TimeMovingAverage GetOrAddGearDelta(int wheelIndex, string gearKey)
        {
            string key = wheelIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (gearKey ?? string.Empty);
            if (!_gearWheelSpeedDelta.TryGetValue(key, out Calibration.TimeMovingAverage average))
            {
                average = new Calibration.TimeMovingAverage { MaxSamples = 1500.0, MaxTimeMs = 2000000000.0 };
                _gearWheelSpeedDelta[key] = average;
            }
            return average;
        }

        public LegacyWheelLockSlipResult Compute(
            ITelemetrySample sample, LegacyThresholds? thresholds = null,
            AggregationWeights? lockAggregation = null, AggregationWeights? slipAggregation = null,
            RawWheelTelemetrySnapshot rawTelemetry = null)
        {
            LegacyThresholds t = thresholds ?? LegacyThresholds.Defaults;
            AggregationWeights lockWeights = lockAggregation ?? AggregationWeights.LockDefaults;
            AggregationWeights slipWeights = slipAggregation ?? AggregationWeights.SlipDefaults;

            ITelemetryFrame newFrame = sample?.New;
            ITelemetryFrame oldFrame = sample?.Old;
            double? brakePercent = newFrame?.BrakePercent;
            double? throttlePercent = newFrame?.ThrottlePercent;

            // A whole-channel trigger threshold, applied on top of whichever branch computes the native
            // reading below - uniform across every title/branch, independent of which one fires.
            bool lockTriggered = brakePercent >= t.LockBrakeThresholdPercent;
            bool slipTriggered = brakePercent >= t.SlipBrakeThresholdPercent || throttlePercent >= t.SlipThrottleThresholdPercent;

            double? groundSpeedOrFallback = newFrame?.GroundSpeedKmh ?? newFrame?.SpeedKmh;

            string lockBranch;
            string slipBranch;
            var lockNative = new double[4];
            var slipNative = new double[4];
            var lockSignalKnown = new bool[4];
            var slipSignalKnown = new bool[4];

            if (rawTelemetry == null)
            {
                lockBranch = WheelSlipBranchNames.NoCapabilitiesSuppliedLockFallback;
                slipBranch = WheelSlipBranchNames.NoCapabilitiesSuppliedSlipFallback;

                double gateSpeed = groundSpeedOrFallback ?? 0.0;
                double lockValue = Math.Abs(gateSpeed) > 1.0
                    ? BrakingVsSpeedModel.Compute(brakePercent, groundSpeedOrFallback, t.LockSensibility)
                    : 0.0;
                // ABSENT-VS-ZERO (item 1): with no ground-speed reading at all this frame, there is
                // nothing to evaluate BrakingVsSpeedModel against - the car-level reading is genuinely
                // UNKNOWN, not "confirmed not braking".
                bool lockKnownThisFallback = groundSpeedOrFallback.HasValue;
                for (int i = 0; i < 4; i++) { lockNative[i] = lockValue; lockSignalKnown[i] = lockKnownThisFallback; }

                // BrakeSpeedSlipModel.Compute's own outer gates (speed/RPM present on both frames, gear
                // unchanged) are wheel-INDEPENDENT - re-derived here (rather than duplicated) via the
                // same internal guards it uses, so "genuinely gated out" and "no telemetry to gate with"
                // are told apart instead of both silently reading 0.
                bool slipKnownThisFallback = BrakeSpeedSlipModel.PassesMinimumSpeedGate(newFrame)
                    && BrakeSpeedSlipModel.HasUsableSpeedAndRpm(newFrame, oldFrame)
                    && BrakeSpeedSlipModel.GearIsUnchanged(oldFrame, newFrame);
                for (int i = 0; i < 4; i++)
                {
                    slipNative[i] = BrakeSpeedSlipModel.Compute(sample, i, thresholds).Slip;
                    slipSignalKnown[i] = slipKnownThisFallback;
                }
            }
            else
            {
                lockBranch = WheelSlipBranchSelector.Select(rawTelemetry, isLock: true);
                slipBranch = WheelSlipBranchSelector.Select(rawTelemetry, isLock: false);

                for (int i = 0; i < 4; i++)
                {
                    lockNative[i] = ComputeBranchValue(lockBranch, isLock: true, i, sample, rawTelemetry, t, groundSpeedOrFallback);
                    lockSignalKnown[i] = BranchHasSignalForWheel(lockBranch, i, newFrame, oldFrame, rawTelemetry, groundSpeedOrFallback);
                }
                for (int i = 0; i < 4; i++)
                {
                    slipNative[i] = ComputeBranchValue(slipBranch, isLock: false, i, sample, rawTelemetry, t, groundSpeedOrFallback);
                    slipSignalKnown[i] = BranchHasSignalForWheel(slipBranch, i, newFrame, oldFrame, rawTelemetry, groundSpeedOrFallback);
                }

                // Learners are fed AFTER this frame's own values are computed, so a learner never
                // observes the very frame it is about to be queried for - causally consistent frame
                // ordering, not a live-updating reference mid-computation.
                FeedLearners(newFrame, rawTelemetry, groundSpeedOrFallback);
            }

            // WHICH BRANCHES THE TRIGGER-THRESHOLD GATE APPLIES TO. The driver-configurable pedal
            // threshold was designed and validated against exactly the two formulas that have no pedal
            // gate of their own (BrakingVsSpeedModel and BrakeSpeedSlipModel, the latter already
            // applying these same thresholds internally as its own brake/throttle mux). The other,
            // more specific branches each carry their own internal gating (a decelerating-speed guard,
            // a percentile-readiness check, and so on) and have no external pedal gate of their own to
            // begin with, so the trigger threshold is applied only to the two/three branches it was
            // built for.
            bool lockUsesTriggerGate = lockBranch == WheelSlipBranchNames.BrakingVsSpeed
                || lockBranch == WheelSlipBranchNames.RpmVsSpeedAssumedLegacy
                || lockBranch == WheelSlipBranchNames.NoCapabilitiesSuppliedLockFallback;
            bool slipUsesTriggerGate = slipBranch == WheelSlipBranchNames.BrakingVsSpeed
                || slipBranch == WheelSlipBranchNames.RpmVsSpeedAssumedLegacy
                || slipBranch == WheelSlipBranchNames.NoCapabilitiesSuppliedSlipFallback;

            // ABSENT-VS-ZERO (item 1): a trigger-gated branch's own "triggered?" verdict needs a KNOWN
            // pedal reading to be trustworthy - an absent brake/throttle reading must not silently read
            // as "confirmed not pressed" (a real, quiet 0) the same way a genuinely-below-threshold
            // reading does. Three-valued: known-true as soon as ONE side is known and over its own
            // threshold (the other side's absence cannot change that verdict); known-false only when
            // EVERY side that could still flip the verdict is actually known; otherwise unknown.
            bool lockTriggerKnown = brakePercent.HasValue;
            bool slipTriggerKnown =
                (brakePercent.HasValue && brakePercent.Value >= t.SlipBrakeThresholdPercent)
                || (throttlePercent.HasValue && throttlePercent.Value >= t.SlipThrottleThresholdPercent)
                || (brakePercent.HasValue && throttlePercent.HasValue);

            var lockScaled = new double?[4];
            var slipScaled = new double?[4];
            for (int i = 0; i < 4; i++)
            {
                bool lockKnown = lockSignalKnown[i] && (!lockUsesTriggerGate || lockTriggerKnown);
                bool slipKnown = slipSignalKnown[i] && (!slipUsesTriggerGate || slipTriggerKnown);

                lockScaled[i] = lockKnown
                    ? (double?)((!lockUsesTriggerGate || lockTriggered) ? ClampMath.To0100(lockNative[i] * 100.0) : 0.0)
                    : null;
                slipScaled[i] = slipKnown
                    ? (double?)((!slipUsesTriggerGate || slipTriggered) ? ClampMath.To0100(slipNative[i] * 100.0) : 0.0)
                    : null;
            }

            var lockWheels = new Corners(lockScaled[Corners.FL] ?? 0.0, lockScaled[Corners.FR] ?? 0.0, lockScaled[Corners.RL] ?? 0.0, lockScaled[Corners.RR] ?? 0.0);
            var slipWheels = new Corners(slipScaled[Corners.FL] ?? 0.0, slipScaled[Corners.FR] ?? 0.0, slipScaled[Corners.RL] ?? 0.0, slipScaled[Corners.RR] ?? 0.0);

            // Aggregates only ever combine wheels that actually reported this frame - see
            // Aggregator.ComputeAvailable's own remarks (two reporting wheels combine as two, not four
            // with two silent zeros).
            WheelAggregate lockAggregate = Aggregator.ComputeAvailable(
                lockScaled[Corners.FL], lockScaled[Corners.FR], lockScaled[Corners.RL], lockScaled[Corners.RR], lockWeights, out bool[] lockAggregateHasValue);
            WheelAggregate slipAggregate = Aggregator.ComputeAvailable(
                slipScaled[Corners.FL], slipScaled[Corners.FR], slipScaled[Corners.RL], slipScaled[Corners.RR], slipWeights, out bool[] slipAggregateHasValue);

            return new LegacyWheelLockSlipResult(
                lockWheels,
                lockAggregate.Front, lockAggregate.Rear, lockAggregate.Left, lockAggregate.Right, lockAggregate.All,
                slipWheels,
                slipAggregate.Front, slipAggregate.Rear, slipAggregate.Left, slipAggregate.Right, slipAggregate.All,
                lockBranch, slipBranch,
                lockAggregateHasValue, slipAggregateHasValue);
        }

        private double ComputeBranchValue(
            string branch, bool isLock, int wheelIndex,
            ITelemetrySample sample, RawWheelTelemetrySnapshot raw, LegacyThresholds t, double? groundSpeedOrFallback)
        {
            ITelemetryFrame newFrame = sample?.New;
            ITelemetryFrame oldFrame = sample?.Old;

            if (branch == WheelSlipBranchNames.PrecalibratedSlip)
                return DispatchBranchFormulas.DirectSlip(WheelSlipRatio(raw, wheelIndex), raw.CapabilityWheelSlipCalibrationProviderSlipScale);

            if (branch == WheelSlipBranchNames.RpsAndDummyRadius)
                return DispatchBranchFormulas.LockFromWheelRotation(
                    _rotationLockFilters[wheelIndex], WheelRotationRate(raw, wheelIndex), newFrame?.GroundSpeedMps, t.LockSensibility, sample?.Dt);

            if (branch == WheelSlipBranchNames.WheelSpeedLock)
                return DispatchBranchFormulas.LockFromWheelSpeed(WheelSpeed(raw, wheelIndex), groundSpeedOrFallback);

            if (branch == WheelSlipBranchNames.BrakingVsSpeed)
                return isLock
                    ? BrakingVsSpeedModel.Compute(newFrame?.BrakePercent, groundSpeedOrFallback, t.LockSensibility)
                    : BrakingVsSpeedModel.ComputeSlipBrakingVsSpeed(newFrame?.BrakePercent, groundSpeedOrFallback);

            if (branch == WheelSlipBranchNames.Rps)
            {
                // Per-AXLE, matching SimHub's own "RPSToSpeed" + Front/Rear metric split.
                Calibration.ICalibrationData pool = _calibration.GetRpsToSpeedCalibration(_trackIdWithConfig, _carModel, wheelIndex < 2);
                return DispatchBranchFormulas.LockFromLearnedRotationRatio(
                    WheelRotationRate(raw, wheelIndex), newFrame?.SpeedKmh, oldFrame?.SpeedKmh, pool, t.LockSensibility);
            }

            if (branch == WheelSlipBranchNames.DirectSlipFeedback)
                return 0.0; // No signal exists for this branch on any title reaching it - a genuine zero, not a gap.

            if (branch == WheelSlipBranchNames.SlipData)
            {
                // ONE POOLED CALIBRATION FOR ALL FOUR WHEELS, and the same one for both channels - this
                // is SimHub's own scoping (see Calibration.CalibrationDataProvider). It supersedes the
                // former per-wheel Lock learners; the `front` argument is passed only so this call reads
                // like SimHub's, and is ignored there exactly as it is here.
                Calibration.ICalibrationData pool = _calibration.GetSlipCalibration(_trackIdWithConfig, _carModel, wheelIndex < 2);
                return DispatchBranchFormulas.SlipFromLearnedDistribution(
                    WheelSlipRatio(raw, wheelIndex), pool, newFrame?.BrakePercent, raw.CapabilityWheelsRPS == true,
                    raw.WheelRpsFrontLeft, raw.WheelRpsFrontRight, raw.WheelRpsRearLeft, raw.WheelRpsRearRight);
            }

            if (branch == WheelSlipBranchNames.WheelsSpeed)
            {
                string gearKey = newFrame?.Gear ?? string.Empty;
                Calibration.TimeMovingAverage gearAverage = GetOrAddGearDelta(wheelIndex, gearKey);
                return DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                    isLock, WheelSpeed(raw, wheelIndex), WheelSpeed(raw, OppositeSide(wheelIndex)),
                    groundSpeedOrFallback, newFrame?.BrakePercent, newFrame?.ThrottlePercent, newFrame?.ClutchPercent,
                    gearAverage.CurrentAverage, gearAverage.Count,
                    // Shipped per-game bounds and the airborne guard - 1.0.7.1, see the formula's remarks.
                    _calibration.GameBounds, raw.IsFlying);
            }

            if (branch == WheelSlipBranchNames.RpmVsSpeedAssumedLegacy)
            {
                WheelLegacyResult r = BrakeSpeedSlipModel.Compute(sample, wheelIndex, t);
                return isLock ? r.Lock : r.Slip;
            }

            // WheelSlipBranchNames.NoBranchMatched, NoCapabilitiesSupplied* (handled above), or any
            // future unrecognised name - no title-reported capability identified ANY usable signal
            // shape at all. See BranchHasSignalForWheel's own remarks: this is now published as ABSENT
            // (nothing to measure with), not a "genuine zero" - the 0.0 returned here is only ever a
            // harmless placeholder for Corners' non-nullable slot once BranchHasSignalForWheel has
            // already said "no value" for this branch.
            return 0.0;
        }

        /// <summary>
        /// ABSENT-VS-ZERO (telemetry-integrity pass, item 1): mirrors, per branch, the exact null-check
        /// each formula in <see cref="ComputeBranchValue"/> already performs internally before it will
        /// compute anything - kept as a SEPARATE, side-effect-free predicate (rather than having every
        /// formula return a nullable itself) so this one place documents, per branch, precisely which
        /// input(s) must be present for that wheel's reading to mean anything at all. A false result
        /// here means <see cref="ComputeBranchValue"/>'s own 0.0 for this branch/wheel is a placeholder,
        /// never a claim that the wheel is confirmed unlocked/non-spinning.
        /// </summary>
        private static bool BranchHasSignalForWheel(
            string branch, int wheelIndex, ITelemetryFrame newFrame, ITelemetryFrame oldFrame,
            RawWheelTelemetrySnapshot raw, double? groundSpeedOrFallback)
        {
            if (branch == WheelSlipBranchNames.PrecalibratedSlip)
                return WheelSlipRatio(raw, wheelIndex).HasValue && raw.CapabilityWheelSlipCalibrationProviderSlipScale.HasValue;

            if (branch == WheelSlipBranchNames.RpsAndDummyRadius)
                return WheelRotationRate(raw, wheelIndex).HasValue && newFrame?.GroundSpeedMps != null;

            if (branch == WheelSlipBranchNames.WheelSpeedLock)
                return WheelSpeed(raw, wheelIndex).HasValue && groundSpeedOrFallback.HasValue;

            if (branch == WheelSlipBranchNames.BrakingVsSpeed)
                // Car-level: the brake side is already covered by the trigger-gate's own three-valued
                // "is the pedal reading known" check (see Compute) - this branch itself only needs SOME
                // ground-speed reading to evaluate against at all.
                return groundSpeedOrFallback.HasValue;

            if (branch == WheelSlipBranchNames.Rps)
                return WheelRotationRate(raw, wheelIndex).HasValue && newFrame?.SpeedKmh != null && oldFrame?.SpeedKmh != null;

            if (branch == WheelSlipBranchNames.DirectSlipFeedback)
                return true; // A permanent, documented characteristic of the branch/title (see ComputeBranchValue) - a real zero, not a per-frame absence.

            if (branch == WheelSlipBranchNames.SlipData)
                return WheelSlipRatio(raw, wheelIndex).HasValue;

            if (branch == WheelSlipBranchNames.WheelsSpeed)
                return WheelSpeed(raw, wheelIndex).HasValue && WheelSpeed(raw, OppositeSide(wheelIndex)).HasValue && groundSpeedOrFallback.HasValue;

            if (branch == WheelSlipBranchNames.RpmVsSpeedAssumedLegacy)
                return BrakeSpeedSlipModel.PassesMinimumSpeedGate(newFrame)
                    && BrakeSpeedSlipModel.HasUsableSpeedAndRpm(newFrame, oldFrame)
                    && BrakeSpeedSlipModel.GearIsUnchanged(oldFrame, newFrame);

            // NoBranchMatched, or any future unrecognised name - the title reported no usable capability
            // shape whatsoever. This is the clearest case of this whole audit: "no branch matched" was
            // previously documented (and published) as a "genuine zero" - it is corrected here to ABSENT,
            // since a title with nothing to measure has nothing to say, not confirmed evidence of zero
            // lock/slip.
            return false;
        }

        /// <summary>Folds this frame's own telemetry into every learner - gated the same way each
        /// formula's own doc comment (<see cref="DispatchBranchFormulas"/>) describes its learned
        /// reference as being built from.</summary>
        private void FeedLearners(ITelemetryFrame newFrame, RawWheelTelemetrySnapshot raw, double? groundSpeedOrFallback)
        {
            if (newFrame == null) return;

            // SHAKEIT CALIBRATION FEEDING (1.0.7.1) - delegated wholesale to the ported provider,
            // which reproduces SimHub's own Update(): the same global SpeedKmh > 2 gate, the same
            // Brake < 5 && Speed > 50 cruise gate for the per-axle RPSToSpeed reference, the same
            // |WheelRPS/SpeedKmh| and raw WheelSlip values, all four wheels pooled into ONE Slip
            // calibration, and the same 'stop feeding past 7000 positive points' cap.
            _calibration.Update(
                _trackIdWithConfig, _carModel,
                raw.CapabilityWheelsRPS == true, raw.CapabilityWheelsSlip == true,
                newFrame.SpeedKmh, newFrame.BrakePercent,
                new[] { raw.WheelRpsFrontLeft, raw.WheelRpsFrontRight, raw.WheelRpsRearLeft, raw.WheelRpsRearRight },
                new[] { raw.WheelSlipRatioFrontLeft, raw.WheelSlipRatioFrontRight, raw.WheelSlipRatioRearLeft, raw.WheelSlipRatioRearRight });

            double? speedKmh = newFrame.SpeedKmh;

            // PER-WHEEL, PER-GEAR WHEEL-SPEED-DELTA REFERENCE (1.0.7.1). Three changes from the former
            // single shared learner, all to match SimHub:
            //   - one average PER WHEEL per gear, not one shared across all four;
            //   - each wheel enqueues max(its own delta, its OPPOSITE-SIDE delta) - the previous code
            //     used max(FrontLeft, RearRight), a diagonal, computed once for every wheel;
            //   - the qualifying gate now also excludes cornering frames via yaw rate.
            if (groundSpeedOrFallback.HasValue && Math.Abs(groundSpeedOrFallback.Value) > 1e-9)
            {
                double groundSpeedMagnitude = Math.Abs(groundSpeedOrFallback.Value);
                string cruiseGearKey = newFrame.Gear ?? string.Empty;

                for (int wheel = 0; wheel < 4; wheel++)
                {
                    double? thisWheelSpeed = WheelSpeed(raw, wheel);
                    double? oppositeWheelSpeed = WheelSpeed(raw, OppositeSide(wheel));
                    if (!thisWheelSpeed.HasValue || !oppositeWheelSpeed.HasValue) continue;

                    double largestDelta = Math.Max(
                        thisWheelSpeed.Value / groundSpeedMagnitude - 1.0,
                        oppositeWheelSpeed.Value / groundSpeedMagnitude - 1.0);

                    if (DispatchBranchFormulas.QualifiesAsGearCruiseSample(
                            newFrame.BrakePercent, newFrame.ClutchPercent, newFrame.ThrottlePercent,
                            groundSpeedOrFallback, largestDelta, raw.OrientationYawChangePerSecond))
                    {
                        GetOrAddGearDelta(wheel, cruiseGearKey).Enqueue(largestDelta);
                    }
                }
            }
        }


        private static double? WheelRotationRate(RawWheelTelemetrySnapshot raw, int wheelIndex)
        {
            switch (wheelIndex)
            {
                case Corners.FL: return raw.WheelRpsFrontLeft;
                case Corners.FR: return raw.WheelRpsFrontRight;
                case Corners.RL: return raw.WheelRpsRearLeft;
                default: return raw.WheelRpsRearRight;
            }
        }

        private static double? WheelSpeed(RawWheelTelemetrySnapshot raw, int wheelIndex)
        {
            switch (wheelIndex)
            {
                case Corners.FL: return raw.WheelSpeedFrontLeft;
                case Corners.FR: return raw.WheelSpeedFrontRight;
                case Corners.RL: return raw.WheelSpeedRearLeft;
                default: return raw.WheelSpeedRearRight;
            }
        }

        private static double? WheelSlipRatio(RawWheelTelemetrySnapshot raw, int wheelIndex)
        {
            switch (wheelIndex)
            {
                case Corners.FL: return raw.WheelSlipRatioFrontLeft;
                case Corners.FR: return raw.WheelSlipRatioFrontRight;
                case Corners.RL: return raw.WheelSlipRatioRearLeft;
                default: return raw.WheelSlipRatioRearRight;
            }
        }

        /// <summary>Same axle, other side (FL&lt;-&gt;FR, RL&lt;-&gt;RR) - used by the wheel-speed-delta
        /// branch's own reference wheel.</summary>
        private static int OppositeSide(int wheelIndex)
        {
            switch (wheelIndex)
            {
                case Corners.FL: return Corners.FR;
                case Corners.FR: return Corners.FL;
                case Corners.RL: return Corners.RR;
                default: return Corners.RL;
            }
        }
    }
}

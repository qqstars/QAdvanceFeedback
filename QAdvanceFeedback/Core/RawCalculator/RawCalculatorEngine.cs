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
    /// pre-dispatch fixed behaviour (Lock via <see cref="BrakingVsSpeedModel.ComputeWithLowSpeedFix"/>,
    /// Slip via <see cref="BrakeSpeedSlipModel"/>) - only <c>QAdvanceFeedback.cs</c>'s own
    /// <c>DataUpdate</c>, which always captures and passes a real snapshot, reaches the full dispatch.
    /// <para/>
    /// STATE THIS INSTANCE OWNS (reset only by process restart): four long-lived
    /// <see cref="WheelRotationLockFilter"/> instances (one per wheel, each with its own smoothing
    /// state), two <see cref="IValueDistributionLearner"/> pools tracking a learned rotation-rate/speed
    /// cruise ratio (front axle, rear axle), two more tracking Slip's own learned slip-ratio percentile
    /// band (front/rear, axle-pooled), four more tracking Lock's own learned slip-ratio percentile band
    /// (one per wheel - see <see cref="_lockSlipRatioPerWheel"/>'s own remarks for why Lock does not
    /// share Slip's axle-pooled pair), and one gear-keyed dictionary of learners standing in for a
    /// per-gear cruise reference used by the wheel-speed-delta branch.
    /// </summary>
    internal sealed class RawCalculatorEngine : ILegacyWheelLockSlipEngine
    {
        /// <summary>Upper bound on how many observations any one learner pool below folds in - keeps a
        /// very long-running session's own memory bounded once a pool has seen far more evidence than
        /// it needs to answer confidently.</summary>
        private const int LearnerSampleCap = 7000;

        private readonly WheelRotationLockFilter[] _rotationLockFilters =
            { new WheelRotationLockFilter(), new WheelRotationLockFilter(), new WheelRotationLockFilter(), new WheelRotationLockFilter() };

        private readonly IValueDistributionLearner _rotationToSpeedFront = new StreamingPercentileLearner();
        private readonly IValueDistributionLearner _rotationToSpeedRear = new StreamingPercentileLearner();

        private readonly IValueDistributionLearner _slipRatioFront = new StreamingPercentileLearner();
        private readonly IValueDistributionLearner _slipRatioRear = new StreamingPercentileLearner();

        /// <summary>
        /// FULL-LOCK FIDELITY FIX (Raw Layer 3, docs\raw-full-lock-fidelity-report.md): one learner PER
        /// WHEEL, fed only that wheel's own <c>WheelSlipRatio</c> - used EXCLUSIVELY by the Lock channel
        /// on the <see cref="WheelSlipBranchNames.SlipData"/> branch. <see cref="_slipRatioFront"/>/
        /// <see cref="_slipRatioRear"/> above remain the Slip channel's own unchanged axle-POOLED
        /// reference (mixing both wheels of an axle into one learner) - Slip's own published values are
        /// bit-for-bit unaffected by this fix. Lock switched away from that same pooled reference because
        /// a title's own per-wheel <c>WheelSlipRatio</c> field is not guaranteed to share one common
        /// native scale across the two wheels of an axle (observed directly: one wheel's own field can
        /// read all the way to +/-1.0 at a genuine lock event while the axle-mate's own field never
        /// exceeds a small fraction of that, for the exact same physical car/session) - pooling two
        /// different native scales into one learner lets whichever wheel locks harder/more often drag the
        /// SHARED high percentile toward its own scale, so the OTHER wheel's genuine full-lock reading
        /// (small on its own native scale) is judged against a reference calibrated to a different
        /// wheel's scale and reads far below 100 even though every wheel is, in fact, fully locked. A
        /// per-wheel reference judges each wheel only against its own history, which is what
        /// <see cref="DispatchBranchFormulas.SlipFromLearnedDistribution"/>'s own doc comment ("a per-wheel
        /// slip ratio against a LEARNED percentile band of THAT SAME SIGNAL") already says this formula is
        /// supposed to do.
        /// </summary>
        private readonly IValueDistributionLearner[] _lockSlipRatioPerWheel =
            { new StreamingPercentileLearner(), new StreamingPercentileLearner(), new StreamingPercentileLearner(), new StreamingPercentileLearner() };

        private readonly Dictionary<string, IValueDistributionLearner> _gearCruiseAverage =
            new Dictionary<string, IValueDistributionLearner>(StringComparer.Ordinal);

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
                    ? BrakingVsSpeedModel.ComputeWithLowSpeedFix(brakePercent, groundSpeedOrFallback, t.LockSensibility)
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
                    ? BrakingVsSpeedModel.ComputeWithLowSpeedFix(newFrame?.BrakePercent, groundSpeedOrFallback, t.LockSensibility)
                    : BrakingVsSpeedModel.ComputeSlipBrakingVsSpeed(newFrame?.BrakePercent, groundSpeedOrFallback);

            if (branch == WheelSlipBranchNames.Rps)
            {
                IValueDistributionLearner pool = wheelIndex < 2 ? _rotationToSpeedFront : _rotationToSpeedRear;
                return DispatchBranchFormulas.LockFromLearnedRotationRatio(
                    WheelRotationRate(raw, wheelIndex), newFrame?.SpeedKmh, oldFrame?.SpeedKmh, pool, t.LockSensibility);
            }

            if (branch == WheelSlipBranchNames.DirectSlipFeedback)
                return 0.0; // No signal exists for this branch on any title reaching it - a genuine zero, not a gap.

            if (branch == WheelSlipBranchNames.SlipData)
            {
                // FULL-LOCK FIDELITY FIX: Lock reads its own PER-WHEEL learner (see
                // _lockSlipRatioPerWheel's own remarks); Slip is untouched, still reading the axle-pooled
                // learner exactly as before.
                IValueDistributionLearner pool = isLock ? _lockSlipRatioPerWheel[wheelIndex] : (wheelIndex < 2 ? _slipRatioFront : _slipRatioRear);
                return DispatchBranchFormulas.SlipFromLearnedDistribution(
                    WheelSlipRatio(raw, wheelIndex), pool, newFrame?.BrakePercent, raw.CapabilityWheelsRPS == true,
                    raw.WheelRpsFrontLeft, raw.WheelRpsFrontRight, raw.WheelRpsRearLeft, raw.WheelRpsRearRight);
            }

            if (branch == WheelSlipBranchNames.WheelsSpeed)
            {
                string gearKey = newFrame?.Gear ?? string.Empty;
                IValueDistributionLearner gearLearner = GetOrAddGearLearner(gearKey);
                double gearAverage = gearLearner.Average() ?? 0.0;
                return DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                    isLock, WheelSpeed(raw, wheelIndex), WheelSpeed(raw, OppositeSide(wheelIndex)),
                    groundSpeedOrFallback, newFrame?.BrakePercent, newFrame?.ThrottlePercent, newFrame?.ClutchPercent,
                    gearAverage, gearLearner.Count);
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

            // Rotation-to-speed cruise reference: a light-throttle, moderate-to-high-speed cruise
            // (low brake, meaningful speed) is the cleanest evidence of "what does an unlocked wheel's
            // rotation rate look like relative to speed" for this car.
            double? speedKmh = newFrame.SpeedKmh;
            if ((newFrame.BrakePercent ?? 100.0) < 5.0 && speedKmh > 50.0)
            {
                FeedRotationToSpeed(_rotationToSpeedFront, raw.WheelRpsFrontLeft, speedKmh.Value);
                FeedRotationToSpeed(_rotationToSpeedFront, raw.WheelRpsFrontRight, speedKmh.Value);
                FeedRotationToSpeed(_rotationToSpeedRear, raw.WheelRpsRearLeft, speedKmh.Value);
                FeedRotationToSpeed(_rotationToSpeedRear, raw.WheelRpsRearRight, speedKmh.Value);
            }

            // Slip-ratio distribution: any frame with more than trivial ground speed is fair evidence
            // of this signal's ordinary shape.
            if (speedKmh > 2.0)
            {
                if (_slipRatioFront.Count < LearnerSampleCap)
                {
                    if (raw.WheelSlipRatioFrontLeft.HasValue) _slipRatioFront.Observe(raw.WheelSlipRatioFrontLeft.Value);
                    if (raw.WheelSlipRatioFrontRight.HasValue) _slipRatioFront.Observe(raw.WheelSlipRatioFrontRight.Value);
                }
                if (_slipRatioRear.Count < LearnerSampleCap)
                {
                    if (raw.WheelSlipRatioRearLeft.HasValue) _slipRatioRear.Observe(raw.WheelSlipRatioRearLeft.Value);
                    if (raw.WheelSlipRatioRearRight.HasValue) _slipRatioRear.Observe(raw.WheelSlipRatioRearRight.Value);
                }

                // FULL-LOCK FIDELITY FIX: same gate, same values, but fed one-per-wheel instead of
                // pooled per axle - see _lockSlipRatioPerWheel's own remarks. Lock-only; Slip keeps
                // reading the pooled learners fed just above, untouched.
                FeedPerWheelSlipRatio(Corners.FL, raw.WheelSlipRatioFrontLeft);
                FeedPerWheelSlipRatio(Corners.FR, raw.WheelSlipRatioFrontRight);
                FeedPerWheelSlipRatio(Corners.RL, raw.WheelSlipRatioRearLeft);
                FeedPerWheelSlipRatio(Corners.RR, raw.WheelSlipRatioRearRight);
            }

            // Gear-keyed cruise average for the wheel-speed-delta branch - see
            // DispatchBranchFormulas.QualifiesAsGearCruiseSample's own remarks for the qualifying gate.
            if (raw.WheelSpeedFrontLeft.HasValue && raw.WheelSpeedRearRight.HasValue && groundSpeedOrFallback.HasValue
                && Math.Abs(groundSpeedOrFallback.Value) > 1e-9)
            {
                double deltaFrontLeft = raw.WheelSpeedFrontLeft.Value / Math.Abs(groundSpeedOrFallback.Value) - 1.0;
                double deltaRearRight = raw.WheelSpeedRearRight.Value / Math.Abs(groundSpeedOrFallback.Value) - 1.0;
                double largestDelta = Math.Max(deltaFrontLeft, deltaRearRight);

                if (DispatchBranchFormulas.QualifiesAsGearCruiseSample(newFrame.BrakePercent, newFrame.ClutchPercent, newFrame.ThrottlePercent, groundSpeedOrFallback, largestDelta))
                {
                    string gearKey = newFrame.Gear ?? string.Empty;
                    GetOrAddGearLearner(gearKey).Observe(largestDelta);
                }
            }
        }

        private static void FeedRotationToSpeed(IValueDistributionLearner learner, double? wheelRotationRateHz, double speedKmh)
        {
            if (!wheelRotationRateHz.HasValue || learner.Count >= LearnerSampleCap) return;
            learner.Observe(Math.Abs(wheelRotationRateHz.Value / speedKmh));
        }

        /// <summary>Feeds one wheel's own <see cref="_lockSlipRatioPerWheel"/> learner - see that
        /// field's own remarks for why Lock needs a per-wheel reference distinct from Slip's axle-pooled
        /// one.</summary>
        private void FeedPerWheelSlipRatio(int wheelIndex, double? wheelSlipRatio)
        {
            if (!wheelSlipRatio.HasValue) return;
            IValueDistributionLearner learner = _lockSlipRatioPerWheel[wheelIndex];
            if (learner.Count < LearnerSampleCap) learner.Observe(wheelSlipRatio.Value);
        }

        private IValueDistributionLearner GetOrAddGearLearner(string gearKey)
        {
            if (!_gearCruiseAverage.TryGetValue(gearKey, out IValueDistributionLearner learner))
            {
                learner = new StreamingPercentileLearner();
                _gearCruiseAverage[gearKey] = learner;
            }
            return learner;
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

using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// WHICH FRAMES MAY TEACH LOCK'S SMax (docs\slip-smax-crossing-gate-design.md, section 9).
    /// <para/>
    /// The WheelLock twin of <see cref="SlipCrossingGate"/>, built after the owner observed the same
    /// SMax = 100 failure on WheelLock in EA WRC that WheelSlip showed in F1. The physics is the mirror
    /// image and the rule is the same shape: a wheel going past the peak of its friction curve shows
    /// the LOCK source rising while achieved G falls - more slip buying less braking. What follows the
    /// crossing is the wheel letting go completely, where the source runs toward full scale while the
    /// car is no longer decelerating, and teaching from THAT is what drives the ceiling to 100.
    /// <para/>
    /// DELIBERATELY A SEPARATE CLASS FROM <see cref="SlipCrossingGate"/>, NOT A SHARED BASE OR A
    /// PARAMETERISED ONE. The owner's explicit requirement: "similar mechanism logic but different
    /// implementation so we can disable one side only just in case". The two channels are calibrated
    /// against different physical events, they arrive here through different upstream detectors (Lock
    /// has the corner-local at-limit confidence, Slip has a plain boolean), and their thresholds are
    /// free to diverge as evidence accumulates. Sharing the code would make every future tuning change
    /// to one channel a silent change to the other, and would make "turn Lock's gate off and keep
    /// Slip's" a conditional inside shared code rather than a switch on an independent object.
    /// The duplication here is the feature.
    /// <para/>
    /// WHAT ARMS IT DIFFERS FROM SLIP'S, and this is the one substantive difference. Slip's gate arms
    /// on the engine's <c>physicallyAtLimit</c> boolean. Lock has something better already: the
    /// corner-local at-limit detector (<c>ComputeCornerAtLimitConfidence</c>) reports a CONTINUOUS
    /// confidence that this frame is at the braking limit, and that is what Lock's SMax teaching has
    /// always been weighted by. So Lock's gate arms on "the corner-local detector has any confidence
    /// at all in this frame", which is a stronger and better-evidenced condition than Slip's threshold
    /// on total G.
    /// </summary>
    public sealed class LockCrossingGate
    {
        /// <summary>How far back the trend reaches. Matches <see cref="SlipCrossingGate.TrendFrames"/>
        /// today; kept as this class's OWN constant so the two can diverge without one channel's tuning
        /// silently retuning the other.</summary>
        public const int TrendFrames = 5;

        /// <summary>Minimum rise in the lock source over the trend, in source units (0-100). Absolute
        /// rather than fractional for the same reason Slip's is: the question is whether the source
        /// moved meaningfully, and a fractional test would equate a wobble near zero with a real
        /// lock-up.</summary>
        /// <summary>
        /// THE SOURCE CEILING (owner's rule 2, 2026-09-07). Every basis entering this gate is clamped to
        /// it, so a reading of 100.0000000001 - which telemetry and our own arithmetic both produce -
        /// cannot read as "still rising" against a previous 100.
        /// </summary>
        public const double SourceCeiling = 100.0;

        /// <summary>Smallest frame-over-frame rise that counts as GENUINELY INCREASING (owner's rule 1).
        /// A hair above zero rather than zero, so floating noise on a held signal is not a rise.</summary>
        public const double MinFrameRise = 1e-6;

        /// <summary>
        /// THE G-COLLAPSE LIMIT (owner, 2026-09-07). Rejects a "crossing" whose acceleration-G fell far
        /// too fast to be a tyre passing its peak - an upshift, a lift, a kerb strike, wheels off the
        /// ground. Measured as a FRACTION OF THE CAR'S OWN CURRENT G, PER SECOND.
        /// <para/>
        /// WHY A FRACTION AND NOT AN ABSOLUTE g/s (the owner's explicit requirement that a different
        /// car, surface or weather must not be limited). An absolute threshold cannot work: an F1 car
        /// shedding 1.0 g is routine, while a road car on ice never moves 1.0 g at all, so any fixed
        /// number either filters nothing on the fast car or everything on the slow one. A fraction is
        /// dimensionless - it asks "how much of what this car currently HAS did it just lose?" - so it
        /// adapts to grip level automatically, with no per-car, per-surface or per-weather tuning and no
        /// dependency on a learned maximum.
        /// <para/>
        /// PER SECOND, not per frame, so a 30 Hz title and a 144 Hz title agree.
        /// <para/>
        /// THE NUMBER, and why it is deliberately permissive (the owner: "keep correct data AS MUCH AS
        /// POSSIBLE... few and low percentage incorrect data leaking should be fine"). Measured on the
        /// scenario probe:
        /// <list type="bullet">
        /// <item>upshift: 0.85 -&gt; 0.30 in one 60 Hz frame = 65% lost = <b>39/s</b></item>
        /// <item>progressive crossing: ~4% per frame = <b>2.4/s</b></item>
        /// <item>fast tarmac spin at detection: ~11% per frame = <b>6.4/s</b></item>
        /// <item>steady-G exit: ~6% per frame = <b>3.3/s</b></item>
        /// </list>
        /// 15/s sits an order of magnitude clear of every genuine crossing above and still less than half
        /// the upshift, so as a filter it errs heavily towards keeping real data.
        /// <para/>
        /// BUT IT SHIPS **OFF** (0.0), because replaying the 12-session corpus measured NO BENEFIT and a
        /// real RISK. Sweeping it over 0 / 6 / 10 / 15 / 25 / 40 per second left ten of the twelve logs
        /// bit-identical, and where it moved anything it was as likely to hurt as help:
        /// <list type="bullet">
        /// <item>c_1_5_3 Raw: max taught value went 78.3 (off) -&gt; <b>90.6</b> (at 6-15/s) - WORSE.</item>
        /// <item>Common_1_5 ShakeItWet: p90 went 48.8 (off) -&gt; <b>66.4</b> (at 6/s) - WORSE.</item>
        /// <item>c_1_7_1: 105 crossings -&gt; 103, p90 unchanged at 58.0 - neutral.</item>
        /// </list>
        /// WHY IT CAN BACKFIRE, which the synthetic scenario could never have shown: rejecting a crossing
        /// does not open the hold window, so ONE CROSSING PER EVENT no longer suppresses the rest of the
        /// break-away - and a LATER, HIGHER reading fires instead. The filter removes a bad sample and
        /// admits a worse one. Fixing that properly means a rejected disturbance must also sit out the
        /// hold window; until that is built and measured, this stays off.
        /// <para/>
        /// Set it to 15.0 to enable the behaviour the tests document.
        /// </summary>
        public const double DefaultMaxGCollapseFractionPerSecond = 0.0;

        /// <summary>
        /// Below this G the fractional test is SKIPPED rather than applied. Dividing by a near-zero G
        /// makes the fraction meaningless and enormous, which would reject everything at low speed and
        /// on very low grip - exactly the cars the owner asked not to penalise. Skipping is the
        /// permissive choice, consistent with letting a little bad data through rather than losing good.
        /// </summary>
        public const double MinGForCollapseTest = 0.05;

        public const double LockRise = 1.0;

        /// <summary>Maximum change in achieved G over the trend for a crossing - G must be FALLING, not
        /// merely flat. A plateau is the normal state at sustained maximum braking and is emphatically
        /// not a wheel going past its friction peak.</summary>
        public const double GFall = -0.05;

        /// <summary>How long one crossing keeps subsequent candidate frames qualified. What those frames
        /// teach is the ONSET snapshot, not their own reading - see <see cref="TeachingBasis"/>.</summary>
        public const double HoldSeconds = 1.0;

        /// <summary>
        /// Weight MULTIPLIER applied to a candidate frame that is not part of a crossing. Zero by
        /// default, so a non-crossing frame teaches nothing.
        /// <para/>
        /// A MULTIPLIER, where Slip's equivalent is an absolute weight - the difference that matters
        /// between the two channels. Lock's teaching weight is the corner-local detector's own
        /// continuous confidence, so this scales that confidence rather than replacing it, and Lock's
        /// existing evidence weighting survives untouched inside a qualified window.
        /// </summary>
        public const double DefaultNonCrossingWeightFactor = 0.0;

        /// <summary>See <see cref="SlipCrossingGate.NominalFrameSeconds"/> - the engine passes 0.0 for
        /// dt whenever a title reports no frame time, and a window aged by zero never expires.</summary>
        public const double NominalFrameSeconds = 1.0 / 60.0;

        /// <summary>Hard frame backstop, for a caller feeding a tiny-but-positive dt forever.</summary>
        public const int HoldMaxFrames = 500;

        private const int HistoryLength = TrendFrames + 1;

        /// <summary>The live G-collapse limit - see
        /// <see cref="DefaultMaxGCollapseFractionPerSecond"/>. Set to 0 or less to DISABLE the test
        /// entirely, which is the kill switch for it.</summary>
        public double MaxGCollapseFractionPerSecond { get; set; } = DefaultMaxGCollapseFractionPerSecond;

        /// <summary>The immediately previous accepted basis, for the frame-over-frame rise test - see
        /// the rule 1 block in <see cref="Observe"/>. NaN until the first accepted frame.</summary>
        private double _previousBasis = double.NaN;

        /// <summary>The immediately previous accepted G, for the collapse-rate test.</summary>
        private double _previousG = double.NaN;

        private readonly double[] _basisHistory = new double[HistoryLength];
        private readonly double[] _gHistory = new double[HistoryLength];
        private readonly Dictionary<string, FrameCounts> _counts = new Dictionary<string, FrameCounts>(StringComparer.Ordinal);

        private int _written;
        private int _next;
        private double _secondsSinceCrossing = double.PositiveInfinity;
        private int _framesSinceCrossing = int.MaxValue;
        private double _nonCrossingWeightFactor = DefaultNonCrossingWeightFactor;
        private double _crossingBasis;
        private double _crossingFallbackBasis;

        /// <summary>See <see cref="DefaultNonCrossingWeightFactor"/>. Clamped to 0..1.</summary>
        public double NonCrossingWeightFactor
        {
            get { return _nonCrossingWeightFactor; }
            set { _nonCrossingWeightFactor = ClampMath.To01(value); }
        }

        /// <summary>True while a crossing has occurred within the last <see cref="HoldSeconds"/> AND
        /// within <see cref="HoldMaxFrames"/> frames.</summary>
        public bool Qualified
        {
            get { return _secondsSinceCrossing <= HoldSeconds && _framesSinceCrossing <= HoldMaxFrames; }
        }

        /// <summary>Diagnostics only - the gate itself acts on <see cref="Qualified"/>.</summary>
        public bool CrossedThisFrame { get; private set; }

        /// <summary>
        /// Scales the corner-local detector's own confidence for this frame: unchanged inside a
        /// qualified window, multiplied by <see cref="NonCrossingWeightFactor"/> outside it.
        /// </summary>
        public double TeachingWeight(double cornerAtLimitConfidence)
        {
            if (!ClampMath.IsFinite(cornerAtLimitConfidence) || cornerAtLimitConfidence <= 0.0) return 0.0;
            return Qualified ? cornerAtLimitConfidence : cornerAtLimitConfidence * _nonCrossingWeightFactor;
        }

        /// <summary>
        /// The value a candidate frame should teach SMax - the reading captured AT THE CROSSING while
        /// the hold window is open, and the frame's own live reading otherwise.
        /// <para/>
        /// The single most important rule in both gates, and the one that took three attempts to get
        /// right on the Slip side. The crossing marks the instant the wheel goes past its friction
        /// peak; THAT reading is what "at the braking limit" means. What the source does over the
        /// following second is the lock-up running away, and on real captures it runs to full scale.
        /// Teaching each hold frame its own live value calibrates against the far side of the event -
        /// measured on the Slip channel's own reference capture, the crossings peaked at 64.5 while
        /// the taught pool's P90 was 100.0, and every taught frame at or above 80 was a hold frame
        /// rather than a crossing.
        /// </summary>
        public double TeachingBasis(double liveBasis)
        {
            return AtMostCeiling(Qualified && IsUsable(_crossingBasis) ? _crossingBasis : liveBasis);
        }

        /// <summary><see cref="TeachingBasis"/> for the Raw-fallback key. Both keys must learn on the
        /// same definition or the engine's divergence test between them compares quantities that are
        /// not comparable.</summary>
        public double TeachingFallbackBasis(double liveFallbackBasis)
        {
            return AtMostCeiling(Qualified && IsUsable(_crossingFallbackBasis) ? _crossingFallbackBasis : liveFallbackBasis);
        }

        /// <summary>Rule 2 applied on the way OUT as well as the way in: the live-value fallback
        /// path never passed through Observe's clamp, so a live 100.0000000001 could still leave
        /// this gate and become SMax. Caught by CrossingGateHeldSourceTests.</summary>
        private static double AtMostCeiling(double basis)
            => ClampMath.IsFinite(basis) && basis > SourceCeiling ? SourceCeiling : basis;

        private static bool IsUsable(double basis) => ClampMath.IsFinite(basis) && basis > 0.0;

        /// <summary>Clears the trend history AND the hold window - called wherever the channel stops
        /// being engaged, since the frames either side of a disengagement are not a continuous
        /// series.</summary>
        public void Reset()
        {
            _previousBasis = double.NaN;
            _previousG = double.NaN;
            _written = 0;
            _next = 0;
            _secondsSinceCrossing = double.PositiveInfinity;
            _framesSinceCrossing = int.MaxValue;
            CrossedThisFrame = false;
            _crossingBasis = 0.0;
            _crossingFallbackBasis = 0.0;
        }

        /// <summary>
        /// Folds one engaged frame into the trend and ages the hold window.
        /// </summary>
        /// <param name="lockBasis">This frame's lock calibration basis (0-100) - the SAME quantity that
        /// would be taught, so the trend measures the thing being calibrated.</param>
        /// <param name="fallbackBasis">This frame's Raw-fallback basis, snapshotted alongside it.</param>
        /// <param name="achievedG">This frame's achieved motion magnitude in g.</param>
        /// <param name="atLimit">Whether the corner-local detector has any confidence in this frame -
        /// see the class remarks for why Lock arms on that rather than on a G threshold.</param>
        /// <param name="dtSeconds">Frame time; unusable values age by <see cref="NominalFrameSeconds"/>.</param>
        public bool Observe(double lockBasis, double fallbackBasis, double achievedG, bool atLimit, double dtSeconds)
        {
            double aged = ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0 ? dtSeconds : NominalFrameSeconds;
            _secondsSinceCrossing += aged;
            if (_framesSinceCrossing < int.MaxValue) _framesSinceCrossing++;

            CrossedThisFrame = false;

            // Telemetry can arrive NaN, infinite or negative. A non-finite reading must not enter the
            // trend history, and an INFINITE basis would satisfy the rise test outright and arm a window
            // whose snapshot the learner then discards - teaching nothing for a whole second.
            if (!ClampMath.IsFinite(lockBasis) || !ClampMath.IsFinite(achievedG) || lockBasis < 0.0)
                return Qualified;

            // ---- OWNER'S RULES 1 AND 2, 2026-09-07 ------------------------------------------------
            //
            // RULE 2 - CLAMP TO THE CEILING. Everything downstream of here, the snapshot that becomes
            // SMax included, is therefore <= 100 exactly.
            //
            // RULE 1 - THE SOURCE MUST BE RISING ON THIS VERY FRAME, not merely higher than it was
            // TrendFrames ago. THE LEAK THIS CLOSES: the trend below differences against 5 frames back,
            // so a source that shoots 10 -> 100 in two frames and then SITS at 100 still reports a rise
            // of +90 for the next five frames. Across those frames it is not rising at all - it is
            // pegged - yet the gate saw a large rise, G was falling because the wheel had already let
            // go, and the frame qualified and taught 100. That is the "source is HOLD but G is
            // decreasing" case, reported from real driving.
            //
            // WHY THIS MAKES SMax = 100 STRUCTURALLY UNREACHABLE, which is the real prize: teaching now
            // requires a strictly increasing source at the detection frame, and a value clamped to 100
            // cannot increase. The taught snapshot is therefore always the last reading BELOW the
            // ceiling. Normal-range crossings are untouched - a crossing at 55 is still rising at 55.
            if (lockBasis > SourceCeiling) lockBasis = SourceCeiling;
            if (ClampMath.IsFinite(fallbackBasis) && fallbackBasis > SourceCeiling) fallbackBasis = SourceCeiling;

            double previousBasis = _previousBasis;
            double previousG = _previousG;
            _previousBasis = lockBasis;
            _previousG = achievedG;
            bool risingThisFrame = ClampMath.IsFinite(previousBasis)
                                   && lockBasis - previousBasis > MinFrameRise;

            // ---- THE G-COLLAPSE LIMIT (owner, 2026-09-07) ----------------------------------------
            //
            // A tyre passing the peak of its slip curve sheds grip PROGRESSIVELY. G that vanishes far
            // faster than that did not come from the tyre: an upshift's torque interruption, a lift, a
            // kerb, a landing. Those all show "slip up, G down" and used to qualify - the probe measured
            // an upshift teaching 40 from a G drop of 0.85 -> 0.30 in a single frame.
            //
            // Self-scaling by construction - see MaxGCollapseFractionPerSecond.
            bool collapseTooFast = false;
            if (MaxGCollapseFractionPerSecond > 0.0
                && ClampMath.IsFinite(previousG)
                && previousG >= MinGForCollapseTest)
            {
                double dropFraction = (previousG - achievedG) / previousG;
                if (dropFraction > 0.0)
                {
                    double perSecond = dropFraction / aged;
                    collapseTooFast = perSecond > MaxGCollapseFractionPerSecond;
                }
            }


            int slot = _next;
            _basisHistory[slot] = lockBasis;
            _gHistory[slot] = achievedG;
            _next = (slot + 1) % HistoryLength;
            if (_written < HistoryLength) _written++;

            // ONE CROSSING PER EVENT, AND ONLY AT THE LIMIT. "Source rising while G falls" is satisfied
            // by the whole lock-up, not just its onset, so a re-arm mid-event would overwrite the
            // snapshot with the runaway's value and the refreshed window would reach forward into the
            // next braking zone. And a crossing detected while the car is NOT at its braking limit is a
            // spin, a kerb, or wheels off the ground - all of which show the same signature.
            if (_written >= HistoryLength && !Qualified && atLimit && risingThisFrame && !collapseTooFast)
            {
                double deltaBasis = lockBasis - _basisHistory[_next];
                double deltaG = achievedG - _gHistory[_next];
                if (deltaBasis > LockRise && deltaG < GFall)
                {
                    _secondsSinceCrossing = 0.0;
                    _framesSinceCrossing = 0;
                    CrossedThisFrame = true;
                    _crossingBasis = lockBasis;
                    _crossingFallbackBasis = fallbackBasis;
                }
            }

            return Qualified;
        }

        /// <summary>Records that one candidate frame was seen for a key, and whether it qualified.
        /// <see cref="TotalFrames"/> is deliberately not consumed by anything.</summary>
        public void RecordCandidate(string key, bool qualified)
        {
            if (key == null) return;
            FrameCounts counts;
            if (!_counts.TryGetValue(key, out counts)) counts = new FrameCounts();
            counts.Total++;
            if (qualified) counts.Valid++;
            _counts[key] = counts;
        }

        /// <summary>Candidate frames for this key that were part of a crossing.</summary>
        public long ValidFrames(string key)
        {
            FrameCounts counts;
            return key != null && _counts.TryGetValue(key, out counts) ? counts.Valid : 0L;
        }

        /// <summary>Every candidate frame for this key. Recorded, not consumed.</summary>
        public long TotalFrames(string key)
        {
            FrameCounts counts;
            return key != null && _counts.TryGetValue(key, out counts) ? counts.Total : 0L;
        }

        private struct FrameCounts
        {
            public long Valid;
            public long Total;
        }
    }
}

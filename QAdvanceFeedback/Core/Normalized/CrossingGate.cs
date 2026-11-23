using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>How <see cref="CrossingGate.TeachingWeight"/> turns the gate state into a weight.</summary>
    public enum TeachingWeightMode
    {
        /// <summary>Slip's rule: 1.0 inside a qualified window, <see cref="CrossingGate.NonCrossingWeight"/>
        /// outside it. The confidence argument is ignored.</summary>
        Fixed,

        /// <summary>Lock's rule: the caller's own continuous confidence (0 when non-finite or not
        /// positive), unchanged inside a qualified window and multiplied by
        /// <see cref="CrossingGate.NonCrossingWeight"/> outside it.</summary>
        MultiplyConfidence,
    }

    /// <summary>
    /// The fixed thresholds of one <see cref="CrossingGate"/>. See <see cref="SlipCrossingGate"/> and
    /// <see cref="LockCrossingGate"/> for what every value means and why the shipped values are what they
    /// are; the doc comments on their constants are the authoritative record. The gate COPIES these at
    /// construction, so mutating a parameter object afterwards never retunes a live gate.
    /// </summary>
    public sealed class CrossingGateParameters
    {
        /// <summary>How many frames back the trend compares against.</summary>
        public int TrendFrames;

        /// <summary>Every basis entering the gate (and leaving it) is clamped to this.</summary>
        public double SourceCeiling;

        /// <summary>Smallest frame-over-frame rise that counts as genuinely increasing.</summary>
        public double MinFrameRise;

        /// <summary>Minimum basis rise over the trend for a crossing (Slip's <c>SlipRise</c>, Lock's
        /// <c>LockRise</c>).</summary>
        public double Rise;

        /// <summary>Maximum G change over the trend for a crossing - G must be falling.</summary>
        public double GFall;

        /// <summary>How long one crossing keeps subsequent frames qualified.</summary>
        public double HoldSeconds;

        /// <summary>Frame time assumed when the caller's dt is unusable.</summary>
        public double NominalFrameSeconds;

        /// <summary>Hard frame backstop on the hold window.</summary>
        public int HoldMaxFrames;

        /// <summary>Below this previous G the G-collapse test is skipped.</summary>
        public double MinGForCollapseTest;

        /// <summary>How the teaching weight is formed - see <see cref="TeachingWeightMode"/>.</summary>
        public TeachingWeightMode WeightMode;

        /// <summary>The highest basis the gate ever TEACHES (snapshot or live) - the hard learn ceiling.
        /// Detection still clamps only to <see cref="SourceCeiling"/>. Not positive or not finite means
        /// <see cref="SourceCeiling"/>. Lock/Slip: 95 (<see cref="KeyedScaleLearner.MaxLearnedCeiling"/>).</summary>
        public double LearnCeiling;

        /// <summary>The pre-extraction <see cref="SlipCrossingGate"/> values (5, 100, 1e-6, 1.0, -0.05,
        /// 1.0, 1/60, 500, 0.05, Fixed), built FROM <see cref="SlipCrossingGate"/>'s own public constants
        /// so the wrapper's constants stay authoritative. A fresh object on every call.</summary>
        public static CrossingGateParameters SlipShipped => new CrossingGateParameters
        {
            TrendFrames = SlipCrossingGate.TrendFrames,
            SourceCeiling = SlipCrossingGate.SourceCeiling,
            MinFrameRise = SlipCrossingGate.MinFrameRise,
            Rise = SlipCrossingGate.SlipRise,
            GFall = SlipCrossingGate.GFall,
            HoldSeconds = SlipCrossingGate.HoldSeconds,
            NominalFrameSeconds = SlipCrossingGate.NominalFrameSeconds,
            HoldMaxFrames = SlipCrossingGate.HoldMaxFrames,
            MinGForCollapseTest = SlipCrossingGate.MinGForCollapseTest,
            WeightMode = TeachingWeightMode.Fixed,
            LearnCeiling = SlipCrossingGate.LearnCeiling,
        };

        /// <summary>The pre-extraction <see cref="LockCrossingGate"/> values - Slip's numbers, with the
        /// confidence-multiplying weight - built FROM <see cref="LockCrossingGate"/>'s own public
        /// constants so the wrapper's constants stay authoritative. A fresh object on every call.</summary>
        public static CrossingGateParameters LockShipped => new CrossingGateParameters
        {
            TrendFrames = LockCrossingGate.TrendFrames,
            SourceCeiling = LockCrossingGate.SourceCeiling,
            MinFrameRise = LockCrossingGate.MinFrameRise,
            Rise = LockCrossingGate.LockRise,
            GFall = LockCrossingGate.GFall,
            HoldSeconds = LockCrossingGate.HoldSeconds,
            NominalFrameSeconds = LockCrossingGate.NominalFrameSeconds,
            HoldMaxFrames = LockCrossingGate.HoldMaxFrames,
            MinGForCollapseTest = LockCrossingGate.MinGForCollapseTest,
            WeightMode = TeachingWeightMode.MultiplyConfidence,
            LearnCeiling = LockCrossingGate.LearnCeiling,
        };

        /// <summary>The slip-angle channel (docs\slip-angle-design.md section 9.5a): a 45-degree source
        /// ceiling and a 0.5-degree rise, otherwise Slip's values. The learn ceiling stays the 45-degree
        /// plausibility bound: the Max cap depends on the source (20 degrees native tyre slip angle, 30
        /// car-angle fallback) and is applied by the slip-angle learner itself. A fresh object on every
        /// call.</summary>
        public static CrossingGateParameters SlipAngle => new CrossingGateParameters
        {
            TrendFrames = 5,
            SourceCeiling = 45.0,
            MinFrameRise = 1e-6,
            Rise = 0.5,
            GFall = -0.05,
            HoldSeconds = 1.0,
            NominalFrameSeconds = 1.0 / 60.0,
            HoldMaxFrames = 500,
            MinGForCollapseTest = 0.05,
            WeightMode = TeachingWeightMode.Fixed,
            LearnCeiling = 45.0,
        };
    }

    /// <summary>
    /// THE SHARED CROSSING DETECTOR behind <see cref="SlipCrossingGate"/> and <see cref="LockCrossingGate"/>
    /// (docs\slip-smax-crossing-gate-design.md; extracted in slip-angle plan Task 7, docs\slip-angle-design.md
    /// section 9.5a). A frame may teach SMax only inside the hold window opened by a CROSSING: the source
    /// rising on this very frame and over <see cref="CrossingGateParameters.TrendFrames"/> while G falls,
    /// at the limit, not too fast a G collapse, not already saturated. What the window teaches is the
    /// onset snapshot (walked back to the G peak of the rise), never the runaway that follows.
    /// <para/>
    /// The two shipped gates were line-for-line the same logic with the same numbers; the only
    /// behavioural difference was how the teaching weight is formed, which is
    /// <see cref="CrossingGateParameters.WeightMode"/>. They remain separate OBJECTS (each engine channel
    /// owns its own instance and its own enable switch), so disabling or retuning one side still never
    /// touches the other.
    /// <para/>
    /// BIT-IDENTICAL to the pre-extraction gates with <see cref="CrossingGateParameters.SlipShipped"/> /
    /// <see cref="CrossingGateParameters.LockShipped"/> - pinned by <c>CrossingGateEquivalenceTests</c>
    /// against frozen verbatim copies. Do not reorder or "simplify" any arithmetic here.
    /// </summary>
    public sealed class CrossingGate
    {
        /// <summary>Default for <see cref="MaxGCollapseFractionPerSecond"/> - OFF. See
        /// <see cref="SlipCrossingGate.DefaultMaxGCollapseFractionPerSecond"/>.</summary>
        public const double DefaultMaxGCollapseFractionPerSecond = 0.0;

        /// <summary>Default for <see cref="MinSaturatedWheelsForRejection"/>. See
        /// <see cref="LockCrossingGate.DefaultMinSaturatedWheelsForRejection"/>.</summary>
        public const int DefaultMinSaturatedWheelsForRejection = 2;

        /// <summary>Default for <see cref="WalkBackGBandFraction"/>. See
        /// <see cref="LockCrossingGate.DefaultWalkBackGBandFraction"/>.</summary>
        public const double DefaultWalkBackGBandFraction = 0.10;

        /// <summary>Default for <see cref="NonCrossingWeight"/> - a non-crossing frame teaches nothing.</summary>
        public const double DefaultNonCrossingWeight = 0.0;

        private readonly int _trendFrames;
        private readonly int _historyLength;
        private readonly double _sourceCeiling;
        private readonly double _minFrameRise;
        private readonly double _rise;
        private readonly double _gFall;
        private readonly double _holdSeconds;
        private readonly double _nominalFrameSeconds;
        private readonly int _holdMaxFrames;
        private readonly double _minGForCollapseTest;
        private readonly TeachingWeightMode _weightMode;
        private readonly double _learnCeiling;

        private readonly double[] _basisHistory;
        private readonly double[] _gHistory;
        private readonly double[] _fallbackHistory;
        /// <summary>Per history slot: did this frame's basis rise from the frame written before it?</summary>
        private readonly bool[] _roseIntoHistory;
        private readonly Dictionary<string, FrameCounts> _counts = new Dictionary<string, FrameCounts>(StringComparer.Ordinal);

        private double _previousBasis = double.NaN;
        private double _previousG = double.NaN;
        private int _written;
        private int _next;
        private double _secondsSinceCrossing = double.PositiveInfinity;
        private int _framesSinceCrossing = int.MaxValue;
        private double _nonCrossingWeight = DefaultNonCrossingWeight;
        private double _crossingBasis;
        private double _crossingFallbackBasis;

        public CrossingGate(CrossingGateParameters p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (p.TrendFrames < 1) throw new ArgumentOutOfRangeException(nameof(p), "TrendFrames must be at least 1.");

            _trendFrames = p.TrendFrames;
            _historyLength = p.TrendFrames + 1;
            _sourceCeiling = p.SourceCeiling;
            _minFrameRise = p.MinFrameRise;
            _rise = p.Rise;
            _gFall = p.GFall;
            _holdSeconds = p.HoldSeconds;
            _nominalFrameSeconds = p.NominalFrameSeconds;
            _holdMaxFrames = p.HoldMaxFrames;
            _minGForCollapseTest = p.MinGForCollapseTest;
            _weightMode = p.WeightMode;
            _learnCeiling = ClampMath.IsFinite(p.LearnCeiling) && p.LearnCeiling > 0.0
                ? Math.Min(p.LearnCeiling, p.SourceCeiling)
                : p.SourceCeiling;

            _basisHistory = new double[_historyLength];
            _gHistory = new double[_historyLength];
            _fallbackHistory = new double[_historyLength];
            _roseIntoHistory = new bool[_historyLength];
        }

        /// <summary>The trend length this gate was built with.</summary>
        public int TrendFrames => _trendFrames;

        /// <summary>The source ceiling this gate was built with.</summary>
        public double SourceCeiling => _sourceCeiling;

        /// <summary>The live G-collapse limit, as a fraction of the previous G per second. 0 or less
        /// disables the test.</summary>
        public double MaxGCollapseFractionPerSecond { get; set; } = DefaultMaxGCollapseFractionPerSecond;

        /// <summary>The live saturated-wheel guard. 0 or less disables it.</summary>
        public int MinSaturatedWheelsForRejection { get; set; } = DefaultMinSaturatedWheelsForRejection;

        /// <summary>The live G-banded walk-back. 0 or less disables it (snapshot the detection frame).</summary>
        public double WalkBackGBandFraction { get; set; } = DefaultWalkBackGBandFraction;

        /// <summary>The weight (Fixed) or weight multiplier (MultiplyConfidence) of a frame outside a
        /// qualified window. Clamped to 0..1.</summary>
        public double NonCrossingWeight
        {
            get { return _nonCrossingWeight; }
            set { _nonCrossingWeight = ClampMath.To01(value); }
        }

        /// <summary>True while a crossing has occurred within the hold time AND the hold frame count.</summary>
        public bool Qualified
        {
            get { return _secondsSinceCrossing <= _holdSeconds && _framesSinceCrossing <= _holdMaxFrames; }
        }

        /// <summary>True on the frame a crossing was actually detected - diagnostics only.</summary>
        public bool CrossedThisFrame { get; private set; }

        /// <summary>
        /// The weight a candidate frame should teach SMax with. <see cref="TeachingWeightMode.Fixed"/>
        /// ignores <paramref name="confidence"/>; <see cref="TeachingWeightMode.MultiplyConfidence"/> scales it.
        /// </summary>
        public double TeachingWeight(double confidence = 1.0)
        {
            if (_weightMode == TeachingWeightMode.Fixed)
                return Qualified ? 1.0 : _nonCrossingWeight;

            if (!ClampMath.IsFinite(confidence) || confidence <= 0.0) return 0.0;
            return Qualified ? confidence : confidence * _nonCrossingWeight;
        }

        /// <summary>
        /// The onset snapshot while the hold window is open, else <paramref name="live"/>; clamped to the
        /// learn ceiling either way.
        /// <para/>
        /// NEVER THE LIVE VALUE INSIDE A WINDOW (SMax 100/90/70 fix, 2026-11-19). The frames after a
        /// crossing are the runaway - the wheel locking or spinning up to 100 - and teaching them is
        /// exactly what drove the at-limit P90 to 100 in the field logs: an unusable (0) snapshot used to
        /// fall back to the live value for the whole window. A crossing whose snapshot is unusable is now
        /// rejected in <see cref="Observe"/> (no window opens), so inside a window the snapshot is always
        /// the answer. Outside a window the live value is returned for the non-crossing weight
        /// (<see cref="NonCrossingWeight"/>, 0 by default, so it teaches nothing).
        /// </summary>
        public double TeachingBasis(double live)
        {
            return AtMostCeiling(Qualified ? _crossingBasis : live);
        }

        /// <summary><see cref="TeachingBasis"/> for the Raw-fallback key, snapshotted at the same frame.
        /// Inside a window an unusable (non-finite) fallback snapshot is returned as is - the caller's
        /// observation floor then teaches nothing for that key, never the live runaway.</summary>
        public double TeachingFallbackBasis(double liveFallback)
        {
            return AtMostCeiling(Qualified ? _crossingFallbackBasis : liveFallback);
        }

        private double AtMostCeiling(double basis)
            => ClampMath.IsFinite(basis) && basis > _learnCeiling ? _learnCeiling : basis;

        /// <summary>The learn ceiling this gate was built with (see <see cref="CrossingGateParameters.LearnCeiling"/>).</summary>
        public double LearnCeiling => _learnCeiling;

        private static bool IsUsable(double basis) => ClampMath.IsFinite(basis) && basis > 0.0;

        /// <summary>Clears the trend history AND the hold window (the candidate counts are kept).</summary>
        public void Reset()
        {
            _previousBasis = double.NaN;
            _previousG = double.NaN;
            _written = 0;
            _next = 0;
            Array.Clear(_roseIntoHistory, 0, _roseIntoHistory.Length);
            _secondsSinceCrossing = double.PositiveInfinity;
            _framesSinceCrossing = int.MaxValue;
            CrossedThisFrame = false;
            _crossingBasis = 0.0;
            _crossingFallbackBasis = 0.0;
        }

        /// <summary>
        /// Walking back from the detection frame while the basis keeps falling AND G stays inside the
        /// band, returns the slot with the HIGHEST G - the peak of this one rise. Returns
        /// <paramref name="newestSlot"/> whenever the walk cannot step.
        /// <para/>
        /// THE WALK STAYS INSIDE THE RISE (SMax 100/90/70 fix, 2026-11-19). A frame may be stepped onto
        /// only when it is itself part of the rise - reached by a rise from ITS own predecessor. "Lower
        /// than the frame after it" alone is always true of the flat frame the rise STARTED from, and
        /// that frame usually carries the highest G (grip has not let go yet), so the walk used to land
        /// on it and snapshot its basis: 0. FH6 frame 5300 (0 -> 7.2) and Viper frame 2955 (0 -> 67.5)
        /// both did exactly that. Whether a frame rose from its predecessor is recorded when the frame
        /// is written (<see cref="_roseIntoHistory"/>), so the oldest frame in the history is judged
        /// against its real predecessor even though that one has already left the ring.
        /// </summary>
        private int WalkBackSlot(int newestSlot)
        {
            double gAtDetection = _gHistory[newestSlot];
            if (WalkBackGBandFraction <= 0.0 || !ClampMath.IsFinite(gAtDetection) || gAtDetection <= 0.0)
                return newestSlot;

            double band = WalkBackGBandFraction * gAtDetection;
            int best = newestSlot;
            int here = newestSlot;
            // At most HistoryLength-1 steps, so the walk can never wrap onto the detection frame.
            for (int step = 0; step < _historyLength - 1; step++)
            {
                int previous = (here - 1 + _historyLength) % _historyLength;
                if (!(_basisHistory[previous] < _basisHistory[here])) break;      // left this rise
                if (!_roseIntoHistory[previous]) break;                           // previous is where the rise started
                if (Math.Abs(_gHistory[previous] - gAtDetection) > band) break;   // outside the G band
                here = previous;
                if (_gHistory[here] > _gHistory[best]) best = here;
            }
            return best;
        }

        /// <summary>
        /// Folds one engaged frame into the trend and ages the hold window. Returns <see cref="Qualified"/>.
        /// See <see cref="SlipCrossingGate.Observe"/> / <see cref="LockCrossingGate.Observe"/> for the
        /// meaning of each argument.
        /// <para/>
        /// The inline comments below are CONDENSED. The full rationale for every step (why the arm
        /// condition needs at-limit, why the collapse test self-scales, why saturation is read first,
        /// the walk-back measurements) is kept verbatim in the frozen pre-extraction copies
        /// <c>QAdvanceFeedback.Tests\Frozen\FrozenSlipCrossingGate.cs</c> and
        /// <c>QAdvanceFeedback.Tests\Frozen\FrozenLockCrossingGate.cs</c> (their <c>Observe</c>).
        /// </summary>
        public bool Observe(double basis, double fallbackBasis, double g, bool atLimit, double dtSeconds,
            int saturatedWheelCount = 0)
        {
            // Age the window first and always; an unusable dt ages by a nominal frame, never by nothing.
            double aged = ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0 ? dtSeconds : _nominalFrameSeconds;
            _secondsSinceCrossing += aged;
            if (_framesSinceCrossing < int.MaxValue) _framesSinceCrossing++;

            CrossedThisFrame = false;

            // Non-finite / negative telemetry never enters the trend history.
            if (!ClampMath.IsFinite(basis) || !ClampMath.IsFinite(g) || basis < 0.0)
                return Qualified;

            // Owner's rule 2: clamp to the ceiling. Rule 1: rising on THIS frame.
            if (basis > _sourceCeiling) basis = _sourceCeiling;
            if (ClampMath.IsFinite(fallbackBasis) && fallbackBasis > _sourceCeiling) fallbackBasis = _sourceCeiling;

            double previousBasis = _previousBasis;
            double previousG = _previousG;
            _previousBasis = basis;
            _previousG = g;
            bool risingThisFrame = ClampMath.IsFinite(previousBasis)
                                   && basis - previousBasis > _minFrameRise;

            // The G-collapse limit, self-scaling as a fraction of the car's own G per second.
            bool collapseTooFast = false;
            if (MaxGCollapseFractionPerSecond > 0.0
                && ClampMath.IsFinite(previousG)
                && previousG >= _minGForCollapseTest)
            {
                double dropFraction = (previousG - g) / previousG;
                if (dropFraction > 0.0)
                {
                    double perSecond = dropFraction / aged;
                    collapseTooFast = perSecond > MaxGCollapseFractionPerSecond;
                }
            }

            int slot = _next;
            _basisHistory[slot] = basis;
            _gHistory[slot] = g;
            _fallbackHistory[slot] = fallbackBasis;
            _roseIntoHistory[slot] = ClampMath.IsFinite(previousBasis) && previousBasis < basis;
            _next = (slot + 1) % _historyLength;
            if (_written < _historyLength) _written++;

            // ONE CROSSING PER EVENT (no re-arm inside a live window), ONLY AT THE LIMIT, and not once
            // the source has already let go on enough wheels. Read before anything below can change it.
            bool holdIsLive = Qualified;

            bool sourceAlreadyLetGo = MinSaturatedWheelsForRejection > 0
                                      && saturatedWheelCount >= MinSaturatedWheelsForRejection;

            if (_written >= _historyLength && !holdIsLive && atLimit && risingThisFrame && !collapseTooFast
                && !sourceAlreadyLetGo)
            {
                double deltaBasis = basis - _basisHistory[_next];
                double deltaG = g - _gHistory[_next];
                if (deltaBasis > _rise && deltaG < _gFall)
                {
                    // The onset snapshot, walked back to the G peak of this rise; both keys from one slot.
                    // A crossing whose snapshot is unusable is REJECTED - no window opens, so its runaway
                    // can never be taught in its place (see TeachingBasis); the next real crossing still arms.
                    int teachSlot = WalkBackSlot(slot);
                    if (IsUsable(_basisHistory[teachSlot]))
                    {
                        _secondsSinceCrossing = 0.0;
                        _framesSinceCrossing = 0;
                        CrossedThisFrame = true;
                        _crossingBasis = _basisHistory[teachSlot];
                        _crossingFallbackBasis = _fallbackHistory[teachSlot];
                    }
                }
            }

            return Qualified;
        }

        /// <summary>Records that one candidate frame was seen for a key, and whether it qualified.</summary>
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

        /// <summary>Every candidate frame for this key - recorded, deliberately not consumed.</summary>
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

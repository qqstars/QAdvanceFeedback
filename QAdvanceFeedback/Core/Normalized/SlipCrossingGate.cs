using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// WHICH FRAMES MAY TEACH SLIP'S SMax (docs\slip-smax-crossing-gate-design.md).
    /// <para/>
    /// Slip's SMax used to be taught by a single boolean on total G magnitude
    /// (<c>physicalRatioNow &gt;= PhysicalLimitRatioThreshold</c>), which has no direction, no speed
    /// condition and no plausibility ceiling. Measured on the owner's own 1.8.2 capture that fired on
    /// 30.9% of frames, of which 52.6% were BRAKING frames - deceleration G qualifying a frame as "at
    /// the slip limit", for a channel that measures a traction phenomenon. The practical failure is a
    /// standing start: launch G is genuinely near the car's peak, so the gate opens while the source is
    /// reading full wheelspin, and ten seconds of launch pinned the learned ceiling at 99.8 with full
    /// confidence for the rest of the session (the owner's own field observation of Slip SMax = 100).
    /// <para/>
    /// THE SIGNATURE THIS DETECTS. Crossing the traction limit means the tyre is past the peak of its
    /// slip curve: more slip is buying LESS grip. So slip rising WHILE acceleration-G falls, and
    /// nothing else, qualifies a frame. A launch has no such moment - slip and G rise together - so it
    /// never qualifies, with no launch detection, no speed threshold and no clutch condition anywhere
    /// in this class. That is the whole point: the physics excludes the launch by construction rather
    /// than by a heuristic that has to be tuned per title.
    /// <para/>
    /// CAUSAL BY CONSTRUCTION. The trend compares this frame against the frame
    /// <see cref="TrendFrames"/> back - never a centred window. An earlier CSV-only analysis used a
    /// centred window and is not implementable live; it also over-counted the yield by ~6x because it
    /// ignored that <c>ComputeChannel</c> only teaches while the channel is ENGAGED. See the design
    /// document's own section 6 for the corrected, replay-measured yields.
    /// <para/>
    /// WHAT "accelG" IS. The caller passes <c>AchievedMotion</c>'s own magnitude, the same quantity
    /// that decides <c>physicallyAtLimit</c> upstream. On every frame of the whole 17-log corpus that
    /// is bit-identical to <c>|LongitudinalG|</c> (19,727 of 19,727 on the reference capture), so
    /// there is no second, competing definition to choose between.
    /// </summary>
    public sealed class SlipCrossingGate
    {
        /// <summary>How far back the trend reaches. Five frames is ~90ms at 56Hz - long enough that
        /// frame-to-frame telemetry noise cannot manufacture a crossing, short enough that the real
        /// event (which lasts a few tenths of a second) is not smeared away.</summary>
        public const int TrendFrames = 5;

        /// <summary>Minimum rise in the slip source over the trend, in source units (0-100), for a
        /// crossing. Deliberately an ABSOLUTE step rather than a fraction: the quantity being detected
        /// is "the source moved meaningfully", and a fractional test would make a 2 -&gt; 3 wobble near
        /// zero look identical to a 40 -&gt; 60 genuine break-away.</summary>
        public const double SlipRise = 1.0;

        /// <summary>Maximum change in acceleration-G over the trend for a crossing - i.e. G must be
        /// FALLING, not merely flat. Small and negative rather than 0.0 so that a plateau (which is
        /// the normal state at sustained maximum grip, and is NOT a crossing) does not qualify.</summary>
        public const double GFall = -0.05;

        /// <summary>How long one crossing keeps subsequent candidate frames qualified. A crossing is an
        /// instant; the moments either side of it are the same physical event, and teaching from one
        /// frame per event would starve the learner far worse than section 6's yields already do.
        /// <para/>
        /// WHAT THOSE FRAMES TEACH IS NOT THEIR OWN READING - see <see cref="TeachingBasis"/>. Holding
        /// the window open while teaching each frame's LIVE value was a real defect, measured on the
        /// owner's own c_1_8_1 capture: 311 of 324 taught frames were hold frames, not one of the 58
        /// frames at basis >= 80 was a crossing, and the ceiling read 100 while the crossings themselves
        /// peaked at 64.5. The window was calibrating against the far side of the break-away instead of
        /// its onset.</summary>
        public const double HoldSeconds = 1.0;

        /// <summary>
        /// Weight given to a candidate frame that is NOT part of a crossing. Zero by default, which is
        /// the whole design: a launch teaches nothing.
        /// <para/>
        /// WHY THIS IS A WEIGHT AND NOT A SKIP - the crux of the design, and easy to "simplify" wrongly.
        /// <see cref="OnlineDistributionLearner"/> counts two different things: <c>Count</c> advances
        /// once per CALL regardless of weight (it drives
        /// <see cref="KeyedScaleLearner.CeilingHandoverConfidence"/> and the forgetting clock), while
        /// <c>PositiveSampleCount</c> and the histogram itself are WEIGHT-derived. Folding a
        /// non-crossing candidate in at weight 0.0 therefore contributes nothing to the VALUE while
        /// preserving the sample economics - and, critically, keeps the decay running in wall-clock
        /// terms. Dropping the call instead would stretch the forgetting half-life in proportion to how
        /// many frames were dropped, which is strictly worse.
        /// <para/>
        /// Adjustable rather than a constant because the design document measured 0.05 and rejected it
        /// (SMax is a P90, so any contaminant occupying more than ~10% of the weighted pool owns the
        /// answer outright - a 20s launch stayed pinned at 87.1), and a future title may want the knob
        /// without a rebuild. Anything above zero re-admits launch contamination in proportion.
        /// </summary>
        public const double DefaultNonCrossingWeight = 0.0;

        /// <summary>
        /// Frame time assumed when the caller cannot supply a usable one.
        /// <para/>
        /// NOT cosmetic. <c>NormalizedWheelLockSlipEngine</c> passes <c>0.0</c> for <c>dtSeconds</c>
        /// whenever the title reports no frame time at all (its own
        /// <c>sample.Dt.HasValue &amp;&amp; TotalSeconds &gt; 0.0</c> ternary), and a hold window aged by
        /// zero NEVER EXPIRES - one crossing would then qualify every remaining frame of the session,
        /// which is the exact failure this whole class exists to prevent. Ageing by a nominal frame
        /// instead keeps the window bounded in frames when it cannot be bounded in seconds.
        /// </summary>
        public const double NominalFrameSeconds = 1.0 / 60.0;

        /// <summary>
        /// Hard backstop on how many frames one crossing may keep qualified, whatever the frame times
        /// say. <see cref="NominalFrameSeconds"/> already handles dt == 0; this additionally bounds a
        /// caller feeding a tiny-but-positive dt (say 1e-9 every frame), which would otherwise take
        /// billions of frames to expire. Deliberately generous - 500 frames is a full second at 500 Hz,
        /// far above any real telemetry rate - so it can never bite during normal operation and is a
        /// safety net rather than a tuning knob.
        /// </summary>
        public const int HoldMaxFrames = 500;

        private const int HistoryLength = TrendFrames + 1;

        private readonly double[] _basisHistory = new double[HistoryLength];
        private readonly double[] _gHistory = new double[HistoryLength];
        private readonly Dictionary<string, FrameCounts> _counts = new Dictionary<string, FrameCounts>(StringComparer.Ordinal);

        private int _written;
        private int _next;
        private double _secondsSinceCrossing = double.PositiveInfinity;
        private int _framesSinceCrossing = int.MaxValue;
        private double _nonCrossingWeight = DefaultNonCrossingWeight;

        /// <summary>The configured source's own reading at the instant the live crossing was detected -
        /// what every frame in the hold window teaches. See <see cref="TeachingBasis"/>.</summary>
        private double _crossingBasis;

        /// <summary>The <see cref="_crossingBasis"/> twin for the Raw-fallback key, snapshotted at the
        /// same instant. Both keys must learn their ceilings on the SAME definition or the divergence
        /// test the engine runs between them compares quantities that are not comparable.</summary>
        private double _crossingFallbackBasis;

        /// <summary>See <see cref="DefaultNonCrossingWeight"/>. Clamped to 0..1.</summary>
        public double NonCrossingWeight
        {
            get { return _nonCrossingWeight; }
            set { _nonCrossingWeight = ClampMath.To01(value); }
        }

        /// <summary>True while a crossing has occurred within the last <see cref="HoldSeconds"/> AND
        /// within <see cref="HoldMaxFrames"/> frames - see that constant for why the frame bound is
        /// needed as well as the time one.</summary>
        public bool Qualified
        {
            get { return _secondsSinceCrossing <= HoldSeconds && _framesSinceCrossing <= HoldMaxFrames; }
        }

        /// <summary>True on the frame a crossing was actually detected - diagnostics only; the gate
        /// itself acts on <see cref="Qualified"/>, which spans the hold window.</summary>
        public bool CrossedThisFrame { get; private set; }

        /// <summary>The weight a candidate frame should teach SMax with, given the current state.</summary>
        public double TeachingWeight { get { return Qualified ? 1.0 : _nonCrossingWeight; } }

        /// <summary>
        /// The value a candidate frame should teach SMax - the reading captured AT THE CROSSING while
        /// the hold window is open, and the frame's own live reading otherwise.
        /// <para/>
        /// THIS IS THE POINT OF THE WHOLE CLASS AND THE EASIEST THING TO GET WRONG. The crossing detects
        /// the instant the tyre goes past the peak of its slip curve; that instant's reading is what "at
        /// the traction limit" means. What the source does over the following second is the break-away
        /// running away - grip already lost - and on a real capture it runs to full scale. Teaching each
        /// hold frame its own live value therefore calibrated against the far side of the event:
        /// measured on c_1_8_1, the crossings peaked at 64.5 while the taught pool's P90 was 100.0, and
        /// every single one of the 58 taught frames at >= 80 was a hold frame rather than a crossing.
        /// <para/>
        /// Repeating the onset value across the window is what makes the hold safe: it still gives the
        /// learner the sample count it needs for one physical event (teaching only crossing INSTANTS
        /// yielded 13 samples on that capture, against the 20 the percentile needs and the 100 full
        /// readiness needs), without letting the runaway tail define the ceiling.
        /// <para/>
        /// This is also how Lock has always worked - its corner-local detector fires at the moment G
        /// stops rising and calibrates against THAT moment.
        /// </summary>
        /// <param name="liveBasis">This frame's own calibration basis, used when no crossing is live
        /// (a non-qualified candidate, which teaches at <see cref="NonCrossingWeight"/> - zero by
        /// default, in which case the value is immaterial anyway).</param>
        public double TeachingBasis(double liveBasis)
        {
            return Qualified && IsUsable(_crossingBasis) ? _crossingBasis : liveBasis;
        }

        /// <summary><see cref="TeachingBasis"/> for the Raw-fallback key - see
        /// <see cref="_crossingFallbackBasis"/>.</summary>
        public double TeachingFallbackBasis(double liveFallbackBasis)
        {
            return Qualified && IsUsable(_crossingFallbackBasis) ? _crossingFallbackBasis : liveFallbackBasis;
        }

        /// <summary>
        /// A snapshot the learner would actually accept. <c>ObserveAtPhysicalLimit</c> discards a
        /// non-finite or non-positive value, so handing it one would teach NOTHING for the whole hold
        /// window while <see cref="RecordCandidate"/> counted those frames as qualified - failing
        /// silently rather than falling back. Returning the live reading instead degrades to
        /// pre-snapshot behaviour for that window, which is imperfect but not silent.
        /// </summary>
        private static bool IsUsable(double basis)
        {
            return ClampMath.IsFinite(basis) && basis > 0.0;
        }

        /// <summary>
        /// Clears the trend history AND the hold window. Called wherever the channel stops being
        /// engaged, for the same reason <c>atLimitLastG</c> is nulled at those points: the frames either
        /// side of a disengagement are not a continuous series, so a trend measured across the gap is
        /// comparing two unrelated moments.
        /// </summary>
        public void Reset()
        {
            _written = 0;
            _next = 0;
            _secondsSinceCrossing = double.PositiveInfinity;
            _framesSinceCrossing = int.MaxValue;
            CrossedThisFrame = false;
            _crossingBasis = 0.0;
            _crossingFallbackBasis = 0.0;
        }

        /// <summary>
        /// Folds one engaged frame into the trend and ages the hold window. Returns
        /// <see cref="Qualified"/> for convenience.
        /// </summary>
        /// <param name="slipBasis">This frame's slip calibration basis (0-100), the SAME quantity that
        /// would be taught to the learner - so the trend measures the thing being calibrated, not a
        /// neighbouring proxy.</param>
        /// <param name="fallbackBasis">This frame's Raw-fallback calibration basis, snapshotted at the
        /// crossing alongside <paramref name="slipBasis"/> - see <see cref="_crossingFallbackBasis"/>.</param>
        /// <param name="accelG">This frame's achieved motion magnitude in g.</param>
        /// <param name="atLimit">Whether this frame is itself at the car's own traction limit (the
        /// engine's <c>physicallyAtLimit</c>). A crossing may only FIRE on such a frame - see the
        /// remarks at the arm condition below. The trend history is fed regardless.</param>
        /// <param name="dtSeconds">Frame time. Non-finite or non-positive values age the window by
        /// <see cref="NominalFrameSeconds"/> instead - see that constant.</param>
        public bool Observe(double slipBasis, double fallbackBasis, double accelG, bool atLimit, double dtSeconds)
        {
            // AGE THE WINDOW FIRST, AND ALWAYS. An unusable frame time ages it by a nominal frame rather
            // than by nothing: the engine passes 0.0 whenever the title reports no frame time, and a
            // window aged by zero never expires, so one crossing would qualify every remaining frame of
            // the session - the exact failure this class exists to prevent. The frame counter is the
            // unconditional backstop for a tiny-but-positive dt.
            double aged = ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0 ? dtSeconds : NominalFrameSeconds;
            _secondsSinceCrossing += aged;
            if (_framesSinceCrossing < int.MaxValue) _framesSinceCrossing++;

            CrossedThisFrame = false;

            // INPUT VALIDATION - every one of these values originates in game telemetry, so any of them
            // can arrive NaN, infinite, or negative. A non-finite reading must not enter the trend
            // history: it would silently suppress detection for the next TrendFrames frames, and an
            // INFINITE basis would satisfy the rise test outright, arm a window, and then hand
            // ObserveAtPhysicalLimit a value it rejects - teaching nothing for a whole second while
            // RecordCandidate counted those frames as qualified. Skipping the frame keeps the window's
            // own clock running (aged above) and leaves the trend differencing real readings.
            if (!ClampMath.IsFinite(slipBasis) || !ClampMath.IsFinite(accelG) || slipBasis < 0.0)
                return Qualified;

            int slot = _next;
            _basisHistory[slot] = slipBasis;
            _gHistory[slot] = accelG;
            _next = (slot + 1) % HistoryLength;
            if (_written < HistoryLength) _written++;

            // ONE CROSSING PER EVENT. A crossing may only fire when no hold window is already live.
            //
            // WHY - the second half of the same defect TeachingBasis documents, and the reason
            // snapshotting the onset is not sufficient on its own. The condition "slip rising while G
            // falls" is satisfied by the WHOLE break-away, not just its onset: once the tyre lets go,
            // the source runs from ~35 to 100 over a few frames while G collapses, which trips the
            // detector again and overwrites the snapshot with the runaway's own value. The snapshot then
            // leaks forward, because the refreshed window stays open into the next grip phase - so the
            // following corner's genuinely-at-limit frames teach 100 as well.
            //
            // A further rise inside a live window is the same physical event continuing, not a new
            // traction limit. Holding the arm shut until the window expires makes "one traction-limit
            // event teaches one value" true by construction, which is what the hold was always meant to
            // express. The trend history below keeps being fed on every engaged frame regardless, so the
            // series it differences against is still continuous.
            bool holdIsLive = Qualified;

            // AND THE FRAME MUST ITSELF BE AT THE LIMIT. The snapshot this arms is defined as "the
            // source's reading at the traction limit", so it has to be sampled while the car is
            // actually there. Without this the detector fires on frames the engine would never let
            // teach - a spin, a kerb strike, wheels off the ground, the tail of a break-away - all of
            // which show slip rising while G falls, and all of which then set the snapshot and open a
            // window that reaches forward into the next genuinely-at-limit frames. Measured on c_1_8_1
            // with only the one-crossing-per-event rule in place: 289 of 291 taught frames were hold
            // frames whose snapshot had been armed by a NON-candidate crossing, and the ceiling still
            // read 100 while the two candidate crossings sat at 52.7.
            //
            // The trend history above is deliberately fed on every engaged frame anyway, so the series
            // being differenced stays continuous through the frames that may not arm it.
            if (_written >= HistoryLength && !holdIsLive && atLimit)
            {
                double deltaBasis = slipBasis - _basisHistory[_next];
                double deltaG = accelG - _gHistory[_next];
                if (deltaBasis > SlipRise && deltaG < GFall)
                {
                    _secondsSinceCrossing = 0.0;
                    _framesSinceCrossing = 0;
                    CrossedThisFrame = true;
                    // THE ONSET SNAPSHOT - see TeachingBasis. Taken at the DETECTION frame, the top of
                    // the confirmed rise: the earliest reading at which more slip is demonstrably
                    // buying less grip. The frame TrendFrames back is the pre-crossing value (still
                    // gripping) and would under-report; anything later is the break-away running away.
                    _crossingBasis = slipBasis;
                    _crossingFallbackBasis = fallbackBasis;
                }
            }

            return Qualified;
        }

        /// <summary>
        /// Records that one candidate frame was seen for a key, and whether it qualified.
        /// <para/>
        /// <see cref="ValidFrames"/> is the qualified count; <see cref="TotalFrames"/> is every
        /// candidate. TOTAL IS DELIBERATELY NOT CONSUMED BY ANYTHING - it is recorded now so that a
        /// future Lock/Slip balancing decision can weigh "how much evidence existed" separately from
        /// "how much of it qualified" without needing a new capture campaign first.
        /// </summary>
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

        /// <summary>Every candidate frame for this key. See <see cref="RecordCandidate"/> - nothing
        /// consumes this yet, by design.</summary>
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

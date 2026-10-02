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

        /// <summary>The live G-collapse limit - see
        /// <see cref="DefaultMaxGCollapseFractionPerSecond"/>. Set to 0 or less to DISABLE the test
        /// entirely, which is the kill switch for it.</summary>
        public double MaxGCollapseFractionPerSecond { get; set; } = DefaultMaxGCollapseFractionPerSecond;

        /// <summary>
        /// THE SATURATED-WHEEL GUARD (owner, 2026-09-25 - v1.1.0). How many of the four wheels may
        /// already be reading full scale before a crossing is rejected as "not the onset".
        /// <para/>
        /// SLIP'S OWN COPY of <see cref="LockCrossingGate.DefaultMinSaturatedWheelsForRejection"/> - see
        /// that constant for the mechanism, for why the test is car/surface/weather-neutral, and for why
        /// the owner's own walk-back alternative was measured and declined. Duplicated rather than
        /// shared for this class's own standing reason: either channel must be switchable without
        /// touching the other, and the two are free to diverge as evidence accumulates.
        /// <para/>
        /// SLIP GAINS MORE FROM IT THAN LOCK DOES, which is why it ships on for both. Replayed over the
        /// same corpus, Slip crossing snapshots:
        /// <code>
        ///   log                  current p90 / max      with guard p90 / max
        ///   FH6      115227          72.1 / 100.0           57.2 / 86.3
        ///   F1 25    20260825        57.0 / 100.0           49.2 / 76.8
        ///   old-logs 20260815        84.4 / 100.0           74.8 / 74.8
        ///   F1 25    114458          72.7 /  98.1           25.3 / 83.3
        ///   F1 25    20260831        79.6 /  99.0           37.9 / 79.6
        ///   seven further logs               bit-identical
        /// </code>
        /// THREE logs sat at exactly 100.0 and all three come down, while the median barely moves
        /// (16.8 -> 14.2, 26.4 -> 25.9, 62.2 -> 58.9) and the crossing count is nearly untouched
        /// (200 -> 196, 118 -> 110, 45 -> 42) - i.e. it removes the saturated tail without thinning the
        /// evidence the ceiling is actually built from.
        /// </summary>
        public const int DefaultMinSaturatedWheelsForRejection = 2;

        /// <summary>How close to <see cref="SourceCeiling"/> a single wheel must read to count as
        /// saturated. A hair below the ceiling rather than an exact compare, since the aggregate and the
        /// per-wheel values both arrive through floating-point arithmetic.</summary>
        public const double SaturatedWheelThreshold = SourceCeiling - 1e-6;

        /// <summary>The live saturated-wheel guard - see
        /// <see cref="DefaultMinSaturatedWheelsForRejection"/>. Set to 0 or less to DISABLE the guard
        /// entirely, which is the kill switch for it; the two channels own independent gate objects, so
        /// switching one off leaves the other exactly as it was.</summary>
        public int MinSaturatedWheelsForRejection { get; set; } = DefaultMinSaturatedWheelsForRejection;

        /// <summary>
        /// THE G-BANDED WALK-BACK (owner, 2026-09-25 - v1.1.0). Slip's own copy of
        /// <see cref="LockCrossingGate.DefaultWalkBackGBandFraction"/> - see that constant for the
        /// mechanism, for the corpus measurement of the band width, and for the honestly-disclosed cost.
        /// Duplicated rather than shared for this class's own standing reason: either channel must be
        /// switchable without touching the other.
        /// <para/>
        /// SLIP'S OWN SHARE of the measured result, on top of the saturated-wheel guard: 20260831 Slip
        /// 37.9 -&gt; 32.5, 20260815-230140 Slip 66.4 -&gt; 58.8, 20260816-212439 Slip 58.5 -&gt; 53.0,
        /// while FH6 Slip, the Viper capture's Slip and two further sessions are bit-identical.
        /// </summary>
        /// <summary>
        /// THE COST, ACCEPTED DELIBERATELY BY THE OWNER (2026-09-25). This walk-back is in tension with
        /// <see cref="KeyedScaleLearner.CanonicalAtLimitAnchor"/> and that was weighed before enabling it.
        /// <para/>
        /// SMax was previously defined so that a reading AT the grip limit Rescales to EXACTLY 80 - the
        /// input position of the four-range curve's top knot ("60-80: the ideal band, up to the measured
        /// grip limit; 80-100: past the limit"). Reading SMax at the grip PEAK instead of at the
        /// detection frame teaches a ceiling BELOW the detection value, so an at-limit reading now maps
        /// to <c>80 / (1 - drop)</c> - above that knot. The effect is one-way by construction: the walk
        /// can only ever lower SMax, never raise it, so it does not average out.
        /// <para/>
        /// MEASURED SIZE. On the synthetic acceptance suite, whose ramp gives up a uniform 10%, a raw 90
        /// taught at the limit Rescales to 88.9 rather than 80. On the real log corpus the drift is
        /// smaller - median SMax -4.2%, so the anchor lands nearer 83.5 - because the walk averages only
        /// 1.16 frames deep rather than the suite's full 5. Either way, genuinely-at-limit braking now
        /// reads slightly INTO the "past the limit" band rather than exactly at its edge, which is a
        /// real behavioural change and not merely a louder output.
        /// <para/>
        /// WHY IT IS WORTH IT (the owner's call, recorded here rather than argued): the defect being
        /// fixed is SMax learning 98-100 on titles whose source saturates, which mis-scales the ENTIRE
        /// channel; a few points of anchor drift is the smaller error. Tests that pinned the exact
        /// 80-anchor identity were re-baselined to the new value at the same time, deliberately and in
        /// one place, rather than loosened.
        /// <para/>
        /// Set to 0 or less to DISABLE the walk-back entirely, which restores the pre-1.1.0 behaviour of
        /// snapshotting the detection frame and with it the exact 80-anchor identity.
        /// </summary>
        public const double DefaultWalkBackGBandFraction = 0.10;

        /// <summary>The live G-banded walk-back - see <see cref="DefaultWalkBackGBandFraction"/>. Set to
        /// 0 or less to DISABLE the walk-back entirely (snapshot the detection frame, as before v1.1.0),
        /// which is the kill switch for it.</summary>
        public double WalkBackGBandFraction { get; set; } = DefaultWalkBackGBandFraction;

        /// <summary>The Raw-fallback basis per slot, so the walk-back snapshots BOTH keys at the SAME
        /// frame - see <see cref="TeachingFallbackBasis"/> on why they must agree.</summary>
        private readonly double[] _fallbackHistory = new double[HistoryLength];

        /// <summary>
        /// Picks the frame this crossing should teach from: walking back from the detection frame while
        /// the basis keeps falling AND the G stays inside <see cref="WalkBackGBandFraction"/>, return the
        /// slot with the HIGHEST G - the traction peak of this one rise. Returns
        /// <paramref name="newestSlot"/> unchanged whenever the walk cannot step, so every degenerate
        /// case reproduces the pre-1.1.0 snapshot exactly.
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
            for (int step = 0; step < HistoryLength - 1; step++)
            {
                int previous = (here - 1 + HistoryLength) % HistoryLength;
                if (!(_basisHistory[previous] < _basisHistory[here])) break;      // left this rise
                if (Math.Abs(_gHistory[previous] - gAtDetection) > band) break;   // outside the G band
                here = previous;
                if (_gHistory[here] > _gHistory[best]) best = here;
            }
            return best;
        }

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
            return AtMostCeiling(Qualified && IsUsable(_crossingBasis) ? _crossingBasis : liveBasis);
        }

        /// <summary><see cref="TeachingBasis"/> for the Raw-fallback key - see
        /// <see cref="_crossingFallbackBasis"/>.</summary>
        public double TeachingFallbackBasis(double liveFallbackBasis)
        {
            return AtMostCeiling(Qualified && IsUsable(_crossingFallbackBasis) ? _crossingFallbackBasis : liveFallbackBasis);
        }

        /// <summary>
        /// A snapshot the learner would actually accept. <c>ObserveAtPhysicalLimit</c> discards a
        /// non-finite or non-positive value, so handing it one would teach NOTHING for the whole hold
        /// window while <see cref="RecordCandidate"/> counted those frames as qualified - failing
        /// silently rather than falling back. Returning the live reading instead degrades to
        /// pre-snapshot behaviour for that window, which is imperfect but not silent.
        /// </summary>
        /// <summary>Rule 2 applied on the way OUT as well as the way in: the live-value fallback
        /// path never passed through Observe's clamp, so a live 100.0000000001 could still leave
        /// this gate and become SMax. Caught by CrossingGateHeldSourceTests.</summary>
        private static double AtMostCeiling(double basis)
            => ClampMath.IsFinite(basis) && basis > SourceCeiling ? SourceCeiling : basis;

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
        /// <param name="saturatedWheelCount">How many of THIS frame's four CONFIGURED wheel readings are
        /// at the source's own ceiling - see <see cref="DefaultMinSaturatedWheelsForRejection"/>. The
        /// configured wheels specifically, because <paramref name="slipBasis"/> is built from them and it
        /// is that basis the crossing is defined on; the Raw-fallback snapshot rides along with the same
        /// decision. DEFAULTS TO 0 - "the caller did not report saturation" - so every pre-existing
        /// caller and test keeps its previous behaviour byte for byte.</param>
        public bool Observe(double slipBasis, double fallbackBasis, double accelG, bool atLimit, double dtSeconds,
            int saturatedWheelCount = 0)
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
            if (slipBasis > SourceCeiling) slipBasis = SourceCeiling;
            if (ClampMath.IsFinite(fallbackBasis) && fallbackBasis > SourceCeiling) fallbackBasis = SourceCeiling;

            double previousBasis = _previousBasis;
            double previousG = _previousG;
            _previousBasis = slipBasis;
            _previousG = accelG;
            bool risingThisFrame = ClampMath.IsFinite(previousBasis)
                                   && slipBasis - previousBasis > MinFrameRise;

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
                double dropFraction = (previousG - accelG) / previousG;
                if (dropFraction > 0.0)
                {
                    double perSecond = dropFraction / aged;
                    collapseTooFast = perSecond > MaxGCollapseFractionPerSecond;
                }
            }

            int slot = _next;
            _basisHistory[slot] = slipBasis;
            _gHistory[slot] = accelG;
            _fallbackHistory[slot] = fallbackBasis;
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
            // THE SATURATED-WHEEL GUARD (v1.1.0) - see DefaultMinSaturatedWheelsForRejection for this
            // channel's own measurement, and LockCrossingGate's for the mechanism and the declined
            // walk-back alternative. A crossing whose wheels are already pegged is the break-away
            // running away, not the traction limit being reached.
            bool sourceAlreadyLetGo = MinSaturatedWheelsForRejection > 0
                                      && saturatedWheelCount >= MinSaturatedWheelsForRejection;

            if (_written >= HistoryLength && !holdIsLive && atLimit && risingThisFrame && !collapseTooFast
                && !sourceAlreadyLetGo)
            {
                double deltaBasis = slipBasis - _basisHistory[_next];
                double deltaG = accelG - _gHistory[_next];
                if (deltaBasis > SlipRise && deltaG < GFall)
                {
                    _secondsSinceCrossing = 0.0;
                    _framesSinceCrossing = 0;
                    CrossedThisFrame = true;
                    // THE ONSET SNAPSHOT - see TeachingBasis. Anything LATER than the detection frame is
                    // the break-away running away, so the snapshot never moves forward.
                    //
                    // REFINED IN v1.1.0 (the owner's own observation): it no longer sits ON the detection
                    // frame either. Confirming the crossing costs TrendFrames of evidence, so detection is
                    // structurally late and G has already fallen off its peak by the time we get here. The
                    // walk-back steps back to the grip peak OF THIS ONE RISE, bounded by the G band so it
                    // can never cross into an earlier, unrelated phase - the failure that made a plain
                    // "take the highest-G frame in the window" rule collapse a 74.0 snapshot to 11.8. A
                    // flat, blind step of TrendFrames back was ALSO measured and rejected: that lands on
                    // the pre-crossing value, still gripping, and under-reports badly.
                    int teachSlot = WalkBackSlot(slot);
                    _crossingBasis = _basisHistory[teachSlot];
                    _crossingFallbackBasis = _fallbackHistory[teachSlot];
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

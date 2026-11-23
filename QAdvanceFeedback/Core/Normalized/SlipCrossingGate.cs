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

        /// <summary>
        /// THE LEARN CEILING (owner, 2026-11-19). The highest SMax this gate may ever teach: every taught
        /// basis - the crossing snapshot inside a window, the live value outside one - is clamped to it.
        /// The source range is a hard 0-100, so a fully saturated source (100) can never be a grip-limit
        /// reading; 95 is the absolute hard limit, shared with <see cref="KeyedScaleLearner.MaxLearnedCeiling"/>
        /// (the learner's own cap) and the typed SMax on the settings page and ShakeIt panel.
        /// Detection is unaffected: the trend history still clamps only to <see cref="SourceCeiling"/>.
        /// </summary>
        public const double LearnCeiling = KeyedScaleLearner.MaxLearnedCeiling;

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

        /// <summary>The shared detector this gate delegates to, built from
        /// <see cref="CrossingGateParameters.SlipShipped"/> (slip-angle plan Task 7). Every constant above
        /// is still this class's own and still the documented value; the parameter set mirrors them and
        /// <c>CrossingGateEquivalenceTests</c> pins both against the frozen pre-extraction copy. Each
        /// <see cref="SlipCrossingGate"/> owns its own instance, so Slip's gate stays independently
        /// switchable and tunable from Lock's.</summary>
        private readonly CrossingGate _gate = new CrossingGate(CrossingGateParameters.SlipShipped);

        public SlipCrossingGate()
        {
            _gate.MaxGCollapseFractionPerSecond = DefaultMaxGCollapseFractionPerSecond;
            _gate.MinSaturatedWheelsForRejection = DefaultMinSaturatedWheelsForRejection;
            _gate.WalkBackGBandFraction = DefaultWalkBackGBandFraction;
            _gate.NonCrossingWeight = DefaultNonCrossingWeight;
        }

        /// <summary>The live G-collapse limit - see
        /// <see cref="DefaultMaxGCollapseFractionPerSecond"/>. Set to 0 or less to DISABLE the test
        /// entirely, which is the kill switch for it.</summary>
        public double MaxGCollapseFractionPerSecond
        {
            get { return _gate.MaxGCollapseFractionPerSecond; }
            set { _gate.MaxGCollapseFractionPerSecond = value; }
        }

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
        public int MinSaturatedWheelsForRejection
        {
            get { return _gate.MinSaturatedWheelsForRejection; }
            set { _gate.MinSaturatedWheelsForRejection = value; }
        }

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
        public double WalkBackGBandFraction
        {
            get { return _gate.WalkBackGBandFraction; }
            set { _gate.WalkBackGBandFraction = value; }
        }

        /// <summary>See <see cref="DefaultNonCrossingWeight"/>. Clamped to 0..1.</summary>
        public double NonCrossingWeight
        {
            get { return _gate.NonCrossingWeight; }
            set { _gate.NonCrossingWeight = value; }
        }

        /// <summary>True while a crossing has occurred within the last <see cref="HoldSeconds"/> AND
        /// within <see cref="HoldMaxFrames"/> frames - see that constant for why the frame bound is
        /// needed as well as the time one.</summary>
        public bool Qualified => _gate.Qualified;

        /// <summary>True on the frame a crossing was actually detected - diagnostics only; the gate
        /// itself acts on <see cref="Qualified"/>, which spans the hold window.</summary>
        public bool CrossedThisFrame => _gate.CrossedThisFrame;

        /// <summary>The weight a candidate frame should teach SMax with, given the current state.</summary>
        public double TeachingWeight => _gate.TeachingWeight();

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
        public double TeachingBasis(double liveBasis) => _gate.TeachingBasis(liveBasis);

        /// <summary><see cref="TeachingBasis"/> for the Raw-fallback key - both keys are snapshotted
        /// at the same frame, so the engine's divergence test between them compares like with like.
        /// A non-finite or non-positive snapshot falls back to the live reading rather than teaching
        /// nothing silently, and the result is clamped to <see cref="SourceCeiling"/> on the way out
        /// (caught by CrossingGateHeldSourceTests).</summary>
        public double TeachingFallbackBasis(double liveFallbackBasis) => _gate.TeachingFallbackBasis(liveFallbackBasis);

        /// <summary>
        /// Clears the trend history AND the hold window. Called wherever the channel stops being
        /// engaged, for the same reason <c>atLimitLastG</c> is nulled at those points: the frames either
        /// side of a disengagement are not a continuous series, so a trend measured across the gap is
        /// comparing two unrelated moments.
        /// </summary>
        public void Reset() => _gate.Reset();

        /// <summary>
        /// Folds one engaged frame into the trend and ages the hold window. Returns
        /// <see cref="Qualified"/> for convenience.
        /// </summary>
        /// <param name="slipBasis">This frame's slip calibration basis (0-100), the SAME quantity that
        /// would be taught to the learner - so the trend measures the thing being calibrated, not a
        /// neighbouring proxy.</param>
        /// <param name="fallbackBasis">This frame's Raw-fallback calibration basis, snapshotted at the
        /// crossing alongside <paramref name="slipBasis"/> - see <see cref="TeachingFallbackBasis"/>.</param>
        /// <param name="accelG">This frame's achieved motion magnitude in g.</param>
        /// <param name="atLimit">Whether this frame is itself at the car's own traction limit (the
        /// engine's <c>physicallyAtLimit</c>). A crossing may only FIRE on such a frame - see the
        /// remarks at the arm condition in <see cref="CrossingGate.Observe"/> (full text in the frozen
        /// copy, QAdvanceFeedback.Tests\Frozen\FrozenSlipCrossingGate.cs). The trend history is fed regardless.</param>
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
            => _gate.Observe(slipBasis, fallbackBasis, accelG, atLimit, dtSeconds, saturatedWheelCount);

        /// <summary>
        /// Records that one candidate frame was seen for a key, and whether it qualified.
        /// <para/>
        /// <see cref="ValidFrames"/> is the qualified count; <see cref="TotalFrames"/> is every
        /// candidate. TOTAL IS DELIBERATELY NOT CONSUMED BY ANYTHING - it is recorded now so that a
        /// future Lock/Slip balancing decision can weigh "how much evidence existed" separately from
        /// "how much of it qualified" without needing a new capture campaign first.
        /// </summary>
        public void RecordCandidate(string key, bool qualified) => _gate.RecordCandidate(key, qualified);

        /// <summary>Candidate frames for this key that were part of a crossing.</summary>
        public long ValidFrames(string key) => _gate.ValidFrames(key);

        /// <summary>Every candidate frame for this key. See <see cref="RecordCandidate"/> - nothing
        /// consumes this yet, by design.</summary>
        public long TotalFrames(string key) => _gate.TotalFrames(key);
    }
}

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

        /// <summary>
        /// THE SATURATED-WHEEL GUARD (owner, 2026-09-25 - v1.1.0). How many of the four wheels may
        /// already be reading full scale before a crossing is rejected as "not the onset".
        /// <para/>
        /// WHAT IT FIXES, which rules 1 and 2 did NOT. Those require the AGGREGATE basis to be rising
        /// on the detection frame, and that is honestly satisfied long after half the car has let go:
        /// in the owner's own FH6 capture (session-20260925-115227, frame 7617) both front wheels had
        /// been pegged at 100 for nine frames while the rears were still climbing, so the aggregate was
        /// genuinely rising, G was genuinely falling, and the gate taught 98.25 - which became SMax.
        /// The trend window is five frames; FH6's lock source travels 66 -> 100 in about fourteen, so
        /// the detection frame is structurally LATE and no amount of tuning the rise/fall thresholds
        /// reaches the onset.
        /// <para/>
        /// WHY IT IS CAR-, SURFACE- AND WEATHER-NEUTRAL (the owner's standing constraint). It reads
        /// ONLY the source's own saturation - how many wheels are pegged at their own ceiling. It never
        /// touches G, grip, speed, surface or any learned reference, so there is nothing in it that a
        /// different car, a different surface or a different weather can be penalised by. A wheel at
        /// full scale means the same thing on ice as on slicks: that wheel's reading has stopped
        /// carrying information.
        /// <para/>
        /// AND IT CLOSES THE SMax = 100 PATH THAT RULES 1 AND 2 LEFT OPEN. A rise from 99.9 to 100.0 is
        /// still a rise, so a crossing detected on the frame the source first reaches full scale taught
        /// exactly 100 - observed in the corpus (session-20260825-201217: Lock max 100.0, Slip max
        /// 100.0). Lock's basis is the weighted aggregate, and across 1934 corpus frames with
        /// <c>Raw.All &gt;= 99.9</c> there was NOT ONE with fewer than two pegged wheels, so at 2 this
        /// guard rejects every such frame. <see cref="AtMostCeiling"/> remains the structural backstop.
        /// <para/>
        /// MEASURED ON THE WHOLE 11-LOG CORPUS, Lock channel, crossing snapshots:
        /// <code>
        ///   log                  current p90 / max      with guard p90 / max
        ///   FH6      115227          97.1 / 98.2            86.6 / 86.6
        ///   F1 25    114458          79.9 / 93.9            74.0 / 74.0
        ///   F1 25    20260825        66.1 / 100.0           64.9 / 80.2
        ///   F1 25    20260831        69.2 / 96.5            62.5 / 62.5
        ///   F1 25    20260816-2124   57.8 / 93.1            54.3 / 90.0
        ///   six further logs                 bit-identical
        /// </code>
        /// It never RAISES p90 or max on any log, and the crossing count barely moves (114 -> 111,
        /// 21 -> 18, 46 -> 44); FH6 itself is the one large relative loss, 13 -> 6, which is the point.
        /// <para/>
        /// WHY NOT WALK BACK TO THE PRE-SATURATION FRAME INSTEAD (the owner's own proposal, measured and
        /// declined). Their observation is exactly right and is guaranteed by construction: the arm
        /// condition below requires <c>deltaBasis &gt; LockRise</c> and <c>deltaG &lt; GFall</c>, so the
        /// frame <see cref="TrendFrames"/> back ALWAYS has a lower basis and a higher G than the
        /// detection frame - such a frame must exist. But snapshotting there over-corrects badly,
        /// because "basis lower, G higher" holds across the whole APPROACH to the limit, not just near
        /// the crossing: walking to the earliest such frame lands near the start of the braking zone.
        /// Measured, it lowered the max AND the median on every log in the corpus including the six this
        /// guard leaves untouched (20260902, never broken: max 39.8 -> 19.0; FH6 p50 73.8 -> 17.0;
        /// 114458 max 74.0 -> 17.7). Understating SMax is the DANGEROUS direction - Rescale is
        /// <c>source * (80 / SMax)</c>, so a low SMax inflates the whole curve and shakes too hard from
        /// the first corner, and unlike an over-estimate it does not self-correct. See
        /// <see cref="KnownSourceColdStartReference"/>'s own note on the same asymmetry.
        /// </summary>
        public const int DefaultMinSaturatedWheelsForRejection = 2;

        /// <summary>How close to <see cref="SourceCeiling"/> a single wheel must read to count as
        /// saturated. A hair below the ceiling rather than an exact compare, since the aggregate and the
        /// per-wheel values both arrive through floating-point arithmetic.</summary>
        public const double SaturatedWheelThreshold = SourceCeiling - 1e-6;

        /// <summary>
        /// THE G-BANDED WALK-BACK (owner, 2026-09-25 - v1.1.0). How far a frame's achieved G may sit from
        /// the DETECTION frame's G, as a fraction of it, before the walk-back refuses to step onto it.
        /// <para/>
        /// WHAT THE WALK-BACK IS FOR. The detection frame is structurally LATE: the arm condition needs
        /// <see cref="TrendFrames"/> frames of evidence, so by the time "basis rising while G falls" is
        /// confirmed, the grip peak has already passed. The owner's own observation, from the FH6 capture:
        /// at detection frame 7617 (G 0.980) the car had actually been braking hardest at 7613 (G 1.248),
        /// and the lock source read 92.0 there rather than 98.2. Max grip is where G PEAKED, which is the
        /// project's own settled definition, so that is the frame SMax should be read at.
        /// <para/>
        /// TWO BOUNDS, BOTH NECESSARY. The walk steps back only while
        /// <list type="number">
        /// <item>the basis is still FALLING as we go back - i.e. we stay inside the one contiguous rise
        /// that produced this crossing, and never step into a previous, unrelated phase; and</item>
        /// <item>the frame's G is within THIS fraction of the detection frame's G.</item>
        /// </list>
        /// The unbounded version was measured and is dangerous: on the owner's Viper capture (frame 293)
        /// the source jumped 15.3 -&gt; 73.5 in a single frame, so the highest-G frame in the window sat on
        /// the far side of that jump and the snapshot collapsed 74.0 -&gt; 11.8. Across the corpus the
        /// unbounded rule produced 11 such collapses in 1081 crossings; at 10% it produces ONE.
        /// <para/>
        /// WHY 10, MEASURED AND NOT TUNED. During genuinely hard braking the car sits on a grip PLATEAU,
        /// so G barely moves - the opposite of the intuition that a hard stop is volatile. Cumulative
        /// |dG| as a fraction of the detection frame's G, over the whole corpus:
        /// <code>
        ///   subset             n      -1 fr    -2 fr    -3 fr    (median / p90, percent)
        ///   hardest 25%      269      2 / 10   3 / 15   6 / 16
        ///   G &gt;= 3.0 g       184      2 /  8   3 / 12   5 / 14
        ///   gentlest 25%     269     53 /419  63 /565  86 /700
        /// </code>
        /// The walk is on average 1.16 frames deep at this setting, and 10 is exactly the p90 of what a
        /// real hard brake does in ONE frame - so the band is matched to the granularity the walk actually
        /// uses. It admits the nearest frame on 90% of hard-braking crossings (5% admits only 69%, which
        /// turns the rule off on a third of the events it exists for) while still excluding the Viper
        /// outlier at 23%. Going wider buys almost nothing (15% -&gt; 93%, 20% -&gt; 96%) and costs: at 15%
        /// three sessions move further down for no benefit, and at 30% the Viper collapse returns.
        /// <para/>
        /// THE WILD PERCENTAGES ARE ALL AT LOW G, where a small absolute wobble is a huge fraction of a
        /// small denominator - which is precisely where the band should be strict, and is why the test is
        /// fractional rather than an absolute g threshold. Being a fraction also makes it car-, surface-
        /// and weather-neutral, the owner's standing constraint: it asks "how far is this frame from what
        /// the car is doing RIGHT NOW", never "how many g".
        /// <para/>
        /// MEASURED EFFECT ON PUBLISHED SMax, on top of the saturated-wheel guard (p90 of the taught pool,
        /// 25 channel-sessions): median -4.2%, 7 of 25 bit-identical, worst -36.1%. The sessions this is
        /// FOR: FH6 Lock 86.6 -&gt; 78.9, 20260831 Lock 62.5 -&gt; 50.9 and Slip 37.9 -&gt; 32.5,
        /// 20260815-230140 Slip 66.4 -&gt; 58.8.
        /// <para/>
        /// HONESTLY DISCLOSED COST. This is not free: the mean change is -7.2%, and a lower SMax means a
        /// LOUDER output (<c>Rescale = source * 80 / SMax</c>), so adopting this raises everyone's
        /// amplitude by roughly that much. It was adopted with that understood. One session
        /// (20260902-202751 Lock, 39.8 -&gt; 25.4) moves much further than the rest and is NOT explained by
        /// an anomaly leaking through - its top two crossings each genuinely re-read about 14 points lower.
        /// <para/>
        /// Set to 0 or less to DISABLE the walk-back entirely, which restores the pre-1.1.0 behaviour of
        /// snapshotting the detection frame itself.
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

        /// <summary>The live saturated-wheel guard - see
        /// <see cref="DefaultMinSaturatedWheelsForRejection"/>. Set to 0 or less to DISABLE the guard
        /// entirely, which is the kill switch for it; the two channels own independent gate objects, so
        /// switching one off leaves the other exactly as it was.</summary>
        public int MinSaturatedWheelsForRejection { get; set; } = DefaultMinSaturatedWheelsForRejection;

        /// <summary>The live G-banded walk-back - see <see cref="DefaultWalkBackGBandFraction"/>. Set to
        /// 0 or less to DISABLE the walk-back entirely (snapshot the detection frame, as before v1.1.0),
        /// which is the kill switch for it; the two channels own independent gate objects, so switching
        /// one off leaves the other exactly as it was.</summary>
        public double WalkBackGBandFraction { get; set; } = DefaultWalkBackGBandFraction;

        /// <summary>
        /// Picks the frame this crossing should teach from: walking back from the detection frame while
        /// the basis keeps falling AND the G stays inside <see cref="WalkBackGBandFraction"/>, return the
        /// slot with the HIGHEST G - the grip peak of this one rise. See
        /// <see cref="DefaultWalkBackGBandFraction"/> for why, and for the measurement.
        /// <para/>
        /// Returns <paramref name="newestSlot"/> itself whenever the walk cannot step - the band is off,
        /// the previous frame is not part of this rise, its G is out of band, or the detection G is not
        /// usable as a denominator - so the degenerate cases all reproduce the pre-1.1.0 snapshot exactly.
        /// </summary>
        private int WalkBackSlot(int newestSlot)
        {
            double gAtDetection = _gHistory[newestSlot];
            if (WalkBackGBandFraction <= 0.0 || !ClampMath.IsFinite(gAtDetection) || gAtDetection <= 0.0)
                return newestSlot;

            double band = WalkBackGBandFraction * gAtDetection;
            int best = newestSlot;
            int here = newestSlot;
            // At most HistoryLength-1 steps, so the walk can never wrap around onto the detection frame
            // and start re-reading the ring buffer as though it were older data.
            for (int step = 0; step < HistoryLength - 1; step++)
            {
                int previous = (here - 1 + HistoryLength) % HistoryLength;
                // (1) stay inside the one contiguous rise that produced this crossing
                if (!(_basisHistory[previous] < _basisHistory[here])) break;
                // (2) and inside the G band around the detection frame
                if (Math.Abs(_gHistory[previous] - gAtDetection) > band) break;
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

        /// <summary>The Raw-fallback basis, kept per slot alongside the configured one so the walk-back
        /// can snapshot BOTH keys at the SAME chosen frame. Teaching them from different frames would
        /// break the engine's configured-vs-fallback divergence test, which compares the two on the
        /// assumption that they describe the same moment - see <see cref="TeachingFallbackBasis"/>.</summary>
        private readonly double[] _fallbackHistory = new double[HistoryLength];
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
        /// <param name="saturatedWheelCount">How many of THIS frame's four CONFIGURED wheel readings are
        /// at the source's own ceiling - see <see cref="DefaultMinSaturatedWheelsForRejection"/>. The
        /// configured wheels specifically, because <paramref name="lockBasis"/> is the aggregate built
        /// from them and it is that basis the crossing is defined on; the Raw-fallback snapshot rides
        /// along with the same decision. DEFAULTS TO 0 - "the caller did not report saturation" - so
        /// every pre-existing caller and test keeps its previous behaviour byte for byte.</param>
        public bool Observe(double lockBasis, double fallbackBasis, double achievedG, bool atLimit, double dtSeconds,
            int saturatedWheelCount = 0)
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
            _fallbackHistory[slot] = fallbackBasis;
            _next = (slot + 1) % HistoryLength;
            if (_written < HistoryLength) _written++;

            // ONE CROSSING PER EVENT, AND ONLY AT THE LIMIT. "Source rising while G falls" is satisfied
            // by the whole lock-up, not just its onset, so a re-arm mid-event would overwrite the
            // snapshot with the runaway's value and the refreshed window would reach forward into the
            // next braking zone. And a crossing detected while the car is NOT at its braking limit is a
            // spin, a kerb, or wheels off the ground - all of which show the same signature.
            // THE SATURATED-WHEEL GUARD (v1.1.0) - see DefaultMinSaturatedWheelsForRejection for the
            // measurement and for why it is car/surface/weather-neutral. It decides WHETHER this crossing
            // may teach at all; DefaultWalkBackGBandFraction then decides WHICH frame it teaches from.
            // The two are independent and were measured independently - the guard alone leaves 13 of 25
            // channel-sessions bit-identical, the walk-back alone does not close the saturated cases.
            bool sourceAlreadyLetGo = MinSaturatedWheelsForRejection > 0
                                      && saturatedWheelCount >= MinSaturatedWheelsForRejection;

            if (_written >= HistoryLength && !Qualified && atLimit && risingThisFrame && !collapseTooFast
                && !sourceAlreadyLetGo)
            {
                double deltaBasis = lockBasis - _basisHistory[_next];
                double deltaG = achievedG - _gHistory[_next];
                if (deltaBasis > LockRise && deltaG < GFall)
                {
                    _secondsSinceCrossing = 0.0;
                    _framesSinceCrossing = 0;
                    CrossedThisFrame = true;
                    // THE G-BANDED WALK-BACK (v1.1.0) - the detection frame is structurally late by up
                    // to TrendFrames, so the snapshot is taken at the grip PEAK of this one rise rather
                    // than here. Both keys are read from the SAME chosen slot. Degenerates to exactly
                    // `lockBasis`/`fallbackBasis` whenever the walk cannot step - see WalkBackSlot.
                    int teachSlot = WalkBackSlot(slot);
                    _crossingBasis = _basisHistory[teachSlot];
                    _crossingFallbackBasis = _fallbackHistory[teachSlot];
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

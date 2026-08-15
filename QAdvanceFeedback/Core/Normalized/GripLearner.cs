namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Learns the largest longitudinal motion (deceleration under braking, acceleration under
    /// throttle) THIS car has actually demonstrated, in units of g, and uses it - not a fixed
    /// physical constant - as the reference a raw g reading is compared against. One instance per
    /// channel (Lock, Slip); never shared between them, since a car's braking and driven axles can
    /// have very different peak capability.
    /// <para/>
    /// THE LESSON THIS CLASS EXISTS TO APPLY: the sibling ReliableWheelLockSlip project's equivalent
    /// (<c>GripBudgetEstimator</c>) normalised achieved deceleration against a FIXED ~1g budget
    /// whenever no per-wheel telemetry was available to drive its own learner - an arcade title
    /// pulling 4g under routine braking then divided every frame's deceleration by 1g, read
    /// ratio&gt;=4 on the very first brake stab, and clamped straight to "fully locked" for the rest
    /// of the session; a sim car's genuine 1.2g stop, by contrast, would have read only 120% under
    /// that same fixed reference - survivable, but only by luck of matching the guess. This class
    /// instead RAISES <see cref="LearnedPeakG"/> toward whatever the car actually achieves (a decaying
    /// maximum, not a ratchet: a single sensor glitch must not raise it permanently, and a car that
    /// genuinely brakes harder later must be allowed to raise it again), so both an arcade car's 4g
    /// and a sim car's 1.2g settle into "this IS roughly my peak" and produce a comparable 0-100
    /// reading for a comparable fraction of THEIR OWN peak - see
    /// <see cref="NormalizedWheelLockSlipEngineTests"/>'s arcade-vs-sim test for the acceptance case
    /// this class must pass.
    /// <para/>
    /// COLD START: before enough evidence has accumulated, a freshly-learned (or freshly-seeded)
    /// peak is not yet trustworthy - the very first hard brake of a session could BE the highest g
    /// this car will ever produce, in which case trusting it immediately would read 100 ("fully
    /// locked") for what might only be a firm, ordinary stop. <see cref="Ratio"/> ceilings its
    /// result at <see cref="ColdStartCeilingRatio"/> until <see cref="Confidence"/> (evidence count
    /// over <see cref="MaturitySamples"/>) reaches 1.0, then removes the ceiling entirely - mirroring
    /// the sibling project's own <c>ColdStartMaxRatio</c> guard, applied to a learner that (unlike
    /// the sibling's) never depends on per-wheel telemetry to mature, since Layer 4 has none to work
    /// with (see the brief's explicit ban on wheel-speed-derived slip).
    /// <para/>
    /// OUTLIER REJECTION: <see cref="Observe"/> discards non-finite, non-positive, or implausibly
    /// large (&gt; <see cref="MaxPlausibleG"/> - a session-reset teleport or a one-frame telemetry
    /// glitch, not a real tyre) readings before they can corrupt the learned peak.
    /// </summary>
    public sealed class GripLearner
    {
        /// <summary>Seed value before anything has been observed - a plausible, unremarkable
        /// starting guess, not a permanent reference: <see cref="Observe"/> moves away from it as
        /// soon as real evidence arrives, and <see cref="ColdStartCeilingRatio"/> (not this seed)
        /// is what protects the FIRST few readings from over-trusting it.</summary>
        public const double SeedPeakG = 1.0;

        /// <summary>Rejected as a sensor glitch/teleport rather than folded into the learned peak.</summary>
        public const double MaxPlausibleG = 8.0;

        /// <summary>The ratio ceiling while <see cref="Confidence"/> is still 0 - see this class's
        /// own remarks.</summary>
        public const double ColdStartCeilingRatio = 0.75;

        /// <summary>Qualifying samples for full confidence/maturity. At a typical 60fps with the
        /// engine's own pedal-committed gate, a few seconds of real braking/throttle reaches this.</summary>
        public const int MaturitySamples = 200;

        private const double ForgetPerSample = 0.9995;
        private const double RaiseAlpha = 0.15;
        private const double MinPeakFloor = 0.1;

        private double _learnedPeakG = SeedPeakG;
        private int _samples;

        public double LearnedPeakG => _learnedPeakG;
        public int Samples => _samples;

        /// <summary>0..1 maturity of the learned peak - 1.0 once <see cref="MaturitySamples"/>
        /// qualifying observations have been folded in.</summary>
        public double Confidence => ClampMath.To01(ClampMath.SafeDiv(_samples, MaturitySamples, 0.0));

        /// <summary>
        /// Folds one qualifying observation (already gated by the engine on pedal commitment and the
        /// lateral-isolation check - see <see cref="NormalizedWheelLockSlipEngine"/>) into the learned
        /// peak. A decaying maximum: every call decays the current estimate slightly, then raises it
        /// toward <paramref name="magnitudeG"/> if that observation exceeds it - so the learner keeps
        /// tracking a car that gets faster tyres or a different setup mid-session, rather than
        /// freezing at whatever it first learned.
        /// </summary>
        public void Observe(double magnitudeG)
        {
            if (!ClampMath.IsFinite(magnitudeG) || magnitudeG <= 0.0 || magnitudeG > MaxPlausibleG) return;

            _learnedPeakG *= ForgetPerSample;
            if (magnitudeG > _learnedPeakG) _learnedPeakG += RaiseAlpha * (magnitudeG - _learnedPeakG);
            if (_learnedPeakG < MinPeakFloor) _learnedPeakG = MinPeakFloor;

            _samples++;
        }

        /// <summary>
        /// <paramref name="magnitudeG"/> as a fraction of the learned peak, ceilinged per
        /// <see cref="ColdStartCeilingRatio"/> while <see cref="Confidence"/> is below 1.0.
        /// Deliberately NOT itself clamped to [0,1] once mature - a genuine full lock/spin can
        /// exceed the learned peak (the peak is a decaying maximum of ordinary driving, not a hard
        /// physical ceiling), and the caller (<see cref="NormalizedWheelLockSlipEngine"/>) clamps the
        /// final published value to 0-100 regardless.
        /// </summary>
        public double Ratio(double magnitudeG)
        {
            double clamped = ClampMath.Clamp(magnitudeG, 0.0, MaxPlausibleG);
            double raw = ClampMath.SafeDiv(clamped, _learnedPeakG, 0.0);

            double confidence = Confidence;
            if (confidence >= 1.0) return raw;

            double ceiling = ColdStartCeilingRatio + confidence * (1.0 - ColdStartCeilingRatio);
            return raw < ceiling ? raw : ceiling;
        }

        /// <summary>Seeds this learner from previously persisted state (<c>RuntimeStore</c>) -
        /// called once at Init. Atomic: a non-positive/non-finite peak OR a non-positive sample
        /// count means "nothing usable was stored", and BOTH fields are left at their fresh-seed
        /// values - adopting one half of a corrupt pair (e.g. a valid sample count paired with a
        /// NaN peak) would leave the learner in a state it could never reach through
        /// <see cref="Observe"/> alone.</summary>
        public void Load(double learnedPeakG, int samples)
        {
            if (!ClampMath.IsFinite(learnedPeakG) || learnedPeakG <= 0.0 || samples <= 0) return;
            _learnedPeakG = learnedPeakG;
            _samples = samples;
        }
    }
}

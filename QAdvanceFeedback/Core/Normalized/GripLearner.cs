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

        /// <summary>DEFAULT learning-path reject ceiling - rejected as a sensor glitch/teleport
        /// rather than folded into the learned peak. Kept as the DEFAULT for
        /// <see cref="LearnCapG"/> (and for a plain <c>new GripLearner()</c>, e.g. every pre-existing
        /// test in this file) - a channel-specific instance may override it with a tighter, asymmetric
        /// value instead (see <see cref="NormalizedWheelLockSlipEngine"/>'s own Lock/Slip constants
        /// and docs\gforce-direction-fix-report.md for the derivation).</summary>
        public const double MaxPlausibleG = 8.0;

        /// <summary>
        /// LIVE-PATH-ONLY clamp ceiling for <see cref="Ratio"/> - deliberately HIGHER than any
        /// learning-path cap (REJECT and CLAMP are different needs, per
        /// docs\gforce-direction-fix-report.md: the learning path must REJECT an impact-magnitude
        /// reading outright, since one bad sample would otherwise become the persistent normalisation
        /// reference for every subsequent frame - see <see cref="LearnCapG"/>/<see cref="Observe"/>;
        /// the LIVE path must instead CLAMP, never reject/drop the frame, since dropping it would
        /// freeze the published severity at its last value, which feels like the plugin hanging
        /// through a crash rather than recovering immediately after it). 15g comfortably exceeds
        /// every realistic figure this fix's own brief cited (F1 braking ~5-6g, drag-launch ~4-5g)
        /// with margin, while staying well below a genuine wall-impact spike (this plugin's own
        /// captured session showed a ~19.8g-equivalent collision reading) - high enough to never be
        /// mistaken for ordinary driving, low enough to still mean something as a live ceiling.
        /// </summary>
        public const double LiveClampG = 15.0;

        /// <summary>The ratio ceiling while <see cref="Confidence"/> is still 0 - see this class's
        /// own remarks.</summary>
        public const double ColdStartCeilingRatio = 0.75;

        /// <summary>Qualifying samples for full confidence/maturity. At a typical 60fps with the
        /// engine's own pedal-committed gate, a few seconds of real braking/throttle reaches this.</summary>
        public const int MaturitySamples = 200;

        /// <summary>
        /// Minimum qualifying samples before a (game,car) key is included in
        /// <see cref="KeyedGripLearner.ExportAll"/>'s persisted snapshot at all - deliberately far
        /// below <see cref="MaturitySamples"/> (persistence across a restart should not require FULL
        /// confidence, or a merely-early session would never survive a restart at all), but high
        /// enough that a handful of frames from a five-second menu-only session cannot alone create a
        /// persisted entry for a brand-new key. Now that <see cref="TelemetryLearningGate"/> already
        /// excludes menu/pit/replay/paused/teleport frames from ever reaching <see cref="Observe"/> at
        /// all, "samples" here already means qualifying, gated observations, not raw frames - this is
        /// a second, independent line of defence specifically for persistence, not a substitute for
        /// that gate.
        /// </summary>
        public const int MinPersistSamples = 30;

        private const double ForgetPerSample = 0.9995;
        private const double RaiseAlpha = 0.15;
        private const double MinPeakFloor = 0.1;

        private double _learnedPeakG = SeedPeakG;
        private int _samples;

        public double LearnedPeakG => _learnedPeakG;
        public int Samples => _samples;

        /// <summary>The LEARNING-path reject ceiling actually used by THIS instance - defaults to
        /// <see cref="MaxPlausibleG"/>, but a channel-specific instance (see the constructor) may use
        /// a tighter, asymmetric value instead. <see cref="Observe"/> rejects a magnitude above this
        /// outright.</summary>
        public double LearnCapG { get; }

        /// <param name="learnCapG">The learning-path reject ceiling for THIS instance - see
        /// <see cref="LearnCapG"/>. Defaults to <see cref="MaxPlausibleG"/> so every pre-existing
        /// <c>new GripLearner()</c> call site (including this file's own tests) is unaffected. A
        /// non-positive or non-finite value falls back to the default rather than disabling
        /// rejection entirely.</param>
        public GripLearner(double learnCapG = MaxPlausibleG)
        {
            LearnCapG = learnCapG > 0.0 && ClampMath.IsFinite(learnCapG) ? learnCapG : MaxPlausibleG;
        }

        /// <summary>0..1 maturity of the learned peak - 1.0 once <see cref="MaturitySamples"/>
        /// qualifying observations have been folded in.</summary>
        public double Confidence => ClampMath.To01(ClampMath.SafeDiv(_samples, MaturitySamples, 0.0));

        /// <summary>
        /// Folds one qualifying observation (already gated by the engine on pedal commitment and the
        /// lateral-isolation check - see <see cref="NormalizedWheelLockSlipEngine"/>, plus the
        /// per-frame <see cref="TelemetryLearningGate"/>/pedal-minimum check - see that engine's own
        /// remarks) into the learned peak. A decaying maximum: every call decays the current estimate
        /// slightly, then raises it toward <paramref name="magnitudeG"/> if that observation exceeds
        /// it - so the learner keeps tracking a car that gets faster tyres or a different setup
        /// mid-session, rather than freezing at whatever it first learned.
        /// <para/>
        /// REJECT, not clamp (see <see cref="LearnCapG"/>'s own remarks): a magnitude above
        /// <see cref="LearnCapG"/> is discarded OUTRIGHT here, never folded in even partially - the
        /// live-path equivalent, <see cref="Ratio"/>, CLAMPS instead, against the separate, higher
        /// <see cref="LiveClampG"/>.
        /// </summary>
        public void Observe(double magnitudeG)
        {
            if (!ClampMath.IsFinite(magnitudeG) || magnitudeG <= 0.0 || magnitudeG > LearnCapG) return;

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
        /// <para/>
        /// CLAMP, not reject (see <see cref="LiveClampG"/>'s own remarks): this is the LIVE path, so
        /// an impact-magnitude reading is clamped down to a finite, sane input rather than the frame
        /// being dropped - dropping it would freeze the published severity at its last value instead
        /// of producing a real (saturated) reading for that frame and recovering immediately after.
        /// </summary>
        public double Ratio(double magnitudeG)
        {
            double clamped = ClampMath.Clamp(magnitudeG, 0.0, LiveClampG);
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

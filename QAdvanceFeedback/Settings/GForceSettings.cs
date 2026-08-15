using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;

namespace QAdvanceFeedback.Settings
{
    /// <summary>How the max-G reference used to normalise a G-force axis (acceleration or
    /// deceleration) is obtained.</summary>
    public enum GMaxMode
    {
        /// <summary>Always use the configured Fixed*MaxG value, regardless of anything observed at
        /// runtime.</summary>
        Fixed,

        /// <summary>Learn the maximum actually observed, per game+car (see
        /// <see cref="GForceMaxLearner"/>), with single-frame-spike outlier rejection.</summary>
        Auto
    }

    /// <summary>
    /// Model/algorithm settings for the G-force feedback channels (Core/GForce) - composed into
    /// <see cref="QAdvanceFeedbackSettings.GForce"/>, persisted through <c>ConfigStore</c>, wired into
    /// <c>QAdvanceFeedback.cs</c>'s Init/DataUpdate (AttachDelegate the 8 GForce properties,
    /// <see cref="SetCurrentGameAndCar"/> and Observe*G called once per frame, <see cref="GForceEngine"/>
    /// fed the result of <see cref="EffectiveAccelMaxG"/>/<see cref="EffectiveDecelMaxG"/>), and exposed
    /// on the settings UI's G-Force tab - see docs\wiring-ui-report.md for the wiring task's own writeup.
    /// The AUTO-learned maxima additionally round-trip through <c>RuntimeStore</c> via
    /// <see cref="ExportLearnedMaxima"/>/<see cref="ImportLearnedMaxima"/> so they survive a restart.
    /// <para/>
    /// Maxima bind per game AND per car under AUTO (a Formula car and a road car in the same game
    /// learn different maxima) - see <see cref="GForceMaxLearner"/> for the (gameId, carId) key and
    /// its mandatory single-frame-spike outlier rejection, added specifically because an
    /// outlier-unaware learned reference (or normalising against a fixed constant regardless of
    /// context) is exactly what corrupted the sibling ReliableWheelLockSlip project's signal - a
    /// 19.9g collision spike became "the" reference forever after. FIXED mode ignores learned values
    /// entirely, by construction (see <see cref="EffectiveAccelMaxG"/>/<see cref="EffectiveDecelMaxG"/>).
    /// </summary>
    public sealed class GForceSettings
    {
        private const double MinAllowedMaxG = 0.05;

        private double _fixedAccelMaxG = 0.9;
        private double _fixedDecelMaxG = 2.0;

        /// <summary>
        /// FIXED mode on/off for the acceleration axis. Default <see cref="GMaxMode.Fixed"/> so a
        /// freshly-installed plugin behaves predictably before any learning has happened.
        /// </summary>
        public GMaxMode AccelMaxMode { get; set; } = GMaxMode.Fixed;

        /// <summary>FIXED mode on/off for the deceleration/braking axis. See
        /// <see cref="AccelMaxMode"/>'s remarks.</summary>
        public GMaxMode DecelMaxMode { get; set; } = GMaxMode.Fixed;

        /// <summary>
        /// Default rationale (feel over physical realism, per the brief's own instruction): 2.0g for
        /// braking. The brief's own example is that even an F1 car's hardest braking should hit the
        /// top of the scale quickly - 2.0g comfortably covers hard-braking road/GT/formula content in
        /// typical sim titles (most peak well under this under braking) while leaving enough headroom
        /// that the meter isn't permanently pegged at 100 for merely firm braking.
        /// </summary>
        public double FixedDecelMaxG
        {
            get => _fixedDecelMaxG;
            set => _fixedDecelMaxG = value > MinAllowedMaxG ? value : MinAllowedMaxG;
        }

        /// <summary>
        /// Default rationale: 0.9g for acceleration - deliberately lower than the 2.0g braking
        /// default because sustained acceleration-g is physically smaller than braking-g for almost
        /// all vehicles in typical sim content (acceleration is power/traction-limited; braking gets
        /// the combined benefit of tyre grip plus aerodynamic downforce at speed). Using the same
        /// ceiling for both axes would make acceleration feel permanently numb by comparison; 0.9g
        /// lets a strong (not necessarily record) launch or mid-corner power-down clearly reach
        /// toward the top of the scale.
        /// </summary>
        public double FixedAccelMaxG
        {
            get => _fixedAccelMaxG;
            set => _fixedAccelMaxG = value > MinAllowedMaxG ? value : MinAllowedMaxG;
        }

        private double _brakeBottomRearSustainPercent = 50.0;
        private double _brakeBackLowSustainPercent = 25.0;
        private double _accelBottomRearSustainPercent = 25.0;
        private double _accelBackLowSustainPercent = 50.0;

        /// <summary>0-100, clamped. Default 50 - the MIDDLE zone of the braking chain (distance 1 from
        /// the terminal Bottom Front), re-derived from this model's own chain topology (halving per
        /// hop from the terminal zone) - see
        /// <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s remarks for the full derivation
        /// and why the previous flat-50%-everywhere defaults were wrong. 0 reproduces the
        /// pre-this-feature fade-to-nothing behaviour exactly.</summary>
        public double BrakeBottomRearSustainPercent
        {
            get => _brakeBottomRearSustainPercent;
            set => _brakeBottomRearSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100, clamped. Default 25 - the FAR zone of the braking chain (distance 2 from
        /// the terminal Bottom Front - half of <see cref="BrakeBottomRearSustainPercent"/>'s own 50%).
        /// See <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s remarks for the full
        /// derivation.</summary>
        public double BrakeBackLowSustainPercent
        {
            get => _brakeBackLowSustainPercent;
            set => _brakeBackLowSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100, clamped. Default 25 - the FAR zone of the acceleration chain (distance 2
        /// from the terminal Back Top - half of <see cref="AccelBackLowSustainPercent"/>'s own 50%).
        /// See <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s remarks for the full
        /// derivation.</summary>
        public double AccelBottomRearSustainPercent
        {
            get => _accelBottomRearSustainPercent;
            set => _accelBottomRearSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>0-100, clamped. Default 50 - the MIDDLE zone of the acceleration chain (distance 1
        /// from the terminal Back Top), per the brief's explicit "Back Low should keep vibrating (just
        /// less strongly)" requirement - see <see cref="GForceEngine.BrakeBottomRearSustainFraction"/>'s
        /// remarks for the full derivation.</summary>
        public double AccelBackLowSustainPercent
        {
            get => _accelBackLowSustainPercent;
            set => _accelBackLowSustainPercent = ClampMath.To0100(value);
        }

        /// <summary>The owner's driver-facing lateral direction toggle - Normal (default, unchanged
        /// pre-existing behaviour) or Reversed. See <see cref="GForceEngine.LateralDirection"/>'s
        /// remarks for exactly what each mode means physically.</summary>
        public LateralDirectionMode LateralDirection { get; set; } = LateralDirectionMode.Normal;

        /// <summary>
        /// The owner-requested "Integrate Wheel Lock and Slip" G-force shake (see
        /// <see cref="GForceEngine.ShakeFrequencyHz"/>/<c>Core.GForce.GForceShake</c> for the mechanics).
        /// Default OFF: this is a new, intrusive change to the existing G-force FEEL (it superimposes an
        /// oscillation on top of every pad pair's level) - shipping it enabled by default would silently
        /// change how the plugin already feels for every existing install, which nothing else in this
        /// plugin family does (compare <see cref="Core.Projection.PulseSettings.Enabled"/>, the other
        /// "changes the feel" toggle, which also ships OFF). A driver opts in deliberately.
        /// </summary>
        public bool IntegrateWheelLockAndSlip { get; set; } = false;

        private double _shakeFrequencyHz = Core.GForce.GForceShake.MinFrequencyHz;

        /// <summary>Hz, clamped to [5, 20] in the setter itself - see
        /// <see cref="GForceEngine.ShakeFrequencyHz"/>'s own remarks. Default 5 Hz (the floor).</summary>
        public double ShakeFrequencyHz
        {
            get => _shakeFrequencyHz;
            set => _shakeFrequencyHz = ClampMath.Clamp(value, Core.GForce.GForceShake.MinFrequencyHz, Core.GForce.GForceShake.MaxFrequencyHz);
        }

        private double _wheelLockShakeScale = 1.0;

        /// <summary>Non-negative, clamped in the setter. Default 1.0 (unscaled).</summary>
        public double WheelLockShakeScale
        {
            get => _wheelLockShakeScale;
            set => _wheelLockShakeScale = value >= 0.0 ? value : 0.0;
        }

        private double _wheelSlipShakeScale = 1.0;

        /// <summary>Non-negative, clamped in the setter. Default 1.0 (unscaled).</summary>
        public double WheelSlipShakeScale
        {
            get => _wheelSlipShakeScale;
            set => _wheelSlipShakeScale = value >= 0.0 ? value : 0.0;
        }

        private double _sustainTimeConstantSeconds = 0.15;
        private double _transientTimeConstantSeconds = 0.08;
        private double _transientGain = 1.5;

        /// <summary>Seconds, clamped positive. See <see cref="GForceEngine.SustainTimeConstantSeconds"/>'s
        /// remarks for the default's reasoning.</summary>
        public double SustainTimeConstantSeconds
        {
            get => _sustainTimeConstantSeconds;
            set => _sustainTimeConstantSeconds = value > 1e-3 ? value : 1e-3;
        }

        /// <summary>Seconds, clamped positive. See <see cref="GForceEngine.TransientTimeConstantSeconds"/>'s
        /// remarks for the default's reasoning.</summary>
        public double TransientTimeConstantSeconds
        {
            get => _transientTimeConstantSeconds;
            set => _transientTimeConstantSeconds = value > 1e-3 ? value : 1e-3;
        }

        /// <summary>Clamped non-negative. See <see cref="GForceEngine.TransientGain"/>'s remarks for
        /// the default's reasoning.</summary>
        public double TransientGain
        {
            get => _transientGain;
            set => _transientGain = value >= 0.0 ? value : 0.0;
        }

        /// <summary>
        /// Applies every model/algorithm setting on this object to <paramref name="engine"/> - the one
        /// place that keeps the settings POCO and the live engine's tunable properties from drifting
        /// apart. Called once at Init and again whenever the settings UI's global Apply button saves.
        /// </summary>
        public void ApplyTo(GForceEngine engine)
        {
            if (engine == null) return;
            engine.BrakeBottomRearSustainFraction = BrakeBottomRearSustainPercent / 100.0;
            engine.BrakeBackLowSustainFraction = BrakeBackLowSustainPercent / 100.0;
            engine.AccelBottomRearSustainFraction = AccelBottomRearSustainPercent / 100.0;
            engine.AccelBackLowSustainFraction = AccelBackLowSustainPercent / 100.0;
            engine.LateralDirection = LateralDirection;
            engine.SustainTimeConstantSeconds = SustainTimeConstantSeconds;
            engine.TransientTimeConstantSeconds = TransientTimeConstantSeconds;
            engine.TransientGain = TransientGain;
            engine.IntegrateWheelLockAndSlip = IntegrateWheelLockAndSlip;
            engine.ShakeFrequencyHz = ShakeFrequencyHz;
            engine.WheelLockShakeScale = WheelLockShakeScale;
            engine.WheelSlipShakeScale = WheelSlipShakeScale;
        }

        private readonly GForceMaxLearner _accelLearner = new GForceMaxLearner();
        private readonly GForceMaxLearner _decelLearner = new GForceMaxLearner();

        private string _currentGameId = string.Empty;
        private string _currentCarId = string.Empty;

        /// <summary>
        /// Records which game/car is currently active, so the no-arg <see cref="CurrentLearnedAccelMaxG"/>/
        /// <see cref="CurrentLearnedDecelMaxG"/> properties below reflect the right (gameId, carId)
        /// without every caller having to thread both strings through. Intended to be called once per
        /// frame by the plugin integration (not yet wired - see this class's own remarks).
        /// </summary>
        public void SetCurrentGameAndCar(string gameId, string carId)
        {
            _currentGameId = gameId ?? string.Empty;
            _currentCarId = carId ?? string.Empty;
        }

        /// <summary>Feeds one frame's acceleration-G magnitude (non-negative) into the AUTO learner
        /// for this (gameId, carId). Safe to call even when <see cref="AccelMaxMode"/> is FIXED - the
        /// learner keeps learning in the background so switching to AUTO later has data to use; FIXED
        /// mode simply never reads it back (see <see cref="EffectiveAccelMaxG"/>).</summary>
        public void ObserveAccelG(string gameId, string carId, double magnitude) => _accelLearner.Observe(gameId, carId, magnitude);

        /// <summary>Feeds one frame's deceleration/braking-G magnitude (non-negative) into the AUTO
        /// learner for this (gameId, carId). See <see cref="ObserveAccelG"/>'s remarks.</summary>
        public void ObserveDecelG(string gameId, string carId, double magnitude) => _decelLearner.Observe(gameId, carId, magnitude);

        /// <summary>The learned acceleration max for a specific (gameId, carId) - present mainly for
        /// direct testability of the per-game/per-car keying; see <see cref="CurrentLearnedAccelMaxG"/>
        /// for the UI-facing no-arg equivalent.</summary>
        public double GetLearnedAccelMaxG(string gameId, string carId) => _accelLearner.GetLearnedMax(gameId, carId);

        /// <summary>The learned deceleration max for a specific (gameId, carId). See
        /// <see cref="GetLearnedAccelMaxG"/>'s remarks.</summary>
        public double GetLearnedDecelMaxG(string gameId, string carId) => _decelLearner.GetLearnedMax(gameId, carId);

        /// <summary>
        /// Read-only - what the (not-yet-built) settings UI shows in its learned-acceleration-value
        /// textbox for whichever game/car <see cref="SetCurrentGameAndCar"/> was last told is active.
        /// 0.0 before anything has been confirmed for that key.
        /// </summary>
        public double CurrentLearnedAccelMaxG => _accelLearner.GetLearnedMax(_currentGameId, _currentCarId);

        /// <summary>Read-only - the deceleration equivalent of <see cref="CurrentLearnedAccelMaxG"/>.</summary>
        public double CurrentLearnedDecelMaxG => _decelLearner.GetLearnedMax(_currentGameId, _currentCarId);

        /// <summary>
        /// The max-G value <see cref="GForceEngine.Compute"/> should actually normalise acceleration
        /// against for this (gameId, carId) this frame. FIXED always returns
        /// <see cref="FixedAccelMaxG"/>, ignoring anything learned (by construction - this branch
        /// never reads the learner). AUTO returns the learned value, falling back to
        /// <see cref="FixedAccelMaxG"/> as a seed only until enough frames have been observed to
        /// confirm one (so the very first moments of a new car are not divided by an unlearned zero).
        /// </summary>
        public double EffectiveAccelMaxG(string gameId, string carId)
        {
            if (AccelMaxMode == GMaxMode.Fixed) return FixedAccelMaxG;
            double learned = _accelLearner.GetLearnedMax(gameId, carId);
            return learned > MinAllowedMaxG ? learned : FixedAccelMaxG;
        }

        /// <summary>The deceleration equivalent of <see cref="EffectiveAccelMaxG"/>.</summary>
        public double EffectiveDecelMaxG(string gameId, string carId)
        {
            if (DecelMaxMode == GMaxMode.Fixed) return FixedDecelMaxG;
            double learned = _decelLearner.GetLearnedMax(gameId, carId);
            return learned > MinAllowedMaxG ? learned : FixedDecelMaxG;
        }

        /// <summary>Clears all learned state for both axes - for a full session reset (analogous to
        /// SimHubTelemetryAdapter.Reset), not called automatically by this class.</summary>
        public void ResetLearning()
        {
            _accelLearner.Reset();
            _decelLearner.Reset();
        }

        /// <summary>Snapshots both learners' confirmed maxima for <c>RuntimeStore</c> to persist to
        /// <c>plugin.QAdvanceFeedback.runtime.json</c> - the wiring task's addition so AUTO mode's
        /// learning survives a SimHub restart, matching how the Lock/Slip <see cref="Core.Normalized.GripLearner"/>
        /// states already do.</summary>
        public void ExportLearnedMaxima(out Dictionary<string, double> accel, out Dictionary<string, double> decel)
        {
            accel = _accelLearner.ExportLearnedMaxima();
            decel = _decelLearner.ExportLearnedMaxima();
        }

        /// <summary>Restores both learners from a previously persisted snapshot - called once at
        /// Init. See <see cref="GForceMaxLearner.ImportLearnedMaxima"/>'s remarks.</summary>
        public void ImportLearnedMaxima(Dictionary<string, double> accel, Dictionary<string, double> decel)
        {
            _accelLearner.ImportLearnedMaxima(accel);
            _decelLearner.ImportLearnedMaxima(decel);
        }

        // ---- Recommended shaker frequency range (data only - the settings UI displays this, it does
        // ---- not drive anything in Core/GForce itself; SimHub's own ShakeIt "From Hz"/"To Hz" effect
        // ---- fields are outside this task's file ownership).

        /// <summary>The rumble pads' own hardware capability, per the brief: 10-300 Hz.</summary>
        public const double DeviceMinHz = 10.0;

        /// <summary>See <see cref="DeviceMinHz"/>.</summary>
        public const double DeviceMaxHz = 300.0;

        private double _recommendedFromHz = 300.0;
        private double _recommendedToHz = 20.0;

        /// <summary>
        /// Recommended "From" Hz - the frequency a channel plays at when its published value is near
        /// 0 (the high/subtle end of the convention: value near 0 -&gt; high Hz, value 100 -&gt; low
        /// Hz). Defaults to the owner's own example (300 Hz - the device's own maximum), clamped to
        /// the device's actual 10-300 Hz capability so a differently-capable device still gets a sane
        /// recommendation instead of an out-of-range one.
        /// </summary>
        public double RecommendedFromHz
        {
            get => ClampMath.Clamp(_recommendedFromHz, DeviceMinHz, DeviceMaxHz);
            set => _recommendedFromHz = value;
        }

        /// <summary>
        /// Recommended "To" Hz - the frequency a channel plays at when its published value is 100
        /// (the low/punchy end). Defaults to the owner's own example (20 Hz), clamped to the device
        /// range AND kept at or below <see cref="RecommendedFromHz"/> - the whole point of the
        /// convention is value 0 -&gt; high Hz, value 100 -&gt; low Hz, so a "to" above "from" would
        /// silently invert it.
        /// </summary>
        public double RecommendedToHz
        {
            get => ClampMath.Clamp(_recommendedToHz, DeviceMinHz, RecommendedFromHz);
            set => _recommendedToHz = value;
        }
    }
}

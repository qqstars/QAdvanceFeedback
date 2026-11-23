using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Clutch
{
    /// <summary>One telemetry frame as the bite detector needs it.</summary>
    public readonly struct ClutchFrame
    {
        public ClutchFrame(double timeSec, double? clutch, double? rpm, double? speedKmh, double? throttle,
            bool inGear, bool looseSurface, bool validForLearning)
        {
            TimeSec = timeSec; Clutch = clutch; Rpm = rpm; SpeedKmh = speedKmh; Throttle = throttle;
            InGear = inGear; LooseSurface = looseSurface; ValidForLearning = validForLearning;
        }

        public double TimeSec { get; }
        /// <summary>0 released .. 100 fully pressed.</summary>
        public double? Clutch { get; }
        public double? Rpm { get; }
        public double? SpeedKmh { get; }
        public double? Throttle { get; }
        public bool InGear { get; }
        public bool LooseSurface { get; }
        /// <summary>False while paused, in a menu, spectating, or otherwise not a learning frame.</summary>
        public bool ValidForLearning { get; }
    }

    public enum ClutchDetectorPhase { Waiting, Armed, Releasing }

    /// <summary>
    /// Standing-start bite point detection (docs\clutch-bite-design.md, "Auto bite point").
    /// <list type="number">
    /// <item>Arm: in gear, stopped, clutch pressed in.</item>
    /// <item>Watch the release, keeping a rolling history.</item>
    /// <item>On the RPM trigger (or, without one, the speed trigger) backdate to the ONSET frame - never
    /// where the dip was confirmed or where the driver stopped.</item>
    /// <item>Correct one frame of pedal travel back toward pressed (the engine shows the load a few
    /// frames late).</item>
    /// <item>Reject a release that was too fast at the bite, a throttle swing, or a speed-only sample on
    /// a loose surface.</item>
    /// <item>Confirm when the car really pulls away.</item>
    /// </list>
    /// Holding the pedal still never creates a sample by itself.
    /// </summary>
    public sealed class ClutchBiteDetector
    {
        public const double StandstillKmh = 0.5;
        public const double RpmOnsetFraction = 0.01;
        public const double SpeedTriggerKmh = 0.3;
        public const double RateWindowSec = 0.2;
        private const double HistorySec = 3.0;
        /// <summary>The idle must be steady before a release counts (review fix I3): at least this long
        /// armed, with RPM inside the steady band (<see cref="ClutchLearningParameters.SteadyIdlePercent"/>) of its mean - a settling or falling
        /// idle would otherwise read as clutch load.</summary>
        public const double SteadySec = 0.3;
        public const double SteadyTrendFraction = 0.01;
        /// <summary>A longer gap between frames (pause, menu, a stall) resets the detector (review fix I4).</summary>
        public const double MaxFrameGapSec = 0.2;
        /// <summary>The one-frame delay correction never adds more than this.</summary>
        public const double MaxFrameTravel = 3.0;

        private struct Entry { public double T, Clutch, Rpm, Speed; }

        private readonly List<Entry> _history = new List<Entry>();
        private readonly List<Entry> _armedRpm = new List<Entry>();
        private double _lastTime = double.NaN;
        private double _baselineRpm, _baselineSpeed, _throttleMin, _throttleMax;
        private double? _rpmSample, _speedSample;
        private bool _rpmRejected, _speedRejected, _loose;

        public ClutchDetectorPhase Phase { get; private set; }

        /// <summary>The tunable thresholds (arm level, RPM dip, confirm speed, release speed, throttle
        /// swing, steady idle); the rest are fixed. Set before each frame by the engine.</summary>
        public ClutchLearningParameters Parameters { get; set; } = ClutchLearningParameters.Default;

        public void Reset()
        {
            Phase = ClutchDetectorPhase.Waiting;
            _history.Clear();
            _armedRpm.Clear();
            _rpmSample = _speedSample = null;
            _rpmRejected = _speedRejected = _loose = false;
        }

        /// <returns>True on the frame a sample (clutch position 0..100) is confirmed.</returns>
        public bool Update(ClutchFrame f, out double sample)
        {
            sample = 0;
            if (!f.ValidForLearning || !f.InGear || !f.Clutch.HasValue || !f.SpeedKmh.HasValue
                || !ClampMath.IsFinite(f.Clutch.Value) || !ClampMath.IsFinite(f.SpeedKmh.Value))
            {
                Reset();
                return false;
            }

            // A pause, menu or stall between frames: start over (the gap would also fool the rate check).
            bool gap = !double.IsNaN(_lastTime) && f.TimeSec - _lastTime > MaxFrameGapSec;
            _lastTime = f.TimeSec;
            if (gap && Phase != ClutchDetectorPhase.Waiting) Reset();

            double clutch = f.Clutch.Value, speed = f.SpeedKmh.Value;
            bool stopped = speed < StandstillKmh;
            bool clutchIn = clutch >= Parameters.ArmClutchPercent;

            switch (Phase)
            {
                case ClutchDetectorPhase.Waiting:
                    if (stopped && clutchIn) Arm(f);
                    return false;

                case ClutchDetectorPhase.Armed:
                    if (!stopped) { Reset(); return false; }
                    if (clutchIn) { Arm(f); return false; }      // keep the baseline fresh while waiting
                    if (!SteadyIdle()) { Reset(); return false; }  // revs still moving: not measurable this time
                    Phase = ClutchDetectorPhase.Releasing;
                    break;

                case ClutchDetectorPhase.Releasing:
                    if (clutchIn)
                    {
                        // Pressed back in: re-arm if still stopped, else start over.
                        if (stopped) Arm(f); else Reset();
                        return false;
                    }
                    break;
            }

            Record(f.TimeSec, clutch, f.Rpm, speed);
            if (f.Throttle.HasValue)
            {
                _throttleMin = Math.Min(_throttleMin, f.Throttle.Value);
                _throttleMax = Math.Max(_throttleMax, f.Throttle.Value);
            }
            _loose |= f.LooseSurface;

            if (!_rpmSample.HasValue && !_rpmRejected && _baselineRpm > 0 && f.Rpm.HasValue
                && f.Rpm.Value <= _baselineRpm * (1.0 - Parameters.RpmDipPercent / 100.0))
                Backdate(e => e.Rpm < _baselineRpm * (1.0 - RpmOnsetFraction), ref _rpmSample, ref _rpmRejected, extrapolateRpm: true);

            if (!_speedSample.HasValue && !_speedRejected && speed >= _baselineSpeed + SpeedTriggerKmh)
                Backdate(e => e.Speed > _baselineSpeed + 0.05, ref _speedSample, ref _speedRejected, extrapolateRpm: false);

            if (speed < _baselineSpeed + Parameters.ConfirmSpeedKmh) return false;

            // Pulled away: decide.
            bool throttleSwing = _throttleMax - _throttleMin > Parameters.MaxThrottleSwing;
            double? chosen = _rpmSample ?? (_rpmRejected ? null : (_loose ? null : _speedSample));
            Reset();
            if (throttleSwing || !chosen.HasValue) return false;
            sample = ClampMath.To0100(chosen.Value);
            return true;
        }

        private void Arm(ClutchFrame f)
        {
            Phase = ClutchDetectorPhase.Armed;
            _history.Clear();
            if (f.Rpm.HasValue && ClampMath.IsFinite(f.Rpm.Value))
            {
                _armedRpm.Add(new Entry { T = f.TimeSec, Rpm = f.Rpm.Value });
                // Keep the oldest entry that still gives a full SteadySec span.
                while (_armedRpm.Count > 2 && f.TimeSec - _armedRpm[1].T >= SteadySec) _armedRpm.RemoveAt(0);
            }
            double sum = 0;
            foreach (Entry e in _armedRpm) sum += e.Rpm;
            _baselineRpm = _armedRpm.Count > 0 ? sum / _armedRpm.Count : 0.0;
            _baselineSpeed = f.SpeedKmh ?? 0.0;
            _throttleMin = _throttleMax = f.Throttle ?? 0.0;
            _rpmSample = _speedSample = null;
            _rpmRejected = _speedRejected = _loose = false;
            Record(f.TimeSec, f.Clutch ?? 100.0, f.Rpm, f.SpeedKmh ?? 0.0);
        }

        /// <summary>Armed long enough with RPM inside the steady band. With no RPM at all (a game that
        /// does not report it) only the speed onset can be used, which needs no steady idle.</summary>
        private bool SteadyIdle()
        {
            if (_armedRpm.Count == 0) return true;
            if (_armedRpm[_armedRpm.Count - 1].T - _armedRpm[0].T < SteadySec - 1e-9) return false;
            double min = double.MaxValue, max = double.MinValue;
            foreach (Entry e in _armedRpm) { min = Math.Min(min, e.Rpm); max = Math.Max(max, e.Rpm); }
            if (!(_baselineRpm > 0) || max - min > _baselineRpm * Parameters.SteadyIdlePercent / 100.0) return false;

            // Not trending: a settling idle (falling after a blip, or recovering) stays inside the band
            // for a moment but keeps moving afterwards. Compare the window's first and last few frames.
            int n = Math.Min(3, _armedRpm.Count / 2);
            double head = 0, tail = 0;
            for (int i = 0; i < n; i++) { head += _armedRpm[i].Rpm; tail += _armedRpm[_armedRpm.Count - 1 - i].Rpm; }
            return n == 0 || Math.Abs(head - tail) / n <= _baselineRpm * SteadyTrendFraction;
        }

        private void Record(double t, double clutch, double? rpm, double speed)
        {
            _history.Add(new Entry { T = t, Clutch = clutch, Rpm = rpm ?? double.NaN, Speed = speed });
            while (_history.Count > 2 && t - _history[0].T > HistorySec) _history.RemoveAt(0);
        }

        /// <summary>
        /// Walks back from the trigger (the newest frame) to the first frame of the contiguous run that
        /// satisfies <paramref name="onset"/> and rejects the sample if the pedal moved too fast in the
        /// window before it. The position is:
        /// <list type="bullet">
        /// <item>for the RPM dip, extrapolated to where RPM was still at its baseline. A straight line of RPM
        /// against pedal position is fitted over the dip frames, which removes the trigger threshold and the
        /// frame-quantisation error and leaves only the engine's response delay;</item>
        /// <item>otherwise (or when the fit is unusable) the onset frame's position;</item>
        /// </list>
        /// then corrected by one frame of pedal travel back toward pressed (the shortest response delay).
        /// </summary>
        private void Backdate(Func<Entry, bool> onset, ref double? sample, ref bool rejected, bool extrapolateRpm)
        {
            int last = _history.Count - 1;
            int i = last;
            while (i > 0 && onset(_history[i - 1])) i--;

            if (FastestRate(i) > Parameters.MaxReleaseRatePerSec) { rejected = true; return; }

            double position = _history[i].Clutch;
            if (extrapolateRpm && TryExtrapolateToBaseline(i, last, out double atBaseline))
            {
                // Never beyond the frame before the onset, nor past the trigger frame.
                double upper = _history[Math.Max(0, i - 1)].Clutch, lower = _history[last].Clutch;
                position = ClampMath.Clamp(atBaseline, Math.Min(lower, upper), Math.Max(lower, upper));
            }

            double frameTravel = i > 0 ? ClampMath.Clamp(_history[i - 1].Clutch - _history[i].Clutch, 0.0, MaxFrameTravel) : 0.0;
            sample = position + frameTravel;
        }

        /// <summary>Least-squares line of RPM against pedal over the dip frames, solved for the
        /// baseline RPM. False when there are fewer than two frames or RPM does not fall with the pedal.</summary>
        private bool TryExtrapolateToBaseline(int from, int to, out double clutchAtBaseline)
        {
            clutchAtBaseline = 0;
            int n = 0;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int j = from; j <= to; j++)
            {
                double x = _history[j].Clutch, y = _history[j].Rpm;
                if (!ClampMath.IsFinite(x) || !ClampMath.IsFinite(y)) continue;
                n++; sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            if (n < 2) return false;
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) return false;
            double slope = (n * sxy - sx * sy) / denom;              // rpm per pedal point; > 0 when releasing loads the engine
            if (slope <= 1e-6) return false;
            double intercept = (sy - slope * sx) / n;
            clutchAtBaseline = (_baselineRpm - intercept) / slope;
            return ClampMath.IsFinite(clutchAtBaseline);
        }

        /// <summary>The fastest pedal movement (points per second, over three-frame spans) in the window
        /// before frame <paramref name="onsetIndex"/>, so a quick crossing after a hold is not averaged
        /// away by the hold.</summary>
        private double FastestRate(int onsetIndex)
        {
            double tOnset = _history[onsetIndex].T, fastest = 0;
            for (int j = onsetIndex; j >= 0 && tOnset - _history[j].T <= RateWindowSec; j--)
            {
                int k = Math.Max(0, j - 3);
                double dt = _history[j].T - _history[k].T;
                if (dt <= 0) continue;
                fastest = Math.Max(fastest, Math.Abs(_history[k].Clutch - _history[j].Clutch) / dt);
            }
            // Also the frames up to the onset from the window's newest side (the move may straddle it).
            for (int j = Math.Min(_history.Count - 1, onsetIndex + 3); j > onsetIndex; j--)
            {
                double dt = _history[j].T - _history[onsetIndex].T;
                if (dt > 0) fastest = Math.Max(fastest, Math.Abs(_history[onsetIndex].Clutch - _history[j].Clutch) / dt);
            }
            return fastest;
        }
    }
}

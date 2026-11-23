using System;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>States of the <see cref="DriftAttenuator"/> state machine (spec §9.7).</summary>
    public enum DriftState
    {
        /// <summary>Output = calc. Drift counting runs while smoothed Nᵤ &gt; the drift start.</summary>
        Normal,
        /// <summary>A held drift: output eases from 100 % to ToPercent along the S-curve.</summary>
        Drifting,
        /// <summary>Drift released: the factor is frozen; output = Min(frozen × calc, calc).</summary>
        Recovering,
    }

    /// <summary>
    /// SLIP ANGLE DRIFT ATTENUATION (docs\slip-angle-design.md §9.7; Nᵤ per §8). Pure, per-channel,
    /// allocation-free per frame. Feel: pressing in, the vibration rises through the ideal window and
    /// peaks past the boundary; once the car is HELD in a stable drift (detected quickly - rally flicks
    /// are short) it eases gently to ~40 % of the calculated output and stays there, until the car
    /// returns below the ideal window.
    /// <code>
    /// Normal ──(sNᵤ &gt; DriftStart for Hold, rise inside the hold window ≤ StabilityRange)──▶ Drifting
    /// Drifting:  factor = 1 − (1 − To)·S(t),  S(t) = t^γ/(t^γ+(1−t)^γ),  t = timeInDrift/Duration
    ///            out = Min(factor × calcPeakSinceDrifting, calc)
    /// Drifting ──(sNᵤ ≤ peakSinceDrifting − SwingMargin, held TrendConfirm)──▶ Recovering
    ///            (locked = factor × calcPeakSinceDrifting at that frame)
    /// Recovering: out = Min(locked, calc)
    /// Recovering ──(sNᵤ ≥ troughSinceRecovering + SwingMargin, held TrendConfirm)──▶ Normal (out = calc)
    /// Recovering ──(sNᵤ &lt; 30, α &lt; Min)──▶ Normal
    /// Drifting / Recovering ──(sNᵤ ≤ 80 and throttle &gt; Min(Level, ref + Rise), or ≥ Level)──▶ Normal
    /// </code>
    /// DRIFT START (owner, 2026-11-20): the caller passes the Nᵤ at which drift counting starts - the
    /// Nᵤ of the angle Max(1.2·Max, Min(30°, 1.5·Max)) - never below 80 (α = Max).
    /// <para/>
    /// LOCKED OUTPUT (owner, 2026-11-20): once a drift is recognised the scale factor is applied to the
    /// highest calculated value since the drift began, never to a falling one: as the angle comes down,
    /// the output holds the scaled-down value until the unscaled calc drops below it, then follows calc
    /// (100 eased to 40 stays 40 while calc falls 100 → 40, then follows calc).
    /// <para/>
    /// THROTTLE CLEAR (owner, 2026-11-20): while the flag is on (Drifting or Recovering) and sNᵤ ≤ 80
    /// (α ≤ Max), ref = Min(ref, throttle) (started from the first such frame's throttle); the flag
    /// clears when throttle &gt; ref + <see cref="DriftSettings.ThrottleRiseClear"/> or throttle ≥
    /// <see cref="DriftSettings.ThrottleLevelClear"/>. ref is kept if the angle goes back above Max and
    /// dropped when the flag clears. No throttle (null / NaN) never clears.
    /// sNᵤ is Nᵤ smoothed by an EMA with time constant <see cref="DriftSettings.SmoothingSeconds"/>
    /// (dt-correct: alpha = 1 − exp(−dt/τ); τ = 0 → no smoothing; the first sample after construction /
    /// <see cref="Reset"/> initialises the EMA directly, so there is no lag from zero). Every decision
    /// uses sNᵤ, including the "&lt; 30" exit.
    /// <para/>
    /// HOLD WINDOW: while sNᵤ &gt; the drift start, (time, raw Nᵤ, sNᵤ) samples are kept for the last
    /// HoldSeconds. Once sNᵤ has been above it continuously for ≥ HoldSeconds, the rise inside the window is
    /// compared with StabilityRange: ≤ → Drifting; &gt; → an escalation (spin), stay Normal at full
    /// output. The rise is max(young raw rise, smoothed rise), each the largest upward excursion
    /// (max over i&lt;j of v_j − v_i). The YOUNG RAW rise only starts from samples taken while the EMA
    /// is young (&lt; <see cref="YoungTauMultiple"/>·τ since it was seeded at construction / Reset /
    /// landing): a freshly seeded EMA lags a spin's onset and would under-read it (ruling R25). Once
    /// the EMA is warm only the smoothed rise counts, so raw frame-to-frame noise (washboard, rumble
    /// strips: ±10 pts every frame) cannot block a held drift forever. τ = 0 → raw and smoothed are the
    /// same series (no young phase). Smoothing still governs entering (&gt; drift start) and the trend
    /// confirmations. The window SLIDES, so a slide that escalates and
    /// later stabilises is still detected. Dropping to ≤ the drift start clears the count. Timers count the frame
    /// that starts them (a condition true on N frames has been held N·dt).
    /// <para/>
    /// ROBUSTNESS: dt ≤ 0 / NaN / ∞ ages every timer by the nominal 1/60 s; a finite dt is capped at
    /// min(<see cref="MaxDt"/> (0.1 s), HoldSeconds / 2) so a stall frame can never satisfy the hold
    /// in one sample, even with the shortest hold (0.05 s). A
    /// non-finite calc returns 0; a non-finite Nᵤ with a finite calc returns the CURRENT state's output
    /// (Normal: calc; Drifting/Recovering: the held factor rule). Neither changes any state (no EMA
    /// update, no timer advance) - never throws. Null settings
    /// mean defaults; every setting is read through <see cref="DriftSettings"/>'s range clamps.
    /// <para/>
    /// CALLER CONTRACT (airborne / landing, §9.7 last rule): this class knows nothing about flight. The
    /// projected engine calls <see cref="Reset"/> while the car is airborne (→ Normal, full output) and
    /// keeps calling it during the landing wobble so the hold timers stay reset until the car settles.
    /// </summary>
    public sealed class DriftAttenuator
    {
        /// <summary>α = Max ⇔ Nᵤ = 80 (the lowest drift start, and the throttle-clear limit) / Recovering-exit
        /// threshold (§9.7: α &lt; Min ⇔ Nᵤ &lt; 30).</summary>
        public const double DriftThreshold = 80.0, RecoverExitThreshold = 30.0;

        /// <summary>The lowest drift start <see cref="Step"/> accepts (owner, 2026-11-20). Normal sources start
        /// at 80 or above (<c>SlipAngleDriftStart.Nu</c>); a grip-ratio source that stops at full slip may need
        /// a start just under its ceiling, which is below 80 when its learned Max sits at that ceiling.</summary>
        public const double MinDriftStart = 75.0;

        /// <summary>dt used when the supplied dt is ≤ 0 or non-finite.</summary>
        public const double NominalDt = 1.0 / 60.0;

        /// <summary>Largest dt honoured per frame (a stall frame must not satisfy the hold alone).</summary>
        public const double MaxDt = 0.1;

        /// <summary>The EMA counts as "young" (raw rise honoured) for this many τ after it was seeded.</summary>
        public const double YoungTauMultiple = 3.0;

        private const double Eps = 1e-9;
        private static readonly DriftSettings Defaults = new DriftSettings();

        private bool _hasSmoothed;
        private double _smoothed;
        private double _clock;
        private double _seedClock;

        // Hold window (ring buffer of samples while sNᵤ > the drift start).
        private double[] _times = new double[64];
        private double[] _values = new double[64];
        private double[] _raws = new double[64];
        private bool[] _young = new bool[64];
        private int _head, _count;
        private double _aboveTime;

        private double _driftTime, _peak, _trough, _trendTime;

        // Highest calc since entering Drifting; the output held through Recovering; the throttle reference.
        private double _calcPeak, _locked, _throttleRef = double.NaN;

        public DriftState State { get; private set; } = DriftState.Normal;

        /// <summary>Current multiplier: 1 in Normal, the ramp value in Drifting, the frozen value in Recovering.</summary>
        public double Factor { get; private set; } = 1.0;

        /// <summary>Back to Normal, factor 1, EMA and every timer/window cleared.</summary>
        public void Reset()
        {
            State = DriftState.Normal;
            Factor = 1.0;
            _hasSmoothed = false;
            _smoothed = 0;
            _clock = 0;
            _seedClock = 0;
            ClearWindow();
            _driftTime = _peak = _trough = _trendTime = 0;
            _calcPeak = _locked = 0;
            _throttleRef = double.NaN;
        }

        /// <summary>
        /// One frame. <paramref name="nUnclamped"/> = combined Nᵤ (§8, may exceed 100),
        /// <paramref name="calc"/> = the projected value, <paramref name="dt"/> in seconds,
        /// <paramref name="driftStart"/> = the Nᵤ above which drift counting runs (clamped to ≥
        /// <see cref="MinDriftStart"/>; non-finite → 80), <paramref name="throttlePercent"/> = the frame's throttle (null = unknown).
        /// Returns the attenuated output.
        /// </summary>
        public double Step(double nUnclamped, double calc, double dt, DriftSettings s,
            double driftStart = DriftThreshold, double? throttlePercent = null)
        {
            if (!ClampMath.IsFinite(calc)) return 0.0;
            if (!ClampMath.IsFinite(nUnclamped)) return Output(calc);
            s = s ?? Defaults;
            if (!ClampMath.IsFinite(dt) || dt <= 0) dt = NominalDt;
            else
            {
                double cap = Math.Min(MaxDt, DriftSettings.Hold(s.HoldSeconds) / 2.0);
                if (dt > cap) dt = cap;
            }

            double tau = DriftSettings.Smoothing(s.SmoothingSeconds);
            if (!_hasSmoothed) { _smoothed = nUnclamped; _hasSmoothed = true; _seedClock = _clock; }
            else if (tau <= 0) _smoothed = nUnclamped;
            else _smoothed += (1.0 - Math.Exp(-dt / tau)) * (nUnclamped - _smoothed);
            _clock += dt;
            double sm = _smoothed;
            bool young = _clock - _seedClock < YoungTauMultiple * tau - Eps;

            double start = ClampMath.IsFinite(driftStart) ? Math.Max(MinDriftStart, driftStart) : DriftThreshold;

            switch (State)
            {
                case DriftState.Normal:
                    StepNormal(nUnclamped, sm, young, dt, s, start, calc);
                    break;
                case DriftState.Drifting:
                    if (calc > _calcPeak) _calcPeak = calc;
                    StepDrifting(sm, dt, s);
                    break;
                case DriftState.Recovering:
                    StepRecovering(sm, dt, s);
                    break;
            }

            if (State != DriftState.Normal) ApplyThrottleClear(sm, throttlePercent, s);

            return Output(calc);
        }

        private double Output(double calc)
        {
            switch (State)
            {
                case DriftState.Drifting: return Math.Min(Factor * _calcPeak, calc);
                case DriftState.Recovering: return Math.Min(_locked, calc);
                default: return calc;
            }
        }

        private void ApplyThrottleClear(double sm, double? throttlePercent, DriftSettings s)
        {
            if (sm > DriftThreshold || !throttlePercent.HasValue || !ClampMath.IsFinite(throttlePercent.Value)) return;
            double throttle = ClampMath.To0100(throttlePercent.Value);
            if (double.IsNaN(_throttleRef) || throttle < _throttleRef) _throttleRef = throttle;
            if (throttle + Eps >= DriftSettings.Level(s.ThrottleLevelClear)
                || throttle > _throttleRef + DriftSettings.Rise(s.ThrottleRiseClear) + Eps)
                BackToNormal();
        }

        private void BackToNormal()
        {
            State = DriftState.Normal;
            Factor = 1.0;
            ClearWindow(); // drift counting restarts from the next frame
            _trendTime = 0;
            _throttleRef = double.NaN;
        }

        private void StepNormal(double raw, double sm, bool young, double dt, DriftSettings s, double start, double calc)
        {
            Factor = 1.0;
            if (sm <= start) { ClearWindow(); return; }

            double hold = DriftSettings.Hold(s.HoldSeconds);
            _aboveTime += dt;
            Push(_clock, raw, sm, young);
            while (_count > 1 && _times[_head] < _clock - hold - Eps) { _head = (_head + 1) % _times.Length; _count--; }

            if (_aboveTime + Eps < hold) return;
            if (Math.Max(YoungRawRise(), WindowRise(_values)) > DriftSettings.Stability(s.StabilityRange))
                return; // escalation: stay full, window slides

            State = DriftState.Drifting;
            ClearWindow();
            _driftTime = 0;
            _peak = sm;
            _calcPeak = calc;
            _throttleRef = double.NaN;
            _trendTime = 0;
            Factor = 1.0;
        }

        private void StepDrifting(double sm, double dt, DriftSettings s)
        {
            _driftTime += dt;
            double to = DriftSettings.To(s.ToPercent) / 100.0;
            double duration = DriftSettings.Duration(s.DurationSeconds);
            double t = duration <= 0 ? 1.0 : Math.Min(1.0, _driftTime / duration);
            Factor = 1.0 - (1.0 - to) * SCurve(t, DriftSettings.GammaOf(s.Gamma));

            if (sm > _peak) _peak = sm;
            if (!Confirmed(sm <= _peak - DriftSettings.Swing(s.SwingMargin), dt, s)) return;

            State = DriftState.Recovering; // Factor stays frozen at its current value
            _locked = Factor * _calcPeak;  // the scaled-down output, held while calc falls
            _trough = sm;
            _trendTime = 0;
        }

        private void StepRecovering(double sm, double dt, DriftSettings s)
        {
            if (sm < _trough) _trough = sm;
            bool increase = Confirmed(sm >= _trough + DriftSettings.Swing(s.SwingMargin), dt, s);
            if (increase || sm < RecoverExitThreshold) BackToNormal();
        }

        /// <summary>Accumulates the trend timer while <paramref name="condition"/> holds; true once held TrendConfirm.</summary>
        private bool Confirmed(bool condition, double dt, DriftSettings s)
        {
            if (!condition) { _trendTime = 0; return false; }
            _trendTime += dt;
            return _trendTime + Eps >= DriftSettings.Confirm(s.TrendConfirmSeconds);
        }

        /// <summary>S(t) = t^γ/(t^γ+(1−t)^γ) on [0,1].</summary>
        private static double SCurve(double t, double gamma)
        {
            if (t <= 0) return 0.0;
            if (t >= 1) return 1.0;
            double a = Math.Pow(t, gamma), b = Math.Pow(1.0 - t, gamma);
            return a / (a + b);
        }

        /// <summary>Largest upward excursion within the window: max over i&lt;j of v_j − v_i.</summary>
        private double WindowRise(double[] buffer)
        {
            double runningMin = double.PositiveInfinity, rise = 0;
            for (int k = 0; k < _count; k++)
            {
                double v = buffer[(_head + k) % buffer.Length];
                if (v - runningMin > rise) rise = v - runningMin;
                if (v < runningMin) runningMin = v;
            }
            return rise;
        }

        /// <summary>
        /// Largest raw upward excursion that STARTS at a sample taken while the EMA was young:
        /// max over i&lt;j, sample i young, of raw_j − raw_i. 0 when the window holds no young sample.
        /// </summary>
        private double YoungRawRise()
        {
            double runningMin = double.PositiveInfinity, rise = 0;
            for (int k = 0; k < _count; k++)
            {
                int i = (_head + k) % _raws.Length;
                double v = _raws[i];
                if (v - runningMin > rise) rise = v - runningMin;
                if (_young[i] && v < runningMin) runningMin = v;
            }
            return rise;
        }

        private void Push(double time, double raw, double value, bool young)
        {
            if (_count == _times.Length)
            {
                int n = _times.Length;
                var nt = new double[n * 2];
                var nv = new double[n * 2];
                var nr = new double[n * 2];
                var ny = new bool[n * 2];
                for (int k = 0; k < _count; k++)
                {
                    int i = (_head + k) % n;
                    nt[k] = _times[i];
                    nv[k] = _values[i];
                    nr[k] = _raws[i];
                    ny[k] = _young[i];
                }
                _times = nt; _values = nv; _raws = nr; _young = ny; _head = 0;
            }
            int idx = (_head + _count) % _times.Length;
            _times[idx] = time;
            _values[idx] = value;
            _raws[idx] = raw;
            _young[idx] = young;
            _count++;
        }

        private void ClearWindow()
        {
            _head = 0;
            _count = 0;
            _aboveTime = 0;
        }
    }
}

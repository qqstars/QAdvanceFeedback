using System;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Turns a flat "pinned at 100" reading into a pulsing 100-&gt;min-&gt;100-&gt;min waveform, per
    /// the brief's pulse requirement. One instance is needed PER PUBLISHED TARGET (FrontLeft,
    /// FrontRight, ..., All - nine per channel): each tracks its own phase independently, because one
    /// wheel can be pinned at 100 while another is not, and they must not share a clock. All nine
    /// instances for a channel share the SAME <see cref="PulseSettings"/> object (one gap/min/enabled
    /// per channel, exactly as the brief specifies "per channel", not per target).
    /// <para/>
    /// TIME SOURCE: deliberately not wall-clock. <see cref="Advance"/> takes <c>dtSeconds</c> as a
    /// plain parameter, read by the caller from the same frame-to-frame delta
    /// (<c>ITelemetrySample.Dt</c>) every other per-frame calculation in this plugin already uses -
    /// this is what the brief means by "make the time source injectable ... drive it from frame dt":
    /// a test supplies synthetic dt values directly, with no <c>Thread.Sleep</c> and no wall-clock
    /// dependency anywhere in this class.
    /// <para/>
    /// WAVEFORM: a raised cosine, not linear and not a square wave (the brief is explicit on this
    /// point). Let t = milliseconds elapsed since the value FIRST reached 100 this pulse, and
    /// gap = <see cref="PulseSettings.GapMs"/>:
    /// <code>output = min + (100 - min) * 0.5 * (1 + cos(pi * t / gap))</code>
    /// At t=0, cos(0)=1 -&gt; output=100. At t=gap, cos(pi)=-1 -&gt; output=min (one "move", exactly
    /// <c>gap</c> ms). At t=2*gap, cos(2*pi)=1 -&gt; output=100 again (one full cycle = 2*gap ms) -
    /// this is exactly the brief's worked example (gap=500ms -&gt; 100-&gt;min-&gt;100-&gt;min, each
    /// move taking 500ms). The curve's velocity is zero at both endpoints (derivative of cos is zero
    /// at 0/pi/2pi/...), which is what makes it a smooth sinusoid rather than a disguised linear ramp
    /// or a discontinuous square wave.
    /// <para/>
    /// "REACHES 100" AND WHAT HAPPENS ON DROPPING BACK OFF IT (both stated explicitly, per the
    /// brief's own instruction to pick sane behaviour and test it):
    /// <list type="bullet">
    /// <item>A frame "reaches 100" when the PROJECTED value passed to <see cref="Advance"/> is at or
    /// above <c>100 - Epsilon</c> (a tiny floating-point tolerance, not a strict <c>== 100.0</c>
    /// compare, since the curve's own closing point is exactly 100.0 in practice but a tolerance
    /// costs nothing and protects against an unforeseen future rounding path).</item>
    /// <item>The FIRST frame a pulse becomes active always reads exactly 100 (t=0 of its own cycle) -
    /// so entering "pinned at max" never itself causes a visible dip on the very frame it happens.</item>
    /// <item>The instant the projected value drops back BELOW 100 - even if a pulse is mid-descent
    /// toward min - the pulse stops immediately and <see cref="Advance"/> returns the plain projected
    /// value for that frame, with the phase clock reset to zero. Chosen deliberately over "let the
    /// current half-cycle finish first": the input dropping below 100 means the upstream signal
    /// itself says the driver is no longer at the limit, and continuing to show a stale pulse toward
    /// <see cref="PulseSettings.MinValue"/> while the real value is, say, 62 would contradict the very
    /// value being displayed. The NEXT time the value reaches 100 (whether moments later or after a
    /// long gap), the pulse restarts fresh at phase 0 (t=0 -&gt; 100), not wherever an old cycle left
    /// off.</item>
    /// <item>Below 100, or whenever <see cref="PulseSettings.Enabled"/> is false, the output is always
    /// exactly the plain projected value passed in - the pulse never engages away from the very top of
    /// the range.</item>
    /// </list>
    /// Allocation-free and side-effect-free besides its own two private fields, so nine instances per
    /// channel cost nothing meaningful in the per-frame path.
    /// </summary>
    public sealed class PulseGenerator
    {
        private const double Epsilon = 1e-6;

        private readonly PulseSettings _settings;
        private bool _active;
        private double _elapsedMs;

        public PulseGenerator(PulseSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>True while a pulse cycle is currently in progress - exposed for tests/diagnostics
        /// only, not required for <see cref="Advance"/>'s own correctness.</summary>
        public bool IsActive => _active;

        public double Advance(double dtSeconds, double projectedValue0100)
        {
            double value = ClampMath.To0100(projectedValue0100);
            bool atMax = value >= 100.0 - Epsilon;

            if (!_settings.Enabled || !atMax)
            {
                _active = false;
                _elapsedMs = 0.0;
                return value;
            }

            if (!_active)
            {
                // Freshly entering "pinned at max": start the cycle at its own t=0, which the
                // waveform below always evaluates to exactly 100 - see this class's remarks.
                _active = true;
                _elapsedMs = 0.0;
            }
            else if (ClampMath.IsFinite(dtSeconds) && dtSeconds > 0.0)
            {
                _elapsedMs += dtSeconds * 1000.0;
            }

            double gap = _settings.GapMs < PulseSettings.MinGapMs ? PulseSettings.MinGapMs : _settings.GapMs;
            double theta = Math.PI * (_elapsedMs / gap);
            double min = _settings.MinValue;

            double output = min + (100.0 - min) * 0.5 * (1.0 + Math.Cos(theta));
            return ClampMath.To0100(output);
        }

        /// <summary>Clears any in-progress pulse - e.g. on a game/session switch, mirroring how
        /// <c>SimHubTelemetryAdapter.Reset</c> clears its own frame-to-frame bookkeeping.</summary>
        public void Reset()
        {
            _active = false;
            _elapsedMs = 0.0;
        }
    }
}

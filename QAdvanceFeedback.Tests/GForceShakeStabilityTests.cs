using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE PULSE-INTO-SHAKE DEFECT AND ITS FIX (docs\raw-gap-and-pad-balance-report.md). With the
    /// G-Force "Integrate Wheel Lock and Slip" shake ON and a channel's pulse also enabled and
    /// saturated, feeding the PULSED <c>Projected.*</c> value into <see cref="GForceEngine.Compute"/>'s
    /// shake amplitude reads the pulse's own 100-&gt;min-&gt;100 presentation waveform as if it were a
    /// genuine change in lock/slip severity - one oscillation (the pulse, ~a few Hz) modulating another
    /// (the shake, 5-20 Hz), felt as an unstable "wiggle" with no relation to actual G. The fix feeds
    /// the shake from <c>ProjectedWithoutPulse.*</c> instead (see
    /// <see cref="ProjectedWheelLockSlipResult"/>'s own remarks) - identical to the pulsed value except
    /// while a pulse cycle is actively running, so the shake's own amplitude only ever tracks real
    /// lock/slip severity.
    /// <para/>
    /// These tests isolate the shake AMPLITUDE (the pulse-frequency envelope) from the shake's own,
    /// intentional 5-20 Hz oscillation by holding G (and therefore each pad's "centre") constant and
    /// comparing PEAK-TO-PEAK SPANS measured over successive one-shake-period windows: if the envelope
    /// is genuinely stable, every window's span is the same; if something slower is modulating the
    /// amplitude (the defect), the spans themselves visibly rise and fall from window to window.
    /// </summary>
    public class GForceShakeStabilityTests
    {
        private const double AccelMax = 0.9;
        private const double DecelMax = 2.0;
        private const double ShakeHz = 10.0;
        private const double DtSeconds = 0.02; // 50 fps -> 5 frames per shake period at 10 Hz
        private const int FramesPerWindow = 5; // one full shake period

        // brakePercent=80 makes decelChainActive true directly (brakeApplied), so the braking chain's
        // sustain level settles to a real, steady, non-zero centre without depending on the
        // coasting-delta path (which needs a genuinely CHANGING ratio - not what a held-constant
        // longitudinalG across every frame provides).
        private static TelemetrySample BrakingSample()
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -1.0, brakePercent: 80.0);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(DtSeconds));
        }

        private static GForceEngine NewShakingEngine()
            => new GForceEngine { IntegrateWheelLockAndSlip = true, WheelLockShakeScale = 1.0, WheelSlipShakeScale = 0.0, ShakeFrequencyHz = ShakeHz };

        /// <summary>Runs <paramref name="frameCount"/> frames feeding <paramref name="wheelLockValues"/>
        /// (one entry consumed per frame, cycling if shorter than <paramref name="frameCount"/>) and
        /// returns the BottomRearLeft pad reading for every frame.</summary>
        private static List<double> RunSeries(GForceEngine engine, IReadOnlyList<double> wheelLockValues, int frameCount)
        {
            var values = new List<double>(frameCount);
            for (int i = 0; i < frameCount; i++)
            {
                double wheelLock = wheelLockValues[i % wheelLockValues.Count];
                var r = engine.Compute(BrakingSample(), AccelMax, DecelMax, wheelLockAll0100: wheelLock, wheelSlipAll0100: 0.0);
                values.Add(r.BottomRearLeft.Value);
            }
            return values;
        }

        private static List<double> WindowSpans(List<double> series)
        {
            var spans = new List<double>();
            for (int start = 0; start + FramesPerWindow <= series.Count; start += FramesPerWindow)
            {
                double min = double.MaxValue, max = double.MinValue;
                for (int i = start; i < start + FramesPerWindow; i++)
                {
                    if (series[i] < min) min = series[i];
                    if (series[i] > max) max = series[i];
                }
                spans.Add(max - min);
            }
            return spans;
        }

        /// <summary>
        /// THE FIX, PROVEN DIRECTLY: fed the steady (WithoutPulse-equivalent) value 100.0 every frame,
        /// the shake's own peak-to-peak span is IDENTICAL window over window - a genuinely stable
        /// amplitude, exactly what the driver should feel once the fix is wired in.
        /// </summary>
        [Fact]
        public void Fed_a_steady_WithoutPulse_style_value_the_shake_amplitude_is_stable_window_over_window()
        {
            var engine = NewShakingEngine();
            // Warm up so the sustain-level "centre" has fully converged before measuring.
            RunSeries(engine, new[] { 100.0 }, frameCount: 500);

            List<double> series = RunSeries(engine, new[] { 100.0 }, frameCount: 40);
            List<double> spans = WindowSpans(series);

            Assert.True(spans.Count >= 4, "expected at least 4 measurement windows");
            double firstSpan = spans[0];
            foreach (double span in spans)
                Assert.Equal(firstSpan, span, 6);
        }

        /// <summary>
        /// THE DEFECT, PROVEN DIRECTLY: fed a REALISTIC pulsed sequence (a real
        /// <see cref="PulseGenerator"/> instance, saturated at 100, pulsing down to
        /// <see cref="PulseSettings.MinValue"/>=0 and back over a 400ms cycle), the SAME shake setup's
        /// peak-to-peak span visibly rises and falls from window to window - the pulse's own slow
        /// waveform is riding through as a modulation of the shake's amplitude, exactly the "wiggle"
        /// the driver reported.
        /// <para/>
        /// MUTATION EVIDENCE: this is the closest testable proxy for QAdvanceFeedback.cs's own
        /// (SimHub-coupled, not unit-testable - see PropertyPublisherStructureTests' own remarks on why)
        /// choice of which Projected value feeds GForceEngine.Compute. Temporarily changing
        /// <see cref="Fed_a_steady_WithoutPulse_style_value_the_shake_amplitude_is_stable_window_over_window"/>
        /// to feed THIS SAME pulsed series instead of a steady 100.0 made that test fail (spans were no
        /// longer equal) - reproduced, confirmed, and reverted; this is exactly what reverting
        /// QAdvanceFeedback.cs's wiring back to the pulsed <c>Projected.LockAll</c> would do to the real
        /// shake output. Full suite re-confirmed green after reverting.
        /// </summary>
        [Fact]
        public void Fed_a_realistic_pulsed_sequence_the_shake_amplitude_visibly_wobbles_window_over_window()
        {
            var engine = NewShakingEngine();
            RunSeries(engine, new[] { 100.0 }, frameCount: 500);

            var pulse = new PulseGenerator(new PulseSettings { Enabled = true, GapMs = 200, MinValue = 0.0 });
            var pulsedValues = new List<double>();
            for (int i = 0; i < 40; i++)
                pulsedValues.Add(pulse.Advance(DtSeconds, 100.0));

            // Sanity: the synthetic pulse itself genuinely swings from near-100 to near-0 within this window.
            Assert.Contains(pulsedValues, v => v > 95.0);
            Assert.Contains(pulsedValues, v => v < 5.0);

            List<double> series = RunSeries(engine, pulsedValues, frameCount: 40);
            List<double> spans = WindowSpans(series);

            Assert.True(spans.Count >= 4, "expected at least 4 measurement windows");
            double minSpan = spans[0], maxSpan = spans[0];
            foreach (double span in spans)
            {
                if (span < minSpan) minSpan = span;
                if (span > maxSpan) maxSpan = span;
            }

            Assert.True(maxSpan - minSpan > 5.0,
                $"expected the shake's own peak-to-peak span to visibly wobble across windows (the defect), got min={minSpan:F2} max={maxSpan:F2}");
        }

        /// <summary>The shake must still respond normally to a GENUINE change in lock/slip severity -
        /// the fix must not make the shake inert, only remove the pulse-driven wobble.</summary>
        [Fact]
        public void The_shake_still_responds_to_a_genuine_change_in_lock_severity()
        {
            var engine = NewShakingEngine();
            List<double> low = RunSeries(engine, new[] { 10.0 }, frameCount: 200);
            List<double> highSeries = RunSeries(engine, new[] { 100.0 }, frameCount: 200);

            double lowSpan = WindowSpans(low)[WindowSpans(low).Count - 1];
            double highSpan = WindowSpans(highSeries)[WindowSpans(highSeries).Count - 1];

            Assert.True(highSpan > lowSpan + 5.0,
                $"a genuinely larger lock value should produce a visibly larger shake span (low={lowSpan:F2}, high={highSpan:F2})");
        }
    }
}

using System;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A REPORT: how quickly the published maximum moves from the driver's typed set point to the value
    /// actually being measured. Two things are in series - the LEARNER needs enough samples in its window
    /// to produce an estimate at all, and then <c>MaxRamp</c> moves the published value onto it.
    /// </summary>
    public class GForceMaxConvergenceReportTests
    {
        private readonly ITestOutputHelper _out;
        public GForceMaxConvergenceReportTests(ITestOutputHelper output) => _out = output;

        private const double Hz = 60.0;

        /// <summary>Feeds a constant magnitude at 60 Hz, calling Effective every frame exactly as the
        /// plugin does, and reports when the published value first gets within 5% of its destination.</summary>
        private void Run(string label, double setPoint, double actualMax)
        {
            var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = setPoint };
            s.SetCurrentGameAndCar("G", "C");

            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double floor = Math.Min(GForceSettings.MinLearnedLatMaxG, setPoint);
            double destination = Math.Max(actualMax, floor);

            double? firstMove = null, settled = null;
            double published = setPoint;

            for (int i = 0; i < (int)(Hz * 40); i++)      // 40 simulated seconds
            {
                DateTime now = t0.AddSeconds(i / Hz);
                s.ObserveLatG("G", "C", actualMax, now);
                published = s.EffectiveLatMaxG("G", "C", now);

                if (firstMove == null && Math.Abs(published - setPoint) > 0.01) firstMove = i / Hz;
                if (settled == null && Math.Abs(published - destination) <= Math.Abs(destination) * 0.05)
                    settled = i / Hz;
            }

            string floored = destination > actualMax + 1e-9 ? $"  (floored from {actualMax:F2})" : "";
            _out.WriteLine($"{label,-34} set {setPoint:F2} -> real {actualMax:F2}  dest {destination:F2}{floored}");
            _out.WriteLine($"    first movement   : {(firstMove.HasValue ? firstMove.Value.ToString("F2") + " s" : "never")}");
            _out.WriteLine($"    within 5% of dest: {(settled.HasValue ? settled.Value.ToString("F2") + " s" : "never")}");
            _out.WriteLine($"    value at 40 s    : {published:F3}");
            _out.WriteLine("");
        }

        /// <summary>
        /// The realistic case: the car spends only <paramref name="peakDuty"/> of its time at the
        /// maximum and the rest at a fraction of it. This is what actually decides the wait, because the
        /// estimator's pool is drawn from ranks 5%-14.5% of its window - so the peak has to make up
        /// roughly the top 15% of the samples before it can dominate the estimate.
        /// </summary>
        private void RunDutyCycle(string label, double setPoint, double actualMax, double peakDuty)
        {
            var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = setPoint };
            s.SetCurrentGameAndCar("G", "C");

            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double published = setPoint;
            double? within10 = null;
            int period = (int)(Hz * 10);   // a 10-second lap-like cycle

            for (int i = 0; i < (int)(Hz * 300); i++)      // 5 simulated minutes
            {
                DateTime now = t0.AddSeconds(i / Hz);
                bool atPeak = (i % period) < (int)(period * peakDuty);
                s.ObserveLatG("G", "C", atPeak ? actualMax : actualMax * 0.35, now);
                published = s.EffectiveLatMaxG("G", "C", now);

                if (within10 == null && published >= actualMax * 0.90) within10 = i / Hz;
            }

            _out.WriteLine($"{label,-24} within 10% of {actualMax:F1}g after " +
                           $"{(within10.HasValue ? within10.Value.ToString("F1") + " s" : "NOT within 300 s")}" +
                           $"   (at 300 s: {published:F2}g)");
        }

        [Fact]
        public void Report_convergence_speed_from_set_point_to_measured_maximum()
        {
            _out.WriteLine("CONVERGENCE FROM THE TYPED SET POINT TO THE MEASURED MAXIMUM");
            _out.WriteLine("  60 Hz telemetry, constant G at the real maximum, floor = Min(0.5, set point)");
            _out.WriteLine("");

            Run("big DROP (set 1.5, real 0.3)", 1.5, 0.3);
            Run("moderate DROP (set 1.5, real 1.0)", 1.5, 1.0);
            Run("big RISE (set 1.5, real 3.5)", 1.5, 3.5);
            Run("low set point (set 0.2, real 0.3)", 0.2, 0.3);

            _out.WriteLine("--------------------------------------------------------------------");
            _out.WriteLine("REALISTIC DRIVING - the peak is only touched occasionally, which is what");
            _out.WriteLine("actually governs the wait. Above, the maximum was fed CONTINUOUSLY.");
            _out.WriteLine("");

            RunDutyCycle("peak 50% of the time", 1.5, 3.5, 0.50);
            RunDutyCycle("peak 20% of the time", 1.5, 3.5, 0.20);
            RunDutyCycle("peak 10% of the time", 1.5, 3.5, 0.10);
            RunDutyCycle("peak  3% of the time", 1.5, 3.5, 0.03);

            Assert.True(true);   // report only
        }
    }
}

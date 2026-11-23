using System;
using System.Globalization;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// One G-Force Test Effect frame: the G-ball pad and wheel bar turned into telemetry and run through
    /// the REAL <see cref="GForceEngine"/> with every G-Force setting applied. Moved verbatim from the
    /// plugin page's Test Effect so the page and the ShakeIt panels compute the same frame.
    /// </summary>
    public static class GForceTestFrame
    {
        public sealed class Result
        {
            public GForceOutput Output { get; set; }
            public double LongitudinalG { get; set; }
            public double LateralG { get; set; }
            public bool Accelerating { get; set; }
            public double Wheel0100 { get; set; }
            public double Angle0100 { get; set; }
            public double LongMax { get; set; }
            public double LatMax { get; set; }
        }

        /// <param name="padX">-1..+1, +1 at the right of the pad.</param>
        /// <param name="padY">-1..+1, +1 at the top of the pad (braking).</param>
        /// <param name="wheel01">0..1 along the Lock/Slip bar.</param>
        /// <param name="angle01">0..1 along the Slip Angle bar (docs\slip-angle-design.md section 10) - fed
        /// to the engine as the projected Slip Angle; it only acts while Integrate Slip Angle is on.</param>
        public static Result Compute(double padX, double padY, double wheel01, GForceSettings settings, GForceEngine engine, TimeSpan dt,
            double angle01 = 0.0)
        {
            settings.ApplyTo(engine);

            double accelMax = settings.FixedAccelMaxG;
            double decelMax = settings.FixedDecelMaxG;
            double latMax = settings.FixedLatMaxG;

            // PAD ORIENTATION (owner, 2026-09-06): UP IS BRAKING, DOWN IS ACCELERATING, and the LEFT of
            // the pad is TURNING RIGHT. The ball marks where the load is thrown: brake and you go
            // forward/up; turn right and you go to your left. padY is +1 at the top of the pad, so the
            // LONGITUDINAL axis is read directly (top = positive = braking).
            //
            // THE LATERAL AXIS IS **NOT** NEGATED. The engine's convention is that a POSITIVE lateralG
            // biases the RIGHT pads, i.e. positive means TURNING LEFT. The pad's -1 is its left edge,
            // which is turning right, and turning right must read negative - so padX maps straight
            // through.
            bool accelerating = padY < 0.0;
            double longitudinalG = accelerating ? -padY * accelMax : padY * decelMax;
            double lateralG = padX * latMax;

            // The wheel value drives BOTH channels; the engine applies the lock and slip scales itself.
            double wheel = ClampMath.To0100(wheel01 * 100.0);
            double angle = ClampMath.To0100(angle01 * 100.0);

            double speed = 100.0;
            var oldFrame = new TelemetryFrame(groundSpeedKmh: accelerating ? speed : speed + 1.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: accelerating ? speed + 1.0 : speed,
                longitudinalG: longitudinalG,
                lateralG: lateralG,
                brakePercent: accelerating ? 0.0 : 80.0,
                throttlePercent: accelerating ? 80.0 : 0.0);

            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, dt);

            GForceOutput output = engine.Compute(
                sample, accelMax, decelMax, wheelLockAll0100: wheel, wheelSlipAll0100: wheel,
                latMaxG: latMax, slipAngleAll0100: angle);

            return new Result
            {
                Output = output,
                LongitudinalG = longitudinalG,
                LateralG = lateralG,
                Accelerating = accelerating,
                Wheel0100 = wheel,
                Angle0100 = angle,
                LongMax = accelerating ? accelMax : decelMax,
                LatMax = latMax,
            };
        }

        /// <summary>"  1.23G ( 45.6%)" - fixed width so the readout rows line up.</summary>
        public static string FormatG(double g, double max)
        {
            double percent = max > 1e-9 ? Math.Abs(g) / max * 100.0 : 0.0;
            return string.Format(CultureInfo.InvariantCulture, "{0,6:F2}G ({1,5:F1}%)", Math.Abs(g), percent);
        }

        /// <summary>Left / right channel pair.</summary>
        public static string FormatPair(double? left, double? right)
            => string.Format(CultureInfo.InvariantCulture, "{0,5:F1} / {1,5:F1}", left ?? 0.0, right ?? 0.0);

        /// <summary>The same 6-wide right-aligned field as <see cref="FormatG"/>'s leading number.</summary>
        public static string FormatWheel(double wheel0100)
            => string.Format(CultureInfo.InvariantCulture, "{0,6:F1}", wheel0100);
    }
}

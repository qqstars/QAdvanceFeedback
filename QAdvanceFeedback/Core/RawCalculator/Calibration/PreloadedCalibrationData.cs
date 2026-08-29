using System;

namespace QAdvanceFeedback.Core.RawCalculator.Calibration
{
    /// <summary>
    /// A faithful port of SimHub's own
    /// <c>SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.PreloadedCalibrationData</c> - the SHIPPED,
    /// per-game calibration SimHub loads from <c>ShakeIt\ShakeItPrecalibration\GameData.json</c>.
    /// <para/>
    /// WHAT IT ACTUALLY IS. Not a histogram at all: a preset is two numbers,
    /// <see cref="MeasuredMaximum"/> and <see cref="CorrectionFactor"/>, and the band it publishes is
    /// linear in the requested percentile - the same functional shape as
    /// <see cref="CalibrationData"/>'s own pre-maturity fallback. The live autocalibration is then
    /// blended in at a FIXED <see cref="AutocalibrationDataRatio"/>, which SimHub sets to 0.25 and never
    /// ramps. So where a preset exists, three quarters of the published band is shipped data
    /// permanently, and a session's own histogram can only ever move it a quarter of the way.
    /// <para/>
    /// That is SimHub's design decision, not this project's, and it is reproduced verbatim: matching
    /// ShakeIt's published values is the entire point of Layer 3.
    /// </summary>
    public sealed class PreloadedCalibrationData : ICalibrationData
    {
        /// <summary>SimHub's own default weight on the LIVE autocalibration when a preset exists
        /// (<c>CalibrationDataProvider.AutocalibrationDataRatio</c>). The remaining 0.75 stays with the
        /// shipped numbers, permanently.</summary>
        public const double DefaultAutocalibrationDataRatio = 0.25;

        public double CorrectionFactor = 1.0;

        public double MeasuredMaximum { get; set; }

        /// <summary>The live calibration for this same key, attached by the provider so the blend below
        /// has something to blend with. Null until then.</summary>
        public ICalibrationData AutoCalibrationData { get; set; }

        public double AutocalibrationDataRatio { get; set; }

        public bool IsReady => AutocalibrationDataRatio != 0.0 && AutoCalibrationData != null
            ? AutoCalibrationData.IsReady
            : true;

        public double Completion => AutocalibrationDataRatio != 0.0 && AutoCalibrationData != null
            ? AutoCalibrationData.Completion
            : 100.0;

        public bool IsPreset() => true;

        public ICalibrationData GetUnderlyingAutocalibration() => AutoCalibrationData;

        public double GetPercentile(double percentile, bool includeZero = false)
        {
            double preset = MeasuredMaximum * CorrectionFactor * percentile / 100.0;
            if (AutocalibrationDataRatio > 0.0 && AutoCalibrationData != null)
                preset = (AutoCalibrationData.GetPercentile(percentile, includeZero) - preset) * AutocalibrationDataRatio + preset;
            return preset;
        }

        /// <summary>
        /// SimHub throws <see cref="NotImplementedException"/> here, and that is not an oversight - it is
        /// load-bearing evidence about which channels can use a preset at all. The Lock branch
        /// (<c>GetRpsLock</c>) reads <c>GetAverage()</c>, so if SimHub ever shipped a preset for the
        /// <c>RPSToSpeed*</c> metrics it would throw on the first braking frame. It therefore ships
        /// presets only for percentile-read metrics (Slip), and Lock always runs on live autocalibration.
        /// <para/>
        /// Reproduced as a throw rather than quietly returning null so that any future call site which
        /// wrongly hands a preset to an average-reading branch fails loudly here, in a test, rather than
        /// silently publishing a wrong number in a car.
        /// </summary>
        public double? GetAverage()
            => throw new NotImplementedException(
                "A preset calibration has no mean - SimHub ships presets only for percentile-read metrics (Slip), "
                + "never for the RPSToSpeed metrics the Lock branch averages. See this method's own remarks.");
    }
}

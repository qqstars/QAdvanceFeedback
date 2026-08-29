namespace QAdvanceFeedback.Core.RawCalculator.Calibration
{
    /// <summary>
    /// The contract SimHub's own ShakeIt calibration objects expose, ported so the two implementations
    /// this project needs - a live, accumulating <see cref="CalibrationData"/> and a shipped, fixed
    /// <see cref="PreloadedCalibrationData"/> - are interchangeable at every call site exactly as they
    /// are inside SimHub.
    /// </summary>
    public interface ICalibrationData
    {
        /// <summary>True once this calibration holds enough evidence that SimHub stops showing a
        /// "calibrating" state for it.</summary>
        bool IsReady { get; }

        /// <summary>Progress toward <see cref="IsReady"/>, as a percentage.</summary>
        double Completion { get; }

        /// <summary>True for a shipped/preset calibration, false for one learned live.</summary>
        bool IsPreset();

        /// <summary>The live, accumulating calibration underneath - itself for
        /// <see cref="CalibrationData"/>, the attached autocalibration for a preset.</summary>
        ICalibrationData GetUnderlyingAutocalibration();

        /// <summary>NEVER returns null - see <see cref="CalibrationData.GetPercentile"/> for why that
        /// matters more than anything else in this port.</summary>
        double GetPercentile(double percentile, bool includeZero = false);

        /// <summary>Null only when there is genuinely nothing to average. Note a preset deliberately does
        /// NOT implement this - see <see cref="PreloadedCalibrationData.GetAverage"/>.</summary>
        double? GetAverage();
    }
}

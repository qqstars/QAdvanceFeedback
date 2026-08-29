using System;
using GameReaderCommon;
using GameReaderCommon.Feedback;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Health;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Layer 2: the ONLY class allowed to know SimHub's own type names
    /// (<see cref="GameData"/>/<see cref="StatusDataBase"/>/<see cref="FeedbackData"/>/
    /// <see cref="LocalVelocity"/>, and, for <see cref="CaptureRawTelemetry"/> diagnostics only,
    /// <see cref="PluginManager"/>/<see cref="FeedbackCapabilities"/>). Maps them onto Layer 1's
    /// game-agnostic <see cref="ITelemetrySample"/>/<see cref="ITelemetryFrame"/> shape so
    /// <c>Core\</c> (Layer 3, the algorithm) never has to reference SimHub or GameReaderCommon at all -
    /// the test project links <c>Core\**\*.cs</c> straight into a plain net8.0 test assembly with no
    /// SimHub package reference at all, which only compiles because that boundary holds.
    /// <para/>
    /// Implements the public <see cref="ITelemetryAdapter"/> contract - see that interface for the full
    /// documented shape/guards.
    /// <para/>
    /// Reads exactly what the Raw-layer algorithm needs: the current and previous frame, ground speed
    /// (kept separate from the base speed reading rather than pre-merged - see
    /// <see cref="ITelemetryFrame"/>'s own remarks on why), RPM, gear, pedals, lateral velocity, plus
    /// longitudinal/lateral acceleration for later layers' G-force needs (not read by Layer 3 itself).
    /// <para/>
    /// UNVERIFIED UNIT ASSUMPTION, flagged rather than silently assumed: <see cref="FeedbackData.GroundSpeed"/>
    /// carries no unit documentation in the reference assembly. It is mapped here to
    /// <see cref="ITelemetryFrame.GroundSpeedKmh"/> (assumed km/h, consistent with its un-converted
    /// fallback to <see cref="StatusDataBase.SpeedKmh"/> and this project's own convention of suffixing
    /// genuinely metres/second fields with "Mps"). If this assumption is ever wrong, only the speed gate
    /// and the RPM/speed brake-and-throttle terms are affected, and only for titles that actually
    /// populate <see cref="FeedbackData.GroundSpeed"/> - most fall back to the unambiguous base reading.
    /// <para/>
    /// UNIT CONVERSION: <see cref="StatusDataBase.AccelerationSurge"/>/<see cref="StatusDataBase.AccelerationSway"/>
    /// are metres/second², confirmed against a captured session, despite
    /// <see cref="ITelemetryFrame.LongitudinalG"/>/<see cref="ITelemetryFrame.LateralG"/>'s own "G"
    /// names - every consumer of those two fields is calibrated in G, so the conversion belongs here,
    /// once, at this SimHub-specific edge, rather than downstream where every consumer would otherwise
    /// need to know the raw units.
    /// </summary>
    internal sealed class SimHubTelemetryAdapter : ITelemetryAdapter
    {
        private DateTime? _lastFrameTime;

        /// <summary>Resets the previous-FrameTime bookkeeping - call this on a game switch/session
        /// restart so the first frame of a new session doesn't report a huge Dt spanning the gap.</summary>
        public void Reset() => _lastFrameTime = null;

        public TelemetrySample Read(GameData data)
        {
            TelemetryFrame newFrame = ToFrame(data?.NewData);
            TelemetryFrame oldFrame = ToFrame(data?.OldData);
            DateTime frameTime = data?.FrameTime ?? DateTime.UtcNow;

            TimeSpan? dt = _lastFrameTime.HasValue ? frameTime - _lastFrameTime.Value : (TimeSpan?)null;
            _lastFrameTime = frameTime;

            return new TelemetrySample(newFrame, oldFrame, frameTime, dt);
        }

        /// <summary>
        /// DIAGNOSTICS ONLY - see <see cref="ITelemetryAdapter.CaptureRawTelemetry"/>'s contract. A
        /// deliberately thin map from SimHub's own types onto plain primitives, handed straight to
        /// <see cref="RawWheelTelemetryBuilder.Build"/> - the null-vs-zero gating logic itself lives
        /// there (SimHub-free, unit-tested directly), the same "Layer 2 maps, Layer 1 holds the logic"
        /// split <see cref="ToFrame"/> already uses for <see cref="TelemetryFrame"/>.
        /// <para/>
        /// <paramref name="pluginManager"/> is needed ONLY to reach the live
        /// <see cref="FeedbackCapabilities"/> object - every other reading here comes from
        /// <paramref name="data"/> alone, same as <see cref="Read"/>.
        /// </summary>
        public RawWheelTelemetrySnapshot CaptureRawTelemetry(GameData data, PluginManager pluginManager)
        {
            FeedbackData fb = data?.NewData?.FeedbackData;
            FeedbackCapabilities caps = SafeGetFeedbackCapabilities(pluginManager);

            if (caps == null)
            {
                // Capabilities could not be reached this frame - every capability AND every gated
                // per-wheel reading must come back null (unknown), never a guessed value.
                return RawWheelTelemetryBuilder.Build(
                    capabilityWheelsRPS: null, wheelRps: fb?.WheelRPS,
                    capabilityWheelsSpeed: null, wheelSpeed: fb?.WheelSpeed,
                    capabilityWheelsSlip: null, wheelSlipRatio: fb?.WheelSlip,
                    isFlying: fb?.IsFlying ?? 0,
                    orientationYawChangePerSecond: fb?.OrientationYawChangePerSecond);
            }

            return RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: caps.WheelsRPS, wheelRps: fb?.WheelRPS,
                capabilityWheelsSpeed: caps.WheelsSpeed, wheelSpeed: fb?.WheelSpeed,
                capabilityWheelsSlip: caps.WheelsSlip, wheelSlipRatio: fb?.WheelSlip,
                capabilityWheelsSlipDirectMode: caps.WheelsSlipDirectMode,
                capabilityWheelSlipUseSimpleBraking: caps.WheelSlipUseSimpleBraking,
                capabilityDetectLockFromWheelsSpeed: caps.DetectLockFromWheelsSpeed,
                capabilityDetectLockFromWheelsRPSAndDummyRadius: caps.DetectLockFromWheelsRPSAndDummyRadius,
                capabilitySpeed: caps.Speed,
                capabilityRpm: caps.RPM,
                capabilityGameFamily: caps.GameFamily.ToString(),
                capabilityWheelSlipCalibrationProviderSlipScale: caps.WheelSlipCalibrationProvider?.SlipScale,
                isFlying: fb?.IsFlying ?? 0,
                orientationYawChangePerSecond: fb?.OrientationYawChangePerSecond);
        }

        /// <summary>
        /// PIPELINE-EXCEPTION-SAFETY (docs\pipeline-exception-safety-report.md): <c>GetFeedbackCapabilities</c>
        /// is a real, directly-referenced SimHub API (not reflection), but it is exactly the kind of
        /// undocumented-shape SimHub internal the brief calls a realistic failure mode - a future SimHub
        /// version could throw here (an unsupported/uninitialised game manager state) just as easily as
        /// a reflection target could move. <see cref="CaptureRawTelemetry"/>'s own contract ("must never
        /// throw", see <see cref="ITelemetryAdapter"/>) already required this to degrade to null rather
        /// than propagate - this wraps the call itself so that contract actually holds, and records the
        /// fault once via <see cref="HealthRegistry"/> so the settings UI can tell a driver "diagnostics
        /// only" rather than staying silently blank.
        /// </summary>
        private static FeedbackCapabilities SafeGetFeedbackCapabilities(PluginManager pluginManager)
        {
            try
            {
                return pluginManager?.GameManager?.GetFeedbackCapabilities();
            }
            catch (Exception e)
            {
                HealthRegistry.Report(HealthSubsystems.CapabilityDetection, HealthSeverity.Degraded,
                    "Health.Impact.CapabilityDetection", e.ToString(), isSimHubCompatibilityIssue: true);
                return null;
            }
        }

        private static TelemetryFrame ToFrame(StatusDataBase d)
        {
            if (d == null) return TelemetryFrame.Empty;

            FeedbackData fb = d.FeedbackData;
            LocalVelocity lv = fb?.LocalVelocity;

            return new TelemetryFrame(
                groundSpeedKmh: fb?.GroundSpeed,
                speedKmh: d.SpeedKmh,
                groundSpeedMps: lv?.SpeedMPS(),
                rpm: d.Rpms,
                gear: d.Gear,
                brakePercent: d.Brake,
                throttlePercent: d.Throttle,
                clutchPercent: d.Clutch,
                lateralLocalVelocity: lv?.Lateral,
                longitudinalG: d.AccelerationSurge.HasValue ? Core.AccelerationUnits.Ms2ToG(d.AccelerationSurge.Value) : (double?)null,
                lateralG: d.AccelerationSway.HasValue ? Core.AccelerationUnits.Ms2ToG(d.AccelerationSway.Value) : (double?)null,
                // A title that always reports 0/false for a field it does not really support degrades
                // to "never flagged", the correct safe default for a plain non-nullable SimHub field.
                isInPit: d.IsInPit != 0 || d.IsInPitLane != 0,
                isReplay: d.IsGameReplay,
                isSessionRestart: d.IsSessionRestart,
                wheelOnLooseSurfaceFrontLeft: WheelOnLooseSurface(fb, 0),
                wheelOnLooseSurfaceFrontRight: WheelOnLooseSurface(fb, 1),
                wheelOnLooseSurfaceRearLeft: WheelOnLooseSurface(fb, 2),
                wheelOnLooseSurfaceRearRight: WheelOnLooseSurface(fb, 3));
        }

        /// <summary>See <see cref="ITelemetryFrame.WheelOnLooseSurfaceFrontLeft"/>'s own remarks on why
        /// null-vs-false is unresolvable for this one field - returns the raw bool whenever the array is
        /// present and long enough, null only when the array itself is missing/malformed.</summary>
        private static bool? WheelOnLooseSurface(FeedbackData fb, int wheelIndex)
        {
            bool[] array = fb?.WheelInGrassOrGravel;
            return array != null && array.Length > wheelIndex ? array[wheelIndex] : (bool?)null;
        }
    }
}

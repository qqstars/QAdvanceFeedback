namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// One frame's worth of game-agnostic telemetry, described by PURPOSE rather than by any one
    /// game's or sim's own field names (Layer 1 - no configuration, no SimHub types anywhere in this
    /// file or its assembly).
    /// <para/>
    /// Every reading is nullable and that is load-bearing: null means "this game/session did not
    /// supply this value this frame", NOT "the value is zero". A car with the clutch fully engaged
    /// reports ClutchPercent == 0 (a REAL zero reading); a game that doesn't expose a clutch channel
    /// at all reports ClutchPercent == null. Higher layers must check HasValue (nullable's own
    /// pattern) before trusting a reading, and degrade - e.g. treat the guard it belongs to as not
    /// satisfied - rather than silently treating a missing reading as a real zero. See
    /// <c>BrakeSpeedSlipModel</c> for where this actually matters (the speed/RPM guards).
    /// </summary>
    public interface ITelemetryFrame
    {
        /// <summary>
        /// Ground speed in km/h, from the game's own dedicated "ground speed" channel when the
        /// game/plugin supplies one (SimHub: FeedbackData.GroundSpeed - see
        /// SimHubTelemetryAdapter's remarks for why this is believed to be km/h-scaled despite its
        /// name, and why that is flagged as an inference rather than a proven fact). Null when the
        /// game/plugin does not supply this specific channel.
        /// </summary>
        double? GroundSpeedKmh { get; }

        /// <summary>
        /// Ground speed in km/h from the base telemetry channel every SimHub game plugin populates
        /// (SimHub: StatusDataBase.SpeedKmh). This is the exact fallback SimHub's own
        /// WheelSlipEffect falls back to when <see cref="GroundSpeedKmh"/> is unavailable - kept as
        /// its own reading (not pre-merged by Layer 2) so Layer 3 can reproduce SimHub's own
        /// <c>??</c> fallback chain itself, rather than trusting an opaque pre-merged number.
        /// </summary>
        double? SpeedKmh { get; }

        /// <summary>
        /// Ground speed in metres/second, purpose-derived (e.g. from the magnitude of local
        /// velocity components) rather than taken from any one game's own field. Not consumed by the
        /// Layer 3 legacy algorithm (which is faithfully reproduced against the km/h-flavoured
        /// fields above, exactly as SimHub itself computes it) - exposed for later layers/effects
        /// that want a canonical, unit-unambiguous ground speed.
        /// </summary>
        double? GroundSpeedMps { get; }

        /// <summary>Engine RPM this frame.</summary>
        double? Rpm { get; }

        /// <summary>
        /// Selected gear, e.g. "1".."8", "R", "N". Null when the game does not report a gear at all
        /// - distinct from "N", which is itself a real, known reading (neutral).
        /// </summary>
        string Gear { get; }

        /// <summary>Brake pedal, 0-100.</summary>
        double? BrakePercent { get; }

        /// <summary>Throttle pedal, 0-100.</summary>
        double? ThrottlePercent { get; }

        /// <summary>Clutch pedal, 0-100 (SimHub convention: 0 = fully engaged/released pedal).</summary>
        double? ClutchPercent { get; }

        /// <summary>
        /// Lateral component of the car's local velocity vector (SimHub:
        /// FeedbackData.LocalVelocity.Lateral).
        /// </summary>
        double? LateralLocalVelocity { get; }

        /// <summary>
        /// Longitudinal G-force (SimHub: AccelerationSurge). Not consumed by the Layer 3 legacy
        /// algorithm - exposed for later layers (G-force channels are explicitly out of scope here).
        /// </summary>
        double? LongitudinalG { get; }

        /// <summary>
        /// Lateral G-force (SimHub: AccelerationSway). Not consumed by the Layer 3 legacy algorithm
        /// - exposed for later layers (G-force channels are explicitly out of scope here).
        /// </summary>
        double? LateralG { get; }

        /// <summary>
        /// Whether the car is currently in the pit or pit lane (SimHub: StatusDataBase.IsInPit != 0
        /// OR IsInPitLane != 0). Added for <see cref="TelemetryLearningGate"/> - a car sitting in the
        /// pit is not evidence of the car's own real driving limits, and its speed/G readings must not
        /// be folded into a cross-frame learner. Null when the title does not report either field at
        /// all (the gate treats null as "not known to be a problem", never as an invented rejection).
        /// </summary>
        bool? IsInPit { get; }

        /// <summary>
        /// Whether this frame is being played back from a replay rather than driven live (SimHub:
        /// StatusDataBase.IsGameReplay). See <see cref="IsInPit"/>'s remarks - same reasoning,
        /// different signal.
        /// </summary>
        bool? IsReplay { get; }

        /// <summary>
        /// Whether this frame marks a session restart (SimHub: StatusDataBase.IsSessionRestart) - a
        /// teleport-like discontinuity in everything the frame reports, not a real driving moment.
        /// See <see cref="IsInPit"/>'s remarks.
        /// </summary>
        bool? IsSessionRestart { get; }

        /// <summary>
        /// Whether the FRONT LEFT wheel is currently on a loose surface (grass/gravel) rather than
        /// sealed tarmac (SimHub: <c>FeedbackData.WheelInGrassOrGravel[0]</c>, confirmed by decompiling
        /// <c>GameReaderCommon.FeedbackData</c> - docs\branch-dispatch-and-source-keyed-learning-report.md,
        /// "surface-keyed learning"). Added because a learned grip reference dominated by tarmac's
        /// higher achievable deceleration silently reads a genuine loose-surface limit as low severity
        /// (the owner's own worked example: ~1.5g tarmac peak vs ~0.4g grass peak - 0.4g against a
        /// 1.5g reference reads ~27%, near-silent at exactly the moment grip is lowest).
        /// <para/>
        /// NULL VS FALSE IS UNRESOLVABLE FOR THIS ONE FIELD, FLAGGED HONESTLY: unlike
        /// <c>WheelRPS</c>/<c>WheelSpeed</c>/<c>WheelSlip</c> (each backed by its own
        /// <c>FeedbackCapabilities</c> flag - <c>WheelsRPS</c> etc.), decompiling
        /// <c>FeedbackCapabilities</c> found NO corresponding capability flag for
        /// <c>WheelInGrassOrGravel</c> at all - there is no reachable way to distinguish "this title
        /// genuinely reports tarmac right now" from "this title does not populate this field and it
        /// defaults to false" the way the raw-telemetry diagnostics gate already does for the other
        /// three arrays. This is NOT silently assumed away: a title that never reports <c>true</c>
        /// degrades, BY CONSTRUCTION (see <c>SurfaceLooseFraction</c>'s own remarks), to exactly the
        /// single-reference behaviour this plugin had before surface-keying existed - so the ambiguity
        /// is harmless for correctness even though it cannot be resolved for certainty - and
        /// <c>Diag.Capabilities.SurfaceEverReportedLoose</c> (a latching, observed-evidence diagnostic,
        /// not a capability flag) publishes whether any loose reading was EVER actually seen this
        /// session, so the ambiguity is visible on a rig even though it cannot be resolved in code.
        /// </summary>
        bool? WheelOnLooseSurfaceFrontLeft { get; }

        /// <summary>See <see cref="WheelOnLooseSurfaceFrontLeft"/> - the front right wheel.</summary>
        bool? WheelOnLooseSurfaceFrontRight { get; }

        /// <summary>See <see cref="WheelOnLooseSurfaceFrontLeft"/> - the rear left wheel.</summary>
        bool? WheelOnLooseSurfaceRearLeft { get; }

        /// <summary>See <see cref="WheelOnLooseSurfaceFrontLeft"/> - the rear right wheel.</summary>
        bool? WheelOnLooseSurfaceRearRight { get; }
    }
}

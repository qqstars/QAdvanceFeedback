using System;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>Whether the car is measurably slowing or speeding up, per differentiated ground
    /// speed - see <see cref="LongitudinalDirectionResolver"/>'s remarks. "Unknown" covers both "no
    /// derivative available yet" (the very first frame of a session, or a rejected implausible
    /// jump) and "within the dead band" (speed is not changing by enough to call a direction either
    /// way) - in both cases neither the Lock nor the Slip channel has anything honest to report.</summary>
    public enum LongitudinalMotionState { Unknown, Slowing, SpeedingUp }

    /// <summary>
    /// THE FIX FOR THE OWNER'S STANDING RULE VIOLATION (read this before touching
    /// <see cref="NormalizedWheelLockSlipEngine"/> again): which of Lock/Slip is the "active" channel
    /// this frame must NEVER be decided from which pedal is committed - the owner has stated this
    /// emphatically, more than once, because brake and throttle overlap constantly in real driving
    /// (left-foot braking, trail braking, a downshift blip). The engine used to gate Lock on
    /// <c>BrakePercent</c> and Slip on <c>ThrottlePercent</c>; this class replaces that with the
    /// technique proven in the sibling ReliableWheelLockSlip project's own Task 32 fix
    /// (<c>InputResolver.ResolveAcceleration</c>): direction comes ONLY from differentiated ground
    /// speed, whose sign is ours by definition (we compute it ourselves from two speed readings) and
    /// therefore needs no per-game telemetry convention to be trusted - unlike
    /// <see cref="ITelemetryFrame.LongitudinalG"/>, whose sign is NOT verified for every title (see
    /// <see cref="AchievedMotion"/>'s remarks) and which this class never reads at all.
    /// <para/>
    /// INPUTS: <see cref="ITelemetrySample.New"/>/<see cref="ITelemetrySample.Old"/>'s
    /// <see cref="ITelemetryFrame.GroundSpeedKmh"/> (falling back to
    /// <see cref="ITelemetryFrame.SpeedKmh"/>, exactly like <see cref="AchievedMotion"/>'s own
    /// fallback) plus <see cref="ITelemetrySample.Dt"/> - SimHub hands this plugin a genuine
    /// previous-frame/current-frame pair on every <c>DataUpdate</c> call (GameData.OldData/NewData),
    /// so no persistent "remember the last speed myself" bookkeeping is needed the way the sibling
    /// project's <c>InputResolver</c> requires (that project's <c>TelemetryFrame</c> carries only ONE
    /// frame at a time). What IS persistent here, across calls, is the light smoothing filter below -
    /// the raw one-frame derivative is comparatively noisy, and smoothing it over successive frames is
    /// what a single Old/New comparison cannot do by itself.
    /// <para/>
    /// DEAD BAND + LIGHT SMOOTHING (per the brief - "noise near constant speed must not chatter the
    /// state"): the raw per-frame derivative is folded into an exponential moving average
    /// (<see cref="FilterAlpha"/>), and only the SMOOTHED value is compared against
    /// <see cref="DeadBandMs2"/>. A car holding a genuinely constant speed produces a raw derivative
    /// that jitters around zero from ordinary telemetry noise; without the dead band that jitter would
    /// flip the state (and therefore which channel is "active") back and forth every frame. Both
    /// constants mirror the sibling project's own Task 32 values (<c>AccelFilterAlpha</c>,
    /// <c>DirectionDeadBandMs2</c>) - proven in practice, not re-derived from scratch here.
    /// <para/>
    /// AN IMPLAUSIBLE JUMP (a teleport, a session restart, a pit-to-track warp) is rejected outright -
    /// not folded into the filter at all - rather than momentarily reading tens of g and flipping the
    /// state on a single garbage frame; the previous state (and the previous filtered value) is held
    /// instead. The very next genuinely small, physically-plausible derivative resumes updating the
    /// filter from wherever it was left, exactly as before the jump.
    /// <para/>
    /// SIGN-AGNOSTIC BY CONSTRUCTION, NOT BY VERIFICATION: this class never reads
    /// <see cref="ITelemetryFrame.LongitudinalG"/> at all, so a title that reports that channel with an
    /// inverted convention (positive while genuinely slowing - the exact, evidenced Forza Horizon 6
    /// case the brief cites) cannot affect this class's answer in any way. <see cref="AchievedMotion"/>
    /// remains the source of MAGNITUDE (via <c>Math.Abs</c>, itself already sign-agnostic); this class
    /// is the ONLY source of DIRECTION.
    /// </summary>
    public sealed class LongitudinalDirectionResolver
    {
        /// <summary>m/s^2 - small relative to any real braking or accelerating event (a fraction of a
        /// percent of a modest 1g stop) but large enough that ordinary speed-sampling jitter at a
        /// genuinely constant speed cannot cross it. Matches the sibling project's own
        /// <c>DirectionDeadBandMs2</c>.</summary>
        private const double DeadBandMs2 = 0.6;

        /// <summary>Exponential-moving-average weight for the light smoothing the brief asks for.
        /// Matches the sibling project's own <c>AccelFilterAlpha</c> - proven in practice.</summary>
        private const double FilterAlpha = 0.35;

        /// <summary>~6g - a raw derivative beyond this is a teleport/session-restart discontinuity,
        /// not a real, physically achievable deceleration/acceleration; rejected outright rather than
        /// folded into the filter (see this class's remarks).</summary>
        private const double MaxPlausibleMs2 = 60.0;

        private double _filteredMs2;
        private LongitudinalMotionState _state = LongitudinalMotionState.Unknown;

        /// <summary>The most recently resolved state - exposed mainly for diagnostics; every real
        /// caller should use <see cref="Resolve"/>'s own return value for the frame it just
        /// processed.</summary>
        public LongitudinalMotionState State => _state;

        /// <summary>The current smoothed derivative in m/s^2, signed (positive = speeding up,
        /// negative = slowing) - exposed for diagnostics only, mirroring the sibling project's own
        /// <c>Diag.DerivedAccelMs2</c>.</summary>
        public double FilteredAccelerationMs2 => _filteredMs2;

        /// <summary>
        /// Resolves this frame's direction from <paramref name="sample"/>'s own Old/New ground-speed
        /// pair. Holds the previous state (and does not touch the filter) whenever this frame has
        /// nothing usable to offer - no speed reading on either side, no <see cref="ITelemetrySample.Dt"/>,
        /// a non-positive Dt, or an implausible jump - since there is nothing honest to update to; the
        /// caller must treat <see cref="LongitudinalMotionState.Unknown"/> (whether freshly returned or
        /// held over) as "gate both channels off", never as "assume slowing" or "assume speeding up".
        /// </summary>
        public LongitudinalMotionState Resolve(ITelemetrySample sample)
        {
            double? newSpeed = sample?.New?.GroundSpeedKmh ?? sample?.New?.SpeedKmh;
            double? oldSpeed = sample?.Old?.GroundSpeedKmh ?? sample?.Old?.SpeedKmh;
            double? dtSeconds = sample?.Dt?.TotalSeconds;

            if (newSpeed.HasValue && oldSpeed.HasValue && dtSeconds.HasValue && dtSeconds.Value > 0.0)
            {
                double deltaKmh = newSpeed.Value - oldSpeed.Value;
                double deltaMs = deltaKmh / 3.6;
                double rawMs2 = deltaMs / dtSeconds.Value;

                if (ClampMath.IsFinite(rawMs2) && Math.Abs(rawMs2) <= MaxPlausibleMs2)
                {
                    _filteredMs2 += FilterAlpha * (rawMs2 - _filteredMs2);

                    if (_filteredMs2 <= -DeadBandMs2) _state = LongitudinalMotionState.Slowing;
                    else if (_filteredMs2 >= DeadBandMs2) _state = LongitudinalMotionState.SpeedingUp;
                    else _state = LongitudinalMotionState.Unknown;
                }
                // else: implausible jump - held, not folded into the filter (see class remarks).
            }
            // else: nothing usable this frame - held.

            return _state;
        }

        /// <summary>Clears the filter and state - call on a game/session switch, mirroring
        /// <c>SimHubTelemetryAdapter.Reset</c>.</summary>
        public void Reset()
        {
            _filteredMs2 = 0.0;
            _state = LongitudinalMotionState.Unknown;
        }
    }
}

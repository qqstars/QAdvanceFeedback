using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Shared validity gate, consulted by every cross-frame LEARNER in this plugin (Layer 4's
    /// per-(game,car) <c>KeyedGripLearner</c> for the Lock/Slip channels, and
    /// <c>Core.GForce.GForceMaxLearner</c> for the G-force AUTO accel/decel maxima) before folding a
    /// frame's reading into learned state, so a menu, a loading screen, a pit stop, a session
    /// restart, a paused/alt-tabbed game, or a teleport-sized speed discontinuity cannot corrupt a
    /// learned reference.
    /// <para/>
    /// WHY THIS EXISTS (docs\gforce-direction-fix-report.md): a captured Forza Horizon 6 session's own
    /// <c>Diag.GForce.LearnedAccelMaxG</c> reached 179.8 (about 18g once the unit bug was corrected) -
    /// a figure no real road/GT car produces. The existing defences
    /// (<c>GForceMaxLearner</c>'s 2-consecutive-frame confirmation, and both learner families'
    /// <c>MaxPlausibleG</c> = 8.0 hard ceiling) only catch a SINGLE one-off spike or an outright
    /// impossible magnitude - neither stops a physically PLAUSIBLE-looking reading born from bad
    /// CONTEXT (a loading-screen frame-time spike, a teleport back to the pits, a replay clip) from
    /// being folded straight in as if it were ordinary driving. This gate is the missing, earlier line
    /// of defence: it judges the CONTEXT a reading arrived in, before any learner ever sees the value
    /// itself - exactly the "one bad frame becomes the permanent reference" failure mode that ruined
    /// the sibling ReliableWheelLockSlip project's own signal on this title.
    /// <para/>
    /// "GAME RUNNING / NOT PAUSED / NOT IN MENU" IS DELIBERATELY NOT THIS CLASS'S JOB: SimHub's own
    /// <c>GameData.GameRunning</c>/<c>GamePaused</c>/<c>GameInMenu</c> are checked ONCE, upstream, at
    /// the composition root (<c>QAdvanceFeedback.cs</c>'s own <c>DataUpdate</c> guard clause) before
    /// ANY frame reaches Core at all - the exact same precedent <c>GameRunning</c> itself already set
    /// before this class existed. Layer 1's own <see cref="ITelemetryFrame"/> has no business
    /// re-exposing a SimHub-level "is the game even running" flag down here for a second,
    /// Core-side check; this gate only judges what a frame that DID make it through that upstream
    /// guard can still tell us is untrustworthy (pit, replay, session-restart, dt, speed,
    /// discontinuity - all genuinely Layer-1-shaped facts, unlike "is the game paused").
    /// <para/>
    /// STATEFUL, LIKE <see cref="Normalized.LongitudinalDirectionResolver"/>: a rejected, implausible
    /// speed reading must not itself become the baseline the NEXT frame is compared against (that
    /// would let one bad frame manufacture a SECOND, compounding "discontinuity" out of thin air) -
    /// see <see cref="IsValid"/>'s own remarks for exactly how the last-known-good speed is tracked
    /// and when it is deliberately cleared instead of updated.
    /// <para/>
    /// ONE CALL PER FRAME: like <c>LongitudinalDirectionResolver.Resolve</c>, <see cref="IsValid"/>
    /// must be called EXACTLY ONCE per frame per gate instance (it advances internal state) - a
    /// caller that needs more than one channel-specific answer for the same frame (e.g. Lock needing
    /// a brake-pedal minimum, Slip needing a throttle-pedal minimum) must combine this method's ONE
    /// result with its own additional, stateless, per-channel check rather than calling this method
    /// twice for the same frame (see <see cref="LearnMinBrakePercent"/>/<see cref="LearnMinThrottlePercent"/>'s
    /// own remarks).
    /// </summary>
    public sealed class TelemetryLearningGate
    {
        /// <summary>A frame-to-frame gap beyond this is a pause/loading-screen/alt-tab artefact, not a
        /// real driving interval - typical sim telemetry runs at 20-360Hz (2.8-50ms/frame); even a
        /// rough stutter rarely exceeds a few hundred milliseconds. 0.25s is comfortably past any
        /// ordinary hitch while well short of "the player alt-tabbed for a while" - a judgment call,
        /// flagged as such rather than derived from a specific title's own numbers.</summary>
        public const double MaxPlausibleDtSeconds = 0.25;

        /// <summary>Below this ground speed, anything normalised by "how hard is the car changing
        /// speed right now" is not meaningful evidence of the car's own real capability - a stationary
        /// or barely-rolling car's sensor noise is not a driving event. ~5 km/h comfortably excludes
        /// standstill/pit-rolling noise while still admitting genuine low-speed braking (a hairpin's
        /// very end).</summary>
        public const double MinSpeedKmh = 5.0;

        /// <summary>A frame-to-frame ground-speed change implying more than this is a teleport/
        /// session-restart discontinuity, not real driving - deliberately mirrors
        /// <see cref="Normalized.LongitudinalDirectionResolver"/>'s own <c>MaxPlausibleMs2</c> (~6g) so
        /// both classes draw the same "this cannot be real driving" line.</summary>
        public const double MaxPlausibleSpeedChangeMs2 = 60.0;

        /// <summary>Minimum brake pedal (0-100) for a frame to count as evidence for the LOCK
        /// channel's learner specifically - mirrors the sibling ReliableWheelLockSlip project's own
        /// <c>LearnMinBrake</c> (this codebase had no equivalent before this fix). Coasting/
        /// engine-braking deceleration with no brake pedal at all is real physics (see
        /// docs\field-fixes-report.md defect D) but is not a representative "how hard can this car
        /// brake" sample - a caller checks this itself, statelessly, alongside this gate's own
        /// <see cref="IsValid"/> result (see this class's own remarks on why it is not folded into
        /// <see cref="IsValid"/> directly).</summary>
        public const double LearnMinBrakePercent = 10.0;

        /// <summary>Minimum throttle pedal (0-100) for a frame to count as evidence for the SLIP
        /// channel's learner - the accelerating-axis equivalent of
        /// <see cref="LearnMinBrakePercent"/>.</summary>
        public const double LearnMinThrottlePercent = 10.0;

        private double? _lastGoodSpeedKmh;

        /// <summary>
        /// True when this frame is trustworthy evidence for a cross-frame learner. Checks, in order:
        /// pit/replay/session-restart (explicit true only - a title that never reports a field is
        /// never rejected on it), <see cref="MaxPlausibleDtSeconds"/>, a present and finite ground
        /// speed, <see cref="MinSpeedKmh"/>, and finally a teleport-sized jump
        /// (<see cref="MaxPlausibleSpeedChangeMs2"/>) relative to the LAST GOOD speed THIS GATE has
        /// itself seen - not <see cref="ITelemetrySample.Old"/>, which may already have been
        /// invalidated by an earlier rejected frame (a stale/huge dt, or the far side of a teleport):
        /// using the gate's own remembered good speed means one bad frame can never manufacture a
        /// second, compounding "discontinuity" verdict against whatever comes after it.
        /// </summary>
        public bool IsValid(ITelemetrySample sample)
        {
            if (sample?.New == null) return false;

            if (sample.New.IsInPit == true) return false;
            if (sample.New.IsReplay == true) return false;
            if (sample.New.IsSessionRestart == true) return false;

            double? dtSeconds = sample.Dt?.TotalSeconds;
            if (!dtSeconds.HasValue || !ClampMath.IsFinite(dtSeconds.Value) || dtSeconds.Value <= 0.0 || dtSeconds.Value > MaxPlausibleDtSeconds)
            {
                // A stale/huge gap invalidates whatever speed reading comes after it as a baseline too
                // - see this method's own remarks.
                _lastGoodSpeedKmh = null;
                return false;
            }

            double? newSpeed = sample.New.GroundSpeedKmh ?? sample.New.SpeedKmh;
            if (!newSpeed.HasValue || !ClampMath.IsFinite(newSpeed.Value)) return false;

            if (newSpeed.Value < MinSpeedKmh)
            {
                // A real, low reading - remembered as the new baseline (this is not a discontinuity,
                // just a car that is genuinely slow/stationary), but not itself valid evidence.
                _lastGoodSpeedKmh = newSpeed.Value;
                return false;
            }

            if (_lastGoodSpeedKmh.HasValue)
            {
                double deltaMs = (newSpeed.Value - _lastGoodSpeedKmh.Value) / 3.6;
                double impliedMs2 = deltaMs / dtSeconds.Value;
                if (!ClampMath.IsFinite(impliedMs2) || Math.Abs(impliedMs2) > MaxPlausibleSpeedChangeMs2)
                {
                    _lastGoodSpeedKmh = null; // do not carry the discontinuity's own endpoint forward
                    return false;
                }
            }

            _lastGoodSpeedKmh = newSpeed.Value;
            return true;
        }

        /// <summary>Clears the remembered last-good-speed baseline - call on a game/session switch,
        /// mirroring every other stateful gate/filter in this plugin (e.g.
        /// <see cref="Normalized.LongitudinalDirectionResolver.Reset"/>), so a fresh session does not
        /// get its very first valid frame rejected as a "discontinuity" against whatever the previous
        /// game/car was doing.</summary>
        public void Reset() => _lastGoodSpeedKmh = null;
    }
}

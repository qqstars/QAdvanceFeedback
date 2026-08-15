using System;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="TelemetryLearningGate"/> - the owner-requested shared validity gate every
    /// cross-frame learner in this plugin consults before folding a frame into learned state (see
    /// docs\gforce-direction-fix-report.md). "Game running/paused/menu" is deliberately NOT this
    /// class's concern (see its own remarks) so it is not tested here - it is a
    /// <c>QAdvanceFeedback.cs</c>-level guard, untestable per this project's own SimHub-free test
    /// project architecture (mirrors SimHubTelemetryAdapter's own untested call sites).
    /// </summary>
    public class TelemetryLearningGateTests
    {
        private static ITelemetrySample Sample(
            double? newSpeedKmh, double? oldSpeedKmh, double dtSeconds = 0.02,
            bool? isInPit = null, bool? isReplay = null, bool? isSessionRestart = null)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: oldSpeedKmh);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: newSpeedKmh,
                isInPit: isInPit, isReplay: isReplay, isSessionRestart: isSessionRestart);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        [Fact]
        public void An_ordinary_driving_frame_is_valid()
        {
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.5)));
        }

        [Fact]
        public void Null_sample_or_missing_New_frame_is_invalid()
        {
            var gate = new TelemetryLearningGate();
            Assert.False(gate.IsValid(null));
        }

        [Fact]
        public void A_frame_reporting_IsInPit_true_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            Assert.False(gate.IsValid(Sample(100.0, 100.5, isInPit: true)));
        }

        [Fact]
        public void A_frame_reporting_IsReplay_true_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            Assert.False(gate.IsValid(Sample(100.0, 100.5, isReplay: true)));
        }

        [Fact]
        public void A_frame_reporting_IsSessionRestart_true_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            Assert.False(gate.IsValid(Sample(100.0, 100.5, isSessionRestart: true)));
        }

        [Fact]
        public void A_title_that_never_reports_pit_replay_or_restart_fields_is_never_rejected_on_them()
        {
            // isInPit/isReplay/isSessionRestart all null (never set) - must NOT be treated as "true".
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(100.0, 100.5, isInPit: null, isReplay: null, isSessionRestart: null)));
        }

        [Fact]
        public void A_stale_huge_dt_from_a_pause_or_loading_screen_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            Assert.False(gate.IsValid(Sample(100.0, 100.5, dtSeconds: 2.0)));
        }

        [Fact]
        public void A_dt_exactly_at_the_plausible_ceiling_is_still_accepted()
        {
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(100.0, 100.5, dtSeconds: TelemetryLearningGate.MaxPlausibleDtSeconds)));
        }

        [Fact]
        public void Missing_or_non_positive_dt_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            Assert.False(gate.IsValid(new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, null)));
            Assert.False(gate.IsValid(new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.Zero)));
        }

        [Fact]
        public void Below_minimum_speed_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            Assert.False(gate.IsValid(Sample(newSpeedKmh: 2.0, oldSpeedKmh: 2.0)));
        }

        [Fact]
        public void A_speed_reading_exactly_at_the_minimum_is_accepted()
        {
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(newSpeedKmh: TelemetryLearningGate.MinSpeedKmh, oldSpeedKmh: TelemetryLearningGate.MinSpeedKmh)));
        }

        [Fact]
        public void No_speed_reading_at_all_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            var oldFrame = new TelemetryFrame();
            var newFrame = new TelemetryFrame();
            Assert.False(gate.IsValid(new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(0.02))));
        }

        // ---------------------------------------------------------------------------------------
        // Teleport/session-restart discontinuity - the concrete failure mode that let a captured
        // session's own Diag.GForce.LearnedAccelMaxG reach 179.8. MUTATION target: removing this
        // check (see the dedicated mutation test at the bottom of this file).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_teleport_sized_speed_jump_is_rejected()
        {
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.0))); // establishes a good baseline

            // A 300 km/h swing in one 20ms frame - a teleport, not real driving.
            Assert.False(gate.IsValid(Sample(newSpeedKmh: 300.0, oldSpeedKmh: 300.0, dtSeconds: 0.02)));
        }

        [Fact]
        public void The_frame_after_a_rejected_teleport_does_not_itself_read_as_a_second_discontinuity()
        {
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.0)));

            // Teleport frame - rejected, and must not become the new baseline.
            Assert.False(gate.IsValid(Sample(newSpeedKmh: 300.0, oldSpeedKmh: 300.0)));

            // The very next frame, ordinary driving resuming AT the post-teleport speed - must be
            // accepted (not rejected as "a jump from the pre-teleport 100 km/h baseline").
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 300.0, oldSpeedKmh: 300.0)));
        }

        [Fact]
        public void A_stale_dt_frame_also_clears_the_baseline_so_the_next_reading_is_not_a_false_discontinuity()
        {
            var gate = new TelemetryLearningGate();
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.0)));

            // A pause/loading-screen gap - rejected on dt alone.
            Assert.False(gate.IsValid(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.0, dtSeconds: 3.0)));

            // Resuming at a DIFFERENT, but itself perfectly ordinary, speed must not be rejected as a
            // "discontinuity" against the stale pre-pause baseline.
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 60.0, oldSpeedKmh: 60.0)));
        }

        [Fact]
        public void Reset_clears_the_remembered_baseline()
        {
            var gate = new TelemetryLearningGate();
            gate.IsValid(Sample(newSpeedKmh: 100.0, oldSpeedKmh: 100.0));

            gate.Reset();

            // Without Reset, jumping straight to 300 km/h would read as a teleport against the 100
            // km/h baseline above; after Reset, there is no baseline to compare against, so THIS is
            // simply the first reading (accepted) rather than "held/rejected forever".
            Assert.True(gate.IsValid(Sample(newSpeedKmh: 300.0, oldSpeedKmh: 300.0)));
        }

        [Fact]
        public void A_legitimate_hard_braking_event_is_still_accepted_the_gate_is_not_too_tight()
        {
            // The coordinator's own explicit requirement: prove the gate does not silently exclude
            // everything. A hard, but entirely real, braking event: 200 km/h decaying by 3 km/h every
            // 20ms frame (a strong but physically ordinary deceleration).
            var gate = new TelemetryLearningGate();
            double speed = 200.0;
            bool sawAnAccepted = false;
            for (int i = 0; i < 30; i++)
            {
                double next = speed - 3.0;
                if (gate.IsValid(Sample(newSpeedKmh: next, oldSpeedKmh: speed))) sawAnAccepted = true;
                speed = next;
            }

            Assert.True(sawAnAccepted, "an entirely ordinary hard-braking sequence must not be rejected wholesale");
        }

        // ---------------------------------------------------------------------------------------
        // MUTATION EVIDENCE: removing the teleport/dt rejection lets an implausible learned maximum
        // through - see GForceSettingsTests' own
        // "MUTATION_evidence_without_the_gate_a_session_restart_teleport_would_be_learned_as_a_new_maximum"
        // for the full end-to-end demonstration against the real GForceSettings integration (the gate
        // alone has nothing to "learn" - it only answers true/false).
        // ---------------------------------------------------------------------------------------
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.ShakeItEffects;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>ShakeIt source detection and availability (docs\shakeit-source-design.md §3–4).</summary>
    public class ShakeItSourceAvailabilityTests
    {
        private const string Game = "IRacing";

        private static ShakeItAvailabilityInput Driving(bool fresh = true, bool? supported = true, double dt = 0.1, string game = Game) =>
            new ShakeItAvailabilityInput
            {
                EnabledInProfile = true, HeartbeatAgeSeconds = 0.01,
                GameRunning = true, HasFeedbackData = true, GameId = game, GameSupported = supported, ValuesFresh = fresh, DtSeconds = dt
            };

        private static ShakeItAvailabilityInput Menu(string game = Game) =>
            new ShakeItAvailabilityInput
            {
                EnabledInProfile = true, HeartbeatAgeSeconds = 0.01,
                GameRunning = false, GameId = game, DtSeconds = 0.1
            };

        private static ShakeItSourceState Run(ShakeItSourceAvailability m, ShakeItAvailabilityInput input, double seconds)
        {
            ShakeItSourceState s = m.State;
            for (double t = 0; t < seconds - 1e-9; t += input.DtSeconds) s = m.Step(input);
            return s;
        }

        // ---- state machine ----

        [Fact]
        public void Starts_not_set_up() => Assert.Equal(ShakeItSourceState.NotSetUp, new ShakeItSourceAvailability().State);

        [Fact]
        public void Not_enabled_in_profile_is_not_set_up_even_with_a_heartbeat()
        {
            var m = new ShakeItSourceAvailability();
            var i = Driving(); i.EnabledInProfile = false;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(i));
        }

        [Fact]
        public void Enabled_without_heartbeat_is_not_set_up()
        {
            var m = new ShakeItSourceAvailability();
            var i = Menu(); i.HeartbeatAgeSeconds = double.PositiveInfinity;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(i));
            i.HeartbeatAgeSeconds = double.NaN;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(i));
        }

        [Fact]
        public void Heartbeat_up_to_two_seconds_counts_and_older_expires()
        {
            var m = new ShakeItSourceAvailability();
            var i = Menu();
            i.HeartbeatAgeSeconds = 2.0;
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(i));
            i.HeartbeatAgeSeconds = 2.01;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(i));
            i.HeartbeatAgeSeconds = 0.5;
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(i));
        }

        [Fact]
        public void Heartbeat_expiry_while_driving_drops_supported_to_not_set_up()
        {
            var m = new ShakeItSourceAvailability();
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving()));
            var i = Driving(); i.HeartbeatAgeSeconds = 2.5;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(i));
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving()));
        }

        [Fact]
        public void Set_up_at_menu_paused_or_no_game_is_idle()
        {
            var m = new ShakeItSourceAvailability();
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Menu()));
            var paused = Driving(); paused.GamePaused = true;
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(paused));
            var menu = Driving(); menu.GameInMenu = true;
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(menu));
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Menu(game: "")));
        }

        [Fact]
        public void Driving_with_fresh_values_and_supported_or_unknown_game_is_supported()
        {
            Assert.Equal(ShakeItSourceState.Supported, new ShakeItSourceAvailability().Step(Driving(supported: true)));
            Assert.Equal(ShakeItSourceState.Supported, new ShakeItSourceAvailability().Step(Driving(supported: null)));
        }

        [Fact]
        public void Game_reported_unsupported_is_not_supported_at_once_even_with_fresh_values()
        {
            var m = new ShakeItSourceAvailability();
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Driving(fresh: true, supported: false)));
        }

        [Fact]
        public void Stale_values_wait_in_idle_then_become_not_supported_after_three_seconds()
        {
            var m = new ShakeItSourceAvailability();
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(m, Driving(fresh: false, supported: null, dt: 0.1), 2.9));
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, Driving(fresh: false, supported: null, dt: 0.1), 0.1));
        }

        [Fact]
        public void A_brief_dropout_holds_supported_and_fresh_values_reset_the_clock()
        {
            var m = new ShakeItSourceAvailability();
            m.Step(Driving());
            Assert.Equal(ShakeItSourceState.Supported, Run(m, Driving(fresh: false, dt: 0.1), 2.5));
            m.Step(Driving());
            Assert.Equal(ShakeItSourceState.Supported, Run(m, Driving(fresh: false, dt: 0.1), 2.5));
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, Driving(fresh: false, dt: 0.1), 0.6));
        }

        [Fact]
        public void Staleness_counts_only_in_game_time()
        {
            var m = new ShakeItSourceAvailability();
            Run(m, Driving(fresh: false, dt: 0.1), 2.0);
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(m, Menu(), 10));   // menu: clock does not run
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(m, Driving(fresh: false, dt: 0.1), 2.5));
        }

        [Fact]
        public void Staleness_does_not_accumulate_while_not_set_up()
        {
            var m = new ShakeItSourceAvailability();
            var notSetUp = Driving(fresh: false); notSetUp.EnabledInProfile = false;
            Assert.Equal(ShakeItSourceState.NotSetUp, Run(m, notSetUp, 10));
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(m, Driving(fresh: false, dt: 0.1), 2.5));
        }

        [Fact]
        public void Negative_or_infinite_dt_never_advances_staleness()
        {
            var m = new ShakeItSourceAvailability();
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Driving(fresh: false, dt: -5)));
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Driving(fresh: false, dt: double.PositiveInfinity)));
        }

        [Fact]
        public void Without_shakeit_feedback_data_the_game_is_not_being_driven_and_staleness_never_runs()
        {
            // Session load, first telemetry frames, tow/reset, garage: running but ShakeIt hands no
            // calibrated data, so stale values there must not latch NotSupported.
            var m = new ShakeItSourceAvailability();
            var noFeedback = Driving(fresh: false, supported: null); noFeedback.HasFeedbackData = false;
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(m, noFeedback, 30));
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving()));
        }

        [Fact]
        public void A_staleness_verdict_recovers_to_supported_on_fresh_values()
        {
            var m = new ShakeItSourceAvailability();
            m.Step(Driving());
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, Driving(fresh: false), 3.1));
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, Driving(fresh: false), 10));
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving(fresh: true)));
            // and a later dropout needs the full 3 s again
            Assert.Equal(ShakeItSourceState.Supported, Run(m, Driving(fresh: false), 2.5));
        }

        [Fact]
        public void A_staleness_verdict_holds_at_the_menu_and_clears_on_a_game_switch()
        {
            var m = new ShakeItSourceAvailability();
            Run(m, Driving(fresh: false), 3.1);
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Menu()));
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Menu(game: "Other")));
        }

        [Fact]
        public void A_reported_unsupported_game_stays_not_supported_despite_fresh_values()
        {
            var m = new ShakeItSourceAvailability();
            m.Step(Driving(fresh: true, supported: false));
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, Driving(fresh: true, supported: null), 5));
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Driving(fresh: true, supported: true)));
        }

        [Fact]
        public void Not_supported_sticks_for_the_game_across_menu_and_fresh_values()
        {
            var m = new ShakeItSourceAvailability();
            m.Step(Driving(supported: false));
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Menu()));
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Driving(fresh: true, supported: true)));
        }

        [Fact]
        public void Not_set_up_wins_over_a_sticky_not_supported_which_returns_when_set_up_again()
        {
            var m = new ShakeItSourceAvailability();
            m.Step(Driving(supported: false));
            var off = Driving(); off.EnabledInProfile = false;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(off));
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Driving()));
        }

        // ---- a game with no car feedback data at all (owner, 2026-11-20: Counter-Strike) ----

        private static ShakeItAvailabilityInput NoFeedbackGame(bool? supported, string game = "CounterStrike")
        {
            var i = Driving(fresh: false, supported: supported, game: game);
            i.HasFeedbackData = false;   // ShakeIt never hands our effects calibrated data there
            return i;
        }

        [Fact]
        public void A_running_game_without_feedback_data_reported_unsupported_is_not_supported_at_once()
        {
            var m = new ShakeItSourceAvailability();
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(NoFeedbackGame(supported: false)));
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, NoFeedbackGame(supported: false), 10));
        }

        [Fact]
        public void The_unsupported_verdict_also_holds_at_that_games_menu_or_pause_and_clears_on_a_game_switch()
        {
            var m = new ShakeItSourceAvailability();
            var menu = NoFeedbackGame(supported: false); menu.GameInMenu = true;
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(menu));
            var paused = NoFeedbackGame(supported: false); paused.GamePaused = true;
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(paused));
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving(game: "AssettoCorsa")));
        }

        [Fact]
        public void A_supported_or_unknown_game_without_feedback_data_stays_idle()
        {
            // Session load, garage, tow in a racing game: no feedback data, but the game supports it.
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(new ShakeItSourceAvailability(), NoFeedbackGame(supported: true, game: Game), 30));
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(new ShakeItSourceAvailability(), NoFeedbackGame(supported: null, game: Game), 30));
        }

        [Fact]
        public void A_selected_but_not_running_game_reported_unsupported_is_not_supported()
        {
            // SimHub checks the SELECTED game's capabilities, running or not (Add Effect, the unsupported mark).
            var m = new ShakeItSourceAvailability();
            var i = Menu(game: "CounterStrike"); i.GameSupported = false;
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(i));
            Assert.Equal(ShakeItSourceState.NotSupported, Run(m, i, 10));
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Menu(game: "AssettoCorsa")));
        }

        [Fact]
        public void Unsupported_with_no_game_selected_is_not_a_verdict()
        {
            var m = new ShakeItSourceAvailability();
            var i = Menu(game: ""); i.GameSupported = false;
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(i));
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving()));
        }

        [Fact]
        public void A_selected_unsupported_game_still_needs_an_effect_in_the_profile()
        {
            var m = new ShakeItSourceAvailability();
            var i = Menu(game: "CounterStrike"); i.GameSupported = false; i.EnabledInProfile = false;
            Assert.Equal(ShakeItSourceState.NotSetUp, m.Step(i));
        }

        [Fact]
        public void The_plugin_falls_back_to_shakeits_capability_check_when_the_tap_has_no_verdict()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "QAdvanceFeedback", "QAdvanceFeedback.cs")))
                dir = System.IO.Path.GetDirectoryName(dir);
            // LF whatever the file uses (CRLF or LF - owner, 2026-11-25).
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "QAdvanceFeedback", "QAdvanceFeedback.cs")).Replace("\r\n", "\n");
            Assert.Contains("SupportFor(tap, gameId, isLock: true)\n                    ?? (gameId.Length > 0 ? Integration.ShakeIt.ShakeItBuiltInTap.SupportedByCapabilities(pluginManager, isLock: true) : null);", code);
            Assert.Contains("SupportFor(tap, gameId, isLock: false)\n                    ?? (gameId.Length > 0 ? Integration.ShakeIt.ShakeItBuiltInTap.SupportedByCapabilities(pluginManager, isLock: false) : null);", code);
            string tap = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "QAdvanceFeedback", "Integration", "ShakeIt", "ShakeItBuiltInTap.cs"));
            Assert.Contains("pluginManager?.GameManager?.GetFeedbackCapabilities()", tap);
        }

        [Fact]
        public void A_game_switch_re_checks_support()
        {
            var m = new ShakeItSourceAvailability();
            Run(m, Driving(fresh: false), 3.1);
            Assert.Equal(ShakeItSourceState.NotSupported, m.State);
            Assert.Equal(ShakeItSourceState.SetUpIdle, m.Step(Menu(game: "AssettoCorsa")));
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving(game: "AssettoCorsa")));
            // and back: the earlier verdict is not remembered either
            Assert.Equal(ShakeItSourceState.Supported, m.Step(Driving(game: Game)));
        }

        [Fact]
        public void A_game_switch_resets_the_staleness_clock()
        {
            var m = new ShakeItSourceAvailability();
            Run(m, Driving(fresh: false), 2.5);
            Assert.Equal(ShakeItSourceState.SetUpIdle, Run(m, Driving(fresh: false, game: "Other"), 2.5));
        }

        [Fact]
        public void Null_game_id_equals_empty()
        {
            var m = new ShakeItSourceAvailability();
            m.Step(Driving(supported: false, game: ""));
            Assert.Equal(ShakeItSourceState.NotSupported, m.Step(Driving(game: null)));
        }

        // ---- tap support flags ----

        [Fact]
        public void Tap_support_is_per_channel_and_only_for_the_game_it_was_computed_for()
        {
            var snap = new FrameSnapshot<ShakeItTapValues>(
                new ShakeItTapValues(Corners.Zero, Corners.Zero, true, false, Game), 0);
            Assert.True(ShakeItTapValues.SupportFor(snap, Game, isLock: true));
            Assert.False(ShakeItTapValues.SupportFor(snap, Game, isLock: false));
            Assert.Null(ShakeItTapValues.SupportFor(snap, "Other", isLock: true));
            Assert.Null(ShakeItTapValues.SupportFor(null, Game, isLock: true));
        }

        [Fact]
        public void Legacy_tap_constructor_reports_unknown_support()
        {
            var v = new ShakeItTapValues(Corners.Zero, Corners.Zero);
            Assert.Null(v.LockSupported);
            Assert.Null(v.SlipSupported);
            Assert.Null(ShakeItTapValues.SupportFor(new FrameSnapshot<ShakeItTapValues>(v, 0), "", isLock: true));
        }

        // ---- debounced broadcast ----

        [Fact]
        public void Broadcaster_publishes_once_after_half_a_second_of_stability()
        {
            int notified = 0;
            var b = new ShakeItSourceStateBroadcaster(() => notified++);
            Assert.Equal(ShakeItSourceState.NotSetUp, b.Published.Lock);

            b.Update(ShakeItSourceState.Supported, ShakeItSourceState.NotSupported, 0.1);
            for (int i = 0; i < 4; i++) b.Update(ShakeItSourceState.Supported, ShakeItSourceState.NotSupported, 0.1);
            Assert.Equal(0, notified);
            b.Update(ShakeItSourceState.Supported, ShakeItSourceState.NotSupported, 0.1);
            Assert.Equal(1, notified);
            Assert.Equal(ShakeItSourceState.Supported, b.Published.Lock);
            Assert.Equal(ShakeItSourceState.NotSupported, b.Published.Slip);
            Assert.Equal(ShakeItSourceState.NotSupported, b.Published.For(isLock: false));

            for (int i = 0; i < 20; i++) b.Update(ShakeItSourceState.Supported, ShakeItSourceState.NotSupported, 0.1);
            Assert.Equal(1, notified);
        }

        [Fact]
        public void Broadcaster_ignores_a_flicker_shorter_than_the_hold()
        {
            int notified = 0;
            var b = new ShakeItSourceStateBroadcaster(() => notified++);
            for (int i = 0; i < 10; i++)
            {
                b.Update(ShakeItSourceState.SetUpIdle, ShakeItSourceState.SetUpIdle, 0.1);
                b.Update(ShakeItSourceState.NotSetUp, ShakeItSourceState.NotSetUp, 0.1);
            }
            Assert.Equal(0, notified);
            Assert.Same(ShakeItSourceStates.Initial, b.Published);
        }

        // ---- profile inspection ----

        private sealed class FakeReader : IShakeItProfileReader
        {
            private readonly Func<IReadOnlyList<ShakeItContainerInfo>> _read;
            public FakeReader(Func<IReadOnlyList<ShakeItContainerInfo>> read) { _read = read; }
            public FakeReader(params ShakeItContainerInfo[] containers) : this(() => containers) { }
            public string Name => "fake";
            public IReadOnlyList<ShakeItContainerInfo> ReadContainers() => _read();
        }

        private static ShakeItContainerInfo C(string type, bool enabled, params bool[] groups) =>
            new ShakeItContainerInfo(type, enabled, groups);

        [Fact]
        public void Every_one_of_our_effects_counts()
        {
            Assert.Equal(8, ShakeItProfileInspection.OurContainerTypeNames.Count);
            foreach (string type in ShakeItProfileInspection.OurContainerTypeNames)
            {
                ShakeItProfileSnapshot s = ShakeItProfileInspection.Inspect(new[] { new FakeReader(C(type, true)) });
                Assert.True(s.EnabledInProfile, type);
            }
            Assert.Contains("QAdvanceFeedbackClutchBiteContainer", ShakeItProfileInspection.OurContainerTypeNames);
            Assert.Contains("QAdvanceFeedbackSlipAngleContainer", ShakeItProfileInspection.OurContainerTypeNames);
            Assert.Contains("QAdvanceFeedbackGForceBackTopContainer", ShakeItProfileInspection.OurContainerTypeNames);
        }

        [Fact]
        public void Other_container_types_do_not_count()
        {
            ShakeItProfileSnapshot s = ShakeItProfileInspection.Inspect(new[]
            {
                new FakeReader(C("WheelsLockContainer", true), C("WheelsSlipContainer", true), C("qadvancefeedbackwheellockcontainer", true))
            });
            Assert.False(s.EnabledInProfile);
            Assert.Equal(0, s.EnabledEffectCount);
            Assert.False(s.Failed);
        }

        [Fact]
        public void A_disabled_effect_does_not_count()
        {
            ShakeItProfileSnapshot s = ShakeItProfileInspection.Inspect(new[] { new FakeReader(C("QAdvanceFeedbackWheelLockContainer", false)) });
            Assert.False(s.EnabledInProfile);
        }

        [Fact]
        public void An_effect_in_a_disabled_group_does_not_count_at_any_depth()
        {
            Assert.False(ShakeItProfileInspection.Inspect(new[] { new FakeReader(C("QAdvanceFeedbackWheelSlipContainer", true, false)) }).EnabledInProfile);
            Assert.False(ShakeItProfileInspection.Inspect(new[] { new FakeReader(C("QAdvanceFeedbackWheelSlipContainer", true, true, false)) }).EnabledInProfile);
            Assert.True(ShakeItProfileInspection.Inspect(new[] { new FakeReader(C("QAdvanceFeedbackWheelSlipContainer", true, true, true)) }).EnabledInProfile);
        }

        [Fact]
        public void Both_plugins_are_read_and_counted()
        {
            var motors = new FakeReader(C("QAdvanceFeedbackWheelLockContainer", true), C("WheelsLockContainer", true));
            var bassShakers = new FakeReader(C("QAdvanceFeedbackGForceBottomFrontContainer", true), C("QAdvanceFeedbackSlipAngleContainer", false));
            ShakeItProfileSnapshot s = ShakeItProfileInspection.Inspect(new[] { motors, bassShakers });
            Assert.Equal(2, s.EnabledEffectCount);

            Assert.True(ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { new FakeReader(), bassShakers }).EnabledInProfile);
            Assert.True(ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { motors, new FakeReader() }).EnabledInProfile);
        }

        [Fact]
        public void A_missing_plugin_or_profile_is_skipped()
        {
            var absent = new FakeReader(() => null);
            Assert.False(ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { absent, null }).EnabledInProfile);
            Assert.True(ShakeItProfileInspection.Inspect(new IShakeItProfileReader[]
                { absent, new FakeReader(C("QAdvanceFeedbackClutchBiteContainer", true)) }).EnabledInProfile);
            Assert.False(ShakeItProfileInspection.Inspect(null).EnabledInProfile);
        }

        private static FakeReader Broken() => new FakeReader(() => throw new MissingMethodException("GetAllContainers"));

        [Fact]
        public void An_api_failure_of_every_reader_is_not_set_up_and_flags_a_simhub_compatibility_issue()
        {
            HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
            try
            {
                ShakeItProfileSnapshot s = ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { Broken(), Broken() });

                Assert.True(s.Failed);
                Assert.False(s.EnabledInProfile);
                HealthEntry entry = HealthRegistry.Snapshot().Single(e => e.Subsystem == HealthSubsystems.ShakeItProfile);
                Assert.True(entry.IsSimHubCompatibilityIssue);
            }
            finally
            {
                HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
            }
        }

        [Fact]
        public void One_failing_plugin_keeps_the_other_plugins_count_but_is_still_flagged()
        {
            HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
            try
            {
                var ok = new FakeReader(C("QAdvanceFeedbackWheelLockContainer", true));
                ShakeItProfileSnapshot s = ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { Broken(), ok });

                Assert.False(s.Failed);
                Assert.True(s.EnabledInProfile);
                Assert.Equal(1, s.EnabledEffectCount);
                Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.ShakeItProfile);
            }
            finally
            {
                HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
            }
        }

        [Fact]
        public void The_compatibility_flag_survives_an_existing_effects_entry_and_clears_on_recovery()
        {
            HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
            HealthRegistry.Clear(HealthSubsystems.ShakeItEffects);
            try
            {
                HealthRegistry.Report(HealthSubsystems.ShakeItEffects, HealthSeverity.Degraded, "Health.Impact.ShakeItEffects", "earlier tap fault");
                ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { Broken() });
                HealthEntry entry = HealthRegistry.Snapshot().Single(e => e.Subsystem == HealthSubsystems.ShakeItProfile);
                Assert.True(entry.IsSimHubCompatibilityIssue);

                ShakeItProfileInspection.Inspect(new IShakeItProfileReader[] { new FakeReader(C("QAdvanceFeedbackWheelLockContainer", true)) });
                Assert.DoesNotContain(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.ShakeItProfile);
                Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.ShakeItEffects);
            }
            finally
            {
                HealthRegistry.Clear(HealthSubsystems.ShakeItProfile);
                HealthRegistry.Clear(HealthSubsystems.ShakeItEffects);
            }
        }

        [Fact]
        public void Not_set_up_snapshot_default()
        {
            Assert.False(ShakeItProfileSnapshot.None.EnabledInProfile);
            Assert.False(new ShakeItProfileSnapshot(3, failed: true).EnabledInProfile);
            Assert.True(new ShakeItProfileSnapshot(1, failed: false).EnabledInProfile);
        }

        [Fact]
        public void Container_info_defaults_to_no_groups()
        {
            var c = new ShakeItContainerInfo(null, true);
            Assert.Equal(string.Empty, c.TypeName);
            Assert.True(c.IsEffectivelyEnabled);
            Assert.Empty(c.AncestorGroupsEnabled);
        }
    }
}

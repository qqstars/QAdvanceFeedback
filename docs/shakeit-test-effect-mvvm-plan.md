# ShakeIt Test Effects + MVVM groundwork — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Test Effects inside the native QAdvanceFeedback ShakeIt effects make the real device vibrate with no game running:
- G-Force: a G-ball test.
- WheelLock/WheelSlip: four per-wheel source sliders, with balls on the graph.

Every test plays the published `GForce.*` / `Projected.*` values and auto-stops after 10 s idle. ShakeIt's own Test link is removed. All touched UI is rebuilt on a new MVVM foundation.

**Architecture:**
- WPF-free, SimHub-free view models hold all state and rules; they are unit-tested in the net8 test project.
- XAML views only bind to them. Custom controls own their own drawing and drag input, exposed through dependency properties.
- Tests publish through the plugin's existing publish path, so the native effects (which read only `LatestProjected` / `LatestGForce`) play exactly the published property values.

**Tech Stack:** C# / .NET Framework 4.8 WPF (plugin, single DLL), MahApps.Metro 1.5 (SimHub's), xunit on net8.0 (link-compiled SimHub-free sources).

**Spec:** `docs\shakeit-test-effect-mvvm-design.md` (approved 2026-11-17). Background: `docs\shakeit-native-effects-design.md`.

## Global Constraints

- **Output:** `bin\Release\net48` holds only `QAdvanceFeedback.dll` + `.pdb`. No new NuGet runtime packages.
- **Compatibility:** the 62 product property names and the Custom Effect binding pattern must not change.
- **MVVM rule:**
  - View models implement `INotifyPropertyChanged`. Views bind (one-way / two-way / commands).
  - Code-behind only constructs view models and sets `DataContext`.
  - No event handler may set another control's property.
  - Allowed view code: a custom control's own rendering and input, and attached layout behaviours.
- **Testable code:** view models and rule classes reference neither WPF (`System.Windows.*`) nor SimHub types, so they compile into `QAdvanceFeedback.Tests` (net8.0, no WPF). Add each new SimHub-free file to the test csproj with an explicit `<Compile Include>`, matching that file's existing convention.
- **Tests never persist:** they never call `MarkDirty`, never save settings, never feed learners.
- **Auto-off:** 10 s with no test input switches the test off. The toggle is bound to the view model, so it shows off.
- **Ball colours:**
  - Per wheel: FL bright red, FR dark red, RL bright blue, RR dark blue.
  - Front/Rear: standard red and standard blue.
  - Left/Right: bright grey and dark grey.
  - One channel: orange.
  - Radial gradient fill.
- **Ball position:** y is the pulse-free projected value.
- **Strings:** every new string exists in both `StringTableEn.cs` and `StringTableZhHans.cs`.
- **Docs:** directly under `docs\`. Screenshots in both languages (`docs\images\shakeit-*.png`) and both READMEs updated.
- **Workflow:** no git commands. "Commit" steps below mean: run the full suite and record the result in the task's report.

## Review Focus

1. **Leaving the ShakeIt panel while a test runs:** the edit view unloads, so the test must stop and hand the outputs back (`PublishTestEffect(null)` / `PublishLockSlipTest(null)`). A graph rebuilt per request must never stop it.
2. **A live game frame arriving while the Lock/Slip test runs:** `DataUpdate` must not overwrite `Projected.*` / `LatestProjected` (it still computes and learns from real telemetry). After the test is off, the next live frame publishes normally.
3. **Settings changed during a test** (routing, pattern, key data, projector, pulse): the next tick uses the new values, and the balls follow the new routing.
4. **Two tests at once** (page G-Force test, then a ShakeIt Lock test): starting one stops the other, and the other's toggle shows off.
5. **Threading:** hub `Changed` events and ShakeIt calls can arrive off the UI thread. View-model state changes go through `IUiDispatcher`.

---

### Task 1: MVVM foundation + the rule in the architecture doc

**Files:**
- Create: `QAdvanceFeedback\Ui\Mvvm\ObservableObject.cs`, `RelayCommand.cs`, `IUiDispatcher.cs`, `ITicker.cs`
- Create (test helpers): `QAdvanceFeedback.Tests\Ui\ManualTicker.cs`, `ImmediateDispatcher.cs`
- Test: `QAdvanceFeedback.Tests\Ui\MvvmFoundationTests.cs`
- Modify: `QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj` (explicit Includes for `Ui\Mvvm\*.cs`)
- Modify: `docs\architecture.md` (new "UI: MVVM (standing rule)" section)

**Interfaces — Produces:**
- `abstract class ObservableObject : INotifyPropertyChanged`:
  - `protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)`
  - `protected void OnPropertyChanged(string name)`
- `sealed class RelayCommand : ICommand`:
  - constructor `RelayCommand(Action execute, Func<bool> canExecute = null)`
  - `void RaiseCanExecuteChanged()`
- `interface IUiDispatcher { void Post(Action action); }`. The WPF implementation is `WpfUiDispatcher` (plugin-only, in `Ui\Views\`), created in Task 7.
- `interface ITicker`:
  - `IDisposable Every(TimeSpan interval, Action tick)`
  - `DateTime UtcNow { get; }`
  - The WPF implementation is `DispatcherTicker` (plugin-only), Task 7.
- Test helpers:
  - `ManualTicker : ITicker` with `Advance(TimeSpan)`, which fires due ticks in order and moves `UtcNow`.
  - `ImmediateDispatcher : IUiDispatcher`, which runs inline.

- [ ] **Step 1:** Write the failing tests:
  - `SetProperty_raises_PropertyChanged_once_and_only_on_change`
  - `RelayCommand_executes_and_reports_CanExecute`
  - `ManualTicker_fires_every_interval_and_stops_when_disposed`
- [ ] **Step 2:** Run `dotnet test --filter MvvmFoundationTests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the four files.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Add the architecture section: the rule text from the spec's "MVVM rule", plus the folder layout `Ui\Mvvm`, `Ui\ViewModels` (SimHub/WPF-free) and `Ui\Views` (XAML + custom controls).
- [ ] **Step 6:** Run the full suite. Expected: all pass.

### Task 2: Shared normalization + Lock/Slip test calculation + balls

**Files:**
- Create: `QAdvanceFeedback\Settings\KeyDataNormalization.cs`, `QAdvanceFeedback\Settings\LockSlipTestEngine.cs`, `QAdvanceFeedback\Settings\TestBalls.cs`
- Modify: `QAdvanceFeedback\Settings\SourceToProjectedGraph.cs` (uses `KeyDataNormalization`)
- Test: `QAdvanceFeedback.Tests\ShakeItEffects\LockSlipTestEngineTests.cs`
- Modify: test csproj Includes

**Interfaces:**
- **Consumes:** `KeyDataView` (`KeyDataPanelModel.Resolve`), `Aggregator.Compute(Corners, AggregationWeights) → WheelAggregate`, `ProjectedWheelLockSlipEngine(lockProjector, lockPulse, slipProjector, slipPulse)`, and its `.Compute(NormalizedWheelLockSlipResult, dt, lockConfidence = 1, slipConfidence = 1, lockFloor, slipFloor)`.
- **Produces:**
  - `static class KeyDataNormalization`:
    - `double Normalize(double source0100, bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75)` — exactly the per-sample math now inside `SourceToProjectedGraph.Points`, including its fallbacks.
    - `SourceToProjectedGraph.Points` calls it, with output unchanged.
  - `sealed class LockSlipTestEngine`:
    - constructor `LockSlipTestEngine(bool isLock, WheelChannelSettings channel)`, which builds its own `ProjectedWheelLockSlipEngine` from `channel.Projector` / `channel.Pulse` (the other channel gets shipped defaults and is fed zeros).
    - `ProjectedWheelLockSlipResult Step(Corners sources0100, KeyDataView keyData, double dtSeconds)`:
      - normalizes each wheel;
      - combines with `channel.ToAggregationWeights()`;
      - projects with confidence 1 and floor 1;
      - leaves the other channel at 0.
    - Brake/throttle triggers are treated as fully pressed: this path has no pedal gate.
  - `enum BallColour { BrightRed, DarkRed, BrightBlue, DarkBlue, Red, Blue, BrightGrey, DarkGrey, Orange }`
  - `readonly struct TestBall(EffectSlot Slot, double Source0100, double Projected0100, BallColour Colour)`
  - `static IReadOnlyList<TestBall> TestBalls.For(EffectRouting routing, bool isLock, Corners sources0100, AggregationWeights weights, ProjectedWheelLockSlipResult result)`:
    - x for a wheel = its slider; x for a group = `Aggregator.Compute(sources, weights)` for that group;
    - y = the `*WithoutPulse` value for that slot.

- [ ] **Step 1:** Write the failing tests (Lock, `ProjectorSettings.CreateShippedDefault`, pulse disabled unless stated):
  - `Normalize_matches_the_graph_samples` — `SourceToProjectedGraph.Points` y at x = 0, 5 … 100 equals `projector.Project(KeyDataNormalization.Normalize(x, …))` mapped to the canvas.
  - `Step_projects_each_wheel_through_the_shown_key_data` — sources (80, 60, 40, 0) with SMax/S90/S75 = 80/60/40 under Mapping:
    - `result.LockWheels.FrontLeft == projector.Project(80)`, `FrontRight == projector.Project(60)`, `RearLeft == projector.Project(30)`, `RearRight == 0`.
  - `Step_aggregates_groups_with_the_channel_weights` — `result.LockFrontWithoutPulse == projector.Project(Aggregator.Compute(normalizedWheels, weights).Front)`.
  - `Step_runs_the_pulse` — `Pulse.Enabled = true`, constant input. Over 1 s of 1/60 s steps, `LockAll` differs from `LockAllWithoutPulse` on at least one step.
  - `Step_leaves_the_other_channel_at_zero`.
  - `Step_ignores_pedals_and_never_touches_learners` — the engine has no telemetry input. Assert `channel.KeyDataPoints` serializes identically before and after 100 steps.
  - `Balls_follow_routing_and_colours`, one case per routing:
    - Corners → 4 balls, FL BrightRed, FR DarkRed, RL BrightBlue, RR DarkBlue;
    - FrontRear → Red, Blue;
    - LeftRight → BrightGrey, DarkGrey;
    - Mono → 1 Orange.
  - `Ball_y_is_pulse_free_and_group_x_is_aggregated_source`.
- [ ] **Step 2:** Run `dotnet test --filter LockSlipTestEngineTests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the three files and refactor `SourceToProjectedGraph` to use `KeyDataNormalization`.
- [ ] **Step 4:** Run `dotnet test --filter "LockSlipTestEngineTests|PanelReadoutSharingTests"`. Expected: PASS, including the existing graph tests.
- [ ] **Step 5:** Run the full suite. Expected: all pass.

### Task 3: Plugin test session, Lock/Slip publish path, out-of-game gate, Test link removed

**Files:**
- Create: `QAdvanceFeedback\Core\ShakeItEffects\TestEffectSession.cs`
- Modify: `Core\ShakeItEffects\GForceOutputGate.cs` → rename the class to `TestAwareOutputGate` (same `Allows` signature). Update its one caller.
- Modify: `QAdvanceFeedback\QAdvanceFeedback.cs`:
  - add `PublishLockSlipTest`, `TestLockSlipActive`, `TestSession`;
  - `DataUpdate` skips `UpdateProjected` / `LatestProjected` while the Lock/Slip test is active.
- Modify: `Integration\ShakeIt\QAdvanceFeedbackWheelChannelContainers.cs`:
  - `IsAllowedOutOfGame => true`;
  - `ProcessEffects` sets `_gateOpen = TestAwareOutputGate.Allows(plugin.TestLockSlipActive, …)`;
  - `ValueFor` returns 0 while closed.
- Modify: `Integration\ShakeIt\QAdvanceFeedbackEffect.cs` (`public override bool AllowTestMode => false;`)
- Test: `QAdvanceFeedback.Tests\ShakeItEffects\TestEffectSessionTests.cs`; update `ReviewFixTests` for the rename.

**Interfaces — Produces:**
- `enum TestKind { None, GForce, Lock, Slip }`
- `sealed class TestEffectSession`:
  - `TestKind Active { get; }`
  - `bool Begin(TestKind kind)` — starts it, stops any other, raises `Stopped(previousKind)`;
  - `void End(TestKind kind)` — a no-op unless that kind is active;
  - `event Action<TestKind> Stopped`.
  - Thread-safe (lock).
- Plugin:
  - `TestEffectSession TestSession { get; }`
  - `void PublishLockSlipTest(ProjectedWheelLockSlipResult result)`:
    - non-null: write `_publisher.UpdateProjected(result)` and `LatestProjected`;
    - null: release, then publish an all-zero result, in that order (the same order as `PublishTestEffect(null)`).
  - `bool TestLockSlipActive`
  - `PublishTestEffect` is unchanged in behaviour. `TestEffectActive` stays and means "the G-Force test publishes".
- The native containers keep reading only `LatestProjected` / `LatestGForce`. That is what makes "the test output is the published property values" true.

- [ ] **Step 1:** Write the failing tests:
  - `Begin_stops_the_other_kind_and_raises_Stopped`
  - `End_of_a_kind_that_is_not_active_is_a_noop`
  - `Gate_opens_for_test_or_live_game_only` (the renamed gate, same cases as the existing G-Force gate tests)
  - `Native_effect_values_equal_the_published_projected_values` — build a `ProjectedWheelLockSlipResult`, feed it to `PropertyPublisher.State`'s `UpdateProjected`. For every `EffectSlot`, `ChannelValues.Lock(result, slot)` equals the published `WheelLock.Projected.<slot>` value read back from that state. Same for Slip, and for G-Force via `UpdateGForce` / `ChannelValues.GForce`.
  - `DataUpdate_skips_projected_publish_while_lock_slip_test_active` — a source scan of `QAdvanceFeedback.cs`: the `UpdateProjected(projected)` call sits inside `if (!_testLockSlipActive)`.
- [ ] **Step 2:** Run `dotnet test --filter "TestEffectSessionTests|ReviewFixTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement the changes above.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Run `dotnet build QAdvanceFeedback -c Release`. Expected: 0 warnings. Then run the full suite. Expected: all pass.

### Task 4: G-Force test view model (shared by the page and ShakeIt)

**Files:**
- Create: `QAdvanceFeedback\Settings\GForceTestFrame.cs` — the pad/bar → telemetry → `GForceEngine` logic of `SettingsControl.PushSimulatedFrame`, moved verbatim (pad orientation, lateral sign, 80 % pedal, speed delta), plus the readout formatting (`Format`, `Pair`).
- Create: `QAdvanceFeedback\Ui\ViewModels\ITestEffectHost.cs`, `GForceTestViewModel.cs`
- Test: `QAdvanceFeedback.Tests\Ui\GForceTestViewModelTests.cs`

**Interfaces:**
- `interface ITestEffectHost`:
  - `TestEffectSession Session { get; }`
  - `void PublishGForceTest(GForceOutput outputOrNull)`
  - `void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull)`
  - The plugin implements it in Task 7 by forwarding to `PublishTestEffect` / `PublishLockSlipTest`.
- `static GForceTestFrame.Result GForceTestFrame.Compute(double padX, double padY, double wheel01, GForceSettings settings, GForceEngine engine, TimeSpan dt)`, where `Result` has `Output`, `LongitudinalG`, `LateralG`, `Accelerating`, `Wheel0100`, `LongMax`, `LatMax`.
- `sealed class GForceTestViewModel : ObservableObject`:
  - constructor `GForceTestViewModel(ITestEffectHost host, Func<GForceSettings> settings, ITicker ticker, IUiDispatcher ui)`
  - Two-way properties: `bool IsTestEnabled`; `double PadX`, `PadY` (−1..1); `double Wheel` (0..1); `double RateHz` (1..240, default 60).
  - Readout properties (strings): `LongLabel`, `LongText`, `LatText`, `WheelText`, `FrontText`, `RearText`, `LowText`, `TopText`.
  - `void Stop()`
  - `static readonly TimeSpan AutoOff = 10 s`
  - Behaviour:
    - On enable: `Session.Begin(GForce)`, start ticking at `RateHz`; each tick → `GForceTestFrame.Compute` → `host.PublishGForceTest(output)` → update the readouts.
    - Changing `PadX` / `PadY` / `Wheel` resets the idle timer.
    - 10 s idle, `Stop()` or `Session.Stopped(GForce)` → `IsTestEnabled = false`, stop ticking, `host.PublishGForceTest(null)`, `Session.End(GForce)`.

- [ ] **Step 1:** Write the failing tests (`ManualTicker`, `ImmediateDispatcher`, fake host):
  - `Enabling_publishes_frames_at_the_rate`
  - `Frame_matches_the_page_formula` — for pad (0, +1) and full brake, the output equals `GForceEngine.Compute` with the same inputs the old `PushSimulatedFrame` built.
  - `Idle_for_10s_turns_the_toggle_off_and_releases` — after `Advance(10 s)`: `IsTestEnabled == false`, `PropertyChanged("IsTestEnabled")` was raised, the last publish was null.
  - `Input_resets_the_idle_timer` — move `PadX` at 9 s; still on at 15 s; off at 19.1 s.
  - `Another_test_starting_turns_this_one_off`
  - `Never_writes_the_settings_object` — the provider's `GForceSettings` serializes the same before and after.
- [ ] **Step 2:** Run `dotnet test --filter GForceTestViewModelTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Run the full suite. Expected: all pass.

### Task 5: Lock/Slip test view model

**Files:**
- Create: `QAdvanceFeedback\Ui\ViewModels\LockSlipTestViewModel.cs`
- Test: `QAdvanceFeedback.Tests\Ui\LockSlipTestViewModelTests.cs`

**Interfaces:**
- Constructor: `LockSlipTestViewModel(bool isLock, ITestEffectHost host, Func<WheelChannelSettings> channel, Func<KeyDataView> keyData, Func<EffectRouting> routing, ITicker ticker, IUiDispatcher ui)`
- Two-way properties: `bool IsTestEnabled`; `double FrontLeft`, `FrontRight`, `RearLeft`, `RearRight` (0..100, default 0).
- `IReadOnlyList<TestBall> Balls` — empty while off; raises `PropertyChanged` each tick.
- `void Stop()`, `void RoutingChanged()` (recomputes `Balls`).
- Behaviour:
  - Ticks at 60 Hz.
  - Each tick creates a fresh `LockSlipTestEngine` when the channel's projector or pulse changed (compared by JSON). Otherwise it reuses the engine, so the pulse phase continues.
  - Each tick: `engine.Step(sources, keyData(), dt)` → `host.PublishLockSlipTest(result)` → `Balls = TestBalls.For(routing(), …)`.
  - Session kind `Lock` / `Slip`; idle and release rules identical to Task 4.

- [ ] **Step 1:** Write the failing tests:
  - `Enabling_publishes_the_slider_projection`
  - `Balls_track_sliders_and_routing`
  - `Idle_for_10s_turns_off_and_publishes_null`
  - `Slider_moves_reset_idle`
  - `Settings_change_mid_test_is_used_next_tick` — change the projector `ModerateOutput`, tick, and the published value follows.
  - `Stop_releases_and_clears_balls`
- [ ] **Step 2:** Run `dotnet test --filter LockSlipTestViewModelTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Run the full suite. Expected: all pass.

### Task 6: ShakeIt panel view models (port of today's panel behaviour)

**Files:**
- Create in `QAdvanceFeedback\Ui\ViewModels\`:
  - `IPanelHost.cs`
  - `KeyDataViewModel.cs`
  - `GForceAxisViewModel.cs`
  - `GForceEffectViewModel.cs`
  - `WheelChannelEffectViewModel.cs`
  - `IRoutingTarget.cs`
  - `GraphViewModel.cs`
- Test: `QAdvanceFeedback.Tests\Ui\PanelViewModelTests.cs`

**Interfaces:**
- `interface IPanelHost : ITestEffectHost` adds:
  - `QAdvanceFeedbackSettings Settings`, `SettingsHub Hub`, `string CurrentGameId`, `int KeyDataRevision`
  - `double? LearnedCeiling(bool isLock, string sourceIdentity)`
  - `IReadOnlyList<ShakeItSourceOption> SourceOptions(bool isLock)` — today's availability rule from `WheelChannelEffectPanel.RefreshSources`.
- `interface IRoutingTarget { EffectRouting Routing { get; set; } }` — the container implements it.
- `KeyDataViewModel(bool isLock, IPanelHost host, ITicker, IUiDispatcher)`:
  - `NormalizePattern Pattern` (two-way), `bool Auto` (two-way), `double? SMax`, `S90`, `S75` (two-way);
  - `bool ValuesEditable`, `bool ShowLowerAnchors`, `string Status`, `bool ShowStatus`;
  - `KeyDataView Current`.
  - Rules: today's `KeyDataSection`, unchanged:
    - commit through the hub after 700 ms of no typing;
    - a pending commit is flushed before a pattern/Auto change and on `Deactivate()`;
    - refresh every 1 s while Auto, or when `KeyDataRevision` changes;
    - never refresh over a pending edit.
- `GForceAxisViewModel`: `bool Auto` (two-way), `double Max` (two-way, rounded to 2), `string Readout` (`GForceReadout.ForAxis`).
- `GForceEffectViewModel(IPanelHost, ITicker, IUiDispatcher)`:
  - `Axes` (Accel / Decel / Lat), `LateralDirectionMode LateralDirection` (two-way), `bool Integrate` (two-way), `bool Available`;
  - `GForceTestViewModel Test`, whose settings provider is `() => host.Settings.GForce`;
  - readout refresh every 1 s;
  - `Activate()` / `Deactivate()` — subscribe / unsubscribe the hub; `Deactivate` stops the test.
- `GraphViewModel(bool isLock, IPanelHost host)`: `IReadOnlyList<(double X0100, double Y0100)> Curve` (from `SourceToProjectedGraph.Points` at 100×100), `IReadOnlyList<TestBall> Balls`, `void Refresh()`.
- `WheelChannelEffectViewModel(bool isLock, IRoutingTarget routing, IPanelHost host, ITicker, IUiDispatcher)`:
  - `IReadOnlyList<ShakeItSourceOption> Sources`, `SourceMode Source` (two-way), `EffectRouting Routing` (two-way, writes `IRoutingTarget`);
  - `KeyDataViewModel KeyData`, `GraphViewModel Graph`, `LockSlipTestViewModel Test`;
  - `Activate()` / `Deactivate()`. `Deactivate` stops the test and flushes key data.
  - `Graph.Balls` mirrors `Test.Balls`. The graph refreshes on the same hub fields `ProjectionCurvePlot` listens to today, and once a second under Auto.

- [ ] **Step 1:** Write the failing tests, porting each behaviour that exists today:
  - `KeyData_auto_makes_values_read_only_and_smax_only_hides_lower`
  - `KeyData_commits_after_700ms_through_hub_and_flushes_on_pattern_change`
  - `KeyData_refresh_never_overwrites_a_pending_edit`
  - `GForce_axis_editor_stays_editable_under_auto_and_readout_shows_default`
  - `GForce_lateral_and_integrate_write_through_hub`
  - `Hub_change_from_page_updates_viewmodel`
  - `Wheel_routing_change_writes_target_and_updates_balls`
  - `Source_options_follow_availability`
  - `Deactivate_stops_running_test`
- [ ] **Step 2:** Run `dotnet test --filter PanelViewModelTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Run the full suite. Expected: all pass.

### Task 7: ShakeIt views (XAML + custom controls), container ownership, old panels removed

**Files:**
- Create in `QAdvanceFeedback\Ui\Views\` (plugin-only):
  - `WpfUiDispatcher.cs`, `DispatcherTicker.cs`
  - `SourceProjectedGraph.cs` — custom control with DPs `Curve` and `Balls`. Draws the grid, the green dashed curve and the balls with radial gradients from `BallColour`.
  - `GBallPad.cs` — DPs `PadX` / `PadY`, two-way by default. Drag and deadzone snap as today; drawing ported from `BuildTestPad`.
  - `WheelBar.cs` — DP `Value`, two-way. Ported from `BuildTestBar`.
  - `TrackAlignment.cs` — attached properties:
    - `AlignTrackToColumn` (bool) — today's `AlignTrackToColumn`;
    - `IndentToTrackOf` (ToggleSwitch) — today's `KeepLeftMargin` use.
  - `LabeledRow` — a XAML style/template using `SharedSizeGroup`.
  - `WheelChannelEffectView.xaml`, `KeyDataView.xaml`, `LockSlipTestView.xaml` (toggle + 4 sliders), `GForceEffectView.xaml`, `GForceTestView.xaml`, `GraphView.xaml`.
- Modify: `QAdvanceFeedbackWheelChannelContainers.cs`:
  - implements `IRoutingTarget`;
  - owns a lazy, `[JsonIgnore]` `WheelChannelEffectViewModel`;
  - sets `_filter.Owner = this`;
  - `GetEditControl()` returns a `WheelChannelEffectView` with that `DataContext`. `Loaded` → `Activate()`, `Unloaded` → `Deactivate()`. This view's lifetime alone controls the test.
- Modify: `QAdvanceProjectionFilter.cs` — `[JsonIgnore] internal QAdvanceFeedbackWheelChannelContainerBase Owner`. `ResponseCurveEditor` returns a `GraphView` bound to `Owner?.ViewModel.Graph`, or a host-less `GraphViewModel` when there is no owner (curve only).
- Modify: `QAdvanceFeedbackGForceContainers.cs` — `GetEditControl()` returns `GForceEffectView` with an owned `GForceEffectViewModel` (same activation rule).
- Modify: `QAdvanceFeedback.cs` — implements `IPanelHost` by forwarding. Add `SourceOptions` from the existing panel logic.
- Delete: `WheelChannelEffectPanel.cs`, `KeyDataSection.cs`, `GForceEffectPanel.cs`, `ProjectionCurvePlot.cs`. Move the `LabeledRow` / `TrackAlignment` helpers out of `ShakeItEffectSupport.cs`.
- Strings: `ShakeItPanel.Test.Header`, `ShakeItPanel.Test.Note` ("Plays on your ShakeIt device; stops after 10 s without input."), `ShakeItPanel.Test.FL/FR/RL/RR`, in both tables.

- [ ] **Step 1:** Run `dotnet build QAdvanceFeedback -c Release`. Expected: 0 warnings, 0 errors, and `bin\Release\net48` holds only the DLL + pdb.
- [ ] **Step 2:** Add the source-scan test `ShakeIt_views_follow_MVVM` (`QAdvanceFeedback.Tests\Ui\MvvmRuleTests.cs`). In every `Ui\Views\*.xaml.cs` file, the only statements are `InitializeComponent`, `DataContext` assignment and `Loaded` / `Unloaded` → `Activate` / `Deactivate`. No `+=` handler bodies set another control's property; the custom controls in `Ui\Views\*.cs` are exempt. Run it. Expected: PASS.
- [ ] **Step 3:** Update `tools\screenshot-harness\ShakeItEffectCaptures.cs` to build the new views with a fake `IPanelHost`. Add a capture `shakeit-wheellock-test(.zh-Hans).png`: Lock, per-wheel routing, test on, sliders 85/70/40/20, four balls. Run the harness and visually check every image: alignment as before, balls coloured per the table, toggles not mid-animation.
- [ ] **Step 4:** Run the full suite. Expected: all pass.

### Task 8: The plugin page's G-Force Test Effect group on the shared view model

**Files:**
- Modify: `Settings\SettingsControl.xaml` — the `GForceTestGroup` content becomes one `<ui:GForceTestView x:Name="GForceTestView"/>`; keep the header, toggle and note bound to its view model. Every remaining named element keeps the `GForceTest` prefix.
- Modify: `Settings\SettingsControl.GForceTest.cs` — delete the test-effect half (timer, engine, pad, bar, readout). Keep the shake-preview / feeling code. The constructor creates `GForceTestViewModel(plugin, () => page working values via SaveGForceToSettings(throwaway), DispatcherTicker, WpfUiDispatcher)` and sets the view's `DataContext`. `Unloaded` → `Stop()`.
- Modify: `QAdvanceFeedback.Tests\SettingsControlDirtyTrackingTests.cs` — `Nothing_in_the_test_effect_panel_marks_the_page_dirty` also scans `Ui\ViewModels\GForceTestViewModel.cs`; the prefix test keeps working.

- [ ] **Step 1:** Write the failing test `Page_test_effect_uses_the_shared_view_model` (source scan: the page creates `GForceTestViewModel`; `SettingsControl.GForceTest.cs` no longer contains `PushSimulatedFrame`). Run it. Expected: FAIL.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** Run `dotnet test --filter "SettingsControlDirtyTrackingTests|Page_test_effect_uses_the_shared_view_model"`. Expected: PASS.
- [ ] **Step 4:** Run the harness; `settings-gforce.png` still shows the Test Effect group. Then the Release build (0 warnings) and the full suite. Expected: all pass.

### Task 9: Docs, final verification

**Files:**
- Modify: `docs\shakeit-test-effect-mvvm-design.md` (Status: implemented)
- Create: `docs\shakeit-test-effect-mvvm-report.md` — built / verified / needs-SimHub checklist / rulings.
- Modify: `README.md`, `README.zh-Hans.md` — Test Effect bullets for both effect kinds, the new screenshot, the auto-off and "plays on the device" notes.
- Modify: `docs\setup\Setup Guide 设置指南.txt` (both languages)

- [ ] **Step 1:** Update the docs.
- [ ] **Step 2:** Run the full suite and the Release build. Expected: all pass, 0 warnings, only the DLL + pdb in the output.
- [ ] **Step 3:** Write the needs-SimHub checklist in the report:
  - each test vibrates the device with no game running;
  - the Test link is gone from all six effects;
  - auto-off turns each toggle off;
  - leaving a panel stops its test;
  - an in-game Lock test overrides the live output and live output returns afterwards.

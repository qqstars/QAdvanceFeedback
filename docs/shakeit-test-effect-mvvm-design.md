# ShakeIt Test Effects + MVVM groundwork — design

Date: 2026-11-17. Status: implemented (report: `docs\shakeit-test-effect-mvvm-report.md`). Builds on `docs\shakeit-native-effects-design.md`.

This is **project 1 of 2**. Project 2 (the plugin settings page restructured to MVVM, tab by tab) gets
its own design after this one ships.

> **Project 2 is done (2026-11-20).** The whole plugin settings page is MVVM: one
> `SettingsPageViewModel` over a draft / baseline / live settings model, one view model per tab, views
> that only bind, and the panels' remaining code-behind events moved to attached behaviours. Design and
> plan: `docs\full-mvvm-plan.md`; what shipped, the rulings and the test changes:
> `docs\full-mvvm-report.md`; the standing rule: `docs\architecture.md`, "UI: MVVM (standing rule)".
> The page's G-Force Test Effect described below is now hosted by `GForcePageViewModel` (on a throwaway
> copy of the page's unapplied G-Force values) and stops when the G-Force tab is hidden.

## Intent

The plugin settings page's G-Force Test Effect drives the published `GForce.*` properties, but nothing
reaches the device: Custom Effects bound to properties are gated by SimHub (game running, not paused),
and in Shakers mode a Custom Effect refuses to run out of game at all. The owner wants tests that make the
**real ShakeIt device vibrate**, from inside the native QAdvanceFeedback ShakeIt effects, for G-Force and
for WheelLock/WheelSlip, with no game running.

The owner also set a **project-wide rule**: all UI follows strict MVVM. This project lays the MVVM
groundwork and builds everything it touches on it.

## What SimHub allows (verified in SimHub.Plugins 9.13.2)

- ShakeIt calls every effect container's `ProcessEffects` every frame, game or not. A container outputs
  out of game only if `IsAllowedOutOfGame` is true; otherwise it is gated on game running / not paused /
  not in menu / not spectating.
- The device layer does not gate on the game: bass shakers (FMOD) only suspend after an idle delay and
  resume on the first non-zero output (the very start of a test may be lost while the device reopens);
  motor/haptic devices with a frequency channel have no game check. Global mute, gain, effect
  mute/disable and the channel mapping still apply.
- ShakeIt's own "Test effect" link is shown only when the effect's `AllowTestMode` is true.

## Goals

1. **G-Force Test Effect in the four G-Force ShakeIt panels.** The same G-ball pad + wheel bar + readout
   as the plugin page, running the real `GForceEngine` and publishing `GForce.*` through the existing
   `PublishTestEffect`. The native G-Force effects already play while a test runs.
2. **WheelLock / WheelSlip Test Effect in their ShakeIt panels:**
   - A Test toggle and **four sliders, FL / FR / RL / RR**, each a simulated source value 0–100.
   - While the test is on, the channel's output is computed from those sliders and published to
     `WheelLock/WheelSlip.Projected.*` (and `LatestProjected`), so the native effect — reading
     `Projected.*` as always — plays it on the device. The pulse therefore follows the Pulse settings
     automatically.
   - Balls on the "Source value -> Projected value" graph show the current position (see "Balls").
3. **10-second auto-off** for every Test Effect (both ShakeIt kinds and the plugin page's G-Force test):
   no input (dragging the G-ball/bar, moving a slider) for 10 s turns the test off and **its toggle shows
   off**.
4. **ShakeIt's own "Test effect" link is removed** from all six QAdvanceFeedback effects
   (`AllowTestMode => false`).
5. **MVVM groundwork**, and the ShakeIt panels (WheelLock/WheelSlip, G-Force, key data section, graph)
   rebuilt on it. The plugin page's G-Force Test Effect group is rebuilt on the shared test view model,
   because the ShakeIt panel shares it.

## Non-goals

- Restructuring the rest of the plugin settings page (project 2).
- A Lock/Slip test on the plugin settings page.
- Any change to the live calculation, learning, or the 62 published product property names.

## Anti-goals

- A test must never change learned values or saved settings (SMax/S90/S75 stay exactly as they were).
- A test must never stay on by itself: leaving the panel or 10 s idle always stops it and hands the
  outputs back to the live pipeline.
- No event handler that sets another control's property (the MVVM rule).

## MVVM rule (standing, project-wide)

All UI — plugin settings page and ShakeIt panels — uses view models with `INotifyPropertyChanged`,
dependency properties where a control needs them, one-way / two-way bindings and command bindings.
Code-behind is wiring only (constructing the view model and setting `DataContext`). Never handle an event
and set another control's property from it; expose a view-model property and bind to it. Recorded in
`docs\architecture.md`.

Allowed view-side code: a custom control's own rendering and input handling (the graph, the G-ball pad),
exposed through its dependency properties, and small attached behaviours for pure layout (aligning a
toggle's switch track to a column).

## Approaches considered

For the Lock/Slip test:

1. **Feed the live engine fake telemetry** (null or full pedals). Rejected: the trigger gate needs real
   pedal values (null closes it, so Lock/Slip read 0), and with fake values the learners would learn from
   the test and move SMax.
2. **ShakeIt's own test burst** (100 % for 750 ms). Considered and dropped by the owner: it is a fixed
   full burst, not a feel test.
3. **A separate test path (chosen):** the slider values go through the same normalization the graph
   draws, the engine's own aggregation, the real projection curve and pulse, and are published to
   `Projected.*`. While the test is on, the brake and throttle triggers count as fully pressed for this
   path only. That ends the moment the test is off, and it never touches the live path or the learners.

For the MVVM groundwork: hand-rolled `ObservableObject` + `RelayCommand` (chosen; single-DLL distribution
rules out extra libraries such as CommunityToolkit.Mvvm) vs. a third-party MVVM library (rejected).

## Design

### Shared MVVM building blocks (SimHub-free, unit-tested)

`QAdvanceFeedback\Ui\Mvvm\`:
- `ObservableObject` (`INotifyPropertyChanged`, `SetProperty`).
- `RelayCommand`.
- A dispatcher seam so view models can be tested without WPF threading.
- A clock/timer seam so the 10 s auto-off and the 60 Hz test tick are testable.

View models live in `QAdvanceFeedback\Ui\ViewModels\` and views in `QAdvanceFeedback\Ui\Views\`. Views are
XAML `UserControl`s inside the single DLL. View models depend only on settings, the hub and small plugin
interfaces, and are link-compiled into the test project like the other SimHub-free files.

### Test session (plugin side)

- A single `TestEffectSession` in the plugin owns "who is testing what". At most one test runs at a time.
  Starting one stops any other.
- The G-Force test keeps `PublishTestEffect(GForceOutput)`.
- Lock/Slip gets `PublishLockSlipTest(ProjectedWheelLockSlipResult)` and `TestLockSlipActive`. While
  active:
  - The live `DataUpdate` does not overwrite `Projected.*` / `LatestProjected`.
  - The channel not under test publishes 0.
- Native WheelLock/WheelSlip containers become `IsAllowedOutOfGame = true` with the same gate as G-Force:
  output only when (Lock/Slip test active) or (game running, not paused, not in menu, not spectating).
- Turning a test off publishes empty values and releases the override, in that order (as
  `PublishTestEffect(null)` does today).

### Lock/Slip test calculation (SimHub-free, unit-tested)

`LockSlipTestEngine`, per tick (60 Hz):

1. **Input:** the four slider values (FL/FR/RL/RR, 0–100).
2. **Normalize each wheel** with the key data the panel currently shows (`KeyDataPanelModel.Resolve`:
   learned under Auto, the manual slot under Manual, else the shipped reference, else the channel
   default), using exactly the normalization in `SourceToProjectedGraph`. Refactor that normalization into
   one shared function used by both.
3. **Combine:** Front / Rear / Left / Right / All from the normalized wheels with `Aggregator.Compute` and
   the channel's `ToAggregationWeights()`, as the engine does.
4. **Project:** through a test-owned `ProjectedWheelLockSlipEngine` built from the channel's applied
   projector and pulse settings, with real `dt`, so the pulse runs exactly as in the car. Cold-start
   amplitude damping is not applied (confidence 1, floor 1); the test plays the fully calibrated curve.
5. **Triggers:** brake and throttle triggers count as fully pressed; there is no pedal gate in this path.

The result is a `ProjectedWheelLockSlipResult` (pulsed and pulse-free values) for the tested channel.

### Balls on the "Source value -> Projected value" graph

One ball per output channel of the effect's current routing:

| Routing | Balls and colours (radial gradient fill) |
|---|---|
| Per wheel | FL bright red, FR dark red, RL bright blue, RR dark blue |
| Front/Rear | Front standard red, Rear standard blue |
| Left/Right | Left bright grey, Right dark grey |
| One channel | one ball, orange (builder's call, distinct from the green curve) |

- **x** = that channel's source value. For a wheel, its slider. For a group, the same aggregation applied
  to the four slider values.
- **y** = that channel's **pulse-free** projected value. The ball sits on the curve and does not flicker
  with the pulse; the device still gets the pulsed value.
- Because groups are combined after normalization, a group ball can sit slightly off the curve. That is
  expected and correct.
- Balls appear only while the test is on.

### G-Force test in the ShakeIt panel

`GForceTestViewModel`, shared by the plugin page's Test Effect group and the four G-Force ShakeIt panels:
- Pad position, bar position, rate (Hz) and readout texts.
- Enabled toggle, two-way bound.
- A settings provider. The page passes its unapplied working values, as today. ShakeIt passes the applied
  settings.

The view is one `GForceTestView` (pad, bar, readout), used in both places.

**The test output is the published property values (owner's requirement, same rule as Lock/Slip):**
- Each test frame is published once, through `PublishTestEffect`, to the `GForce.*` properties and to
  `LatestGForce`, from the same `GForceOutput`.
- The native G-Force effects play only `LatestGForce`. That means they play exactly what `GForce.*`
  reports, test or live.
- Nothing else feeds the effects: no second, test-only route.
- The same holds for Lock/Slip: `PublishLockSlipTest` writes `Projected.*` and `LatestProjected` from one
  result, and the native effects read only `LatestProjected`.
- A unit test pins it: for a published test frame, each native effect's value equals the matching
  published property value.

### Auto-off

Each test view model holds a last-input timestamp, reset by every slider, pad or bar change and by
switching the test on. A 10 s idle sets `IsTestEnabled = false`. The toggle is bound to that property, so
it shows off, and the view model stops publishing and releases the outputs. Leaving the panel (view
unloaded) does the same immediately.

### Who owns the Lock/Slip test (two ShakeIt controls, one view model)

ShakeIt builds two separate controls for a WheelLock/WheelSlip effect. The shipped layout keeps both:
- The **edit panel** (`GetEditControl()` → today `WheelChannelEffectPanel`) knows the container and its
  routing.
- The **curve-slot graph** (`QAdvanceProjectionFilter.ResponseCurveEditor`, a new control per request)
  knows only `IsLock`.

Decision (designer's call):
- **The container owns** one non-serialized `WheelChannelEffectViewModel`, created lazily.
- **The filter gets a back-reference** to its owning container. The container sets it whenever its
  `Filter` is created or deserialized. The graph control binds to that same view model, and the view
  model exposes the routing, so the balls follow it.
- **The test controls** (toggle + FL/FR/RL/RR sliders) are in the **edit panel**, at its bottom, which
  ShakeIt shows directly above the graph.
- **The test stops when the edit panel unloads**, never on the graph's `Unloaded`: the graph is rebuilt
  per request and would otherwise end the test as it starts. The 10 s auto-off applies as well.
- If the graph has no view model (no back-reference yet), it draws the curve without balls.

### ShakeIt panels rebuilt on MVVM

Behaviour is kept exactly as it is today (sync through the hub, 700 ms key data debounce, 1 s refresh
under Auto, read-only/hidden rules, alignment, two-decimal readouts):
- `WheelChannelEffectViewModel`: source, routing, key data, graph, test.
- `GForceEffectViewModel`: maxima, lateral direction, integrate switch, test.
- The graph becomes a control with dependency properties for curve points and balls.

The existing rule classes (`KeyDataPanelModel`, `GForceReadout`, `SourceToProjectedGraph`, `SettingsHub`)
stay and are used by the view models.

## Constraints

- net48 WPF, single-DLL output (`bin\Release\net48` holds only `QAdvanceFeedback.dll` + `.pdb`); no new
  NuGet runtime dependencies.
- Must not change the 62 product properties or the Custom Effect binding pattern.
- Strings in both languages (`StringTableEn`/`StringTableZhHans`).
- Docs directly under `docs\`. Screenshots in both languages (`docs\images\shakeit-*.png`) and both READMEs
  updated.
- Unit tests first for every SimHub-free part.
- No git; local changes only.

## Left to the builder

- Slider layout and labels, ball size, test toggle placement (above the graph).
- Folder and class names beyond those above.
- The 60 Hz tick for the Lock/Slip test (the G-Force test keeps its Rate setting).

## Needs SimHub to verify

- A device vibrates for each test with no game running.
- The bass shaker start delay after idle.
- The Test link is gone.
- Auto-off turns the toggle off.

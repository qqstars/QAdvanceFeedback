# ShakeIt Test Effects + MVVM groundwork — implementation report

Date: 2026-11-17. Design: `docs\shakeit-test-effect-mvvm-design.md`. Plan:
`docs\shakeit-test-effect-mvvm-plan.md`.

## What was built

| Area | Files |
|---|---|
| MVVM foundation | `Ui\Mvvm\ObservableObject.cs`, `RelayCommand.cs`, `IUiDispatcher.cs`, `ITicker.cs`; rule in `docs\architecture.md` ("UI: MVVM") |
| Lock/Slip test calculation | `Settings\KeyDataNormalization.cs` (shared with the graph), `Settings\LockSlipTestEngine.cs`, `Settings\TestBalls.cs`; `ChannelValues.LockWithoutPulse/SlipWithoutPulse` |
| One test at a time, out-of-game gate | `Core\ShakeItEffects\TestEffectSession.cs`, `TestAwareOutputGate.cs` (was `GForceOutputGate`) |
| Plugin publish path | `QAdvanceFeedback.PublishLockSlipTest`, `TestLockSlipActive`, static `TestSession`; `DataUpdate` leaves `Projected.*` to a running Lock/Slip test |
| Native effects | WheelLock/WheelSlip `IsAllowedOutOfGame` + gate; `QAdvanceFeedbackEffect.AllowTestMode => false` (ShakeIt's Test link removed) |
| View models (SimHub/WPF-free) | `Ui\ViewModels\`: `GForceTestViewModel`, `LockSlipTestViewModel`, `KeyDataViewModel`, `GForceEffectViewModel` (+ `GForceAxisViewModel`), `WheelChannelEffectViewModel` (+ `GraphViewModel`), `PanelViewModelBase`, `IPanelHost`, `ITestEffectHost`; frame logic in `Settings\GForceTestFrame.cs` |
| Views | `Ui\Views\`: `WheelChannelEffectView`, `GForceEffectView`, `GForceTestView`, `GraphView` (XAML); `GBallPad`, `WheelBar`, `SourceProjectedGraph` (custom controls); `TrackAlignment`; `WpfUiDispatcher`, `DispatcherTicker`, `LocExtension`, `VisibleWhenConverter` |
| SimHub wiring | `Integration\ShakeIt\PluginPanelHost.cs`; containers own their view model; the filter's graph binds to its owner's view model |
| Plugin page | The G-Force Test Effect group is the shared `GForceTestView` on a `GForceTestViewModel` (page working values); the old timer/pad/bar code is gone |
| Removed | `WheelChannelEffectPanel`, `KeyDataSection`, `GForceEffectPanel`, `ProjectionCurvePlot` (code-behind panels) |
| Strings | `ShakeItPanel.Test.*` in both tables |
| Docs | Both READMEs, setup guide (both languages), this report, design status |
| Screenshots | `shakeit-wheellock-test(.zh-Hans).png` (new), all `shakeit-*.png` and `settings-*.png` re-rendered |

## Verification done here

- **Full suite:** 1733/1733 after the review fixes (1721 before them; 1657 at the start of this project). New test classes:
  - `MvvmFoundationTests`, `LockSlipTestEngineTests`, `TestEffectSessionTests`;
  - `GForceTestViewModelTests`, `LockSlipTestViewModelTests`, `PanelViewModelTests`;
  - `MvvmRuleTests`, `PageTestEffectTests`, `ReviewFixTestEffectTests`.
- **Updated tests:**
  - `ReviewFixTests`: gate rename.
  - `SettingsControlDirtyTrackingTests`: the `MarkDirty` scan now also reads the shared view model, and the
    prefix check reads the shared view.
- **Release build:** `dotnet build QAdvanceFeedback -c Release`, 0 warnings, 0 errors. `bin\Release\net48`
  holds only `QAdvanceFeedback.dll` + `.pdb`.
- **The test output is the published property values:**
  `Native_effect_values_equal_the_published_projected_values` and the G-Force equivalent compare each
  native effect slot with the value `PropertyPublisher` publishes for it.
- **Screenshots checked by eye, both languages:**
  - alignment unchanged;
  - four balls in the per-wheel colours on the curve;
  - toggles fully on/off;
  - the page's Test Effect group intact.

## Needs SimHub to verify (no SimHub on the build machine)

1. **With no game running:**
   - The WheelLock/WheelSlip Test plays on the device through the effect's channel mapping.
   - The G-Force Test plays through the four G-Force effects.
2. **Test link:** ShakeIt's own "Test effect" link no longer shows on the six QAdvanceFeedback effects.
3. **Auto-off:** after 10 s without moving a slider / the ball, the toggle turns off and the device goes
   quiet.
4. **Leaving the panel:** leaving a ShakeIt effect's panel (or the plugin page) while a test runs stops
   it.
5. **One test at a time:** starting a test in one place turns another running test's toggle off.
6. **In game:** a Lock test overrides the live output; the live output returns when it stops.
7. **Bass shakers:** the first fraction of a second of a test may be lost while SimHub reopens a
   suspended audio device (expected).

## Final review fixes

An independent whole-change review found no Critical issues and three Important ones. All three were fixed
test-first in `ReviewFixTestEffectTests`. Each test was seen failing against the old behaviour, except
I3's, whose API did not exist before.

- **I1 - The panel's open views were not counted.**
  - Problem: ShakeIt builds a new edit view each time it shows an effect, and all views share the
    container's view model. If two views overlapped, leaving the panel could fail to stop a test, and
    re-showing it could kill one.
  - Fix: `PanelViewModelBase` counts loaded views. It stops only when none is left, checked after the
    current layout pass (`IUiDispatcher.PostLater`).
- **I2 - The pulse ran on the nominal 1/60 s.**
  - Problem: a drifting `DispatcherTimer` made the device pulse slower than configured.
  - Fix: both tests now pass the measured frame time, clamped to 100 ms.
- **I3 - "One test at a time" was keyed by kind.**
  - Problem: two Lock tests (or the page's and a panel's G-Force test) could both run.
  - Fix: `TestEffectSession` is keyed by owner. A test taken over on the same outputs does not release
    them over the new test.
  - Also fixed (M2): tests listen to the static session only while running, so idle view models (and
    their containers) are no longer held for the session's lifetime.

After the fixes: full suite 1733/1733, Release build 0 warnings.

Deferred (minor, not fixed):

- At the moment a Lock/Slip test starts or stops, one in-game frame can be dropped or mixed. This is the
  same pattern as the existing G-Force test, and the comment in `PublishLockSlipTest` describes the
  release order too optimistically.
- If the curve-slot graph is shown without its edit panel active, it draws with the default key data
  until the panel activates.
- The Lock/Slip test serializes the projector and pulse to JSON every tick to detect changes.
- On the plugin page, the Test Effect's Rate row no longer shares the G-Force label column. Switching
  tabs inside the page leaves a test running until the 10 s auto-off, as before.
- Clearing a G-Force max box or the Rate box shows a binding-error border instead of being ignored.

## Rulings

- `ChannelValues.LockWithoutPulse/SlipWithoutPulse` added to Core for the ball positions. Additive.
- `GForceTestFrame` was moved from the page before its view-model test (a verbatim move). It is pinned by
  `Frame_matches_the_shared_frame_formula`.
- `IPanelHost` exposes `ViperAvailable`. The view model builds the source options with the existing
  `ShakeItSourceOptions`, so that rule stays unit-tested.
- **Views namespace:** views live in the root namespace `QAdvanceFeedbackViews`. Under `QAdvanceFeedback.*`
  the plugin class shadows the namespace in WPF-generated code, the same reason `NumericEditor` lives in
  `QAdvanceFeedbackControls`.
- **Fewer view files:** the key data and Lock/Slip test sections are inline in
  `WheelChannelEffectView.xaml`, not separate files.
- **Forwarding host:** `PluginPanelHost` forwards to `QAdvanceFeedback.Instance`, instead of the plugin
  class implementing `IPanelHost`. With a static `TestSession`, panels created before the plugin starts,
  or across a restart, keep working.
- **Two tests not seen failing:** `PanelViewModelTests` and `PageTestEffectTests` were written before
  their code but not run red. Their compile, or a check against the baseline, would have failed.
- **Page test and the device:** the page's G-Force test still publishes the page's unapplied values. It
  reaches the device only through the native G-Force effects. Property-bound Custom Effects stay
  game-gated by SimHub.

## Follow-up: Test Effect visibility rules

- Every Test Effect (G-Force, WheelLock/WheelSlip, Clutch Bite) shows its sliders/pad and description only while it is on, bound to `IsTestEnabled` through `VisibleWhenConverter`.
- Switching a test off (by the user, the 10 s auto-off, leaving the panel, or another test starting) resets its inputs: G-Force pad X/Y and wheel bar, the four Lock/Slip sliders and the clutch slider go to 0.
- The WheelLock/WheelSlip Test Effect moved out of `WheelChannelEffectView` into `GraphView`, below the "Source value -> Projected value" graph (`GraphViewModel.Test`), so it is the last section of the panel.
- Tests: `TestEffectUiRulesTests`. Full suite 1824/1824.

## Follow-up: Test Effect alignment

- The WheelLock/WheelSlip Test Effect lives in the graph control, which ShakeIt hosts separately from the panel, so it had its own label column sized only by "Test": with the toggle pulled left onto its track, "Off" overlapped "Test". G-Force had no label column for its test toggle at all.
- New pure-layout attached behaviour `Ui/Views/LabelColumnSync.cs` (math in `Ui/Mvvm/LeadingColumns.cs`): views joined by the same object (`Group="{Binding Test}"`, the shared test view model) keep one label-column width, so the test toggle and sliders start where the panel controls start.
- G-Force: the test now has a "Test" label + toggle row, with the toggle in the same column as Integrate Wheel Lock and Slip, and the update-rate editor indented to the toggle track like the Lateral feedback direction dropdown. On the plugin page the same view aligns its own rows.
- Tests: `LeadingColumnsTests`. Full suite 1829/1829; screenshots regenerated in both languages.

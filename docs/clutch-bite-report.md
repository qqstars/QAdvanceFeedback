# Clutch Bite effect — implementation report

Date: 2026-11-17. Design: `docs\clutch-bite-design.md`. Plan: `docs\clutch-bite-plan.md`.

## What was built

| Area | Files |
|---|---|
| Output curve, last-direction rule | `Core\Clutch\ClutchBiteCurve.cs` (`ClutchBiteCurve`, `ClutchDirectionTracker`) |
| Standing-start detection | `Core\Clutch\ClutchBiteDetector.cs` |
| Learning (blend → mature) | `Core\Clutch\ClutchBiteLearner.cs` |
| Source, per-frame engine | `Core\Clutch\ClutchBiteEngine.cs` (`ClutchSourceResolver`, `ClutchBiteParameters`, `ClutchBiteEngine`) |
| Settings, sync | `Settings\ClutchBiteSettings.cs`; seven `SettingField.Clutch*` hub fields |
| Persistence | `Core\Runtime\RuntimeDocument.cs` / `RuntimeCache.cs`, `RuntimeStore.cs` (`ClutchBiteLearners`, `LastClutchKey` in `QAdvanceFeedback.Parameters.json`) |
| Plugin | `QAdvanceFeedback.Clutch.cs` (class made partial): source, output, learning, save (cache + timer; flush on shutdown), test publish, axis list/read, Forget; wired into Init, `DataUpdate` and shutdown |
| Published | `QAdvanceFeedback.Clutch.Bite.Output` (63rd product property, appended last); `Diag.Clutch.Source/Input/BiteFinal/BiteLearned/BiteSamples` (appended to diagnostics and CSV) |
| Native effect | `Integration\ShakeIt\QAdvanceFeedbackClutchBiteContainer.cs` (45 Hz / 25 Hz high; plays only `LatestClutch`; test-aware gate) |
| UI (MVVM) | `Ui\ViewModels\ClutchViewModels.cs`; `Ui\Views\ClutchBiteView.xaml`, `ClutchGraph.cs`; page tab `Settings\SettingsControl.Clutch.cs` + `ClutchTab` in the XAML |
| Strings, health | Both string tables; `HealthSubsystems.ClutchBite` |
| Docs, screenshots | Both READMEs, setup guide (both languages); `settings-clutch.png`, `shakeit-clutch(-test)(.zh-Hans).png` |

## Verification done here

- **Full suite:** 1800/1800 (1733 before this project).
- **New test classes:** `ClutchBiteCurveTests`, `ClutchBiteLearningTests` (including the owner's
  hold-above / fast-crossing / slow-crossing cases), `ClutchSettingsAndPersistenceTests`,
  `ClutchBiteEngineTests`, `ClutchEffectTests`, `ClutchViewModelTests`.
- **Mutation checks:**
  - disabling the release-speed rule fails the fast-crossing tests;
  - disabling the backdating fails the three accuracy tests.
- **Product property count:** the tests pinning 62 product properties were updated deliberately to 63
  (`AllPublishedPropertiesTests`, `ShakeItTapTests`).
- **Release build:** 0 warnings, 0 errors; `bin\Release\net48` holds only the DLL + pdb.
- **Screenshots** checked by eye in both languages:
  - the shared label column and toggle tracks line up on the tab and in the panel;
  - the curve peaks at the bite, with the deadzones shaded;
  - the test ball sits on the curve.

## Needs SimHub to verify

1. With no game running, a chosen pedal axis drives the Test Effect and the device plays it.
2. Game clutch detection across titles: games without a clutch channel fall back to the axis.
3. Standing-start samples in a few cars land near the felt bite point. Watch `Diag.Clutch.*`.
4. The 25/45 Hz feel; the bite point shown on the tab follows the car.
5. Learned values survive a restart and a car switch (`QAdvanceFeedback.Parameters.json`,
   `ClutchBiteLearners`).

## Final review fixes

An independent whole-change review found no Critical issues and four Important ones. All four were fixed
test-first in `ClutchReviewFixTests`; each test was seen failing before its fix.

- **I1 - Synchronous disk flush on SimHub's data thread.**
  - Problem: every car, game or source switch (and a flickering axis) flushed the multi-MB parameters file
    on the data thread, which could hitch every effect.
  - Fix: learning now goes to the runtime cache; the store's 5 s timer writes it, and shutdown still
    flushes. A crash can therefore lose at most the last few seconds, not a sample per "at once" save.
- **I2 - One key for everything.** The readout, graph, Test bite and Forget now use the same key: the car
  whose clutch is really in use (game or axis), kept after the session.
  - Also fixed (M1): a game that never showed a clutch (constant 0) is no longer a source, so it cannot
    replace the last real car.
  - Also fixed (M3): learning is saved under the lock, so a concurrent Forget cannot be undone.
- **I3 - A settling idle created a false early sample.** A release counts only after at least 0.3 s armed
  with RPM inside ±2 % and not trending by more than 1 % (first vs last frames). A blip still decaying at
  release learns nothing.
- **I4 - A pause or menu mid-start continued the start.** Leaving the session drops a start in progress,
  any frame gap over 0.2 s resets the detector, and the one-frame correction is capped at 3 points.

After the fixes: full suite 1807/1807, Release build 0 warnings.

Deferred (minor, not fixed):

- If the pedal axis disconnects during a test, the test falls back to the slider value until the 10 s
  auto-off.
- A learned bite outside 10–90 shows unclamped in the readout and `Diag.Clutch.BiteFinal`, while the curve
  uses the clamped value.
- With a non-integer bite and a very small range (1–3), the graph's peak can draw under 100, because it is
  sampled at whole points.
- **Number boxes:** `TargetNullValue` on the three number boxes has no effect, and clearing a box leaves
  it blank (nothing is applied).
- **Axis picker:**
  - the "(none)" axis item may show blank on load (WPF selector with a null value; check in SimHub);
  - it lists every `JoystickPlugin.*` property, buttons included, and refreshes only when the panel opens.
- **Settings sync:** an external file edit that clears the fallback axis is not adopted (existing hub rule
  for null values).

## Owner's follow-up: Restore defaults and learning parameters

- **Restore all defaults** now resets the Clutch settings, the learning parameters included. Learned bite
  points are kept. Pinned by `Restore_all_defaults_resets_the_clutch_settings`, which was seen failing
  without the fix.
- **Learning parameters** on the plugin page's Clutch tab only:
  - the eight thresholds in the design's "Learning parameters" table;
  - `Core\Clutch\ClutchLearningParameters.cs` (Core), `ClutchLearningSettings` (Settings), one hub field
    `ClutchLearning`, and `ClutchLearningViewModel`;
  - the detector and the learner read them every frame.
- **Not in the ShakeIt panel:** the panel's view model is created without `showLearning`, and a source
  scan pins that only the page passes `showLearning: true`.
- **Tests:** `ClutchLearningParametersTests`. They check that a looser release limit accepts a crossing
  the default rejects, that a higher arm level needs the pedal pressed further, and that the mature count
  and spread are honoured.
- The source readout keeps the last in-session source while out of game.
- `ClutchGraphViewModel.Lo/Hi` are set but not bound.
- `TestEffectSession.cs` and `ToneDefaults.cs` had their line endings rewritten.

## Rulings

- **Test threshold (curve):** at gamma 2.5 the outer-third test value is 15.0, so the test asserts
  `< 16`.
- **File layout:**
  - `ClutchDirectionTracker` lives in `ClutchBiteCurve.cs`;
  - `ClutchBiteLearnerState`/`Status` live in `ClutchBiteLearner.cs`;
  - the plugin code is in the partial `QAdvanceFeedback.Clutch.cs`.
- **Release-speed check:** it uses the fastest three-frame movement in the 0.2 s window, not the average.
  Otherwise "hold at 46, then 46 → 40 in 0.1 s" averages to about 20 %/s and slips through.
- **RPM onset extrapolated to the baseline.** A least-squares line of RPM against pedal position over the
  dip frames is solved for the baseline RPM, then one frame of travel is added.
  - The onset-frame-only rule missed by 1.25 points at 25 %/s in the simulator.
  - Cost if wrong: a sim whose early load is strongly non-linear could bias about 1 point. This is
    bounded, because the result is clamped between the trigger frame and the frame before the onset.
- **Core stays free of `Settings`:** the engine takes `ClutchBiteParameters`, and
  `ClutchBiteSettings.ToParameters()` converts.
- **One build for Tasks 5–7:** the container needs the view and view model, so those three tasks were
  verified by one build.
- **`ClutchBiteView` constructors:** it has a parameterless constructor that activates its `DataContext`
  view model, which the page's XAML needs. It also neutralises the page's implicit
  TextBlock/ComboBox/NumericUpDown margins, so the tab and the panel look the same.
- **Harness order:** the screenshot harness sets `QAdvanceFeedback.Instance`/`Hub` before it builds the
  settings page.
- **Tests not seen failing first:** several tests were written before their code but not run red (the
  types were missing). The mutation checks above stand in for the key behaviours.

## Follow-up: panel visibility, Detect and pedal-following test

- The Test Effect's slider and description show only while the test is on; switching it off resets the slider to 0.
- While the test is on and an axis is readable, the slider follows the clutch pedal (the view model's Tick writes the axis value into the slider).
- The fallback-axis note, the "bite in use" readout and **Forget learned bite** show only when Bite point mode is Auto (`ClutchBiteViewModel.IsAuto`).
- **Detect** button beside the fallback axis: ShakeIt's device settings hold only output devices (no pedal axis), so the axis is detected instead. For 5 s it samples every controller axis; the one with the largest travel (≥ 30 %) is selected, and Invert is set when its rest reading is above mid. With no movement, nothing changes.
- Tests: `QAdvanceFeedback.Tests/Ui/TestEffectUiRulesTests.cs`. Full suite 1824/1824; Release build 0 warnings; screenshots regenerated in both languages.
- Setup guide and both READMEs now lead with the native ShakeIt effects (recommended), with the property-based profiles and the ShakeIt-calculation source as alternate options at the end.

## Follow-up (2026-11-18): layout

- Removed the "Your bite point" text beside the Bite point editor (it was cut off in the ShakeIt panel; the editor shows the value).
- The ShakeIt panel's controls start right after its own labels again. Cause: the page-only learning rows were still built (hidden) in the panel and their long labels widened the shared label column, since a ContentTemplate renders even for null content. They now come from a typed DataTemplate on `LearningSection` (null in the panel), so they are never created there.
- Plugin page Clutch tab: categories (GroupBoxes) - Clutch Bite, Clutch axis, Bite point learning (Auto only), Bite curve, Test Effect - through one `HeaderedContentControl` style that is a GroupBox when `IsCategorized` (page) and plain in the ShakeIt panel.
- Tone default now 40 Hz / 15 Hz at the bite; default gain 100 %.
- Tests: `ClutchLayoutTests`. Full suite 1844/1844; screenshots regenerated in both languages.

# Clutch Bite effect — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** a QAdvanceFeedback clutch-bite feel:
- `QAdvanceFeedback.Clutch.Bite.Output` (0–100), computed every frame from the game's clutch (with a
  pedal-axis fallback): an S-curve range around a bite point that is learned per game, car and source
  from standing starts;
- a native ShakeIt effect that plays the published value;
- a Clutch tab and a ShakeIt panel sharing one view model, with a clutch → output graph and a Test Effect
  that reads the pedal with no game running.

**Architecture:**
- SimHub-free Core classes (curve, direction tracker, detector, learner) are unit-tested.
- The plugin resolves the source, computes and publishes the output, feeds the learner and persists it in
  the runtime document.
- MVVM view models (WPF/SimHub-free) drive one XAML view used in both the page tab and the ShakeIt panel.
- The native container plays only the published snapshot, like WheelLock/WheelSlip.

**Tech Stack:** C# / .NET Framework 4.8 WPF (single DLL), MahApps 1.5, xunit on net8.0 (link-compiled
SimHub-free sources).

**Spec:** `docs\clutch-bite-design.md` (approved 2026-11-17). Builds on
`docs\shakeit-test-effect-mvvm-design.md`.

## Global Constraints

- **Output:** `bin\Release\net48` holds only `QAdvanceFeedback.dll` + `.pdb`. No new runtime packages.
- **Properties:** existing properties and Custom Effect bindings are unchanged. Product properties go
  62 → 63 (`Clutch.Bite.Output` appended last). The pinned tests are updated deliberately.
- **Clutch convention:** 0 = released, 100 = fully pressed (SimHub's `[Clutch]`, `ITelemetryFrame.ClutchPercent`).
- **Defaults:**
  - When Apply = Press and Release; Bite Point Mode = Auto; Bite Point = 50 (10–90); Faded Range = 30
    (1–50); Gamma = 2.5 (1.0–6.0); fallback axis none; invert off.
  - Tone: low-output 45 Hz, high-output 25 Hz (used), white noise off.
- **Curve:** `lo = max(5, b − r)`, `hi = min(95, b + r)`, `S(t) = tᵏ / (tᵏ + (1−t)ᵏ)`; 0 at or outside
  lo/hi; 100 at b.
- **Direction:** option B (last direction of movement), with 2 points of hysteresis.
- **Detection:**
  - arm when in gear, below 0.5 km/h, clutch ≥ 90;
  - RPM trigger ≥ 4 % below baseline; onset at baseline − 1 %, falling to the trigger;
  - speed trigger 0.3 km/h, confirm +2 km/h;
  - correction: one frame of pedal travel back toward pressed;
  - reject: release faster than 30 %/s in the 0.2 s before onset; throttle swing > 15; speed-only on a
    loose surface; paused / menu / spectating.
- **Learning:**
  - keep the last 15 samples, value = median;
  - before maturity: `final = Blend(configured, median, ConcaveHotWeight(totalCount, sd / 50, 9))`;
  - mature at ≥ 9 samples with sd ≤ 3: store only the matured bite, drop the samples, stop learning;
  - Manual mode: final = configured.
- **Key:** `KeyedGripLearner.MakeKey(game, car, "GameClutch" | "Axis:<name>")`. Persist every accepted
  sample at once (cache + `Flush`), plus on game/car switch and shutdown. Remember the last in-game key.
- **Game clutch available:** the frame's `ClutchPercent` is non-null and has exceeded 5 this game. The
  latch resets on a game change. A source switch while the detector is armed resets the detector.
- **Live output:** only in a game session (`TestAwareOutputGate`). The Clutch test is `TestKind.Clutch`
  (owner-keyed). The test uses the last in-game key's final bite, else the configured value. Auto-off
  after 10 s without input.
- **MVVM rule:** `docs\architecture.md` "UI: MVVM". Every new string in both string tables. Screenshots
  in both languages. Docs under `docs\`. No git.
- **Testable code:** every SimHub-free file is added to the test csproj with an explicit
  `<Compile Include>`, except under `Core\**` and `Ui\ViewModels\*`, which are already wildcards.

## Review Focus

1. **A game with no clutch channel** (`ClutchPercent` null), or one that reports a constant 0: the
   fallback axis is used if configured; otherwise output 0. No learning under a key the source never
   provided.
2. **A pedal axis that disconnects mid-test or mid-drive** (property returns null): output 0, the source
   readout shows none, and nothing throws into SimHub.
3. **A car or game switch during a standing start:** the detector resets, the previous key is flushed,
   and no sample lands under the new car.
4. **Bite and range at the limits:**
   - b = 10 with r = 50 (lo clamps to 5), and b = 90 (hi clamps to 95);
   - gamma = 1 (straight line);
   - c exactly at lo, b and hi.
5. **A matured key:** later standing starts never change it. "Forget learned bite" clears it, and learning
   restarts from the configured value.

---

### Task 1: Curve and direction tracker

**Files:**
- Create: `QAdvanceFeedback\Core\Clutch\ClutchBiteCurve.cs`, `ClutchDirectionTracker.cs`
- Test: `QAdvanceFeedback.Tests\Clutch\ClutchBiteCurveTests.cs`

**Interfaces — Produces:**
- `enum ClutchApplyDirection { PressAndRelease, PressOnly, ReleaseOnly }`
- `static class ClutchBiteCurve`:
  - `double Output(double clutch0100, double bite, double range, double gamma)` — 0..100;
  - `(double Lo, double Hi) Edges(double bite, double range)`;
  - `IReadOnlyList<GraphPoint> Points(double bite, double range, double gamma, int samples = 101)` —
    value coordinates (x clutch, y output).
  - Bite is clamped to 10..90, range to 1..50, gamma to 1..6 inside.
- `sealed class ClutchDirectionTracker`:
  - `ClutchMoveDirection Last { get; }`, where `enum ClutchMoveDirection { Unknown, Pressing, Releasing }`;
  - `void Update(double clutch0100)` — 2-point hysteresis from the last turning point;
  - `bool Allows(ClutchApplyDirection apply)` — PressAndRelease always; PressOnly ⇔ Last == Pressing;
    ReleaseOnly ⇔ Last == Releasing;
  - `void Reset()`.

- [ ] **Step 1:** Write the failing tests:
  - `Peak_is_100_at_the_bite_and_0_at_or_outside_the_edges` — b 50, r 30: Output(50) = 100;
    Output(20) = Output(80) = Output(10) = Output(95) = 0.
  - `S_curve_is_slow_fast_slow` — b 50, r 30, k 2.5: Output(30) < 15, Output(40) > 70, and
    Output(45) − Output(40) < Output(40) − Output(35).
  - `Gamma_1_is_a_straight_line` — Output(35) = 50 ± 1e-9.
  - `Edges_keep_a_5_point_deadzone` — Edges(75, 30) = (45, 95); Edges(20, 30) = (5, 50).
  - `Bite_is_clamped_to_10_90`.
  - `Release_only_follows_last_direction_and_holds` — 100 → 60 (releasing), then hold at 60 for 10
    updates: Allows(ReleaseOnly) stays true and Allows(PressOnly) false.
  - `Jitter_under_2_points_does_not_flip_direction` — releasing at 60, then 61.5, 60.5, 61.9: still
    Releasing; 62.1: Pressing.
  - `Unknown_direction_allows_only_press_and_release`.
- [ ] **Step 2:** Run `dotnet test --filter ClutchBiteCurveTests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement both files.
- [ ] **Step 4:** Run the same command. Expected: PASS. Then the full suite. Expected: all pass.

### Task 2: Standing-start detector and learner

**Files:**
- Create: `QAdvanceFeedback\Core\Clutch\ClutchBiteDetector.cs`, `ClutchBiteLearner.cs`, `ClutchBiteLearnerState.cs`
- Test: `QAdvanceFeedback.Tests\Clutch\ClutchBiteLearningTests.cs`

**Interfaces — Produces:**
- `readonly struct ClutchFrame(double TimeSec, double? Clutch, double? Rpm, double? SpeedKmh, double? Throttle, bool InGear, bool LooseSurface, bool ValidForLearning)`
- `sealed class ClutchBiteDetector`:
  - `bool Update(ClutchFrame f, out double sample)` — true on the frame a sample is confirmed;
  - `void Reset()`;
  - `ClutchDetectorPhase Phase`.
  - It holds a 1 s history and implements arm / watch / backdate / correct / confirm / reject exactly as
    in the Global Constraints.
- `sealed class ClutchBiteLearnerState`:
  - `List<double> Samples` (≤ 15), `int TotalCount`, `double? Matured`;
  - JSON round-trippable.
- `sealed class ClutchBiteLearner`:
  - `void Import(Dictionary<string, ClutchBiteLearnerState>)` / `Dictionary<string, ClutchBiteLearnerState> ExportAll()`;
  - `bool AddSample(string key, double sample)` — ignored (false) when the key is matured; matures and
    drops the samples when ≥ 9 with sd ≤ 3;
  - `ClutchBiteStatus Status(string key, double configured, bool auto)` — `Learned` (median or matured),
    `Count`, `IsMatured`, `Final`;
  - `void Forget(string key)`;
  - `static string Key(string game, string car, string sourceIdentity)` → `KeyedGripLearner.MakeKey(game, car, sourceIdentity)`.

- [ ] **Step 1:** Write the failing tests. Frames run at 60 Hz; true bite 45; the RPM falls only after
  the pedal passes 45, with a 2-frame engine delay:
  - `Slow_release_learns_the_true_bite` — 90 → 30 at 12 %/s: sample within 45 ± 1.
  - `Hold_above_bite_learns_nothing` — 90 → 46, hold 10 s: no sample.
  - `Fast_crossing_then_hold_is_rejected` — slow to 46, then 46 → 40 in 0.1 s, hold 40, car pulls away:
    no sample.
  - `Moderate_crossing_then_hold_learns_the_true_bite` — 46 → 40 in 0.5 s, hold 40: sample within
    45 ± 1.
  - `Throttle_swing_rejects` — throttle 10 → 40 during the release: no sample.
  - `Speed_only_on_loose_surface_rejects` — RPM held flat (idle governor), loose surface: no sample; on
    tarmac: a sample near the speed onset.
  - `Not_armed_unless_stopped_in_gear_with_clutch_in`.
  - `Learner_blends_then_matures_then_stops`:
    - configured 50, samples 44, 45, 46: Final strictly between 45 and 50;
    - after 9 samples within ±2: IsMatured, Samples empty, Final = median;
    - a 10th sample of 30: ignored.
  - `Learner_does_not_mature_on_spread_samples` — 9 samples with sd 5: not matured.
  - `Forget_returns_to_configured`.
  - `Manual_mode_final_is_configured`.
  - `Export_import_round_trip_keeps_learning_and_matured_keys`.
- [ ] **Step 2:** Run `dotnet test --filter ClutchBiteLearningTests`. Expected: FAIL.
- [ ] **Step 3:** Implement the three files. Dispersion goes into `ColdWarmBlend.ConcaveHotWeight` as
  `sd / 50`.
- [ ] **Step 4:** Run the same command. Expected: PASS. Then the full suite. Expected: all pass.

### Task 3: Settings, hub fields, runtime persistence

**Files:**
- Create: `QAdvanceFeedback\Settings\ClutchBiteSettings.cs`
- Modify:
  - `Settings\QAdvanceFeedbackSettings.cs` (`ClutchBiteSettings Clutch`);
  - `Settings\SettingsHub.cs` (fields `ClutchApply`, `ClutchBiteMode`, `ClutchBitePoint`, `ClutchFadedRange`,
    `ClutchGamma`, `ClutchAxis`, `ClutchAxisInvert` — editable, appended to the enum; Read/Write);
  - `Core\Runtime\RuntimeDocument.cs` (`Dictionary<string, ClutchBiteLearnerState> ClutchBiteLearners`,
    `string LastClutchKey`);
  - `Core\Runtime\RuntimeCache.cs` and `RuntimeStore.cs` (`Load/SaveClutchBiteLearners`,
    `Load/SaveLastClutchKey`).
- Test: `QAdvanceFeedback.Tests\Clutch\ClutchSettingsAndPersistenceTests.cs`; test csproj Include for
  `ClutchBiteSettings.cs`.

**Interfaces — Produces:**
- `enum ClutchBiteMode { Auto, Manual }`
- `ClutchBiteSettings`:
  - `ClutchApplyDirection Apply`, `ClutchBiteMode Mode`;
  - `double BitePoint`, `FadedRange`, `Gamma` (clamped in setters);
  - `string FallbackAxis`, `bool InvertAxis`;
  - defaults as in the Global Constraints.

- [ ] **Step 1:** Write the failing tests:
  - `Defaults_match_the_design`;
  - `Setters_clamp`;
  - `Hub_sets_each_clutch_field_and_raises_it`;
  - `Diff_reports_clutch_fields`;
  - `Settings_json_round_trip`;
  - `Runtime_document_round_trips_clutch_learners_and_last_key` — through `RuntimeCache`, like the
    existing `RuntimeStoreTests` pattern;
  - `Old_runtime_file_without_clutch_section_loads_empty`.
- [ ] **Step 2:** Run `dotnet test --filter ClutchSettingsAndPersistenceTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS. Then the full suite. Expected: all pass.

### Task 4: Plugin pipeline, published property, diagnostics

**Files:**
- Create:
  - `QAdvanceFeedback\Core\Clutch\ClutchBiteEngine.cs` (SimHub-free, per frame: source → tracker →
    curve → output; detector → learner);
  - `QAdvanceFeedback\Core\Clutch\ClutchSourceResolver.cs` (game-vs-axis rule, latch, identity).
- Modify:
  - `Core\AllPublishedProperties.cs` (product name `Clutch.Bite.Output` last; diagnostics
    `Diag.Clutch.Source/Input/BiteFinal/BiteLearned/BiteSamples` appended);
  - `PropertyPublisher.State.cs` (`UpdateClutch(double output)`, `UpdateClutchDiagnostics(...)`, CSV
    snapshot order) and `PropertyPublisher.cs` (register);
  - `QAdvanceFeedback.cs`:
    - per frame: read the axis via `PluginManager.GetPropertyValue`, run the engine, publish (skipped
      while the Clutch test is active), `LatestClutch` snapshot, save the learners to the cache;
    - `Flush` on an accepted sample and on game/car switch;
    - `PublishClutchTest(double? output0100)`, `TestClutchActive`;
    - `ClutchStatus` for the view models, `ForgetClutchBite()`;
    - `ControllerAxes()` (`GetAllPropertiesNames()` filtered to `JoystickPlugin.`), `ReadAxis01(name)`;
    - load at Init.
  - `Core\Health\HealthSubsystems.cs` (+ `ClutchBite`) and both string tables.
- Test: `QAdvanceFeedback.Tests\Clutch\ClutchBiteEngineTests.cs`; update `AllPublishedPropertiesTests`,
  `PublishedPropertyNamesTests` and the CSV parity tests for 63.

**Interfaces — Produces:**
- `ClutchSourceResolver`:
  - `ClutchReading Resolve(string gameId, double? gameClutch, double? axis01, string axisName, bool invert)`
    → `(double? Clutch0100, string Identity, ClutchSourceKind Kind)`;
  - `enum ClutchSourceKind { None, Game, Axis }`.
- `ClutchBiteEngine`:
  - `double Step(ClutchReading r, ClutchFrame f, string game, string car, ClutchBiteSettings s, ClutchBiteLearner learner, out bool sampleAccepted)`;
  - `double OutputFor(double clutch0100, ClutchBiteSettings s, double finalBite)` (used by the test);
  - `ClutchBiteStatus CurrentStatus`.

- [ ] **Step 1:** Write the failing tests:
  - `Null_game_clutch_uses_axis_and_constant_zero_game_clutch_uses_axis_until_latched`;
  - `Latch_resets_on_game_change`;
  - `Source_change_mid_start_resets_detector`;
  - `Car_switch_mid_start_drops_the_sample`;
  - `Output_follows_curve_and_direction`;
  - `Product_properties_are_63_with_clutch_last`;
  - `Csv_row_matches_header_with_clutch_diagnostics`.
- [ ] **Step 2:** Run `dotnet test --filter "ClutchBiteEngineTests|AllPublishedPropertiesTests|PublishedPropertyNamesTests|RegressionFixTests"`.
  Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS. Then `dotnet build QAdvanceFeedback -c Release`
  (0 warnings) and the full suite. Expected: all pass.

### Task 5: Native ShakeIt effect

**Files:**
- Create: `QAdvanceFeedback\Integration\ShakeIt\QAdvanceFeedbackClutchBiteContainer.cs`
- Modify:
  - `Core\ShakeItEffects\ToneDefaults.cs` (`Clutch`: low 45, high 25 used, noise off);
  - `Core\ShakeItEffects\TestEffectSession.cs` (`TestKind.Clutch`; `SharesOutputs(Clutch, Clutch)` true).
- Test: extend `ShakeItEffectsMappingTests` (tone defaults) and `TestEffectSessionTests` (Clutch kind).

**Interfaces:**
- `[ShakeItContainerMetadata(205, "QAdvanceFeedback Clutch Bite", ..., "QAdvanceFeedback", OutputMode.Any)]`
- `QAdvanceFeedbackClutchBiteContainer : EffectsContainerBase<ToneOutput>`:
  - one `QAdvanceFeedbackEffect` (All);
  - `IsAllowedOutOfGame => true`, gated by `TestAwareOutputGate.Allows(plugin.TestClutchActive, …)`;
  - value from `plugin.LatestClutch` (fresh), filter null;
  - `SetAudioDefaults` → `ToneDefaults.Clutch`;
  - lazy, `[JsonIgnore]` `ClutchBiteViewModel`;
  - `GetEditControl()` → `ClutchBiteView`.
  - The class name is frozen from now on.

- [ ] **Step 1:** Write the failing tests `Clutch_tone_defaults_are_45_low_25_high` and
  `Clutch_kind_shares_outputs_only_with_itself`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. The container compiles only in the plugin.
- [ ] **Step 4:** Run the tests. Expected: PASS. Then the Release build (0 warnings) and the full suite.
  Expected: all pass.

### Task 6: View models

**Files:**
- Create in `QAdvanceFeedback\Ui\ViewModels\`: `IClutchHost.cs`, `ClutchBiteViewModel.cs`, `ClutchGraphViewModel.cs`, `ClutchTestViewModel.cs`
- Modify: `Integration\ShakeIt\PluginPanelHost.cs` (implements `IClutchHost`); `QAdvanceFeedback.Tests\Ui\PanelViewModelTests.cs` fake host
- Test: `QAdvanceFeedback.Tests\Ui\ClutchViewModelTests.cs`

**Interfaces:**
- `interface IClutchHost`:
  - `ClutchBiteStatus ClutchStatus { get; }`, `string ClutchSourceText { get; }`, `string ClutchCarText { get; }`;
  - `IReadOnlyList<string> ControllerAxes()`, `double? ReadAxis01(string name)`;
  - `void ForgetClutchBite()`, `void PublishClutchTest(double? output0100)`;
  - `double FinalBiteForTest { get; }` — the last in-game key's final bite, else configured.
- `ClutchBiteViewModel : PanelViewModelBase`, constructor `(IPanelHost host, IClutchHost clutch, ITicker, IUiDispatcher)`:
  - two-way, writing through the hub: `Apply`, `Mode`, `BitePoint`, `FadedRange`, `Gamma`, `FallbackAxis`,
    `InvertAxis`;
  - `Choices` for the enums and `Axes`;
  - readout strings `ConfiguredText`, `LearnedText`, `SamplesText` ("learning"/"matured"), `FinalText`,
    `SourceText`, `CarText`;
  - `ICommand ForgetCommand`;
  - `ClutchGraphViewModel Graph`, `ClutchTestViewModel Test`;
  - readout refresh every 1 s; `Deactivate` stops the test.
- `ClutchGraphViewModel`: `Curve` (value coordinates), `BiteMarker`, `ConfiguredMarker` (Auto only), `TestBall` (nullable point).
- `ClutchTestViewModel`:
  - `IsTestEnabled`, `UseSlider` (no axis), `Slider` (0..100), `InputText`, `OutputText`;
  - ticks at 60 Hz: input = axis (inverted per settings) or slider → `ClutchDirectionTracker` →
    `ClutchBiteCurve.Output(..., FinalBiteForTest, ...)` → `PublishClutchTest(output)`;
  - the ball goes to (input, output);
  - session `TestKind.Clutch`, owner-keyed, listening only while running;
  - auto-off after 10 s without input change (axis delta > 0.5 or slider move);
  - `Stop()` publishes null.

- [ ] **Step 1:** Write the failing tests:
  - `Settings_write_through_hub_and_refresh_from_hub`;
  - `Readout_shows_learning_then_matured`;
  - `Forget_command_calls_host`;
  - `Graph_curve_is_the_shared_curve_and_moves_with_bite`;
  - `Test_publishes_curve_output_from_axis`;
  - `Test_uses_slider_without_axis`;
  - `Test_auto_offs_after_10s_without_pedal_movement`;
  - `Test_stops_on_deactivate`;
  - `Another_test_stops_clutch_test`.
- [ ] **Step 2:** Run `dotnet test --filter ClutchViewModelTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS. Then the full suite. Expected: all pass.

### Task 7: Views, Clutch tab, ShakeIt wiring, strings, screenshots

**Files:**
- Create in `QAdvanceFeedback\Ui\Views\`:
  - `ClutchBiteView.xaml(.cs)`: settings rows, readout, Forget button, axis picker, test toggle and slider,
    `ClutchGraph`;
  - `ClutchGraph.cs`: a custom control with DPs `Curve`, `BiteMarker`, `ConfiguredMarker`, `TestBall`;
    shaded deadzones.
- Modify:
  - `Settings\SettingsControl.xaml`: new `<TabItem x:Name="ClutchTab">` holding `<v:ClutchBiteView x:Name="ClutchBiteView"/>`;
  - `SettingsControl.xaml.cs`: header localized; DataContext = a page-owned `ClutchBiteViewModel`;
    Loaded/Unloaded → Activate/Deactivate. `Clutch*` names must be excluded from the dirty sweep if a
    handled type, since the tab applies at once;
  - both string tables;
  - `tools\screenshot-harness` (`settings-clutch.png`, `shakeit-clutch(.zh-Hans).png`, a test-on capture).
- Test: extend `MvvmRuleTests` (covers new views automatically); `SettingsControlDirtyTrackingTests` if a
  named Clutch control is a handled type.

- [ ] **Step 1:** Run `dotnet build QAdvanceFeedback -c Release`. Expected: 0 warnings; output only the
  DLL + pdb.
- [ ] **Step 2:** Run `dotnet test --filter "MvvmRuleTests|SettingsControlDirtyTrackingTests"`.
  Expected: PASS.
- [ ] **Step 3:** Run the harness and check every new image by eye:
  - alignment (shared label column, toggle tracks);
  - curve peak at the bite, deadzones, ball;
  - both languages.
- [ ] **Step 4:** Run the full suite. Expected: all pass.

### Task 8: Docs and final verification

**Files:**
- Modify: `docs\clutch-bite-design.md` (status: implemented); `README.md`, `README.zh-Hans.md`;
  `docs\setup\Setup Guide 设置指南.txt`
- Create: `docs\clutch-bite-report.md` (built / verified / needs-SimHub checklist / rulings).

- [ ] **Step 1:** Update the docs, including how to bind the native effect and the
  `Clutch.Bite.Output` property.
- [ ] **Step 2:** Run the full suite and the Release build. Expected: all pass; 0 warnings.
- [ ] **Step 3:** Write the needs-SimHub checklist in the report:
  - the axis is readable with no game;
  - the test plays on the device;
  - game clutch availability across titles;
  - standing-start samples in a few cars;
  - 25/45 Hz feel.

# QAdvanceFeedback — final assembly (wiring + settings UI) report

> **Naming note (added later, see docs/refinements-report.md):** the property tiers this report calls "Reliable" and "Final" were subsequently renamed to **"Normalized"** and **"Projected"** respectively (published names `WheelLock.Normalized.*`/`WheelLock.Projected.*` etc.) - this report is left as a historical record of what was true at the time and is NOT updated throughout for the new names.


## STATUS

Complete. Layer 4's direction fix is implemented and tested. The plugin is fully wired
(`QAdvanceFeedback.cs` Init/DataUpdate/End) publishing Raw/Reliable/Final (54 properties) plus the 8
G-force channels unconditionally, and 9 diagnostic properties when enabled. `ConfigStore`/`RuntimeStore`
persist settings and learned state. CSV export, the diagnostics toggle, and the four-tab
`IWPFSettingsV2` settings UI (Wheel Lock, Wheel Slip, G-Force, General) are built and localised in
English and Simplified Chinese with a key-parity test. The G-force longitudinal model was rebuilt
twice more during this task at the owner's explicit direction (configurable sustain floors, then a
full washout/motion-cueing redesign) — see "The G-force model's final form" below; every change is
tested and mutation-evidenced.

## Final test count

**309 tests, all passing**
(`dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj -v q --nologo`).

## Build verification

- `MSBuild QAdvanceFeedback\QAdvanceFeedback.csproj -t:Restore,Build -p:Configuration=Release -v:m` →
  **0 Warning(s), 0 Error(s)**.
- `bin\Release\net48\` contains **exactly two files**: `QAdvanceFeedback.dll` and
  `QAdvanceFeedback.pdb`. Every SimHub/GameReaderCommon/MahApps/Newtonsoft reference in the csproj is
  `<Private>false</Private>` — confirmed by directory listing after adding `UseWPF` and the MahApps
  reference for the settings control; WPF framework assemblies (PresentationCore/Framework,
  WindowsBase, System.Xaml) are part of .NET Framework itself and are never copied into `bin\` either.

## Verbatim published property names (representative)

```
QAdvanceFeedback.WheelLock.Raw.All
QAdvanceFeedback.WheelLock.Raw.FrontLeft
QAdvanceFeedback.WheelSlip.Raw.All
QAdvanceFeedback.WheelLock.Reliable.All
QAdvanceFeedback.WheelSlip.Reliable.FrontLeft
QAdvanceFeedback.WheelLock.Final.All
QAdvanceFeedback.WheelSlip.Final.RearRight
QAdvanceFeedback.GForce.Bottom.FrontLeft
QAdvanceFeedback.GForce.Back.TopRight
```
62 product names total (Raw/Reliable/Final × 9 targets × 2 channels = 54, plus 8 G-force), always
published. Plus 9 diagnostic names (`Diag.Direction`, `Diag.MotionLevel`, `Diag.MotionMagnitudeG`,
`Diag.Lock.LearnedPeakG`, `Diag.Lock.LearnerConfidence`, `Diag.Slip.LearnedPeakG`,
`Diag.Slip.LearnerConfidence`, `Diag.GForce.LearnedAccelMaxG`, `Diag.GForce.LearnedDecelMaxG`) only
when `GeneralSettings.EnableDiagnostics` is on at Init. The exact set and the gate are unit-tested,
SimHub-free, in `AllPublishedPropertiesTests.cs` (`Core\AllPublishedProperties.cs` is the single
source of truth `PropertyPublisher.Register<TPlugin>` dispatches from).

**Correct spelling, per the standing rule**: the G-force pad names are `GForce.Bottom.*` (corrected
from the requirement text's "Buttom" typo throughout code, tests and docs — `docs\gforce-report.md`
and the earlier task's files were swept too). A broader misspelling sweep across every file this task
touched (and a re-check of files from earlier tasks) found nothing else to fix beyond the historical
"Buttom"→"Bottom" references, which are deliberately preserved as documentation of the correction, not
left as live typos.

## How Layer 4 direction now works

`Core\Reliable\LongitudinalDirectionResolver.cs` (new) replaces pedal-based gating entirely.
Direction (`Unknown`/`Slowing`/`SpeedingUp`) is derived ONLY from differentiated ground speed
(`ITelemetryFrame.GroundSpeedKmh` ?? `SpeedKmh`, via the sample's own Old/New pair — SimHub already
hands this plugin a genuine previous/current frame every `DataUpdate`, so no extra bookkeeping is
needed), with a 0.6 m/s² dead band and a light EMA smoothing filter (α=0.35) so ordinary telemetry
jitter at constant speed cannot chatter the state. `ReliableWheelLockSlipEngine.ComputeChannel` gates
Lock on `Slowing` and Slip on `SpeedingUp` — pedal state is never read anywhere in this decision.
`LongitudinalG`'s sign is never read either, so a title with an inverted convention (the evidenced
Forza Horizon 6 case — positive while slowing in 95.8% of qualifying frames) cannot affect direction
at all; `Inverted_longitudinal_g_convention_still_reads_braking_on_the_lock_channel_from_the_first_frame`
proves this directly. Layer 3's own internal `Brake > 20`/`Throttle > 40` gates inside
`LegacySlipAlgorithm` are untouched (that is SimHub's own decompiled design, out of scope for this fix).

## The G-force model's final form (two redesigns during this task)

The brief's Settings section asked for accel/decel maxima only; two further owner messages arrived
mid-task and both **replaced** the previous G-force longitudinal model rather than adding to it:

1. **Configurable sustain floors** (first redesign): every non-terminal pad in both chains (Bottom
   Rear/Back Low for braking, Bottom Rear/Back Low for acceleration) got a driver-editable sustain
   floor via `Math.Max(hat, floor*terminalWeight)` — continuous by construction (Max of two continuous
   functions). Defaults (50% for all four) are **derived**, not picked: each hat in this model is a
   symmetric isoceles triangle whose value at the midpoint of its own decay ramp is always exactly 0.5
   — computed from the model's own geometry, not a round number (replacing the old hard-coded 35%).
2. **Washout/motion-cueing redesign** (second, superseding redesign, per the owner's explicit reference
   to classical 4-axis-rig washout algorithms): the single "travel" parameter (which conflated LEVEL
   with MOTION) is now two separate paths per chain:
   - **Sustained** (low-pass, `SustainTimeConstantSeconds`=0.15s default): tracks the steady-state G
     ratio and drives the (unchanged) hat+sustain-floor spatial distribution.
   - **Transient** (the classical washout high-pass complement, `raw − lowpass(raw)`, itself smoothed
     by a second low-pass `TransientTimeConstantSeconds`=0.08s, then scaled by `TransientGain`=1.5):
     responds to the RATE G is changing, decays to zero once the input holds steady, and is applied to
     **every** pad in a chain (leading and trailing alike, deliberately uniform rather than a
     leading-vs-trailing redistribution — see the S5 scenario below) via **headroom scaling**
     (`ApplyTransient`: `1 - level` for a positive/onset drive, `level` for a negative/easing drive) so
     it can never leave [0,1] and still has somewhere to go even when the sustained path is fully
     saturated.
   All three constants are numerically calibrated (via an offline Python simulation of the exact
   discrete exponential-smoothing recurrence, not guessed) so the owner's S2 scenario ("hold ~1g for
   0.5s") settles to a small residual within that stated window while S3/S5 ("a fast step")
   produce a clearly larger, obviously visible transient — `GForceEngineTests.cs` encodes exactly the
   six acceptance scenarios (S1–S6) by name, using a "TransientGain=0 twin" comparison technique
   (a second engine instance, otherwise identical, fed the same input sequence with the gain zeroed
   out) to isolate the transient's own contribution precisely without hand-computing the filter
   cascade for every assertion.
   Both the sustain floors and the washout constants are exposed on the G-Force settings tab and
   persist through `ConfigStore`.

**Unverified**: neither redesign has been run on real hardware or in a live SimHub session — the six
acceptance scenarios are validated as pure, offline, stateful unit tests against `GForceEngine`
directly (dozens-of-frames simulations with hand-verified filter convergence), not against actual
rumble pads.

## Mutation evidence (all applied directly to the shipped file, tested, then reverted and
diff-verified byte-identical to the original)

**Part 1 — Layer 4 direction (mandatory a/b/c from the original brief):**

| Mutation | Change | Result |
|---|---|---|
| (a) revert Layer 4 direction to pedal state | `ComputeChannel`'s gate reverted to `BrakePercent`/`ThrottlePercent` thresholds instead of `LongitudinalDirectionResolver` | **6 tests failed**: `Lock_channel_reads_zero_while_the_car_is_measurably_speeding_up_even_with_the_brake_pedal_down`, `Slip_channel_reads_zero_while_the_car_is_measurably_slowing_even_with_the_throttle_pedal_down`, `Lock_channel_is_live_while_slowing_even_with_the_brake_pedal_fully_up`, `Unknown_direction_on_the_very_first_frame_reads_zero_on_both_channels`, `Inverted_longitudinal_g_convention_still_reads_braking_on_the_lock_channel_from_the_first_frame`, `ResetDirection_clears_the_learned_filter_so_a_stale_direction_does_not_survive_a_game_switch` |
| (b) publishing helper takes a non-generic `IPlugin` | `PropertyPublisher.Register<TPlugin>` → `Register(IPlugin plugin, ...)` | Compiles cleanly (silent bug, exactly as the brief warns) — no test in the SimHub-free test project can exercise the real `PropertyPublisher` against a live `PluginManager` (see "Concerns" below for why, and the local generic-inference mirror test `PropertyPublisherStructureTests.cs` that proves the underlying mechanism instead) |
| (c) publish diagnostic properties when the toggle is off | `AllPublishedProperties.AllNames` stopped gating `DiagnosticNames` on `diagnosticsEnabled` | **2 tests failed**: `AllPublishedPropertiesTests.Diagnostics_off_publishes_only_the_62_product_names`, `Diagnostics_on_adds_the_diagnostic_names_on_top_of_the_62_product_names` |

**G-force sustain-floor continuity (added mutation, per the sustain-floor requirement):**

| Mutation | Change | Result |
|---|---|---|
| (d) sustain floors applied via a hard `travel >= 1.0 ? floor : hat` branch instead of `Max(hat, floor*terminal)` | Both braking and acceleration chains mutated | **2 tests failed**: `No_step_change_in_Bottom_Rear_or_Back_Low_as_braking_travel_sweeps_past_completion_with_default_sustain`, `No_step_change_in_Back_Low_or_Bottom_Rear_as_acceleration_travel_sweeps_past_completion_with_default_sustain` |

**G-force washout model (replaces the earlier rate-term mutation, per the owner's explicit instruction):**

| Mutation | Change | Result |
|---|---|---|
| (a) remove the high-pass/transient path (`transient = 0.0` always) | `AdvanceWashout` | **4 tests failed**: `S1_gentle_onset_produces_a_small_transient_appropriate_to_the_modest_rate`, `S3_a_fast_step_produces_a_large_transient_that_then_settles_into_the_new_level`, `S5_a_transient_while_already_saturated_spends_the_headroom_above_the_sustain_floors`, `S6_a_slow_continuous_bleed_off_produces_a_small_but_sustained_continuous_transient` |
| (b) remove headroom scaling (`ApplyTransient` adds the drive directly, unscaled) | `ApplyTransient` | **1 test failed**: `S5_a_transient_while_already_saturated_spends_the_headroom_above_the_sustain_floors` (a loose ">5" bound did not initially catch this — the test was tightened to a precise, independently-computed range, `InRange(afterStep.BottomRearLeft.Value, 57.0, 62.0)`, once this gap was found; the tightened version is what is shipped and what fails under the mutation) |
| (c) remove washout (`transient = Math.Max(transient, Math.Abs(rawTransient))` — latches, never decays) | `AdvanceWashout` | **1 test failed**: `S2_holding_steady_G_washes_the_transient_out_to_the_sustained_distribution` |

Every mutation above was reverted and `diff`-verified byte-identical to the original before moving on;
the full 309-test suite and the 0-warning/single-DLL Release build were re-verified green after all
reverts.

## Localisation

English and Simplified Chinese, 81 keys each, exact parity enforced by
`LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set`. The Chinese table
was generated by a two-step, bash-heredoc-free pipeline per the standing rule: a UTF-8 source file
(`zh_source.py`, written directly via the file-write tool) was converted by a second script into a
**pure-ASCII** generator (every CJK character re-expressed as an explicit `\uXXXX` escape via Python's
own `unicode_escape` codec) which then wrote `StringTableZhHans.cs` via `io.open(..., encoding='utf-8')`
— verified afterwards by decoding the output file as UTF-8, confirming no `U+FFFD` replacement
characters and exact key parity with the English table.

## Concerns / unverified / judgment calls (flagged, not silently assumed)

1. **Mutation (b)'s test coverage gap, stated plainly**: `PropertyPublisher.cs` references
   `SimHub.Plugins` (a .NET Framework assembly); the test project targets net8.0. Adding a compile-time
   reference to `SimHub.Plugins.dll` there was tried and **broke test discovery for the entire assembly**
   (`could not find dependent assembly 'SimHub.Plugins'` at runtime) — confirmed directly, then reverted.
   There is therefore no test in the shipped SimHub-free test project that fails when the REAL
   `PropertyPublisher.Register` is mutated to a non-generic `IPlugin` parameter (confirmed: the mutation
   compiles cleanly and the full suite still passes). `PropertyPublisherStructureTests.cs` instead
   proves the identical generic-inference mechanism using a local, SimHub-free mirror (a fake
   `IFakePlugin` interface + an `AttachDelegate`-shaped extension method) — this is real evidence for the
   underlying C# language fact `PropertyPublisher.Register` depends on, but it is evidence about the
   mechanism, not a test that exercises the shipped file directly. Flagged rather than silently claimed
   otherwise.
2. **The G-force washout model's time constants/gain were calibrated against an offline Python
   simulation of the exact discrete recurrence, not against real hardware or a live SimHub session** —
   they produce the stated qualitative behaviour (S1–S6) in that simulation and in the C# engine
   itself, but how they actually feel on rumble pads is unverified and cannot be verified here.
3. **`GForceSettings.ResetLearning()`/`GForceEngine.Reset()` are hooked at GAME-change granularity
   only**, not on every same-game car change — `GForceMaxLearner` already keys per (game, car), so
   resetting on every car swap would needlessly discard every OTHER car's already-learned maximum. This
   is a judgment call on the brief's literal "session/vehicle-change reset" wording; the less
   destructive reading was applied and is documented in `QAdvanceFeedback.cs`'s own remarks.
4. **The `EnableDiagnostics` toggle's "write NO log output" requirement was interpreted as gating
   informational/diagnostic logging only** (Init/End notices), not genuine warnings/errors (a corrupt
   config file, a CSV write failure) — silencing those too would make a real operational problem
   invisible with no way to diagnose it. Flagged as a judgment call, not silently assumed.
5. **Custom Layer 4 wheel sources** (a driver pointing a source field at something other than the
   shipped default) are resolved via `WheelSourceResolver`: `PluginManager.GetPropertyValue` for Plain,
   `SimHubExpressionEvaluator` for JavaScript/NCalc, falling back to Layer 3's own Raw value for that
   wheel on any failure. This class, and `SimHubScriptEditor`/`PropertyPickerLauncher`/
   `SimHubExpressionEvaluator` (ported near-verbatim from the sibling project), all talk to a live
   SimHub process by design and **cannot be unit-tested** — their resolution/degradation logic is
   exercised only by the sibling project's own prior hard-won verification, not re-verified here.
6. **Nothing in this task has been run inside a live SimHub session, against a real game, or on rumble
   hardware** — unverifiable per the task constraints, same as every prior report in this project. All
   309 tests are pure, offline, hand-computed-expectation (or simulation-calibrated) unit tests, plus
   real-temp-file tests for `ConfigStore`/`RuntimeStore`/`CsvExportWriter`.

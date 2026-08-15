# QAdvanceFeedback - Layers 4/5 report

> **Naming note (added later, see docs/refinements-report.md):** the property tiers this report calls "Reliable" and "Final" were subsequently renamed to **"Normalized"** and **"Projected"** respectively (published names `WheelLock.Normalized.*`/`WheelLock.Projected.*` etc.) - this report is left as a historical record of what was true at the time and is NOT updated throughout for the new names.


## Status

Complete for the scope this task owns: **Core algorithms, settings model, and persistence** for
Layer 4 ("Reliable" projection) and Layer 5 (curve + pulse). **Not done, deliberately: wiring
`QAdvanceFeedback.cs`'s Init/DataUpdate to actually `AttachDelegate` the 36 new properties and drive
these engines from live SimHub telemetry.** See "Concerns" below for why, and note that this mirrors
the parallel G-force task's own documented scope boundary (`Settings/GForceSettings.cs`'s remarks
say the same thing about itself), which lands as a strong, independent signal that the plugin-wiring
step is meant to be a separate, later task shared by all three feature branches rather than three
concurrent edits to the same `QAdvanceFeedback.cs`/`.csproj`.

## Test count

**244 tests, all passing** (`dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj`) - up
from the 32 reported after Layers 1-3 (the increase includes this task's ~110 new tests plus the
parallel G-force task's own tests, all landing in the same shared test project).

## Build verification

- `MSBuild QAdvanceFeedback/QAdvanceFeedback.csproj -t:Rebuild -p:Configuration=Release -v:normal`
  -> **0 Warning(s), 0 Error(s)**.
- `bin\Release\net48\` contains **exactly two files**: `QAdvanceFeedback.dll` and
  `QAdvanceFeedback.pdb`. The new `Newtonsoft.Json` reference (needed by `ConfigStore`/`RuntimeStore`)
  is `<Private>false</Private>`, same convention as every other SimHub-shipped reference in this
  csproj - single-DLL distribution is unaffected.
- `dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -v q` -> 244/244 passed.

## What was built

- **`Core/Reliable/`** (Layer 4): `AchievedMotion` (resolves a g-magnitude from Layer 1 telemetry,
  with fallback), `GripLearner` (online-learned, outlier-rejecting, cold-start-clamped peak-g
  reference), `ReliableWheelLockSlipEngine`/`ReliableWheelLockSlipResult` (the fusion of Layer 3's Raw
  with the learned car-relative ratio).
- **`Core/Projection/`** (Layer 5): `MonotoneCubicCurve`, `PiecewiseCurve`, `ProjectorSettings`
  (+`ProjectorPreset`/`ProjectionChannel`), `OutputProjector`, `ProjectorAnchorEditor`/`AnchorSlot` -
  all ported from the sibling ReliableWheelLockSlip project (only `SlipDirection` renamed to
  `ProjectionChannel.Lock`/`Slip`) - plus new `PulseSettings`/`PulseGenerator` and the Layer 5
  orchestrator `FinalWheelLockSlipEngine`/`FinalWheelLockSlipResult`.
- **`Settings/`**: `WheelChannelSettings` (per-channel: 4 wheel sources + script type, `Projector`,
  `Pulse`), `ScriptType`/`ScriptSyntax` (Plain/JavaScript/NCalc), `DefaultWheelSources` (shipped
  default = plain reference to the matching Layer 3 Raw property), root `QAdvanceFeedbackSettings`
  (Version + `Lock`/`Slip` channels only - no G-force property, per the file-ownership boundary).
- **Persistence**: `ConfigStore` (load/save `plugin.QAdvanceFeedback.config.json`, atomic write,
  degrades to defaults on missing/corrupt file), `RuntimeStore`/`Core/Runtime/RuntimeCache`+
  `RuntimeDocument` (load/save the two `GripLearner` states to
  `plugin.QAdvanceFeedback.runtime.json`, in-memory cache, dirty-only background flush every 5s,
  atomic temp-file-plus-replace, synchronous final flush) - both link-compiled into the test project
  and exercised against real temp files, same convention as the sibling project.
- **`Core/ClampMath.cs`** (Layers 1-3's own file): additively extended with `IsFinite`/`SafeDiv`
  (ported verbatim from the sibling project) - `Clamp`/`To0100`/`To01` untouched.

## Layer 4 degradation ladder (stated explicitly, per the brief)

1. **Direct**: `ITelemetryFrame.LongitudinalG` present this frame - its **magnitude** is used.
2. **Derived**: LongitudinalG absent, but two consecutive frames' ground speed (`GroundSpeedKmh` ??
   `SpeedKmh`, mirroring Layer 3's own fallback) and a valid positive `ITelemetrySample.Dt` exist -
   differentiated speed (delta-v/delta-t, converted to g) stands in.
3. **Unavailable** (neither possible): Layer 3's Raw value is passed through unmodified (clamped
   0-100), per wheel and per aggregate - "something sane", never zero or garbage.

Independently of that ladder, whichever pedal is relevant (Brake% for Lock, Throttle% for Slip) gates
the live output to exactly 0 when it is present and below threshold (~15%) - the channel's event
cannot physically be occurring with the pedal up, regardless of what a noisy Raw heuristic reports. A
genuinely **absent** pedal reading (null, not zero) does **not** gate - it is treated as "cannot tell,
don't zero it", so a game that omits pedal telemetry entirely still gets a live reading.

**Deliberately sign-agnostic**: the engine never reads the *sign* of `LongitudinalG` (no assumption
about which sign means "slowing down" - that convention is undocumented for this field, and adding a
second unverifiable inference on top of Layer 2's already-flagged `GroundSpeed`-unit inference would
compound risk for no benefit). Direction (Lock vs. Slip) comes entirely from which pedal is committed;
magnitude comes from `Math.Abs`.

**Per-wheel differentiation**: g-force is a whole-car scalar and per-wheel slip data is banned, so the
only per-wheel signal available is Layer 3's own Raw four-way *proportions*. The engine computes a
car-level, car-relative "grip utilisation" (0-100, from the learned-ratio ladder above) and
redistributes it across the four wheels using Raw's own share of the total (`w_i / mean(w)`, falling
back to an even 1.0 share for all four when Raw itself is all-zero) - this uses Raw's *shape*, never
its *absolute scale* (which is exactly what Layer 4 exists to stop trusting blindly).

## Pulse behaviour (exact, as implemented and tested)

- **"Reaches 100"** = the projected value passed to `PulseGenerator.Advance` is `>= 100 - 1e-6`.
- Below that threshold, or whenever `PulseSettings.Enabled` is false: output is **always** the plain
  projected value - pulse never engages away from the very top of the range.
- The **first frame** a value reaches 100 always reads exactly 100 (phase t=0) - entering "pinned at
  max" never itself causes a visible dip.
- Waveform: `output = min + (100-min) * 0.5 * (1 + cos(pi * t / gap))`, `t` = ms elapsed since this
  pulse's own t=0. At `t=0` -> 100; at `t=gap` -> min (one "move", exactly `gap` ms); at `t=2*gap` ->
  100 again (one full cycle = `2*gap` ms) - exactly the brief's worked example (gap=500ms ->
  100->min->100->min, each move 500ms).
- **Dropping back off 100 mid-pulse**: the instant the projected value itself drops below 100, the
  pulse **stops immediately** and that frame returns the plain projected value, with the phase clock
  reset to zero - chosen over "finish the current half-cycle" because continuing to show a stale pulse
  toward `MinValue` while the real value has already recovered would contradict the value being
  displayed. The next time 100 is reached (moments later or long after), the cycle **restarts at
  phase 0** (t=0 -> 100), never resuming wherever an old cycle left off.
- `GapMs`'s floor (200ms / 5Hz) is enforced in the **setter itself** (`PulseSettings.GapMs`), so a
  hand-edited config file, a fresh default, or a spinner all go through the same floor - not merely a
  UI `Minimum`.
- Time source is injectable by construction: `Advance(dtSeconds, projectedValue)` takes `dt` as a
  plain parameter (the same per-frame delta every other calculation in this plugin already reads from
  `ITelemetrySample.Dt`) - no wall clock, no `Thread.Sleep` anywhere in `PulseGenerator` or its tests.
- One `PulseGenerator` instance per published **target** (9 per channel, 18 total) - each tracks its
  own phase independently (one wheel can be pinned at 100 while another isn't) - but all targets in a
  channel share the **same** `PulseSettings` object, matching the brief's "per channel" (not
  per-target) scope.

## Mutation evidence (mandatory) - all three applied, tested, then reverted and diff-verified

| Mutation | Result |
|---|---|
| (a) `PulseSettings.GapMs`'s setter floor removed (`set => _gapMs = value;`) | **9 tests failed**: `PulseSettingsTests.Gap_below_200ms_is_floored_to_200ms_by_the_model_itself` (x4 InlineData), `PulseSettingsTests.Non_finite_gap_is_floored_to_200ms_rather_than_accepted` (x3 InlineData), `WheelChannelSettingsTests.Pulse_ships_disabled_with_a_200ms_floor_enforced`, `ConfigStoreTests.A_pulse_gap_persisted_below_200ms_by_a_hand_edited_file_still_loads_floored` |
| (b) `PulseGenerator`'s waveform replaced with a linear ramp (same period/endpoints) | **1 test failed**: `PulseGeneratorTests.Waveform_at_one_quarter_gap_matches_a_raised_cosine_not_a_linear_ramp` (deliberately asserts at `t=gap/4`, not the midpoint, since a raised cosine and a linear ramp agree at both endpoints AND at the exact midpoint - only an off-center point discriminates them) |
| (c) `GripLearner.Ratio`'s divisor replaced with a fixed `1.0` (the sibling project's own bug, reproduced on purpose) | **2 tests failed**: `ReliableWheelLockSlipEngineTests.Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating` (the brief's named acceptance test), `GripLearnerTests.Ceiling_relaxes_as_confidence_matures_and_disappears_at_full_confidence` |

Each mutation was applied to the single relevant file, `dotnet test` run, then reverted by restoring
from a scratch-directory backup and `diff`-verified byte-identical to the original before moving on.
Final full suite re-run green at 244/244, and the Release build re-verified 0-warning/single-DLL after
all three reverts.

## Concerns / seams for later work

1. **Plugin wiring is NOT done** (stated above, repeated here since it is the biggest gap): nothing in
   `QAdvanceFeedback.cs`'s `Init`/`DataUpdate`/`End` calls any of this task's new code. Layers 4/5
   publish nothing live yet. This was a deliberate choice, not an oversight: (a) the brief's own
   "DO NOT build the settings UI - a later task owns that" instruction, together with the parallel
   G-force task's `GForceSettings.cs` doc comment explicitly stating "A later task wires this class
   into ... QAdvanceFeedback.cs's Init/DataUpdate", reads as a project-wide convention that the
   Init/DataUpdate wiring for every new channel is staged as one later, dedicated task - almost
   certainly so three feature branches don't make concurrent, conflicting edits to the same plugin
   class/csproj; (b) actually resolving a driver's *custom* per-wheel source expression (JavaScript/
   NCalc, not the shipped Plain default) needs a live-SimHub reflection-based evaluator (the sibling
   project's `SimHubExpressionEvaluator`) that does not exist in this project and cannot be unit-tested
   without a running SimHub session - building it blind felt like exactly the kind of improvisation
   the brief warns against. **This is flagged, not silently decided**: if the intent was actually for
   this task to wire the properties through using the DEFAULT source only (skipping custom expression
   evaluation), that is a very small additional change from here (loop the 9 targets, call the two new
   engines with Layer 3's own result, `AttachDelegate` 36 more properties) and I can do it on request.
2. **`LongitudinalG`'s sign convention is unverified** (same class of gap Layer 2 already flagged for
   `GroundSpeed`'s unit) - resolved by design, not by decompilation: Layer 4 only ever reads
   `Math.Abs(LongitudinalG)` and gates direction by which pedal is committed, so the ambiguity cannot
   affect correctness, but it is called out explicitly rather than silently assumed away.
3. **The learned grip reference is a single global value per channel, not per-car/per-game** (unlike
   the sibling project's keyed profile store). This is a deliberate simplification the brief's Layer 4
   section supports ("learned online with outlier rejection and a cold-start clamp" - no per-car
   requirement stated), but it means switching cars mid-session shares one learned peak across both;
   a very different second car will re-mature the shared estimate over its own next few seconds of
   driving rather than instantly switching to a separately-remembered value. Flagged as a real,
   known limitation, not hidden.
4. **The per-wheel "RawShare" redistribution is a heuristic**, not a physically derived model (it
   cannot be, given the brief's ban on wheel-speed-derived slip) - it uses Raw's own four-way
   proportions to *shape* a car-level, car-relative severity, not to reconstruct genuine per-wheel
   slip. Tested for the properties that matter (a wheel Raw flags as worse reads higher; equal Raw
   reads equal; all-zero Raw distributes evenly) but not claimed to be more than that.
5. **Nothing here has been run inside a live SimHub session or against a real game** - unverifiable
   per the task constraints, same as Layers 1-3. All 244 tests are pure, offline, hand-computed-
   expectation unit tests (plus real-temp-file tests for the two persistence stores); the actual
   runtime behaviour of `ConfigStore`/`RuntimeStore` under `PluginManager.GetCommonStoragePath` inside
   a real SimHub install, and everything about live wiring per point 1, is unverified.

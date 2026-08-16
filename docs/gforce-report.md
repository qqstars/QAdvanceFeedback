# QAdvanceFeedback - G-force feedback channels report

> **Naming note (added later, see docs/refinements-report.md):** the property tiers this report calls "Reliable" and "Final" were subsequently renamed to **"Normalized"** and **"Projected"** respectively (published names `WheelLock.Normalized.*`/`WheelLock.Projected.*` etc.) - this report is left as a historical record of what was true at the time and is NOT updated throughout for the new names.


## Status

Complete for the scope this task owns: `Core/GForce/**` and `Settings/GForceSettings.cs` (model,
algorithm, defaults, published-property-facing state - **no settings UI, no persistence/config-store
wiring, no plugin AttachDelegate wiring**, all deliberately out of scope per file ownership - see
"What still needs wiring" below).

## Test count

**207 tests total, all passing** at the time of this report (`dotnet test
QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -v q`) - this number keeps growing because the
parallel Reliable/Projection task is actively adding its own tests to the same shared test project
while this task runs; re-running the command will very likely show a higher total by the time this is
read. **This task's own, stable contribution is 42 tests**: `GForceEngineTests` (19),
`GForceMaxLearnerTests` (9), `GForceSettingsTests` (11), `GForcePublishedNamesTests` (3), all passing.
The remaining tests (32 from Layers 1-3, plus the parallel task's own files -
`AchievedMotionTests`/`GripLearnerTests`, growing over time) are outside this task's scope and were
not touched.

## Build verification

- `MSBuild QAdvanceFeedback/QAdvanceFeedback.csproj -t:Restore,Build -p:Configuration=Release -v:m`
  -> **0 Warning(s), 0 Error(s)**.
- `bin\Release\net48\` contains **exactly two files**: `QAdvanceFeedback.dll` and
  `QAdvanceFeedback.pdb` - single-DLL distribution intact (confirmed after adding this task's own
  files; the parallel task's `Newtonsoft.Json` reference for persistence is also present in the
  csproj with `<Private>false</Private>`-equivalent handling, so it doesn't land in `bin\` either -
  verified by directory listing, not just trusted).
- `dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -v q` -> 124/124 passed.

## Files added (this task's ownership only)

- `QAdvanceFeedback/Core/GForce/GForceOutput.cs` - the 8 nullable-double pad outputs.
- `QAdvanceFeedback/Core/GForce/GForcePublishedNames.cs` - the exact 8 published-name suffixes, kept
  in its own file rather than added to the Layer 1-3 `PublishedPropertyNames` class (that file is
  outside this task's ownership).
- `QAdvanceFeedback/Core/GForce/GForceMaxLearner.cs` - per-(gameId,carId) AUTO max-G learner with
  outlier (single-frame-spike) rejection.
- `QAdvanceFeedback/Core/GForce/GForceEngine.cs` - the travel/position + spatial-distribution model.
- `QAdvanceFeedback/Settings/GForceSettings.cs` - FIXED/AUTO mode, defaults, learned-value exposure,
  recommended frequency range. No UI, no persistence.
- `QAdvanceFeedback.Tests/GForceEngineTests.cs`, `GForceMaxLearnerTests.cs`, `GForceSettingsTests.cs`,
  `GForcePublishedNamesTests.cs`.
- One narrow addition to `QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj`: a single explicit
  `<Compile Include="..\QAdvanceFeedback\Settings\GForceSettings.cs" .../>` line (not a `Settings\**`
  wildcard, specifically so it can't silently absorb the parallel task's own Settings files - which,
  as it turned out, were added to the same csproj concurrently by that task with their own explicit,
  narrow `<Compile>` lines; no conflict occurred).

## Published property names (verbatim, "Bottom" spelling preserved)

```
QAdvanceFeedback.GForce.Bottom.FrontLeft
QAdvanceFeedback.GForce.Bottom.FrontRight
QAdvanceFeedback.GForce.Bottom.RearLeft
QAdvanceFeedback.GForce.Bottom.RearRight
QAdvanceFeedback.GForce.Back.LowLeft
QAdvanceFeedback.GForce.Back.LowRight
QAdvanceFeedback.GForce.Back.TopLeft
QAdvanceFeedback.GForce.Back.TopRight
```

Confirmed via reflection against `lib\GameReaderCommon.dll` that `GameData.GameName` (string) and
`StatusDataBase.CarId` (string) exist - these are the natural (gameId, carId) identifiers for the
later wiring task to pass into `GForceSettings`/`GForceMaxLearner`.

## The travel/distribution model

**Braking and accelerating are modelled as two independent, non-negative "travel" signals**
(`brakeTravel`, `accelTravel`, each in [0,1]) rather than one shared axis, because a single frame's
`LongitudinalG` can only be braking OR accelerating (or neither), never both - and because the
brief's own two chains actually start at *different* zones (braking starts at Back Low, accelerating
starts at Bottom Rear), so they don't compose into one continuous axis cleanly.

For each signal:

```
magnitudeRatio = clamp01(|G| / maxG)                       // energy: how much is present
rateNorm       = clamp(signedRateOfChange / RateScalePerSecond, -1, 1)
travel         = clamp01(magnitudeRatio + RateGain * rateNorm)   // position: WHERE along the chain
```

`magnitudeRatio` alone would make position a pure function of the current G level (magnitude-only).
Adding the rate term is what makes **rising** G push the sensation further along its chain than the
same static G would (Bottom Front "keeps rising toward maximum" while G climbs), and makes **falling**
G recede it back - proportionally to how fast it's dropping ("slightly if easing gently,
significantly if dropping fast"). `magnitudeRatio` (not `travel`) is what scales the total energy, so
a 0g frame is exactly 0 output on every zone regardless of travel/rate - no special-casing needed for
"no G = no vibration."

Each travel signal is mapped onto its 3-zone chain with piecewise-linear "hat" functions that form a
partition of unity (sum to exactly 1 at every point) - continuous by construction, so sweeping travel
from 0 to 1 never produces a step change on any pad:

- Braking: Back Low (t=0) -> Bottom Rear (t=0.5) -> Bottom Front (t=1).
- Accelerating: Bottom Rear (t=0) -> Back Low (t=0.5) -> Back Top (t=1), with Back Low's weight
  additionally floored at `BackLowSustainFloor * BackTopWeight` so it keeps vibrating (at 35% by
  default) once the sensation has fully moved to Back Top, per the brief's explicit requirement.

Bottom Rear and Back Low are **shared** zones (braking's mid/start-zone and accelerating's
start/mid-zone respectively) - their final value is the **sum** of both axes' contributions, which is
safe because braking-energy and accelerating-energy can never both be non-zero for the same frame
(one is `max(0,-G)`, the other `max(0,G)`).

**Lateral** is applied last and independently: `lateralBias = clamp(lateralG/1.6, -1, 1)` produces a
left/right gain pair (`1 - 0.5*bias`, `1 + 0.5*bias`) applied to every zone's magnitude before the
final 0-100 clamp - at full-scale lateral G the loaded side reaches up to 1.5x its unbiased value
(clamped at 100) and the unloaded side drops to 0.5x.

**Availability**: `LongitudinalG` missing on the new frame -> all 8 channels null (the position
parameter cannot be computed at all without it). `LateralG` missing -> the model still produces real,
non-null values with a neutral (unbiased) left/right split, since the brief's PUBLISH list does not
gate the whole frame on the lateral channel and the longitudinal logic doesn't need it. `Old` frame or
`Dt` missing (e.g. the very first sample of a session) -> the rate term falls back to 0 for that one
frame only (magnitude-only for that frame), not nulled.

## Chosen fixed maxima and justification

- **FixedDecelMaxG = 2.0g.** Per the brief's own guidance ("even an F1 car's hardest braking should
  hit maximum quickly"): 2.0g comfortably covers hard-braking road/GT/formula content in typical sim
  titles while leaving enough headroom that the meter isn't permanently pegged at 100 for merely firm
  braking.
- **FixedAccelMaxG = 0.9g.** Deliberately lower than the braking default: sustained acceleration-g is
  physically smaller than braking-g for almost all vehicles in typical sim content (acceleration is
  power/traction-limited; braking additionally benefits from aerodynamic downforce at speed). Using
  the same ceiling for both axes would make acceleration feel numb by comparison; 0.9g lets a strong
  launch or mid-corner power-down clearly reach toward the top of the scale.
- **LateralReferenceG = 1.6g** (engine-internal, not owner-configurable): the brief's Settings section
  only asks for accel/decel maxima, not a lateral one, so this is a fixed internal constant rather than
  a FIXED/AUTO setting - 1.6g is a reasonable reference spanning road cars through GT3-class content.

## AUTO learning and outlier rejection (per game AND per car)

`GForceMaxLearner` keys on `(gameId, carId)` (via `GameData.GameName` / `StatusDataBase.CarId`, per
above). A candidate reading above the current learned max is **not** accepted immediately - it is
held as a "pending candidate" and only promoted once a **second**, similar reading (within 15% of the
first) arrives within the very next observation. A single one-frame spike (the brief's own 19.9g
collision-spike cautionary tale) sets a pending candidate that is simply discarded the next time an
ordinary reading arrives, so it never becomes the learned reference; two consecutive similar high
readings (a genuinely sustained event) ARE learned. FIXED mode never reads the learner at all (see
`GForceSettings.EffectiveAccelMaxG`/`EffectiveDecelMaxG`) - "ignoring learned values" is structural,
not a runtime check that could be bypassed.

## Recommended frequency range

**CORRECTION (docs\raw-gap-and-pad-balance-report.md, superseding the default stated below):** the
300 Hz -> 20 Hz default this section originally described was revised TWICE after real seat-time
feedback, not theory: first down to 50 Hz -> 20 Hz (300 Hz at the low end read too harsh/thin), then -
after further seat time found 20 Hz too weak to shake strongly enough - to the current **100 Hz ->
50 Hz**. Everything else in this section (data-only, clamped to device capability, no effect on any
computed output) is unchanged and still accurate.

`GForceSettings.RecommendedFromHz` / `RecommendedToHz` defaulted to **300 Hz -> 20 Hz** at the time
this report was written - the owner's own example verbatim (value near 0 -> the high/subtle end
(300 Hz), value 100 -> the low/punchy end (20 Hz)), both properties clamped to the device's declared
10-300 Hz capability (`GForceSettings.DeviceMinHz`/`DeviceMaxHz`), and `RecommendedToHz` additionally
clamped to never exceed `RecommendedFromHz` (so the convention can't be silently inverted by a bad UI
value). This is exposed purely as data for the settings UI to display as a recommendation - it does
not drive anything inside `Core/GForce` itself.

## What still needs wiring (explicitly NOT done here, per file ownership)

1. **QAdvanceFeedback.cs (Init/DataUpdate)**: AttachDelegate the 8 `GForcePublishedNames.AllNames()`
   properties (mirroring the existing Lock/Slip pattern), call `GForceEngine.Compute` once per frame
   with `GForceSettings.EffectiveAccelMaxG(gameId, carId)` / `EffectiveDecelMaxG(gameId, carId)`, and
   call `GForceSettings.SetCurrentGameAndCar` + `ObserveAccelG`/`ObserveDecelG` (feeding
   `Math.Max(0, longG)` / `Math.Max(0, -longG)` respectively) once per frame. `QAdvanceFeedback.cs` is
   outside this task's file ownership, so this integration is not implemented.
2. **Persistence**: `GForceSettings` has no save/load. The brief says a later task wires settings in
   and owns the config/persistence store; I did not build a second one. Note the parallel task appears
   to already have `ConfigStore.cs`/`RuntimeStore.cs`/`QAdvanceFeedbackSettings.cs` in progress in the
   same csproj (observed via the shared test project's `<Compile>` list) - `GForceSettings` was
   deliberately kept as a plain POCO-shaped class (no SimHub/Newtonsoft dependency of its own) so it
   should be a straightforward property/field to add to whatever root settings container that task
   builds, without QAdvanceFeedback.Core.GForce needing to know about it.
3. **Settings UI**: not built, per the brief.
4. **Session-reset hook**: `GForceSettings.ResetLearning()` exists (clears both learners) but nothing
   calls it yet - the equivalent of `SimHubTelemetryAdapter.Reset()`'s game-switch handling in
   `QAdvanceFeedback.cs` should probably call it too, so a game switch doesn't carry stale learned
   maxima into an unrelated car. Left as a clean, unused seam rather than guessed at.

## Mutation evidence (mandatory)

All three were applied directly to `Core/GForce/GForceEngine.cs` / `GForceMaxLearner.cs`, tests run,
then reverted exactly (confirmed via `diff` against a byte-for-byte backup copy taken before each
mutation - all three diffs came back empty after reverting; full 124-test suite re-run green and the
Release build re-verified 0-warning/single-DLL after all three reverts).

| Mutation | Change | Result |
|---|---|---|
| (a) missing G-force outputs 0 instead of null | `if (!longG.HasValue) return GForceOutput.Empty;` -> `if (!longG.HasValue) longG = 0.0;` | **1 test failed**: `GForceEngineTests.Missing_longitudinal_G_produces_null_on_every_channel_even_with_lateral_present` |
| (b) drop the rate-of-change term (magnitude only) | `brakeTravel`/`accelTravel` computed from `brakeIntensity`/`accelIntensity` alone, no `RateGain * rateNorm` term | **4 tests failed**: `Rising_braking_G_pushes_Bottom_Front_above_what_magnitude_alone_would_give`, `Steady_low_level_acceleration_starts_at_Bottom_Rear`, `Gentle_easing_recedes_less_than_a_fast_drop_at_the_same_magnitude`, `Falling_braking_G_recedes_all_the_way_to_Back_Low_despite_real_G_still_present` |
| (c) AUTO learning accepts a single-frame spike (no confirmation) | `Observe` collapsed to `if (magnitude <= s.LearnedMax) return; s.LearnedMax = magnitude;` | **3 tests failed**: `GForceMaxLearnerTests.Sustained_readings_are_learned_after_the_confirmation_threshold`, `GForceMaxLearnerTests.A_single_isolated_spike_is_never_learned`, `GForceSettingsTests.Auto_mode_rejects_a_single_spike_exactly_like_the_underlying_learner` |

## Concerns / unverified assumptions

1. **LongitudinalG sign convention is an inference, not a documented fact** (same category as Layer
   2's GroundSpeed-unit inference). Assumed: positive = accelerating, negative = braking. Confirmed
   there is no adjacent `GameReaderCommon.xml` doc file and no embedded XML doc comments in the DLL
   (checked directly). If this is backwards in a real game, the fix is a single-line swap in
   `GForceEngine.Compute` (`brakeG`/`accelG` definitions) - the model, its continuity, and its
   rate-of-change behaviour are all unaffected either way, only which physical direction lights up.
2. **LateralG sign-to-side convention is likewise an assumption** (positive -> biases Right). Same
   single-line fix if backwards; tests are written around the *symmetry* of the bias (mirrored L/R
   under sign flip) rather than asserting a specific real-world "left" or "right", so they remain
   valid either way.
3. **LateralReferenceG (1.6g) is a judgment call**, not something the brief asked to be configurable -
   the brief's Settings section only lists accel/decel maxima. Flagged rather than silently invented
   as a hidden FIXED/AUTO setting that wasn't requested.
4. **The "missing G unavailable -> null" rule was interpreted as gating on LongitudinalG only**, not
   LateralG, since the 8 channels are fundamentally driven by the longitudinal travel/position
   parameter and lateral only modulates an already-computed value. This is a judgment call on an
   underspecified interaction between two independently-nullable fields, not a literal instruction -
   flagged rather than assumed silently correct.
5. **Nothing here has been run inside a live SimHub session, against a real game, or on the rumble
   hardware** - it cannot be, per the task constraints. All 42 new tests are pure, offline,
   hand-computed-expectation unit tests against `Core/GForce` and `Settings/GForceSettings.cs`
   directly. The actual runtime behaviour once wired into `QAdvanceFeedback.cs` and driving real
   shakers is **unverified**.
6. **The continuity test's bound (8.0 units per 0.01g step) was derived from the model's own worst-case
   analytic slope (~3.33/step near G-intensity=1 on the Back Top curve)**, with margin - it is a
   correctness check for *this* model's construction, not an external, independently-sourced
   tolerance. A future change to `RateGain`/`BackLowSustainFloor` that increases the steepest slope
   materially would need this bound re-derived, not just left in place.

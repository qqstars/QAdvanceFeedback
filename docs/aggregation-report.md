# Aggregation report — QAdvanceFeedback, wheel lock/slip Front/Rear/Left/Right/All aggregation

## STATUS

IMPLEMENTED. Replaced the generic p-norm/`GroupMode` aggregation (never exposed to settings, never
persisted, hard-coded `GroupMode.PNorm` at every call site) with the owner's physically-motivated
Max/Min axle blend + Front/Rear weight-transfer scheme, fully configurable per channel (Wheel Lock,
Wheel Slip independently), persisted, and included in "Restore all default settings".

**Tests:** 567 total (555 pre-existing + 12 net new — some existing tests were rewritten in place
rather than added, see "Test changes" below), 0 failed, 0 skipped, full build (`Private/` present).
**Clean-clone tests:** 523 total, 0 failed, 0 skipped, `Private/` absent (verified in an isolated
scratch copy of the source tree — see "Clean-clone verification" below; the working tree's own
`Private/` folder was never touched).
**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback.sln -c Debug` and
`dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj -c Release` both report `0 Warning(s), 0
Error(s)` for the full tree; the same two builds against the isolated clean-clone scratch copy also
report `0 Warning(s), 0 Error(s)`. `bin/Release/net48/` contains exactly `QAdvanceFeedback.dll` and
`QAdvanceFeedback.pdb` in both cases.

**Localisation:** every new UI-facing string (`Group.Aggregation`, `Aggregation.*` — 12 keys) exists
in both `StringTableEn.cs` and `StringTableZhHans.cs`; `LocalizationTests.
English_and_Simplified_Chinese_have_exactly_the_same_key_set` passes. The Chinese table was generated
via a Python script using `io.open(..., encoding='utf-8')`, escaping every non-ASCII character to
`\uXXXX` before insertion — never pasted as raw CJK through a bash heredoc (per this project's own
standing rule, previously violated and since fixed elsewhere in this project's history).

**Screenshots regenerated** (Wheel Lock and Wheel Slip tabs each gained a new "Wheel combination"
section between Sources and Output shaping; Wheel Slip's version additionally has a slip-floor
spinner) — `settings-wheel-lock.png`, `settings-wheel-slip.png` via the harness at
`C:\Users\qchen\AppData\Local\Temp\claude\...\scratchpad\qpreview`, kept out of the repo.
`settings-gforce.png`/`settings-general.png` untouched by this task (G-Force tab explicitly out of
scope, owned by a concurrently-running agent — see "Coordination" below).

---

## 1 — Why the old scheme had to go

`Aggregator` (pre-task) was a single p-norm (`p=2`, uniform weights), identical for Front/Rear/Left/
Right/All and identical for Wheel Lock and Wheel Slip. It was never wired to settings at all — every
call site (`LegacyWheelLockSlipEngine`, `NormalizedWheelLockSlipEngine`, `PropertyPublisher`'s
`Diag.Source.*`) constructed `new Aggregator(GroupMode.PNorm, 2.0, Corners.Uniform(1.0))` directly, a
placeholder the original doc comment explicitly flagged as such ("a placeholder until a settings layer
... exposes GroupMode/p/weights to the driver").

This ignores weight transfer, the dominant real effect a wheel-lock/slip cue should reflect: under
braking, load shifts forward, so the front wheels carry the grip and matter most; under power, the
driven wheels are the ones that spin. A symmetric p-norm cannot express either bias, let alone let a
driver tune it per car.

## 2 — The owner's scheme, and self-consistency check

As specified:

```
Front = Max(FL,FR)*wMax + Min(FL,FR)*wMin
Rear  = Max(RL,RR)*wMax + Min(RL,RR)*wMin
Left  = FL*wFront + RL*wRear
Right = FR*wFront + RR*wRear
All   = Front*wFront + Rear*wRear
```

**Interpretation confirmed self-consistent.** Two distinct blend operations are at work, not one:

- An **order-independent** "axle" blend (`Max`/`Min` of the pair) for Front/Rear — it does not matter
  which physical wheel on the axle is stronger.
- An **order-dependent** "side/car" blend (front-position value vs. rear-position value, always in that
  role) for Left/Right/All — front is always front. Left/Right use the RAW per-side wheel pair
  (`FL`/`RL`, `FR`/`RR`); All uses the AXLE-BLENDED Front/Rear from the first stage, not the four raw
  wheels directly — so a car-level reading already reflects the same front/rear bias driving
  Left/Right/Front/Rear, rather than being computed independently from them.

Both are simple weighted sums of continuous functions (`Math.Max`, `Math.Min`, and plain
multiplication/addition), so the whole pipeline is continuous end-to-end — see §4.

**A notable, load-bearing coincidence, confirmed not accidental:** the owner's four shipped numbers
happen to sum to exactly 1.0 within each pair, for both channels:

| Channel | wMax + wMin | wFront + wRear |
|---|---|---|
| Wheel Lock | 0.45 + 0.55 = 1.00 | 0.90 + 0.10 = 1.00 |
| Wheel Slip | 0.55 + 0.45 = 1.00 | 0.65 + 0.35 = 1.00 |

This is exactly what makes a **uniform four-wheel reading pass through the whole aggregation
unchanged** (`Uniform_four_wheel_input_passes_through_unchanged_for_lock_defaults`/`..._slip_defaults`
in `AggregatorTests`) — which matters concretely: this plugin's own Wheel Lock algorithm
(`SimpleBrakingLockAlgorithm`, Layer 3) is car-level with no per-wheel term at all, so all four wheels
already carry the identical value every frame; an aggregation scheme that distorted a uniform input
would silently invent per-wheel variation that was never there. `AggregationWeights` deliberately does
**not enforce** the sum-to-1 property, though (see §5) — it is a property of the shipped numbers, not a
constraint on the type.

## 3 — Configurability

Every one of the five numbers (`WMax`, `WMin`, `WFront`, `WRear`, `SlipFloorFactor`) is independently
configurable, per channel, exactly as instructed:

- `Settings.WheelChannelSettings` gained five public properties, each with a clamped setter
  (`AggregationWMax`/`WMin`/`WFront`/`WRear` clamp to `>= 0`; `SlipFloorFactor` clamps to `[0,1]`).
- `WheelChannelSettings.ToAggregationWeights()` bundles them into `Core.AggregationWeights` — the one
  conversion point every caller (Layer 3, Layer 4, `Diag.Source.*`) uses, so `Core` never needs a
  reference back to the Settings-layer POCO.
- `CreateLockDefaults()`/`CreateSlipDefaults()` stamp the owner's tested numbers for a fresh install;
  `QAdvanceFeedbackSettings.RestoreDefaults()` (the global "Restore all default settings" button) goes
  through the same path, so the shipped defaults and the restore button can never drift apart (existing
  convention in this codebase, unchanged).
- The settings UI gained a new, clearly-labelled "Wheel combination" `GroupBox` on both the Wheel Lock
  and Wheel Slip tabs (between Sources and Output shaping — the natural place in the pipeline: sources
  decide WHAT feeds each wheel, aggregation decides HOW the four wheels combine, the curve decides how
  the combined value FEELS). Wheel Slip's version additionally has the slip-floor spinner; Wheel Lock's
  does not (see §6).
- Every weight is read fresh from settings **every frame** (`AggregationWeights?` optional parameters
  threaded through `ILegacyWheelLockSlipEngine.Compute`/`NormalizedWheelLockSlipEngine.Compute`,
  mirroring the pre-existing `LegacyThresholds?` pattern), NOT baked into an engine's constructor — the
  owner's explicit "tune without a rebuild" requirement. `Aggregator` itself is now a static, stateless
  class (`Aggregator.Compute(Corners, AggregationWeights)`) rather than an instance wrapping one fixed
  set of weights, specifically so re-reading current settings every frame costs nothing (no
  per-frame allocation).

### Sum-to-1: decided NOT to auto-normalise

The brief asked me to decide whether Max/Min and Front/Rear pairs must sum to 1, and to make the choice
visible rather than silently rescaling. **Decision: do not enforce or auto-normalise.** Each of the
four weights is independently clamped to `>= 0` only. If a driver types weights that do not sum to 1
within a pair (e.g. `WMax=1.0, WMin=0.5`), the blend is **not** rescaled back to a convex combination —
the raw, larger (or smaller) result is what they get, bounded only by the final 0-100 clamp (verified in
`Weights_not_summing_to_one_are_not_silently_rescaled`). Reasoning: auto-normalising would silently
change what the driver typed into something else, defeating the entire "make every weight configurable
... they want to tune without a rebuild" ask — a driver who deliberately wants an amplified or
attenuated combined reading (not just a re-weighted blend) should get exactly that, not a "corrected"
version. The UI's help text explains this ("not forced to sum to 1... what you type is what you get").

`SlipFloorFactor` IS clamped, to `[0,1]` specifically (not just `>= 0`): it multiplies the strongest
participating wheel to produce a floor, and a factor above 1 would let the floor exceed every wheel's
own reading — an outright boost, not "never diluted away," which is not what this mechanism is for.

## 4 — Continuity (verified numerically)

A weighted Max/Min blend and a bare `Math.Max` are BOTH continuous in **value** as two inputs cross (no
jump — `Math.Max` is a genuinely continuous, if not differentiable, function of its arguments). The
property that actually matters for a shaker motor (and that the brief's "click" concern is really
about) is the **kink** at the crossover — the discontinuity in *slope*, which a felt "click" corresponds
to more directly than a value jump would.

Measured via the discrete second difference `f(b-h) + f(b+h) - 2*f(b)` at a crossover point `b`, which
equals `h * (slope_after - slope_before)`:

- **Weighted blend** (Slip's own shipped weights, `WMax=.55`/`WMin=.45`): kink = `h * |WMax - WMin|` =
  `h * 0.10` — small, and PROPORTIONAL to how far the two weights are from each other.
- **Bare Max**: kink = `h * |1 - 0|` = `h * 1.0` — a full order of magnitude larger, REGARDLESS of any
  weight (Max has no weights to speak of — its slope is always exactly 0 on the losing side and exactly
  1 on the winning side).

Both facts are asserted directly in `AggregatorTests` (`Axle_blend_has_a_small_bounded_kink_at_the_
crossover`, `Bare_Max_has_a_much_larger_kink_at_the_same_crossover`) — the second test recomputes what a
bare Max would give at the SAME three points and asserts it is `> 5x` the blend's own kink, so the
continuity guard is demonstrated to be discriminating, not just asserting an arbitrary small number.

## 5 — Bounds

Every input wheel value is clamped to 0-100 before either blend stage; every one of Front/Rear/Left/
Right/All is clamped to 0-100 again after both blend stages AND the floor. This is what actually
guarantees the 0-100 bound for ANY non-negative weight, however large — not a fragile invariant on the
weights themselves (verified in `Output_is_clamped_to_0_100_under_extreme_weights`, weights of 50 each
plus a floor factor of 1.0 still produce exactly 100 for a uniform 100 input) and
`Zero_weights_produce_a_bounded_zero_result_not_a_crash` (all weights 0 → result 0, not NaN/exception).

## 6 — The slip floor, and why Wheel Lock does not need an equivalent

**Mechanism:** applied independently to All/Front/Rear/Left/Right, after both blend stages:
`result = Max(result, Max(participating wheels) * SlipFloorFactor)`. "Participating wheels" is all four
for All, the axle pair for Front/Rear, the side pair for Left/Right. The mechanism is entirely generic —
it fires whenever `SlipFloorFactor > 0`, on EITHER channel; only the shipped defaults (`0.0` for Lock,
`0.4` for Slip) make it Slip-only in practice. This was a deliberate design choice: keeping the
mechanism generic (rather than hard-coding "only Slip has this code path") means a driver whose car
disagrees with the "Lock doesn't need it" finding below can still enable it for Lock, without a
rebuild — just set Lock's own `SlipFloorFactor` above 0 in a hand-edited config (the UI does not expose
a control for it on the Lock tab, by design — see below).

**Finding: Wheel Lock does NOT need an equivalent floor, verified two ways:**

1. **Analytically.** Lock's own shipped `WMax` (0.45) is actually SMALLER than `WMin` (0.55) — the
   axle blend weights the AXLE'S WEAKER wheel slightly MORE than the stronger one. Combined with Lock's
   extreme front bias (`WFront=0.90`), a single locked FRONT wheel already reads strongly at the car
   level without any floor (`Lock_never_applies_a_floor_by_default`: FL=100, others=0 → `Front = 45.0`,
   `All` inherits most of that through the 0.90 front weight). A floor would only meaningfully change
   the result for a lone REAR spinner/locker under Lock's weights — which leads to the second reason:
2. **Physically.** This plugin's actual Wheel Lock algorithm (`SimpleBrakingLockAlgorithm`, Layer 3,
   `Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs`) is CAR-LEVEL with no per-wheel term at all —
   all four wheels already receive the identical value every frame (see
   `LegacyWheelLockSlipEngineTests.Lock_is_uniform_across_all_four_wheels_and_every_aggregate`). A
   "single wheel locked, others free" scenario the floor exists to rescue essentially never occurs for
   THIS engine's own Lock output today. Power slip, by contrast, genuinely is per-wheel (an open
   differential routinely spins ONE driven wheel while the other has traction) — exactly the scenario
   the floor targets. The mechanism remains available (not hard-coded away) in case a different Lock
   implementation, or a driver's own experimentation, ever needs it, but ships disabled by default with
   this documented reasoning, not by omission.

Both the axle-level and car-level engagement points are exercised end-to-end (not just at the
`Aggregator` unit level) in `Private\QAdvanceFeedback.Tests\LegacyWheelLockSlipEngineTests.cs`'s
`Slip_floor_engages_through_the_real_Raw_tier_engine_for_a_single_spinning_wheel` test, cross-checking
the real `LegacyWheelLockSlipEngine.Compute` output against a direct `Aggregator.Compute` call on the
same per-wheel values.

## 7 — `GroupMode`: retired, not kept as an escape hatch

**Decision: retire `GroupMode` entirely** (the enum, and the old `Pair`/`Quad` instance API) rather than
keep it alongside the new scheme.

Justification:

- **It was never a real, driver-facing feature to begin with.** `GroupMode` was never exposed in the
  settings UI and never persisted to `WheelChannelSettings`/`QAdvanceFeedbackSettings` — every one of
  its three call sites hard-coded `GroupMode.PNorm` at construction. There is therefore **no persisted
  driver setting to migrate** — nothing a real config file could have been relying on.
- **Keeping it would add confusion for no benefit.** A settings surface offering "use the owner's
  physically-motivated scheme" alongside "or fall back to a generic p-norm nobody could previously
  configure or even discover" invites exactly the "which one is actually in effect" confusion the task
  asked me to avoid, for an escape hatch nobody has ever used (it was never reachable from the UI).
- **The new scheme's own generality already covers the old modes' useful cases.** `AggregationWeights`
  with `WMax=WMin=0.5, WFront=WRear=0.5` reduces both blend stages to a plain mean (roughly the spirit
  of the old uniform-weight p-norm/mean default) — a driver who wants "just average them" still can,
  through the SAME configurable weights, without a second parallel enum.

No migration code was needed as a result (see above — nothing was ever persisted). This IS still a
behaviour change worth calling out explicitly: any code that constructed `new Aggregator(GroupMode.X,
p, weights)` directly (there was no such call site outside this plugin's own three internal ones, all
updated in this task) would need updating to `Aggregator.Compute(wheels, AggregationWeights)` instead.

## 8 — Raw tier, and confirmation Normalized/Projected inherit it

**Confirmed still true.** Aggregation happens at the Raw tier
(`Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs`, Layer 3) exactly as before, now calling
`Aggregator.Compute` with this frame's `lockAggregation`/`slipAggregation` parameters (defaulting to
`AggregationWeights.LockDefaults`/`SlipDefaults`) instead of the retired `_aggregator.Pair`/`.Quad`
calls.

`NormalizedWheelLockSlipEngine` (Layer 4) "inherits" the scheme in the sense that matters: it calls the
SAME `Aggregator.Compute` with the SAME weights (also threaded through as fresh per-call parameters),
but on ITS OWN per-wheel output (`ComputeChannel`'s severity-redistributed values), not by reusing Raw's
own aggregate numbers directly — this was already the pre-existing architecture (Normalized computes
its own Front/Rear/Left/Right/All from its own per-wheel Corners) and is unchanged by this task, other
than swapping which aggregation formula is used. Verified in `NormalizedWheelLockSlipEngineTests.
Aggregates_are_produced_by_the_same_owner_configured_scheme_layer_3_uses` (cross-checks Normalized's
`LockAll` against a direct `Aggregator.Compute` call on Normalized's own `LockWheels`) and
`Aggregation_weights_are_reread_every_call_with_no_engine_rebuild_needed` (passing different weights on
two calls to the same already-constructed engine instance changes the result — the "tune without a
rebuild" property, demonstrated at the Normalized tier specifically).

`ProjectedWheelLockSlipEngine` (Layer 5) does **not** aggregate at all — it never did. It curve-shapes
and pulses each of the nine already-computed Normalized values (the four wheels plus Front/Rear/Left/
Right/All) independently; there is nothing for this task to touch there, and nothing changed in that
file.

`PropertyPublisher`'s `Diag.Source.*` (the diagnostic readout of what Layer 4 actually consumed this
frame) was also updated to use `Aggregator.Compute` with the caller-supplied per-channel weights
(previously its own separate, hard-coded `_sourceAggregator` field) — `QAdvanceFeedback.cs`'s
`DataUpdate` now computes `lockAggregation`/`slipAggregation` once per frame and passes them to all
three of `_legacyEngine.Compute`, `_normalizedEngine.Compute`, and `_publisher.UpdateSource`, so the
diagnostic view and the two real tiers can never silently disagree about which weights were in effect.

## 9 — Settings round-trip / a config file predating this feature

`WheelChannelSettings`'s five new properties have bare field initialisers set to the NEUTRAL fallback
(`AggregationWeights.Neutral` = 0.5/0.5/0.5/0.5/0.0 — a plain mean, no floor) rather than either
channel's real numbers, since a single shared POCO cannot give two different field-initialiser defaults
for the same property (`CreateLockDefaults()`/`CreateSlipDefaults()` are what actually stamp the correct
per-channel numbers, exactly like the pre-existing `BrakeThresholdPercent`/`ThrottleThresholdPercent`).

**This neutral fallback is, in practice, never actually observed by `ConfigStore.Load`, even for a
config file that predates this feature entirely.** `QAdvanceFeedbackSettings.Lock`/`.Slip` are
themselves field-initialised to `CreateLockDefaults()`/`CreateSlipDefaults()` (non-null, already
channel-correct objects), and Newtonsoft's default object-population behaviour
(`ObjectCreationHandling.Auto`) REUSES an existing non-null property value instead of replacing it with
a bare `WheelChannelSettings()` — so deserialising a `Lock`/`Slip` JSON object that is missing these
five keys only overwrites whatever properties ARE present, leaving every absent one (these five
included) at whatever `CreateLockDefaults()`/`CreateSlipDefaults()` already set. Verified directly in
`ConfigStoreTests.A_config_file_saved_before_this_feature_existed_still_loads_this_channels_real_
aggregation_defaults` (a hand-written JSON file containing only `Lock.SourceFrontLeft`, nothing about
aggregation, still loads `Lock.AggregationWMax == 0.45`, not the neutral `0.5`). The neutral fallback is
therefore only ever actually observed from a genuinely bare `new WheelChannelSettings()` (a driver's own
code, or the settings UI's per-source-mode scratch objects) — never from `ConfigStore.Load`.

Round-trip (`Save` then `Load`) and "Restore all default settings" are both covered end-to-end in
`ConfigStoreTests`/`WheelChannelSettingsTests`.

## 10 — Test changes

New: `AggregatorTests.cs` rewritten from scratch (hand-computed formula tests for both channels, floor
engagement/non-engagement, the kink-based continuity proof and its bare-Max counter-proof, bounds under
extreme/zero weights, the sum-to-1 non-enforcement, `AggregationWeights`' own bounds enforcement).
`WheelChannelSettingsTests.cs`/`ConfigStoreTests.cs` gained aggregation-specific defaults/clamping/
round-trip/restore coverage. `NormalizedWheelLockSlipEngineTests.cs` gained the "same scheme, reread
every call" coverage described in §8.
`Private\QAdvanceFeedback.Tests\LegacyWheelLockSlipEngineTests.cs`'s Slip aggregation test was
recomputed by hand against the new formula (the old p-norm expectation, `26.352314`, is superseded by
the new formula's `25.833333` — both hand-derivable, only the formula changed) and a new end-to-end
floor-engagement test was added there.

## 11 — Mutation evidence

Performed against the real production files (`Core/AggregationWeights.cs`, `Core/Aggregator.cs`),
reverted immediately after each observation — never left in the tree.

**(a) Swap `WMax`/`WMin` in `AggregationWeights.LockDefaults`** (`0.45, 0.55, ...` → `0.55, 0.45, ...`):
**6 tests failed**, all hand-computed against the correct (unswapped) numbers —
`AggregatorTests.Lock_defaults_match_the_owners_tested_values_exactly`,
`WheelChannelSettingsTests.Lock_defaults_ship_the_owners_tested_aggregation_weights_exactly`,
`WheelChannelSettingsTests.RestoreDefaults_restores_the_owners_aggregation_weights_after_being_
customised`, `ConfigStoreTests.RestoreDefaults_restores_the_owners_aggregation_weights_through_the_
full_settings_object`, `ConfigStoreTests.A_config_file_saved_before_this_feature_existed_still_loads_
this_channels_real_aggregation_defaults`, `ConfigStoreTests.Missing_file_yields_the_owners_aggregation_
defaults_for_both_channels`.

**(b) Remove the slip-floor stage entirely from `Aggregator.Compute`:** **1 test failed** (the
single-spinning-wheel test, exactly as required) —
`AggregatorTests.Slip_floor_engages_for_a_single_strongly_spinning_wheel` (expected `40` — the floored
value — got `35`, the un-floored blend).

**(c) Replace `AxleBlend` with a bare `Math.Max(a, b)`:** **8 tests failed**, including the dedicated
continuity guard — `AggregatorTests.Axle_blend_has_a_small_bounded_kink_at_the_crossover` (expected kink
`0.10`, got `1` — exactly the bare-Max kink predicted in §4), plus every hand-computed formula test that
depends on the actual weighted blend (`Lock_formula_matches_hand_computation_for_a_known_input_set`,
`Slip_formula_matches_hand_computation_including_the_floor_when_it_does_not_engage`,
`Lock_never_applies_a_floor_by_default`, `Slip_floor_engages_for_a_single_strongly_spinning_wheel`,
`Input_out_of_0_100_range_is_clamped_before_blending`, `Weights_not_summing_to_one_are_not_silently_
rescaled`, and `Bare_Max_has_a_much_larger_kink_at_the_same_crossover`, which fails because with the
mutation applied the "blend" IS the bare Max, so its own kink is no longer smaller than itself).

All three mutations were reverted immediately after capturing the failures above; the full suite
(567 tests, `Private/` present) passes again with the reverted code, confirmed by re-running the full
build and test suite after each revert.

## 12 — Clean-clone verification

The working tree's own `Private/` folder was **never moved, renamed, or deleted** (an explicit ask from
the task, and separately blocked by the harness's own safety policy as a shared-resource risk while
another agent works concurrently in the same repository). Instead, the two project folders
(`QAdvanceFeedback/`, `QAdvanceFeedback.Tests/`) and the `.sln` were copied (via `tar`, excluding
`bin/obj/.vs`) into an isolated scratch directory that never had a `Private/` sibling at all, simulating
a genuine fresh clone. Built there (`-p:SimHubRefPath=...` pointed at the real `lib/` folder) in both
Debug and Release: `0 Warning(s), 0 Error(s)` in both configurations; `bin/Release/net48/` contains
exactly `QAdvanceFeedback.dll` + `.pdb`; the test project (`523` tests) passes in full, using
`InertLegacyWheelLockSlipEngine`'s new (extra-optional-parameter) signature, confirming the public
fallback compiles and behaves correctly independent of `Private/`.

## 13 — Coordination note

Per the brief, a second agent was reported to still be finishing shake-frequency/intensity work
touching `Settings\GForceSettings.cs`, the G-Force tab of `Settings\SettingsControl.xaml(.cs)`, the
string tables, and `docs\images\`. This task's own edits to `Settings\SettingsControl.xaml(.cs)` and
the string tables were confined to the Wheel Lock/Wheel Slip tabs' new "Wheel combination" sections and
the corresponding `Group.Aggregation`/`Aggregation.*` keys — no line touched by this task overlaps the
G-Force tab or any G-Force-prefixed string key. No merge conflict was encountered while editing; if the
other agent's changes land afterwards, the two sets of edits should not intersect.

## 14 — Concerns / open questions

- **Sum-to-1 is a property of the shipped numbers, not an enforced invariant.** A driver who edits
  weights without understanding this could produce an aggregation that reads persistently near 0 or
  saturates near 100 across ordinary driving. The UI's help text says so explicitly, but there is no
  live warning/preview in the settings control if a pair drifts far from 1 (matching the existing
  curve-editor convention of no other cross-field validation warnings either).
- **The neutral (0.5/0.5/0.5/0.5/0.0) fallback is only theoretically reachable** via `ConfigStore.Load`
  (see §9) — in practice it is only ever hit from a genuinely bare `new WheelChannelSettings()`. This is
  a subtle enough mechanism (relying on Newtonsoft's `ObjectCreationHandling.Auto` default) that a future
  change to `QAdvanceFeedbackSettings.Lock`/`.Slip`'s own field initialisers (e.g. if someone later
  "simplifies" them to a bare `new WheelChannelSettings()`) would silently reintroduce the gap this
  report documents as currently absent. Flagged here so a future change to those two field initialisers
  is made with this dependency in mind.
- **Real-world feel is unverified**, as with every other change in this project's history — nothing here
  has run inside a live SimHub session or on real rumble hardware; the owner's own seat-time testing is
  what produced the shipped numbers, not this implementation.

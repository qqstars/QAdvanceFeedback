# Mid-chain clamp fix (both channels)

Fixes the clamp-then-reaggregate defect diagnosed in
`QAdvanceFeedback_analysis_shakeit_overshake\shakeit-overshake-analysis.md`: the published
Front/Rear/Left/Right group channels (Lock) and every published Slip channel including `All` were
built by re-aggregating already-scaled-**and-clamped** per-wheel values, silently discarding
headroom whenever a single wheel's scaled native reading exceeded 100 before the axle/side blend
ever saw it. Lock's `All` was already protected (the pre-existing "Defect A" `:1041` override); this
task closes the same gap for Lock's Front/Rear/Left/Right and for every Slip channel.

## What changed, and where

`QAdvanceFeedback\Core\Normalized\NormalizedWheelLockSlipEngine.cs`:

- **`ComputeChannel`** (private): added an `out WheelAggregate nativeAggregate` parameter. Every
  return path now populates it so that `nativeAggregate.X * allScale` (after ONE final clamp)
  reproduces that path's own published value:
  - `!triggered` / `!engaged`: all-zero `WheelAggregate`, `allScale` stays its neutral 1.0 default
    (out of scope, no-op).
  - `motion.Level == Unavailable` (the `KeyedScaleLearner.Rescale` degradation-floor path, deliberately
    untouched calibration machinery): `nativeAggregate = Aggregator.Compute(rescaled Corners, weights)`,
    `allScale` stays 1.0 - reproduces this branch's own numbers bit-for-bit (out of scope).
  - No-raw-differentiation branch (`proportionMean <= NoRawSignalEpsilon`): `nativeAggregate =
    Aggregator.Compute(Corners of 4x severity, weights)` - since every Aggregator weight pair sums to
    1, this equals `severity` on every field, `allScale` stays 1.0 (no-op, matches today).
  - **Slip's own real scaling path** (`!useAggregatedAllScale`, the flat-mean-relative proportion,
    `severity * (bw_i / flatMean(bw))`, formula UNCHANGED): now also sets `allScale =
    ClampMath.SafeDiv(severity, proportionMean, 1.0)` - the SAME uniform multiplier this formula
    already applies to all four wheels, simply surfaced for the first time - and `nativeAggregate =
    Aggregator.Compute(new Corners(bw0..bw3), weights)` (native, pre-scale).
  - **Lock's own real scaling path** (`useAggregatedAllScale`): `aggregatedNativeConfigured`/
    `aggregatedNativeFallback` now capture the FULL `WheelAggregate` (not just `.All` as before);
    Front/Rear/Left/Right are blended by `fallbackWeight` the SAME way `blendedNativeAggregate`/`.All`
    already is (matching the analysis doc's own Fix 1 blueprint), then packed into `nativeAggregate`
    alongside `blendedNativeAggregate` as `.All`. `allScale` is unchanged
    (`severity / blendedNativeAggregate`).
- **`Compute`** (public): replaced `Aggregator.Compute(lockWheels/slipWheels, weights)` with
  `new WheelAggregate(ClampMath.To0100(nativeAggregate.Front * allScale), ..., ClampMath.To0100(nativeAggregate.All * allScale))`
  for both channels - ONE clamp, at the very end, applied to a value that already reflects the FULL
  scale. New instance fields `_lockNativeAggregate`/`_slipNativeAggregate` carry the `out` value
  between the `ComputeChannel` calls and this construction, mirroring the existing
  `_lockAllScale`/`_slipAllScale` fields.
- The pre-existing Lock `:1041` override (`if (LockUsesAggregatedAllScale) lockAggregate = ...` with
  `All` replaced by `ClampMath.To0100(_lockCarLevelSeverity)`) is **kept, not removed** - see below.
- `LockAllScale`/`SlipAllScale` XML docs updated: `SlipAllScale` is no longer pinned to 1.0 just
  because `SlipUsesAggregatedAllScale` is false - it now surfaces Slip's own real multiplier.
- No aggregation weight, curve anchor, output value, flatten range, or severity formula changed. Both
  `LockUsesAggregatedAllScale = true` and `SlipUsesAggregatedAllScale = false` are untouched
  (compile-time constants, not flipped).

**Test re-expressed**: `QAdvanceFeedback.Tests\NormalizedWheelLockSlipEngineTests.cs`,
`Aggregates_are_produced_by_the_same_owner_configured_scheme_layer_3_uses` pinned the exact old
clamped values for Lock Front/Rear/Left/Right (`raw=(80,20,20,20)` - Front was capped at 82.379545).
Re-expressed to independently reconstruct the expected value via `Aggregator.Compute(raw,
weights).Front * engine.LockAllScale`, plus a regression guard that Front now exceeds 90 (up from the
old clamped 82.38). No assertion was weakened or deleted; the one genuinely-old-behavior-pinning
assertion was replaced with an equivalent one that asserts the NEW, documented formula.

**Full diff scope** (confirmed via `diff -rq` against the pre-task snapshot): exactly two files
differ from `QAdvanceFeedback_PRETASK_SNAPSHOT_clampfix` - `NormalizedWheelLockSlipEngine.cs` (the
fix) and `NormalizedWheelLockSlipEngineTests.cs` (the one re-expressed test). Nothing else changed.

## Did the `:1041` override become redundant?

**No - kept, deliberately.** `nativeAggregate.All` for Lock is now DEFINED to equal
`blendedNativeAggregate`, the exact same quantity `allScale` was divided FROM
(`allScale = severity / blendedNativeAggregate`). So `nativeAggregate.All * allScale` is the algebraic
identity `blendedNativeAggregate * (severity / blendedNativeAggregate)`, which equals `severity`
whenever `blendedNativeAggregate != 0` - true for the overwhelming majority of frames. The override is
kept anyway because it is not **provably** redundant: (1) `ClampMath.SafeDiv`'s own fallback covers
`blendedNativeAggregate == 0` exactly, where `allScale` falls back to its neutral 1.0 instead of being
derived from `severity` at all - `nativeAggregate.All * allScale` would then read 0, not `severity`;
(2) `x * (y/x) == y` is a real-number identity, not a floating-point one - IEEE754 rounding can leave
the two sides a few ULPs apart. The override makes "`Lock.All == carLevelSeverity`, always, exactly"
hold by construction, not by (overwhelmingly likely but not perfectly guaranteed) arithmetic
coincidence. Measured on all 108,233 replayed frames (see below): `max|LockAll -
LockCarLevelSeverity| = 0.0000000000` - exact, every frame.

## Measurement method

Reused `QAdvanceFeedback_analysis_shakeit_overshake\harness\Shared\Program.cs`, copied into this
session's scratchpad (`clampfix-harness\`, untouched analysis folder) and extended with
Front/Rear/Left/Right and `LockCarLevelSeverity` output columns (the original only logged per-wheel
FL/FR/RL/RR and `All`). Built twice against pre-built product DLLs only (no product source rebuilt by
the harness itself):

- **BEFORE** = `QAdvanceFeedback_PRETASK_SNAPSHOT_clampfix\...\QAdvanceFeedback.dll` (pre-task
  snapshot, verified clean-building, 994/994 tests, before any edit).
- **AFTER** = `QAdvanceFeedback\...\QAdvanceFeedback.dll` (this task's fixed build).

Both replays consumed identical input rows: **108,233 frames each** (determinism check passed,
matching the original analysis exactly). All 14 usable logs, same exclusions as the brief and the
original analysis (`old-logs\*` excluded; `I_1_6_8\Raw` restart dropped 3,612/10,499 rows -> 6,887
kept; `Common\ShakeIt\Dry` restart dropped 3,196/6,952 -> 3,756 kept; `I_1_6_5\ShakeIt` additionally
dropped 6 rows - all confirmed byte-identical to the analysis doc's own `drop_notes`).

## Results

### 1. Clamp saturation rate (per-wheel, any of FL/FR/RL/RR published at exactly/over 100.0)

Using the analysis doc's own "active" definition (published `*All` > 1.0), pooled Red Bull/Dry:

| Channel | Source | n active | saturated | rate BEFORE | rate AFTER |
|---|---|---|---|---|---|
| Lock | Raw | 2,404 | 776 | **32.3%** | **32.3%** (unchanged) |
| Lock | ShakeIt | 2,373 | 1,378 | **58.1%** | **58.1%** (unchanged) |
| Slip | Raw | 11,751 | 1,268 | 10.8% | 10.8% (unchanged) |
| Slip | ShakeIt | 16,927 | 2,568 | 15.2% | 15.2% (unchanged) |

These figures reproduce the analysis doc's own headline Lock numbers (32.3%/58.1%) exactly, and are
**identical before and after** by construction - the fix never touches the per-wheel clamp itself
(unchanged formula, unchanged output), only how the aggregates are built from it. This confirms
"the mid-chain clamp no longer affects the published aggregates": the clamp still visibly engages at
the same per-wheel rate, but its old side effect of silently capping Front/Rear/Left/Right/Slip-All
before the axle/side blend is gone.

### 2. Published group channel distributions (median/p90/p99/max), active frames, pooled Red Bull/Dry

**Lock** (Front/Rear/Left/Right - median unchanged where the axle/side blend already saturated at the
p90+ tail only; RISES at p90/median wherever clamping was biting the median range):

| Field | Source | BEFORE med/p90/p99/max | AFTER med/p90/p99/max |
|---|---|---|---|
| Front | Raw | 33.12 / 94.49 / 100.00 / 100.00 | 33.12 / **96.21** / 100.00 / 100.00 |
| Front | ShakeIt | 56.10 / 81.63 / 100.00 / 100.00 | 56.10 / **82.63** / 100.00 / 100.00 |
| Rear | Raw | 25.87 / 100.00 / 100.00 / 100.00 | unchanged (already saturated at p90) |
| Rear | ShakeIt | 73.44 / 100.00 / 100.00 / 100.00 | unchanged (already saturated at p90) |
| Left | Raw | 34.63 / 86.74 / 99.98 / 100.00 | **40.55** / 86.74 / 99.98 / 100.00 |
| Left | ShakeIt | 56.58 / 89.52 / 100.00 / 100.00 | **59.45** / **94.77** / 100.00 / 100.00 |
| Right | Raw | 38.28 / 91.23 / 99.70 / 100.00 | **44.70** / **94.12** / **99.99** / 100.00 |
| Right | ShakeIt | 56.53 / 70.17 / 100.00 / 100.00 | **57.55** / **76.21** / 100.00 / 100.00 |
| All | Raw/ShakeIt | unchanged (pre-existing `:1041` override, unaffected by this task) | unchanged |

Lock's Left/Right medians rise 2.9-6.4 points; p90s rise 1.0-6.0 points. Lock's Front/Rear/All show
smaller or no median movement in this pooled sample (mostly p90+-tail effects) since Front/Rear's own
WMax/WMin=0.75/0.25 axle weighting already keeps the stronger wheel dominant even before the fix.

**Slip** (every field, including `All` - previously had NO protection anywhere):

| Field | Source | BEFORE med/p90/p99/max | AFTER med/p90/p99/max |
|---|---|---|---|
| Front | Raw/ShakeIt | unchanged | unchanged (this scenario's Front never saturates) |
| Rear | Raw/ShakeIt | unchanged | unchanged (p99/max already 100 in this pooled sample) |
| Left | Raw | 0.65 / 11.62 / **70.00** / **70.00** | 0.65 / 11.62 / **80.18** / **100.00** |
| Left | ShakeIt | 4.51 / 41.21 / **70.00** / **70.00** | 4.51 / 41.21 / **100.00** / **100.00** |
| Right | Raw | 0.69 / 11.95 / **70.00** / **70.00** | 0.69 / 11.95 / **81.98** / **100.00** |
| Right | ShakeIt | 4.52 / 41.35 / **70.00** / **70.00** | 4.52 / 41.35 / **96.59** / **100.00** |
| All | Raw | 0.75 / 12.50 / **70.00** / **70.00** | 0.75 / 12.50 / **92.99** / **100.00** |
| All | ShakeIt | 4.69 / 41.61 / **70.00** / **70.00** | 4.69 / 41.61 / **100.00** / **100.00** |

**This is the fix's biggest, clearest win.** Before the fix, Slip's Left/Right/All were pinned at
**exactly 70.0** (the `SlipFloorFactor` value) at p99 and max in every pooled Red Bull/Dry
comparison - the pre-existing "floor pins published All at exactly 70.0 regardless of severity" defect
described in `SlipUsesAggregatedAllScale`'s own remarks. After the fix, these same fields reach
80-100 at p99 and 100 at max, tracking severity properly instead of being artificially capped. Medians
are unchanged (the floor/clamp interaction only bites in the upper tail, where severity is already
high) - an honest finding, not overstated.

### 3. ShakeIt-versus-Raw gap, pooled Red Bull/Dry (median ratio, same car/track)

| Field | BEFORE gap/ratio | AFTER gap/ratio | Narrowed by |
|---|---|---|---|
| Lock.Front | +22.98 / 1.69x | +22.98 / 1.69x | 0 (median unaffected here) |
| Lock.Rear | +47.57 / 2.84x | +47.57 / 2.84x | 0 |
| Lock.Left | +21.95 / 1.63x | +18.89 / 1.47x | **3.06 points** |
| Lock.Right | +18.26 / 1.48x | +12.85 / 1.29x | **5.40 points** |
| Lock.All | +11.06 / 1.23x | +11.06 / 1.23x | 0 (pre-existing override, unaffected) |
| Slip.Front/Rear/Left/Right/All | various | same at the median | 0 at median (effect is in the tail - see Section 2) |

**Honest read**: at the MEDIAN, the ShakeIt-vs-Raw gap barely moves for most fields (Lock.Left/Right
being the exception, narrowing by 3-5 points) - the per-wheel clamp mostly bites in the upper tail
(p90+), not at typical mid-braking magnitudes, so a median-based gap metric understates the fix's
real effect. The MORE representative measure is Section 2's tail statistics: Slip's p99/max
ShakeIt-vs-Raw asymmetry (both previously pinned at the same 70.0 ceiling) is now resolved by
construction (both sources can now reach 100 when severity legitimately gets there), and Lock's own
per-wheel saturation asymmetry (58.1% ShakeIt vs 32.3% Raw, confirmed unchanged in Section 1) no
longer propagates into Front/Rear/Left/Right the way it used to. The fix does **not** claim to close
the underlying ShakeIt-vs-Raw dynamic-range gap (Candidate 3/4 in the analysis doc, explicitly out of
scope) - it only stops that gap from being *additionally amplified* by a second, avoidable
clamp-then-reaggregate step.

### 4. Endpoint verification and full-lock maxima

- **`LockAll == LockCarLevelSeverity`, exactly, on all 108,233 replayed frames**:
  `max|LockAll - LockCarLevelSeverity| = 0.0000000000`.
- **Full-lock maxima per log, before vs after** - no channel regressed (every delta is `>= 0`):
  Lock.All maxima are byte-identical in every log (protected by the kept override). Slip.All maxima
  RISE in 7 of 14 logs (the ones where the floor-pin defect was live) - e.g. `I_1_6_5/Raw` 70.00 ->
  100.00, `l_1_6_6/Raw` 70.00 -> 83.58, `Common/ShakeIt/Wet` 70.00 -> 100.00 - and are unchanged in the
  other 7 (where severity never approached the old 70.0 ceiling in that log to begin with). No log
  shows a Lock or Slip maximum decreasing.
- **Endpoint proxy** (native source reading >= 99.5, both builds): Lock's published `All` at these
  near-ceiling source frames has median 100.00 in both BEFORE and AFTER (169 matching frames) -
  unaffected, as expected (this fix does not touch the curve/Rescale/severity formulas). The dedicated
  endpoint unit tests (`NormalizePatternTests`, `S90FallbackRatioTests`,
  `DeltaGCollapseBandMappingTests`, etc.) all still pass unmodified.

### 5. Per-wheel ranking preserved

Not re-measured on the real logs (per-wheel Corners formulas are byte-for-byte unchanged by this
fix - the fix only changes how Front/Rear/Left/Right/All are built FROM those unchanged per-wheel
values), so ranking cannot have changed. The existing dedicated test
(`Per_wheel_ranking_is_preserved_under_the_uniform_scale_exactly_as_under_the_old_proportion`) still
passes unmodified, confirming this by construction: uniformly scaling four wheels by the same
non-negative constant cannot change their relative order, and this task never introduces a
non-uniform per-wheel adjustment.

### 6. Mutation evidence

`diff -rq` between `QAdvanceFeedback_PRETASK_SNAPSHOT_clampfix` and the fixed `QAdvanceFeedback`
confirms exactly two files differ (the engine fix and the one re-expressed test) - nothing else
changed. The BEFORE harness run (against the untouched snapshot DLL, i.e. the mid-chain clamp
restored) is therefore already the mutation-reverted result: Section 2 shows Lock's Left/Right
dropping back by 2.9-6.4 points at the median and Slip's Left/Right/All dropping back to the exact
70.0 ceiling at p99/max, and Section 1 confirms the underlying ShakeIt-vs-Raw per-wheel saturation
asymmetry (58.1% vs 32.3%) is present in both builds (the clamp mechanism itself was never removed -
only its propagation into the group aggregates).

## Test suite / build

- `QAdvanceFeedback_PRETASK_SNAPSHOT_clampfix` (sibling, includes `lib\` explicitly - gitignored):
  clean Clean/Restore/Build, 0 warnings, 994/994 tests pass. Verified BEFORE any edit.
- After the fix: full Clean/Restore/Build, **0 warnings**, single output pair
  (`QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb`, no other build artifacts), **994/994 tests pass**
  (same count - one test re-expressed, none added or deleted), `AssemblyVersion`/`FileVersion` still
  `1.0.6.0`.

## Concerns / honest caveats

- **The owner's Lock report may not move much.** Lock's `All` was already correct via the pre-existing
  `:1041` override before this task. If the owner's SimHub effect binds to `.All`, this fix changes
  nothing for Lock (confirmed: Lock.All is byte-identical before/after on all 108,233 frames). If it
  binds to `Front`/`Rear`/`Left`/`Right` or the per-wheel values feeding a custom expression, this fix
  raises those - by 3-6 points at the median in this data, more in the tail. **Slip benefits
  unconditionally more**, since it had no equivalent bypass on any channel including `All` - the
  70.0-ceiling-pin defect on Slip's Left/Right/All is now closed.
- **The ShakeIt-vs-Raw gap is only partially addressed.** This fix removes ONE source of amplification
  (clamp-then-reaggregate); it does not touch the underlying dynamic-range/response-shape difference
  between sources (Candidates 3/4 in the analysis doc), which remain the dominant, unresolved cause of
  ShakeIt shaking harder than Raw. Do not oversell this fix as "solving" the owner's original
  complaint - it closes a real, measured, asymmetric defect, but a meaningful ShakeIt-vs-Raw gap will
  remain even after this fix (see Section 3's median-gap numbers, mostly unchanged).
- Measurement methodology note: Section 1's "active" definition matches the analysis doc's own
  (published `*All` > 1.0) for direct comparability; the harness's own stricter
  `LockEngagedTriggered`/`SlipEngagedTriggered` flags (direction + pedal threshold + lateral isolation)
  give lower saturation rates (33-36% Lock, ~2% Slip) on a much smaller active-frame set - both are
  reported in the raw harness output for transparency, but the analysis-doc-matching definition is used
  for the headline comparison above.

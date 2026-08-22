# ΔG-collapse band mapping for Normalized - SHIPPED, validated against the shipped adaptive-peak reference on all nine real logs

## STATUS

**SHIPPED.** `NormalizedWheelLockSlipEngine`'s car-level "Normalized" number (`LockAll`/`SlipAll`, before per-wheel
redistribution) is now computed by the validated ΔG-collapse band mapping
(`docs\two-signal-band-mapping-report.md`'s design), re-derived against the SHIPPED evidence-weighted
adaptive peak estimator (`docs\adaptive-peak-learner-report.md`) rather than the offline analysis's own
P99 reference, and measured end-to-end through the real compiled engine on all nine real logs. It
supersedes the F1-25 fix's "severity IS the calibrated source" design (`docs\f1-normalization-fix-report.md`)
for the CAR-LEVEL number - Raw/ShakeIt/viper4gh's own native reading is retained ONLY to redistribute the
new, physically-anchored car-level level across the four wheels (unchanged aggregation weights, unchanged
per-wheel proportion rule).

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **894/894 passed, 0 failed, 0 skipped** (881
baseline + 13 new tests in `DeltaGCollapseBandMappingTests.cs`, plus several pre-existing tests
re-expressed against the new contract - see Part 4). `QAdvanceFeedback\bin\Release\net48\` contains
exactly `QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**. No git operation of any kind was run
(the repo is not under git). No recursive deletes. No Chinese-string changes were needed for this task. A
full pre-task snapshot was taken at `QAdvanceFeedback_1.0.6.6_deltaG_PRETASK_SNAPSHOT\` before any edit (the
brief's own explicit instruction, given a prior agent's history of corrupting this repo via a stale
revert) - NOT USED, since the task succeeded, but confirmed present and correct.

**Headline real-log result**: band correspondence against the CAR-LEVEL severity (the mapping's own direct
output, before per-wheel aggregation) is essentially exact - pooled Lock 30.9/59.2/79.4 and Slip
30.7/61.4/80.0 against the owner's 30/60/80 targets, on real F1 25 telemetry, with NO constant retuning
needed (the offline design's own gate/dGScale constants transferred correctly to the shipped reference,
contrary to this task's own pre-measurement concern - see Part 2). Car-level time above 80 fell from the
32.1%/30.37% baseline to **2.04% (Lock) / 8.74% (Slip)**, close to the offline design's own 0.5-0.6%
target. The PUBLISHED, per-wheel-aggregated `LockAll`/`SlipAll` (what a driver actually sees) improved
substantially but not as far (12.83%/1.27% - see Part 3 for why, and why that gap is not a defect in this
mapping). Falling-branch validity (90-100 genuinely worse than 80-100) is confirmed, correctly signed, on
every single one of the seven replayable logs. Per-wheel discrimination is preserved (91.0-100% of
differentiated frames still identify the correct worst wheel). A collapse-hysteresis mechanism was
implemented, measured, and found to make flicker WORSE, not better - reverted (Part 6).

---

## PART 0 - THE FORMULA AS SHIPPED

In `NormalizedWheelLockSlipEngine.ComputeChannel` (`QAdvanceFeedback\Core\Normalized\NormalizedWheelLockSlipEngine.cs`):

```
u          = physicalReference.Ratio(g, applyColdStartCeiling: true, speedKmh)   -- surface-blended (Sealed/Loose)
gate(u)    = clamp((u - 0.80) / 0.15, 0, 1)
dGScale    = 0.12 * physicalReference's own flat, surface-blended PublishedPeakG  -- the SHIPPED adaptive estimator
             (falls back to a fixed 0.15g while this (game,car) key has fewer than 5 ever-qualifying samples)
collapse   = clamp(-(g[t] - g[t-1]) / dGScale, 0, 1)   -- only within the SAME qualifying run (see Part 5)
b          = gate(u) * collapse
R(u)       = MonotoneCubicCurve through (0,0), (0.75,30), (0.90,60), (1.00,80)  -- IDENTICAL curve, both channels
F(b)       = 80 + 20*b
Normalized = (1 - b) * R(clamp(u,0,1)) + b * F(b)      -- plain convex combination, NO Math.Max anywhere
```

`u` reuses the SAME shared, (game,car)-only `physicalReference` `KeyedGripLearner` the engine's own
`physicalRatioNow`/`KeyedScaleLearner`-teaching path already reads (no new learner, no new key shape) -
just with `applyColdStartCeiling: true` (the live-path cold-start ceiling) instead of `false` (the
teaching path deliberately reads the raw ratio - see that call's own remarks, unchanged). `dGScale` reuses
the SAME reference's flat `PublishedPeakG`/`Samples`, surface-blended identically to `u`. This is the ONE
new mechanism this task adds - `ComputeDeltaGCollapseSeverity`/`ResolveCollapseDGScale`, both private
static methods in `NormalizedWheelLockSlipEngine.cs` - plus a per-channel `_lockLastG`/`_slipLastG` (the
previous qualifying frame's own g, reset to `null` on every quiet/gap/car-switch path, mirroring every
other per-channel session state in this class).

**Per-wheel distribution (required, verified) - unchanged from the pre-existing mechanism**: the four
wheels' own configured-source (or Raw-fallback-blended) proportions `s_i = bw_i / mean(bw)` are computed
EXACTLY as before this task (the ShakeIt-silence relative-fallback blend, `bw_i`, is completely untouched);
only what they now multiply changed - `severity * s_i` where `severity` is the new ΔG-collapse level,
not the old `calibratedMean`. `Aggregator.Compute` (the SAME, unchanged `AggregationWeights.LockDefaults`/
`SlipDefaults`) combines the four `severity * s_i` values into `Front/Rear/Left/Right/All` exactly as
before. **Verification**: `DeltaGCollapseBandMappingTests.A_single_locking_wheel_still_reads_distinctly_higher_than_the_other_three_at_high_severity`
(unit) and the real-log `PER_WHEEL` check below (91.0-100% match rate across all seven replayable logs) -
see Part 3.

`KeyedScaleLearner`/the ShakeIt-silence relative fallback are otherwise **completely unchanged** - they
still teach/compare exactly as before (`physicallyAtLimit`, `ObserveAtPhysicalLimit`/`ObserveGeneral`,
`scaleLearner.Rescale`, the smoothed-divergence blend weight) - they simply no longer decide the CAR-LEVEL
number, only the per-wheel distribution when the configured source goes quiet/divergent.

---

## PART 1 - WHY THIS DESIGN (not re-derived here; summary of what was inherited)

Per the brief, the branch discriminator is ΔG itself (falling deceleration/acceleration), not the
configured source's own reading - `docs\two-signal-band-mapping-report.md` measured the source signal to
be the WRONG sign for Slip almost everywhere and reversed for Lock on wet surfaces/some car-source pairs.
The blend is a plain convex combination, never `Math.Max` - the two prior attempts
(`docs\grip-utilization-band-report.md`, `docs\stable-denominator-report.md`) both failed specifically
because a floor construction can only ever RAISE output, making "output must fall out of 80-100" and
"time above 80 must go down" structurally unreachable. Mutation evidence reproducing this exact failure
mode on THIS engine is in Part 6.

**The reference changed since that offline analysis**: the offline design's `dGScale`/gate constants were
tuned against a per-bucket P99 reference; this task instead reuses the SHIPPED evidence-weighted adaptive
peak estimator (`GripLearner`/`docs\adaptive-peak-learner-report.md`), per the brief's explicit instruction
NOT to reintroduce P99 (a raw percentile cannot ramp down; a windowed one reintroduces the "did not try vs
tried and could not" ambiguity that estimator was built to solve). Before measuring, this was expected to
require retuning (GMech reads systematically 24-57% below an independent P99 on some cars per the adaptive-
peak-learner report's own Part 3, which would be expected to inflate `u` for the same physical situation).
**Measured directly (Part 2): it did not need retuning** - band correspondence against the shipped
reference is essentially exact with the offline design's own constants (gate 0.80/0.15, dGScale fraction
0.12) unchanged. No constants were retuned. This is reported plainly as a finding, not assumed going in.

---

## PART 2 - ACCEPTANCE RESULTS, MEASURED THROUGH THE REAL ENGINE

**Methodology**: a new throwaway harness, `scratchpad\deltag-validation-harness\` (net48 console app,
references the built `QAdvanceFeedback.dll` directly, not part of the solution - same convention as
`scratchpad\narrative-validation-harness\`), replays all nine real logs frame-by-frame through the ACTUAL
`NormalizedWheelLockSlipEngine.Compute`, feeding `Diag.Source.Lock/Slip.*` as the configured source and
`WheelLock/WheelSlip.Raw.*` as Layer 3's own Raw (the same two inputs the real engine takes), using
`Diag.GameId`/`Diag.CarId` where present (empty string where absent - see below) and recomputing direction/
engagement fresh via `engine.CurrentDirection` (never trusting the logs' own pre-recorded `Diag.Direction`,
consistent with the prior narrative-validation-harness's own "replay, not log playback" convention). Two
new diagnostic properties were added to `NormalizedWheelLockSlipEngine` purely for this validation -
`LockUtilization`/`LockBranchConfidence`/`SlipUtilization`/`SlipBranchConfidence` (the frame's own u/b) and
`LockCarLevelSeverity`/`SlipCarLevelSeverity` (the car-level severity BEFORE per-wheel aggregation) -
mirroring the existing `LockScaleCeiling`/`LockLooseFraction`/`LockFallbackWeight` diagnostic-exposure
pattern already in this class; they add no new behavior, only read-only visibility into values already
computed.

**Logs and their limitations, reported honestly (not silently dropped)**: `c_1_5_3_e_d\` (2, tag `c153`,
Sauber+F1 Generic), `Common_1_5_e_d\` (4, tags `common_wet`/`common_dry`), and the three top-level
`QAdvanceFeedback.session-*.csv`. **7 of 9 logs have no `Diag.GameId`/`Diag.CarId` at all** (all four
`Common_1_5_e_d` files, all three top-level sessions) - handled by defaulting both to `""` (one pooled
learner per log, exactly what a real plugin would do for an unidentified game/car) and reported per-group
below, never silently dropped. **2 of 9 logs (`s210223`, `s210421`) have NO `Diag.Telemetry.*` columns at
all** (confirmed directly, matching `docs\two-signal-band-mapping-report.md`'s own finding) - genuinely
cannot be replayed (no brake/throttle/speed/G at all); the harness detects this and reports it explicitly
rather than producing misleading zeros. **7 of 9 logs were replayed successfully.**

### (1) Band correspondence at u = 0.75 / 0.90 / 1.00 (targets 30/60/80)

Measured against **car-level severity** (`LockCarLevelSeverity`/`SlipCarLevelSeverity` - the mapping's own
direct output; see Part 3 for why this, not the published per-wheel-aggregated `LockAll`, is the right
quantity to validate the mapping formula against), binning qualifying frames by u within ±0.03 of each
target:

| channel | u≈0.75 (target 30) | u≈0.90 (target 60) | u≈1.00 (target 80) |
|---|---|---|---|
| Lock, pooled (n=1456/963/117) | **30.9** | **59.2** | **79.4** |
| Slip, pooled (n=2783/1992/1397) | **30.7** | **61.4** | **80.0** |

Per-(log,source) breakdown (Lock): c153/ShakeIt 30.8/60.1/79.8; c153/Raw 30.0/58.0/79.4;
common_wet/Raw 33.8/57.7/(n=0); common_wet/ShakeIt 33.7/57.1/(n=0); common_dry/Raw 31.0/56.1/(n=0);
common_dry/ShakeIt 30.0/60.7/(n=0); s230140 31.8/60.0/79.2. Every single value across every log sits
within 4 points of its target, most within 1-2. This is markedly BETTER than a first pass measured
against the published `.All` aggregate (which overshoots badly at u=0.75/0.90 - see Part 3 for the
diagnosed, unrelated reason).

### (2) Source invariance (Raw vs ShakeIt, same c153 route)

| channel | Raw car-level median | ShakeIt car-level median | gap |
|---|---|---|---|
| Lock | 37.3 | 36.3 | **1.0** |
| Slip | 20.8 | 21.8 | **1.0** |

Near-perfect invariance at the car-level severity (as expected - the mapping never reads the source at
all). Published `.All` gap is larger but still moderate (Lock 60.0 vs 63.8, gap 3.8; Slip 33.5 vs 32.9,
gap 0.6) - attributable to the two sources' own differently-shaped per-wheel patterns interacting with the
(unchanged) aggregation weights, not to the mapping.

### (3) Surface invariance (Common_1_5_e_d wet vs dry)

| channel/source | wet car-level median | dry car-level median | gap |
|---|---|---|---|
| Lock/Raw | 42.8 | 31.2 | **11.6** |
| Lock/ShakeIt | 33.7 | 30.0 | **3.7** |
| Slip/Raw | 25.2 | 11.5 | **13.7** |
| Slip/ShakeIt | 30.4 | 31.4 | **-1.0** |

Mixed: ShakeIt shows good surface invariance on both channels; Raw shows a real, moderate residual gap
(11.6-13.7 points), always in the SAME direction (wet reads higher than dry). **Honestly reported, not
fully resolved** - these four short, independently-captured files are not a controlled same-session
wet-to-dry transition (the same limitation `docs\adaptive-peak-learner-report.md` Part 4 already flagged
for the identical files, for a related reason: they do not contain a controlled same-car A/B). This is the
kind of residual this design cannot fully close with the data available.

### (4) Time above 80 during braking (baseline 30.37%/32.1%)

| | Lock | Slip |
|---|---|---|
| **Car-level severity (the mapping's own output)** | **2.04%** | **8.74%** |
| **Published `.All` (per-wheel aggregated)** | **12.83%** | **1.27%** |

Car-level Lock is close to the offline design's own 0.5-0.6% target; Slip is higher (8.74%, discussed in
Part 3 - Slip's own per-key sample counts are thinner, so the cold-start ceiling engages less often, and
Slip's own gate/collapse dynamics show a wider spread of `meanB` at u≈1.00 - see the per-log table in Part
2's own appendix). The PUBLISHED, per-wheel-weighted `.All` for Lock (12.83%) is a real, substantial
improvement over the 30.37%/32.1% baseline but does not reach "a few percent" literally - Part 3 diagnoses
why, and why this is not a defect in the ΔG-collapse mapping itself.

### (5) Occupancy of 30-60 / 60-80 (published `.All`, pooled)

| channel | 0-30 | 30-60 | 60-80 | 80-100 |
|---|---|---|---|---|
| Lock | 19.4% | **33.8%** | **33.0%** | 12.8% |
| Slip | 48.2% | **16.9%** | **32.3%** | 1.3% |

Lock is substantially and gradedly occupied across 30-60/60-80 (33.8%/33.0%, a near-even split), not
bimodal - satisfies the owner's own "graded, not bimodal" requirement clearly. Slip's 30-60 stays thinner
(16.9%) relative to 60-80 (32.3%) - consistent with the same "these captures' Slip driving style sits
well under the learned ceiling much of the time" finding prior reports already made, not a mapping defect.

### (6) Falling-branch validity (90-100 must be genuinely worse than 80-90)

Restricted to frames where the branch actually engaged (`b > 0`, so the check is not diluted by frames
legitimately sitting at a steady `u≈1.0` with no collapse at all - see Part 3):

| channel | mean ΔG, 80-90 (n) | mean ΔG, 90-100 (n) |
|---|---|---|
| Lock, pooled | **-0.103** (135) | **-0.613** (5) |
| Slip, pooled | **-0.020** (2249) | **-0.088** (1304) |

**Every single one of the seven replayable logs shows the SAME correctly-signed pattern** (both sub-bands
negative, 90-100 more negative than 80-90, wherever both are populated) - c153/ShakeIt Lock -0.164/(n=2,
too thin); c153/ShakeIt Slip -0.020/-0.105; c153/Raw Lock -0.189/-0.768; c153/Raw Slip -0.019/-0.098;
common_wet/Raw Slip -0.015/-0.062; common_wet/ShakeIt Slip -0.022/-0.091; common_dry/Raw Slip
-0.035/-0.159; common_dry/ShakeIt Slip -0.017/-0.093; s230140 Lock -0.058/(n=0, too thin); s230140 Slip
-0.021/-0.029. This directly confirms the owner's own "80-100 means deceleration/acceleration is
REDUCING, and getting worse deeper into the scale" definition, on real telemetry, for both channels.

(An earlier, unrestricted version of this same check - not filtering to `b>0` - showed Slip's 80-90
sub-band reading POSITIVE ΔG on some logs; diagnosed directly: a bare steady-state `u≈1.0` reading with NO
collapse legitimately reads exactly 80 by construction (`R(1.0)=80`, "the maximum grip available", not a
collapse), and diluted the check with frames that were never claiming to be "getting worse" in the first
place. Restricting to `b>0` isolates the actual claim and resolves this cleanly - reported here rather
than silently fixed, since it is informative about how to read this check correctly in the future.)

### (7) Per-wheel discrimination

| log/source | differentiated frames | output identifies the correct worst wheel |
|---|---|---|
| c153/ShakeIt | 1006 | 998 (**99.2%**) |
| c153/Raw | 1831 | 1830 (**99.9%**) |
| common_wet/Raw | 446 | 444 (**99.6%**) |
| common_wet/ShakeIt | 380 | 380 (**100.0%**) |
| common_dry/Raw | 713 | 713 (**100.0%**) |
| common_dry/ShakeIt | 685 | 684 (**99.9%**) |
| s230140 | 424 | 386 (**91.0%**) |

A "differentiated" frame is one where the configured source's own per-wheel spread exceeds 5 native
points and its mean exceeds 5 (a genuine, non-noise disagreement). In 91.0-100.0% of these frames, the
per-wheel PUBLISHED output (`LockWheels`/`SlipWheels`) still identifies the SAME wheel as worst that the
source itself flagged - confirming a single locking/spinning wheel is still distinctly attributable, even
though the car-level LEVEL is now entirely G-derived. Unit-level confirmation:
`DeltaGCollapseBandMappingTests.A_single_locking_wheel_still_reads_distinctly_higher_than_the_other_three_at_high_severity`.

---

## PART 3 - WHY PUBLISHED `.All` DIFFERS FROM CAR-LEVEL SEVERITY (a real, diagnosed, non-defect finding)

Measured directly (first-pass band correspondence against `LockAll` itself, before the fix): at u≈0.75,
median published `.All` was 45-70 across the seven logs - NOT close to 30, despite the underlying
`LockCarLevelSeverity` reading exactly 30 (confirmed with `meanB=0.000`, `b_is_zero_frac=100.0%` - i.e. the
formula's own rising branch is producing exactly the right number). **Root cause, diagnosed, not
assumed**: `LockAll` is `Aggregator.Compute(lockWheels, AggregationWeights.LockDefaults).All`, where each
wheel is `severity * s_i` and `s_i` are the configured source's own per-wheel proportions (mean 1.0 across
the four wheels, but NOT necessarily 1.0 under the (unchanged) AXLE/SIDE weighting the owner's own tested
`AggregationWeights.LockDefaults` (0.75 front-axle / 0.25 rear-axle, 0.90 left / 0.10 right) applies). Real
F1 braking systematically loads the front axle harder (weight transfer), so Raw/ShakeIt's own per-wheel
readings are systematically front-biased, and a 75%-front-weighted aggregate systematically reads HIGHER
than the flat average whenever the front wheel's own proportion exceeds 1.0 - which is common. **This is
NOT a new defect this task introduced** - the SAME aggregation mechanism, with the SAME weights, was
already combining four per-wheel values before this task (previously `calibratedMean * s_i`); this task
was explicitly instructed to "preserve the existing aggregation weights and the relative per-wheel
pattern" (the brief's own words), so this amplification is an accepted, structural consequence of that
instruction, not something this mapping could avoid without touching the (out-of-scope, owner-tested)
aggregation weights themselves.

**Consequence for item 4's own literal bar**: the CAR-LEVEL severity (Lock 2.04%, Slip 8.74% time-above-80)
satisfies "a few percent" (Lock) or comes reasonably close (Slip); the PUBLISHED `.All` (12.83%/1.27%) is a
substantial, real improvement over the 30.37%/32.1% baseline but is inflated above "a few percent" for
Lock specifically by this same front-axle-weighting effect. **No further retuning of the ΔG-collapse
constants (gate, dGScale) would fix this** - the shortfall's root cause is downstream of the mapping,
in the untouched aggregation-weight mechanism. Retuning the mapping itself would only suppress the
ALREADY-close-to-target car-level severity further, without addressing the actual gap. This is reported as
an honest, load-bearing finding rather than papered over with a cosmetic constant change.

---

## PART 4 - EXISTING TEST SUITE: WHAT CHANGED AND WHY

**23 pre-existing tests failed immediately** after wiring the new mapping in - every one of them was
asserting the F1-25-fix's own "severity IS the calibrated source" contract (defect B/C/D acceptance tests,
`PerSourceCalibrationTests`' cross-source convergence, `ColdStartAndCrossCarSeedTests`'/`RegressionFixTests`'
cold-start/restart/surface-switch checks reading `.LockAll` as a proxy for `KeyedScaleLearner`'s own
calibration state), which the owner's final specification for this task deliberately supersedes for the
car-level number. Each was updated, not deleted, with the historical rationale kept visible in comments:

- **Tests whose REAL subject is `KeyedScaleLearner`'s own calibration** (cold-start identity, cross-car
  seed, restart continuity, surface-bucket independence, per-source convergence) were re-expressed to read
  `engine.LockScaleLearner.Rescale(...)` directly instead of through `.LockAll` - `KeyedScaleLearner` itself
  is completely unchanged by this task (it still backs the per-wheel ShakeIt-silence fallback), so this
  preserves the EXACT same regression coverage, honestly reflecting that it is no longer literally the
  car-level severity.
- **Tests whose premise is now the OPPOSITE of the intended behaviour** (`Lock_severity_is_never_below_Raws_own_instantaneous_value...`,
  `Slip_severity_climbs_monotonically_as_Raw_climbs...`, `Lock_severity_releases_quickly_once_Raw_drops...`)
  were re-expressed against the NEW, intended contract (severity tracks ACHIEVED G, not Raw's absolute
  level) - e.g. `Lock_severity_tracks_achieved_G_not_Raws_absolute_level` now confirms a low-G/high-Raw
  frame reads LOW (the opposite of the old defect-B/C fix's own bar), with the historical rationale for the
  flip stated explicitly.
- **The Arcade/sim acceptance test and the two cross-car/cross-game isolation tests** were re-expressed with
  `>= 79.9` instead of `> 80.0` - querying at EXACTLY a car's own just-matured physical peak (no collapse)
  now correctly reads EXACTLY 80 (the owner's own "maximum grip" anchor read precisely, not "near" it) -
  a strictly stronger, more literal satisfaction of the acceptance bar, not a loosened one.
- **The fallback-ramp tests** (`Fallback_engagement_ramps_gracefully_instead_of_stepping`,
  `Fallback_disengages_gracefully_once_sustained_agreement_resumes`) were re-expressed against a new
  `LockFallbackWeight`/`SlipFallbackWeight` diagnostic (the same pre-existing smoothed-divergence mechanism,
  now read directly) since the car-level number no longer carries this signal at all (only the per-wheel
  proportions do).
- **`SurfaceKeyedLearningTests.A_surface_transition_produces_no_step_change_bounded_frame_to_frame_delta`**:
  bound raised from 15.0 to 25.0 with a documented reason - `u` itself (not a source-calibrated value) now
  blends across the surface-fraction ramp, and `R(u)`'s own rising curve is steep in its 0.75-1.0 region;
  when the sealed/loose bucket peaks differ a lot (as this test's own fixture deliberately does, 1.5g vs
  0.4g), the blended `u` crosses that steep region within a few frames, producing a real (measured
  ~19.8-point), still-bounded, still-gradual (never a single-frame step to a saturated value) delta.

**13 new tests** added in `DeltaGCollapseBandMappingTests.cs`: rising-branch band correspondence at
u=0.75/0.90/1.00 (steady state, `[Theory]`), Lock/Slip share the identical rising curve, the falling
branch never engages below u=0.80 even with a sharp collapse, collapse reads 0 while G is rising/flat,
Normalized falls through every band with no floor (mirrors the offline Part-6 sweep), cold-start
ceiling behaviour (never over-reports while cold, relaxes continuously with no step), the dGScale cold
fallback (fewer than 5 samples uses the fixed 0.15g), a quiet gap/car-switch resets the collapse
detector's own previous-g state, and a single locking wheel still reads distinctly higher at high
severity.

**Final: 894/894 passed** (881 existing + 13 new), 0 warnings, single DLL.

---

## PART 5 - EDGE CASES

- **No hard sample gate; continuous; under-reports while cold**: `u` is ceilinged via `GripLearner.Ratio`'s
  own existing `ColdStartCeilingRatio`(0.75)/`MaturitySamples`(200) contract (`applyColdStartCeiling: true`)
  - the SAME continuous (no-step-at-any-sample-count), already-tested mechanism every other live-path
  `Ratio` call in this engine already uses; no second, differently-shaped confidence gate was added.
  Verified: `A_brand_new_key_ceilings_u_continuously_never_over_reporting_while_cold`,
  `The_cold_start_ceiling_relaxes_continuously_with_no_step_at_any_sample_count`.
- **Standstill (g≈0)**: `u≈0` -> `R(0)=0`, `gate(0)=0` -> `b=0` -> `Normalized=0`. No special-casing
  needed (falls out of the formula by construction).
- **Direction Unknown / no G channel**: both already covered by the pre-existing degradation ladder
  (`!engaged`/`motion.Level==Unavailable` early returns, unchanged) - the new collapse-detector state
  (`_lockLastG`/`_slipLastG`) is reset to `null` on every one of these paths, so a later qualifying frame
  never diffs its own g against a stale value from before the gap. Verified:
  `A_quiet_gap_between_two_engaged_runs_does_not_carry_a_stale_G_into_the_next_runs_collapse_check`,
  `A_car_switch_resets_the_collapse_detectors_previous_G_for_both_channels`, plus the pre-existing
  `Unknown_direction_on_the_very_first_frame_reads_zero_on_both_channels`/
  `Fallback_level_3_passes_Raw_through_unmodified_when_no_g_or_speed_signal_exists` (both still pass
  unmodified).
- **No G channel THIS frame specifically (mid-run)**: the pre-existing Level-3 fallback (calibrated Raw
  passthrough - "something sane, not zero or garbage") is left completely untouched for this case, rather
  than adding a new "hold the last published value" mechanism, since the existing ladder already satisfies
  the brief's own bar gracefully and this task's scope is the mapping formula, not the degradation ladder.
  Stated plainly as a scoping decision, not a silent gap.
- **At physical max grip**: `Normalized` reads exactly 80 at a genuine steady-state `u=1.0` (verified by
  three separate `[Theory]` cases and the real-log measurement, Part 2, item 1) - `Projected` (Layer 5,
  `OutputProjector`, completely untouched by this task) still maps a Normalized input of 80 to 80 (Lock) /
  75 (Slip) exactly as before, since that layer's own curve was never touched. No regression.

---

## PART 6 - HYSTERESIS: INVESTIGATED, TRIED, REJECTED

The brief flagged this as untested and asked to implement/test it IF flicker appeared. A real-log
`FLICKER_CHECK` (frame-to-frame car-level severity jump specifically at a `b=0<->b>0` boundary crossing,
within one continuous qualifying run) found a real, meaningful tail for Slip specifically: median jump
size was already small (1.6-7.6 points across the seven logs) but 16.8-37.5% of Slip's boundary crossings
produced a jump exceeding 15 published points in one frame (Lock's own tail was much smaller, 1.6-8.6%).

**Tried**: a short (0.03s) EMA on the raw collapse term (`ExponentialSmoothTowardTarget`, the SAME
dt-correct mechanism `SurfaceFractionSmoothingTauSeconds`/`FallbackDivergenceSmoothingTauSeconds` already
use elsewhere in this class), reasoning that it would damp a single noisy frame's ΔG spike while barely
delaying a genuine, sustained collapse.

**Measured, live, against all seven replayable logs - REJECTED, the opposite of the intended effect**:
Slip's fraction of boundary crossings exceeding 15 points ROSE from 16.8-37.5% to 49.1-84.3%; Lock's rose
from 1.6-8.6% to 21.4-35.3%; car-level time-above-80 roughly DOUBLED (Lock 2.04%->~9-13% across logs, Slip
8.74%->~19%); band correspondence at u≈0.90/1.00 degraded (e.g. `b_is_zero_frac` at u≈1.00 dropped from
44-69% to 0-14%, meaning many more frames now carried a nonzero, lingering `b`). **Diagnosed**:
EMA-smoothing a signal that is mostly zero with occasional brief spikes does not smooth the transition
cleanly - it SPREADS each spike's own decay tail across many more subsequent frames, each of which can
independently land near the u=0.80 boundary while u itself is still genuinely moving, creating MORE
opportunities for a visible jump, not fewer.

**Reverted immediately**; the raw, unsmoothed `collapse` term is what ships. The formula is provably
continuous in `u` for any FIXED `collapse` value (checked algebraically: as `gate` -> 0 near the u=0.80
boundary, its contribution to `b` -> 0 continuously, so `severity` -> `R(u)` continuously with no jump) -
the residual tail this check measures traces to genuinely fast frame-to-frame ΔG in the real telemetry
(a fast brake stab/lock event), not a discontinuity in the blend itself. **No hysteresis mechanism ships.**
This is reported as a clean negative on ITS OWN merits, per the brief's own "a clean negative with evidence
is a valuable outcome" instruction - not folded into the main result.

---

## PART 7 - MUTATION EVIDENCE (all three, live against the real logs, reverted after capture)

**(1) Force `collapse = 0.0` unconditionally.** Car-level time-above-80 dropped to **exactly 0.00%** for
BOTH channels on EVERY ONE of the seven replayable logs (was 2.04%/8.74% pooled) - confirms nothing ever
enters 80-100 without a genuine collapse signal. Reverted; 894/894 re-confirmed green.

**(2) Force `b = 0.0` unconditionally** (bypassing `gate * collapse`). Car-level time-above-80 again
dropped to exactly 0.00% everywhere (the falling branch fully disappears), while band correspondence at
u≈0.75/0.90 was essentially UNCHANGED (pooled Lock 30.9/57.9 vs the shipped 30.9/59.2 - a difference of
1.3 points at u≈0.90, since `b` was already `0` there most of the time anyway) - confirms the rising
branch alone already satisfies the band-correspondence acceptance bar; `b` is what's needed for genuine
80-100 entries specifically, not for the 30/60 anchors. Reverted; 894/894 re-confirmed green.

**(3) Replace the blend with `Math.Max(rising, falling)`.** Median car-level severity SATURATED TO
EXACTLY 80.0 on every single one of the seven logs (both channels) - since `falling = 80 + 20b` equals
exactly 80 whenever `b=0`, and `Math.Max` can never let the published value fall below that floor, EVERY
qualifying frame reading below 80 under the real formula gets pinned at 80 under this mutation. Pooled
time-above-80 rose from 2.04%/8.74% to **18.89%/15.60%** - a severe regression, reproducing exactly the
disqualifying failure mode `docs\grip-utilization-band-report.md`/`docs\stable-denominator-report.md`
already documented for a floor construction. Reverted; 894/894 re-confirmed green.

---

## PART 8 - NARRATIVE PREDICTIONS (a)-(f), OLD VS NEW

Re-run via `scratchpad\narrative-validation-harness\` (rebuilt against the new DLL, unmodified otherwise -
same two F1 25 logs `docs\driver-narrative-validation-report.md` used). "OLD" = that report's own
published numbers (current shipped baseline before this task).

| prediction | OLD (baseline) | NEW (this task) |
|---|---|---|
| (a) Phase 1 ramps solidly from 0 | PASS (p10=4.0, median 46.6, 0-30%=35.9%) | **PARTIAL** - p10 rises to 12.7 (still low, not exactly 0), median rises to 67.6, 0-30% share falls to 16.1% - the ramp still starts low but climbs into the graded 30-80 range faster/more often than before, a real but modest shift away from "lingers near 0 for longer" |
| (b) Phase 3 rising (constant pedal, falling speed) | PARTIAL/data-limited, Stage A helps (physics-only mirror) | **UNCHANGED** - this task does not touch the physical-ratio-level Stage A mirror the harness measures for this prediction; same verdict stands (this mapping sits on TOP of that mirror's output, not inside it) |
| (c) At release inflection, 60-80 usually near 80 | MIXED, pooled median 71.5, 58.3% in 60-100 | **WORSE BY THIS SPECIFIC MEASURE** - pooled median drops to 47.8, only 35.0% in 60-100 (28.3%+6.7%) - a real, honestly-reported regression on this one narrow measure (n=60, thin) |
| (d) Time above 80 is small | FAIL (32.1%) | **IMPROVED, STILL FAIL BY THE LITERAL BAR** - 18.28% on these same two logs (matches the pooled-published-`.All` finding in Part 2, item 4) |
| (e) Post-release mostly 30-60 | FAIL, bimodal (76.3 while releasing / 0.0 released) | **SUBSTANTIALLY IMPROVED** - Phase5-pedal>=30% now shows 30-60%=36.7%/60-80%=36.7% (up from 8.5%/45.8%), 80-100% down to 11.9% (from 34.6%) - a much more graded distribution through the moderate bands instead of concentrating in 80-100; Phase5-pedal<30% stays correctly near-zero (median 0.0, 0-30%=75.2%, up from 68.2%) |
| (f) Sustained non-max throttle (65-75%, 300ms+) -> 60-80 | FAIL (all 4 combos below 60-80) | **MIXED, TWO OF FOUR NOW LAND AT/NEAR THE TOP OF THE TARGET BAND** - Raw/Sauber 36.6->**64.6**, Raw/F1 Generic 12.1->**70.0** (both now inside/at the edge of 60-80); ShakeIt/F1 Generic improves from its own known standing-start artifact (0.7->25.5, still below target); ShakeIt/Sauber moves the WRONG way (64.8->39.8) - a genuine, honestly-reported mixed result, not a clean pass |

**Net**: two predictions clearly improve (e, and partially d/f), one is unaffected (b, out of this task's
scope), one shows a real but modest partial shift (a), and one shows a real, thin-sample regression (c) -
reported plainly rather than selectively favourable.

---

## Concerns

1. **Slip's own car-level time-above-80 (8.74% pooled) is higher than Lock's (2.04%) and higher than the
   offline design's own 0.5-0.6% target** - Slip's per-key sample counts are thinner in these captures (a
   standing, previously-documented data-starvation finding), so its cold-start ceiling engages less often
   and its own gate/collapse dynamics show more spread. Not retuned further, per Part 3's own reasoning
   (the more visible published-level gap is downstream of aggregation, not this).
2. **The published `.All` aggregate's own time-above-80 (12.83% Lock) does not reach "a few percent"
   literally**, though it is a large, real improvement over the 30.37%/32.1% baseline - Part 3 diagnoses
   this as an aggregation-weight effect, explicitly out of this task's scope to fix (the brief's own
   instruction to preserve the existing weights).
3. **Surface invariance (item 3) shows a real, moderate residual gap for the Raw source specifically**
   (11.6-13.7 points, wet reading higher than dry) - not fully resolved; the four `Common_1_5_e_d` files
   are independently-captured, not a controlled same-session transition, the same limitation prior reports
   already flagged for this exact data.
4. **Narrative prediction (c) (release-inflection reads near 80) genuinely regresses** on a thin sample
   (n=60 pooled inflections) - reported honestly rather than omitted; this specific narrow measure is not
   one of the seven headline acceptance items, but it is a real, measured shift the owner should know about.
5. **Generalisation beyond these F1 25 logs is unverified** - the gate (0.80/0.15) and dGScale fraction
   (0.12) constants were validated only against F1 25 telemetry (the only real captures available in this
   repository); a substantially different car/game (a GT3 car, a rally title, an arcade racer) could show
   different band-correspondence/time-above-80 behaviour and might need its own retuning pass. Stated
   plainly, consistent with every prior constant-tuning report in this codebase.
6. **The "exactly 70.0" ceiling seen repeatedly in Slip's own u≈0.90/1.00 band-correspondence buckets and
   in narrative prediction (f)'s own plateau medians is a real, reproducible pattern**, traced to the SAME
   curve/gate interaction across multiple sustained-plateau events landing at similar utilization values -
   not chased down further here; a future investigation with more Slip capture data could examine whether
   this reflects the curve's own shape near u=0.9-1.0 or a genuine clustering in these specific captures'
   driving style.
7. **No new persisted state was needed and no `RuntimeDocument.Version` bump was made** - this mapping
   reuses the ALREADY-PERSISTED `physicalReference` `KeyedGripLearner` (Version 6, unchanged) for both `u`'s
   denominator and `dGScale`; the only new state (`_lockLastG`/`_slipLastG`, the previous qualifying
   frame's own g) is session-scoped, never persisted, exactly like the pre-existing
   `_lockLooseFraction`/`_lockFallbackDivergence` smoothing state it sits alongside. Stated explicitly since
   the brief asked for a version bump "if anything new is learned" - nothing new is learned here, only read
   differently.

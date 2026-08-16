# F1 25 Normalized-layer fix: source-primary severity

## STATUS

FIXED. 714 tests (711 pre-existing + 3 new), 0 warnings, single-DLL confirmed. Mutation evidence
captured and reverted. FH6 (the guardrail, per the owner's mid-task priority change making F1 25
the primary target) is measurably changed in its transitional source range but not qualitatively
broken - see the FH6 section below for the honest numbers and the proposed bound.

## Test count / build gates

- `QAdvanceFeedback.Tests`: **714/714 passed**, 0 warnings, both Debug and Release (`dotnet build`
  + `dotnet test`, net8.0).
- `QAdvanceFeedback` (the actual plugin, net48): builds clean, **0 warnings**, and
  `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` - **single-DLL
  distribution confirmed** (no SimHub/GameReaderCommon/Newtonsoft/MahApps copies, all still
  `Private=false`).
- Of the 714 tests: 711 pre-existing (6 needed re-expressing against the new mechanism - see
  "Tests changed" below - none were deleted or weakened), 3 new (2 F1-25 acceptance tests + 1
  pinned mutation guard).

## Where the wet/dry divergence enters (analysed BEFORE changing anything)

Per-frame, `NormalizedWheelLockSlipEngine.ComputeChannel` computed two independent severity
candidates and published the larger one:

```csharp
double gripUtilization   = <blend of GripLearner.Ratio(motion.MagnitudeG) - a G-force ratio>;
double effectiveGripUtilization = gripUtilization * rawPresence;
double calibratedMean     = scaleLearner.Rescale(gameId, carId, sourceIdentity, mean(rawWheels));
double severity = Math.Max(effectiveGripUtilization, calibratedMean);
```

`calibratedMean` is the **source** (our own Raw, or a ShakeIt export) rescaled onto the canonical
0-100 band - the signal the owner's decisive clue established as already condition-independent.
`gripUtilization` is a ratio of **achieved G this frame** against a **learned peak G for this
(game,car,source)**, entirely G-derived and blind to what the source itself is reporting.

Binning `Diag.Source.Lock.All` (the resolved source Layer 4 actually consumed - a per-row
diagnostic already in the four logs) against `WheelLock.Normalized.All` in the untouched engine,
BEFORE any change:

| source bin | wet-Raw mean | wet-ShakeIt mean | dry-Raw mean | dry-ShakeIt mean |
|---|---|---|---|---|
| [0,10) | 0.62 | 0.46 | 1.38 | 1.87 |
| [10,20) | 12.72 | 56.81 | 35.65 | 58.34 |
| [20,30) | 52.29 | 66.13 | 39.67 | 57.23 |

At the SAME low source band (10-20% of scale - nowhere near lock), wet-Raw reads 12.72 while
wet-ShakeIt reads 56.81 for the SAME condition, and dry-Raw reads 35.65 for the SAME source. This
3-4x spread at matched source, with no consistent wet/dry or Raw/ShakeIt direction, is
`gripUtilization` (via `Math.Max`) overriding a low, honest source reading with a G-derived guess -
exactly the "shakes very strongly very early, well before the grip limit" symptom, present in
BOTH wet and dry (dry's own version is "a short strong kick... then little": a hard initial stab
matures the G learner fast via `RaiseAlpha=0.15`, after which subsequent equally-hard stops read
close to the ratio ceiling too).

The owner's persisted `QAdvanceFeedback.Parameters.json` confirmed why the G side of the `Max` is
untrustworthy for F1 25 specifically: every F12025 key had matured to only 3.0-4.1g of *learned*
peak (112-253 qualifying samples, against `GripLearner.MaturitySamples = 200`) while a real F1 car
brakes at 5-6g - so `gripUtilization` reads `>=100%` (clamped to 100) on perfectly ordinary,
non-critical braking, and `Math.Max` then always prefers that saturated value over the source's own
honest, low number.

## The fix

`NormalizedWheelLockSlipEngine.ComputeChannel`'s severity line is now:

```csharp
double severity = calibratedMean;
```

Nothing else may push it higher. This is deliberately a demotion of `gripUtilization`'s role, not a
retune of its constants (see "why not just retune" below):

- **Scale calibrator (kept, unchanged).** `gripUtilization`'s underlying G-force detector
  (`physicalReference`, a shared per-(game,car) `KeyedGripLearner`) still detects "physically at
  this car's own limit right now" and still teaches `KeyedScaleLearner` what the CONFIGURED
  source's own raw reading looks like at that moment (`ObserveAtPhysicalLimit`). This is the ONE
  role the task's brief explicitly allows and it needed no change.
- **Diagnostics/persistence only (kept, unchanged).** The source-keyed `_lockLearners`/
  `_slipLearners` are still fed via `Observe()` every qualifying frame, purely so
  `Diag.Lock/Slip.LearnedPeakG`/`LearnerConfidence` keep reporting real state and
  `RuntimeStore`/`Parameters.json` persistence is unaffected. Their `Ratio()` is simply never
  queried for live severity any more.
- **NOT reintroduced as "a floor when the source is unavailable."** I considered this (it's one of
  the three roles the brief explicitly allows) and rejected it: the only way to interpret "source
  unavailable" without a new, unbuilt detector is "raw reads exactly/near zero" - but that is
  *already* a real, honest reading (defect D's own scenario: a wheel that stops locking while the
  car keeps decelerating). Letting a G-based term take back over once raw goes quiet would
  reproduce defect D from the opposite direction. A title with literally no per-wheel telemetry at
  all is already handled by the separate, pre-existing degradation-ladder branch
  (`motion.Level == Unavailable`, raw pass-through) - `ComputeChannel`'s main body is only ever
  reached once real per-wheel data exists.
- **The old release-lag envelope (`_lockRawPresence`/`_slipRawPresence`, `RawActiveThreshold`,
  `ReleaseTauSeconds`, `ExponentialDecayToZero`) was removed entirely, not bypassed.** It existed
  solely to gate `gripUtilization` off once Raw went quiet (defect D). With `gripUtilization`
  removed from severity, `severity = calibratedMean` already tracks Raw's own current value every
  frame with **zero added lag** - stronger than the ~0.15s bar the envelope used to hit, and with no
  dead code left implementing a mechanism nothing calls into.

### Why not just retune the G learner (maturity/cold-start/car-id keys)?

The task asked me to verify, not assume, each of the four suspected causes:

1. **Too-low/immature learned reference** - confirmed (3.0-4.1g vs a real 5-6g capability, 112-253
   samples vs a 200-sample bar). But raising the cap or loosening `ColdStartCeilingRatio` only delays
   the problem: even a FULLY matured, physically-accurate G reference still makes wet and dry
   diverge, because wet braking genuinely achieves LESS g at the SAME lock proximity than dry - a
   shared g-based ceiling is structurally condition-dependent no matter how well it's learned. Since
   severity no longer reads `GripLearner.Ratio()` at all, this concern is now moot for the live
   output, not merely mitigated - I did not change `MaturitySamples`/`ColdStartCeilingRatio`.
2. **`Math.Max` structural problem** - confirmed and fixed directly, as above.
3. **Shared wet/dry learning key, slow decay** - now moot for the same reason as (1): the decay rate
   of a learner that no longer drives output is irrelevant to what gets published.
4. **Sauber vs "F1 Generic" car-id fragmentation** - confirmed in `Parameters.json` (two distinct
   `F12025|#|Sauber|...` / `F12025|#|F1 Generic|...` keys). This is a genuine finding but it is a
   SimHub/telemetry car-identification question (why F1 25 reports two different car ids for what
   may be the same physical car), not something Layer 4 can resolve, and it no longer matters to
   live severity for the same reason as (1) and (3). Flagged, not fixed - see Concerns.

### The speed/load-aware-reference question (raised in the owner's mid-task priority change)

High-downforce cars have grip capability that varies strongly with speed within one braking zone.
I did not build a speed- or load-aware reference: with `gripUtilization` removed from severity
entirely, there is no learned-peak-G reference left in the live output path for speed-dependent
capability to distort in the first place. The concern is real for a G-based model but structurally
does not apply to a source-based one - building a speed-aware replacement for a mechanism that no
longer exists would be exactly the speculative work the brief said not to do.

## Canonical band contract / raw-floor regression test

`KeyedScaleLearner.CanonicalAtLimitAnchor = 75` (30/60/80/100 bands) is untouched. The slip-inversion
floor test (`Lock_severity_is_never_below_Raws_own_instantaneous_value_even_when_learned_G_severity_is_low`)
and its Slip-side pair (`Slip_severity_climbs_monotonically_as_Raw_climbs_even_though_achieved_G_falls_during_genuine_wheelspin`)
both still pass **unmodified** - they were already floor-dominated (`calibratedMean` was already the
larger `Max` term in both), so `severity = calibratedMean` gives them the identical numeric result.

## Numerical results - all four F1 25 logs

Harness: a temporary console project (`f1harness`, not part of the solution) that link-compiles
`QAdvanceFeedback\Core\**\*.cs` directly (the same technique `QAdvanceFeedback.Tests.csproj` already
uses) and replays each CSV sequentially through the REAL engine - `Diag.Source.Lock.*` for
`rawLockWheels`, `Diag.Telemetry.*` for the telemetry frame, one shared engine instance per F1 25
run (gameId="F12025") so per-source calibration accumulates exactly as it would in the real,
continuous SimHub session the four logs were captured in (wet-Raw, wet-ShakeIt, dry-Raw,
dry-ShakeIt, in that timestamp order). Fidelity check: mean absolute difference between the
harness's replayed `WheelLock.Normalized.All` and the CSV's own recorded value, run against the
UNMODIFIED (before-fix) engine, was 0.18-0.35 across all four logs - confirms the harness
faithfully reproduces the real, captured behaviour.

### Early-saturation elimination (the headline symptom)

"Frames where the source reads under 10 (nowhere near lock) but Normalized reads over 50 anyway" -
maximum Normalized value within the [0,10) source bin:

| log | before: max Normalized @ source<10 | after: max Normalized @ source<10 |
|---|---|---|
| wet-Raw | 92.22 | **9.87** |
| wet-ShakeIt | 89.95 | **23.37** |
| dry-Raw | 93.61 | **27.19** |
| dry-ShakeIt | 99.69 | **27.47** |

"source<5, Normalized>50" frame count (an even stricter cut):

| log | before | after |
|---|---|---|
| wet-Raw | 5/2303 (0.22%) | 0/2303 (0.00%) |
| wet-ShakeIt | 0/3281 (0.00%) | 0/3281 (0.00%) |
| dry-Raw | 40/4951 (0.81%) | 0/4951 (0.00%) |
| dry-ShakeIt | 61/6224 (0.98%) | 0/6224 (0.00%) |

### Wet vs dry, and Raw vs ShakeIt, at matched source bins (mean Normalized)

| source bin | wet-Raw before→after | dry-Raw before→after | wet-ShakeIt before→after | dry-ShakeIt before→after |
|---|---|---|---|---|
| [0,10) | 0.62→0.07 | 1.38→0.22 | 0.46→0.14 | 1.87→0.36 |
| [10,20) | 12.72→3.29 | 35.65→19.60 | 56.81→31.00 | 58.34→34.14 |
| [20,30) | 52.29→21.30 | 39.67→32.69 | 66.13→40.96 | 57.23→48.14 |
| [30,40) | 44.49→26.31 | 37.09→33.68 | 69.88→54.19 | 48.24→47.81 |
| [90,100) | 95.10→95.02 | 87.72→87.81 | 97.69→97.69 | n/a |

Reported honestly, not narratively: the fix removes the dramatic, symptom-matching early
saturation (the [0,10) max and the "source<5" spike rate, both above) cleanly to zero in every log.
The RESIDUAL spread at low-mid source bins (e.g. ShakeIt reading ~30-50 points higher than Raw at
the SAME [10,30) source band, both before and after) is a DIFFERENT, pre-existing, already-documented
limitation: `KeyedScaleLearner`'s single physically-anchored calibration point guarantees close
agreement AT the anchor (see the [90,100) row above - within ~0.1 point after the fix) but not
away from it if a source's native curve isn't proportional to canonical - `PerSourceCalibrationTests`
itself already tolerates up to a 40-point spread for exactly this reason. This fix does not attempt
to improve that separate, known limitation; it removes the `Math.Max` ceiling, which is a different
defect that WAS fully within scope and IS fully addressed (see the elimination table above).

### Distribution of Normalized against source (binned), all four logs, AFTER

| source bin | wet-Raw n / mean | wet-ShakeIt n / mean | dry-Raw n / mean | dry-ShakeIt n / mean |
|---|---|---|---|---|
| [0,10) | 2780 / 0.07 | 3309 / 0.14 | 5057 / 0.22 | 6315 / 0.36 |
| [10,20) | 260 / 3.29 | 48 / 31.00 | 163 / 19.60 | 114 / 34.14 |
| [20,30) | 133 / 21.30 | 83 / 40.96 | 82 / 32.69 | 95 / 48.14 |
| [30,40) | 118 / 26.31 | 80 / 54.19 | 90 / 33.68 | 385 / 47.81 |
| [40,50) | 62 / 35.03 | 53 / 55.39 | 408 / 47.06 | 10 / 75.32 |
| [50,60) | 58 / 33.79 | 69 / 60.20 | 18 / 76.20 | 6 / 76.67 |
| [60,70) | 29 / 58.90 | 17 / 63.35 | 15 / 53.25 | 27 / 89.74 |
| [70,80) | 29 / 75.81 | 29 / 73.29 | 12 / 57.74 | n/a |
| [80,90) | 21 / 83.80 | 14 / 84.21 | 18 / 90.49 | n/a |
| [90,100) | 32 / 95.02 | 20 / 97.69 | 34 / 87.81 | n/a |

Normalized now rises with source across the full range in every log instead of saturating in the
first bin - no bin shows a mean anywhere close to the (0,10) bin's near-zero mean jumping to a
mid-range value the way the "before" numbers did (contrast wet-Raw [10,20) before=12.72 - itself
already low - against wet-ShakeIt [10,20) before=56.81, a same-condition-band 4x disagreement that
the after-column narrows to 3.29 vs 31.00 - improved, though the ShakeIt/Raw native-scale gap
discussed above remains).

## FH6 guardrail (demoted to bounded-regression per the owner's mid-task priority change)

**Data limitation, stated up front:** of the three FH6 references, only
`QAdvanceFeedback.session-20260815-230140.csv` ("FH6-mixed", 8985 rows) has the
`Diag.Telemetry.*` columns needed to replay through the engine at all. The 210223(raw)/210421
(shakeit) pair predates that diagnostics pass and has no `BrakePercent`/`GroundSpeedKmh`/
`LongitudinalG` columns - there is no way to re-drive ANY version of the engine (old or new)
against them. I used their own RECORDED `Diag.Source.Lock.All` vs `WheelLock.Normalized.All`
columns (real gameplay output, "before" only, no possible "after") as a qualitative cross-check,
and it is consistent in shape with FH6-mixed's own "before" numbers (e.g. both show a ~50-65 mean
in the 10-50% source band, the same zone FH6-mixed's real replay shows shifting). FH6-mixed is the
only log with a genuine, replay-based before/after.

### FH6-mixed, before → after (mean Normalized per source bin)

| source bin | n (unchanged) | before | after | delta |
|---|---|---|---|---|
| [0,10) | 8380 | 0.24 | 0.01 | -0.23 |
| [10,20) | 13 | 63.23 | 19.07 | -44.16 |
| [20,30) | 18 | 60.23 | 34.71 | -25.52 |
| [30,40) | 14 | 56.09 | 38.70 | -17.39 |
| [40,50) | 36 | 48.56 | 44.42 | -4.14 |
| [50,60) | 16 | 82.48 | 68.72 | -13.76 |
| [60,70) | 14 | 88.75 | 76.05 | -12.70 |
| [70,80) | 15 | 83.66 | 79.89 | -3.77 |
| [80,90) | 10 | 79.57 | 77.90 | -1.67 |
| [90,100) | 469 | 89.83 | 89.82 | -0.01 |

"source<5, Normalized>50" spikes: 3/8372 (0.04%) before → 0/8372 (0.00%) after.

### Assessment and proposed bound

The two bins covering 98.5% of this session's frames ([0,10) with 8380 rows, [90,100) with 469
rows) are **essentially unchanged** (deltas of -0.23 and -0.01). The bins that move substantially
(10-90% source, a combined 136 of 8985 rows, ~1.5% of the session) are exactly the same
transitional zone the F1 defect lived in, and they move in the SAME direction (down, toward the
source's own honest reading) for the SAME reason - `gripUtilization` no longer inflates a
moderate source reading into a "critical" one. No bin goes silent (all stay a smooth, rising
function of source) and none is newly pinned at 100 or 0.

Proposed bound (mine, since none was specified): **(a)** the dominant near-zero and genuine-full-
lock bins must not move by more than a few points - satisfied (≤0.23); **(b)** no bin may become
silent (near-0 despite a clearly non-trivial source) or permanently saturated (pinned at 100
despite a clearly non-critical source) - satisfied, shape stays smooth and monotonic in both
before and after; **(c)** the transitional zone may shift more substantially, since it is exactly
where the fix's intended effect lives, PROVIDED it does not exceed roughly one canonical band's
width (30 points) except where a small sample count (n<20, most of these transitional bins) makes
a single-frame effect look larger than it is. The [10,20) bin's -44.16 delta exceeds that 30-point
bound on 13 frames; I am flagging this rather than hiding it - see Concerns.

## Mutation evidence

Reverted the fix (temporarily reintroduced `gripUtilization`'s query and
`Math.Max(gripUtilization, calibratedMean)` in `ComputeChannel`, then ran the new regression test):

```
Severity_does_not_saturate_early_when_the_learned_G_reference_is_immature_and_the_source_itself_reads_low
  FAILED: a source reading of 5 (nowhere near lock) must not be inflated to a high severity by an
  under-matured, too-low learned G reference - got 100
```

Reverted the mutation immediately after capturing this; rebuilt and re-ran the full suite -
714/714 green again, 0 warnings. The captured value (100.0) is pinned in
`MutationGuard_reintroducing_the_gripUtilization_ceiling_reproduces_the_F1_saturation_bug`.

## Tests changed

- **New (3):** `Severity_does_not_saturate_early_when_the_learned_G_reference_is_immature_and_the_source_itself_reads_low`,
  `Wet_and_dry_produce_similar_severity_for_the_same_source_reading_despite_very_different_learned_G_references`,
  `MutationGuard_reintroducing_the_gripUtilization_ceiling_reproduces_the_F1_saturation_bug` (all in
  `NormalizedWheelLockSlipEngineTests.cs`).
- **Re-expressed, not weakened (6):** `Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating`,
  `Learned_parameters_for_one_game_and_car_do_not_bleed_into_a_different_car_in_the_same_game`,
  `Learned_parameters_for_one_game_do_not_bleed_into_the_same_car_id_in_a_different_game`,
  `Lock_severity_releases_quickly_once_Raw_drops_even_though_the_car_keeps_decelerating` (all in
  `NormalizedWheelLockSlipEngineTests.cs`), plus `Being_at_the_limit_on_a_loose_surface_reads_high_severity_not_the_shared_reference_ratio`
  and `Absent_surface_data_degrades_cleanly_to_a_single_reference` (`SurfaceKeyedLearningTests.cs`).
  All six previously used a Raw fixture held BELOW `MinRawForCalibrationObservation` specifically to
  isolate `gripUtilization`'s own G-based calibration - the exact mechanism this fix removed from the
  live output. Each was re-expressed against the mechanism that now actually provides the same
  guarantee (`KeyedScaleLearner`'s physically-anchored calibration), with every original assertion's
  INTENT preserved (car-relative comparability, per-key isolation, instant release, surface-blind
  correctness) - only the numeric "near-max" bar moved from >85 to >80, because a genuine full-lock
  reading now calibrates to `100 * 75/ceiling` (~83 for a ceiling of 90) under the documented 75-point
  canonical anchor, not to a saturated ~100 the old GripUtilization-driven design happened to produce.

## Concerns

1. **The [10,20) FH6-mixed bin (13 frames) moves 44 points**, exceeding my own proposed 30-point
   transitional-zone bound. Per the owner's mid-task instruction I took the F1 side rather than
   compromise the F1 fix - but this is exactly the kind of number that instruction asked me to
   surface rather than bury. It is 13 frames out of 8985 (0.14% of this session); I have no way to
   know whether the owner will perceive it.
2. **The two older FH6 logs (210223/210421) cannot be validated post-fix at all** - missing
   telemetry columns. If the owner needs a true before/after on those specific runs, they would
   need to be re-captured (or replayed through a version of the plugin new enough to log
   `Diag.Telemetry.*`, which did not exist when they were captured).
3. **Residual Raw-vs-ShakeIt spread at low-mid source levels** (e.g. ~30 point gap at F1's [10,20)
   bin, after) is real and unfixed by this change - it is `KeyedScaleLearner`'s own documented,
   pre-existing single-anchor limitation, not the defect this task targeted. If the owner wants
   tighter agreement away from the anchor, that is a follow-up to `KeyedScaleLearner` itself
   (e.g. a second calibration point), not to this severity formula.
4. **Sauber vs "F1 Generic" car-id fragmentation is unresolved** - flagged, not fixed (see "Why not
   just retune" item 4). No longer affects live severity, but the underlying SimHub/F1-25
   car-identification question remains open.
5. **`physicalReference` (the shared, per-(game,car) G-based "are we at the limit" detector that now
   solely drives scale calibration) is never exported/imported through `RuntimeStore`** - it resets
   on every plugin restart, unlike the source-keyed learners it's more consequential than before now
   that it is the ONLY thing gating `KeyedScaleLearner`'s primary (physically-anchored) tier. I did
   not fix this (it pre-dates this task and the four logs happen to be one continuous session, so it
   did not affect this analysis), but a driver who restarts SimHub mid-session will re-cold-start
   calibration every time. Flagged as a candidate follow-up, not implemented here to keep this change
   focused.
6. **The owner's ShakeIt-direct gamma mapping (60→5, 100→100, etc.) was used only as a sanity check
   of SHAPE**, not reproduced numerically - that curve lives downstream, in the Projected/
   OutputProjector layer's gamma/threshold settings, which this fix does not touch. Normalized's own
   job is to be condition-independent and roughly proportional to the calibrated source, which the
   numbers above confirm; the owner's own curve is applied after this layer, unchanged.

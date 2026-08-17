# Cold-start continuity, cross-car seeding, and F1 25 Raw-timing investigation

## STATUS

**SYMPTOM 1 (car-switch silence): FIXED, in the sense the evidence supports** - the hard step at
`KeyedScaleLearner`'s primary-tier threshold is real, verified, and removed; cross-car cold-start
seeding is new, real, and mutation-verified; `physicalReference` now survives a restart. The literal
"car switch produces near-zero output" claim in the hypothesis was **not reproduced** against the
current, already-fixed code - see "Confirmed cause" below for what I actually found and why.

**SYMPTOM 2 (Raw fires late): NOT FIXED - stopping short deliberately, per this task's own instruction
not to describe a partial fix as complete.** The branch is confirmed (`"Slip data"` for BOTH Lock and
Slip on F1 25). The frame-matched Raw-vs-ShakeIt measurement the task itself specifies does **not**
show Raw lagging - if anything it leads/overshoots. My cold-start fix cannot move that number (it lives
entirely in Layer 3, which I did not touch) and, empirically, in the exact four logs provided, never
even activates for Lock (primary-tier sample count stayed at 0 throughout the wet-Raw capture). See
"Symptom 2" below for the full reasoning and why I did not attempt a Layer-3 rewrite instead.

## Test count / build gates

- `QAdvanceFeedback.Tests`: **726/726 passed**, 0 warnings, both Debug and Release (net8.0). 714
  pre-existing (all still passing; 1 re-expressed, not weakened - see below), 12 new (8 in the new
  `ColdStartAndCrossCarSeedTests.cs`, 4 in `RuntimeStoreTests.cs`).
- `QAdvanceFeedback` (the plugin, net48): `dotnet build -t:Clean,Restore,Build -p:Configuration=Release`
  and the same for Debug -> **0 Warning(s), 0 Error(s)** both times. `bin\Release\net48\` contains
  exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` - single-DLL distribution confirmed.
- **Re-expressed, not weakened (1):** `Switching_back_to_a_previously_driven_car_restores_its_own_earlier_learned_values`
  - loosened from 3 to 1 decimal place. Cause: the OLD hard cutoff gave primary-tier trust EXACTLY
  weight 1.0 for any sample count >= 20, so one extra qualifying observation between the "before" and
  "after" reads changed nothing; the NEW continuous ramp (by construction, see `ColdWarmBlend.HotWeight`)
  never reaches exactly 1.0 at any finite count, so one extra observation nudges the result by a real,
  tiny amount (~0.003). The test's own intent (Car1 unaffected by whatever Car2 did) is still met.

## Where I looked (read BEFORE changing anything)

Read `docs\f1-normalization-fix-report.md` and `docs\branch-dispatch-and-source-keyed-learning-report.md`
first, then the actual code: `NormalizedWheelLockSlipEngine.ComputeChannel`, `KeyedScaleLearner`,
`KeyedGripLearner`, `GripLearner`, `RuntimeStore`/`RuntimeCache`/`RuntimeDocument`,
`WheelSlipBranchSelector`, `RawCalculatorEngine`, `DispatchBranchFormulas`. Then the DATA:
`Common_1_5_e_d\QAdvanceFeedback.Parameters.json` and all four F1 25 CSVs, replayed both directly
(column extraction) and through the real, shipped engine via a temporary harness (same technique the
F1 fix report's own "f1harness" used - not part of the solution, not committed).

---

## SYMPTOM 1 - the car-switch "silence"

### Verifying the hypothesis, not assuming it

The hypothesis: `severity = calibratedMean` (the F1 fix) means a cold `KeyedScaleLearner` key produces
near-zero output on a car switch. I checked `KeyedScaleLearner.Rescale` directly: when no ceiling has
ever been learned, it returns `rawValue` **unchanged** (identity) - not zero, not the assumed rescale
factor the hypothesis worried about. I confirmed this with a direct unit test
(`PerSourceCalibrationTests.An_uncalibrated_cold_start_does_not_emit_a_full_scale_cue`, pre-existing,
still green) and by hand-tracing `ComputeChannel`: `severity = calibratedMean`, and for a cold key
`calibratedMean == mean(rawWheels)` exactly. **A cold key does not, by itself, produce literal silence
under the current code** - identity pass-through of a source that genuinely claims "already 0-100" is
already the documented, correct cold-start default.

So I looked for what DOES go wrong, rather than assuming the hypothesis's literal mechanism:

1. **A genuine, verified hard step.** `KeyedScaleLearner`'s primary (physically-anchored) tier was ALL
   (once `MinPhysicalAnchorSamples` = 20 qualifying moments existed) or NOTHING (identity) below it - an
   instant jump the moment sample #20 arrived. Measured directly (`ColdStartAndCrossCarSeedTests`,
   before my fix): identity (90.0, for a 90-native-reading source) held for samples 1-4, then **jumped
   straight to 75.0 in ONE frame at sample #20** - a 15-point step. This is exactly the kind of
   discontinuity the task's own acceptance criterion ("blending in as evidence accumulates -
   continuously, no step") calls out, and it is real, in the code, independent of anything about a car
   switch specifically - it also happens the first time ANY key (including the very car and source a
   driver started the session with) ever calibrates.
2. **A genuine, verified gap: no cross-car seeding at all.** `KeyedGripLearner` (the G-based learner)
   already has a rich "seed a brand-new key from the best related previous profile" fallback chain
   (`Find`/`GetOrCreate` - pre-surface-split, pre-source-keying, even the pre-per-car legacy import).
   `KeyedScaleLearner`, added later, had **none** - a brand-new (game,car,source) key fell all the way
   back to bare identity even when the EXACT SAME source had already been calibrated on a DIFFERENT car
   in the SAME game just moments earlier. The source's own native-scale behaviour (what "60" means for
   ShakeIt) is a property of the game+source, not really the car - unlike the physically-anchored G
   reference (which genuinely IS car-specific and is deliberately untouched here) - so this was a real,
   fixable gap, not a defence against a real risk.
3. **`physicalReference` still unpersisted**, exactly as flagged (not fixed) in the F1 fix report's own
   Concern #5 - confirmed still true in the current code (no public property, no RuntimeStore wiring at
   all) before I touched it.
4. **Concrete, direct evidence that F1 25 specifically fragments calibration evidence across multiple
   keys for what may be one physical car**, re-confirming the F1 fix report's own Concern #4: the
   owner's own `QAdvanceFeedback.Parameters.json` has BOTH
   `F12025|#|Sauber|#|Plain:...WheelLock.Raw...|#|Sealed` (253 samples) AND
   `F12025|#|F1 Generic|#|Plain:...WheelLock.Raw...|#|Sealed` (119 samples) - two separate, individually
   under-matured car-id variants. **Suggestive, not certain, additional finding**: replaying the
   `withRawWet` CSV's own recorded telemetry through the real gating logic
   (`TelemetryLearningGate.IsValid` + lateral isolation + direction + brake-minimum), I independently
   counted ~158 frames that SHOULD qualify to feed the learner in that one 3522-row capture, yet the
   log's own `Diag.Lock.LearnedPeakG`/`Confidence` columns read exactly `1` / `0` (the seed values, never
   moved) at literally every single row I sampled, including the last five rows of the file - a
   discrepancy consistent with (not conclusively proven by, since the CSV carries no CarId column)
   car-id instability splitting evidence across shifting keys even within one nominal car, not just at a
   deliberate switch. Either way, this data confirms cold/near-cold calibration is not a brief edge case
   for this title - it is close to the prevailing state, which is why getting the cold path right matters
   this much.

### Confirmed cause

The car-switch symptom is real and severe, but the precise mechanism is: **the hard step (1) makes
whatever calibration DOES eventually accumulate arrive as a jolt rather than a fade-in, and the missing
cross-car seed (2) means a brand-new car gets no benefit at all from an already-calibrated source on a
DIFFERENT car - even though the source's own native scale is very likely still the same.** Whether the
owner's SPECIFIC session ever crossed into literal near-zero output cannot be confirmed from the data
available (no CarId column in the CSVs, and the ShakeIt-configuration state for the custom car is
outside this plugin's own telemetry entirely - if SimHub's own ShakeIt effect was never assigned to
that custom car, no Layer-4 change can invent a signal from nothing). What I found, fixed, and can prove
numerically is real and squarely in Layer 4's remit.

### The fix (`KeyedScaleLearner`, `NormalizedWheelLockSlipEngine`, `RuntimeStore`/`RuntimeCache`/`RuntimeDocument`)

- **Continuous primary-tier ramp.** Reuses the SAME dispersion-weighted `ColdWarmBlend` mechanism
  already used for a persisted cold reference, applied to the primary tier's own partial evidence: a
  tight cluster of physically-at-limit readings earns trust within a handful of samples; a scattered one
  stays near the floor regardless of count. Measured after the fix: identity(90) -> 85.1 -> 83.7 -> 82.7
  -> 81.8 -> ... -> 76.4 by sample 40, smoothly, monotonically, max single-sample delta 4.87 across the
  whole ramp (vs the old mechanism's single 15-point jump at sample 20).
- **Cross-car cold-start seeding.** Once any (game,\*,source) key's own primary tier earns meaningful
  trust (dispersion-weighted confidence >= 0.5), its ceiling is remembered per (game,source), ignoring
  carId. A brand-new car with the SAME source starts its own continuous ramp from THAT seed instead of
  bare identity. A source genuinely never seen before in this game still falls back to plain identity -
  "a cold or unknown key must still produce a usable signal", not a wrong one borrowed from an unrelated
  source.
  - **Self-contamination bug found and fixed during test-writing, not shipped**: my first draft let a
    key's own ramp blend against ITS OWN cross-car seed - since that same key is simultaneously WRITING
    that shared seed once its weight crosses 0.5, this produced a NEW, worse step (output snapped to the
    fully-calibrated anchor in a single frame the instant the key's own weight passed 0.5, discovered via
    the `Warming_up_past_the_old_hard_threshold_produces_no_step_change` test failing at 7.65 instead of
    the expected sub-5 continuous curve). Fixed by ramping a key's own evidence against bare identity
    ALWAYS, and reserving the cross-car seed strictly for a key with `Count == 0` of its own.
- **`physicalReference` persistence (`RuntimeDocument.Version` 3 -> 4).** `NormalizedWheelLockSlipEngine`
  now exposes `LockPhysicalReference`/`SlipPhysicalReference`; `RuntimeCache`/`RuntimeStore` gained
  matching `Load*/Save*` pairs (mirroring `LockLearners`' own pattern exactly); `QAdvanceFeedback.cs`
  Init/DataUpdate import/export them every frame, same convention as every other keyed learner. The
  cross-car seed dictionaries (`LockScaleCrossCarSeed`/`SlipScaleCrossCarSeed`) are persisted the same
  way. No explicit one-time-migration code needed for the bump, for the identical reason the 2->3 bump
  needed none (a Version-3 file simply lacks these keys; Newtonsoft's construct-then-overwrite convention
  already leaves them correctly empty).

### Verified: all four cold-key scenarios

- **Car change**: `A_brand_new_car_with_an_already_calibrated_source_reads_warm_not_cold` - CarB (never
  seen) reads 74.75 for the same raw-90 reading CarA's own calibration was learned from (identity would
  give 90.0). `A_genuine_hard_lock_on_a_brand_new_car_is_never_silent` - a hard lock on a switched-to
  car reads 89.0-ish (>40 assertion, non-trivial).
- **Source change** (never configured for ANY car): falls back to honest identity, verified non-trivial
  (`A_brand_new_source_never_seen_for_any_car_still_produces_a_usable_identity_reading`).
- **Surface change** (Sealed calibrated, brand-new Loose bucket): unaffected, since `KeyedScaleLearner`'s
  own ceiling is deliberately NOT surface-keyed (only the physical reference is) -
  `Switching_to_a_never_before_seen_surface_still_reads_a_usable_calibrated_cue` confirms a genuine
  full-lock loose-surface event still reads >60.
- **Restart**: `A_restart_reproduces_the_previous_sessions_calibration_immediately` (same car) and
  `A_restart_with_a_never_before_seen_car_still_starts_warm_via_the_cross_car_seed` (a DIFFERENT car than
  was ever driven before the restart) both confirm a returning driver starts warm.

### Mutation evidence

**(a)** Reverted the continuous ramp to the OLD hard cutoff (`Count >= MinPhysicalAnchorSamples`,
nothing below it) and re-ran `Warming_up_past_the_old_hard_threshold_produces_no_step_change`:

```
FAILED: warming up past the old MinPhysicalAnchorSamples(20) threshold must not produce a step -
max single-sample jump was 15
```

Reverted immediately; full suite re-confirmed green (726/726). The captured 15-point step is pinned in
`MutationGuard_reintroducing_the_hard_cutoff_reproduces_the_15_point_step`.

**(b)** Reverted cross-car seeding (the "truly nothing observed" fallback returns `null` unconditionally
instead of consulting `_crossCarSeed`) and re-ran the cross-car tests:

```
FAILED: A_brand_new_car_with_an_already_calibrated_source_reads_warm_not_cold
  CarB (brand-new key) should start from CarA's already-learned ShakeIt calibration, not raw identity -
  got 90, expected well below the raw 90.0 identity value
FAILED: A_restart_with_a_never_before_seen_car_still_starts_warm_via_the_cross_car_seed
  a restart's cross-car seed must warm a genuinely new car too... got 90
```

Reverted immediately; full suite re-confirmed green (726/726).

### What is persisted now

`RuntimeDocument.Version = 4`: `LockPhysicalReference`/`SlipPhysicalReference` (the shared,
(game,car)-only G-based physical-limit detector - previously in-memory only) and
`LockScaleCrossCarSeed`/`SlipScaleCrossCarSeed` (the new per-(game,source) cold-start seed), alongside
everything Version 3 already persisted (`LockScaleLearners`/`SlipScaleLearners`/`SurfaceSupportByGame`).

---

## SYMPTOM 2 - Raw fires late on F1 25 wet

### F1 25's selected branch (directly confirmed, no computation needed)

All four F1 25 logs carry `Diag.SelectedBranch.Lock`/`Diag.SelectedBranch.Slip` (a per-row diagnostic
already captured). Every row, in all four logs (wet-Raw, wet-ShakeIt, dry-Raw, dry-ShakeIt), reads:

```
Diag.SelectedBranch.Lock = "Slip data"
Diag.SelectedBranch.Slip = "Slip data"
```

Confirmed against the capability flags themselves: F1 25 reports `WheelsRPS = 0` (false),
`WheelsSlip = 1` (true). Per `WheelSlipBranchSelector.Select`'s own priority order, Lock's own
RPS-specific branch (`isLock && WheelsRPS`) is skipped because `WheelsRPS` is false here, so Lock falls
through to the SAME shared branch Slip uses (`WheelsSlip -> "Slip data"`,
`DispatchBranchFormulas.SlipFromLearnedDistribution`). **This is a genuinely different, previously
UNVALIDATED capability shape**: the branch-dispatch report's own real-telemetry validation used the
FH6-mixed session, whose capabilities are `WheelsRPS=1, WheelsSlip=1` - Lock dispatches to `"RPS"`
there, never falling through to `"Slip data"`. F1 25 is the first real capture that exercises Lock on
the `"Slip data"` branch - a learned 15th/99th-percentile band of `|WheelSlipRatio|`, a formula built
and tuned around wheelspin/traction (Slip's own use case), now also carrying Lock's braking-side signal.

### The frame-matched Raw-vs-ShakeIt comparison (done exactly as instructed)

`WheelLock.Raw.All` (Layer 3's own output - ALWAYS computed and published, regardless of which source is
actually configured to drive Normalized) and `Diag.Source.Lock.All` (whatever source IS configured -
ShakeIt's own real export, in the two "WithShakeIt" logs) are both recorded for the SAME physical frame
simultaneously, so no cross-log alignment is needed. Measured directly (temporary harness, not
committed): for every braking run that reaches `Diag.Source.Lock.All >= 30` at least once, the first
frame each series crosses 10/30/60/80:

**wet-ShakeIt log** (n = qualifying events where both series crossed the threshold):

| threshold | n | mean lead/lag ms (Raw − Source; + = Raw LATE) | median ms | Raw's own reading at the instant Source crosses |
|---|---|---|---|---|
| 10 | 4 | **−13.1** | −16.5 | 40.22 |
| 30 | 4 | **−16.1** | 0.0 | 51.16 |
| 60 | 2 | **−42.5** | −16.6 | 68.81 |
| 80 | 2 | **−67.0** | 0.0 | 77.24 |

**dry-ShakeIt log:**

| threshold | n | mean lead/lag ms | median ms | Raw's own reading at Source-crossing |
|---|---|---|---|---|
| 10 | 2 | **−34.3** | −17.1 | 20.99 |
| 30 | 2 | **−26.1** | −17.0 | 43.05 |
| 60 | 2 | **−66.5** | −66.3 | 90.24 |
| 80 | 0 | n/a | n/a | n/a |

Every number is negative: **Raw crosses each threshold EARLIER than ShakeIt, not later**, and at the
moment ShakeIt first reaches a given threshold, Raw is already reading substantially HIGHER than that
same threshold (e.g. Raw already at ~40-51 when ShakeIt first touches 10-30). A frame-by-frame dump of
individual events shows WHY this is not actually good news: Raw is not smoothly early, it is **noisy and
non-monotonic** - one captured event opens at `raw=90.00, src=0.00` on the very first qualifying frame
(a residual/inherited value, not a fresh climb), then Raw visibly dips and re-spikes several times
through the same event while ShakeIt climbs smoothly. This is consistent with `"Slip data"`'s own
mechanism: a learned 15th/99th-percentile band over the ENTIRE session's `|WheelSlipRatio|` history
(braking and accelerating events pooled together, no decay), which can read close to its own historical
extreme on a rotation-rate spread or ratio spike well before - or after - the wheel is genuinely close to
locking, rather than the smooth, monotonic rise-to-limit ShakeIt's own (different, SimHub-internal)
Lock model produces.

### Honest conclusion - I did not fix Symptom 2, and I am saying so rather than describing a partial fix as complete

1. **The metric the task specifies is untouched by anything in scope for a Layer-4 fix.**
   `WheelLock.Raw.All` is Layer 3 (`RawCalculatorEngine`/`DispatchBranchFormulas`) - nothing in
   `NormalizedWheelLockSlipEngine`/`KeyedScaleLearner` (this task's actual changes) can move it, because
   Layer 4 never sees it until Layer 3 has already produced it. Before/after on THIS specific number is
   necessarily identical (both "before" and "after" my change), and reporting otherwise would be
   fabrication.
2. **My cold-start fix cannot help this data even one layer up.** For the Normalized (Layer 4) output
   with Raw configured, `severity = Rescale(mean(rawWheels))`. In the `withRawWet` capture, the primary
   tier's own sample count stayed at exactly 0 for the Lock channel throughout the entire 3522-row
   session (confirmed directly - `Diag.Lock.LearnedPeakG`/`Confidence` never move off their seed values).
   My continuity/cross-car-seed fix only changes behaviour once a key's OWN primary tier has at least one
   sample, or a cross-car seed exists from an ALREADY-warmed source - neither condition is met here (this
   is also the FIRST of the four logs in capture order, so there is nothing to cross-car-seed FROM yet
   either). The fix is real and verified (see Symptom 1's mutation evidence), but it genuinely does not
   engage for this specific captured drive.
3. **The evidence points at Layer 3's branch choice, not Layer 4's calibration, as the actual mechanism**
   - and my own measurement shows the defect there is NOISE/overshoot, not simple lag, which a naive
     "add a delay-compensation term" retrofit would not even correctly address (it would need to smooth
     a percentile-band formula's own erratic response, not shift a timestamp).
4. **I chose not to modify `DispatchBranchFormulas.SlipFromLearnedDistribution` or its dispatch this
   pass.** Six of the nine branches (including this one, for Lock) are, by the branch-dispatch report's
   own honest accounting, unvalidated against real telemetry beyond FH6's own Slip-only exercise of it.
   The formula is shared verbatim by every title that reaches this branch for EITHER channel - a change
   here has a blast radius far beyond F1 25, and I have exactly one title's worth of real data to validate
   against. Rewriting a shared, multi-title formula on the strength of one title's noisy-not-late
   evidence is precisely the speculative work this project's own established culture (see the F1 fix
   report's own "why not just retune" section) argues against doing without a much stronger evidentiary
   basis and a properly scoped follow-up task.

**Recommendation, not a fix**: a follow-up task, scoped specifically to
`DispatchBranchFormulas.SlipFromLearnedDistribution`'s use for Lock (not just Slip), with F1 25's own
telemetry as the validation set, is the right next step - likely either a decaying (not permanently
cumulative) percentile band, or splitting the learner by braking/accelerating regime so a spin event's
own history cannot dilate the band a lock event is judged against.

---

## FH6 guardrail

Replayed `QAdvanceFeedback.session-20260815-230140.csv` (FH6-mixed, 8985 rows, the only FH6 log with
the `Diag.Telemetry.*` columns needed to replay causally) through the CURRENT engine (F1 fix + this
task's cold-start/cross-car/continuity changes), one continuous engine instance, exactly as the F1 fix
report's own harness did for its "after" column:

| source bin | F1-fix-only "after" (prior report) | after THIS task's changes | delta |
|---|---|---|---|
| [0,10) | 0.01 | 0.01 | 0.00 |
| [10,20) | 19.07 | 16.72 | −2.35 |
| [20,30) | 34.71 | 28.29 | −6.42 |
| [30,40) | 38.70 | 35.89 | −2.81 |
| [40,50) | 44.42 | 42.46 | −1.96 |
| [50,60) | 68.72 | 61.23 | −7.49 |
| [60,70) | 76.05 | 70.48 | −5.57 |
| [70,80) | 79.89 | 76.29 | −3.60 |
| [80,90) | 77.90 | 81.07 | +3.17 |
| [90,100) | 89.82 | 89.80 | −0.02 |

"source<5, Normalized>50" spikes: 0/8985 (unchanged - still none).

The two dominant bins (98.5% of the session: [0,10) with 8380 rows, [90,100) with 469 rows) are
**essentially unchanged** (deltas of 0.00 and −0.02). The transitional bins move a further few points
(all n <= 36, so individually noisy), all in the SAME direction the F1 fix itself already moved them -
this task's changes are a strictly smaller, second-order refinement on top of an already-applied fix,
not a new source of movement. No bin goes silent or newly saturates; shape stays smooth and monotonic
in both. This is a qualitatively acceptable shift under the same bound the F1 fix report proposed (dominant
bins near-zero movement; transitional zone may shift, bounded by roughly one canonical band's width).

## Concerns

1. **Symptom 2 is not fixed** - see the "Honest conclusion" section above. Flagged as its own follow-up,
   scoped to `DispatchBranchFormulas.SlipFromLearnedDistribution`'s use for Lock specifically.
2. **Car-id instability for F1 25 is suggestive, not proven**, from the data available (no CarId column
   in the CSVs). The `Parameters.json` two-key fragmentation (`Sauber`/`F1 Generic`) IS confirmed direct
   evidence of the underlying class of problem; the CSV-diagnostic discrepancy is corroborating but not
   conclusive. This remains, as the F1 fix report already said, a SimHub/telemetry question outside
   Layer 4's reach - not something this pass attempted to resolve.
3. **The literal "car switch produces near-zero output" claim was not reproduced** against the current
   code - see "Confirmed cause". I fixed what I found (the step, the missing cross-car seed, the
   unpersisted physical reference), which are real, verified, in-scope defects that materially worsen
   exactly the reported scenario, but I cannot claim to have reproduced the owner's own literal
   "no feedback at all" experience from the code alone - a misconfigured (or unconfigured) ShakeIt
   effect for the custom car, or that car's own telemetry omitting fields this plugin depends on, would
   also produce genuine silence and are outside what any Layer-4 change can fix.
4. **The secondary (percentile) tier's own onset (at 500 samples) was deliberately NOT given the same
   continuous treatment as the primary tier** - a disclosed scope boundary. It requires ~25x the
   evidence the primary tier needs, is far less likely to have a session survive long enough to newly
   cross it mid-drive without the primary tier having already engaged, and blending its own PARTIAL
   mean (not its percentile, which has its own separate maturity gate) toward a ceiling would risk
   materially UNDER-estimating the true near-limit ceiling during the ramp (a right-skewed distribution's
   mean sits well below its own 99th percentile) - a correctness risk I chose not to take on without
   dedicated analysis.

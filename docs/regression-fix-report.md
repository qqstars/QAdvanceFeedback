# Regression fix report: CSV header-only, calibration never engaging, cold-start over-reporting

## STATUS

**ALL THREE REGRESSIONS FIXED, verified with mutation evidence, INCLUDING a follow-up redesign of the
Regression-2 calibration-confidence mechanism** (see "FOLLOW-UP" section near the end - the sample
threshold introduced to fix Regression 2 was itself a cliff, and then a second, over-engineered
"two-confidence-notion" design; both were replaced by a single, continuous, concave, dispersion-weighted
confidence with NO absolute-count gate anywhere, per the owner's own explicit, concrete specification).
`QAdvanceFeedback.Tests`: **776/776 passed**, 0 warnings, both Debug and Release (net8.0).
`QAdvanceFeedback` (the plugin, net48): `dotnet build -t:Clean,Restore,Build` for both Debug and
Release -> **0 Warning(s), 0 Error(s)** both times; `bin\Debug\net48\` and `bin\Release\net48\` each
contain exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` - **single-DLL distribution confirmed**
in both configurations. Baseline at the start of this task was 737/737; the original three-regression
pass added 20 new tests; the follow-up redesign below added a further **19** (15 in the new
`ConcaveHotWeightTests.cs`, 4 in `RegressionFixTests.cs`/`OnlineDistributionLearnerTests.cs`), for
737 + 20 + 19 = 776.

---

**NOTE ON READING ORDER**: the section immediately below ("REGRESSION 2") describes the FIRST fix
attempted for the calibration-never-engaging defect (a lowered absolute sample bar, 200 -> 60). The
owner's own follow-up correctly identified this as still a cliff, and a second attempt (a separate
continuous weight for the shared physical-limit detector) as a needless second confidence notion. The
CURRENT, SHIPPED mechanism is described in the "FOLLOW-UP: FROM SAMPLE BAR TO CONTINUOUS CONFIDENCE"
section near the end of this document - read that section for what actually ships. The section
immediately below is kept for its still-valid diagnostic work (WHY the primary tier never engaged) but
its OWN FIX (the 60-sample bar) has since been replaced.

---

*(For reference: after the ORIGINAL three-regression pass and before this follow-up, the count stood at
757/757 - 20 new tests over the 737 baseline: 18 in `RegressionFixTests.cs`, 2 in
`ColdStartAndCrossCarSeedTests.cs`, plus 2 pre-existing tests re-expressed because the owner's own
mid-task clarification explicitly reversed their asserted behaviour - see Regression 3 below.)*

---

## REGRESSION 1 - CSV export produced header-only output

### The cause, found directly (not assumed)

`PropertyPublisher.SnapshotAllValuesForCsv()` (the method "Export CSV" calls every frame) was **four
values short** of `AllPublishedProperties.AllNames(true)` (the method that writes the CSV **header**):
`Diag.GameId`, `Diag.CarId`, `Diag.Lock.SourceFallbackActive`, `Diag.Slip.SourceFallbackActive` were
added to `DiagnosticNames()` by the shakeit-silence-fallback task, but the matching values were never
added to the snapshot method (and the snapshot's own ordering for `SourceFallbackActive` no longer
matched the header's either). `CsvExportWriter.WriteRow` **silently no-ops on any column-count
mismatch** - a deliberate design choice ("a caller bug must never crash a live session") - so with
diagnostics enabled the header (N columns) and every row (N-4 columns) permanently disagreed, and
**every single row after the header was silently dropped, forever**, whenever "Export CSV" and "Enable
Diagnostics" were both on. Confirmed directly by diffing `AllPublishedProperties.DiagnosticNames()`
against `SnapshotAllValuesForCsv()`'s own value list, side by side - not by guessing which values might
be missing.

### The fix

Added the four missing values back into `SnapshotAllValuesForCsv()`, in the exact order
`DiagnosticNames()` yields them.

**Also refactored `PropertyPublisher` into two partial-class files**, specifically so this exact class
of bug becomes directly testable against the real production code (previously it could not be - the
whole file needed `SimHub.Plugins` to compile, which cannot be referenced from the net8.0 test project
without breaking test discovery for the entire assembly - see `PropertyPublisherStructureTests.cs`'s own
remarks):
- `PropertyPublisher.cs` (SimHub-dependent): only `Register`/`AttachTier`/`AttachTierNullable`.
- `PropertyPublisher.State.cs` (SimHub-free, new): every backing field, every `Update*` setter, every
  `*Snapshot` accessor, and `SnapshotAllValuesForCsv` itself - link-compiled into `QAdvanceFeedback.Tests`
  the same way `CsvExportWriter.cs`/`AllPublishedProperties.cs` already are.

This is a pure compile-unit split (partial classes share all members - no behavioural change) plus the
one line-count/line-order fix.

### Tests added (3, `RegressionFixTests.cs`)

- `SnapshotAllValuesForCsv_length_matches_the_full_diagnostics_enabled_property_count` - a direct,
  permanent guard: the snapshot's length must equal `AllNames(true).Count()`.
- `Exporting_with_diagnostics_enabled_writes_a_real_data_row_not_just_the_header` - the task's own
  explicit requirement: writes a REAL row end-to-end through the real `CsvExportWriter` +
  `PropertyPublisher`, with the full diagnostic set enabled, and asserts the file has 2 lines (header +
  one real data row), not 1.
- `MutationGuard_a_four_value_short_row_reproduces_header_only_output` - reproduces the exact pre-fix
  shape (a row 4 values short) directly against the real `CsvExportWriter` and confirms it degrades to
  header-only output.

### Mutation evidence

Reverted the fix (removed the 4 added `values.Add(...)` lines from `PropertyPublisher.State.cs`) and
re-ran `RegressionFixTests`:

```
FAILED SnapshotAllValuesForCsv_length_matches_the_full_diagnostics_enabled_property_count
  Expected: 158  Actual: 154
FAILED Exporting_with_diagnostics_enabled_writes_a_real_data_row_not_just_the_header
  Expected: 2  Actual: 1
```

The second failure is the reported symptom **verbatim** - a 1-line (header-only) file. Reverted
immediately after capturing this; full suite re-confirmed green (757/757).

---

## REGRESSION 2 - the primary (physically-anchored) calibration tier never engaged

### WHY it never engaged (measured directly, not assumed)

**The trigger condition (`physicalConfidence >= 1.0 && physicalRatioNow >= 0.85`) was reachable in
principle, but required far more sustained evidence than a realistic single session provides.** The
shared, (game,car)-only physical-limit detector (`_lockPhysicalReference`/`_slipPhysicalReference`, a
`KeyedGripLearner`) needed `GripLearner.MaturitySamples = 200` qualifying (triggered + engaged +
laterally-isolated + learning-gate-valid) observations before `Confidence` could ever reach 1.0 - and
`physicallyAtLimit` (the ONE thing that gates `KeyedScaleLearner.ObserveAtPhysicalLimit`, i.e. the
PRIMARY tier) requires FULL confidence, not partial.

Measured by replaying the owner's own four F1 25 logs through the **real, unmodified engine** (a
throwaway harness link-compiling `QAdvanceFeedback\Core\**\*.cs` directly, the same technique the prior
reports' own harnesses used - not part of the solution, not committed), with a fresh engine per log
(matching what the real captures actually showed - see below):

| log | qualifying samples this session | Confidence (200-bar) | Primary tier engaged? |
|---|---|---|---|
| wet-Raw | 158 | 0.790 | **No** (falls to identity) |
| wet-ShakeIt | 111 | 0.555 | **No** (falls to identity) |
| dry-Raw | 132 | 0.660 | **No** (falls to SECONDARY/percentile tier, ceiling 51.89) |
| dry-ShakeIt | 181 | 0.905 | **No** (falls to SECONDARY/percentile tier, ceiling 46.99) |

**Zero of four logs ever reach the primary tier** - not because the mechanism is broken, but because
one realistic ~56-second, hard-braking-dense session simply never accumulates 200 qualifying moments for
this specific, narrowly-gated detector. With the primary tier permanently unavailable and the secondary
tier itself gated at 500 samples (`OnlineDistributionLearner.MinSamplesForPercentile`), `calibratedMean`
is an **unrescaled identity pass-through** of the configured source's own native reading for the entire
session in half these logs - exactly why ShakeIt's own native ~20-30-at-lock-onset reading published as
Normalized ~20-30 instead of the canonical ~60 the owner expects, and why the owner's wet+Sauber+ShakeIt
case (previously lifted toward ~60 by the now-removed G-force `Max()` term) now reads near its own
unscaled native value.

*(Aside on why the RECORDED CSVs' own `Diag.Lock.LearnerConfidence` column reads literal 0.000 at every
row, not the 0.555-0.905 this harness measures: that column reflects whatever plugin BUILD was actually
installed at capture time, which predates this task's own analysis - the established project convention,
used by every prior report on these same four logs, is to replay the recorded telemetry through the
CURRENT engine rather than trust a possibly-stale recorded diagnostic column. This harness's own fidelity
is corroborated independently: the qualifying-sample counts (111-181) closely match the cold-start
report's own separately-derived ~158 estimate for `withRawWet`.)*

### The fix

Gave the physical-limit detector its **own, lower, explicitly-justified** maturity bar
(`NormalizedWheelLockSlipEngine.PhysicalReferenceMaturitySamples = 60`) via a new optional constructor
parameter threaded through `GripLearner` -> `KeyedGripLearner` -> the engine's two `physicalReference`
instances **only**. The two DIAGNOSTIC-ONLY G-learners (`_lockLearners`/`_slipLearners`, whose
`Ratio()`/`Confidence()` are no longer read for live severity per the F1 fix) keep the shared default 200
- unaffected, so their own diagnostic readout (`Diag.Lock.LearnedPeakG`/`LearnerConfidence`) is unchanged.

**Why 60, not lower or unchanged:** matches this project's own established "~15-25 qualifying samples
per ordinary braking zone, 3-7 zones to settle" convention (see `ColdWarmBlend.SampleSaturationK`'s own
remarks) - roughly 2-4 genuine braking zones. This detector's job is coarser than a diagnostic
G-learner's (it only decides "trust this frame enough to calibrate the scale learner", not "report the
definitive learned peak") and is already doubly protected downstream: `PhysicalLimitRatioThreshold`
itself (0.85, not 1.0) only accepts frames close to whatever peak IS learned, and
`KeyedScaleLearner`'s own continuous, dispersion-weighted ramp (never a step) further tempers exactly
how much a handful of early physical-limit observations are trusted for live severity.

**This does NOT reinstate `Math.Max(gripUtilization, calibratedMean)`** - severity is still, and remains,
exactly `calibratedMean`. The change is entirely upstream of severity, in whether/when
`KeyedScaleLearner`'s own calibration data gets a chance to be genuinely earned.

### Before/after Normalized distribution at near-limit frames (wet ShakeIt log, Raw > 50)

| | n | mean | in [50,70] |
|---|---|---|---|
| **Before** (200-sample bar) | 119 | 55.40 | 36 (30.3%) |
| **After** (60-sample bar) | 119 | 56.43 | 37 (31.1%) |

The bucket-mean shift looks modest here specifically because this particular capture's own
physically-anchored ceiling (57-70 native units) is not dramatically far from the canonical anchor (75) -
a MODEST rescale factor (1.07-1.31x) for THIS data. The more decisive, structural result is the tier
itself actually engaging - **0 of 4 logs primary-tier-engaged before, 4 of 4 after** - and the resulting
wet/dry consistency (below), which the pre-fix state could not have produced reliably at all (it depended
entirely on which tier - identity or the coarser secondary percentile - happened to be available for that
specific log).

### Wet/dry consistency preserved (the owner's explicit "do not reintroduce the inconsistency" requirement)

New test `Wet_and_dry_calibrations_stay_consistent_at_their_own_genuine_near_limit_reading`: two
independently-matured 80-sample sessions (wet at 3.2g/raw65, dry at 4.8g/raw90 - different native
readings at each one's own genuine physical limit) calibrate to within **<5 points** of each other
post-fix. Reverting the bar to 200 (mutation) reproduces a **25-point** gap (wet=65.00, dry=90.00 - pure
identity pass-through of two different native numbers, exactly the inconsistency the F1 fix was supposed
to eliminate).

### FH6 guardrail (unaffected)

Replayed `QAdvanceFeedback.session-20260815-230140.csv` (FH6-mixed, 8985 rows) through the real engine,
before (200-bar) and after (60-bar): dominant bins essentially unchanged ([0,10) meanNorm 3.83->3.59,
[90,100) 86.64->86.65); "source<5, Normalized>50" count **371 before -> 370 after** (a 1-frame,
noise-level difference - this fix does not meaningfully move this metric). Note this count is **no
longer 0** as the two earlier reports measured, but that is because those reports pre-date the
shakeit-silence-fallback feature entirely - confirmed directly: `engine.LockSourceFallbackActive` fires
on 382/8985 frames in this replay, matching the shakeit-silence-diagnosis report's own independent
prediction of 381 affected frames almost exactly. The "spikes" are the fallback correctly substituting a
genuinely-high Raw reading for a dropped-out configured source - the intended, already-shipped behaviour
of an earlier task, not a regression from this one. Flagged in Concerns for visibility.

### Tests added (3, `RegressionFixTests.cs`)

- `A_realistic_single_session_braking_count_is_enough_for_the_primary_tier_to_engage`
- `Wet_and_dry_calibrations_stay_consistent_at_their_own_genuine_near_limit_reading`
- `MutationGuard_reverting_to_the_200_sample_bar_reproduces_the_non_engagement`

### Mutation evidence

Reverted `PhysicalReferenceMaturitySamples` 60 -> 200 and re-ran `RegressionFixTests`:

```
FAILED A_realistic_single_session_braking_count_is_enough_for_the_primary_tier_to_engage
  the primary (physically-anchored) tier must engage within a realistic single-session braking count...
FAILED Wet_and_dry_calibrations_stay_consistent_at_their_own_genuine_near_limit_reading
  wet and dry must calibrate their OWN genuine near-limit reading to approximately the same canonical
  anchor - wet=65.00 dry=90.00
```

Reverted immediately after capturing this; full suite re-confirmed green (757/757).

---

## REGRESSION 3 - cold-start over-reporting (hard shake on the first 1-2 braking events)

### The cause

`KeyedScaleLearner`'s cross-car seed (a ceiling borrowed from an already-calibrated DIFFERENT car with
the same game+source) was applied **at full strength, uncapped, the instant a brand-new car's own
primary tier had ZERO local evidence** (`primary.Count == 0`). If the borrowed seed's native ceiling was
LOWER than the canonical anchor (75), `Rescale`'s factor (`75 / ceiling`) exceeds 1 - i.e. it
**amplifies** a brand-new car's very first genuine reading, producing a full-strength cue despite the car
having plenty of grip and zero evidence of its own yet - exactly the reported "wheel totally fine, a lot
of grip, hard vibrating" symptom on the first 1-2 corners.

### The fix (gated, per the owner's explicit clarification - not removed outright)

**Cold state before ANY local evidence for the exact key must be plain identity** - this is now true
without exception. The cross-car seed is GATED so it can only ever nudge the ramp's own starting point
**once the current key has recorded at least one physical-limit observation of its own**, blended in by
the SAME weight that already governs how much of this car's own evidence is trusted (continuous, not a
second step), and **capped at the canonical anchor (75)** so it can only ever REDUCE the rescale factor,
never amplify, until this car's own fully-earned average takes over (which MAY legitimately push the
ceiling below 75 once genuinely earned - that is the calibration Regression 2 needs, never a borrowed
guess):

```
anchor   = Blend(CanonicalAtLimitAnchor, Max(seed, CanonicalAtLimitAnchor), weight)
ceiling  = Blend(anchor, ownAverage, weight)
```

At `weight = 0` (zero local evidence) this is EXACTLY `75 -> 75 -> identity`. As this car's own evidence
grows, the seed's (capped, safe) influence grows first, then the car's own genuinely-earned average takes
over.

**I gated it rather than removing it entirely** because removing it outright reintroduces the OTHER
already-documented defect (a brand-new/custom car reading total silence until its own evidence matures) -
gating preserves the "a little evidence helps sooner than pure identity would" benefit while eliminating
the "full-strength false alarm from zero evidence" defect.

### `physicalReference` persistence, re-examined per the coordinator's request

A persisted ceiling for the EXACT SAME (game,car,source) key from a **different weather/surface
condition** (weather/surface is deliberately not part of `KeyedScaleLearner`'s key - an already-flagged,
pre-existing limitation, see `shakeit-silence-diagnosis-report.md`'s own "Weather not in the learning
key" section) could in principle inflate a session driven in a different condition. **I did not weaken
the warm-resume guarantee to address this** - the owner's own explicit acceptance criteria for the
mid-task "verify warm-resume" clarification require EXACT reproduction of a persisted, same-key
calibration on a mid-session switch, with no carve-out for "unless it was learned in a different
condition." Flagged as a known, pre-existing, unresolved risk (not new, not touched this task) rather
than silently resolving the tension by weakening either guarantee - see Concerns.

### Warm mid-session resume - VERIFIED, not assumed

Traced the actual code path (`QAdvanceFeedback.cs`'s `Init`): every learner's persisted state
(`KeyedGripLearner`, `KeyedScaleLearner`'s cold ceilings, `physicalReference`, cross-car seeds) is loaded
via `ImportAll`/`ImportCrossCarSeeds` **once, with the FULL persisted dictionary**, not per-key on
demand. Since every lookup (`GetOrCreate`/`Find`/`PublishedCeiling`) queries these SAME in-memory
dictionaries by composite key, **any** key switch mid-session (car, game, source, or surface) that
matches an already-imported key finds it immediately - by construction, not by a special case. Also
confirmed `KeyedScaleLearner.PublishedCeiling`'s cold/hot blend returns EXACTLY the persisted cold value
whenever this session's own hot weight is 0, regardless of what the (now-gated) cross-car seed
independently computes for "hot" - `ColdWarmBlend.Blend`'s own `hotWeight <= 0 -> return cold` short
circuit. Verified with 5 new tests (persisted-then-switched-mid-session, switch-away-and-back exact
reproduction, no-persisted-data starts at identity, first-seen-mid-session-then-warm-on-next-visit, and a
mutation guard simulating "ignore persisted data on a key change") - all pass; the mutation guard
confirms a fresh, non-imported instance reads identity (40) instead of the persisted, warm calibration
(>65), reproducing exactly the failure mode the coordinator asked to guard against.

### Tests added/re-expressed (6 in `ColdStartAndCrossCarSeedTests.cs`, 5 in `RegressionFixTests.cs`)

- Re-expressed (behaviour the owner's clarification explicitly reversed):
  `A_brand_new_car_with_zero_local_evidence_reads_plain_identity_even_with_an_already_calibrated_source`,
  `A_restart_with_a_never_before_seen_car_still_starts_at_identity_despite_a_restored_cross_car_seed`.
- New: `A_brand_new_cars_own_first_few_observations_gradually_pull_toward_a_capped_cross_car_seed`,
  `MutationGuard_ungating_the_cross_car_seed_would_apply_a_borrowed_scale_before_any_local_evidence`.
- New (`RegressionFixTests.cs`): `With_no_learned_scale_Normalized_equals_the_source_value_exactly`
  (Theory, 0/1/30/60/80/100), `A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event`
  (full 0->100->0 ramp, asserted every frame), `A_car_with_a_persisted_entry_resumes_warm_on_a_mid_session_switch_not_identity`,
  `Switching_away_and_back_within_one_session_reproduces_the_first_visits_mapping_exactly`,
  `A_key_with_no_persisted_entry_starts_at_identity`, `A_key_first_seen_mid_session_persists_and_is_warm_on_the_next_visit`,
  `MutationGuard_a_fresh_learner_instead_of_the_imported_one_reproduces_a_cold_restart`.
- The existing continuity test (`Warming_up_past_the_old_hard_threshold_produces_no_step_change`) and its
  own mutation guard are untouched and still pass unmodified - the continuous-ramp mechanism itself was
  not changed, only the cross-car seed's role within it.

### Mutation evidence

Reverted the gate to the OLD, unconditional, uncapped cross-car-seed mechanism and re-ran the affected
tests:

```
FAILED A_brand_new_car_with_zero_local_evidence_reads_plain_identity_even_with_an_already_calibrated_source
  Expected: 90  Actual: 75
FAILED A_restart_with_a_never_before_seen_car_still_starts_at_identity_despite_a_restored_cross_car_seed
  Expected: 90  Actual: 75
FAILED A_brand_new_cars_own_first_few_observations_gradually_pull_toward_a_capped_cross_car_seed
  Expected: 90  Actual: 75
FAILED MutationGuard_ungating_the_cross_car_seed_would_apply_a_borrowed_scale_before_any_local_evidence
  Expected: 50  Actual: 100
```

The last failure is the smoking gun: a car with ZERO evidence of its own, querying a raw reading of 50,
published **100** (clamped from 50 * (75/30) = 125) instead of the honest 50 - a **doubling** of the
reported severity from a borrowed, unearned scale. Reverted immediately after capturing this; full suite
re-confirmed green (757/757).

---

## Concerns

1. **The FH6 "source<5, Normalized>50" count is no longer 0** (371/8985, essentially unchanged by this
   task's own fixes) - this is the already-shipped shakeit-silence fallback correctly un-silencing a
   dropped-out source, not a new regression, but it means the specific numeric guardrail the earlier
   reports established ("0 spikes") is stale now that the fallback exists. I did not attempt to "fix" this
   back to 0, since doing so would mean breaking the fallback's own intended behaviour.
2. **`physicalReference`/`KeyedScaleLearner`'s persisted-cold-ceiling mechanism can still be inflated by a
   different weather/surface condition sharing the exact same (game,car,source) key** (weather/surface is
   not part of that key - a pre-existing, twice-already-flagged limitation, not touched this task). I
   deliberately did not weaken the "exact warm resume" guarantee to guard against this, per the priority
   the owner's own mid-task clarification placed on warm resume being exact. This remains open.
3. **SUPERSEDED** - `PhysicalReferenceMaturitySamples = 60` (an absolute sample-count bar) was replaced
   entirely by the follow-up redesign below after the owner correctly pointed out it was still a cliff,
   and worse, tuned against one title's own qualifying-sample rate. See "FOLLOW-UP: FROM SAMPLE BAR TO
   CONTINUOUS CONFIDENCE" near the end of this document.
4. **The `KeyedScaleLearner.PublishedCeiling`'s `isPrimaryTier` diagnostic output can be mislabelled** in
   one narrow case (a persisted-cold key whose "hot" computation independently resolves to a cross-car
   seed or the primary tier, even though the VALUE actually returned is the unrelated persisted cold
   ceiling) - this is a pre-existing, cosmetic, diagnostic-only inaccuracy (does not affect the actual
   Rescale computation), not introduced by this task. Not fixed, to keep this change scoped.
5. **The four owner-provided F1 25 logs still predate `Diag.GameId`/`Diag.CarId`**, so the already-flagged
   car-id-fragmentation question (Sauber vs "F1 Generic") remains unresolved for this specific data - a
   fresh capture with the current build would settle it directly, as the shakeit-silence-diagnosis report
   already noted.

---

## FOLLOW-UP: FROM SAMPLE BAR TO CONTINUOUS CONFIDENCE (the shipped Regression-2 mechanism)

The owner asked three things, in order: (1) explain WHY only 111-207 qualifying samples come out of a
real F1 25 session with real numbers, (2) replace the sample-count cliff (200, then 60) with a
continuously-growing confidence that starts low and never gates on an absolute count, and (3) do this in
a way that works for ANY title, not one re-tuned to F1's own qualifying-sample rate. A third message then
specified the blend concretely. This section documents what shipped after all three rounds.

### 1. WHY only ~24-45 qualifying samples per braking zone (real numbers, all four F1 logs)

"Qualifying" = passed `TelemetryLearningGate.IsValid` (not in pit/replay/restart, plausible dt, ground
speed >= 5 km/h, no teleport-sized speed jump) AND the channel's own pedal minimum
(`BrakePercent >= 10`) AND laterally isolated (`|LateralG| <= 0.5`) AND direction == Slowing - the EXACT
gate `NormalizedWheelLockSlipEngine.ComputeChannel` uses before ever calling `Observe` on anything.
Measured directly against the recorded telemetry columns (not the engine's own diagnostic columns, which
predate this analysis - see the original Regression-2 section's own aside on why):

| log | total frames | qualifying frames | qualifying braking zones | avg qualifying frames/zone | qualifying fraction |
|---|---|---|---|---|---|
| wet-Raw | 3522 | 176 | 6 | 29.3 | 5.0% |
| wet-ShakeIt | 3722 | 135 | 3 | 45.0 | 3.6% |
| dry-Raw | 5897 | 171 | 7 | 24.4 | 3.0% |
| dry-ShakeIt | 6952 | 207 | 9 | 23.0 | 3.0% |

**No gate condition is unreasonably strict here** - the qualifying fraction (3-5% of all frames) is
exactly what you would expect from "frames where the driver is meaningfully braking, in a straight line,
at a plausible speed" out of a full lap/session that also contains full-throttle straights, cornering,
and coasting. The bar is not the PERCENTAGE that is the problem - it is that an ABSOLUTE bar of 200 (or
even 60) samples, at ~24-45 samples per zone, needs 5-9 FULL braking zones before crossing it at all, and
a session this length (56-116 seconds) simply may not contain that many. This is a genuinely different
finding from "the gate rejects frames it shouldn't" (checked and ruled out) - it is "a fixed count doesn't
scale to how sparse genuine qualifying evidence naturally is, and that sparsity rate itself varies by
track/style/session length, so ANY single absolute number risks being wrong for some other combination."

### 2. The design: a single, continuous, concave, dispersion-weighted confidence - no absolute-count gate

**The two prior attempts, both retired:**
1. `PhysicalReferenceMaturitySamples` (200, then 60) - a hard boolean gate on the SHARED physical-limit
   detector's own linear `Confidence`. Retired: still a cliff, and an absolute count tuned to one title.
2. A SECOND continuous weight (`GripLearner.HotEvidenceWeight`, reusing the OLD `ColdWarmBlend.HotWeight`)
   computed for the SAME shared detector, layered on top of `KeyedScaleLearner`'s own existing continuous
   ramp. Retired: this is exactly the "second confidence notion" the owner said to avoid - two different
   continuous weights answering overlapping questions, doubling the surface area for nothing.

**What ships:** exactly ONE continuous confidence computation, inside `KeyedScaleLearner`'s own primary-
tier ramp (`LearnedCeilingForKey`), combining:

- **A CONCAVE (front-loaded) count-confidence curve**, `ColdWarmBlend.ConcaveCountConfidence(count, scale, gamma=2)`
  = `1 - (1-x)^2` where `x = min(count/scale, 1)`. This is the owner's own concrete specification
  (`blended = current - (current-observed)*weight`, algebraically identical to the pre-existing
  `ColdWarmBlend.Blend(cold, hot, weight)` this class already used) with a CONCAVE, not linear, weight
  shape, matching their own worked anchors closely: f(0.1)=0.19 (target ~0.18), f(0.5)=0.75 (target
  ~0.75, exact); f(0.3)=0.51 runs a bit under their own rough ~0.60 suggestion, but `gamma=2` is the
  simplest concave shape that is easy to reason about and unit-test directly, preferred over hand-fitting
  all three anchors simultaneously (which would itself be over-tuned to one example). Not using a linear
  weight (the owner's own explicit instruction) is honoured: a linear weight would give 10% of the way to
  the scale reference only 10% confidence, not the front-loaded ~19% the concave curve gives - proven
  directly in `ConcaveHotWeightTests.MutationGuard_a_linear_weight_would_not_front_load_early_evidence`.
- **Dispersion quality**, `ColdWarmBlend.DispersionQuality(cv)` - factored OUT of the EXISTING
  `ColdWarmBlend.HotWeight` (which still uses it, unchanged, for its own unrelated cold/hot persistence
  blends) rather than duplicated, per the owner's own "reuse the existing dispersion-weighted mechanism"
  instruction. `weight = ConcaveCountConfidence(...) * DispersionQuality(...)` - **150 tight, consistent
  observations earn high weight (>0.8); 150 scattered, noisy ones stay near identity (<0.2) regardless of
  count** - proven directly in `ConcaveHotWeightTests.The_same_sample_count_earns_far_less_weight_when_the_evidence_is_scattered`,
  with a paired mutation guard confirming count-confidence ALONE (no dispersion) cannot tell them apart.
- **`CalibrationConfidenceScaleSamples = 200`** - the owner's own literal example scale, matching their
  worked anchors ("10 samples (10/200)... 150 samples (150/200)... >=200 -> weight 1.0") - kept as a
  SEPARATE constant from the pre-existing `MinPhysicalAnchorSamples` (20, a discrete
  persistence-labelling cutoff, untouched) so an unrelated future change to one cannot silently retune the
  other. **This is a SOFT SCALE REFERENCE, NOT A GATE**: the curve already contributes non-zero weight
  from the very first two qualifying samples (dispersion needs >=2 to be defined at all - below that,
  weight is exactly 0, the one deliberate "truly no evidence yet" floor, matching identity exactly) and
  simply keeps approaching 1.0 (reaching it exactly at/beyond 200, per the owner's own explicit
  ">=200 -> weight 1.0" instruction) rather than requiring 200 to do anything at all.
- **No game-specific constants anywhere.** `ConcaveCountGamma` (2.0) and `CalibrationConfidenceScaleSamples`
  (200) are plain class-level constants, not keyed by `gameId` - the SAME curve applies to every title.
  Audited every constant touched this session (`SampleSaturationK`, `DispersionHalfLifeCv` - pre-existing,
  untouched; `ConcaveCountGamma`, `CalibrationConfidenceScaleSamples` - new, both title-agnostic) -
  none is fit to F1 25's own qualifying-sample rate specifically.

The shared physical-limit detector's own `Ratio()` call is still made WITHOUT its own linear
confidence-based ceiling (`applyColdStartCeiling: false`) - so "is this frame near what we've learned so
far" is answered honestly at any sample count - but every qualifying "at the limit" frame now teaches
`KeyedScaleLearner` at plain, full weight (1.0); the ONE place a continuous trust judgement is made is
`KeyedScaleLearner`'s own ramp described above. A virgin detector's own inflated ratio (dividing by an
unlearned seed peak) can look "at the limit" readily in the first few frames of a session, but this is
harmless by construction: `KeyedScaleLearner`'s OWN count is what stays low regardless, so a handful of
early, possibly-unrepresentative teachings cannot move the published ceiling meaningfully until real,
dispersion-confirmed evidence accumulates for this exact key.

### 3. Verification - all five logs (four F1 + FH6), fresh engine per log (matching what the real captures showed)

| log | braking zones | ceiling stabilizes (within 10% of final) at zone | final ceiling (native units) | IsPrimaryTier |
|---|---|---|---|---|
| wet-Raw | 9 | **1** | 69.3 | True |
| wet-ShakeIt | 9 | **1** | 69.9 | True |
| dry-Raw | 12 | **1** | 60.3 | True |
| dry-ShakeIt | 15 | **1** | 64.1 | True |
| FH6-mixed | 31 | **1** | 69.1 | True |

**All five logs - two different titles - become useful within the FIRST braking zone**, not after
several, and the calibrated ceiling ends up in a consistent 60-70 native-unit range across every log,
every source (Raw/ShakeIt), every condition (wet/dry), and both titles (F1 25/FH6). This directly answers
the owner's own cross-title concern: the mechanism is NOT F1-shaped - it reaches useful weight early on
BOTH titles tested, with no re-tuning between them.

**Wet/dry consistency** (150-qualifying-sample test, matching a realistic multi-zone session): wet and dry
calibrate their own genuine near-limit reading to within <5 points of each other
(`RegressionFixTests.Wet_and_dry_calibrations_stay_consistent_at_their_own_genuine_near_limit_reading`,
passing).

**FH6 guardrail**: dominant bins unchanged ([0,10) meanNorm 3.50, [90,100) meanNorm 86.63 - both
consistent with the original Regression-2 fix's own numbers); "source<5, Normalized>50" count unchanged
at 370/8985 (still the already-shipped shakeit-silence fallback correctly un-silencing a dropped-out
source, not a new regression - unaffected by this redesign, confirmed by direct comparison).

**Continuity sweep** (the test that would have caught the original cliff):
`RegressionFixTests.Calibration_confidence_grows_continuously_with_no_jump_at_any_sample_count` sweeps a
fresh engine through 150 consecutive qualifying observations, probing the published severity after each
one, and asserts the maximum single-sample delta stays under 6.0 points - **passing**, including across
the weight=1.0 saturation point (the curve's own derivative is exactly 0 there, a smooth plateau, not a
corner).

**Persistence unaffected**: removing the (now fully retired) hard gate only ever affected a genuinely
first encounter (new game AND new car, nothing on disk, zero in-session evidence) - every mid-session key
switch to an ALREADY-persisted key still resumes warm immediately, because `KeyedScaleLearner.PublishedCeiling`'s
own cold/hot blend returns the persisted value exactly whenever this session's own hot weight for that
EXACT key is 0 (`ColdWarmBlend.Blend`'s own `hotWeight <= 0 -> return cold` short-circuit, untouched by
any of this) - confirmed directly by `RegressionFixTests.A_car_with_a_persisted_entry_resumes_warm_on_a_mid_session_switch_not_identity`
and `Switching_away_and_back_within_one_session_reproduces_the_first_visits_mapping_exactly`, both green.

**A title with very few qualifying samples**: `RegressionFixTests.A_source_with_very_few_qualifying_samples_stays_near_identity_and_remains_usable`
feeds only 3 qualifying observations (far below any bar ever considered, 20/60/200) and confirms the
output stays near identity (>80, i.e. close to the honest raw 90) and NEVER exceeds the source's own
reading. **The answer is, and must be: it stays near identity and remains usable - it does not degrade,
go silent, or need to "unlock" anything.**

### 4. Mutation evidence

**(a) Linear weight instead of the concave curve** -
`ConcaveHotWeightTests.MutationGuard_a_linear_weight_would_not_front_load_early_evidence`: a linear weight
at 10% of the scale reference gives exactly 0.10; the shipped concave curve gives 0.19 - the early-evidence
responsiveness the owner's own worked example depends on is proven to come specifically from the concave
shape, not merely from "some weight."

**(b) Dropping the dispersion factor** -
`ConcaveHotWeightTests.MutationGuard_dropping_dispersion_would_treat_scattered_and_tight_evidence_identically`:
count-confidence alone (no dispersion) rates 150 samples at >0.8 REGARDLESS of how scattered they are,
while the shipped mechanism (count * dispersion) correctly distinguishes tight (>0.8) from scattered
(<0.2) evidence at the identical count.

**(c) Reinstating a hard threshold** - temporarily replaced `KeyedScaleLearner`'s own weight computation
with `primary.Count >= 100 ? 1.0 : 0.0` and re-ran the continuity sweep test:

```
FAILED Calibration_confidence_grows_continuously_with_no_jump_at_any_sample_count
  no single additional qualifying sample may move the published severity by more than a small,
  continuous step - max single-sample jump was 15.00
```

Reverted immediately after capturing this; full suite re-confirmed green (776/776). The captured 15-point
jump is pinned in `RegressionFixTests.MutationGuard_reinstating_a_hard_threshold_reproduces_a_15_point_jump`.

### Concerns (follow-up)

1. **`gamma=2` and `CalibrationConfidenceScaleSamples=200` are still choices**, not derived from first
   principles - they match the owner's own worked example closely (exact at the 50% anchor, close at 10%,
   a bit under their own rough suggestion at 30%) but a different concave exponent or scale could also be
   defended. I did not exhaustively search for an "optimal" pair, per the same reasoning the original
   report already gave for its own now-retired constant.
2. **The shared physical-limit detector's own `Ratio()` is now permissive on a virgin key** (no ceiling at
   all, since `applyColdStartCeiling: false`) - relying entirely on `KeyedScaleLearner`'s own low initial
   count to keep this harmless. This is a deliberate simplification (one confidence notion, not two) but
   means a title whose FIRST few qualifying frames happen to be unusually consistent (low dispersion)
   could in principle earn a little more trust a little faster than a title whose early frames are noisy
   - this is the INTENDED behaviour (tight evidence should count for more), not a defect, but worth
   naming as a real consequence of removing the second gate.
3. **The 30%-anchor gap (51% vs the owner's own rough ~60% suggestion)** is disclosed, not hidden - see
   point 1. If tighter alignment to that specific anchor is wanted, revisiting `gamma` (something between
   2 and 2.5) would move the 30% point closer at some cost to the 10%/50% anchors' own current close fit.

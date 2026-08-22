# Stable-denominator grip-utilization sub-limit band - the sixth attempt, EVALUATED, NOT SHIPPED

## STATUS

**EVALUATED AND REJECTED. NOTHING SHIPPED. REPOSITORY CONFIRMED BACK TO ITS PRE-TASK STATE.**

This task's ONE targeted defect - the fifth attempt's (`docs\grip-utilization-band-report.md`) use of
`GripLearner`'s own live, continuously-updated decaying-maximum peak reference as a per-frame
utilization DENOMINATOR - was diagnosed, fixed with a new STABLE (event-ratcheted) reference, proven
correct at the unit level (the ratchet provably cannot decay within one event, contrasted directly
against the live reference's own measured erosion), and then measured against the project's own real
captured F1 25 logs using the ACTUAL compiled engine. **The fix is real and load-bearing (proven by
mutation), and it dramatically improves on the fifth attempt's own numbers (time above 80: 33.9% vs
44.6%; Phase-1 80-100%: 28.8% vs 62.0%) - but it still does not beat the CURRENT SHIPPED BASELINE on
any of the three headline metrics.** Per this task's own explicit instruction ("ship only if it beats
the CURRENT shipped baseline... if it still doesn't work, ship nothing, revert cleanly, and report with
numbers"), **nothing was shipped**. Every file touched has been restored to be byte-identical to the
pre-task baseline (`QAdvanceFeedback_1.0.6.6_speed_aware`, confirmed via `diff -rq`, excluding
`bin`/`obj`/`scratchpad`).

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **868/868 passed, 0 failed, 0 skipped**
(unchanged from the starting state). `QAdvanceFeedback\bin\Release\net48\` contains exactly
`QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**. No git operation of any kind was run (the
repo is not under git). No recursive deletes. No raw CJK was written (none needed).

A second, separate diagnostic was requested mid-task (the `physicallyAtLimit` gate's own use of the
same decaying max) - investigated on the real logs, evidence came back **weak/contradictory**, so **no
code change was made there either**, per that request's own explicit instruction. See Part 5.

---

## PART 0 - WHAT WAS BUILT (before it was rejected)

- `GripLearner.StableReferencePeakG(gMech, k, speedKmh)` (new static method) - the same
  `gMech + k*v^2` shape `SpeedAwarePeakG` already uses, but taking the model's two parameters as
  explicit arguments so a caller can evaluate a FROZEN/ratcheted snapshot rather than this instance's
  own live, continuously-updated parameters.
- `GripLearner.RatioAgainstStableReference(magnitudeG, referencePeakG, applyColdStartCeiling)` (new
  public method, `Ratio` refactored to share a private `ComputeRatioAgainstReference` helper) - same
  clamp/cold-start-ceiling shape as `Ratio`, dividing into a caller-supplied reference instead of this
  instance's own live one.
- `KeyedGripLearner.PublishedGMech`/`PublishedK`/`RatioAgainstStableReference` - thin pass-throughs
  exposing the two above at the keyed level, mirroring `PublishedPeakG`'s own read-only pattern.
- `GripUtilizationSubLimitMap` (new class) - reused the project's own `MonotoneCubicCurve` (as the
  fifth attempt's own report recommended for a future revival), Lock anchors `0/0.75/0.9/1.0 ->
  0/30/60/80`, Slip anchors `0/0.85/0.9/1.0 -> 0/30/60/80`.
- `NormalizedWheelLockSlipEngine` - per-channel event-ratchet state (`_lockEventActive`/
  `_lockStableGMech`/`_lockStableK`, mirrored for Slip), reset on every "channel goes quiet" early
  return (not triggered, not engaged, no G/speed signal) and on `ResetDirection()`. `gripUtilization`
  computed from `motion.MagnitudeG` (LONGITUDINAL-ONLY, never the friction circle) divided by the
  ratcheted stable reference, mapped through the curve, applied as a **FLOOR ONLY**
  (`severity = Math.Max(severity, engageWeight * curveOutput)`), gated by the single existing
  `scaleCeiling.HasValue` check (no second confidence gate).

### THE STABLE-REFERENCE DESIGN CHOSEN, AND WHY (evaluating the brief's four options)

- **(a) Freeze at event onset, hold for the event's duration** - the starting point, but a LITERAL,
  never-updated freeze reproduced a real regression against a pre-existing pinned test
  (`Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating`): a
  never-released, 300-frame synthetic warm-up is legitimately ONE continuous event by this engine's own
  direction-based definition, so a literal freeze pins the reference at frame 0 (cold, ~seed value)
  forever, even once the live model has genuinely matured within that same event.
- **CHOSEN: a RATCHET** - snapshot the live model's own `(gMech, k)` at onset, then EVERY frame, compare
  the live model evaluated at the current speed against the ratcheted value evaluated the same way, and
  only ever adopt the live pair if it is HIGHER; never adopt a lower one. This trivially satisfies "must
  not measurably decay within one event" (a running max cannot decrease) while still recognising a
  genuinely harder moment arriving later in a long event - closer to the live decaying maximum's own
  "raises instantly" behaviour, just never allowed to fall within one event. Proven correct by
  `Stable_reference_does_not_decay_within_one_sustained_event_while_the_live_reference_erodes` (see Part
  3) before this file was reverted.
- **(b) A separate long-timescale (minutes-scale) decaying reference** - not adopted: it does not
  literally satisfy "must not measurably decay" (it still decays, just slower), and tuning a new decay
  constant risks the same "how slow is slow enough" arbitrariness this codebase's own standing precedent
  (`docs\speed-aware-grip-report.md`) warns against for `GripLearner` changes made casually.
- **(c) The fitted `SpeedAwarePeakG(v)` model directly** - checked, as instructed, whether it inherits
  decay from its own bucket inputs rather than assuming it does not: **it does**. `GripLearner._buckets`
  each carry their OWN `DecayingMaxG`, decayed via the SAME `ForgetPerSample` on every qualifying
  `Observe` call that lands in that bucket - so querying `SpeedAwarePeakG`/`PublishedGMech`/`PublishedK`
  live, every frame, inherits the identical erosion-during-a-plateau defect as the flat scalar. This
  option was correctly ruled OUT rather than assumed safe.
- **(d) The persisted/warm (cold/hot-blended) value** - checked: `PublishedPeakG`'s hot component is
  `_learnedPeakG` itself (the SAME decaying max), and the blend weight rises toward 1.0 (fully hot) as
  session evidence accumulates - so for any session with meaningful evidence, this degenerates back
  toward the live, eroding value. Also ruled out.

### LONGITUDINAL-ONLY G (not the friction circle)

Kept exactly as the fifth attempt's own real-log measurement recommended: `motion.MagnitudeG`, the same
signal the existing physical-limit detector already uses. Not revisited - the fifth attempt's own report
already showed the friction circle measurably worse (56.0% vs 44.6% time-above-80) for a diagnosed
numerator/denominator mismatch (lateral-isolated denominator vs non-isolated friction-circle numerator);
this task did not find new evidence to overturn that, and the brief did not ask for it to be revisited
without such evidence.

### THE BLEND: A FLOOR ONLY, NEVER A CEILING, NEVER A TWO-WAY BLEND

The fifth attempt's own (lost, unshipped) redesign was described as "rise, never fall (except one
confirmed release)". Reconstructing that shape from scratch, algebraic inspection showed a genuine
two-way weighted average toward the curve is EITHER (i) redundant with a `Math.Max` floor (the floor
adds nothing a correctly-directed weighted average didn't already guarantee), or (ii) actively harmful:
capping a genuine, calibration-verified severity reading ABOVE 80 (a real, pinned, intentional design
property - `KeyedScaleLearner`'s own ceiling can legitimately sit below a raw 100 reading, so a genuine
full-lock event calibrates to ~88.9, not capped at the curve's own 80 maximum) down to exactly 80. A
**floor-only** design (`Math.Max(severity, engageWeight * curveOutput)`), engaged only within a narrow
`0.95-1.05`-of-stable-reference ramp, was chosen instead - simpler than the fifth attempt's own
three-weight-function design (no separate source-severity-ramp trigger, no separate release trigger; both
dropped as unnecessary once the numerator has no independent lag to guard against - `motion.MagnitudeG`
drops in lockstep with G the instant the pedal releases, so severity and utilization fall together with
no masking risk).

---

## PART 1 - REAL-LOG MEASUREMENT (the deciding evidence)

Methodology: `scratchpad\narrative-validation-harness` (built fresh against each variant's DLL),
replaying the SAME two real F1 25 logs (`c_1_5_3_e_d\...211733_ShakeIt.csv`,
`...212439_Raw.csv`) the fifth attempt used. My own from-scratch replay of the TRUE baseline (mechanism
absent) reproduced the fifth attempt's own published numbers almost exactly (32.13% vs their 32.1%; Phase
1 80-100% 24.8% exact match; `AtReleaseInflection` pooled median 71.5 exact match; all four Slip plateau
medians exact matches) - confirming the harness and methodology are faithful before trusting the new
numbers.

### The three headline metrics, all three variants side by side

| metric | BASELINE (current shipped) | FIFTH ATTEMPT (rejected) | THIS (sixth) ATTEMPT, fixed | Acceptance bar |
|---|---|---|---|---|
| time above 80 during braking | **32.1%** | 44.6% | **33.9%** | must go DOWN - **FAIL** |
| Phase-1 ramp frames in 80-100 | **24.8%** | 62.0% | **28.8%** | must NOT increase - **FAIL** |
| post-release dwell in 30-60 (pedal>=30%) | **8.5%** | 21.9% | **8.5%** (unchanged) | must go UP - **FAIL** |

**All three headline metrics fail to beat baseline.** The mechanism is dramatically better than the
fifth attempt's own broken design (time-above-80 33.9% vs 44.6%; Phase-1 28.8% vs 62.0%) - proof the
floor-only/narrower-engagement redesign matters - but it does not clear the bar the brief set: beating
the CURRENT SHIPPED baseline, not merely its own broken predecessor.

### The structural reason, confirmed by mutation

Re-running the SAME two logs with the denominator reverted to the LIVE decaying max (mutation 1, see
Part 3) produced **33.89%/28.9%** - statistically indistinguishable from the FIXED variant's
**33.86%/28.8%**. The stable-vs-live choice made almost no aggregate difference on these two logs, even
though the unit-level invariant test proves the stable reference genuinely does not decay within an
event (Part 3). This means the fifth attempt's SEVERE regression (44.6%/62.0%) was NOT primarily driven
by denominator erosion on these specific logs (their braking zones are short - measured ~16.35
qualifying Observe() calls/second of engaged+triggered time, see Part 5 - too short for the erosion this
task diagnosed to dominate) - it was driven by the fifth attempt's own BROADER engagement design (a
two-way blend plus two additional trigger weights this task deliberately dropped). Fixing the diagnosed
denominator defect is real and provably correct, but on these two logs it is not the dominant lever; the
residual gap versus baseline (32.1%->33.9%) appears to be a structural property of ANY floor-only
mechanism layered on top of a baseline that already reads above-80 nearly a third of the time: a floor
can only ADD frames above 80, never remove any, so "time above 80 must go down" is essentially
unreachable for a floor-only design once the *floor itself* ever fires on any frame the baseline would
not otherwise have flagged - which it does, occasionally, exactly when it is supposed to (a genuine
near-limit moment the source has not caught up to).

### Band correspondence (owner's own definition), before/after

Verified DIRECTLY on `GripUtilizationSubLimitMap` (the curve itself, before this file was reverted) via
`GripUtilizationSubLimitMapTests`: Lock `0.75/0.9/1.0 -> 30/60/80` exact; Slip `0.85/0.9/1.0 -> 30/60/80`
exact; monotonically non-decreasing across a dense sweep; clamps at 80 for any utilization >= 1.0;
reads 0 for negative/non-finite input. The FULL blended pipeline does not reach these exact values at
these exact utilizations (the engagement ramp, `0.95-1.05`, deliberately does not trust the curve as a
floor until utilization is already close to 1.0 - see Part 0) - "correspondence" is a property of the
curve's OWN mapping, not a claim that the published severity equals 30/60/80 at exactly 75%/90% of the
live ratio in the full pipeline. No BEFORE exists for this (the mechanism never shipped before); AFTER is
as described.

### Narrative predictions (a)-(f), all three variants

| prediction | BASELINE | FIFTH ATTEMPT (rejected) | THIS ATTEMPT (fixed) |
|---|---|---|---|
| (a) Phase 1 ramps solidly from 0 | PASS (p10=4.0, median 46.6) | DEGRADED (62% of ramp itself reads 80-100) | **PARTIALLY DEGRADED** (p10=4.0 unchanged, median 53.8, 80-100% rises to 28.8% - real but far smaller than the fifth attempt's) |
| (b) Phase 3 rising (constant pedal, falling speed) | PARTIAL/data-limited | UNCHANGED (physics-only trend, this feature does not touch it) | **UNCHANGED** (same reasoning - PRED_B/PRED_B_OLDNEW numbers are the pre-existing speed-model trend, not this feature) |
| (c) At release inflection, 60-80 usually near 80 | MIXED, pooled median 71.5 | WORSE (median drops to 62.1) | **ESSENTIALLY UNCHANGED** (pooled median 71.8, n=60, same as baseline within noise) |
| (d) Time above 80 is small | FAIL (32.1%) | WORSE FAIL (44.6%) | **FAIL, but much closer to baseline** (33.9%) |
| (e) Post-release mostly 30-60 | FAIL, bimodal (76.3/0.0) | PARTIAL IMPROVEMENT (30-60% share rises to 21.9%) | **NO IMPROVEMENT** (8.5%, identical to baseline - the floor essentially never fires in this specific window on these two logs) |
| (f) Sustained 65-75% throttle -> 60-80 (Slip) | FAIL (mixed) | MIXED, artifact-prone (a suspicious repeating 70.0 ceiling under the friction-circle variant) | **MIXED, more modest and plausible, still short of the target band** (Raw/Sauber median rises 36.6->42.2; Raw/F1 Generic median unchanged at 12.1 but its 60-80% share rises 15.6%->37.0%; ShakeIt/F1 Generic's own known standing-start artifact, 0.7, is untouched by this feature either way) |

**Net for this attempt**: no clear improvement anywhere the owner's own predictions are checked, one
small partial gain (f, modest and plausible rather than the fifth attempt's own artifact-prone jump), and
a real but much-reduced version of the fifth attempt's own Phase-1 regression (a).

---

## PART 2 - WET/DRY CONSISTENCY (blocking check)

A small, dedicated throwaway harness (`scratchpad\wetdry-harness\`) replayed all four `Common_1_5_e_d`
logs. These logs have no `Diag.GameId`/`Diag.CarId` columns (an older capture format), so this harness
necessarily starts EVERY replay cold (no persisted `QAdvanceFeedback.Parameters.json` import) - a
materially different, less-representative methodology than a real running session (which would import
the persisted calibration in that same folder) or the fifth attempt's own Part 4 (which does not
document whether it imported that file either). The absolute numbers this harness produces
(Raw-Wet median 38.1, Raw-Dry median 90.1 - filtered to configured-source>=50, n=267/40) are **not
directly comparable** to the fifth attempt's own published wet/dry table and should not be read as this
task's own wet/dry finding in isolation.

**What IS a clean, blocking-relevant check**: running the SAME cold-start harness with the new mechanism
enabled vs fully disabled (a direct A/B on identical replay state) produced **byte-identical output to
one decimal place in every case** (Raw-Wet 38.1/96.7, ShakeIt-Wet 88.1/99.1, Raw-Dry 90.3/91.4,
ShakeIt-Dry n=0 in both). **The new mechanism introduces no measurable wet/dry regression** - it simply
does not engage differently between the two conditions in this dataset; whatever wet/dry spread exists
in this specific cold-start harness is a pre-existing property of this harness's own start-from-cold
methodology, not something this task's change causes or worsens.

---

## PART 3 - MUTATION EVIDENCE

**(1) Revert the denominator to the decaying max, confirm the time-above-80 regression returns.**
Done live: `gripUtilization` temporarily computed via `physicalReference.Ratio(...)` (the LIVE,
continuously-updated reference) instead of `RatioAgainstStableReference(..., stableReferencePeakG, ...)`.
Real-log replay: **33.89%/28.9%** time-above-80/Phase-1, essentially identical to the fixed variant's
33.86%/28.8% (see Part 1's own discussion of why - these two logs' braking zones are too short for the
erosion defect to dominate the aggregate). Reverted immediately; full suite re-confirmed green.

**(2) Force `gripUtilization` constant (0.5, below the 0.95 engagement ramp), confirm a
band-correspondence-adjacent test fails.** Done live: 2 of the mechanism's own load-bearing unit tests
failed immediately -
`A_genuine_physical_limit_event_raises_severity_even_when_the_source_has_not_caught_up` (expected >50,
got 1.78) and `A_non_aero_car_with_a_flat_constant_peak_still_gets_a_working_floor` (expected >40, got
1.78) - confirming the mechanism is genuinely load-bearing (not a no-op) when working as designed.
Reverted immediately; full suite re-confirmed green (889/889 including the 21 new tests, before this
file's own later, full revert brought the suite back to 868/868).

**Additionally, at the primitive level** (`GripLearnerStableReferenceTests`,
`Stable_reference_does_not_decay_within_one_sustained_event_while_the_live_reference_erodes`): a
300-frame plateau at a constant 2g, following a prior 300-frame event matured to ~4g, showed the STABLE
ratchet never decreasing across the plateau (5 sampled checkpoints, strictly non-decreasing), while the
SAME `physicalReference`'s own LIVE `PublishedPeakG` measurably eroded (confirmed `< 0.7 *` the matured
value) over the identical plateau - direct, contrastive proof the fix does what it claims at the unit
level, independent of whether it moves the real-log aggregate.

---

## PART 4 - OTHER REQUIRED CHECKS

- **Cold start**: `The_floor_does_not_engage_while_the_physical_reference_is_still_immature` (10 warm-up
  samples, `GripLearner.Confidence` < 0.2) showed severity staying < 30 for a genuine-limit-shaped query
  with a low source reading - the ceilinged utilization ratio cannot even reach the 0.95 engagement ramp
  while cold, so under-reporting (never over-reporting) falls out of the SAME existing
  `Confidence`/`ColdStartCeilingRatio` mechanism every other `Ratio`-shaped call already uses - no new
  minimum-sample gate was added. `RatioAgainstStableReference_ceiling_relaxes_continuously_with_confidence_no_steps`
  confirmed the ceiling itself never steps as confidence rises sample-by-sample to full maturity.
- **Does not regress "genuine full lock reads 80/>80"**:
  `The_floor_never_lowers_a_severity_the_source_already_reports_above_80` reconstructed
  `NormalizedWheelLockSlipEngineTests`'s own pinned `arcadeHard` scenario and confirmed it stays > 80
  (88.9-ish) - the floor-only design's entire point.
- **Nuisance-output fractions** (fraction of TOTAL driving time in 30-60/60-80, not just braking
  frames), measured from the full per-frame dump across all four (log,car) pairs: 30-60% band 0.9-1.7%,
  60-80% band 2.3-4.7% of ALL replayed frames (braking and non-braking alike) - the overwhelming majority
  of driving time reads near 0, consistent with "no sustained nuisance output during gentle cruising".
  Since the mechanism is a floor-only design, these figures can only be greater than or equal to what the
  baseline alone would show (never lower) - a structural, not merely empirical, consequence of Part 1's
  own finding.
- **G channel missing / standstill / direction Unknown**: verified structurally, not by a new empirical
  run - the new code was inserted strictly AFTER the pre-existing `motion.Level == Unavailable` and
  `!engaged` early returns (both of which already existed and already cover these three cases), and both
  reset the new event-ratchet state on the way out. No new special-casing was needed or added; the
  mechanism is simply inert whenever the pre-existing degradation ladder already is.
- **GT3/non-aero reasoning**: `A_non_aero_car_with_a_flat_constant_peak_still_gets_a_working_floor`
  (a car taught at one fixed speed the whole session, `k` stays exactly 0 per
  `GripLearnerSpeedModelTests`'s own guards) confirmed the floor still engages correctly - the
  mechanism's structure does not depend on a genuine aero fit existing. Not validated on non-F1 data (none
  exists in this project's captures) - stated plainly, not claimed.

---

## PART 5 - THE ADDITIONAL MEASUREMENT REQUEST (physicallyAtLimit / GripLearner.ForgetPerSample)

A mid-task message asked whether `GripLearner.ForgetPerSample=0.997`'s decay, applied to the SAME live
decaying max that ALSO gates `physicallyAtLimit` (the boolean that teaches `KeyedScaleLearner`'s
ceiling), might independently bias that ceiling low and contribute to the 32.1% baseline. Investigated
with a dedicated throwaway harness (`scratchpad\at-limit-decay-analysis\`), real logs, NO code touched.

**1. Effective Observe() rate, measured (not assumed)**: 57.3 Hz average raw telemetry rate, but only
**16.35 qualifying Observe() calls per second of lock-engaged+triggered time** (isolation/bucket-purity
gates dilute the raw rate substantially - about 28.5% of engaged+triggered frames actually qualify).
This is markedly slower than the mid-task message's own 60 Hz assumption
(`0.997^16.35 ≈ 0.952`, ~4.8% decay/second - not ~16%/second).

**2. Within-zone reference decay, measured directly**: of 46 real braking zones, only 5 had enough
qualifying data at BOTH a zone's own peak and its last frame to measure end-of-zone decay; of those,
median drop = 0%, p90 = 15.5%, max = 16.2% - real, but far smaller than the message's own "~40% over
three seconds" estimate (which used the too-fast rate assumption above).

**3. `physicallyAtLimit` firings, fresh-peak vs erosion-assisted**: of 1963 at-limit-eligible frames
across both logs, 57.3% occurred while the live reference had already declined somewhat from that zone's
own running-max (potentially erosion-assisted); 42.7% occurred at/above the zone's own running peak
(fresh). **However, the source reading at "eroded" firings was HIGHER (median 40.1), not lower, than at
"fresh" firings (median 31.3)** - the OPPOSITE of the hypothesis's own directional prediction (that
erosion-driven firings would teach the ceiling from LOW source readings, biasing it down). This is most
plausibly because "later in a zone" correlates with both some reference decay AND a genuinely more
committed, higher source reading, not because erosion is manufacturing spuriously-low-source at-limit
moments.

**Conclusion: the evidence is weak/contradictory, not strong.** Per that request's own explicit
instruction ("do not fix it unless the evidence is strong... if the evidence is weak, report the finding
and leave the code alone"), **no change was made to `physicallyAtLimit` or `GripLearner.ForgetPerSample`**.
This is reported as a clean negative alongside the main finding, not folded into it - the two are
independent questions with independent (both negative) answers.

---

## DECISION

**Ship nothing.** Every file touched during this task has been restored to be byte-identical to the
pre-task baseline (confirmed via `diff -rq QAdvanceFeedback_1.0.6.6_speed_aware/... QAdvanceFeedback/...
--exclude=bin --exclude=obj`, zero differences in `QAdvanceFeedback\Core\Normalized\` and the entire
`QAdvanceFeedback.Tests\` directory). No partial ship is recommended: the three headline (Lock-channel)
metrics all fail to beat baseline, and the one modestly-positive Slip-side signal (f) is not framed as
its own acceptance bar and is too small/mixed to justify shipping in isolation.

No persistence/version-bump was needed at any point - the mechanism never learned or stored anything new
beyond what `GripLearner`/`KeyedGripLearner` already persist; it only read that existing state through a
new, ratcheted lens, session-scoped and never serialized.

**If this idea is revisited again**: the diagnosed denominator defect IS fixed by this task's ratchet
design (proven at the unit level), and the floor-only/narrower-engagement redesign is a real,
substantial improvement over the fifth attempt's own broken shape (33.9%/28.8% vs 44.6%/62.0%) - but Part
1's own mutation evidence shows this specific defect was not the dominant driver of the fifth attempt's
severe regression on THESE two logs (their braking zones are too short), and Part 1's own structural
argument (a floor can only ever raise time-above-80, never lower it) suggests a floor-only design may be
fundamentally unable to satisfy "time above 80 must go down" against a baseline that already sits at
32.1%, regardless of how correct the denominator is. A future attempt would need either a mechanism that
can also LOWER an over-eager source reading (without capping the pinned >80 headroom this task's own
floor-only design was built to protect) or a fundamentally different acceptance framing.

---

## Concerns

1. **The residual gap versus baseline (32.1%->33.9%, 24.8%->28.8%) is small in absolute terms but
   structural, not a tuning problem** - retuning the 0.95/1.05 ramp width would trade off engagement
   frequency against how much the floor helps the one scenario it targets (source under-reporting near a
   genuine limit), but cannot change the FLOOR's one-directional nature.
2. **The post-release dwell metric (e) showed literally zero movement (8.5% -> 8.5%)** on these two real
   logs - the floor essentially never fires in that specific window here, so this task cannot confirm or
   deny whether a floor-shaped mechanism could ever deliver the fifth attempt's own one genuine
   improvement (21.9%) without also reproducing its regressions; that specific tradeoff was not
   re-derived from scratch in this task's own (deliberately more conservative) design.
3. **The wet/dry check (Part 2) used a necessarily cold-start-only harness** (these four logs lack
   `Diag.GameId`/`Diag.CarId` columns) - the A/B (on vs off) comparison is clean and blocking-relevant,
   but the absolute numbers are not comparable to the fifth attempt's own (differently-seeded) figures.
4. **The additional `physicallyAtLimit` investigation (Part 5) had a thin sample for the within-zone
   decay measurement** (5 of 46 zones had usable start/end data) - the qualifying-rate and firing-split
   findings (1963 at-limit frames) are on a much larger, more confident sample.
5. **GT3/non-aero generality is reasoned from structure, not validated** - no non-F1 log exists in this
   project's captured data.

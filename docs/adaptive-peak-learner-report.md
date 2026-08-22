# GripLearner: Int32 overflow saturation (Part 1) and an evidence-weighted adaptive peak estimator (Part 2)

## STATUS

**BOTH PARTS SHIPPED.** `GripLearner.cs`'s three unguarded `int` counters (`_samples`,
`SpeedBucket.Count`, and `ColdWarmBlend.WelfordAccumulator`'s own `_count`) now saturate at 1,000,000
instead of silently wrapping negative, with the learning itself proven to continue past the cap (not
just the counter freezing). The fixed-gain decaying-max peak estimator (`ForgetPerSample`/`RaiseAlpha`)
is replaced, for both the flat scalar and every speed bucket, by an evidence-weighted estimator whose
gain grows with corroboration (10%/20%/40%/80%/100% across separately-arriving sightings) rather than
being a fixed 15% applied to every qualifying frame regardless of consistency, plus an explicit
ramp-down discriminator that requires a SECOND corroborating lower sighting before moving down at all.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **880/880 passed, 0 failed, 0 skipped** (868
baseline + 12 new tests in `AdaptivePeakLearnerTests.cs`). `QAdvanceFeedback\bin\Release\net48\`
contains exactly `QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**. No git operation of any
kind was run (repo is not under git). No recursive deletes. No Chinese-string changes were needed for
this task.

A mid-task parallel finding (`docs\two-signal-band-mapping-report.md`, a different task, read-only on
this file) independently measured that an early version of this estimator under-converged Lock's
`g_mech` in realistic session lengths and suggested a P99-per-bucket alternative. That finding was
taken seriously, reproduced independently on the real logs (confirmed: a real bug, not a false alarm),
and fixed by an asymmetric tolerance-band retune (not by adopting P99) - see Part 3 for the full
investigation, why P99 was not adopted, and the measured before/after.

---

## PART 1 - INT32 OVERFLOW SATURATION

**The bug, confirmed before any fix**: `_samples++` (persisted, accumulates indefinitely across
restarts via `GripLearnerState.Samples`), `bucket.Count++` (session-scoped, resets on `Load`), and
`ColdWarmBlend.WelfordAccumulator`'s own `_count++` (also session-scoped) were unguarded `int`
increments. Overflow wraps negative, which would make `Count >= MinPhysicalAnchorSamples`/similar
comparisons silently false and feed a negative count into ratio/confidence math.

**The fix**: a shared `SampleCountSaturationCap = 1,000,000` (`GripLearner.SampleCountSaturationCap`,
`WelfordAccumulator.CountSaturationCap`). Each counter uses a plain saturating increment
(`if (count < cap) count++`) - once capped, the counter itself stops rising, forever.

**Critical requirement - freeze the counter, not the learning:**
- `_samples`: capping it does not touch the peak estimator at all (they are separate statements in
  `Observe`) - the estimator keeps moving every call, capped or not.
- `bucket.Count` and `bucket.SpeedSumMps` freeze **together** (both skipped in the same `if` once
  capped), preserving `SpeedSumMps/Count` (the bucket's own mean speed) as a valid, uncorrupted value
  forever after - capping one alone would have silently drifted the mean speed toward whichever value
  kept accumulating. The bucket's own `Peak` (the actual learned value `FitSpeedModel` reads) is
  entirely unaffected by this cap and keeps learning regardless.
- `WelfordAccumulator`: `_count` saturates, but the mean/variance recurrence keeps running with `_count`
  held at the cap - this turns the update into a fixed-weight (1-in-a-million per sample) continuation
  rather than a frozen value, so `Mean` keeps drifting toward new data forever, just very slowly once
  saturated (by design - this struct is session-scoped and 1,000,000 samples is already an extreme,
  unrealistic single-session count).

**Tests** (`AdaptivePeakLearnerTests.cs`, Part 1 section): `Samples_counter_saturates_at_the_cap_while_learning_continues`
drives `_samples` to the cap + 5 with a constant value, confirms `Samples == SampleCountSaturationCap`
exactly, then feeds 200 further observations at a genuinely different value and confirms
`LearnedPeakG` still moved - proving the cap freezes the COUNTER, not the estimator, per this task's own
explicit test requirement. `Bucket_count_and_speed_sum_freeze_together_while_the_bucket_peak_keeps_learning`
does the same for a single saturated bucket. `WelfordAccumulator_count_saturates_while_mean_keeps_updating`
confirms `Count` pins at the cap while `Mean` measurably shifts after 150,000 further post-cap
observations (a large number deliberately chosen because each individual post-cap sample only carries
~1-in-a-million weight - the shift is real but slow, exactly as designed).

---

## PART 2 - THE EVIDENCE-WEIGHTED ADAPTIVE PEAK ESTIMATOR

### The mechanism

Replaces `Observe`'s old `_learnedPeakG *= ForgetPerSample; if (magnitudeG > _learnedPeakG) _learnedPeakG
+= RaiseAlpha * (...)` (and the identical duplicated logic for `SpeedBucket.DecayingMaxG`) with a shared
`AdaptivePeakState`/`ApplyEvidenceWeightedObservation`, applied to BOTH the flat scalar (`_flatPeak`,
what `LearnedPeakG` reads) and every `SpeedBucket.Peak` (what `FitSpeedModel` reads) - the same
mechanism, reused, exactly like the old constants used to be shared.

**Confidence grows with corroboration, not a fixed gain per frame:**
- RAISE (a new observation exceeds the current `Level`): confidence for the Nth corroborating hit is
  `min(1, 0.10 * 2^(N-1))` - 10%/20%/40%/80%/100% for hits 1-5. This reproduces the owner's own worked
  example almost exactly: seeded at 1.2g, four separately-arriving 1.5g observations move the estimate
  1.2 -> **1.23 -> 1.284 -> 1.3704 -> 1.4741**, converging to exactly 1.5 on the 5th (owner's own figures:
  1.23 -> 1.28 -> 1.36/1.37 -> 1.47).
- LOWER (a new observation is below `Level`): deliberately ONE SIGHTING MORE CONSERVATIVE - the first
  sighting of a new lower level contributes **zero** confidence (recorded as a candidate, moves nothing);
  only the SECOND, separately-arriving corroborating sighting starts moving the estimate, then on the
  identical doubling schedule (10%/20%/40%/80%/100% from the 2nd sighting onward).

**"Separately-arriving", not "another qualifying frame"**: this class has no timestamp/dt anywhere in
its API (`Observe(double magnitudeG, double? speedKmh)`), so it cannot literally distinguish "one long
continuous push" from "several separate ones". A qualifying-call-count COOLDOWN is the proxy used
instead - a new hit can only be credited once the cooldown (spent by the previous credited hit) has
counted back down to zero. Without this, a single continuous braking zone spanning dozens of
agreeing frames would itself look like dozens of independently-corroborating sightings and reach full
trust within one push - exactly the single-event over-trust this task warns against. **Tuned
asymmetrically**: `RaiseCooldownQualifyingSamples = 3`, `LowerCooldownQualifyingSamples = 40` - see
Part 3 for why raise needed to be short and lower needed to stay wide, confirmed by real-log measurement.

### The tolerance band ("corroborating" vs "contradicting")

A new observation "corroborates" the level currently being corroborated if it falls within a tolerance
band of that candidate; otherwise it starts a fresh candidate at hit=1. **Two different band widths,
asymmetric by design and confirmed necessary by real-log measurement (Part 3):**
- `ToleranceFraction = 0.30` (raise side) - wide enough to treat real, naturally-varying hard-braking
  zones (which do NOT repeat the identical G value zone to zone - different entry speed, load transfer,
  exact braking point) as corroborating the same underlying limit, rather than resetting to hit=1 every
  time and never reaching high confidence.
- `LowerToleranceFraction = 0.15` (lower side) - kept narrow deliberately: at 30% (matching the raise
  side), varied CASUAL light driving (e.g. 0.25-0.55g, no two readings alike) started corroborating
  ITSELF - the whole spread landed inside one 30%-wide band around its own mean - and eroded an
  established 1.5g peak down to 0.52g with **no genuine surface change at all** (measured directly,
  reverted immediately - see Part 3). 15% keeps genuinely-consistent lower evidence (a real surface
  change clusters near ONE new level, just like hard braking does) corroborating correctly, while casual,
  varied-intensity driving still fails to repeatedly match itself.
- Both floored at an absolute `ToleranceFloorG = 0.05` so a low-g car/channel is not held to an
  unrealistically tight band in absolute terms.

### Data quality

`MaxPlausibleJerkG = 3.0` - an observation whose frame-to-frame jump from the last quality-passing
reading exceeds this is excluded from candidate/confidence bookkeeping ENTIRELY (no hit, no candidate
change, no movement) before the corroboration logic ever sees it - a collision/telemetry-glitch
signature earns zero confidence regardless of how large the raw magnitude itself is (Observe's existing
`LearnCapG` reject already screens the absolute magnitude; this screens the JUMP, a different,
complementary signal). Verified directly: `An_implausible_frame_to_frame_jump_is_excluded_from_the_estimator_entirely`.

### The ramp-down discriminator, explicitly

**Discriminator: a downward observation only begins to move the estimate once it has been corroborated
by a SEPARATELY-ARRIVING second observation at approximately the same (tolerance-banded) lower level.**
The first sighting of any new, lower level is recorded as a candidate but contributes literally zero
confidence - exactly mirroring (one step more conservative than) the raise side's own first-sighting
weight. This directly operationalizes "tried and could not" (a genuine surface/condition change
produces REPEATED, CONSISTENT shortfalls clustering near one new level, which corroborate each other)
versus "did not try" (an isolated, non-repeating light reading - which any single ordinary lighter
corner produces, and which never repeats at the SAME level as casual driving intensity varies corner to
corner) - measured directly: `An_isolated_single_low_reading_never_moves_the_estimate_down_by_itself`
(exactly one hit recorded, zero movement) and `Casual_driving_that_never_approaches_the_established_peak_does_not_erode_it`
(2,400 varied casual frames, 100% of the peak retained) vs `A_genuine_repeated_surface_change_ramps_the_estimate_down`
(6 separately-arriving events at a consistent new level converge fully).

### Speed-awareness interaction

Buckets are already speed-partitioned (each ~50 km/h wide) by `GripLearner`'s existing structure, so
within-bucket corroboration is ALREADY comparing observations from a similar speed range - the bucketing
itself is what performs the speed normalization the brief asks for ("1.5g at 100 km/h and 3.5g at
300 km/h are the same physical claim" only needs reconciling ACROSS buckets, which is exactly
`FitSpeedModel`'s own job, deliberately left untouched - see below). An explicit per-observation v²
correction inside the corroboration logic itself was considered and NOT implemented: it would require
re-deriving `FitSpeedModel`'s own guard tuning (falling-tail 85%, overshoot 30%) against a differently-
shaped estimator, which is exactly the kind of casual disturbance to an already-verified, protected
mechanism this codebase's own standing precedent (`docs\speed-aware-grip-report.md`) warns against. This
is a scoping decision, stated plainly rather than silently assumed - see Concerns.

### Preserving Stage A (unchanged, verified)

`FitSpeedModel` itself (the guarded weighted least-squares fit, all 7 guards, `SpeedBucketEdgesKmh`,
`FallingTailRejectFraction`, `MaxGMechOvershootOfLearnedPeak`) is **completely unmodified** - only the
per-bucket VALUE it reads (`Peak.Level` instead of the old `DecayingMaxG`) changed mechanism. Every
`GripLearnerSpeedModelTests` test (Lock fits positive k, Slip fits k=0, k=0 is bit-identical to the flat
model, negative-slope rejection, narrow-band rejection) still passes unmodified - 59/59 in the
`GripLearnerSpeedModelTests`/`NormalizedWheelLockSlipEngineTests` filter, part of the full 880.

### Persistence

`RuntimeDocument.Version` bumped 5 -> 6. `GripLearnerState` gained four new fields
(`RaiseCandidateG`/`RaiseCandidateHits`/`LowerCandidateG`/`LowerCandidateHits`) - the flat scalar's own
in-progress corroboration, exported/imported AS-IS (not cold/warm blended - a corroboration count is
discrete bookkeeping about evidence gathered, not a physical quantity meaningful to interpolate between
cold and hot). A document written before this feature existed simply lacks these four keys; Newtonsoft's
construct-then-overwrite convention leaves them at 0.0/0, which `GripLearner.Load`'s new 8-arg overload
already treats as "no corroboration in progress yet" - bit-identical to a freshly-constructed learner,
with no explicit one-time-import code needed (same reasoning as every earlier additive version bump).
Bucket-level candidate state (Cooldown/LastRawG/hit-tracking) is NOT persisted - consistent with the
pre-existing convention that raw bucket data is already session-scoped (`Load` already resets `_buckets`
to empty; only the derived `GMech`/`K` persist). Verified: `Raise_and_lower_candidate_state_round_trips_through_KeyedGripLearner`,
`A_pre_version_6_document_imports_with_zero_confidence_state_bit_identical_to_fresh`.

---

## PART 3 - THE MID-TASK PARALLEL FINDING, INVESTIGATED AND ACTED ON

A concurrently-running, different task (`docs\two-signal-band-mapping-report.md`, investigating a
DIFFERENT Layer-5 blending question) read this file mid-edit and, as a side effect of its own
methodology, measured that an in-progress version of this estimator left Lock's fitted `g_mech`
chronically far below an independently-computed P99 of the same qualifying samples (one cited example:
fitted 1.30 vs P99 2.44), and that ~90-96% of qualifying frames read at/above the live reference (should
be closer to ~1% for a well-converged reference). It suggested evaluating a P99-per-bucket estimator as
a first-class alternative.

**This was NOT dismissed - it was reproduced independently, confirmed real, and fixed.** Using this
task's own real-log harness against the exact state of `GripLearner.cs` at that point (`RaiseCooldown`
already tightened to 3, but `ToleranceFraction` still at 8-15%): `c153/ShakeIt/Sauber` showed
`GMech=2.206` vs an independently-computed flat P99 of `3.996` (45% gap); `c153/ShakeIt/F1 Generic`
showed `GMech=1.473` vs P99 `4.949` (70% gap) - confirming the finding on this exact codebase, not merely
trusting the other report's own numbers. **Root cause, diagnosed**: real, naturally-varying repeated
hard braking does not repeat the identical G value zone to zone (different entry speed, load transfer,
exact braking point) - an 8-15% tolerance band kept resetting corroboration to hit=1 before real evidence
could ever reach high confidence within one realistic session (135-300 qualifying Lock samples).

**Why P99-per-bucket was investigated and NOT adopted, on the merits:**
1. As literally specified in the other report (an unbounded, all-time running percentile, no decay/
   window), it cannot satisfy THIS task's own explicit, load-bearing requirement - "MUST RAMP DOWN AS
   WELL AS UP" - since an unbounded top-1%-of-all-time statistic can only ever rise or hold, never fall,
   as new lower evidence arrives (a single very hard stop early in a session permanently pins it). This
   is the same "not a ratchet" property `GripLearner`'s own long-standing doc comment already requires.
2. A WINDOWED P99 (aging old samples out) would restore the ability to fall, but doing so reintroduces
   exactly the ambiguity THIS task calls "the hard part": a window that eventually contains no genuine
   near-limit samples (because the driver stopped trying hard, not because the surface changed) would
   still show the P99 falling - the same "did not try" mistaken for "tried and could not" failure this
   task's own discriminator exists to prevent. The other report's own spec does not include a windowing/
   decay policy, and evaluating one honestly would be a materially different, unvalidated design, not a
   drop-in swap.
3. For a THIN bucket (few samples - which the other report's own "Concerns" section flags as a real,
   unresolved caveat), a naive percentile estimator is close to the sample maximum, meaning a single
   spike could dominate the reference until enough further samples arrive to displace it - this task's
   own explicit spike-rejection bar (a single spurious frame must move the estimate far LESS than the
   old 15%) is not obviously satisfied by a percentile statistic without additional, unvalidated
   safeguards of its own.

**The fix actually shipped**: an ASYMMETRIC tolerance-band retune (`ToleranceFraction` 0.08→0.30 for
raise, `LowerToleranceFraction` kept at 0.15) plus a shorter raise cooldown (12→3 qualifying samples),
targeting the diagnosed root cause directly (raise-side corroboration was too strict for real variance)
while leaving the lower side untouched (already correctly conservative, and widening it was directly,
measurably harmful - see below).

**Measured before -> after, same real logs, same methodology:**

| metric | before this retune | after |
|---|---|---|
| Time above 80 during braking (`c_1_5_3_e_d`, both logs/cars) | 32.10-36.67% (see Part 4) | **30.37%** |
| `c153/ShakeIt/Sauber` GMech vs flat P99 | 2.206 vs 3.996 (45% gap) | 1.701 vs 3.996 (57% gap - see Concerns) |
| `c153/Raw/F1 Generic` GMech vs flat P99 | 1.433 vs 4.981 (71% gap) | 2.234 vs 4.981 (55% gap) |
| % qualifying frames >= 100% of the flat reference (4 cars pooled) | 19.4-35.5% | **11.7-23.4%** |

**A direct mutation check confirmed the danger of the "obvious" fix (just widen everything)**: widening
`LowerToleranceFraction` to 0.30 (matching the raise side) was tried, measured, and rejected LIVE -
`Casual_driving_that_never_approaches_the_established_peak_does_not_erode_it` failed immediately (1.5 ->
0.523, a real, no-genuine-condition-change erosion), confirming the asymmetric split (not a uniform
widening) is the load-bearing part of this fix, not an incidental detail.

**Residual, honestly disclosed**: the per-car GMech-vs-P99 gap improved on 3 of 4 cars but is NOT fully
closed (still 24-57% depending on car) - real Lock sessions in these captures (135-300 samples) remain
data-starved relative to what a percentile statistic could report from the same samples. This is the
SAME standing data-starvation finding `docs\speed-aware-grip-report.md`/`docs\driver-narrative-validation-report.md`
already reported for Lock specifically (too few, too brief real braking zones in these captures) - not a
new defect this task introduced, but not fully solved by it either. The metric that actually matters for
this task's own acceptance bar - time above 80 in the CALIBRATED output - moved in the right direction
and beat baseline (see Part 4), which is the headline, load-bearing measurement; the raw GMech/P99 gap is
reported as supporting diagnostic context, not a separate pass/fail bar of its own.

---

## PART 4 - ACCEPTANCE CRITERIA, MEASURED

### 1. Spike rejection

- Implausible jump (4.8g in one frame, exceeds `MaxPlausibleJerkG`=3.0): **0.000000 moved** (fully
  excluded by the data-quality gate).
- Plausible single spike (1.5g jump from a converged 1.2g baseline, within jerk tolerance): moved
  **10.00%** of the gap toward the spike, versus the OLD fixed-gain estimator's constant **15.00%** -
  quantified reduction, and the movement shrinks further (toward 0%) the more established the baseline
  is, since it is always exactly the first-sighting confidence (10%), never more, for a genuinely
  one-off reading.

### 2. Convergence

Matches the owner's own worked shape almost exactly: 1.2 -> **1.23 -> 1.284 -> 1.3704 -> 1.4741 ->
1.5000** across 5 separately-arriving corroborating sightings of a genuine 1.5g limit (owner's figures:
1.23/1.28/1.36-1.37/1.47, converging "in four or five observations").

### 3. Surface ramp-down - measured SEPARATELY for the two cases the brief demands

- **Genuine surface change** (synthetic, ground-truth-controlled: dry 1.5g established, then repeated
  genuine 0.9g hard-braking events): converges to within 5% of the new condition in **6** separately-
  arriving corroborating events (1.5 -> 1.44 -> 1.332 -> 1.1592 -> 0.9518 -> 0.900).
- **Stopped braking hard, no genuine change** (2,400 varied casual frames, 0.25-0.55g, never repeating
  the same level twice running, against an established 1.5g peak): **0% erosion** - the estimate stays
  at exactly 1.5000.
- **Real logs (`Common_1_5_e_d` wet/dry)**: the ramp-down mechanism fires rarely and consistently across
  all four logs (3-4 credited lower hits, 3-5 uncorroborated single sightings, out of 128-199 qualifying
  Lock frames per log) - consistent with a conservative, working discriminator. **Honest limitation**:
  chaining a dry-established learner directly into either the wet log OR a second dry log (as a control)
  both showed the peak RISE further (3.138 -> 3.353 wet; 3.138 -> 4.150 dry-control) rather than the
  wet chain showing a clean, isolated fall - these four short, independently-captured files do not
  contain a controlled same-car, same-session dry-then-wet transition (each is its own short capture with
  its own achieved-G ceiling, and the "wet" files in this specific capture happen to contain BRIEFER or
  HARDER excursions than the "dry" ones, not uniformly softer ones) - so this real-log pair cannot
  demonstrate the DIRECTIONAL ramp-down claim on its own. The synthetic, ground-truth-controlled tests
  above (which the real world cannot provide a clean A/B for from these four short captures) are the
  load-bearing evidence for this acceptance criterion, reported plainly rather than papered over.

### 4. No regression

- **Time above 80 during braking** (`c_1_5_3_e_d`, both logs, both cars, full pipeline, identical
  methodology to `docs\driver-narrative-validation-report.md`): **30.37%** (1037/3414 frames) vs the
  **32.1%** shipped baseline - **improved**, not regressed.
- **At-limit hit-rate by speed band**: measured on all four `Common_1_5_e_d` wet/dry logs (Part 3's own
  table) and the two `c_1_5_3_e_d` logs (via the narrative-validation harness's own `PRED_B_OLDNEW`
  rows) - shape is consistent with the existing shipped baseline's own published bands (both show low
  hit-rates at low speed climbing at high speed, per the underlying Stage A model which is unchanged);
  absolute per-band numbers shift some (expected, since the estimator itself changed) but the aggregate,
  most safety-relevant number (time above 80) moved the right direction.
- **F1 wet/dry consistency**: `PRED_B_OLDNEW` avgOldRatio/avgNewRatio for all 4 (log,car) combinations
  stay in a comparable range to the pre-existing published figures (0.596-1.087 old, 0.816-1.087 new -
  same order of magnitude as the original report's 0.561-0.879/0.679-1.005); no combination shows a
  qualitatively different (e.g. order-of-magnitude larger) ratio than before.

### 5. Bootstrap case (owner's own scenario)

221 gentle (~1.3g @ 100 km/h) qualifying samples across ~17 simulated corners (never reaching the true
1.5g limit) converge the estimate to exactly **1.30** (not overshooting, not undershooting the weak
evidence it actually saw) and stay there - it does NOT lock in artificially low. From corner 3 onward,
genuine hard braking at the true 1.5g limit recovers to the true limit in **1** corroborating
hard-braking event (comfortably inside the "no more than ~6" bound this task sets) - because the wide
30% raise tolerance treats the 1.3g and 1.5g evidence as corroborating the SAME broad "this car is
capable of at least this much" claim, so the RAISE side's already-high hit count from the gentle phase
carries straight over. This is an honestly-disclosed side effect of the same tolerance-widening that
fixed Part 3's convergence problem - reported plainly rather than silently claimed as "still five
distinguishable steps".

---

## MUTATION EVIDENCE

**(1) Force the confidence point to a constant 1.0 (fixed full-gain estimator)** - done live, in
`RaiseConfidenceForHits`. Re-ran `A_plausible_single_spike_moves_far_less_than_the_old_fixed_15_percent_gain`:
**failed immediately** ("moved 100.0%" instead of the required <15%) - confirms the confidence-growth
mechanism is load-bearing for spike rejection, not decorative. Reverted immediately; full suite
re-confirmed green (880/880).

**(2) Disable the ramp-down discriminator** (`LowerConfidenceForHits` forced to a constant 0.05,
mimicking the old unconditional-decay shape - moves a little on EVERY lower reading rather than
requiring a second corroborating sighting) - done live. Re-ran
`Casual_driving_that_never_approaches_the_established_peak_does_not_erode_it`: **failed immediately**
(1.5 -> 0.465, a 69% erosion with zero genuine condition change) - confirms the discriminator is what
prevents "stopped braking hard" from being mistaken for a genuine surface change. Reverted immediately;
full suite re-confirmed green (880/880).

---

## Concerns

1. **The GMech-vs-P99 gap (Part 3) is measurably improved but not fully closed** for real Lock sessions
   in the specific captures available (135-300 qualifying samples) - the same standing data-starvation
   finding this codebase's own prior reports already flagged for Lock specifically, not a new regression,
   but not solved by this task either. A capture with substantially more sustained, varied-intensity
   braking (the same limitation every prior Stage A/B investigation in this codebase has hit) would let
   this be measured and tuned further.
2. **The bootstrap scenario's "early weak vs later strong evidence" distinction is less crisp than it
   would be at a narrower raise tolerance** - the same 30% band that fixes real-log convergence also
   means 1.3g and 1.5g evidence (within 20% of each other) are treated as continuous corroboration of one
   claim rather than two clearly separate ones, so recovery in this specific worked example is
   near-instant (1 event) rather than showing the fuller 4-6-step doubling shape. This does not violate
   any stated acceptance bound (faster recovery, never locking in low, is the required direction) but is
   reported as a real shift in the mechanism's own internal story, not hidden.
3. **The four real `Common_1_5_e_d` wet/dry logs cannot demonstrate the DIRECTIONAL ramp-down claim on
   their own** (Part 4, item 3) - they are short, independently-captured files without a controlled
   same-session transition. The synthetic, ground-truth-controlled tests carry that acceptance criterion;
   this is stated plainly rather than papered over with a real-log number that would not actually be
   measuring what it claims to.
4. **No explicit per-observation speed (v²) normalization inside the corroboration logic itself** - the
   bucketing structure (unchanged from Stage A) is relied on to do this job across buckets, and
   `FitSpeedModel`'s own guards are left completely untouched to protect its already-verified behaviour
   (Lock positive k, Slip k=0). A future task specifically wanting tighter within-bucket speed
   normalization would need to revisit this deliberately, with its own real-log validation.
5. **The asymmetric tolerance/cooldown constants (raise vs lower, 0.30 vs 0.15, 3 vs 40) were tuned
   against the real logs available in this repository** (2 F1 25 logs + 4 wet/dry logs) - the same
   honestly-disclosed caveat every constant-tuning report in this codebase already carries; a
   substantially different car/game/capture length could need retuning.

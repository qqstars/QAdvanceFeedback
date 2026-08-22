# Robust auto max-G, mode-dependent transition scaling, and the shared robust-band estimator

## Status

Complete, per the FINAL specification (the pool-selection formula and the 25%-triggered ramp
superseded three earlier iterations during this task - see "History of the specification" below).
853 tests passing, 0 build warnings, single plugin DLL (`QAdvanceFeedback.dll`).

## Build/test confirmation

- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**.
- Plugin output: `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` (+ .pdb) -
  **single DLL**, no stray plugin assemblies.
- `dotnet test QAdvanceFeedback.Tests -c Release`: **853 passed, 0 failed, 0 skipped**.

## 1. Keying verification

Confirmed correct, already before this task and unchanged by it: `GForceMaxLearner.MakeKey(gameId,
carId)` composites both strings with a separator that cannot appear in a real game/car id
(`GForceMaxLearnerTests.Key_concatenation_does_not_let_different_game_car_pairs_collide`).
`GForceSettings.EffectiveAccelMaxG/EffectiveDecelMaxG/ObserveAccelG/ObserveDecelG` all take `(gameId,
carId)` and thread them straight to the per-key learner/ramp dictionaries - a car switch mid-session
looks up a DIFFERENT dictionary entry immediately, and `GForceMaxLearner.GetLearnedMax` prefers a live
estimate, then an imported/persisted seed, then 0.0 - so a returning car's own previously-learned value
is available the instant the key is looked up, no re-earning required
(`GForceSettingsTests.Learned_maxima_do_not_bleed_between_different_cars_in_the_same_game`,
`An_imported_value_is_used_immediately_before_any_new_evidence_this_session`).

## 2. The estimator - exact definition

`Core.RobustBandEstimator`, index-based pool selection over samples sorted DESCENDING (index 0 =
largest), the owner's own settled specification, refined twice during this task to fix two measured
flaws in an earlier literal formula (collapsing to a 1-sample pool at small/mid n; expanding toward the
outliers instead of away from them):

1. **Exclude the top outliers**: `excludeCount = (n==1) ? 0 : Max(1, Ceil(n * 0.05))`.
2. **Take a band of what remains**: `remaining = n - excludeCount; poolSize = Max(1, Ceil(remaining *
   0.10))`; pool = indices `[excludeCount, excludeCount + poolSize - 1]`.
3. **Guarantee a minimum pool width of 10** (`DefaultMinPoolSize`): if the natural pool from steps 1-2 is
   narrower, expand the END further down (toward higher indices / smaller values) until the pool holds
   10 items or the candidate list is exhausted. The START never moves back up toward the excluded
   outliers.
4. **Statistic**: `estimate = 0.75 * poolMax + 0.25 * poolMean` (`MaxMeanBlendWeight = 0.75`) - "very
   close to the largest value in the pool, but still influenced by the average" (the owner's own
   phrasing). Justification: the pool has already excluded the true outliers, so its own max is a safe,
   pre-trimmed value worth weighting heavily; the 25% mean term still damps a single anomalously-high
   reading that survived into the pool itself. A simple, nameable linear blend (mirrors this codebase's
   own `ColdWarmBlend.Blend` convention) rather than a power-mean/quantile.

**NO MINIMUM-SAMPLE GATE anywhere**: `TryEstimate` returns false ONLY at n=0 (literally zero valid
samples). Verified table (n, [start,end]):
`1:[0,0] 2:[1,1] 5:[1,4] 10:[1,9] 25:[2,11] 50:[3,12] 100:[5,14] 200:[10,28] 1000:[50,144]` - pinned in
`RobustBandEstimatorTests.Pool_bounds_match_the_owners_own_settled_specification` plus general
invariant tests (pool never empty; excludes index 0 except n=1; never expands toward the outliers).

**Complexity**: `Observe` is O(1) amortised (one bucket increment + one FIFO enqueue; eviction of
samples aged out of the time window pops each expired sample exactly once over its lifetime).
`TryEstimate` is **O(bucketCount)** - a small, fixed constant (independent of N or the window's
duration) - never a re-sort of raw samples. `A_large_number_of_observations_completes_quickly` feeds
200k observations across a live-evicting 2-minute window in well under 5s as a smoke guard.

**Retention is a parameter**: `GForceMaxLearner` uses a genuine 2-minute REAL-TIME window (per-key
`RobustBandEstimator`, timestamped by `sample.FrameTime`) - a mid-session surface/condition change ages
out within roughly one window
(`A_surface_condition_change_is_tracked_within_roughly_one_window`).

## 3. Where the shared helper was reused, and where it deliberately was not

- **Reused**: `GForce.GForceMaxLearner` (the auto max-G, the brief's primary target) - 2-minute real-time
  window, no gate.
- **Evaluated and declined for `Normalized.GripLearner`** (measured, not assumed): a windowed-pool swap
  was implemented and measurably reduced outlier sensitivity on this plugin's own captured F1 logs (a
  single injected 7.5g reading among 150 genuine ~2.7-3.4g samples moved the prototype by +0.08g vs the
  existing decaying-max EMA's +0.42g; three such readings among 50 moved it by ~0g vs the EMA's +0.91g).
  However, wiring it in broke a DIFFERENT, already-pinned invariant -
  `RegressionFixTests.A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event`
  - through a well-understood mechanism: that test feeds a CONSTANT 0.2g throughout a raw-wheel sweep,
  and the pool estimator converges to that exact constant far faster than the EMA's slow, seed-anchored
  decay does. The physical-limit detector that feeds `KeyedScaleLearner`'s cross-calibration anchor
  triggers once "current g is close to the learned peak" - the EMA's deliberately slow convergence (its
  own tuned decay rate) is what currently keeps that detector quiet long enough for the cold-start
  identity-rescale guarantee to hold across the whole synthetic sweep; the faster-converging estimator
  triggers it far earlier, letting `KeyedScaleLearner`'s primary tier anchor to an early, lower raw
  reading and rescale a later, higher one above itself. This is a real, structural coupling between
  GripLearner's OWN convergence speed and a downstream detector's timing, not a superficial numeric
  mismatch - reproducing it correctly needs re-deriving that coupling deliberately, not a drop-in
  statistic swap. Reverted per the standing "if adopting risks regressing tuned behaviour, don't"
  instruction. **`GripLearner` already satisfies "no minimum-sample gate"** on its own (its EMA has never
  had one - `Confidence` only ever ceilings `Ratio`, it never blocks `Observe`) - pinned by a new
  mutation-evidence test, `A_single_observation_immediately_moves_the_learned_peak_no_minimum_sample_gate`.
- **Declined for `Normalized.KeyedScaleLearner`**: its primary tier is already anchored to rare,
  independently-detected "at the physical limit" moments (not a raw noisy G stream), a materially
  different and already more outlier-resistant mechanism; its cross-car seeding/cold-warm blend are
  tightly coupled to `OnlineDistributionLearner`'s specific shape. Left as-is, documented in-place.
- **Declined for `RawCalculator.StreamingPercentileLearner`**: out of scope - its call sites (rotation-
  rate/speed ratio, slip ratio, per-gear cruise average) are not G-force samples at all, and its own
  nearest-rank percentile is already outlier-resistant by construction.

## 4. UI readout

Fixed-value spinner is now ALWAYS enabled/editable in both modes (never disabled/overwritten - it is
what Auto uses below its own no-evidence fallback). Readout label to its right, one decimal:
`Fixed: 1.5G` (Fixed mode); `Default: 1.5G. Auto detected: 2.3G` (Auto, evidence exists); `Default:
1.5G. Auto: still using default (no data yet)` (Auto, zero evidence). Localised in both tables
(key-parity test green); Chinese strings added via a Python script emitting `\uXXXX` escapes.

## 5. Mode-dependent transition scaler and the ramp/threshold discontinuity

Two settings: `AutoTransitionAnimationScale` (default 1.2), `FixedTransitionAnimationScale` (default
1.5), both persisted (plain properties on `GForceSettings`, round-tripped through the existing
JSON-based `ConfigStore`), both bounds-enforced to `[0, GForceEngine.MaxTransitionAnimationScale]`.

**Resolution (final, supersedes two earlier designs)**: the max-G value AND the transition scale are
each driven through their own instance of the SAME `MaxRamp` mechanism - a step-size-triggered ramp, not
a sample-threshold-triggered one (there is no sample threshold left to trigger on). Every frame produces
a raw target; if it differs from the last published value by more than 25% of `Max(lastPublished, 1.0)`,
the change is smoothed over 2 seconds of real time (the target is re-read live every frame during the
ramp, so a still-rising value is absorbed naturally); a smaller change applies immediately. For the
scale specifically: raw target is `FixedTransitionAnimationScale` with zero evidence, else
`AutoTransitionAnimationScale` - since 1.2 vs 1.5 is a 20% relative change (under the 25% trigger), it
applies immediately by the SAME rule the owner specified for everything else, so there is no separate
special case and no observed step
(`The_transition_scale_ramps_continuously_alongside_the_max_no_step_at_the_threshold`,
`Ramp_is_continuous_across_a_sweep_of_elapsed_time_no_step_anywhere`). An earlier version of `MaxRamp`
re-checked the trigger threshold every frame even while already ramping, which let a CONVERGING ramp
snap to target the instant its own remaining gap narrowed below 25% - found via
`Ramp_is_continuous_across_a_sweep...` failing at t=1.5s, fixed by only evaluating the trigger when NOT
already mid-ramp.

**Learners get no ramp at all** (owner's explicit instruction) - `GripLearner`'s reverted EMA has never
had one either.

## 6. Default mode -> Auto

`GForceSettings.AccelMaxMode`/`DecelMaxMode` now default to `GMaxMode.Auto`. Verified bit-for-bit:
`Autos_worst_case_with_no_evidence_is_bit_for_bit_identical_to_fixed` constructs an Auto and a Fixed
instance with identical Fixed* values and asserts `EffectiveAccelMaxG`/`EffectiveDecelMaxG`/
`EffectiveAccelTransitionScale`/`EffectiveDecelTransitionScale` agree exactly with zero evidence. The
fallback-to-fixed path now applies ONLY when there are truly zero valid samples for a key AND no
persisted seed - confirmed by `GetLearnedAccelMaxG`/`TryGetAccelAutoDetected` returning 0.0/false in
that one case, and by construction (`RobustBandEstimator.TryEstimate` returns false only at n=0). The
ramp only ever interpolates BETWEEN the fixed default and a real, positive observed value - never
produces anything worse/invalid than either endpoint.

## 7. Mutation evidence

- **(a) blind maximum instead of the trimmed pool**: `RobustBandEstimatorTests.MUTATION_a_...` and
  `GForceMaxLearnerTests.MUTATION_a_...` construct a population with a small high-but-plausible outlier
  cluster plus a larger representative one and assert the estimate is NOT the outlier value - a blind
  max would report it, the pool-based estimate does not.
- **(b) minimum-sample gate reintroduced**: `RobustBandEstimatorTests.TryEstimate_answers_from_a_single_sample_no_gate`,
  `GForceMaxLearnerTests.A_single_observation_immediately_produces_a_usable_value_no_gate`, and
  `GripLearnerTests.A_single_observation_immediately_moves_the_learned_peak_no_minimum_sample_gate` all
  fail immediately if any gate (e.g. "no value below N observations") is reintroduced anywhere in the
  learner paths.
- **(c) count-based window instead of timestamp-based**: `RobustBandEstimatorTests.MUTATION_c_...` and
  `GForceMaxLearnerTests.MUTATION_c_...` advance real time (not sample count) past the 2-minute window
  with no new samples and confirm the old cluster ages out.

## 8. Log-based validation

Real logs used: `c_1_5_3_e_d\` and `Common_1_5_e_d\` (F12025/Sauber telemetry; several other files in
those directories carry a blank `Diag.GameId`/`Diag.CarId`). **No Forza-labelled telemetry exists in the
provided log set** - this is a genuine gap flagged as a concern below; validation against a collision
scenario was done by injecting synthetic 7.5g "collision-adjacent" spikes into the real F12025 Slowing-
direction data (well under the 8g decel plausibility cap, matching "clipping the car ahead"), not by
replaying an actual Forza session.

Full-session (n=3354) pool estimate on the real log: **3.17g** (max observed 5.04g, 95th percentile
3.31g) - plausible for an F1 car's braking, comfortably below its own recorded outliers.
Outlier-sensitivity, pool estimator vs the (reverted-for-GripLearner, still-used-nowhere-in-this-change)
decaying-max EMA, on the SAME real data with one injected 7.5g spike:

| n (valid samples) | pool moved | decaying-max moved |
|---|---|---|
| 20 | +0.0000g | +0.948g |
| 50 | +0.0002g | +0.912g |
| 150 | +0.0825g | +0.424g |

## Concerns

1. **No Forza log was available** to validate the NPC-collision scenario directly - synthetic injection
   into real F1 telemetry was used instead; methodologically sound but not literally Forza data.
2. **GripLearner/KeyedScaleLearner were not migrated** despite being explicitly requested for
   re-evaluation multiple times - a concrete, well-understood regression blocks it (see section 3); if
   revisited, it needs the physical-limit-detector timing and the cold-start identity test's own
   tolerance examined together, not just a statistic swap.
3. The settings screenshot (`settings-gforce.png`, 1024x1256, G-Force tab content only) was regenerated
   in the harness folder only, per instruction - the in-repo `docs\images\settings-gforce.png` was left
   untouched since replacing it was not requested.

# Cold-start convergence: can the Normalized-layer identity period be shortened?

## STATUS

**NO PRODUCTION CODE CHANGED.** Measured directly against all seven available real logs (the two owner-
specified directories, `c_1_5_3_e_d\` and `Common_1_5_e_d\`, plus the pre-existing FH6-mixed capture used
by earlier reports as a second-title guardrail): the current concave, dispersion-weighted confidence ramp
(`ColdWarmBlend.ConcaveHotWeight`, `KeyedScaleLearner.CalibrationConfidenceScaleSamples = 200`, `gamma =
2`) already converges as fast as the data safely supports. Shortening it (smaller scale, smaller gamma) DOES
reach a given confidence level in fewer samples, but the SAME data shows this trades away a real, currently-
almost-zero margin against transient OVER-reporting (see "Candidate ramp evaluation" below) - directly the
risk the owner's brief said to weigh against speed. `RobustBandEstimator` was re-examined for adoption in
both `GripLearner` (the physical-limit detector) and `KeyedScaleLearner` (the scale learner itself) and
declined in both, for two DIFFERENT, freshly-measured reasons (not merely deferred to the earlier report -
see below). **Nothing was changed; this is a well-evidenced "no change warranted."**

`QAdvanceFeedback.Tests`: **853/853 passed, 0 failed, 0 skipped** (unchanged - no code was touched).
`dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**.
`QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` (+ `.pdb`) - **single-DLL
confirmed**.

---

## Method

A throwaway analysis harness (link-compiles `QAdvanceFeedback\Core\**\*.cs` directly, the same technique
every prior report's own harness used - not part of the solution, not committed) replayed all seven logs
frame-by-frame through the REAL, unmodified `NormalizedWheelLockSlipEngine`/`KeyedScaleLearner`/
`GripLearner`, with a fresh engine per log (matching the established project convention). For every frame
where `KeyedScaleLearner.ObserveAtPhysicalLimit` would fire (re-derived, never assumed, from the SAME
public surface the engine itself exposes - `LockPhysicalReference.Ratio(..., applyColdStartCeiling:
false)` blended by `LockLooseFraction`, exactly mirroring the engine's own internal `physicalRatioNow`/
`physicallyAtLimit` computation), the harness recorded:

- the REAL, production `KeyedScaleLearner`-published ceiling (`engine.LockScaleCeiling`) after that
  observation - **the "current mechanism"**;
- the SAME raw at-limit reading fed in parallel to a real `RobustBandEstimator` instance (unbounded
  window, default pool parameters) - **the "robust estimator" comparison**;
- the SAME raw at-limit reading fed in parallel to a real `OnlineDistributionLearner` + `WelfordAccumulator`
  (reproducing `KeyedScaleLearner`'s own internal average/dispersion exactly), so alternative `(scale,
  gamma)` ramps could be recomputed offline using the REAL `ColdWarmBlend` math against the REAL average
  trajectory, without re-running the engine per candidate.

**Braking zones**: a maximal run of frames passing the exact "qualifying for Lock learning" test the engine
itself uses (`TelemetryLearningGate.IsValid` && `BrakePercent >= 10` && `|LateralG| <= 0.5` && direction ==
Slowing), tolerating gaps up to 1.0s (trail-braking modulation) before starting a new zone. This is the
harness's OWN zone convention, chosen for this report and applied identically to every log/every candidate
for a fair comparison - it is not guaranteed to reproduce an earlier report's own zone count exactly (a
different zone-grouping choice), though it lands close (e.g. wet-ShakeIt: 3 zones here vs 3 previously
reported; dry-Raw: 6 here vs 7 previously reported).

Logs used: `Common_1_5_e_d\` (wet-Raw, wet-ShakeIt, dry-Raw, dry-ShakeIt - F12025/Sauber), `c_1_5_3_e_d\`
(ShakeIt and Raw - F12025/"F1 Generic"), plus the pre-existing FH6-mixed capture
(`QAdvanceFeedback.session-20260815-230140.csv`, already used by the robust-auto-gforce-report as the
project's only second-title cross-check) for the same reason that report used it. Only the Lock/braking
channel was analysed (the task's own "braking zones" framing) - Slip was not separately measured; the same
class/mechanism applies, so this is a scope note, not a different finding, but it is unverified for Slip
specifically.

---

## 1. Convergence measurements - current mechanism vs. RobustBandEstimator

"Settle" = the first sample index `n` after which EVERY later value in that log's own series stays within
the stated tolerance of that series' OWN final (end-of-log) value - not merely a lucky early touch.

| log | total physical-limit samples (n) | zones | CURRENT: settle 10% / 5% (n, zone) | ROBUST: settle 10% / 5% (n, zone) | CURRENT final | ROBUST final |
|---|---|---|---|---|---|---|
| wet-Raw | 178 | 4 | n=58 (z1) / n=125 (z2) | n=75 (z1) / n=76 (z1) | 69.34 | 86.21 |
| wet-ShakeIt | 114 | 3 | n=1 (z1) / n=57 (z2) | n=16 (z1) / n=18 (z1) | 69.92 | 97.09 |
| dry-Raw | 278 | 6 | n=88 (z0*) / n=117 (z0*) | n=213 (z2) / n=215 (z2) | 60.74 | 47.69 |
| dry-ShakeIt | 53 | 7 | n=16 (z1) / n=28 (z2) | n=45 (z6) / n=46 (z6) | 64.12 | 22.01 |
| c1153-ShakeIt (F1 Generic) | 227 | 13 | n=66 (z2) / n=124 (z3) | n=19 (z1) / n=22 (z1) | 68.23 | 96.09 |
| c1153-Raw (F1 Generic) | 945 | 14 | n=746 (z8) / n=789 (z9) | n=681 (z7) / n=821 (z9) | 61.90 | 66.18 |
| FH6-mixed | 194 | 6 | n=1 (z1) / n=152 (z6) | n=177 (z6) / n=180 (z6) | 69.14 | 97.57 |

\* `z0` means the settle sample occurred on a physical-limit-anchored frame that preceded this log's own
first strictly-qualifying learning zone - the physical-limit trigger's OWN activation condition (braking
threshold + Slowing direction only) is broader than the full learning-gate test the zone boundary uses, so
an early physical-limit observation can occur slightly before "zone 1" by this harness's stricter
definition. Not a bug - a real, documented difference between the two gates (see
`NormalizedWheelLockSlipEngine.ComputeChannel`'s own remarks).

**Reading this table**: the CURRENT mechanism reaches its own (still session-bounded, not necessarily fully
asymptotic) reading within 5% in as few as 1-16 physical-limit observations for three logs (wet-ShakeIt,
dry-ShakeIt, FH6-mixed all settle 10% at n=1 or n=16), but needs the majority of the session's own evidence
for two others (c1153-Raw: 789 of 945, 83%; dry-Raw: 117 of 278, 42%). This is NOT evidence the ramp itself
is slow - c1153-Raw's own raw-at-limit readings drift steadily downward across the whole session (first
readings 60-78, last readings 20-29 - see the harness's raw dumps), i.e. the underlying quantity is
genuinely non-stationary there (track/tyre evolution over a long session), so no ramp, however fast, could
have "settled" earlier without ALSO committing to a value the data itself hadn't yet produced.

RobustBandEstimator's own final value diverges wildly from the current mechanism's, in EITHER direction,
across these seven logs (from -34% at dry-Raw and -66% at dry-ShakeIt to +40% at wet-ShakeIt/FH6-mixed) -
see section 3 for why this matters for the adoption decision.

---

## 2. The pinned cold-start test - what it actually asserts, and the verdict

`RegressionFixTests.A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event`
feeds a BRAND-NEW (game,car,source) key a CONSTANT 0.2g (`BrakingSample(0.2)`, "plenty of grip, no
physical-limit detection") across a full raw-wheel sweep (0->100->0, ~102 frames total) and asserts
`normalized <= raw` at every single frame.

**Directly re-derived, not assumed** (a standalone check against the real, unmodified classes):

```
EMA GripLearner (SHIPPED):        ratio first reaches >=0.85 at sample 482 (learnedPeak decays 1.0 -> 0.235)
RobustBandEstimator (fed the SAME constant 0.2g stream): ratio reaches >=0.85 at sample 1 (estimate = 0.2000 exactly)
```

The pinned test's own budget is ~102 frames. The SHIPPED EMA needs 482 qualifying samples to call a
constant 0.2g "physically at the limit" - it NEVER fires within this test, so `KeyedScaleLearner`'s primary
tier stays at `Count == 0` (plain identity) for the whole test, which is exactly why it passes today. A
`RobustBandEstimator`-backed physical reference would call the SAME constant stream "at the limit" from the
very FIRST sample - meaning ANY sustained, ordinary, low-g stretch of driving (cruising, gentle trail-
braking, a coast) would immediately start teaching `KeyedScaleLearner` a wide range of raw readings across
the WHOLE session (since "at the limit" would misfire continuously, not just once), which is precisely the
mechanism the earlier report measured breaking `normalized <= raw`.

**Verdict: this is a genuine regression guard, not a stale pin of behaviour the owner now wants changed.**
It protects an invariant orthogonal to the question this task asks (how fast `KeyedScaleLearner`'s OWN
confidence ramp should trust ACCUMULATED evidence for a key) - it instead protects whether a reading gets
treated as evidence AT ALL. The owner's own stated priority ("a missed cue costs a warning; a full-strength
false alarm costs their trust", and they have already reported exactly this symptom once) is exactly what
this test defends. Confirmed, not merely re-asserted from the earlier report's own text.

---

## 3. RobustBandEstimator adoption - re-examined for BOTH call sites, declined for two different reasons

**`GripLearner` (the shared physical-limit detector feeding `physicallyAtLimit`): declined, confirmed.**
Section 2 directly reproduces the mechanism: RobustBandEstimator converges to a constant stream from
sample 1, which would make the physical-limit detector fire on ordinary, non-limit driving. The earlier
report's decision stands, now independently re-derived with a direct numeric reproduction (482 vs. 1
samples) rather than taken on faith.

**`KeyedScaleLearner`'s OWN primary-tier statistic (currently `OnlineDistributionLearner`'s decaying
weighted mean of physically-anchored raw readings): ALSO declined, for a DIFFERENT, freshly-measured
reason.** This swap is NOT blocked by the pinned test above - with `GripLearner` left unchanged, that
test's `physicallyAtLimit` never fires at all, so `ObserveAtPhysicalLimit` is never called and the choice
of statistic inside `KeyedScaleLearner` is irrelevant to that specific test. However, feeding the exact same
physically-anchored raw-reading sequences from all seven real logs into a real `RobustBandEstimator`
(section 1's table) shows its own final estimate diverges from the current mean-based one by large,
INCONSISTENT amounts - safer (higher ceiling, more under-reporting headroom) on 5 of 7 logs, but materially
RISKIER (lower ceiling -> larger rescale factor -> more over-reporting) on dry-Raw (47.69 vs 60.74, -21%)
and dramatically on dry-ShakeIt (22.01 vs 64.12, -66% - a rescale factor of 75/22 = 3.4x instead of the
current 75/64 = 1.17x, on real F1 telemetry). Since the physically-anchored readings ARE ALREADY
pre-filtered to rare, externally-detected "at the limit" moments (not a raw noisy stream RobustBandEstimator
was designed to reject outliers from), the estimator answers a materially different question here (a
near-max pool blend of already-filtered readings, rather than "this source's typical reading when at the
limit") - and on real data that difference lands on the wrong (over-reporting) side often enough, and hard
enough, to decline it without a dedicated re-derivation of the downstream cross-car-seed/dispersion
thresholds this class's own comments already describe as "extensively re-tuned across multiple prior
passes." This is a genuinely different, independently-measured finding from the `GripLearner` case, not a
restatement of it.

---

## 4. Candidate ramp evaluation - is a shorter/steeper curve justified by the data?

Recomputed `ceiling(n) = ColdWarmBlend.Blend(75, average(n), ColdWarmBlend.ConcaveHotWeight(n, cv(n), scale,
gamma))` for each log's REAL average/dispersion trajectory, at several candidate `(scale, gamma)` pairs,
using the real production math:

| log | metric | 200/g2 (current) | 150/g2 | 100/g2 | 60/g2 | 200/g1.5 |
|---|---|---|---|---|---|---|
| wet-Raw | over-report risk (undershoot) | 0.0% | 0.0% | 1.7% | 4.0% | 0.0% |
| wet-ShakeIt | over-report risk (undershoot) | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| dry-Raw | over-report risk (undershoot) | **3.2%** | 3.2% | 5.1% | **18.4%** | 3.2% |
| dry-ShakeIt | over-report risk (undershoot) | 0.0% | 0.0% | 0.7% | **5.5%** | 0.0% |
| c1153-ShakeIt | over-report risk (undershoot) | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| c1153-Raw | over-report risk (undershoot) | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |
| FH6-mixed | over-report risk (undershoot) | 6.9% | 8.1% | 8.1% | 8.1% | 5.9% |

("over-report risk / undershoot" = the worst transient dip of `ceiling(n)` BELOW that candidate's own final
value, as a percentage - a lower ceiling means a LARGER `75/ceiling` rescale factor, i.e. a temporary
over-read relative to where the session eventually settles. This is the direction the owner explicitly does
not want a faster ramp to worsen.)

**The current scale (200) already achieves 0.0% measured over-report risk in 5 of 7 real logs**, and only a
small, pre-existing 3.2%/6.9% in the other two (dry-Raw, FH6-mixed) - not caused by the ramp being
conservative, but by real early-session noise in the underlying average itself. Shrinking the scale
monotonically WORSENS this on every log where it moves at all - most sharply on dry-Raw (3.2% -> 18.4% at
scale 60) and dry-ShakeIt (0.0% -> 5.5%) - while only modestly shortening the settle point in return (e.g.
dry-Raw's 5%-settle: n=107 at scale 200 vs n=57 at scale 60, roughly 2x faster, for a 5-6x increase in
over-report risk). A steeper `gamma=1.5` shows no consistent benefit either (mixed up/down across logs,
worse on FH6-mixed's settle point). **This is a real, measured trade-off, not a hypothetical one: the data
does not justify shortening the identity period.**

---

## 5. Before/after on all logs

No code was changed, so there is no "after" distinct from "before." Section 1's CURRENT-mechanism numbers
ARE both the before and after state; the owner's already-working scenarios (F1 wet/dry, FH6) are therefore
unaffected by construction, not merely re-verified:

- Full suite: 853/853 passing (unchanged).
- The existing continuity guard (`Calibration_confidence_grows_continuously_with_no_jump_at_any_sample_count`)
  and its own hard-threshold mutation guard remain exactly as before - no sweep was re-run since nothing in
  the ramp changed.

---

## 6. Mutation evidence

No code changed, so no new mutation was introduced or needed. The standing "no minimum-sample gate anywhere"
guards (`RobustBandEstimatorTests.TryEstimate_answers_from_a_single_sample_no_gate`,
`GForceMaxLearnerTests.A_single_observation_immediately_produces_a_usable_value_no_gate`,
`GripLearnerTests.A_single_observation_immediately_moves_the_learned_peak_no_minimum_sample_gate`) remain
green, confirmed by the full 853/853 run above - this task did not touch, weaken, or need to re-verify them
beyond that.

---

## Concerns

1. **"Zone" is this report's own convention** (a 1.0s-gap-tolerant grouping of frames passing the full
   Lock-learning-qualification test), chosen for internal consistency across every log/candidate in this
   analysis - it lands close to, but does not exactly reproduce, an earlier report's own zone count for the
   same logs (e.g. dry-Raw: 6 here vs. 7 previously). Treat zone counts as illustrative, sample counts as
   the precise unit.
2. **The harness's simplified "candidate ramp" recompute (section 4) always uses anchor=75**, whereas the
   REAL `KeyedScaleLearner` can, once a single key's own confidence weight exceeds 0.5, feed its own
   cross-car-seed dictionary and then read it back for itself (the cross-car seed key is `(gameId,
   sourceIdentity)` only, not `carId` - a car CAN see its own seed once trusted). This is a small, capped
   (never-amplifying, always >=75) second-order effect that explains the few-point gap between section 1's
   "CURRENT final" (real engine) and section 4's "200/g2 final" (harness recompute) - it does not change any
   of section 4's qualitative comparisons between candidate scales, all of which share the same
   simplification.
3. **Only the Lock/braking channel was measured.** Slip (the accelerating/traction-referenced channel)
   shares the exact same `KeyedScaleLearner`/`ColdWarmBlend` mechanism, so the same conclusion is expected
   to apply, but this was not independently verified against a Slip-heavy log.
4. **`c1153-Raw`'s own late-session drift** (raw at-limit readings falling from ~60-78 early to ~20-29 late
   across a single, unusually long 20,353-frame session) suggests a genuine non-stationary condition change
   mid-session (tyre wear/track evolution) rather than measurement noise - worth flagging for anyone reading
   its "slow to settle" numbers as evidence the ramp itself is slow; it is closer to the ramp honestly
   tracking a moving target.
5. **The FH6-mixed log is outside the two directories named in this task's brief** (it lives at the repo
   root, not under `c_1_5_3_e_d\`/`Common_1_5_e_d\`) - included anyway, matching the established
   project-convention of using it as the one available second-title cross-check (as the
   robust-auto-gforce-report already did), not a substitute for either named directory.

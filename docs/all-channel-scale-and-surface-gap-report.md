# All-channel aggregated scale (Objective A) and Raw's wet/dry gap (Objective B) - SHIPPED FOR LOCK ONLY / DIAGNOSED, NOT CLOSED

## STATUS

**OBJECTIVE A - SHIPPED FOR WHEEL LOCK ONLY; NOT SHIPPED FOR WHEEL SLIP** (measured to be a wash-to-regression
there, on the metric the owner said matters most). **OBJECTIVE B - DIAGNOSED, NOT CLOSED**: the residual
Raw wet-vs-dry gap traces to cold-start-confidence divergence between four short, independently-captured
sessions (not a controlled same-session transition), not a deterministic surface-keying defect - closing it
further would mean retuning owner-tuned `GripLearner` constants against data this repo does not have,
risking regressing the specific, already-measured problems those constants were tuned to fix.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **900/900 passed, 0 failed, 0 skipped** (894
baseline + 6 new tests in `AggregatedAllScaleTests.cs`, plus one pre-existing test re-expressed against the
new, intended contract - see Part 4). `QAdvanceFeedback\bin\Release\net48\` contains exactly
`QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**. No git operation of any kind was run (the repo
is not under git). No recursive deletes. No Chinese-string changes were needed for this task. A full
pre-task snapshot was verified present and correct at `QAdvanceFeedback_1.0.6.7_band_mapping\` (diffed
against the live working folder before any edit - identical except this task's own scratchpad files) -
**not used**, since the task did not need to revert.

**Headline result (Objective A, Lock)**: published `Diag.Lock.All`'s own time-above-80 falls from **12.83%**
to **1.89%**, now closely tracking the car-level severity's own **2.04%** (previously a ~10.8-point gap,
now ~0.15 points) - achieved by construction, not by retuning: the four blended per-wheel native readings
are now scaled by ONE uniform multiplier (`severity / Aggregator.Compute(nativeWheels, weights).All`)
instead of the old flat-mean-relative proportion, which exploits `Aggregator.Compute`'s own degree-1
homogeneity to make re-aggregating the published per-wheel output reproduce car-level severity almost
exactly. **Headline result (Objective A, Slip)**: the identical mechanism was measured live for Slip too
and REJECTED - Slip's own pre-existing, owner-tested `SlipFloorFactor=0.70` (a `Math.Max` floor stage
*inside* `Aggregator.Compute` itself, unrelated to this task) pins published `.All` at exactly 70.0 whenever
the large multiplier this mechanism needs for Slip clamps even one wheel to its own 100 ceiling - measured
directly: 839-1200+ of the frames where Slip's own car-level severity exceeded 80 published exactly 70.0.
Net effect on Slip's own headline metric: **1.02%** (down from a would-be improvement target of 8.74%, and
actually slightly worse than the pre-existing 1.27% baseline) - **zero or negative net gain on Slip's own
highest-priority metric**, so Slip keeps its pre-existing mean-relative-proportion mechanism, unmodified.

---

## PART 0 - THE FORMULA AS SHIPPED (Objective A, Lock only)

In `NormalizedWheelLockSlipEngine.ComputeChannel`
(`QAdvanceFeedback\Core\Normalized\NormalizedWheelLockSlipEngine.cs`), after the existing, UNCHANGED
`severity` (the ΔG-collapse car-level number, `docs\delta-g-band-mapping-report.md`) and the existing,
UNCHANGED fallback-blended per-wheel native readings `bw0..bw3` are computed:

```
sourceAllNative = Aggregator.Compute(new Corners(bw0, bw1, bw2, bw3), weights).All   -- SAME weights that
                                                                                          will re-combine
                                                                                          the published output
allScale        = SafeDiv(severity, sourceAllNative, fallback: 1.0)                  -- the ONE learned scale
wheel_i         = Clamp0100(bw_i * allScale)   for i in {FL, FR, RL, RR}             -- applied UNIFORMLY
```

`Front`/`Rear`/`Left`/`Right`/`All` fall out of the SAME downstream `Aggregator.Compute(lockWheels,
lockWeights)` call `Compute` already made before this task - no separate "apply to the groups too" step was
needed. This directly implements the owner's own proposal (compute the All-channel value from the per-wheel
source values using the existing aggregation weights, learn the calibration on that one aggregated value,
apply the ONE learned scale uniformly to All/Front/Rear/Left/Right/each wheel) as a plain per-frame ratio
rather than a persisted/smoothed learner (see Part 1 for why a persisted learner was evaluated and
rejected). The `proportionMean <= NoRawSignalEpsilon` "no native differentiation" branch is UNCHANGED
(still distributes severity uniformly, `allScale` stays at its neutral 1.0).

**Why this closes the gap by construction**: `Aggregator.Compute`'s two blend stages (`Max(a,b)*wMax +
Min(a,b)*wMin`, `a*wFront + b*wRear`) - and, for a channel with a nonzero `SlipFloorFactor`, its floor stage
(`Max(value, participatingMax*factor)`) - are all homogeneous of degree 1 in their four inputs for any
non-negative scalar `k` (`Max(a*k,b*k) = k*Max(a,b)`, etc.). Re-aggregating four wheels each scaled by the
SAME `allScale` therefore reproduces `allScale * sourceAllNative = severity` EXACTLY at the All level, for
EVERY frame, mod the 0-100 clamp applied to each wheel BEFORE `Aggregator.Compute` sees it (the only place
the exact identity can break, at extreme `allScale` values - see Part 2's Concerns).

**Per-channel toggle**: `NormalizedWheelLockSlipEngine.LockUsesAggregatedAllScale = true`,
`SlipUsesAggregatedAllScale = false` - internal compile-time constants (no new driver-facing setting), one
per channel, exactly matching the owner's "evaluate the two channels independently, ship exactly what wins"
instruction. `ComputeChannel` branches on this per-channel flag; the `false` branch is the OLD flat-mean-
relative proportion, kept byte-for-byte.

---

## PART 1 - WHY A PER-FRAME RATIO, NOT A PERSISTED LEARNER

The owner's own wording ("LEARN the calibration") and worked example (a native aggregate of 73 "learned" to
mean max grip) both read naturally as a persisted, `KeyedScaleLearner`-style ceiling. That alternative was
considered and rejected in favour of the exact per-frame ratio above, for a concrete, load-bearing reason:
a persisted ceiling learned from PAST "at-the-limit" moments only APPROXIMATELY reconstructs `severity` on
a DIFFERENT, later frame (the native aggregate's own relationship to the physical truth drifts frame to
frame, exactly the unreliability `docs\two-signal-band-mapping-report.md` already measured for the
configured source). The acceptance bar is specifically "published `.All` should FALL to match car-level
severity" - a quantity defined framewise, not "settle into the right ballpark eventually". The per-frame
ratio satisfies that bar EXACTLY (mod clamping) by construction; a persisted ceiling would not, and would
reintroduce exactly the kind of source-trust the ΔG-collapse mapping was built to move away from. No new
state is learned or persisted by this mechanism (see Part 2's version-bump note).

---

## PART 2 - ACCEPTANCE RESULTS, MEASURED THROUGH THE REAL ENGINE

**Methodology**: `scratchpad\deltag-validation-harness\` (reused, not rebuilt - the SAME harness
`docs\delta-g-band-mapping-report.md` used, extended with: per-channel per-wheel discrimination for BOTH
Lock and Slip - the ORIGINAL harness's own `PER_WHEEL` check only ever tested Lock, despite the acceptance
bar naming "per-wheel discrimination" generally, a gap discovered and fixed while extending it this task;
`GROUP` occupancy for Front/Rear/Left/Right; `ALLSCALE` distribution diagnostics; a `LockCarLevelSeverity`/
`SlipCarLevelSeverity`-vs-published-`.All` comparison for source/surface invariance, not just the published
number; and a `LEARNER_DIAG` line per log exposing `PublishedPeakG`/`Samples`/`Confidence` plus an
independent P99 of isolated qualifying |G| for Objective B). All nine logs replayed exactly as before (7 of
9 replayable, 2 lack `Diag.Telemetry.*` entirely - unchanged, reported not silently dropped).

### (1) Band correspondence at u = 0.75 / 0.90 / 1.00 (targets 30/60/80) - UNCHANGED, not regressed

Measured against car-level severity (this task does not touch `ComputeDeltaGCollapseSeverity` at all):

| channel | u≈0.75 | u≈0.90 | u≈1.00 |
|---|---|---|---|
| Lock, pooled | 30.9 (unchanged) | 59.2 (unchanged) | 79.4 (unchanged) |
| Slip, pooled | 30.7 (unchanged) | 61.4 (unchanged) | 80.0 (unchanged) |

Identical to the pre-task shipped baseline, confirmed by direct re-measurement, not merely assumed from the
formula being untouched.

### (2) Published `.All` time above 80 (the headline metric)

| | before this task | after (car-level severity, for reference) |
|---|---|---|
| **Lock** | **12.83%** | **1.89%** (car-level 2.04%) |
| **Slip** (not shipped) | 1.27% | 1.02% (car-level 8.74% - unreached) |

Lock's published figure **fell materially**, as the brief predicted if Objective A worked - from a
~10.8-point gap above car-level severity to a ~0.15-point gap. Slip's own published figure moved the WRONG
way (1.27% -> 1.02%, further from its own 8.74% car-level target) under the identical mechanism - see Part 3
for why, and why Slip therefore does not ship it.

### (3) Occupancy of 30-60 / 60-80 (published `.All`, pooled) - still substantial and graded

| channel | 0-30 | 30-60 | 60-80 | 80-100 |
|---|---|---|---|---|
| Lock (NEW) | 37.7% | **45.3%** | **14.0%** | 1.9% |
| Lock (baseline) | 19.4% | 33.8% | 33.0% | 12.8% |
| Slip (unchanged) | 48.2% | 16.9% | 32.3% | 1.3% |

Lock's distribution shifted toward the lower bands - EXPECTED and CORRECT, not a defect: the baseline's own
33.0%/12.8% in 60-80/80-100 were inflated by exactly the aggregation-amplification bug this task fixes (a
frame the mapping itself scored at severity 34 was being published at 56+ before this task - see Part 3).
The new distribution is still smoothly graded (monotonically decreasing through 30-60/60-80/80-100, both
30-60 and 60-80 meaningfully populated) - not bimodal. Slip's occupancy is byte-for-byte unchanged (not
shipped).

### (4) Source invariance (Raw vs ShakeIt, same c153 route) - IMPROVED for Lock, unchanged for Slip

| channel | published gap (before) | published gap (after) | car-level severity gap (unchanged) |
|---|---|---|---|
| Lock | 3.8 | **1.0** | 1.0 |
| Slip | 0.6 | 0.6 (not shipped) | -1.0 |

Lock's published gap improved from 3.8 to 1.0, now matching the mapping's own near-perfect 1.0 severity-
level invariance almost exactly (previously diluted by the same aggregation-weight distortion). Not
regressed for either channel.

### (5) Surface invariance (Common_1_5_e_d wet vs dry) - Objective B's own target; unchanged by Objective A

| channel/source | published gap (NEW) | car-level severity gap (unchanged, before AND after) |
|---|---|---|
| Lock/Raw | 10.9 | **11.5** (baseline: 11.6) |
| Lock/ShakeIt | 3.7 | 3.7 (unchanged) |
| Slip/Raw (not shipped) | 17.2 | 13.6 (baseline: 13.7) |
| Slip/ShakeIt (not shipped) | -8.8 | -1.0 (unchanged) |

Car-level severity gaps are IDENTICAL (mod rounding) to the pre-task baseline, confirming directly that
Objective A does not touch this mechanism at all. Lock's PUBLISHED gap (10.9) now closely tracks its own
severity gap (11.5) for the first time - previously the published number was a distorted, less-faithful
reflection of the same underlying gap. **Interaction between the two objectives, reported explicitly as
requested**: Objective A does not close Objective B's gap (it cannot - it never touches `severity`), but it
DOES make the published number a more honest, faithful reflection of that still-open gap, rather than
papering over or randomly amplifying it via aggregation-weight noise as before. See Part 5 for the
diagnosis.

### (6) Per-wheel discrimination - Lock essentially unchanged; Slip separately measured for the first time

| log/source | Lock (NEW) | Lock (baseline) | Slip (NEW - not shipped, first time measured separately) |
|---|---|---|---|
| c153/ShakeIt | 100.0% | 99.2% | 83.4% |
| c153/Raw | 100.0% | 99.9% | 79.1% |
| common_wet/Raw | 100.0% | 99.6% | 72.3% |
| common_wet/ShakeIt | 100.0% | 100.0% | 74.4% |
| common_dry/Raw | 100.0% | 100.0% | 56.8% |
| common_dry/ShakeIt | 99.9% | 99.9% | 51.1% |
| s230140 | 91.0% | 91.0% | 34.3% |

**Lock's per-wheel discrimination is NOT degraded - it is essentially identical to (fractionally better
than) the pre-task baseline.** This is a direct, provable consequence of the mechanism, not luck: scaling
all four wheels by the SAME positive `allScale` this frame can never reorder them (`bw_i * k` preserves the
ranking of `bw_i` for any `k > 0`), so per-wheel discrimination is invariant to which formula produced the
shared scalar, up to boundary-clamp ties (rare, and no worse than before - see
`AggregatedAllScaleTests.Per_wheel_ranking_is_preserved_under_the_uniform_scale_...`/similar). **Honest
disclosure per the brief's own instruction**: the pre-existing harness's `PER_WHEEL` check only ever tested
Lock - Slip's own per-wheel discrimination (34.3%-83.4%, clearly lower than Lock's) was never separately
measured before this task and is NOT a regression this task introduced (it is the pre-existing,
byte-for-byte-unchanged mean-relative-proportion mechanism's own long-standing behaviour, now visible for
the first time). See Part 3 for the surprising finding that Objective A would have IMPROVED this number for
Slip substantially, and why it still does not ship there.

### (7) Falling-branch validity - UNCHANGED, not regressed

| channel | mean ΔG, 80-90 | mean ΔG, 90-100 |
|---|---|---|
| Lock, pooled | -0.1028 (n=135) | -0.6128 (n=5) |
| Slip, pooled | -0.0201 (n=2249) | -0.0878 (n=1304) |

Identical to the pre-task baseline (this task does not touch the branch/collapse mechanism at all).

---

## PART 3 - THE HONEST PER-WHEEL/PER-CHANNEL COST-BENEFIT (what was NOT shipped, and why)

**For Lock**: no honest cost was found. Published `.All` improved dramatically (12.83% -> 1.89%), source
invariance improved (3.8 -> 1.0), and per-wheel discrimination did not measurably change (99.0-100% vs
91.0-100% baseline - the tiny remaining differences are rounding/clamp noise, not a systematic loss). This
is a rare case where the fix is close to a pure win on every measured axis for the channel it targets.

**For Slip, measured with `SlipUsesAggregatedAllScale` temporarily flipped `true`** (same mechanism, same
code path, only the boolean differs) and compared side-by-side against the shipped `false`:

| metric | Slip OLD (shipped) | Slip WITH Objective A (measured, NOT shipped) |
|---|---|---|
| published `.All` time-above-80 (pooled) | 1.27% | 1.02% (WORSE - further from the 8.74% target) |
| per-wheel discrimination, c153/ShakeIt | 83.4% | **96.7%** |
| per-wheel discrimination, c153/Raw | 79.1% | **95.7%** |
| per-wheel discrimination, common_wet/Raw | 72.3% | **94.8%** |
| per-wheel discrimination, common_dry/ShakeIt | 51.1% | **100.0%** |

**Root cause of the All-channel loss** (diagnosed, not assumed): Slip's own owner-tested
`AggregationWeights.SlipDefaults.SlipFloorFactor` (0.70) is a `Math.Max` floor stage BUILT INTO
`Aggregator.Compute` itself, pre-existing and unrelated to this task, that pins a group's published value at
`Max(blendedValue, strongestParticipatingWheel * 0.70)` - explicitly designed to stop a single strongly-
spinning wheel being diluted away. Because Slip's own native per-wheel readings run far smaller than the
G-derived severity far more often than Lock's do (measured: Slip's `allScale` needed values up to ~8865x on
these logs, vs Lock's own max of ~2569x), the large multiplier Objective A needs for Slip clamps at least one
wheel to its own 100 ceiling far more often - and once ANY wheel reads 100, the floor pins the WHOLE
published All at exactly 70.0, regardless of what severity actually is (measured: 839 to 1200+ of the
frames where Slip's own car-level severity exceeded 80 published exactly 70.0 - see the harness's own
`ALLGAP_SLIP` diagnostic). This is a genuine, structural conflict between two independently-owner-tested
mechanisms (the ΔG-collapse mapping's own severity-tracking goal, and the Slip floor's own "never dilute a
lone spinner" goal) that Objective A's uniform-scale approach cannot resolve without touching the floor
itself (explicitly out of scope - the brief's own "do not change the owner's tested defaults").

**Root cause of the per-wheel gain** (diagnosed): the OLD formula's per-frame scalar is
`severity / flatMean(bw)`; the NEW formula's is `severity / weightedAggregate(bw)`. Slip's own axle weight
(`WMax=0.85`) weights heavily toward whichever wheel is ALREADY spinning hardest, so the weighted aggregate
runs noticeably CLOSER to that dominant wheel's own reading than the flat mean does when only one wheel is
spinning - producing a SMALLER scalar, LESS saturation-driven clamping, and therefore FEWER accidental ties
at the 100 ceiling (the `Array.IndexOf(...).Max()` tie-breaking artifact that was silently degrading the OLD
formula's own measured discrimination).

**Decision, applying the owner's own stated priority order** ("WheelSlip: All is the highest priority, but
per-wheel and per-group values also genuinely matter"): since Objective A provides ZERO OR NEGATIVE gain on
Slip's own highest-priority metric (published All time-above-80), it is not shipped for Slip even though it
would have been a genuine, substantial win on the secondary (per-wheel) metric - "ship exactly what wins"
is read here as "wins on the priority metric", not "wins on some metric". This trade-off is reported in full
so the owner can weigh in - a future, out-of-scope change to the Slip floor mechanism itself (not attempted
here) might let both wins be captured simultaneously.

---

## PART 4 - EXISTING TEST SUITE: WHAT CHANGED AND WHY

**One pre-existing test failed immediately** after wiring Objective A in for Lock -
`Aggregation_weights_are_reread_every_call_with_no_engine_rebuild_needed` asserted
`withFrontBias.LockAll != withRearBias.LockAll` under two extreme, custom aggregation-weight configurations
- exactly the OLD weight-sensitivity of `LockAll` that this task's own diagnosis (published `.All` inflated
by aggregation-weight interaction) identifies as the defect being fixed. Updated, not deleted, with the
historical rationale kept visible: the "tune without a rebuild" requirement is still tested, now via
`LockFront` (which still responds to weight changes, since the per-frame scalar itself depends on how the
weights combined the native aggregate) instead of `LockAll` (now deliberately weight-invariant, asserted
explicitly with `Assert.Equal`, not merely left unchecked).

**6 new tests** added in `AggregatedAllScaleTests.cs` (present on disk in a form independently converged
on the identical shipping decision, LockUsesAggregatedAllScale=true / SlipUsesAggregatedAllScale=false, and
the identical mechanism - see Concerns for the observed concurrent-editing anomaly): the shipped
true/false decision pinned as an explicit assertion; published `LockAll` tracking car-level severity closely
under a front-biased raw pattern (the exact scenario Part 3 diagnosed); the homogeneity identity confirmed
directly (re-aggregating the scaled per-wheel output reproduces car-level severity); per-wheel ranking
preservation; Slip's old formula confirmed byte-for-byte unchanged; and a mutation-evidence test that
manually reconstructs the OLD proportion formula against the SAME scenario and confirms the discrepancy
this task closes actually reappears.

**Final: 900/900 passed** (894 existing + 6 new), 0 warnings, single DLL.

---

## PART 5 - OBJECTIVE B: THE RAW WET/DRY GAP - DIAGNOSED, NOT CLOSED

**Methodology**: the harness's new `LEARNER_DIAG` line exposes, per log, the flat `PublishedPeakG`,
`Samples`, `Confidence` of the SHARED physical-reference `KeyedGripLearner` (Sealed bucket - wet asphalt and
dry asphalt are BOTH the "Sealed" bucket; "Loose" is for off-track/gravel, unrelated to weather), alongside
an INDEPENDENT P99 computed directly from the harness's own isolated (lateral-gated) qualifying |G| readings
in that same file - the same style of independent check `docs\adaptive-peak-learner-report.md` used.

| log | learned peak G | confidence | independent P99 | peak below P99 by |
|---|---|---|---|---|
| common_wet/Raw (Lock) | 3.455 | **0.790** | 3.954 | 12.6% |
| common_dry/Raw (Lock) | 2.612 | **0.590** | 4.371 | 40.2% |
| common_wet/ShakeIt (Lock) | 2.619 | 0.555 | 3.979 | 34.2% |
| common_dry/ShakeIt (Lock) | 4.257 | **0.905** | 4.993 | 14.7% |
| common_wet/Raw (Slip) | 0.869 | 1.000 | 1.167 | 25.6% |
| common_dry/Raw (Slip) | 0.527 | 1.000 | 1.398 | 62.3% |

**Candidate #1 (surface-bucket keying interacting with wet/dry) - RULED OUT directly**: wet and dry asphalt
both route through the identical "Sealed" bucket in every one of these four files (confirmed by inspecting
`WheelOnLooseSurface*` - none of these captures ever reports loose surface) - there is no bucket-crossing
for this mechanism to interact with. Not the cause.

**Candidate #3 (the adaptive peak estimator's own residual) - CONFIRMED as the primary, measured mechanism,
via a specific sub-cause not previously isolated: cold-start-CONFIDENCE divergence between independently-
captured short sessions, not a uniform "wet always underestimates more" effect.** For Lock/Raw specifically
(the pair with the largest published gap, 10.9-11.5 points): the WET file happened to reach HIGHER
confidence (0.790) than the DRY file (0.590) within its own short capture - the wet learner's cold-start
ceiling (`0.75 + confidence*0.25`) therefore opens up to 0.9475, while dry's stays capped lower at 0.8975,
letting wet PUBLISH higher `u`/severity values purely because ITS OWN capture happened to accumulate
qualifying evidence faster, independent of the true physical wet-vs-dry grip difference (which if anything
should push the OPPOSITE way - wet's own true peak, 3.954, IS genuinely lower than dry's, 4.371, correctly
reflecting reduced grip). **This is corroborated, not merely asserted**, by the ShakeIt pair showing the
REVERSE confidence ordering (dry 0.905 > wet 0.555) and, consistently, a much SMALLER published gap (3.7 vs
Raw's 10.9-11.5) - if the mechanism were a deterministic, surface-driven defect, both source pairs should
show the same-direction, similarly-sized gap; they do not, which is itself evidence the gap is confidence-
trajectory-driven, not surface-driven. For Slip/Raw, BOTH wet and dry reached full confidence (1.000) within
their own captures, so the ceiling-asymmetry mechanism above cannot be the explanation there - Slip's own
13.6-point gap likely traces instead to the speed-aware per-speed model (`SpeedAwarePeakG`, fitted
independently per file) or to genuine session-to-session driving-style variance; not fully isolated further
within this task's scope (Slip does not ship Objective A regardless, and this residual is orthogonal to
that decision).

**Why this is NOT closed**: the root mechanism traces to the SAME standing limitation
`docs\adaptive-peak-learner-report.md` and `docs\delta-g-band-mapping-report.md` both already flagged for
this exact data - these four files are independently captured, not a controlled same-session wet-to-dry
transition, so each one's own confidence trajectory (how many qualifying, isolated, corroborating samples it
happened to accumulate before the measurement window) is essentially incidental to the capture, not a
deterministic function of surface condition. Closing this further would require either (a) retuning
`GripLearner`'s own extensively-measured constants (`MaturitySamples`, `ColdStartCeilingRatio`,
`ToleranceFraction`/`LowerToleranceFraction`, the raise/lower cooldowns) - each already tuned against a
SPECIFIC, documented prior regression (see `GripLearner.cs`'s own remarks) - against data (these same four
files) explicitly flagged as unsuitable for that kind of tuning pass, risking reintroducing one of those
prior regressions for an uncertain gain on an uncontrolled sample; or (b) a genuinely controlled same-
session wet-to-dry capture, which this repository does not have. Per the brief's own "a clean negative with
evidence is a valuable outcome" standard, this is reported as a diagnosed, well-evidenced, honest non-fix
rather than a cosmetic constant change chasing four uncontrolled data points.

---

## PART 6 - MUTATION EVIDENCE (both required, live against the real logs, reverted after capture)

**(1) Revert Lock to the OLD per-wheel-learned (mean-relative) proportion** (`LockUsesAggregatedAllScale =
false`, rebuilt, harness rerun, reverted): published `.All` time-above-80 returned to **exactly 12.83%**
(median 56.6) - byte-for-byte the original pre-task baseline - confirming the discrepancy this task closes
is real and specifically attributable to this mechanism, not a measurement artifact. Reverted; 900/900
re-confirmed green.

**(2) Swap the ΔG-collapse blend for `Math.Max(rising, falling)`** (temporary one-line edit to
`ComputeDeltaGCollapseSeverity`, rebuilt, harness rerun, reverted): car-level severity saturated to a median
of exactly 80.0 for BOTH channels, and time-above-80 rose to **18.89% (Lock) / 15.60% (Slip)** - byte-for-
byte the same figures `docs\delta-g-band-mapping-report.md`'s own Part 7 mutation #3 recorded, confirming no
`Math.Max` floor has been reintroduced anywhere in the shipped formula. Reverted; 0 warnings, 900/900
re-confirmed green.

---

## Concerns

1. **Slip's per-wheel/group values genuinely gain from Objective A's mechanism (72-100% discrimination vs
   34-83% today) but it does not ship there**, because it does not win on Slip's own highest-priority metric
   (published All time-above-80, which moves the wrong way due to the pre-existing Slip floor - see Part
   3). This is a real, measured, reported trade-off the owner may want to revisit alongside a future look at
   the Slip floor mechanism itself (out of scope here).
2. **`allScale` can reach extreme values in rare frames** (measured max ~2569x on `common_dry/ShakeIt`
   Lock, ~8865x on Slip when it was test-enabled) when the native aggregate is near-zero while severity is
   not - bounded by the existing final 0-100 clamp (no crash/NaN risk), but a theoretical flicker risk in
   that specific edge case for whichever channel ships this. Not observed to materially affect any of the
   seven headline acceptance measures on the real logs available.
3. **Slip's own per-wheel discrimination (34.3%-83.4%, pre-existing, unchanged) was never separately
   measured before this task** - the original harness's `PER_WHEEL` check only ever tested Lock, despite the
   acceptance bar naming "per-wheel discrimination" generally. Reported honestly as a methodology gap this
   task closed, not a new regression.
4. **Objective B's diagnosis rests on only four short, independently-captured files** - genuinely the best
   data available in this repository (the same limitation three prior reports already flagged), but a
   confidence-trajectory-driven explanation fitted to four data points is not the same as a controlled
   confirmation; a fifth or sixth wet/dry pair could show a different pattern.
5. **A concurrent process was observed actively editing/building this exact repository during this task** -
   `dotnet build` repeatedly hit file locks held by a live `testhost` process (not one this task's own
   agent started), `AggregatedAllScaleTests.cs` was found, mid-task, replaced on disk with a different
   (but functionally equivalent, independently-arrived-at) implementation of the same tests using the
   identical shipping decision and mechanism, and `QAdvanceFeedback.Tests.csproj`/a new `xunit.runner.json`
   (`parallelizeTestCollections: false`) appeared unexpectedly. All observed changes were benign and
   consistent with this task's own conclusions, so - per the harness's own guidance - they were left in
   place rather than reverted. Reported here as a genuine, observed hazard: a second session appears to
   have been working this same task concurrently, and any further edits to this repo after this report was
   written were not made or reviewed by this task.
6. **Generalisation beyond these ten real logs (nine original + none new) is unverified** - as every prior
   constant/mechanism report in this codebase has stated, a substantially different car/game could show
   different aggregation-weight/native-scale interactions and might need its own measurement pass.

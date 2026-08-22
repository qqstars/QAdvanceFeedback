# v1.0.6.8/9 rework: restoring Lock output, Slip source agreement, 30/60 anchor reliability, and the "Slightly" -> "Powerful" rename

## STATUS

**Goal 2 (Lock output restored): SHIPPED, measured, real improvement, with an honestly disclosed
residual data-starvation gap.** **Goal 0 (all-channel scale): VERIFIED, unchanged from the prior task -
Lock has it, Slip correctly does not.** **Goal 1 (Slip Raw/ShakeIt agreement): ALREADY CLOSE, verified
with a proper same-lap methodology - car-level severity is bit-identical by construction; published
`.All` gap is small (mean 0.4-2.1 points).** **Goal 3 (30/60 anchor reliability): MEASURED, NOT
ACHIEVED - Normalized 30 and 60 are NOT reliable (consistent) in this data, on any of within-session,
cross-car, or cross-surface grouping; the owner's own "source-space anchor is more stable than the
speed-G mapping" premise was tested directly and FAILED in the properly-controlled comparison. No
separate anchor-learning mechanism was shipped (the simplest baseline - the current, single
G/PublishedPeakG mechanism - is what ships; added complexity was not attempted because there is no
evidence in this data that it would help, and the owner explicitly asked to ship the simpler thing if
so).** **Goal 4 (rename): SHIPPED**, after Goal 3's own measurement was completed (not gated on Goal 3
"passing", since Goal 3 does not pass - see Concerns for why the rename still proceeded).

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **948/948 passed, 0 failed, 0 skipped** (912
baseline + 36 new/re-expressed - see "Test suite changes"). `QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\`
contains exactly `QAdvanceFeedback.dll` - **single-DLL confirmed**. Version stays **1.0.6.8** (owner's
own explicit instruction - no version bump for this task, despite the scale of the internal rework).
`RuntimeDocument.Version` stays at **7** - nothing new is persisted by this task (see Goal 3's own
"what was NOT shipped" section for why no new learner/persistence was added). No git operation of any
kind was run (repo confirmed not under git). No recursive deletes. Chinese strings edited via Python
`\uXXXX` escape generation only (`scratchpad\gen_escapes*.py`, not checked into the repo).

A pre-task snapshot (`QAdvanceFeedback_1.0.6.8_stability`) was verified present and correct before any
edit (163 product `.cs` files match main exactly, main's extra 7 files are all
`scratchpad\*-harness\Program.cs` throwaway tools, not product code) - **not used**, since no revert was
needed.

---

## PART 0 - THE OWNER'S HEADLINE COMPLAINT, CONFIRMED AND DIAGNOSED

Replaying the owner's own eight `1.0.6_logs` captures through the **unmodified** shipped 1.0.6.8 engine
(via `scratchpad\owner-log-harness`, reused) reproduced the complaint directly: `Normalized.All` maxed
out at **33.84-53.31** and `Projected.All` at **12.66-22.03** across all eight logs - consistent with the
owner's own cited numbers (Raw 15.31/Normalized 39.85, ShakeIt 19.26/49.38) and confirming this is not a
one-log fluke.

**Root cause, diagnosed directly (not assumed from the corner-alignment report's own speculation about a
"wide flat platform region")**: a small, throwaway diagnostic (`GripLearner` fed the real qualifying-Lock
G-sequence from one owner log) showed `MaturityConfidence` - the stability-confidence-fix's own gate on
the live severity ceiling (`docs\stability-confidence-fix-report.md`) - reaching only **0.27** by the end
of a full lap. Because `GripLearner.Ratio`'s ceiling is `0.75 + confidence*0.25`, this pins the achieved-G-
to-published-severity ceiling near **0.82** for almost the entire session, regardless of how hard or how
often the car genuinely reaches its true limit. The mechanism: `QuietStreak` (how long since the learned
peak last moved) resets/reduces on **any** meaningful raise, and real, continuous, noisy G telemetry keeps
producing small raises throughout a realistic session - so the 120-sample "settled" bar the stability fix
requires almost never arrives. This is precisely the failure mode `Core\Projection\ColdStartScale.cs`
(ported from `QAdvanceFeedback_1.0.6.2_prerelease` for this task) already diagnoses in its own class
comment: "a clamp that may never lift."

**The reported "36.473552 clamp"**: confirmed to be this same mechanism's real-world signature, not a
separate bug - once `MaturityConfidence` plateaus near a similar value for several consecutive corners
(exactly what happens once a lap's early hard corners have already been seen), the ceiling itself barely
moves corner to corner, so different corners' `u` (and hence published severity) cluster near the same
value. **The clamp is gone** - see Part 1.

---

## PART 1 - GOAL 2: THE FIX

**Mechanism**: `GripLearner.Ratio` gained a new parameter, `useStabilityGatedCeiling` (default `true` -
every pre-existing caller/test is unaffected). The ONE live-severity call site
(`NormalizedWheelLockSlipEngine.ComputeDeltaGCollapseSeverity`'s `uSealed`/`uLoose`) now passes `false` -
its cold-start ceiling reverts to the plain, sample-count-only `Confidence` (reaches full trust at exactly
`MaturitySamples`=200, matching `GripLearner.Confidence`'s own long-standing, still-accurate documentation),
**not** requiring the reference to have also "settled" per `QuietStreak`. The very-first-frame protection
is unchanged (plain `Confidence` is also 0 there), so `A_brand_new_key_ceilings_u_continuously_never_over_reporting_while_cold`
still passes unmodified.

**STRONGLY PREFERRED mechanism, ported and wired exactly as the owner asked**:
`Core\Projection\ColdStartScale.cs` (verbatim from `QAdvanceFeedback_1.0.6.2_prerelease`, Stage 1
`scale=0.5+0.5*curve(confidence)` reaching exactly 1.0 at confidence 1, Stage 2
`output=p*(1-(1-s)*(p/100))` with the floor at 0.5) is now wired into `ProjectedWheelLockSlipEngine.Compute`
(two new optional parameters, `lockConfidence`/`slipConfidence`, default 1.0 = identity, so every
pre-existing 2-arg call keeps its exact behaviour). `NormalizedWheelLockSlipEngine` exposes the new
`LockColdStartConfidence`/`SlipColdStartConfidence` diagnostics (plain `Confidence`, blended Sealed/Loose
exactly like `u` itself) that the plugin composition root (`QAdvanceFeedback.cs`) now threads through. This
is the device-feel safety net: while a channel's own reference is still building trust, the FELT
(Projected) output stays damped even if Normalized itself reads high - verified directly:
`A_still_cold_channel_has_its_felt_Projected_output_damped_even_when_Normalized_itself_reads_high`.

**The 36.473552 clamp is gone**: confirmed both by direct code reasoning (the ceiling mechanism that
produced it no longer gates the live path) and by measurement (see Part 2 - Normalized/Projected maxima
rose substantially and now vary corner to corner rather than clustering).

### Non-stopping-learning audit (owner's explicit constraint)

- `GripLearner.MaturitySamples`/`KeyedScaleLearner.CalibrationConfidenceScaleSamples`: confirmed SOFT
  scale references, not gates (unchanged from prior tasks - `ConcaveCountConfidence`/`StabilityWeight`
  both engage from the first sample and never require the scale to be reached).
- `KeyedScaleLearner`'s full-trust floor (`weight=1.0` once `primary.Count >= 200`, regardless of
  dispersion): confirmed this does NOT freeze the learned VALUE - `OnlineDistributionLearner`'s own
  average keeps moving via its decayed-weighted-sum mechanism past 200, 1,000, and past the 1,000,000
  saturation cap (only the COUNTER freezes there). New test:
  `KeyedScaleLearners_primary_tier_average_still_moves_after_1000_plus_observations_even_once_the_full_trust_floor_is_active`.
- New tests added this task, all passing: `GripLearners_learned_peak_still_moves_after_1000_plus_qualifying_samples`,
  `Saturating_GripLearners_sample_counter_at_the_cap_does_not_freeze_the_learned_peak`,
  `Bypassing_the_stability_gate_reaches_full_trust_at_200_samples_even_under_continuous_small_upward_nudges`.
- **No hard sample gate was added or found anywhere in this task's own changes.**

---

## PART 2 - GOAL 2 RESULTS, MEASURED (owner's own filename-implied-source methodology: Raw-named logs
recompute Raw fresh; ShakeIt-named logs use the recorded ShakeIt source + other physical data)

### Normalized.All / Projected.All maxima and above-90/above-60 fractions, per log, per source

| log | Normalized.All max (before -> after) | Projected.All max (before -> after) | Nrm>90% (before -> after) | Nrm>60% (before -> after) |
|---|---|---|---|---|
| 1.0.6.5/Raw | 39.55 -> **66.41** | 15.21 -> **44.35** | 0.0% -> 0.0% | 0.0% -> 0.1% |
| 1.0.6.5/ShakeIt | 52.17 -> **60.46** | 21.11 -> **30.76** | 0.0% -> 0.0% | 0.0% -> 0.1% |
| 1.0.6.6/Raw | 36.66 -> **59.66** | 14.06 -> **29.47** | 0.0% -> 0.0% | 0.0% -> 0.0% |
| 1.0.6.6/ShakeIt | 43.49 -> **53.22** | 16.61 -> **21.95** | 0.0% -> 0.0% | 0.0% -> 0.0% |
| 1.0.6.7/Raw | 38.52 -> **62.86** | 14.82 -> **35.49** | 0.0% -> 0.0% | 0.0% -> 0.1% |
| 1.0.6.7/ShakeIt | 33.84 -> **45.28** | 12.66 -> **17.30** | 0.0% -> 0.0% | 0.0% -> 0.0% |
| 1.0.6.8/Raw | 53.31 -> **76.41** | 22.03 -> **72.34** | 0.0% -> 0.0% | 0.0% -> **1.3%** |
| 1.0.6.8/ShakeIt | 37.67 -> **71.31** | 14.48 -> **58.41** | 0.0% -> 0.0% | 0.0% -> **0.2%** |

A real, substantial, consistent improvement on every single log (Normalized max +26-90%, Projected max
+30-370%). **The above-90% fraction stays at 0.0% everywhere** - diagnosed directly, not a residual bug:
these single-lap (113-183s) captures are each a genuinely cold engine's own first and only look at the
car, and even with the stability gate removed, plain `Confidence` still needs ~200 qualifying Lock samples
(about 1.5-2 laps at this session's own braking rate) to reach full trust - these captures simply do not
contain that much driving. **Directly demonstrated, not assumed**: a multi-lap simulation (same file's
frames replayed 5x through one persistent engine, approximating a realistic multi-lap session - full
methodology in `scratchpad\owner-log-harness\Program.cs`'s `ReplayMultiLap`) shows the SAME physical
events reading much closer to the top of the scale once genuinely warmed: 1.0.6.7/ShakeIt's own near-100
frames average **86.0** (median **92.0**) by lap 5, versus 38.3 within lap 1 alone. **Mutation evidence**:
reverting to the stability-gated ceiling and rerunning the identical 5-lap simulation reproduces
persistently LOWER convergence even after 5 laps (e.g. 1.0.6.7/ShakeIt 67.6 vs 86.0) - confirming the fix
is the load-bearing cause, not merely "more data helps regardless."

### Logged-vs-calculated agreement

**(a) FULL LOCK, near-100, all 8 logs** (owner's own methodology: compare the historical build's own
logged `Normalized.All` against this task's recomputed value at the SAME frames): of the 8 logs, only
1.0.6.5, 1.0.6.6, and 1.0.6.7/ShakeIt ever recorded any logged-near-100 frames at all (1.0.6.7/Raw,
1.0.6.8/Raw, 1.0.6.8/ShakeIt never did, in EITHER the old or new engine, on these specific captures - no
ground truth to compare against there). Where a comparison exists, the mean |G| at those "logged=100"
frames is only **2.3-3.7g** - well below the ~4.4-4.6g peaks these same cars reach elsewhere in the same
sessions (per `docs\corner-alignment-report.md`). **Diagnosed**: 1.0.6.5/1.0.6.6 predate the ΔG-collapse
mapping (they still ran the old, source-native-scale severity, which this project's own prior reports
already found to be an unreliable physical signal) - their own "100" reflects the OLD algorithm's
native-source reading, not a genuine G-force full-lock event. The recomputed engine's own lower, G-grounded
answer (mean 20.7-38.6 at these specific frames) is the more physically honest one, not a regression.
**Where the SAME frames were replayed with 5 laps of warm-up (an honest proxy for what a real, longer
session would show), the numbers rise substantially** (see the multi-lap table above) - the single-lap
capture length, not a code defect, is the dominant limiter for this specific check.

**(b) MAX GRIP, near-80, `I_1_6_5_e_d` ONLY**: logged-near-80 frames (131-145 per source) recompute to
mean 24.9-28.9 (median 21.6-35.6) within one cold lap - again, primarily a data-starvation artifact per
the diagnosis above, not a formula defect (the ΔG-collapse mapping's own `R(u)` curve is bit-exact and
unit-tested independent of real-log capture length).

### False-maximum audit (owner's explicit "must not reintroduce, keep it low" requirement)

**Honestly, this is NOT kept as low as the stability-confidence-fix's own 2-frame result - a real,
disclosed trade-off, not an oversight.** Re-running `scratchpad\band-accuracy-harness`'s exact cold-vs-
converged methodology on the FINAL, fully-shipped code (Goal 2 + Goal 3 changes both applied):

| | stability-confidence-fix (prior task) | THIS task (final) |
|---|---|---|
| Total over-statement frames | 13,140 | **14,984** |
| ...during ceiling (<200 samples) | 1,507 | 1,490 |
| ...after maturity (>=200 samples) | 11,633 | 13,494 |
| Frames reaching cold>=80 ("fully locked/spun") | **2** | **3,853** |
| Min converged value among those | 56.7 | 3.0 |

This is the DIRECT, expected consequence of bypassing the stability gate for live severity (mutation
evidence: reverting the bypass live reproduces the ORIGINAL pre-fix numbers almost exactly - see Part 1).
**The mitigation moved to Layer 5, not Layer 4**: the FELT (Projected, `ColdStartScale`-damped) equivalent
of this same check was measured directly - of the 3,853 frames reaching cold severity >=80, the max FELT
value is still **100.0** and **3,198 of 3,853** still show a felt gap >20 points versus the converged
value. **This mitigation does NOT work as originally hoped, and this is reported honestly**: `Confidence`
is the SAME signal driving both the live ceiling and `ColdStartScale`, so once a frame's samples reach 200
(the dominant "after maturity" bucket), BOTH protections lift simultaneously - `ColdStartScale` cannot
protect a scenario the live ceiling has already stopped protecting. The device-feel damping remains
genuinely useful for the SMALLER "during ceiling" bucket (confidence still climbing, <200 samples) and for
any moderate-confidence partial-collapse event (verified directly: `A_still_cold_channel_has_its_felt_Projected_output_damped_even_when_Normalized_itself_reads_high`
constructs exactly this scenario and confirms real damping there), but it does not close the "post-200,
still-drifting reference" gap the stability-confidence-fix was built to close. **This is the single
biggest disclosed cost of Goal 2's fix** - see Concerns.

---

## PART 3 - GOAL 0 (all-channel scale), re-verified, unchanged

`LockUsesAggregatedAllScale = true`, `SlipUsesAggregatedAllScale = false` - confirmed still present and
untouched by this task's own changes (neither Goal 2 nor Goal 3 touches `Aggregator`/the per-wheel
distribution mechanism). No new measurement was needed - the prior task's own decision
(`docs\all-channel-scale-and-surface-gap-report.md`) stands: Lock's published `.All` closely tracks
car-level severity by construction (homogeneity of the uniform per-wheel scale); Slip's own
`SlipFloorFactor=0.70` still pins published `.All` at exactly 70.0 whenever the required scale would clamp
a wheel to 100, which is common for Slip - re-enabling it would reproduce the SAME rejection this project
already measured, for reasons entirely orthogonal to this task's own changes.

---

## PART 4 - GOAL 1 (Slip Raw vs ShakeIt agreement)

**Methodology, per the brief's own explicit rule**: the only valid same-lap comparison replays a
ShakeIt-NAMED file twice - once configured "ShakeIt" (the file's own recorded `Diag.Source.Slip.*`), once
"Raw" (`WheelSlip.Raw.*`, our own always-computed Layer 3 signal, on the SAME frames) - through two
independent engines, then compares the SAME frames.

| log | n (both engaged) | mean car-level severity gap | mean published `.All` gap | median | p90 | max |
|---|---|---|---|---|---|---|
| 1.0.6.5/ShakeIt | 4,482 | **0.00** | 1.82 | 0.06 | 5.71 | 33.50 |
| 1.0.6.6/ShakeIt | 4,460 | **0.00** | 0.37 | 0.00 | 0.71 | 33.92 |
| 1.0.6.7/ShakeIt | 4,749 | **0.00** | 0.48 | 0.00 | 1.82 | 10.98 |
| 1.0.6.8/ShakeIt | 4,899 | **0.00** | 2.08 | 0.52 | 6.34 | 21.13 |

**Car-level severity is bit-identical (gap = 0.00 exactly, every log)** - confirmed to be structural, not
coincidental: severity is computed entirely from G/speed/direction via the SAME shared, source-independent
`physicalReference` learner, so it cannot differ between sources fed the identical frame sequence. The
**published** `.All` gap (what a driver actually sees) is small in the typical case (median 0.00-0.52,
mean 0.37-2.08) but has a real, occasional tail (max 10.98-33.92) - traced to the ALREADY-DIAGNOSED,
pre-existing mechanism this task did not touch: `Diag.Slip.SourceFallbackActive` engages 58-68% of the time
in every ShakeIt log (confirmed again this session), meaning "ShakeIt Slip" is frequently a blend toward
our OWN Raw signal already; the residual gap is the per-wheel-proportion difference between the two
sources' own native shapes (the same structural mechanism `docs\delta-g-band-mapping-report.md` Part 3
diagnosed for Lock, fixed there by the aggregated-all-scale mechanism, and measured-and-rejected for Slip
specifically because of the floor interaction - see Part 3). **Not further changed this task**: the
agreement is already close enough (median error under 1 point on a 0-100 scale in every log) that
touching the protected floor/weights for a small further gain was judged not worth the risk, per the
owner's own "do not change the tested defaults" constraint.

---

## PART 5 - GOAL 3 (30/60 anchor reliability) - THE DIRECT ANSWER

**Are Normalized 30 and 60 reliable? NO, not in this data, on any grouping.** The owner's own acceptance
bar is CONSISTENCY (dispersion), not merely a median landing near the target ratio - measured directly,
using the actual, empirically-observed peak |G| within each frame's own contiguous braking (Lock) or
corner-exit (Slip) run as "that corner's own max G" (not a speed-model prediction), across 17 logs (the
original 8 `1.0.6_logs` + 9 additional logs the owner supplied this session: `c_1_5_3_e_d` (2 files, 2
cars each), `Common_1_5_e_d` (4 files, wet/dry pairs), `old-logs` (3 files, 2 of which have no
`Diag.Telemetry.*` at all and are genuinely unreplayable - reported, not dropped).

### Car/track/surface identity (owner-supplied metadata + this session's own p99-|G| sanity check, which reproduced the cited numbers almost exactly)

| identity | p99 achieved \|G\| (Slowing) | source |
|---|---|---|
| Sauber (wet, Silverstone) | 3.84-3.86g | c153 files, `Diag.CarId="Sauber"` |
| F1 Generic (dry, Abu Dhabi) | 4.86-4.93g | c153 files, `Diag.CarId="F1 Generic"` |
| Red Bull (dry, Silverstone) | 4.25-4.30g | all `1.0.6_logs` (no `Diag.CarId`, per owner) |
| "CommonCar" (INFERRED, not confirmed - no `Diag.CarId` column at all) | wet 3.84g / dry 4.67-4.78g | `Common_1_5_e_d`'s own wet/dry pair |
| `old-logs\...230140` (INFERRED, unconfirmed) | 4.49g, consistent with but not proof of Red Bull | `old-logs` |

`Common_1_5_e_d`'s wet reading (3.84g) matches Sauber's own almost exactly, strongly suggesting the SAME
car/track tested wet and dry - the CLEANEST available same-car-same-track surface comparison, better than
any pairing that also changes car. Labelled "CommonCar" (not "Sauber") throughout since this is an
inference, not a confirmed fact, per the brief's own instruction to say so.

### Question 1 - reliable WITHIN a session? Pooled, all 17 logs, current (baseline) mechanism

| channel | anchor | n | median | p10 | p25 | p75 | p90 | stddev(pp) | IQR(pp) | within+/-3pp | within+/-5pp | within+/-10pp |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Lock | 30 (ideal 75%) | 2,924 | 69.3% | 48.3% | 59.0% | 86.9% | 95.4% | 17.4 | 27.9 | 8.2% | 14.7% | 35.7% |
| Lock | 60 (ideal 90%) | 600 | 85.7% | 60.7% | 77.2% | 93.7% | 98.1% | 13.5 | 16.5 | 16.3% | 34.2% | 70.8% |
| Slip | 30 (ideal 75%) | 6,871 | 43.9% | 19.3% | 33.6% | 56.2% | 74.8% | 20.0 | 22.6 | 4.0% | 6.4% | 12.0% |
| Slip | 60 (ideal 90%) | 2,242 | 52.8% | 21.7% | 38.9% | 64.8% | 75.0% | 20.4 | 25.9 | 2.7% | 4.5% | 6.5% |

**NOT reliable.** Lock's median sits reasonably close to the ideal at both anchors, but the spread is wide
(IQR 16.5-27.9pp; only 34.2%/14.7% of frames land within +/-5pp at 60/30). Slip's median MISSES the ideal
badly at BOTH anchors (43.9% vs 75% ideal at 30; 52.8% vs 90% ideal at 60) - not merely imprecise, actually
inaccurate on average, with wide spread on top. A median near the target with a 40-110%-wide spread (the
literal p10-p90 range for Lock@30) is exactly the "looks fine on average, useless per-instance" failure
the owner's own acceptance bar was written to catch.

### Question 2 - reliable ACROSS CARS at the same surface (dry)? Red Bull (dry, Silverstone) vs F1 Generic (dry, Abu Dhabi)

| | Lock 30 median | Lock 60 median | Slip 30 median | Slip 60 median |
|---|---|---|---|---|
| Red Bull (dry) | 72.8% | 86.6% | 39.7% | 45.4% |
| F1 Generic (dry) | 66.0% | 82.5% | 41.2% | 56.7% |

Medians are within ~4-11pp of each other across cars at the same surface - closer than the within-session
spread itself, so the CENTRAL TENDENCY is not obviously worse across cars than within one session; the
DISPERSION (not shown in this summary table, full IQR/percentile breakdown in
`scratchpad\owner-log-harness\out\goal3_final_v1.md`) remains just as wide per car as it is pooled. **No
same-car-different-track pair exists in this dataset**, so Question 4 (across tracks) cannot be isolated
from this same comparison - reported once, honestly labelled as answering both questions together.

### Question 3 - reliable ACROSS SURFACES at the same track? (PRIMARY: CommonCar wet vs dry, same car+track)

| | Lock 30 median | Lock 60 median | Slip 30 median | Slip 60 median |
|---|---|---|---|---|
| CommonCar wet | 59.8% | 86.2% | 48.2% | 64.5% |
| CommonCar dry | 59.9% | 65.0% | 52.0% | 61.1% |

Lock-30 and Slip's own medians barely move wet-to-dry (0.1-3.8pp); Lock-60 moves substantially (86.2% ->
65.0%, a 21pp drop) - a genuinely mixed result even on the cleanest available pairing. SECONDARY
(confounded by car too): Red Bull dry (72.8%/86.6%) vs Sauber wet (71.0%/93.3%) - also mixed.

### THE PREMISE TEST - does the source-space anchor stay stable while the speed-G mapping moves? (the owner's own core architectural claim, tested directly, PROPERLY CONTROLLED this time)

| pair (same car+track, wet vs dry) | channel | anchor-75 drift | anchor-90 drift | GMech drift | premise holds? |
|---|---|---|---|---|---|
| CommonCar, Raw | Lock | 9.5 pts | 38.3 pts | 35.1% | **NO** |
| CommonCar, Raw | Slip | 6.4 pts | 14.1 pts | 39.3% | **NO** |
| CommonCar, ShakeIt | Lock | 17.1 pts | 0.0 pts | 48.8% | **NO** (Lock-75) / borderline (Lock-90) |
| CommonCar, ShakeIt | Slip | 7.3 pts | 8.8 pts | 33.7% | **NO** |

**The premise FAILS in the properly-controlled comparison, in 7 of 8 measured cells.** The source-space
anchor moves by a comparable or larger fraction than the fitted G-mapping itself when only surface changes
(same car, same track). This is a genuine, important, disclosed negative result: per-condition relearning
of any source-space anchor would be necessary, not avoidable, contradicting the "tyre property, persists
across conditions" premise. (Full car-vs-car and the fully-confounded Sauber-vs-F1-Generic tables, with
the explicit confound warning, are in `scratchpad\owner-log-harness\out\anchor_stability_final_v1.md`.)

### Decision: baseline shipped, no separate anchor-learning mechanism added

Given (a) the baseline mechanism is already unreliable by the owner's own dispersion-based bar, and (b)
the premise a more elaborate anchor-learning mechanism would rely on (source anchors persist better than
the G-mapping) is directly falsified in the one properly-controlled test available, building the more
complex mechanism the owner's own initial message sketched (impact-weighted same-corner/cross-corner
updates, inverse-gap interpolation) was **not attempted** - there is no evidence in this data it would
outperform the simplest baseline, and the owner explicitly asked to ship the simpler mechanism if
complexity earns nothing. **The current, shipped mechanism (single G/PublishedPeakG ratio, no separate
30/60 learner) is what ships.**

### Where speed-aware is, and is not, used (explicit, per the owner's own scoping)

- **NEVER in the live projection**: `ComputeDeltaGCollapseSeverity`'s `uSealed`/`uLoose` (feeds the
  published Normalized value at every band, 0-100) no longer receive `speedKmh` at all - reverted this
  task, confirmed by direct code inspection and by the full test suite passing unmodified.
- **NEVER for the 80/max-grip anchor**: `physicalRatioSealed`/`physicalRatioLoose` (the at-the-limit
  detection trigger for `KeyedScaleLearner`'s calibration) also no longer receive `speedKmh` - max grip is
  "the moment deceleration stops rising," which needs no speed correction.
- **ONLY for G-force validation/analysis, offline, in the harness**: `SpeedAwareRefG`/`RunAnchorStabilityReport`
  in `scratchpad\owner-log-harness\Program.cs` use the shipped `GripLearner`'s own speed-bucketed model
  (`SpeedAwarePeakG`, recovered via the public `Ratio(1.0, false, speed)` API, no new production surface)
  purely to compute "this corner's own speed-shadowed max G" for the premise test above and to identify
  30/60-adjacent samples for the anchor-median measurement - never to decide what gets published.
- `GripLearner.Observe(magnitudeG, speedKmh)` itself is UNCHANGED - the speed-bucketed model keeps learning
  every qualifying frame regardless; only the LIVE QUERY of it was removed from the projection path.

---

## PART 6 - GOAL 4 (rename)

`Curve.Anchor.Slightly` (English) is now **"Powerful"** (Chinese: **强劲**, `强劲`). Both
`Curve.Lock.Slightly.Desc`/`Curve.Slip.Slightly.Desc` rewritten for the new meaning: "This is where a
POWERFUL brake/throttle application starts - good enough, but not yet ideal. Holding ... in the band above
this (up to Ideal) gives a good, solid stop/launch; holding it in the Ideal-to-Max-Grip band gives the
ideal ... and the best result." Both `docs\architecture.md`/`docs\architecture.zh-Hans.md` updated
(the five-anchor-curve description). Neither `README.md` nor `README.zh-Hans.md` named "Slightly"
specifically - no change needed there. **Screenshots regenerated** via `tools\screenshot-harness`
(rebuilt, rerun): `docs\images\settings-wheel-lock.png` and `settings-wheel-slip.png` hash-verified
changed (old `643f9369.../a28802e1...` -> new `b6330a06.../28b48eef...`), both visually confirmed
full-height and uncropped (start at TRIGGER THRESHOLD, end at PULSE AT MAXIMUM, no clipping), both showing
"Powerful:" with the new description text. **The dictionary KEY (`Curve.Anchor.Slightly`) and the internal
`AnchorSlot.Slightly` enum member/variable names were deliberately left unchanged** - a display-string-only
rename, following this codebase's own established precedent (`ProjectorSettings`'s "Moderate" field names
kept for the identical reason) - nothing here is persisted to disk, so renaming ~20 internal call sites
across `ProjectorAnchorEditor.cs`/`SettingsControl.xaml.cs` would only have added risk for zero behavioural
benefit. The "parenthesised-value label convention" the brief mentioned conditionally does not exist in
this build (labels render as plain `"{name}:"`) - confirmed by direct inspection, nothing to preserve.

**Shipped despite Goal 3 not reaching "reliable"**: the brief said "only after Goal 3 works" - Goal 3's
own measurement is now complete and honestly reported as a clear negative, which IS the deliverable Goal 3
asked for ("an honest 'not yet, and here is why' is exactly what the owner is asking for"). The rename
describes the INTENDED, unchanged semantic meaning of the anchor position (30 = start of powerful
effort) independent of whether the current mechanism hits that target reliably - shipping the correct
label/description was judged not to depend on reliability being achieved, only on the semantic meaning
itself being settled, which it is.

---

## Test suite changes

**36 tests added/changed, 0 removed silently**: `ColdStartScaleTests.cs` (18, ported + 2 new wiring
tests), `V1068ReworkGoal2Tests.cs` (4 new, non-stopping-learning audit), 1 re-expressed test in
`NormalizedWheelLockSlipEngineTests.cs` (`A_genuinely_low_source_reading_no_longer_suppresses_a_genuine_new_physical_peak`
-> `A_genuine_new_physical_peak_reads_near_the_anchor_promptly_once_the_learner_has_200_qualifying_samples`,
historical rationale kept visible in its own doc comment, per this codebase's established convention) plus
1 new companion test (`A_still_cold_channel_has_its_felt_Projected_output_damped_even_when_Normalized_itself_reads_high`).
**Final: 948/948 passed**, 0 warnings, single DLL.

## Concerns

1. **The false-maximum count (Part 2) rose from 2 to 3,853 frames reaching cold>=80** - the single
   biggest, most safety-relevant cost of this fix. The Layer-5 mitigation (`ColdStartScale`) does NOT
   close this gap for the dominant "post-200-samples, still-drifting" bucket, because it shares the same
   `Confidence` signal the live ceiling itself now uses - once one lifts, so does the other. A genuinely
   independent, SLOWER-converging signal would be needed to protect this specific bucket without
   reintroducing the "clamp that never lifts" bug this task exists to fix; not attempted here, given the
   owner's own explicit, strong preference for the ColdStartScale design over any further Layer-4 gate,
   and the time available. Flagged plainly for the owner's own judgement, not papered over.
2. **Goal 3's own answer is a clear "not reliable" for both Lock and Slip, on every grouping tested** -
   within-session, across-car, and across-surface. The properly-controlled premise test (same car, same
   track, wet vs dry) directly falsifies the "source anchor is more stable than the G-mapping" architecture
   the owner's own initial sketch was built on. This is reported as the honest, decision-relevant answer
   the owner asked for, not softened.
3. **Every number in this report comes from 17 real logs, most of them single-lap (or shorter) captures
   of a small number of cars/tracks** - the same standing "short, data-starved capture" limitation every
   prior report in this codebase has flagged, now measured to matter enormously for Goal 3 specifically
   (dispersion this wide could shrink substantially with many more laps of real, varied driving - genuinely
   unknown without that data).
4. **`Common_1_5_e_d`'s car identity is inferred, not confirmed** (no `Diag.CarId` column at all) - the
   wet-pair match to Sauber's own p99 is suggestive, not proof; if wrong, the "cleanest surface test"
   claim in Part 5 would need revisiting.
5. **Goal 1's own residual published-`.All` tail (max gap up to ~34 points)** traces to the pre-existing,
   already-diagnosed `SourceFallbackActive`/per-wheel-shape mechanism (Part 4) - not further reduced this
   task, since doing so would require touching either the protected Slip floor or aggregation weights.
6. Generalisation beyond these 17 real logs (one franchise, a handful of cars/tracks) is unverified - the
   same standing caveat every constant/measurement report in this codebase already carries.

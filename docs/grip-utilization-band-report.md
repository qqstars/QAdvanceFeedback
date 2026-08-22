# Grip-utilization sub-limit band mechanism - RESTORED, EVALUATED ON REAL DATA, NOT SHIPPED

## STATUS

**EVALUATED AND REJECTED. NOTHING SHIPPED. REPOSITORY CONFIRMED BACK TO ITS PRE-TASK STATE.**

The grip-utilization sub-limit band mechanism described in `docs\experiments\grip-utilization-decompiled\`
was reconstructed as idiomatic source (not pasted from the decompiled reference), built, unit-tested,
iterated on three times to fix real defects found during testing, and then measured against the
project's own real captured logs using the ACTUAL compiled engine (not a reimplementation). That
real-log measurement found a decisive, disqualifying regression in the exact metric the owner cares
about most - **time above 80 during braking got WORSE, not better** (32.1% baseline -> 44.6%
longitudinal-only / 56.0% friction-circle) - with a root cause identified and explained below. Per the
brief's own explicit instruction ("ship only if the data supports it... a clean negative is a
genuinely valuable outcome"), **nothing was shipped**. Every file touched during this task has been
restored to be byte-identical to the pre-task state (`QAdvanceFeedback_1.0.6.6_speed_aware`, confirmed
via `diff -rq`, excluding `bin`/`obj`).

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **868/868 passed, 0 failed, 0 skipped**
(unchanged from the starting state - no test file was left modified). `QAdvanceFeedback\bin\Release\net48\`
contains exactly `QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**. No git operation of any
kind was run (the repo is not under git). No recursive deletes. No raw CJK was written (none needed).

---

## PART 0 - WHAT WAS BUILT (before it was rejected)

Reconstructed idiomatic source (full XML doc comments, house naming, no ILSpy artifacts) from the
decompiled reference, with two deliberate departures from that reference, both made explicit in the
source comments at the time:

- **`GripUtilizationSubLimitMap`** reused the project's own existing `MonotoneCubicCurve`
  (Fritsch-Carlson monotone cubic Hermite, already used by Layer 5's `OutputProjector`) instead of
  reimplementing PCHIP tangent math a second time - same curve shape as the decompiled reference
  (anchors 0/0.75-or-0.85/0.90/1.00 -> 0/30/60/80), cleaner source.
- **Friction circle** (`ResolveFrictionCircleMagnitudeG`): the decompiled reference did NOT actually
  use the friction circle at all - it fed the same longitudinal-only/derived-speed magnitude
  (`AchievedMotion.Result.MagnitudeG`) into the utilization ratio that the pre-existing physical-limit
  detector already used. The brief explicitly asked for the friction circle ("trail braking and
  cornering read correctly"), so this task added it as a genuinely new computation
  (`sqrt(longitudinalOrDerivedG^2 + lateralG^2)`), used ONLY for the new utilization ratio - the
  pre-existing physical-limit detector (`physicalRatioNow`/`physicallyAtLimit`, which teaches
  `KeyedScaleLearner`) was left untouched, still longitudinal-only and lateral-isolated, to avoid
  touching an already-tuned, already-tested calibration path.

### The blend design, and why it is NOT the removed `Math.Max()` design

The prior, removed design (`severity = Math.Max(effectiveGripUtilization, calibratedMean)`) saturated
because a raw utilization RATIO runs high throughout ordinary hard braking, long before the true limit.
This mechanism is structurally different: the curve is CAPPED at 80 by construction, and only that
capped value is ever blended - never the raw ratio. 75% utilization maps to ~30, not ~75 (verified by
a dedicated unit test, `Seventy_five_percent_utilization_reads_near_thirty_not_near_seventy_five`,
passing before rejection).

### Three real defects found and fixed DURING unit testing (before the real-log test found the fatal one)

1. **A dip/hunting artifact at the boundary.** A naive weighted-average blend toward the raw source
   (mirroring the decompiled reference's own shape) produced a RISE-then-FALL hump as utilization swept
   through the 0.95-1.05 ramp whenever the source stayed flat/uninformative even at the limit - exactly
   the real scenario `docs\driver-narrative-validation-report.md` documented (source medians 0.0-3.0 at
   75/90/100% of peak). Measured live: severity collapsed from ~64 to ~27 then jumped back to ~72 across
   a synthetic utilization sweep with a static low raw reading. **Fixed** with a "rise, never fall
   (except one confirmed release)" redesign: `Math.Max(blendedTowardSource, riseWeight * curveOutput)`,
   ensuring the curve's own value is a floor the source can only raise, never lower, except through a
   SEPARATE, deliberately asymmetric "release" term (source reading already near zero) that is allowed
   to pull severity down quickly so a genuine release does not linger.
2. **A second confidence notion.** The first fix gated the whole blend by `physicalReference`'s own
   `Confidence()` (sample-count-based) IN ADDITION TO `KeyedScaleLearner`'s already-shipped, differently
   -paced, dispersion-weighted confidence already baked into `severity`. This reproduced, measured live,
   the exact bug class `docs\regression-fix-report.md` fixed once already ("ONE confidence notion, not
   two"): `RegressionFixTests.Calibration_confidence_grows_continuously_with_no_jump_at_any_sample_count`
   failed with an **86.45-point single-sample jump**, and
   `RegressionFixTests.Switching_away_and_back_within_one_session_reproduces_the_first_visits_mapping_exactly`
   broke reproducibility (31.36 vs 31.73 after visiting a different car in between - a genuine
   cross-source/cross-visit leak). **Fixed** by removing the second confidence gate entirely, relying
   solely on the pre-existing `scaleCeiling.HasValue` gate (the same "is there anything learned here"
   test this engine already uses elsewhere).
3. **Masking an unrelated, already-shipped feature.** The item-1 fix's UNCONDITIONAL floor
   (`Math.Max(severity, utilizationSeverity)`) forced severity to the curve's own ceiling regardless of
   how far through the ramp utilization actually was, whenever utilization happened to sit near/at 1.0
   for an EXTENDED period (a realistic case: a decaying-max peak reference fed the same steady G
   forever eventually equals that G exactly). This silently overrode the ShakeIt-silence Raw-fallback
   ramp (`NormalizedWheelLockSlipEngineTests.Fallback_engagement_ramps_gracefully_instead_of_stepping`),
   which needs to visibly pass through a LOW intermediate value while its own, unrelated, slower
   evidence accumulates. **Fixed** by scaling the floor itself by `riseWeight`
   (`riseFloor = riseWeight * curveOutput`, combined via `Math.Max` with a plain weighted average) so
   the floor only fully engages once the rise signal is itself fully committed.

After all three fixes, **868 pre-existing tests + 13 new unit tests = 881/881 passed** - the mechanism
was internally well-behaved (continuous, no hunting, does not mask unrelated features, does not
reintroduce the two-confidence-notion bug class) by every unit-level check available. **The real-log
test below is what actually disqualified it.**

---

## PART 1 - THE REAL-LOG MEASUREMENT (the deciding evidence)

Methodology: reused the project's own existing `scratchpad\narrative-validation-harness\` (the exact
harness `docs\driver-narrative-validation-report.md` built and documented), which calls the ACTUAL,
compiled `NormalizedWheelLockSlipEngine.Compute` - not a reimplementation - frame-by-frame against the
two real F1 25 logs (`c_1_5_3_e_d\...211733_ShakeIt.csv`, `...212439_Raw.csv`). The engine was rebuilt
with the mechanism above compiled in, the harness was rebuilt against that DLL, and re-run unmodified.
"BEFORE" numbers below are `docs\driver-narrative-validation-report.md`'s own published numbers (the
CURRENT shipped 1.0.6.6 engine, confirmed byte-identical to what this task started from and ended at).

### Time above 80 during braking (Lock, phase1+3+5 pooled, n=3414 in every variant)

| variant | pct above 80 |
|---|---|
| **BEFORE (current shipped)** | **32.1%** |
| AFTER, longitudinal-only (friction circle disabled) | **44.6%** |
| AFTER, friction circle | **56.0%** |

**Both variants are WORSE than the baseline the owner already considered too high.** The owner's own
stated expectation ("this should be SMALL") is violated more severely after this feature than before it.

### Phase 1 (ramp - should build gradually from 0, not front-load into 80-100)

| | n | median | 0-30% | 30-60% | 60-80% | 80-100% |
|---|---|---|---|---|---|---|
| BEFORE | 1078 | 46.6 | 35.9 | 21.3 | 18.0 | 24.8 |
| AFTER, longitudinal-only | 1006 | 83.4 | 16.0 | 6.6 | 15.4 | **62.0** |

During the RAMP itself - the leading edge of a braking event, well before its own peak - 62% of frames
already read 80-100, up from 24.8%. This is a severe violation of "ramps solidly from 0" (prediction a).

### Post-release (Phase 5, split by whether the pedal has genuinely come off)

| | n | median | pct 30-60 |
|---|---|---|---|
| BEFORE, pedal>=30% (still releasing) | 480 | 76.3 | 8.5% |
| AFTER (longitudinal-only), pedal>=30% | 480 | 73.6 | **21.9%** |
| BEFORE, pedal<30% (released) | 157 | 0.0 | 3.2% |
| AFTER (longitudinal-only), pedal<30% | 157 | 0.0 | 3.8% |

This is the ONE metric that genuinely improved: more time is spent in the moderate 30-60 band while
still releasing (21.9% vs 8.5%), closer to the owner's "a graded fall through 30-60" expectation, while
the fully-released window stays correctly near zero either way. **This partial improvement does not
offset the time-above-80 and Phase-1 regressions above.**

### Slip channel (sustained 65-75% throttle plateaus - owner expects 60-80)

| log/car | BEFORE median | AFTER longitudinal-only | AFTER friction-circle |
|---|---|---|---|
| ShakeIt/Sauber | 64.8 | 70.0 | 70.0 |
| ShakeIt/F1 Generic | 0.7 (known standing-start artifact, see prior report) | 3.0 | 7.1 |
| Raw/Sauber | 36.6 | 19.9 | 70.0 |
| Raw/F1 Generic | 12.1 | 26.4 | 70.0 |

The friction-circle variant's repeated, suspiciously exact **70.0** ceiling across three different
(log,car) pairs is itself diagnostic, not organic variation - see Part 2.

---

## PART 2 - ROOT CAUSE (diagnosed, not just observed)

`GripLearner`'s peak reference (what `gripUtilization`'s denominator reads) is a **continuously-updated
decaying maximum**, fed by every qualifying frame (`_learnedPeakG *= ForgetPerSample` every `Observe`
call, `ForgetPerSample = 0.997`). Before this task, the ONLY consumer of this ratio was a discrete
"physically at this car's own limit RIGHT NOW" boolean (`physicalRatioNow >= 0.85`), used once per frame
to decide whether to teach `KeyedScaleLearner` - a robust, dispersion-weighted, P90-style aggregate that
is comparatively insulated from any one frame's noise. This task's mechanism is the FIRST to read that
same ratio continuously, every frame, as a LIVE denominator feeding the published severity directly.

That is a materially different exposure to the same decaying-max property: over a SUSTAINED plateau (a
throttle/brake held at a roughly constant, sub-maximal level for hundreds of frames), the peak reference
decays toward whatever level is currently being observed (0.997^350 ≈ 0.35 - a ~65% decay over a single
long plateau), so `gripUtilization` drifts toward or past 1.0 as an ARTIFACT of the reference eroding to
match ordinary, sustained driving - not because the car is genuinely near its true capability. The same
mechanism explains the Phase-1 finding: if the reference has already decayed from an earlier plateau, a
NEW, genuinely harder braking event can push utilization past 1.0 well before ITS OWN peak, flooding the
ramp with premature 80-100 readings. This is a structural property of using this SPECIFIC reference this
way, independent of `k`/aero-awareness, independent of friction circle, and not fixable by retuning the
blend weights this task controls - it would require changing `GripLearner`'s own decay behavior, which
every prior report in this codebase has explicitly declined to do casually ("if adopting the helper
anywhere risks regressing already-tuned behaviour, DON'T" - this task honors that same standing rule
rather than redesigning `GripLearner` outside its brief).

**Friction circle makes this WORSE, not better, given the current architecture.** The real logs show
routine lateral G of -2 to -2.8g (an F1 car cornering hard) even during frames gated as genuine
Slip-channel throttle events (corner-exit acceleration, a normal driving pattern). The learned peak
reference is deliberately taught ONLY from lateral-isolated frames (`LateralIsolationGateG = 0.5`) to
avoid teaching a false peak from cornering - so it stays a genuinely small, longitudinal-only number.
Combining it with a RAW total-magnitude numerator that is NOT lateral-isolated (friction circle, by
definition) means routine cornering alone can push the ratio far past 1.0, independent of how hard the
car is braking/accelerating - a numerator/denominator mismatch, confirmed by the friction-circle
variant's own worse numbers (56.0% vs 44.6% time-above-80) and the suspicious exact "70.0" ceiling
repeating across unrelated (log,car) pairs in the Slip table above.

---

## PART 3 - MUTATION EVIDENCE

**(1) Force gripUtilization constant, confirm a band-correspondence test fails.** Done live: temporarily
set `gripUtilization = 0.5` (a fixed constant) in `ComputeChannel` and re-ran the 13 new unit tests.
**6 of 13 failed immediately**, including `Lock_sub_limit_band_correspondence` (both anchors),
`Slip_sub_limit_band_correspondence` (both anchors), `A_non_aero_car_with_a_flat_constant_peak_still_gets_a_working_band_correspondence`,
and `Trail_braking_with_lateral_G_reads_higher_than_the_same_longitudinal_G_alone` - confirming the
mechanism is genuinely load-bearing (not a no-op) when it is working as designed. Reverted immediately;
13/13 re-confirmed green.

**(2) Remove the friction-circle term, confirm the cornering/trail-braking case degrades.** Done live,
twice, in two directions:
- At the UNIT level: `Trail_braking_with_lateral_G_reads_higher_than_the_same_longitudinal_G_alone`
  failed under mutation (1) above (both queries read 10.9 - no cornering benefit at all without the
  lateral term), confirming friction circle IS what makes trail-braking read correctly at the
  mechanism-design level, exactly as intended.
- At the REAL-LOG level, the comparison is the OPPOSITE of a simple "worse without it": Part 1's own
  table shows the LONGITUDINAL-ONLY variant is measurably BETTER (44.6% vs 56.0% time-above-80) than the
  friction-circle variant on these two real logs - because of the numerator/denominator mismatch
  diagnosed in Part 2, not because friction-circle-awareness is a bad idea in principle. **Reported
  plainly, as the brief's own risk section asked**: friction circle helps in the narrow, controlled unit
  scenario it was designed for, but actively worsens the aggregate real-log outcome given how the
  existing peak reference is calibrated - it should not ship in either form on this codebase's current
  `GripLearner` design.

---

## PART 4 - WET/DRY REGRESSION CHECK (the blocking check)

A small, dedicated throwaway harness (`scratchpad\wetdry-harness\`, same convention, references the
built DLL) replayed all four `Common_1_5_e_d` logs (Raw-Wet, ShakeIt-Wet, Raw-Dry, ShakeIt-Dry) through
the actual compiled engine with the mechanism temporarily reinstated, filtering to frames where the
CONFIGURED source itself reads >=50 (a genuine, informative reading, mirroring the prior crashed agent's
own methodology) and reporting Lock median/p90:

| log | Lock median | Lock p90 | n |
|---|---|---|---|
| Raw-Wet | 88.6 | 97.6 | 169 |
| Raw-Dry | 90.1 | 91.2 | 97 |
| ShakeIt-Wet | 82.2 | 97.1 | 149 |
| ShakeIt-Dry | 68.3 | 93.4 | 33 |

**Raw: no regression** - wet (88.6) and dry (90.1) stay tightly consistent (spread 1.5), matching the
already-good baseline consistency (89.1 vs 90.3, spread 1.2, from the pre-existing evaluation) almost
exactly. **ShakeIt: a real but modest 13.9-point wet/dry spread** (82.2 vs 68.3) - notably not the
severe, blocking-scale inconsistency the F1-25 fix originally found and fixed (which was driven by a
SHARED g-based ceiling reading wet/dry differently by tens of points); n=33 for ShakeIt-Dry is thin, and
ShakeIt's own known maturity-dependent under-reporting (`docs\shakeit-silence-diagnosis-report.md`) is a
plausible, already-diagnosed confound distinct from this feature. **Verdict: the wet/dry inconsistency
does NOT clearly return** at the severity/scale the original bug had - this is not the reason this
mechanism was rejected; the time-above-80/Phase-1 regression in Parts 1-2 is.

---

## PART 5 - GT3 / NON-AERO GENERALITY

No non-F1 (GT3-class, LMU) log exists in this project's captured data - this cannot be validated
empirically, and is not claimed to be. Reasoned from structure instead, per the brief's own instruction:

- **Unit-tested**: `A_non_aero_car_with_a_flat_constant_peak_still_gets_a_working_band_correspondence`
  (a car taught at one fixed speed the whole session, so `k` stays exactly 0 per
  `GripLearnerSpeedModelTests`'s own guards, degenerating `SpeedAwarePeakG` to a plain constant) passed
  before rejection - the curve's own anchors (75/90/100% of a CONSTANT peak) work identically regardless
  of whether that peak varies with speed, confirming the mechanism's STRUCTURE degrades correctly for a
  non-aero car.
- **However, the disqualifying root cause (Part 2) is independent of `k` entirely** - it is about how
  `PublishedPeakG` (the decaying-max reference itself, aero-aware or not) behaves when queried as a live,
  per-frame denominator rather than a discrete threshold check. A GT3 car in LMU would be expected to
  exhibit the SAME decaying-reference-erodes-during-plateaus artifact during sustained braking/throttle,
  regardless of its (near-zero) aero sensitivity - this is a property of the LEARNING mechanism's decay
  rate, not the physics model on top of it. **Not validated, but there is no structural reason to expect
  GT3 cars would be exempt from the same failure mode** - if anything, a non-aero car's flatter peak-g
  profile removes one potential mitigating factor (a rising aero ceiling at speed) that could otherwise
  have partially masked the issue for F1-class cars at high speed.

---

## PART 6 - NARRATIVE PREDICTIONS, OLD VS NEW

| prediction | BEFORE (docs\driver-narrative-validation-report.md) | AFTER this mechanism |
|---|---|---|
| (a) Phase 1 ramps solidly from 0 | PASS | **DEGRADED** - p10 stays low, but 62% of the ramp itself now reads 80-100 (was 24.8%) - technically still starts near 0, but front-loads into "at limit" far too early and too often |
| (b) Phase 3 rising (constant pedal, falling speed) | PARTIAL/data-limited, Stage A helps | **UNCHANGED** - this feature does not alter the underlying physical-ratio trend (`PRED_B_OLDNEW` in the harness is a separate, physics-only mirror this task's engine change does not touch); the verdict from the existing report stands |
| (c) At release inflection, 60-80 usually near 80 | MIXED, pooled median 71.5, 58.3% in 60-100 | **WORSE** - pooled median drops to 62.1 (longitudinal-only), a wider spread away from the owner's "usually near 80" |
| (d) Time above 80 is small | **FAIL** (32.1%) | **WORSE FAIL** (44.6%/56.0%) |
| (e) Post-release mostly 30-60 | FAIL, bimodal (76.3 releasing / 0.0 released) | **PARTIAL IMPROVEMENT** - pct in 30-60 while still releasing rises from 8.5% to 21.9%; still bimodal (median stays high at 73.6 while releasing, drops to 0.0 once released), not resolved |
| (f) Sustained 65-75% throttle -> 60-80 | FAIL (one source close, others far off) | **MIXED, NOT CLEARLY IMPROVED** - one (log,car) pair lands closer to the target band, two land further off (see Part 1's Slip table), and the friction-circle variant's suspicious "70.0" ceiling is an artifact, not a genuine fix |

**Net: one real, partial improvement (post-release graded-fall behaviour), against a severe regression
in the single metric the owner weighted most heavily (time above 80) and in Phase 1's own ramp
behaviour.** This is not a marginal call - the regression is large (12-24 percentage points) and
consistent across both friction-circle and longitudinal-only variants.

---

## DECISION

**Ship nothing.** Per the brief's own explicit instruction ("if it doesn't work... ship nothing, revert
cleanly, and report with numbers"), every file touched during this task has been restored to be
byte-identical to the pre-task baseline (confirmed via `diff -rq QAdvanceFeedback_1.0.6.6_speed_aware/...
QAdvanceFeedback/... --exclude=bin --exclude=obj`, zero differences in both `QAdvanceFeedback\` and
`QAdvanceFeedback.Tests\`). No partial ship (e.g. "Lock only" or "Slip only") is recommended either -
Part 1 shows the SAME root-cause regression (time above 80, Phase-1 ramp) affects the Lock channel
directly and measurably, and the Slip channel's own numbers are too mixed/artifact-prone (the "70.0"
ceiling) to recommend shipping in isolation.

No persistence/version-bump was needed at any point - the mechanism never learned or stored anything
new; it only read the existing, already-persisted `GripLearner`/`KeyedGripLearner` state differently.

**If this idea is revisited**: the structural fix would need to address `GripLearner`'s own decaying-max
reference being unsuitable as a LIVE, per-frame denominator (a materially different exposure than its
existing discrete-threshold use) - e.g. a genuinely slower-decaying or session-long robust-percentile
reference purpose-built for this use case, evaluated on its own against real logs before being combined
with any band-mapping curve. That is a `GripLearner`-level redesign, out of this task's scope, and this
codebase's own standing precedent is explicit that such changes should not be made casually.

---

## Concerns

1. **The core regression (time above 80, Phase-1 ramp) is large and consistent across both friction-
   circle and longitudinal-only variants** - this is a structural property of the existing `GripLearner`
   decay mechanism being read live, not a tunable blend-weight problem; retuning the constants this task
   controlled (the 0.95/1.05 and 55/72 ramps) would not fix it.
2. **The post-release "graded fall through 30-60" partial improvement (Part 1) is real and worth noting**
   for whoever next investigates the post-release-bimodality concern this project's other reports have
   already flagged - even though this specific mechanism should not ship, the underlying idea (blend
   toward a physics-derived value during release) measurably helped that one narrow slice.
3. **ShakeIt-Dry's own wet/dry check (Part 4) has a thin sample (n=33)** - not itself disqualifying, but
   a genuinely under-powered check; a session with more sustained Dry ShakeIt braking would give a more
   confident answer on whether any residual wet/dry gap is attributable to this feature or to ShakeIt's
   own already-diagnosed maturity-dependent under-reporting.
4. **GT3/non-aero generality (Part 5) is reasoned from structure, not validated** - no non-F1 log exists
   in this project's captured data; this is stated plainly rather than claimed as tested.
5. **The decompiled reference's own blend (three raw ComputeXxxBlendWeight signals combined via a single
   Math.Max, then averaged) was NOT reused as-is** - three real defects were found in it (or in a direct
   translation of it) during this task's own unit testing (Part 0) and fixed before the real-log test
   ran; anyone reviving this idea should start from THIS task's corrected blend shape (rise/release
   split, single confidence notion, scaled floor), not the original decompiled file, even though neither
   ultimately shipped.

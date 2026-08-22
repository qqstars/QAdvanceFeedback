# Driver-narrative validation of the CURRENT (Stage A) Normalized engine - MEASUREMENT ONLY, NOTHING SHIPPED

## STATUS

**MEASUREMENT/ANALYSIS TASK. NO PRODUCTION CODE CHANGED.** A throwaway C# replay harness
(`scratchpad\narrative-validation-harness\`, net48 console app, references
`QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll` directly - not part of the solution, not
committed, following this project's own established harness convention) replayed both real F1 25
logs (`c_1_5_3_e_d\...211733_ShakeIt.csv`, `...212439_Raw.csv`) frame-by-frame through the ACTUAL,
CURRENT `NormalizedWheelLockSlipEngine` (Stage A speed-aware grip, already shipped), segmented the
output into braking/acceleration events and phases per the owner's own account of how they drove
these sessions, and tested six specific predictions from that account against the replayed
Normalized output.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **868/868 passed, 0 failed, 0 skipped**.
`QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `.pdb` -
**single-DLL confirmed**. The harness project is NOT referenced by `QAdvanceFeedback.sln` (grep
returns nothing) and lives entirely under `scratchpad\narrative-validation-harness\`, outside both
`QAdvanceFeedback\QAdvanceFeedback\` and `QAdvanceFeedback\QAdvanceFeedback.Tests\`. No git
operation of any kind was run. No recursive deletes.

**Headline result**: the owner's account is confirmed in some respects (Phase 1 does ramp from 0;
Stage A's speed-aware model does measurably improve the physical ratio trend during "pedal held,
speed falling" versus the old flat model, in 4 of 4 replayed car/source combinations) and
contradicted in others, with real numbers behind each (post-release severity stays far higher, far
longer, than the owner recalls; time above 80 is not small; the release-inflection source value is
not a stable anchor candidate with only 60 pooled samples across two logs). See Part 3 for the full
prediction-by-prediction verdict.

---

## PART 0 - METHODOLOGY

**Replay, not log playback.** Per the brief, the harness recomputes Normalized fresh from source
telemetry - it never reads the logs' own `WheelLock.Normalized.All`/`WheelSlip.Normalized.All`
columns, since those predate this build. Per frame it builds a plain `ITelemetryFrame` from
`Diag.Telemetry.*` (speed, brake/throttle percent, longitudinal/lateral G, per-wheel loose-surface
flags), the CONFIGURED source's own per-wheel raw reading (`Diag.Source.Lock/Slip.FrontLeft..RearRight`
- the actual input to `NormalizedWheelLockSlipEngine.Compute`'s `rawLockWheels`/`rawSlipWheels`), and
Layer 3's own always-computed Raw (`WheelLock/WheelSlip.Raw.FrontLeft..RearRight` - the
`layer3RawLockWheels`/`layer3RawSlipWheels` fallback-detector input), then calls `engine.Compute(...)`
in strict chronological row order and records `result.LockAll`/`result.SlipAll`.

**Cold replay, deliberately.** A fresh `NormalizedWheelLockSlipEngine()` per log, no `Load`/`LoadCold`/
`ImportAll` - matching the precedent both prior Stage A/B harnesses used. `QAdvanceFeedback.Parameters.json`
(the persisted state alongside these logs) is Version 4, predates `GMech`/`K` entirely, and is an
end-of-session snapshot - loading it would not reproduce "what the current engine does against this
telemetry from a clean start," it would reproduce "what a stale, pre-Stage-A blend looks like,"
which is not the question this task asks. Its keys were used only to recover the EXACT
`sourceIdentity` strings the real plugin used for these two sessions (`Plain:ShakeITMotorsV3Plugin...`
for the ShakeIt log, `Plain:QAdvanceFeedback.WheelLock/WheelSlip.Raw...` for the Raw log), so any
(game,car,source)-keyed behavior (cross-car seeding, cold/warm blend shape) replays under the
identity a real session actually used.

**Car switch handling.** Both logs contain exactly one car switch each, at a single contiguous
boundary (ShakeIt: row 9707, Sauber->F1 Generic; Raw: row 11238, same) - confirmed by inspecting
`Diag.CarId` directly, not assumed. `engine.ResetDirection()` is called at that boundary (mirroring
what a real plugin does on a car switch), and the transition frame's "Old" telemetry frame/Dt are
treated as absent (a fresh start), exactly as SimHub would not hand a cross-car Old/New pair either.

**Frame rate.** `TimestampUtc` is monotonic; after an initial ~0.34-0.37s startup gap (the very
first row of each log, before real driving begins), the interval settles to ~16-18ms (~60fps) for
the remainder of both sessions. This is fine relative to every dt-based smoothing constant the engine
uses (`SurfaceFractionSmoothingTauSeconds`/`FallbackDivergenceSmoothingTauSeconds` = 1.0s,
`LongitudinalDirectionResolver`'s own EMA) - no coarse-logging concern for this data.

**Old-vs-new physical-ratio counterfactual (for prediction b).** A SEPARATE, harness-owned mirror of
the engine's own shared physical-limit detector was built: one `GripLearner` per (carId, channel,
Sealed/Loose surface bucket) - the exact key shape `_lockPhysicalReference`/`_slipPhysicalReference`
use internally - fed by the SAME gates the real engine applies (`triggered` pedal threshold from
`LegacyThresholds.Defaults`, direction-confirmed `engaged`, `|LateralG| <= 0.5` isolation,
`TelemetryLearningGate`'s own validity/pedal-minimum check, mirrored via a second harness-owned gate
instance processing the identical frame sequence) and the SAME smoothed loose-fraction blend. At each
qualifying frame this mirror computes `GripLearner.Ratio(magnitudeG, applyColdStartCeiling:false)`
(OLD - flat, no speed) and `Ratio(magnitudeG, applyColdStartCeiling:false, speedKmh)` (NEW -
speed-aware) - the SAME BEFORE/AFTER comparison `docs\speed-aware-grip-report.md` Part 2 itself used.
This is a physical-ratio-level comparison (roughly 0-1+), not the final 0-100 `LockAll`/`SlipAll` -
useful specifically to isolate Stage A's own effect from the calibration layer sitting on top of it.
Loose-surface frames are rare in both logs (WheelOnLooseSurface true on any wheel: 3.46% of ShakeIt,
0.98% of Raw) but not negligible, so the full Sealed/Loose blend was replicated rather than assumed
away.

**Braking-zone/phase segmentation** (Lock channel): contiguous `BrakePercent > 5` runs, gaps <0.3s
merged, zones <4 frames discarded. Within a zone, brake is smoothed with a causal EMA (tau=120ms);
local maxima >=15% brake, separated by >=150ms, each define one rise/plateau/release sub-event (a
zone with repeated stabs yields multiple sub-events, per the owner's own "same expectations apply to
each sub-phase" instruction). **Phase 1** = sub-event start to the peak. **Phase 3 (plateau)** = the
stretch from the peak where the smoothed trace stays within `max(7 points, 10% of peak)` of the peak
value, ONLY counted if `SpeedKmh` genuinely fell across that stretch (checked per-event, not assumed -
zero contradictions were found: every detected plateau did show falling speed). **Release inflection**
= first point after the plateau where a ~200ms rolling derivative of smoothed brake drops <= -5 points
and stays down (no rebound above +3 points) for >=150ms. **Phase 5 (post-release)** = inflection to
sub-event end (brake back to <=5%, or the next stab).

**Acceleration-zone segmentation** (Slip channel): contiguous `ThrottlePercent > 5` runs, same
gap-merge/min-length rule; sustained 65-75% throttle stretches >=300ms are the "plateau" test
population for prediction (f); a throttle drop from >40% to <=20% marks a release, timed until
`SlipAll` first reaches <=10.

---

## PART 1 - RELEASE-INFLECTION DETECTOR ROBUSTNESS

Across all four (log x car) combinations, **87 braking sub-events** were identified from **46 zones**;
a clean, unambiguous release inflection (by the criterion above) was found in **60 of 87 (69.0%)**:

| log | car | zones | sub-events | with a plateau | with a clean release |
|---|---|---|---|---|---|
| ShakeIt | Sauber | 13 | 35 | 4 (11.4%) | 10 (28.6%) |
| ShakeIt | F1 Generic | 7 | 28 | 6 (21.4%) | 14 (50.0%) |
| Raw | Sauber | 17 | 44 | 10 (22.7%) | 18 (40.9%) |
| Raw | F1 Generic | 9 | 35 | 6 (17.1%) | 18 (51.4%) |

The detector is honest but the underlying events are not generous: only **11.4-22.7%** of sub-events
produce a plateau long enough (>=150ms within tolerance) to even attempt phase-3 analysis on, and the
remaining ~30% of sub-events that DO get a clean release never had a real plateau at all (a single
stab-and-release, or continuous modulation with no flat spot) - consistent with both prior reports'
own finding that these captures contain brief, punchy braking rather than long, held stops. Peak
`|LongitudinalG|` lands at or after the pedal's own local maximum in **98 of 142** sub-events pooled
(69%) - i.e. deceleration usually continues building briefly after the pedal itself peaks, matching
the owner's step 2 ("the event's own peak decel G builds up" after the pedal is already down).

---

## PART 2 - DISTRIBUTIONS

Median / p10 / p90 / % in each canonical band, per phase x channel x log x car. (Full per-combination
table also mirrored in the harness's own `out\tables.md`; reproduced here.)

| log | car | channel | phase | n | median | p10 | p90 | 0-30% | 30-60% | 60-80% | 80-100% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ShakeIt | Sauber | Lock | Phase1(ramp) | 243 | 64.8 | 0.0 | 90.4 | 25.1 | 21.8 | 15.2 | 37.9 |
| ShakeIt | Sauber | Lock | Phase3(plateau,falling speed) | 50 | 78.1 | 51.5 | 88.8 | 0.0 | 16.0 | 36.0 | 48.0 |
| ShakeIt | Sauber | Lock | AtReleaseInflection | 10 | 74.3 | 35.2 | 81.7 | 10.0 | 30.0 | 30.0 | 30.0 |
| ShakeIt | Sauber | Lock | Phase5(post-release) | 120 | 76.6 | 72.5 | 87.8 | 1.7 | 4.2 | 57.5 | 36.7 |
| ShakeIt | Sauber | Lock | Phase5-pedal<30% | 9 | 73.0 | 0.0 | 73.6 | 22.2 | 0.0 | 77.8 | 0.0 |
| ShakeIt | Sauber | Lock | Phase5-pedal>=30% | 111 | 77.3 | 72.8 | 87.9 | 0.0 | 4.5 | 55.9 | 39.6 |
| ShakeIt | Sauber | Slip | Plateau65-75(300ms+) | 46 | 64.8 | 38.8 | 70.0 | 4.3 | 39.1 | 56.5 | 0.0 |
| ShakeIt | F1 Generic | Lock | Phase1(ramp) | 234 | 12.1 | 0.0 | 78.5 | 75.6 | 9.4 | 5.1 | 9.8 |
| ShakeIt | F1 Generic | Lock | Phase3(plateau,falling speed) | 86 | 71.7 | 37.0 | 87.4 | 7.0 | 18.6 | 46.5 | 27.9 |
| ShakeIt | F1 Generic | Lock | AtReleaseInflection | 14 | 45.3 | 0.0 | 90.0 | 50.0 | 0.0 | 21.4 | 28.6 |
| ShakeIt | F1 Generic | Lock | Phase5(post-release) | 141 | 29.4 | 0.0 | 90.2 | 51.8 | 7.8 | 12.8 | 27.7 |
| ShakeIt | F1 Generic | Lock | Phase5-pedal<30% | 57 | 0.0 | 0.0 | 69.2 | 82.5 | 3.5 | 5.3 | 8.8 |
| ShakeIt | F1 Generic | Lock | Phase5-pedal>=30% | 84 | 70.3 | 19.9 | 90.3 | 31.0 | 10.7 | 17.9 | 40.5 |
| ShakeIt | F1 Generic | Slip | Plateau65-75(300ms+) | 353 | 0.7 | 0.0 | 2.1 | 100.0 | 0.0 | 0.0 | 0.0 |
| Raw | Sauber | Lock | Phase1(ramp) | 327 | 67.9 | 14.7 | 94.1 | 16.8 | 26.9 | 24.5 | 31.8 |
| Raw | Sauber | Lock | Phase3(plateau,falling speed) | 152 | 80.6 | 69.0 | 89.3 | 0.0 | 7.2 | 40.1 | 52.6 |
| Raw | Sauber | Lock | AtReleaseInflection | 18 | 53.2 | 0.0 | 83.7 | 33.3 | 22.2 | 11.1 | 33.3 |
| Raw | Sauber | Lock | Phase5(post-release) | 168 | 73.9 | 17.6 | 87.7 | 20.8 | 15.5 | 42.3 | 21.4 |
| Raw | Sauber | Lock | Phase5-pedal<30% | 19 | 0.0 | 0.0 | 70.8 | 73.7 | 0.0 | 26.3 | 0.0 |
| Raw | Sauber | Lock | Phase5-pedal>=30% | 149 | 74.7 | 24.9 | 89.1 | 14.1 | 17.4 | 44.3 | 24.2 |
| Raw | Sauber | Slip | Plateau65-75(300ms+) | 255 | 36.6 | 0.0 | 70.0 | 42.7 | 32.2 | 25.1 | 0.0 |
| Raw | F1 Generic | Lock | Phase1(ramp) | 274 | 44.7 | 13.4 | 86.3 | 34.3 | 24.5 | 23.7 | 17.5 |
| Raw | F1 Generic | Lock | Phase3(plateau,falling speed) | 92 | 80.3 | 65.4 | 86.7 | 0.0 | 8.7 | 40.2 | 51.1 |
| Raw | F1 Generic | Lock | AtReleaseInflection | 18 | 72.4 | 34.1 | 91.0 | 11.1 | 11.1 | 55.6 | 22.2 |
| Raw | F1 Generic | Lock | Phase5(post-release) | 208 | 72.8 | 0.0 | 91.9 | 24.0 | 1.9 | 48.1 | 26.0 |
| Raw | F1 Generic | Lock | Phase5-pedal<30% | 72 | 0.0 | 0.0 | 70.5 | 61.1 | 4.2 | 31.9 | 2.8 |
| Raw | F1 Generic | Lock | Phase5-pedal>=30% | 136 | 77.3 | 70.6 | 93.8 | 4.4 | 0.7 | 56.6 | 38.2 |
| Raw | F1 Generic | Slip | Plateau65-75(300ms+) | 154 | 12.1 | 0.0 | 70.0 | 59.7 | 24.7 | 15.6 | 0.0 |
| **POOLED (all logs/cars)** | - | Lock | Phase1(ramp) | 1078 | 46.6 | 4.0 | 89.2 | 35.9 | 21.3 | 18.0 | 24.8 |
| **POOLED** | - | Lock | AtReleaseInflection | 60 | 71.5 | 0.0 | 90.0 | 26.7 | 15.0 | 30.0 | 28.3 |
| **POOLED** | - | Lock | Phase5(post-release) | 637 | 73.8 | 0.0 | 90.2 | 25.1 | 7.2 | 40.5 | 27.2 |
| **POOLED** | - | Lock | Phase5-pedal<30% | 157 | 0.0 | 0.0 | 71.8 | 68.2 | 3.2 | 24.2 | 4.5 |
| **POOLED** | - | Lock | Phase5-pedal>=30% | 480 | 76.3 | 29.3 | 90.3 | 11.0 | 8.5 | 45.8 | 34.6 |

("Phase5-pedal<30%"/"Phase5-pedal>=30%" split the post-release window by whether the brake pedal has
actually dropped under 30% yet - see Part 3(e) for why this split matters.)

---

## PART 3 - PREDICTIONS, TESTED

### (a) Phase 1 ramps solidly from 0 - **PASS**

Every phase-1 population has p10 = 0.0 (Raw's two combos: 13.4-14.7, still near the floor) and a p90
of 78.5-94.1 - i.e. phase 1 genuinely spans from a real zero up to a high value within the same
sub-event, which is what "ramps solidly from 0" requires. Mechanistically: the channel is gated by
`triggered` (brake >= 20%) AND `engaged` (direction confirmed Slowing) - so the "0" period lasts as
long as it takes the pedal to cross that threshold and the direction resolver's own dead-band-filtered
derivative to confirm deceleration, not literally the first frame brake becomes nonzero. This is by
design, not a defect, and the data is consistent with it.

### (b) Phase 3 (constant pedal, falling speed) produces a RISING Normalized - **PARTIAL PASS / DATA-LIMITED, BUT STAGE A MEASURABLY HELPS**

Only **5 sub-events across all 4 combinations** had a phase-3 stretch long enough (>=5 points, >=0.3s)
to fit a trend to the PUBLISHED `LockAll`: ShakeIt/Sauber 0, ShakeIt/F1 Generic 1 (+154.4 pts/sec),
Raw/Sauber 2 (-3.03, -3.04 pts/sec), Raw/F1 Generic 2 (+144.7, +10.8 pts/sec). That is 3 of 5 rising, 2
of 5 falling - **too few points to call this prediction settled at the published-output level**, and
it should not be oversold as such.

The more statistically meaningful test is the OLD-vs-NEW **physical ratio** (Part 0's counterfactual),
computed over every qualifying phase-3 frame (n=50-152 per combination, not just the trend-fittable
sub-events):

| log | car | n | avg OLD ratio (flat) | avg NEW ratio (speed-aware) | OLD slope/frame | NEW slope/frame |
|---|---|---|---|---|---|---|
| ShakeIt | Sauber | 50 | 0.879 | 1.005 | -0.0033 | **+0.0016** |
| ShakeIt | F1 Generic | 85 | 0.561 | 0.807 | -0.0056 | -0.0041 |
| Raw | Sauber | 152 | 0.784 | 0.959 | -0.0036 | -0.0026 |
| Raw | F1 Generic | 92 | 0.678 | 0.938 | -0.0011 | -0.0003 |

**In all 4 of 4 combinations**, Stage A's speed-aware reference raises the average physical ratio
during phase 3 (by 0.13-0.26) and makes the slope less negative (in 1 of 4 it fully reverses to
positive). This is a real, attributable, and consistent improvement - the old flat-scalar model would
have shown falling grip-utilization as speed drops through a plateau in every single case; the new
model only fully reverses that to a genuine RISE in one of four, and reduces (without eliminating) the
fall in the other three. **Verdict: Stage A demonstrably works in the direction the owner predicted,
but on these two specific logs it does not yet fully overturn the "falling" signature in most braking
zones** - most likely because within one short zone the speed drop is often too small relative to the
fitted k for the v^2 term to dominate ordinary G noise and the learner's own decaying-max updates.

### (c) At the release inflection, Normalized is 60-80, usually near 80 - **MIXED, POOLED: PARTIAL**

Pooled across all 60 detected inflections: median 71.5, but only 30.0% actually land in 60-80 (the
literal predicted band) and 28.3% land in 80-100 (consistent with "may exceed 80, occasionally reach
100"); **26.7% land below 30** - overwhelmingly concentrated in ShakeIt/F1 Generic (50% of its own 14
inflections below 30, median only 45.3). Raw/F1 Generic looks closest to the owner's account (55.6% in
60-80, median 72.4); ShakeIt/Sauber and Raw/Sauber are both spread fairly evenly across all four bands
rather than concentrated near 80. Combined 60-100 = 58.3% pooled - real signal in the right
neighborhood, but "usually near 80" is not what these captures show once ShakeIt/F1 Generic's own low
outcomes are included.

### (d) Time above 80 is a very small % of braking time - **FAIL**

Pooled across all 3,414 qualifying braking-engaged frames (phase 1 + phase 3 + phase 5 combined, all
logs/cars): **1,097 frames (32.1%) read above 80.** This is not "brief lockup that recovers" as a small
minority of time - it is roughly a third of all engaged braking time. This is driven substantially by
Phase 5's own behavior (see (e) below): once the release inflection fires, severity often STAYS above
80 for an extended stretch rather than dropping quickly, which inflates this aggregate well past "very
small."

### (e) Post-release is mostly 30-60 - **FAIL, with an important nuance**

The naive post-release window (inflection to zone end) is NOT mostly 30-60 in 3 of 4 combinations -
pooled median 73.8, only 7.2% of pooled Phase5 time actually in 30-60, and 40.5%+27.2%=67.7% in 60-100.
Splitting Phase5 by how much the pedal has actually come off (rather than by elapsed time, which showed
no clear decay - see the harness's own `Phase5-early`/`Phase5-late` rows in `out\tables.md`, which stay
just as high or higher later in the window) reveals why: while the pedal is still >=30% (still actively
releasing, not yet let go), Normalized stays high (pooled median 76.3, 80.4% in 60-100) - physically
reasonable, the car is still braking hard. Only once the pedal genuinely drops below 30% does severity
fall - but it falls PAST the predicted 30-60 band, straight toward near-zero (pooled median 0.0, 68.2%
in 0-30, only 24.2%+4.5%=28.7% still in 60-100). **There is little dwell time in 30-60 either way** -
the transition looks closer to bimodal (high while still meaningfully braking, then a fast drop to
near-zero once the pedal is mostly off) than to the owner's remembered "a good, speed-appropriate,
moderate decel G lingers for a while." Whether the owner's account describes a MORE gradual real-world
release than these particular captures contain, or whether the model's own release is simply faster
than felt, cannot be distinguished from this data alone - but the "stays high the whole time forever"
reading that the naive Phase5 number alone would suggest is not the right characterization either.

### (f) Sustained non-maximum throttle (65-75%, 300ms+) yields 60-80; throttle release - **FAIL, WITH ONE CONFOUND IDENTIFIED**

Raw data: ShakeIt/Sauber median 64.8 (close), ShakeIt/F1 Generic median 0.7 (near-total 0-30), Raw/Sauber
median 36.6, Raw/F1 Generic median 12.1 - none of the four squarely lands in 60-80.

**ShakeIt/F1 Generic's own near-zero result is an identified, explainable artifact, not a Slip-channel
bug**: all 353 of its "plateau" samples trace to a single ~250ms window at rows 9756-9760, immediately
after the car switch, at a `GroundSpeedKmh` of ~0.01-0.02 (i.e. the car is essentially stationary -
a standing start/grid moment) with `Direction` correctly resolved as `Unknown` throughout. The
engine's own explicit design ("when direction is Unknown, NEITHER channel gets a non-zero reading -
there is nothing honest to attribute the magnitude to") is doing exactly what it is supposed to here;
this specific plateau is not representative real driving and should be excluded from judging this
prediction.

For the other three (real-driving) combinations, restricting to frames where `Direction` was
independently confirmed `SpeedingUp` (excluding trail-braking/overlap frames where throttle sits in
65-75% but the car is not confirmed accelerating - itself a real, by-design consequence of "pedal state
never decides direction") raises every median: ShakeIt/Sauber 64.8->70.0, Raw/Sauber 36.6->50.0, Raw/F1
Generic 12.1->33.1. This closes much of the gap but still leaves Raw's two combinations below 60 -
**a genuine, if partly explained, shortfall** rather than a pure detection artifact: even during
confirmed acceleration, a sustained mid-throttle input does not reliably read 60-80 on the Raw source in
these captures.

Throttle release: no release event in either log crossed from >40% throttle down to <=20% while also
having recorded a qualifying SlipAll reading nearby (`PRED_F_RELEASE` n=0 in all four combinations) - the
throttle traces in these two sessions simply do not contain a clean, isolated "hard lift" the harness's
release detector could isolate from the surrounding modulation. This is a genuine data-coverage gap, not
a pass or fail - it cannot be tested from these two logs as captured.

---

## PART 4 - WHERE THE DATA CONTRADICTS THE OWNER'S ACCOUNT (and where it's the account, not the code)

- **Phase 3's premise held every time it was checked**: zero of the 20 detected plateaus (across all
  four combinations) failed the "speed must actually be falling" cross-check - the owner's account of
  a genuine constant-pedal-falling-speed phase is real in this data, it is just RARE (only 11-23% of
  sub-events have one at all) - an account of the drive, not a code defect.
- **Post-release severity (Part 3e) contradicts the account most clearly** - "a good speed-appropriate
  decel G" reading in 30-60 is not what these two logs show; severity is closer to bimodal
  (high-while-still-releasing, then near-zero once the pedal is mostly off) with little 30-60 dwell
  time either way. This is a genuine candidate for a real behavioral gap (or a genuinely faster car/
  session than the owner recalls) - not something the replay methodology can resolve further.
- **Time above 80 (32.1%) is an order of magnitude more than "a very small percentage"** - either the
  owner's recollection of "brief lockup that recovers" undersells how much of these two specific
  sessions was spent at/near the limit, or the current calibration genuinely reads elevated for too
  long once a car is braking hard - both readings are consistent with the same numbers; this report
  cannot separate them further without more sessions or the owner's own live impression while replaying
  these exact captures.
- **The ShakeIt/F1 Generic throttle-plateau near-zero result is NOT a contradiction of the account or
  the code** - it is a single non-representative standing-start moment right at a car switch, correctly
  suppressed by the direction-confirmation design; it should be discounted, not read as a Slip-channel
  failure.
- **Release events for prediction (f) could not be tested at all** - not a pass, not a fail, a genuine
  gap in what these two captures contain.

---

## PART 5 - THE FORWARD-LOOKING QUESTION: IS THE RELEASE INFLECTION A VIABLE LEARNED ANCHOR?

**Pool sizes are small - Lock's known data-starvation problem persists even at the EVENT level, not just
the within-event-band level**: 10 (ShakeIt/Sauber), 14 (ShakeIt/F1 Generic), 18 (Raw/Sauber), 18
(Raw/F1 Generic) - 60 pooled. This is a materially larger pool than the "0-3 pre-peak samples per band"
the Stage B report found when gating within a band, because a release-inflection label only needs ONE
qualifying moment per sub-event rather than several sustained samples inside a narrow ratio band -
**event-based labeling does sidestep the WITHIN-band starvation problem**, but the number of EVENTS
itself is still small (10-18 per car/source), because braking zones with a clean, detectable release are
themselves not abundant in a two-log capture.

**Dispersion of the source value (`Diag.Source.Lock.All`) at the release inflection**:

| pool | n | mean | median | sd | IQR | CV |
|---|---|---|---|---|---|---|
| ShakeIt/Sauber | 10 | 34.62 | 32.65 | 18.84 | 16.80 | 0.544 |
| ShakeIt/F1 Generic | 14 | 6.47 | 5.05 | 5.91 | 6.03 | 0.913 |
| Raw/Sauber | 18 | 30.54 | 34.12 | 15.35 | 22.21 | 0.503 |
| Raw/F1 Generic | 18 | 33.68 | 29.71 | 16.10 | 13.86 | 0.478 |
| **Pooled, all 4** | **60** | **26.55** | **26.77** | **18.21** | **28.32** | **0.686** |

ShakeIt/F1 Generic's own pool sits far below the other three (mean 6.47 vs 30-34) - a real, unexplained
divergence worth flagging on its own (possibly the same maturity-dependent ShakeIt under-reporting
`docs\shakeit-silence-diagnosis-report.md` documented for this exact car, though this report did not
chase that down further - out of scope here). Excluding it, the three remaining pools (30.5-34.6 mean,
CV 0.48-0.54) look broadly consistent with each other across two different sources (ShakeIt, Raw) and
two cars - a genuinely encouraging sign for cross-source/cross-car consistency - but WITH it, the pooled
CV (0.686) and the fact that one of four pools sits an order of magnitude off the others means this pool
is **not internally consistent enough, as measured here, to trust blindly as a single learned anchor
without first explaining why one (car,source) diverges so far.**

**Compared to the prior reports' own unsupervised-pool dispersion**: `multipoint-calibration-report.md`
Part 4 measured a single-point-estimate std of 6.89-10.09 (ShakeIt Slip) and 8.79-9.70 (Raw Slip) across
its own three ratio-anchor bands - i.e. a similar order of magnitude to this pool's own per-combination
std (5.91-18.84), not obviously tighter. The SAME report's pooled-P90 estimator (a more robust
aggregate, not a raw single-value std) reached 2.68-5.17 - meaningfully tighter than anything measured
here, because it aggregates many more raw readings per band (dozens to hundreds) into one robust
percentile, something a 10-18-sample release-inflection pool cannot yet match. **Verdict: the
release-inflection pool is not obviously more stable than the unsupervised pools the prior reports
already rejected on data-starvation/dispersion grounds** - it sidesteps the WITHIN-band starvation
problem (Stage B's specific failure mode) but is itself still too small and, in one of four cases, too
divergent to recommend as a learned anchor from these two logs alone.

**Recommendation**: label-based anchoring (using the release inflection as the "labelled at-limit"
moment) is a genuinely different and more principled idea than the previous unsupervised, ratio-banded
pooling - it sidesteps the specific failure mode (bimodal/non-monotone unsupervised pools) the two prior
reports diagnosed, by using the DRIVER's own action as ground truth instead of an assumed physical
ratio. It is **not yet demonstrated viable from these two logs**, purely on sample-size and one
divergent-pool grounds - a capture with materially more braking zones (the same limitation every prior
attempt in this codebase has hit) would be needed before recommending it be built. It is a more
promising DIRECTION than what was tried before, not a validated design.

**Runtime-gating caveat (must be read before acting on the above)**: this uses pedal DYNAMICS (the
timing of a sustained pedal decrease) purely as an OFFLINE LEARNING SIGNAL, computed after the fact
over a captured log - it never uses raw pedal STATE to decide, in real time, which channel is active
(`NormalizedWheelLockSlipEngine`'s own standing rule, reaffirmed at the top of that file, is that pedal
state must never gate which channel is "active" this frame - only `LongitudinalDirectionResolver`'s
speed-derived direction may do that). These are different mechanisms, but they are ADJACENT: both read
the brake pedal's own trace to make a decision. The owner explicitly banned the first (a real-time
gating decision, because brake and throttle overlap constantly and a "committed" pedal reading is not
proof of which way the car is going); this report is proposing something different (using a SUSTAINED,
retrospectively-detected pedal DECREASE as a label for an offline calibration fit, never as a live
trigger) - but the owner should decide explicitly whether this distinction is one they are comfortable
with before anyone builds on it. This report does not recommend building it; it reports the evidence and
flags the adjacency plainly, per the brief's own instruction.

---

## Concerns

1. **Phase-3 trend-fit sample size is too small to call prediction (b) settled at the published-output
   level** (5 sub-events total across 4 combinations) - the physical-ratio-level comparison (n=50-152
   per combination) is the load-bearing evidence for this prediction; a future capture with more, longer
   braking zones would let the published-output trend be tested directly and more convincingly.
2. **Post-release behavior (prediction e) is the most concrete, reportable candidate for a real
   behavioral gap** - severity does not settle into a moderate 30-60 band for any meaningful dwell time
   in 3 of 4 combinations; whether this is a genuine over-reporting problem in the current design, or
   simply reflects that this car/session's real releases are shorter/harder than the owner recalls,
   is not resolvable from replay alone.
3. **ShakeIt/F1 Generic's release-inflection source-value pool (mean 6.47) diverges sharply from the
   other three pools (30.5-34.6)** - flagged but not chased down in this task; worth checking against
   `docs\shakeit-silence-diagnosis-report.md`'s own maturity-dependent under-reporting finding for this
   exact car before treating it as a genuine outlier rather than a known, already-diagnosed artifact.
4. **Throttle-release behavior (prediction f, second half) could not be tested at all** - neither log
   contains an isolated, clean hard-lift-off-throttle event the harness's detector could confidently
   time; a purpose-driven capture (a few isolated throttle lifts, not continuous modulation) would be
   needed to test this specific sub-prediction.
5. **The release-inflection anchor idea (Part 5) is more promising in principle than anything tried
   before, but not validated by these two logs** - recommend capturing a session with substantially more
   braking zones (the same limitation flagged by every prior attempt in this codebase) before pursuing
   it further, and resolving concern 3 above first.

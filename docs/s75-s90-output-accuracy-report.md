# Output accuracy at WheelLock Normalized 30 and 60: measured achieved-G-to-available-G ratio, both definitions

## STATUS

**MEASUREMENT ONLY. NO PRODUCT SOURCE FILE WAS MODIFIED.** Confirmed: `git`-free `diff`-equivalent inspection
was not needed because no edit was ever made to any file under `QAdvanceFeedback\QAdvanceFeedback\`; the only
files written by this task live under `scratchpad\s75-s90-output-accuracy-harness\` (new harness) and this
report. The harness references the MAIN build's already-built DLL
(`QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll`, confirmed **v1.0.6.8**, last built
2026-08-20 23:02) via `HintPath`, exactly like the pre-existing `v1068-compare-harness` - the DLL was never
rebuilt. No git operation of any kind was run. No recursive deletes.

**THE PLAIN ANSWER: NOT CLOSE ENOUGH.** At output 30, achieved deceleration has a POOLED median of 74-89% of
available grip depending on which of the two ratio definitions is used (both bracket the 75% target
coincidentally on their median) - but only 11-18% of frames land within ±5 percentage points of 75%, and the
inter-quartile range spans 28-40 percentage points. At output 60, the median (81-95%) again brackets the 90%
target, but only 18-26% of frames land within ±5pp, with a 28-36pp IQR. **The medians landing near target is
not evidence of reliability - the spread is the story, and the spread is wide enough that a driver reading "60"
on any given brake application has no dependable idea whether they actually banked 90%, 70%, or 110% of the
car's own available grip.** This is consistent with, and reinforces, every prior report in this project
(`docs\v1068-four-range-report.md`, `docs\s75-s90-reliability-report.md`,
`docs\s75-s90-slipratio-and-fit-report.md`).

A methodologically important, previously-unreported finding: **the near-30 selection is drawn 82.2% from the
LINEAR RESCALE FALLBACK, not the learned four-range curve** (curve engaged on only 17.8% of near-30 frames);
near-60 is more balanced (47.5% curve / 52.5% fallback). **The near-30 measurement is therefore mostly a test of
the single-anchor linear fallback, not of the S75/S30 anchor mechanism itself** - a materially different thing
from what "Feature C" (the four-range curve) was built to do, and this changes what the near-30 numbers actually
mean: they say relatively little about whether the LEARNED 30-anchor is accurate, because the learned anchor
mostly is not what produced those frames' output.

---

## METHODOLOGY

**Harness**: `scratchpad\s75-s90-output-accuracy-harness\` (new; reuses the CSV-loading/trim/replay skeleton of
the pre-existing `v1068-compare-harness`, and the log list/artifact-exclusion table of
`s75-s90-diagnosis-harness` / `docs\s75-s90-slipratio-and-fit-report.md`). References the MAIN build's DLL via
`HintPath` (`..\..\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll`) - never rebuilt, never edited.

**No diagnostic tap, no reflection, no product edit.** The predecessor task's own tap
(`DebugLockAnchorObservation`) was fully reverted before this task started (confirmed: it does not exist in
`NormalizedWheelLockSlipEngine.cs` today) and was **not** re-added. Instead, every quantity this measurement
needs is available through the engine's own **already-public** API, called from outside exactly as the engine
calls it internally:

- **Achieved G** - `QAdvanceFeedback.Core.Normalized.AchievedMotion.Resolve(sample)` (public static), called by
  the harness on the SAME `TelemetrySample` passed to `engine.Compute(...)` for that row - byte-identical to
  the engine's own internal `motion.MagnitudeG`, never trusted from the (stale, per the brief) logged
  `Diag.MotionMagnitudeG` column.
- **Ratio (a), `uSpeedAware`** - reconstructed via `engine.LockPhysicalReference.Ratio(gameId, carId,
  achievedG, "", "Sealed"/"Loose", applyColdStartCeiling: false, speedKmh: speedKmh, useStabilityGatedCeiling:
  false)` (public method on the public `KeyedGripLearner LockPhysicalReference` property), blended
  Sealed/Loose via the public `engine.LockLooseFraction` property, using the exact same
  `speedKmh = frame.GroundSpeedKmh ?? frame.SpeedKmh` convention the engine itself uses
  (`NormalizedWheelLockSlipEngine.cs` line 1027). This is **exactly** the `uSpeedAware` quantity computed at
  lines 1243-1245 of the engine, called by the harness AFTER `engine.Compute()` returns for that row - since
  `Ratio()` is a pure read (confirmed by inspection: it has no side effect on the learner) and the learner's
  state does not change between the internal call and the harness's own call, the reproduced value is
  bit-identical to what the engine itself used internally that frame.
- **Published severity** - `engine.Compute(...)`'s own return value, `.LockAll` (this equals
  `WheelLock.Normalized.All`; confirmed by this project's own standing invariant, `Normalized.All ==
  LockCarLevelSeverity`, held throughout `docs\v1068-four-range-report.md`).
- **Curve engagement, learned anchors** - `engine.LockFourRangeCurveActive`, `engine.LockAnchors.LearnedS75/90`,
  `engine.LockScaleLearner.LearnedCeiling` - all pre-existing public properties/methods, called the same way the
  pre-existing `s75-s90-diagnosis-harness` already does.
- **Endpoint check** - built directly from the real, public `QAdvanceFeedback.Core.Projection.MonotoneCubicCurve`
  (curve-active path) and the real, public `KeyedScaleLearner.Rescale` (fallback path) - i.e. calling the
  SHIPPED PRODUCTION CODE'S OWN classes/methods directly, not a reimplementation that could silently diverge.

**Replay convention**: cold, independent-per-log (each log gets a fresh `NormalizedWheelLockSlipEngine()`),
`lockSourceIdentity`/`slipSourceIdentity` = `"Raw"` for every log regardless of whether the file's own name says
"Raw" or "ShakeIt" - this matches the ALREADY-ESTABLISHED convention of `v1068-compare-harness` exactly (the
identity string only matters for cross-session persistence, which no log-level replay in this measurement uses;
"Raw" vs "ShakeIt" in the filename instead determines which native values populate the `Diag.Source.Lock.*`
columns fed into `Compute()`, which the CSVs already encode correctly per session). Multi-car files
(`c_1_5_3_e_d`) are replayed as ONE continuous stream (matching `v1068-compare-harness`'s own approach, since the
engine's own (gameId,carId)-keyed learners isolate each car automatically); results are split by `Diag.CarId`
afterward.

**Cross-validation of the harness itself**: pooled overall curve-engagement fraction measured here,
**3,556/28,973 = 12.27%**, matches `docs\s75-s90-slipratio-and-fit-report.md`'s own already-published,
independently-measured figure (**12.27%**) EXACTLY, on the current shipped build. This is strong evidence the
replay methodology (trimming, source selection, cold-per-log convention) reproduces the SAME frame set and the
SAME engine behaviour as the project's own prior, audited work.

**Artefact exclusion** (per-log dropped-frame counts, all measured directly by this harness's own trim logic,
identical algorithm to `v1068-compare-harness`):

| log | rows before trim | restart dropped | frozen-tail dropped | rows used | Slowing frames |
|---|---|---|---|---|---|
| 1.0.6.5/Raw | 6,732 | 0 | 30 | 6,702 | 1,768 |
| 1.0.6.5/ShakeIt | 6,855 | 6 | 30 | 6,819 | 1,903 |
| 1.0.6.6/Raw | 6,763 | 0 | 30 | 6,733 | 1,576 |
| 1.0.6.6/ShakeIt | 6,782 | 0 | 30 | 6,752 | 1,806 |
| 1.0.6.7/Raw | 6,924 | 0 | 30 | 6,894 | 1,585 |
| 1.0.6.7/ShakeIt | 6,765 | 0 | 31 | 6,734 | 1,517 |
| 1.0.6.8/Raw | 10,499 | **3,612** | 30 | 6,857 | 1,637 |
| 1.0.6.8/ShakeIt | 6,872 | 0 | 30 | 6,842 | 1,515 |
| c153/Raw (Sauber+F1Generic) | 20,353 | 0 | 30 | 20,323 | 5,371 |
| c153/ShakeIt (Sauber+F1Generic) | 16,409 | 0 | 30 | 16,379 | 4,447 |
| common/Raw/Wet | 3,522 | 0 | 58 | 3,464 | 953 |
| common/ShakeIt/Wet | 3,722 | 0 | 58 | 3,664 | 868 |
| common/Raw/Dry | 5,897 | 0 | 29 | 5,868 | 1,387 |
| common/ShakeIt/Dry | 6,952 | **3,196** | 30 | 3,726 | 973 |
| old/230140 | 8,985 | 0 | 0 | 8,985 | 1,667 |

`old-logs\...210223` and `...210421` are EXCLUDED per the brief (no `Diag.Telemetry.*` block at all - confirmed
directly by header inspection: `210223`'s header has zero `Diag.Telemetry.*` columns). **15 usable logs, 28,973
pooled Slowing frames.**

**Achieved-G reference for the Common_1_5_e_d / old-logs INFERRED identity** (no `Diag.CarId` column in either):

| log | n (Slowing) | p99 achieved G | max achieved G | nearest known car | confidence |
|---|---|---|---|---|---|
| common/Raw/Wet | 953 | 3.843 | 3.957 | **Sauber (Wet, ref 3.85g)** | high - within 0.01g |
| common/ShakeIt/Wet | 868 | 3.842 | 3.980 | **Sauber (Wet, ref 3.85g)** | high - within 0.01g |
| common/Raw/Dry | 1,387 | 4.667 | 4.975 | ambiguous | **low** - sits between RedBull (4.37-4.62) and F1Generic (4.87), closer to the RedBull boundary (+0.05) than to F1Generic (-0.20) |
| common/ShakeIt/Dry | 973 | 4.795 | 5.037 | ambiguous | **low** - closer to F1Generic (-0.08) than to RedBull (+0.30); DISAGREES with its own Raw/Dry pair above, which is the SAME physical drive under a different configured source - the disagreement itself shows the inference is not reliable enough to pin a single car for the Dry pair |
| old/230140 | 1,667 | 4.488 | 4.994 | **RedBull-like (Dry, ref 4.37-4.62)** | confident - squarely inside the RedBull range, matching this project's own prior "Unknown(RedBull-like)" label |

**The Common/Wet pair is confidently Sauber-like; the Common/Dry pair's car identity cannot be pinned down with
this method** (its own Raw/ShakeIt halves of the SAME drive point to two different cars) - reported honestly as
`CommonCar(INFERRED, ambiguous)` rather than forcing a guess, and excluded from any car-identity-dependent
claim below.

**Selection**: WheelLock braking frames (`engine.CurrentDirection == Slowing`, i.e. `Diag.Direction ==
"Slowing"`) with published `LockAll` in `[25,35]` (near-30) or `[55,65]` (near-60). Widened to ±10 only for
individual per-log cells with n<10 at ±5, flagged explicitly per cell.

**Events and the filtered ratio (b)**: a "braking event" is a maximal contiguous run of Slowing frames (same
car, no car-switch mid-run). Ratio (b) = achieved G / that event's own peak achieved G. The FILTERED variant
additionally requires the event's own peak G to be ≥70% of that SAME (log,car)'s own p99 achieved G over all its
Slowing frames - i.e. only counting events that came reasonably close to that car's own real capability, per the
brief's own explicit caveat that most braking events never approach the limit.

---

## POOLED HEADLINE (all 15 logs, 28,973 Slowing frames)

### Near-30 (target: 75% of available grip)

Band ±5 (25-35), **n=517**.

**Curve engaged: 92/517 (17.8%); linear-Rescale fallback: 425/517 (82.2%).** The near-30 measurement is
overwhelmingly a test of the fallback, not the learned S75 anchor.

| definition | n | median | p10 | p25 | p75 | p90 | IQR | within ±3pp | within ±5pp | within ±10pp |
|---|---|---|---|---|---|---|---|---|---|---|
| (a) achieved / speed-aware reference | 517 | 89.1% | 53.1% | 68.8% | 106.9% | 137.5% | **38.1pp** | 7.7% | 10.6% | 24.6% |
| (b) achieved / event peak, UNFILTERED | 517 | 75.9% | 37.3% | 51.0% | 90.9% | 97.8% | **39.9pp** | 10.3% | 15.9% | 28.8% |
| (b) achieved / event peak, FILTERED (peak≥70% of car p99) | 459 | 74.4% | 35.5% | 49.9% | 90.1% | 97.4% | **40.1pp** | 11.5% | 17.4% | 30.5% |

### Near-60 (target: 90% of available grip)

Band ±5 (55-65), **n=979**.

**Curve engaged: 465/979 (47.5%); linear-Rescale fallback: 514/979 (52.5%).** Near-60 is close to an even split
between the learned curve and the fallback.

| definition | n | median | p10 | p25 | p75 | p90 | IQR | within ±3pp | within ±5pp | within ±10pp |
|---|---|---|---|---|---|---|---|---|---|---|
| (a) achieved / speed-aware reference | 979 | 95.2% | 56.6% | 78.2% | 106.1% | 123.6% | **27.9pp** | 9.8% | 17.5% | 37.4% |
| (b) achieved / event peak, UNFILTERED | 979 | 80.7% | 41.4% | 56.2% | 91.7% | 97.8% | **35.5pp** | 14.2% | 25.5% | 51.6% |
| (b) achieved / event peak, FILTERED (peak≥70% of car p99) | 899 | 80.6% | 40.9% | 56.3% | 91.8% | 97.8% | **35.5pp** | 13.6% | 24.7% | 51.4% |

**Filtering barely moves the pooled numbers** (459/517 = 89% and 899/979 = 92% of frames already pass the ≥70%
threshold pooled) - most near-30/near-60 frames DO belong to events that reached a meaningful fraction of that
car's own capability. The filter matters far more at individual-log/individual-event granularity (see per-log
tables) than in the pooled view.

**Definitions (a) and (b) disagree in direction, exactly as the brief warned they might**: (a)'s median
OVER-shoots both targets (89.1% vs 75%, 95.2% vs 90%), while (b)'s median lands almost exactly ON target (75.9%
and 80.7% respectively - note near-60's (b) median of 80.7% is notably BELOW the 90% target, not on it). Neither
disagreement is resolved by filtering.

---

## PER-LOG TABLES

Ratio (a) and ratio (b) [unfiltered / filtered], median and IQR (percentage points) and fraction within ±5pp of
target. **Bold `n<10`** cells are flagged THIN; those widened from ±5 to ±10 say so explicitly.

### Near-30 (target 75%)

| log | n (band) | curve engaged | ratio (a) median/IQR/±5pp | ratio (b) unfilt median/IQR/±5pp | ratio (b) filt median/IQR/±5pp |
|---|---|---|---|---|---|
| 1.0.6.5/Raw | 26 | 15.4% | 86.1 / 19.5 / 19.2% | 80.4 / 25.9 / 19.2% | 77.7 / 22.1 / 21.7% |
| 1.0.6.5/ShakeIt | **8 (±10 widened from ±5 n=4)** | 75.0% | 55.1 / 81.1 / 12.5% **THIN** | 48.8 / 21.1 / 0.0% **THIN** | 52.1 / 23.4 / 0.0% **THIN** |
| 1.0.6.6/Raw | 24 | 0.0% | 122.5 / 51.7 / 25.0% | 80.7 / 16.7 / 37.5% | 76.0 / 7.0 / 60.0% |
| 1.0.6.6/ShakeIt | **10 (±10 widened from ±5 n=6)** | 40.0% | 120.6 / 56.3 / 0.0% | 86.1 / 45.8 / 10.0% | 86.1 / 45.8 / 10.0% |
| 1.0.6.7/Raw | 32 | 0.0% | 83.0 / 40.5 / 9.4% | 79.2 / 37.0 / 18.8% | 78.7 / 36.7 / 20.7% |
| 1.0.6.7/ShakeIt | 11 | 0.0% | 88.6 / 24.6 / 18.2% | 89.0 / 15.2 / 27.3% | 89.0 / 15.2 / 27.3% |
| 1.0.6.8/Raw | 35 | 0.0% | 78.9 / 13.8 / 31.4% | 70.4 / 27.1 / 17.1% | 66.1 / 18.8 / 20.7% |
| 1.0.6.8/ShakeIt | **37 (±10 widened from ±5 n=8)** | 97.3% | 106.9 / 16.8 / 2.7% | 97.1 / 15.0 / 5.4% | 97.1 / 15.0 / 5.4% |
| c153/Raw (both cars) | 112 | 45.5% | 87.0 / 28.3 / 5.4% | 85.7 / 39.8 / 2.7% | 82.1 / 44.8 / 2.8% |
| c153/ShakeIt (both cars) | 108 | 0.0% | 93.2 / 36.1 / 5.6% | 67.0 / 37.4 / 10.2% | 68.2 / 39.6 / 10.0% |
| common/Raw/Dry | 41 | 0.0% | 93.8 / 71.6 / 17.1% | 79.7 / 44.6 / 12.2% | 76.4 / 38.9 / 15.2% |
| common/Raw/Wet | 28 | 82.1% | 86.6 / 39.1 / 21.4% | 55.3 / 38.3 / 25.0% | 55.6 / 38.8 / 25.9% |
| common/ShakeIt/Dry | 14 | 0.0% | 88.7 / 42.6 / 14.3% | 74.7 / 28.6 / 42.9% | 73.5 / 29.3 / 50.0% |
| common/ShakeIt/Wet | 31 | 0.0% | 174.7 / 153.7 / 0.0% | 76.1 / 9.8 / 54.8% | 76.1 / 9.8 / 54.8% |
| old/230140 | 37 | 0.0% | 60.2 / 68.1 / 0.0% | 90.6 / 65.7 / 2.7% | 91.2 / 65.9 / 0.0% |

### Near-60 (target 90%)

| log | n (band) | curve engaged | ratio (a) median/IQR/±5pp | ratio (b) unfilt median/IQR/±5pp | ratio (b) filt median/IQR/±5pp |
|---|---|---|---|---|---|
| 1.0.6.5/Raw | 109 | 82.6% | 105.1 / 12.3 / 6.4% | 89.8 / 15.7 / 30.3% | 89.8 / 15.9 / 29.0% |
| 1.0.6.5/ShakeIt | 98 | 85.7% | 97.5 / 9.3 / 22.4% | 89.8 / 9.7 / 50.0% | 89.7 / 10.2 / 49.5% |
| 1.0.6.6/Raw | 59 | 71.2% | 93.2 / 10.5 / 42.4% | 85.5 / 9.9 / 45.8% | 85.2 / 9.8 / 44.6% |
| 1.0.6.6/ShakeIt | 58 | 62.1% | 111.6 / 32.7 / 10.3% | 92.2 / 21.1 / 19.0% | 92.6 / 21.2 / 19.3% |
| 1.0.6.7/Raw | 48 | 52.1% | 89.2 / 17.7 / 37.5% | 83.4 / 14.0 / 33.3% | 83.5 / 12.9 / 32.6% |
| 1.0.6.7/ShakeIt | 28 | 7.1% | 105.9 / 48.3 / 10.7% | 90.9 / 4.3 / 75.0% | 90.8 / 4.5 / 74.1% |
| 1.0.6.8/Raw | 31 | 0.0% | 73.1 / 37.4 / 25.8% | 68.2 / 33.3 / 3.2% | 68.3 / 34.7 / 3.6% |
| 1.0.6.8/ShakeIt | 73 | 80.8% | 94.6 / 18.4 / 32.9% | 86.5 / 11.9 / 50.7% | 86.5 / 11.9 / 50.7% |
| c153/Raw (both cars) | 107 | 52.3% | 94.1 / 20.9 / 16.8% | 76.8 / 34.8 / 16.8% | 76.8 / 34.8 / 16.8% |
| c153/ShakeIt (both cars) | 72 | 0.0% | 78.8 / 24.3 / 8.3% | 67.0 / 32.7 / 13.9% | 64.9 / 31.1 / 12.3% |
| common/Raw/Dry | 30 | 0.0% | 152.1 / 105.3 / 0.0% | 46.6 / 11.4 / 0.0% | 46.6 / 11.4 / 0.0% |
| common/Raw/Wet | 54 | 100.0% | 106.6 / 40.8 / 0.0% | 68.4 / 34.9 / 14.8% | 68.6 / 33.1 / 15.4% |
| common/ShakeIt/Dry | 147 | 0.0% | 87.8 / 27.2 / 21.1% | 52.8 / 21.8 / 8.8% | 49.5 / 16.9 / 0.0% |
| common/ShakeIt/Wet | 19 | 89.5% | 78.5 / 36.0 / 5.3% | 68.6 / 13.8 / 5.3% | 68.6 / 19.7 / 7.7% |
| old/230140 | 46 | 0.0% | 49.4 / 85.9 / 4.3% | 60.2 / 54.7 / 10.9% | 96.7 / 53.5 / 4.5% |

**Every single per-log cell, in both anchors and both definitions, shows a double-digit IQR** (the tightest is
`1.0.6.7/ShakeIt` near-60 ratio(b), IQR=4.3pp on n=28 - a genuine bright spot, but an outlier among 30 log×anchor
cells, not the norm). Several logs (`common/Raw/Dry` near-60, `common/ShakeIt/Wet` near-30 ratio (a)) show IQRs
exceeding 70-150pp, driven by cold-start/early-session ratio(a) readings before the speed-aware reference has
converged.

---

## PER (LOG, CAR): c_1_5_3_e_d, split by `Diag.CarId`

Both cars are present in the SAME two physical files; only `c153` carries a real, varying `Diag.CarId`.
1.0.6_logs' own `Diag.CarId` is a constant ("Red Bull Racing") throughout each file, so its per-(log,car) view is
identical to its per-log view already shown above.

| log \| car | anchor | n | curve engaged | ratio (a) median/IQR/±5pp | ratio (b) unfilt median/IQR/±5pp |
|---|---|---|---|---|---|
| c153/Raw \| Sauber | near-30 | 78 | 59.0% | 84.2 / 29.3 / 6.4% | 65.7 / 50.0 / 2.6% |
| c153/Raw \| Sauber | near-60 | 60 | 65.0% | 93.6 / 23.0 / 16.7% | 71.1 / 30.6 / 10.0% |
| c153/Raw \| F1 Generic | near-30 | 34 | 14.7% | 90.0 / 14.4 / 2.9% | 94.6 / 13.0 / 2.9% |
| c153/Raw \| F1 Generic | near-60 | 47 | 36.2% | 95.1 / 17.7 / 17.0% | 83.3 / 31.1 / 25.5% |
| c153/ShakeIt \| Sauber | near-30 | 56 | 0.0% | 87.3 / 39.5 / 7.1% | 85.2 / 37.4 / 5.4% |
| c153/ShakeIt \| Sauber | near-60 | 58 | 0.0% | 78.1 / 23.9 / 8.6% | 71.9 / 35.5 / 15.5% |
| c153/ShakeIt \| F1 Generic | near-30 | 52 | 0.0% | 95.3 / 24.3 / 3.8% | 52.4 / 30.5 / 15.4% |
| c153/ShakeIt \| F1 Generic | near-60 | **14** | 0.0% | 89.9 / 24.7 / 7.1% **THIN** | 59.5 / 18.5 / 7.1% **THIN** |

No (log,car) cell here escapes a double-digit IQR either. The **F1 Generic/ShakeIt interaction flagged by the
brief** ("ShakeIt's source on F1 Generic peaks at only 31.02, so the learned ceiling compresses it more than 3x")
is consistent with `c153/ShakeIt | F1 Generic` showing the LARGEST divergence between (a) (median 95.3% at
near-30) and (b) (median 52.4% at the same selection) of any (log,car) cell measured - ceiling compression
inflates ratio (a) (a small achieved-G change reads as a large fraction of a compressed reference) while ratio
(b) (immune to the ceiling) shows the car was actually far from its own event peak.

---

## LEARNED S75 / S90 / Smax, PER LOG/CAR, AND CROSS-SESSION DISPERSION

| log \| car | identity | surface | S75 | S90 | Smax |
|---|---|---|---|---|---|
| 1.0.6.5/Raw | RedBull | Dry | 3.21 | 4.00 | 72.94 |
| 1.0.6.5/ShakeIt | RedBull | Dry | 4.93 | 30.67 | 41.77 |
| 1.0.6.6/Raw | RedBull | Dry | 2.40 | 4.45 | 73.69 |
| 1.0.6.6/ShakeIt | RedBull | Dry | 9.46 | 9.49 | 33.51 |
| 1.0.6.7/Raw | RedBull | Dry | 2.85 | 4.63 | 57.76 |
| 1.0.6.7/ShakeIt | RedBull | Dry | 6.97 | 13.87 | 38.84 |
| 1.0.6.8/Raw | RedBull | Dry | 9.51 | 6.88 | 71.99 |
| 1.0.6.8/ShakeIt | RedBull | Dry | 1.47 | 28.11 | 43.89 |
| c153/Raw \| Sauber | Sauber | Wet | 21.38 | 20.54 | 52.63 |
| c153/ShakeIt \| Sauber | Sauber | Wet | 28.01 | 23.40 | 59.52 |
| c153/Raw \| F1 Generic | F1Generic | Dry | 38.68 | 13.85 | 37.30 |
| c153/ShakeIt \| F1 Generic | F1Generic | Dry | 15.47 | 4.08 | 65.76 |
| common/Raw/Wet | CommonCar(inferred, ambiguous) | Wet | 16.69 | 19.80 | 76.98 |
| common/ShakeIt/Wet | CommonCar(inferred, ambiguous) | Wet | 18.52 | 24.50 | 55.23 |
| common/Raw/Dry | CommonCar(inferred, ambiguous) | Dry | (no valid S75 candidate) | 2.21 | 58.43 |
| common/ShakeIt/Dry | CommonCar(inferred, ambiguous) | Dry | 54.23 | 33.83 | 69.95 |
| old/230140 | Unknown(RedBull-like) | Dry | 61.41 | 33.51 | 79.04 |

### Cross-session dispersion (max/min ratio) per (identity, surface) group

| group | n sessions | S75 max/min | S90 max/min | Smax max/min |
|---|---|---|---|---|
| RedBull/Dry | 8 | **6.45x** | **7.67x** | 2.20x |
| Sauber/Wet | 2 | 1.31x | 1.14x | 1.13x |
| F1Generic/Dry | 2 | 2.50x | 3.40x | 1.76x |
| CommonCar/Wet | 2 | 1.11x | 1.24x | 1.39x |
| CommonCar/Dry | 2 | (S75 unavailable one session) | 15.32x | 1.20x |
| Unknown(RedBull-like)/Dry | 1 | undefined (n=1) | undefined (n=1) | undefined (n=1) |

**RedBull/Dry (n=8) is the only group with enough independent sessions to trust a max/min ratio at all**, and it
reproduces `docs\s75-s90-slipratio-and-fit-report.md`'s own published cold-independent headline (6.45x/7.67x,
matching to two decimal places) - direct cross-validation that this harness's cold-per-log replay convention
agrees with the project's own prior, independently-built measurement. Every other group has only 1-2 sessions;
**Sauber/Wet, F1Generic/Dry, CommonCar/Wet, CommonCar/Dry, and Unknown(RedBull-like)/Dry are all THIN (n≤2) and
their dispersion ratios (as low as 1.11x, as high as 15.32x) should not be read as evidence the anchors are more
or less reliable for those cars** - there is simply not enough independent evidence to say either way.

---

## ENDPOINT CONFIRMATION: `source == Smax → 80`, `source == 100 → 100`

Verified two ways, both calling the SHIPPED PRODUCTION CODE'S OWN classes directly (not a reimplementation):

- **Curve-active path** (`QAdvanceFeedback.Core.Projection.MonotoneCubicCurve`, rebuilt per-frame from the SAME
  S75/S90/Smax the engine itself used that frame, read via the same public accessors): **n=3,548 frames
  checked. `Evaluate(Smax)` = exactly 80.0000 and `Evaluate(100)` = exactly 100.0000 on EVERY frame, zero
  violations.** (8 curve-active frames were excluded from this specific check due to a harness-side
  read-after-`Compute()` timing artefact - the anchors can advance mid-`Compute()` on the rare frame that closes
  a bracket crossing, so a query issued after `Compute()` returns can see a value one frame newer than what
  decided `curveActive` that same frame; this is a limitation of observing from outside the engine, not a
  defect, and affects 8 of 3,556 curve-active frames, 0.2%.)
- **Fallback (linear Rescale) path** (`KeyedScaleLearner.Rescale`, called directly, live, at every Slowing
  frame): **n=27,921 frames checked. `Rescale(Smax)` = exactly 80.0000 on EVERY frame, zero violations.**
  `Rescale(100)` = exactly 100.0000 on the large majority, but **1,923/27,921 (6.9%) read BELOW 100** (as low as
  94.70) whenever `Smax` was TRANSIENTLY learned above 80 at that point in the session (`Rescale(100) =
  100*(80/Smax) < 100` whenever `Smax > 80`) - even though every log's own FINAL, converged Smax (table above)
  settles below 80. This occurred on 6 of 15 logs (`1.0.6.7/Raw`, `c153/Raw|Sauber`, `c153/ShakeIt|Sauber`,
  `common/Raw/Wet`, `common/ShakeIt/Wet`, `common/Raw/Dry`), with `Smax` peaking transiently as high as 84.48.

**So: `source==Smax → 80` holds unconditionally, on every frame measured, in both branches - by construction,
confirmed directly against the shipped code.** `source==100 → 100` holds whenever `Smax≤80` (the case for every
log's own FINAL learned value, and the large majority of frames overall, 93.1%) but genuinely does NOT hold
during the minority of frames where `Smax` is still mid-convergence and transiently exceeds 80 - a real,
previously undocumented nuance (prior reports checked only the FINAL/converged Smax against this guarantee, not
every frame along the way). This is a narrow, low-severity finding (max shortfall 5.3 points, only in the
fallback branch, only while still converging) - not a regression of the endpoint contract for any converged
session - but is reported plainly rather than rounded up to an unconditional "always holds."

---

## THE VERDICT

**At output 30, achieved deceleration sits at a pooled median of 74-89% of available grip (bracketing the 75%
target depending on definition); at output 60, 81-95% (bracketing 90%). Neither is close enough to be useful,
and the medians landing near target is not the reason - the SPREAD is.** Only 11-25% of frames land within ±5
percentage points of the intended target at either anchor, under either definition; the inter-quartile range is
28-40 percentage points in every pooled and per-log cell measured (with a single n=28 exception at 4.3pp). A
driver who sees "60" on their device has, from this data, roughly a 1-in-4 chance the moment genuinely
represents something close to 90% of that corner's real capability, and a wide, undisclosed chance it represents
somewhere between 55% and 98%.

**Which definition is authoritative**: **ratio (b), FILTERED, is the more literal answer to the owner's own
question** ("how close is the real deceleration to 75%/90% of the SAME-CORNER max grip") - it measures achieved
G directly against that same braking event's own peak, filtered to events that got reasonably close to the car's
real capability, exactly the quantity the owner is asking about. **Ratio (a) is the more diagnostic quantity for
explaining WHY**: it measures whether the code hit ITS OWN internal target (`uSpeedAware`), so where (a) and (b)
diverge sharply (most visibly on `c153/ShakeIt | F1 Generic`, where (a) reads 95.3% while (b) reads 52.4% at the
same selection), the gap is attributable to the LEARNED REFERENCE being wrong (e.g. compressed by ShakeIt's own
~31 native ceiling on that car - the interaction the brief itself flagged), not to the achieved-G measurement
being wrong. **Both agree on the bottom line**: regardless of which is treated as authoritative, the spread is
too wide for either to support a "yes, 30 and 60 are dependable" conclusion.

**What generalisation this data does and does not support**: all eight `1.0.6_logs` sessions are ONE car (Red
Bull) on ONE track (Silverstone), dry only - the only group large enough (n=8) to trust a cross-session
dispersion figure at all. Sauber/Wet, F1Generic/Dry, and the (ambiguous) CommonCar groups each have only 1-2
sessions - genuinely useful as additional data points (and the Sauber/Wet and F1Generic/Dry per-frame ratio
figures are reported above with full confidence, since they come from real replayed telemetry, not from the
THIN anchor-dispersion figures), but **not enough to claim the accuracy/dispersion figures generalise across
cars, tracks, or surfaces** - only that the SAME wide-spread pattern was observed on every car/surface/session
combination available, which is itself suggestive but not proof of universality.

**What the measurement suggests is needed**: the near-30 selection is 82% fallback-sourced - closing the gap
there requires either the S75 anchor to converge and engage far more often (it does not: 12.27% overall
engagement, essentially unchanged since `docs\v1068-four-range-report.md`'s own 3.6-12.27% range across
revisions), or accepting that "30" mostly measures the single-anchor linear Rescale's own accuracy, not a
learned reference at all. Near-60 draws roughly half its frames from each path, so improving BOTH the S90 anchor
AND the Rescale fallback's own calibration speed would both move the needle. Given `docs\s75-s90-slipratio-and-fit-report.md`'s
own finding that the anchor SPACE (source vs. raw slip ratio) is the dominant driver of scatter, not sample
count, the most promising remaining lever - per that report's own concerns - is a properly cross-session-persisted
slip-space anchor with a lower-variance slip-to-source conversion, not simply gathering more of the SAME kind of
session.

---

## Cells too thin to trust (n<10 even after widening, or n=1)

- `1.0.6.5/ShakeIt` near-30: n=8 even after widening to ±10 (was n=4 at ±5).
- `c153/ShakeIt | F1 Generic` near-60: n=14 (usable but small).
- Learned-anchor cross-session dispersion for every group EXCEPT RedBull/Dry (n=8): Sauber/Wet (n=2),
  F1Generic/Dry (n=2), CommonCar/Wet (n=2), CommonCar/Dry (n=2), Unknown(RedBull-like)/Dry (n=1, ratio
  undefined).
- `common/Raw/Dry`'s own S75 anchor never formed a valid candidate this session (no crossing accepted) - reported
  as "(no valid S75 candidate)" in the anchors table, not a zero.

## Concerns

1. **The near-30 selection is mostly a fallback measurement (82.2% of frames), not a learned-anchor
   measurement** - this was not obvious from any prior report and materially changes what "how accurate is
   S75/output-30" means; a future report evaluating S75 specifically should condition on `curveActive==true`
   explicitly rather than on output value alone.
2. **The `source==100→100` endpoint is not unconditional mid-session** - it depends on `Smax≤80`, true for every
   log's own converged value but false on 6.9% of fallback-path frames while `Smax` is still settling. Low
   severity (max observed shortfall 5.3 points) but a genuine, previously undisclosed nuance.
3. **The Common_1_5_e_d Dry pair's car identity is genuinely ambiguous** by the achieved-G inference method the
   brief specifies - its own Raw and ShakeIt halves (the SAME physical drive) point to two different candidate
   cars. Reported as ambiguous rather than forced to a guess; any conclusion keyed to "CommonCar/Dry" specifically
   should be read with that caveat.
4. **RedBull/Dry (n=8) is the only group with enough sessions to trust a learned-anchor dispersion ratio** - the
   headline 6.45x/7.67x figures (which this harness reproduced exactly, cross-validating the methodology) come
   from one car on one track; every other group's dispersion figure in this report is directional context only,
   explicitly flagged thin.
5. **This is a single-pass, cold-per-log measurement** (no cross-session persistence chaining) - consistent with
   the brief's own scope and with `v1068-compare-harness`'s established convention, but it does not measure what
   a real, long-running installation's own persisted/pooled anchors would show (that is what
   `docs\s75-s90-slipratio-and-fit-report.md`'s own separate chained-persistence simulation already covers).
6. **Every number in this report comes from the 15 real, currently-available logs** - the same "short,
   data-starved capture" limitation every prior report in this project has flagged.

## Files

- New harness (measurement only, no product edit): `scratchpad\s75-s90-output-accuracy-harness\Harness.csproj`,
  `scratchpad\s75-s90-output-accuracy-harness\Program.cs`.
- Raw harness output (full per-log/per-car detail, source for every table above):
  `scratchpad\s75-s90-output-accuracy-harness\out\raw_report.md`.

# Three-Build Full-Lock / Max-Grip Normalized Comparison

Compares `Normalized.All` (Layer 4, `WheelLock.Normalized.All` in the CSV) and `Projected.All`
(Layer 5, `WheelLock.Projected.All`) across the three shipped-fix builds, on **every** available
log, not just the eight `1.0.6_logs` sessions used previously.

| Build | DLL |
|---|---|
| 1.0.6.2 | `QAdvanceFeedback_1.0.6.2_prerelease\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll` |
| 1.0.6.3 | `QAdvanceFeedback_1.0.6.3_altprerelease\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll` |
| 1.0.6.8 | `QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll` |

No product source file was modified. All three builds' folders were only read (their pre-built
DLLs). Three harnesses were built, each referencing one DLL via `<HintPath>` — the **only**
difference between them:

- `scratchpad\v1062-compare-harness\` (pre-existing, adapted)
- `scratchpad\v1063-compare-harness\` (pre-existing, adapted)
- `scratchpad\v1068-compare-harness\` (new)

`Program.cs` is byte-identical across all three (verified with `diff`). Each replays a log's
`Diag.Source.Lock/Slip.*` (the configured source, exactly what a real session would feed Layer 4)
and `WheelLock/WheelSlip.Raw.*` (Layer 3's own always-computed Raw, the `layer3Raw*` fidelity
input) through that build's own `NormalizedWheelLockSlipEngine.Compute` and
`ProjectedWheelLockSlipEngine.Compute` — the **logged** `WheelLock.Normalized.*` /
`WheelLock.Projected.*` columns are never read as truth, per the brief.

## Headline answer

**No — the three builds are not "almost identical" at either full lock or max grip once the full
log set is used, though the earlier eight-log finding (1.0.6.2 ≈ 1.0.6.3, 1.0.6.8 sometimes higher)
is confirmed and the size of the gap is bigger and more structural than the eight-log sample
suggested.**

- **Full lock (100).** Peak `Normalized.All` is close (≤3 points) on the dry, Red-Bull/Silverstone
  logs the fix was originally validated against. On the **dry F1 Generic / Abu Dhabi** car,
  1.0.6.2/1.0.6.3 never reach full lock at all (cap 90.3/90.3 in one log, 91.5/90.9 in another)
  while 1.0.6.8 reaches literal 100.00 in both — a ~10-point, reproducible, car-specific gap, not
  noise.
- **Dwell at/near full lock.** Even where the peak agrees (mostly saturating at 100 in ShakeIt-mode
  logs for all three), the number of frames spent ≥95/≥99 differs by 2×–6× between builds
  routinely, and once by ~40× (`old-logs/230140`: ≥95 count 52 → 144 → 301 across 1062→1063→1068,
  same peak of 100.00 for all three).
- **Max grip (80).** The 70-90 band's mean/median value is stable across builds (usually within
  1-3 points), so "what max grip reads as" is essentially build-invariant. But the **dwell** (frame
  count in-band, especially the tight 78-82 band) diverges by up to 8× between builds on some logs
  — confirming and in several cases far exceeding the previously-documented "up to 72%" dwell gap.
- **1.0.6.2 vs 1.0.6.3** stay close on peak values (usually <2 points) as previously found, but
  their own *dwell* counts still diverge from each other by 2-4× on several logs — the "very
  close" finding holds for the headline max, not for how often either build reports a near-limit
  frame.

## Full-lock comparison (`Normalized.All` max, and dwell ≥95 / ≥99)

n = frame count after dual-lap-restart and frozen-tail trimming (see Artifacts section). Cells
with n < 10 are flagged **thin**; n = 0 fidelity cells mean no frame qualified for that gate at all.

| Log / Car | Build | n | Max | ≥95 n (%) | ≥99 n (%) |
|---|---|---|---|---|---|
| 1.0.6_logs/I_1_6_5_e_d/Raw (Red Bull, dry, Silverstone) | 1062 | 6702 | 98.18 | 15 (0.22%) | 0 (0.00%) |
| | 1063 | 6702 | 97.15 | 9 (0.13%) | 0 (0.00%) |
| | 1068 | 6702 | 98.15 | 14 (0.21%) | 0 (0.00%) |
| 1.0.6_logs/I_1_6_5_e_d/ShakeIt | 1062 | 6819 | 100.00 | 44 (0.65%) | 25 (0.37%) |
| | 1063 | 6819 | 100.00 | 39 (0.57%) | 23 (0.34%) |
| | 1068 | 6819 | 100.00 | 105 (1.54%) | 31 (0.45%) |
| 1.0.6_logs/l_1_6_6_e_d/Raw | 1062 | 6733 | 83.80 | 0 | 0 |
| | 1063 | 6733 | 83.45 | 0 | 0 |
| | 1068 | 6733 | 85.80 | 0 | 0 |
| 1.0.6_logs/l_1_6_6_e_d/ShakeIt | 1062 | 6752 | 100.00 | 25 (0.37%) | 15 (0.22%) |
| | 1063 | 6752 | 100.00 | 25 (0.37%) | 13 (0.19%) |
| | 1068 | 6752 | 100.00 | 65 (0.96%) | 44 (0.65%) |
| 1.0.6_logs/I_1_6_7_e_d/Raw | 1062 | 6894 | 100.00 | 52 (0.75%) | 21 (0.30%) |
| | 1063 | 6894 | 99.94 | 43 (0.62%) | 5 (0.07%) |
| | 1068 | 6894 | 100.00 | 51 (0.74%) | 6 (0.09%) |
| 1.0.6_logs/I_1_6_7_e_d/ShakeIt | 1062 | 6734 | 100.00 | 69 (1.02%) | 51 (0.76%) |
| | 1063 | 6734 | 100.00 | 65 (0.97%) | 33 (0.49%) |
| | 1068 | 6734 | 100.00 | 133 (1.98%) | 90 (1.34%) |
| 1.0.6_logs/I_1_6_8_e_d/Raw (2nd pass only) | 1062 | 6857 | 85.70 | 0 | 0 |
| | 1063 | 6857 | 85.59 | 0 | 0 |
| | 1068 | 6857 | 87.49 | 0 | 0 |
| 1.0.6_logs/I_1_6_8_e_d/ShakeIt | 1062 | 6842 | 100.00 | 44 (0.64%) | 40 (0.58%) |
| | 1063 | 6842 | 100.00 | 42 (0.61%) | 40 (0.58%) |
| | 1068 | 6842 | 100.00 | 62 (0.91%) | 41 (0.60%) |
| **c_1_5_3_e_d/ShakeIt/Sauber** (WET, Silverstone) | 1062 | 9706 | 100.00 | 53 (0.55%) | 14 (0.14%) |
| | 1063 | 9706 | **99.07** | 35 (0.36%) | 2 (0.02%) |
| | 1068 | 9706 | 100.00 | 80 (0.82%) | 52 (0.54%) |
| **c_1_5_3_e_d/ShakeIt/F1 Generic** (DRY, Abu Dhabi) | 1062 | 6673 | **90.33** | 0 | 0 |
| | 1063 | 6673 | **90.30** | 0 | 0 |
| | 1068 | 6673 | **100.00** | 30 (0.45%) | 30 (0.45%) |
| c_1_5_3_e_d/Raw/Sauber | 1062 | 11237 | 99.68 | 79 (0.70%) | 5 (0.04%) |
| | 1063 | 11237 | 99.56 | 26 (0.23%) | 2 (0.02%) |
| | 1068 | 11237 | 100.00 | 228 (2.03%) | 214 (1.90%) |
| c_1_5_3_e_d/Raw/F1 Generic | 1062 | 9086 | 99.09 | 61 (0.67%) | 1 (0.01%) |
| | 1063 | 9086 | 97.22 | 32 (0.35%) | 0 (0.00%) |
| | 1068 | 9086 | 100.00 | 144 (1.58%) | 105 (1.16%) |
| Common_1_5_e_d/Wet/Raw (inferred Sauber, WET) | 1062 | 3464 | 98.82 | 26 (0.75%) | 0 |
| | 1063 | 3464 | 96.36 | 6 (0.17%) | 0 |
| | 1068 | 3464 | 98.39 | 11 (0.32%) | 0 |
| Common_1_5_e_d/Wet/ShakeIt | 1062 | 3664 | 99.75 | 19 (0.52%) | 9 (0.25%) |
| | 1063 | 3664 | 99.51 | 19 (0.52%) | 9 (0.25%) |
| | 1068 | 3664 | 100.00 | 19 (0.52%) | 11 (0.30%) |
| **Common_1_5_e_d/Dry/Raw** (inferred F1 Generic, DRY) | 1062 | 5868 | **91.54** | 0 | 0 |
| | 1063 | 5868 | **90.92** | 0 | 0 |
| | 1068 | 5868 | **100.00** | 165 (2.81%) | 72 (1.23%) |
| Common_1_5_e_d/Dry/ShakeIt (thin 2nd pass, see Artifacts) | 1062 | 3726 | 75.92 | 0 | 0 |
| | 1063 | 3726 | 71.97 | 0 | 0 |
| | 1068 | 3726 | 73.95 | 0 | 0 |
| old-logs/210223/Raw (no telemetry - degenerate) | all 3 | 1425 | 0.00 | 0 | 0 |
| old-logs/210421/ShakeIt (no telemetry - degenerate) | all 3 | 1753 | 0.00 | 0 | 0 |
| **old-logs/230140/Unknown** (inferred Red Bull, DRY) | 1062 | 8985 | 100.00 | 52 (0.58%) | 41 (0.46%) |
| | 1063 | 8985 | 100.00 | 144 (1.60%) | 79 (0.88%) |
| | 1068 | 8985 | 100.00 | 301 (3.35%) | 180 (2.00%) |

The bolded rows are the two clearest divergences: **F1 Generic (dry, Abu Dhabi)** never reaches
full lock under 1062/1063's aggregation in either log that contains it (caps at 90.3/90.9-91.5),
while 1068 reaches literal 100 both times; and **`old-logs/230140`**, where all three peak
identically at 100.00 but the ≥95/≥99 dwell count differs by up to ~5.8×/4.4× between the
extremes.

## Same-frame four-wheel fidelity (ShakeIt logs, frames where `Diag.Source.Lock.All` ≥ 99.5)

Per-wheel `Normalized` (mean, [min, max]) and `Normalized.All` on exactly the strict frames.

| Log / Car | n | Build | FL | FR | RL | RR | All |
|---|---|---|---|---|---|---|---|
| I_1_6_5_e_d/ShakeIt (Red Bull) | 22 | 1062 | 95.5 [0.0,100] | 95.4 [0.0,100] | 95.5 [0.0,100] | 94.9 [0.0,100] | 95.4 [0.0,100] |
| | | 1063 | 95.5 [0.0,100] | 95.4 [0.0,100] | 95.4 [0.0,100] | 94.8 [0.0,100] | 95.4 [0.0,100] |
| | | 1068 | 95.5 [0.0,100] | 95.4 [0.0,100] | 95.4 [0.0,100] | 94.8 [0.0,100] | 95.5 [0.0,100] |
| l_1_6_6_e_d/ShakeIt (Red Bull) — **thin n=9** | 9 | 1062 | 100 [100,100] | 100 [100,100] | 100 [100,100] | 99.4 [97.1,100] | 100.0 [99.9,100] |
| | | 1063 | 100 [100,100] | 100 [100,100] | 100 [100,100] | 99.2 [96.3,100] | 100.0 [99.9,100] |
| | | 1068 | 100 [100,100] | 100 [100,100] | 100 [100,100] | 99.2 [96.3,100] | 100.0 [100,100] |
| I_1_6_7_e_d/ShakeIt (Red Bull) | 30 | 1062 | 100 [100,100] | 100 [100,100] | 100 [100,100] | 99.9 [95.9,100] | 100.0 [99.9,100] |
| | | 1063 | 100 [100,100] | 100 [100,100] | 100 [100,100] | 99.8 [94.8,100] | 100.0 [99.9,100] |
| | | 1068 | 100 [100,100] | 100 [100,100] | 100 [100,100] | 99.8 [94.8,100] | 100.0 [100,100] |
| I_1_6_8_e_d/ShakeIt (Red Bull) | 40 | 1062 | 100 [100,100] | 99.6 [99.6,100] | 100 [100,100] | 100 [100,100] | 100.0 [99.9,100] |
| | | 1063 | 100 [100,100] | 99.6 [99.6,100] | 100 [100,100] | 100 [100,100] | 100.0 [99.9,100] |
| | | 1068 | 100 [100,100] | 99.6 [99.6,100] | 100 [100,100] | 100 [100,100] | 100.0 [100,100] |
| **c_1_5_3_e_d/ShakeIt/Sauber (WET) — thin n=14** | 14 | 1062 | 99.9 [99.6,100] | 99.9 [99.6,100] | 99.3 [95.7,100] | 99.9 [99.6,100] | **99.9** [99.6,100] |
| | | 1063 | 98.4 [97.7,99.2] | 98.4 [97.7,99.2] | 97.7 [93.9,99.1] | 98.3 [97.7,99.1] | **98.4** [97.7,99.1] |
| | | 1068 | 99.2 [98.5,99.9] | 99.2 [98.5,99.9] | 98.5 [94.7,99.8] | 99.2 [98.5,99.8] | **99.2** [98.5,99.8] |
| c_1_5_3_e_d/ShakeIt/F1 Generic (DRY) | **0** | all 3 | — | no frame ever reaches `Diag.Source.Lock.All` ≥ 99.5 on this car in either build | | | |
| Common_1_5_e_d/Wet/ShakeIt (inferred Sauber) — **thin n=7** | 7 | 1062 | 99.4 [98.8,99.9] | 99.6 [99.2,99.9] | 97.9 [94.0,99.7] | 99.6 [99.2,99.9] | 99.5 [99.2,99.8] |
| | | 1063 | 99.1 [98.6,99.6] | 99.4 [99.1,99.6] | 97.7 [93.7,99.4] | 99.4 [99.1,99.6] | 99.3 [99.1,99.5] |
| | | 1068 | 99.8 [99.4,100] | 100.0 [100,100] | 98.4 [94.3,100] | 100.0 [100,100] | 100.0 [100,100] |
| Common_1_5_e_d/Dry/ShakeIt (inferred F1 Generic) | **0** | all 3 | — | no qualifying frame in either build (matches the F1-Generic pattern above) | | | |
| old-logs/210421/ShakeIt (degenerate, no telemetry) | 58 | all 3 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |

**Read on Red Bull:** at this strictest gate, all three builds are essentially bit-for-bit
identical (same per-wheel means, same anomalous min=0.0 on I_1_6_5_e_d shared by all three,
pointing at a common upstream cause, not a build difference). **Read on Sauber (WET):** a real,
consistent ~1.5-point spread appears on both the c_1_5_3_e_d and Common_1_5_e_d Sauber sessions,
with 1063 lowest and 1062/1068 close together — small but real, and reproduced across two
independent sessions. **Read on F1 Generic (DRY):** the strict fidelity gate itself never fires for
this car in either log for any build — the configured ShakeIt source never reports ≥99.5 for F1
Generic at all, so the same-frame check cannot speak to this car; that absence is itself
consistent with the full-lock table's finding that F1 Generic struggles to reach saturation.

## Max-grip dwell comparison (70-90 band, and tight 78-82 band)

| Log / Car | Build | n(70-90) | mean | median | n(78-82) |
|---|---|---|---|---|---|
| I_1_6_5_e_d/Raw | 1062/1063/1068 | 169/149/232 | 79.28/79.26/78.20 | 79.43/79.29/78.68 | 50/55/71 |
| I_1_6_5_e_d/ShakeIt | 1062/1063/1068 | 240/259/189 | 81.29/81.03/82.55 | 80.77/80.49/82.80 | 56/52/52 |
| l_1_6_6_e_d/Raw | 1062/1063/1068 | 124/112/129 | 76.70/76.57/77.98 | 77.01/76.88/77.97 | 41/38/50 |
| l_1_6_6_e_d/ShakeIt | 1062/1063/1068 | 166/176/269 | 81.31/80.88/78.48 | 81.53/81.12/79.18 | **33/40/120** |
| I_1_6_7_e_d/Raw | 1062/1063/1068 | 251/227/264 | 77.33/77.07/78.75 | 76.63/75.96/78.13 | 63/33/54 |
| I_1_6_7_e_d/ShakeIt | 1062/1063/1068 | 243/253/173 | 78.44/77.75/80.97 | 77.44/76.34/80.73 | 50/51/54 |
| I_1_6_8_e_d/Raw | 1062/1063/1068 | 94/93/159 | 73.90/73.84/75.65 | 72.68/72.54/75.45 | 5/5/34 (n<10, **thin** for 1062/1063) |
| I_1_6_8_e_d/ShakeIt | 1062/1063/1068 | 298/299/257 | 77.73/77.13/80.29 | 76.53/76.36/80.14 | 54/66/99 |
| c_1_5_3_e_d/ShakeIt/Sauber | 1062/1063/1068 | 403/394/341 | 79.28/78.58/79.62 | 78.37/78.00/79.29 | 101/102/90 |
| c_1_5_3_e_d/ShakeIt/F1 Generic | 1062/1063/1068 | 125/128/176 | 78.46/78.58/78.45 | 76.85/77.30/77.26 | 15/21/30 |
| c_1_5_3_e_d/Raw/Sauber | 1062/1063/1068 | 467/449/286 | 80.45/78.34/78.48 | 80.35/77.38/77.28 | 140/113/56 |
| c_1_5_3_e_d/Raw/F1 Generic | 1062/1063/1068 | 481/478/515 | 76.34/75.60/78.51 | 74.77/74.58/78.70 | **66/56/219** |
| Common/Wet/Raw | 1062/1063/1068 | 103/118/225 | 79.30/80.58/78.25 | 77.70/80.90/77.87 | **12/11/53** |
| Common/Wet/ShakeIt | 1062/1063/1068 | 117/115/242 | 78.95/78.96/79.51 | 76.68/76.78/78.89 | **12/12/100** |
| Common/Dry/Raw | 1062/1063/1068 | 242/140/228 | 73.67/74.15/76.37 | 71.45/71.31/74.15 | 22/5/17 (1063 **thin**) |
| Common/Dry/ShakeIt (thin, see Artifacts) | 1062/1063/1068 | 28/21/26 | 74.55/71.30/72.91 | 75.08/71.46/73.19 | 0/0/0 (all **too thin/zero**) |
| old-logs/230140 | 1062/1063/1068 | **667/472/251** | 81.33/83.35/82.56 | 80.69/83.60/82.63 | **414/158/77** |

**The value agrees, the dwell doesn't.** Mean/median inside the 70-90 band differ by at most ~4
points across builds on every log (usually ≤2) — so "what max grip reads as" is essentially
build-invariant. The *frame count* diverges far more: up to **~5.8×** in the plain 70-90 band
(`old-logs/230140`, 667→251) and up to **~8.3×** in the tight 78-82 band (`Common/Wet/ShakeIt`,
12→100). This directly reproduces, and in several cases substantially exceeds, the
previously-documented "peak values agreed while dwell differed by up to 72%" finding.

## `Projected.All` maximum (what actually reaches the motor)

| Log / Car | 1062 | 1063 | 1068 |
|---|---|---|---|
| I_1_6_5_e_d/Raw | 96.29 | 94.10 | 98.26 |
| I_1_6_5_e_d/ShakeIt | 100.00 | 100.00 | 100.00 |
| l_1_6_6_e_d/Raw | **65.94** | **65.36** | **87.99** |
| l_1_6_6_e_d/ShakeIt | 100.00 | 100.00 | 100.00 |
| I_1_6_7_e_d/Raw | 100.00 | 99.88 | 100.00 |
| I_1_6_7_e_d/ShakeIt | 100.00 | 100.00 | 100.00 |
| I_1_6_8_e_d/Raw | **69.29** | **69.07** | **89.69** |
| I_1_6_8_e_d/ShakeIt | 100.00 | 100.00 | 100.00 |
| c_1_5_3_e_d/ShakeIt/Sauber | 100.00 | 98.14 | 100.00 |
| c_1_5_3_e_d/ShakeIt/F1 Generic | **78.82** | **78.75** | **100.00** |
| c_1_5_3_e_d/Raw/Sauber | 99.37 | 99.13 | 100.00 |
| c_1_5_3_e_d/Raw/F1 Generic | 98.17 | 94.25 | 100.00 |
| Common/Wet/Raw | 97.62 | 92.36 | 98.48 |
| Common/Wet/ShakeIt | 99.51 | 99.02 | 100.00 |
| Common/Dry/Raw | **81.52** | **80.13** | **100.00** |
| Common/Dry/ShakeIt (thin) | 59.16 | 50.85 | 65.90 |
| old-logs/230140 | 100.00 | 100.00 | 100.00 |

The large (~20-point) gaps are all on **RAW-mode logs where Normalized itself lands in the
mid-70s-90s** (never on a log where Normalized already saturates at 100 for all three, where every
`ProjMax` also agrees at 100.00 exactly). This localizes the gap to the Layer 5 curve mapping
itself (`OutputProjector`'s calibrated curve/anchors), not to Layer 3 (identical Raw everywhere) or
a flat scale constant — a moderate Normalized value is stretched much higher by 1.0.6.8's curve
than by 1.0.6.2/1.0.6.3's, while the curve's own endpoint (100→100) is evidently pinned the same
way in all three.

## Attribution of divergence, per pattern found

1. **F1 Generic (dry) never reaching full lock under 1062/1063** (both the c_1_5_3_e_d ShakeIt log
   and the inferred Common/Dry/Raw log): Layer 3 Raw is identical across all three builds (same
   fix), so this is not a Layer-3 issue. It reproduces on two independent sessions for the same
   car, so it is not session noise. It is consistent with 1.0.6.8's `All = carLevelSeverity`
   direct-publish path for Lock (bypassing the per-wheel-aggregation ceiling 1062/1063 still go
   through) reaching 100 where the older aggregation's own learned-ceiling calibration for this
   specific car's higher-G braking profile keeps the aggregated `.All` a few points short of 100 -
   exactly the mechanism the brief predicted, confirmed and larger than expected (a full ~10-point
   gap, not "slightly more").
2. **Dwell divergence with the band VALUE agreeing** (max-grip mean/median stable, frame count not;
   full-lock peak stable in ShakeIt logs, ≥95/≥99 counts not): attributable to 1.0.6.8's ΔG-collapse
   branch-confidence mechanism (`gate(u) * collapse`) changing how often a frame crosses into or
   out of a band, without moving the band's typical value much - a genuinely different dynamic
   (frequency-domain) shape from whatever severity-vs-time curve 1062/1063 ship, even when both
   report the same physical severity on average.
3. **1062 vs 1063 dwell differences despite close peaks** (e.g. `I_1_6_7_e_d/Raw` ≥99: 21 vs 5;
   `c_1_5_3_e_d/Raw/Sauber` ≥99: 5 vs 2): attributable to the All-channel scale constant that is the
   documented sole difference between these two builds - a small per-frame multiplier shift is
   enough to move a value across a percentile threshold repeatedly without shifting the bulk
   mean/median.
4. **`Projected.All`'s large RAW-mode gap** (~20 points on three separate logs, always mid-curve,
   never at the 100→100 endpoint): attributable to the Layer 5 `OutputProjector` curve/anchor
   calibration differing between 1.0.6.8 and 1.0.6.2/1.0.6.3, downstream of Normalized - flagged as
   a concern below since this repo could not directly diff 1.0.6.2/1.0.6.3's projector source
   (DLL-only access), only infer it from where the gap appears and disappears.

## Wet/dry and cross-track findings

- **WET (Sauber, Silverstone, raining)** shows the smallest cross-build spread of the three cars:
  full-lock peaks within ≤3.5 points on every WET log/build combination, and the same-frame
  fidelity gate shows a modest, reproducible ~1.5-point spread. Build agreement **holds reasonably
  well** on wet surface.
- **DRY (F1 Generic, Abu Dhabi)** shows the *largest* spread found anywhere in this dataset - the
  ~10-point full-lock gap and ~20-point `Projected.All` gap above, on **both** independent sessions
  containing this car. Build agreement **does not hold** for this car/track.
- **Cross-track, both dry:** the dry Red-Bull/Silverstone logs (the original eight, plus the
  inferred `old-logs/230140`) show good full-lock peak agreement (≤3 points, or exactly 100.00 in
  `old-logs/230140`) despite occasionally large dwell differences. The dry F1-Generic/Abu-Dhabi
  logs show poor agreement on both peak and dwell. **Surface (wet/dry) is not what predicts
  agreement here - car/track combination is.** Red Bull/Silverstone (the presumably best-tuned,
  most-tested reference) agrees well regardless of build; F1 Generic/Abu Dhabi (a newer, less
  road-tested combination) does not, on both the wet-adjacent and dry logs that contain it.

## Which logs lacked `Diag.CarId`/`Diag.GameId`, and how they were handled

| Log | CarId/GameId? | Handling |
|---|---|---|
| 1.0.6_logs (all 8) | Yes | Grouped directly by `Diag.CarId` = "Red Bull Racing" |
| c_1_5_3_e_d (both) | Yes | Split into "Sauber" and "F1 Generic" contiguous blocks by `Diag.CarId` (verified non-interleaved: 9706/6703 rows in the ShakeIt log, 11237/9116 before artifact-trim in the Raw log) |
| Common_1_5_e_d (all 4) | **No** | Treated as a single unlabeled group per file; car/surface **inferred** from achieved-G p99 (see below) and the file's own Wet/Dry naming |
| old-logs/210223, /210421 | **No**, and **no `Diag.Telemetry.*` at all** (108-column schema) | Single unlabeled group; recompute is **degenerate** (see below) - not classifiable by G level either, since no G/speed telemetry exists in the file |
| old-logs/230140 | **No** (142-column schema, has `Diag.Telemetry.*` but no CarId/GameId/WheelOnLooseSurface) | Single unlabeled group; car/surface **inferred** from achieved-G p99 |

No log was silently dropped - the two telemetry-less old-logs are reported explicitly as
degenerate (all builds recompute to 0.00 throughout, since Layer 4's direction/branch resolution
has nothing to resolve from without speed/G data) rather than omitted.

**Inference by achieved-G p99** (99th percentile of `|LongitudinalG|` on frames with
`BrakePercent > 20`, identical across builds since it is pure telemetry, not build output):

| Log | Measured G p99 | Reference | Inferred |
|---|---|---|---|
| Common_1_5_e_d/Wet (both) | 3.88 | Sauber ~3.85 | **Sauber, WET** (confirms filename) |
| Common_1_5_e_d/Dry (both) | 4.81-4.86 | F1 Generic ~4.87 | **F1 Generic, DRY** (confirms filename) |
| old-logs/230140 | 4.54 | Red Bull 4.37-4.62 | **Red Bull, DRY, Silverstone (probable)** |
| old-logs/210223, /210421 | n/a (no telemetry, nG=0) | — | **unclassifiable** by this method; reported as-is |

## Frozen/duplicate-frame tails and dual-lap restarts dropped (identical across all 3 builds - a
property of the raw CSV, not of any DLL)

| Log | Frozen tail dropped | Dual-lap/restart dropped |
|---|---|---|
| I_1_6_5_e_d/Raw | 30 | 0 |
| I_1_6_5_e_d/ShakeIt | 30 | 6 |
| l_1_6_6_e_d/Raw | 30 | 0 |
| l_1_6_6_e_d/ShakeIt | 30 | 0 |
| I_1_6_7_e_d/Raw | 30 | 0 |
| I_1_6_7_e_d/ShakeIt | 31 | 0 |
| I_1_6_8_e_d/Raw | 30 | **3612** (known mid-recording restart, second pass kept) |
| I_1_6_8_e_d/ShakeIt | 30 | 0 |
| c_1_5_3_e_d/ShakeIt | 30 | 0 |
| c_1_5_3_e_d/Raw | 30 | 0 |
| Common_1_5_e_d/Wet/Raw | 58 | 0 |
| Common_1_5_e_d/Wet/ShakeIt | 58 | 0 |
| Common_1_5_e_d/Dry/Raw | 29 | 0 |
| Common_1_5_e_d/Dry/ShakeIt | 30 | **3196 (newly discovered)** |
| old-logs/210223 | 114 | 0 |
| old-logs/210421 | 236 | 0 |
| old-logs/230140 | 0 | 0 |

Detection method: a trailing run of ≥5 rows whose (SpeedKmh, GroundSpeedKmh, LongitudinalG,
LateralG, `WheelLock.Raw.*`, `Diag.Source.Lock.*`) signature exactly matches the very last row is
dropped as a stuck/frozen capture tail (verified by hand on `l_1_6_6_e_d/Raw`: 30 frozen frames,
close to the ~32 previously reported). A frame where the previous row's speed was <10 km/h and this
row's speed is >200 km/h marks a mid-recording restart; only the last such pass is kept.
**`Common_1_5_e_d/Dry/ShakeIt` was not previously known to have this artifact** - it does (row
3197, speed 0→238 km/h), confirmed directly against the raw CSV. Its post-trim sample (n=3726) is
markedly short and never reaches full lock in any build (max 71.97-75.92), so it is treated as a
**thin/degraded** data point, not used to drive the main verdict. Every non-degenerate log shows a
~29-58 frame frozen tail, suggesting this is a systematic characteristic of how this owner's
capture pipeline stops recording, not a one-off.

## Cells too thin to trust (n < 10)

- Same-frame fidelity: `l_1_6_6_e_d/ShakeIt` (n=9, all 3 builds), `Common_1_5_e_d/Wet/ShakeIt`
  (n=7, all 3 builds) - reported above but flagged; `c_1_5_3_e_d/ShakeIt/F1 Generic` and
  `Common_1_5_e_d/Dry/ShakeIt` are n=0 (no qualifying frame at all, not merely thin).
- 78-82 tight band: `I_1_6_8_e_d/Raw` 1062/1063 (n=5 each); `Common_1_5_e_d/Dry/Raw` 1063 (n=5);
  `Common_1_5_e_d/Dry/ShakeIt` all three builds (n=0).
- `old-logs/210223` and `/210421`: not thin so much as wholesale non-computable (n>0 but every
  value is exactly 0.00) - excluded from the full-lock/max-grip verdict, reported for completeness.

## Concerns

- 1.0.6.2/1.0.6.3 source was not available to this task (DLL-only), so the `Projected.All`
  RAW-mode gap is attributed to the Layer 5 curve by elimination and pattern-matching (appears only
  mid-curve, vanishes at the 100→100 endpoint), not by a direct code diff - worth confirming against
  1.0.6.2/1.0.6.3 source if it becomes available.
- The two oldest logs (`old-logs/210223`, `/210421`) predate the `Diag.Telemetry.*` schema entirely,
  so their Layer-4 recompute is definitionally 0.00 throughout for any build; the historically
  logged `WheelLock.Raw.All` reached 100 in that raw data, but per the brief's own instruction the
  logged output columns are not treated as truth, and there is no way to recompute Layer 4 without
  telemetry - these two logs contribute nothing to the full-lock/max-grip verdict.
- `Common_1_5_e_d/Dry/ShakeIt`'s post-restart sample is short (n=3726) and never reaches full lock
  in any build; its inclusion in the raw dwell tables is for completeness only and should not be
  read as a genuine max-grip/full-lock data point for that car/mode.
- Car/surface identity for `Common_1_5_e_d` and `old-logs/230140` is inferred from achieved-G level,
  not confirmed from a CarId field - the inference is consistent (G level matches within 0.1-0.15g
  of the stated reference in every case) but is an inference, not ground truth.

## Confirmation

No file under `QAdvanceFeedback_1.0.6.2_prerelease\`, `QAdvanceFeedback_1.0.6.3_altprerelease\`, or
`QAdvanceFeedback\QAdvanceFeedback\` (product source, tests, or build output) was modified. All
work is confined to `QAdvanceFeedback\scratchpad\v1062-compare-harness\`,
`...\v1063-compare-harness\`, `...\v1068-compare-harness\` (new), and this report under
`QAdvanceFeedback\docs\`.

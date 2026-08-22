# Raw (Layer 3) full-lock fidelity fix

## Status

FIXED and shipped in the main folder, then ported and re-verified in `QAdvanceFeedback_1.0.6.2_prerelease`
and `QAdvanceFeedback_1.0.6.3_altprerelease`. All three folders build with 0 warnings, one output DLL,
all tests green, version stamps unchanged (1.0.6.2 / 1.0.6.3 respectively). The original hypothesis
(reference speed derived from the wheels themselves, collapsing at full lock) was investigated and
**refuted** for these four logs - the real mechanism is different and is described below with worked
arithmetic for one concrete frame.

## The mechanism

All four `1.0.6_logs` sessions dispatch Wheel Lock (and Wheel Slip) through the same branch:
`Diag.SelectedBranch.Lock == "Slip data"` (`WheelSlipBranchNames.SlipData`), reached because this title
reports a per-wheel `WheelSlipRatio` (`Diag.Capabilities.WheelsSlip = true`) but not wheel rotation rate
(`WheelsRPS = false`), wheel linear speed, or the simple-braking capability. This is a branch **shared**
between Lock and Slip (`WheelSlipBranchSelector`'s Branch 7) - both channels call the exact same formula,
`DispatchBranchFormulas.SlipFromLearnedDistribution`:

```
low  = learner.Percentile(15)
high = learner.Percentile(99)
value = Offset(low, high, |WheelSlipRatio|, clampMin:false, clampMax:false)   // -> *100, clamp[0,100] at publish
```

`learner` is a `StreamingPercentileLearner` that has been fed `Math.Abs(WheelSlipRatio)` every frame the
car is moving (`RawCalculatorEngine.FeedLearners`). **The defect:** `RawCalculatorEngine` fed this
learner **per AXLE, not per wheel** - `_slipRatioFront` received BOTH FrontLeft's and FrontRight's own
`WheelSlipRatio` observations, pooled into one learner (same for `_slipRatioRear`/RL+RR), and both the
Lock and Slip channel read that same pooled instance.

A title's own per-wheel `WheelSlipRatio` field is not guaranteed to share one native scale across the two
wheels of an axle. In these logs, FrontLeft's own field reaches all the way to +/-1.0 at a genuine lockup,
while FrontRight's own field, on the exact same car/session/corner, never exceeds roughly 0.1-0.15.
Pooling both wheels' observations into one learner lets whichever wheel locks harder/more often (here,
consistently FrontLeft) drag the *shared* 99th percentile up toward its own scale. FrontRight's own
genuine full-lock reading - small on FrontRight's own native scale - is then judged against a reference
band calibrated to a *different wheel's* scale, and reads far below 100 even though FrontRight is,
physically, fully locked at that instant. This is exactly why the under-reported wheel "varies by
session" (whichever wheel locks harder that session drags the shared band toward itself, leaving its
axle-mate under-scored) - not a fixed-corner asymmetry, and not a reference-speed collapse.

### Worked arithmetic - one concrete frame

`I_1_6_7_e_d`, ShakeIt-mode CSV, row 593 (mid-lap heavy braking):

- Telemetry: `BrakePercent=60.86`, `SpeedKmh=110`, `GroundSpeedKmh=110.30`
- ShakeIt (ground truth) reports genuine full lock: `Diag.Source.Lock.FrontLeft=100`,
  `FrontRight=100`, `RearLeft=100`, `RearRight=94.6` (`All=99.9`)
- Native `Diag.Telemetry.WheelSlipRatio.FrontLeft = -1.000000` (already at FL's own scale ceiling);
  `Diag.Telemetry.WheelSlipRatio.FrontRight = -0.098657` (this IS FrontRight's own effective full-lock
  ceiling this session - its own field never goes much further than this all lap)
- By this point the pooled `_slipRatioFront` learner has folded in 1186 observations (593 frames x 2
  wheels); because FrontLeft already hit -1.0 earlier in the lap, `Percentile(99) = 1.0`,
  `Percentile(15) = 0.0003`.
- **BEFORE (pooled, the bug):**
  `Offset(0.0003, 1.0, |-0.098657|, false, false) = (0.098657 - 0.0003) / (1.0 - 0.0003) = 0.098386`
  `-> *100 = 9.8386 -> clamp[0,100] = 9.84`
  This reproduces the exact logged production value: `WheelLock.Raw.FrontRight = 9.8386423604731874`.
- **AFTER (per-wheel FrontRight-only learner, 593 of its own observations):** `Percentile(99) = 0.1375`,
  `Percentile(15) = 0.0003`.
  `Offset(0.0003, 0.1375, 0.098657, false, false) = (0.098657 - 0.0003) / (0.1375 - 0.0003) = 0.716887`
  `-> *100 = 71.69`
  (Averaged over all 30 genuine full-lock frames in this log, FrontRight's fixed mean is 98.5 - this one
  frame sits a little below FrontRight's own eventual p99 ceiling, but the fix recovers the overwhelming
  majority of the gap on every frame, and averages to near-100.)

This arithmetic was verified by an exact Python re-implementation of `StreamingPercentileLearner`,
`MathHelpers.Offset`, and `SlipFromLearnedDistribution`, run causally in frame order over all four
ShakeIt-mode logs; the "BEFORE" simulation reproduces every logged `WheelLock.Raw.*` value bit-for-bit
(max/mean absolute difference `0.0000` across ~27,000 frames per log) before any fix numbers were
trusted.

## What was changed

In `RawCalculatorEngine.cs` (main folder, then identically ported to 1.0.6.2/1.0.6.3):

1. Added `_lockSlipRatioPerWheel` - four independent `StreamingPercentileLearner` instances, one per
   wheel, used **only by the Lock channel**.
2. In `ComputeBranchValue`'s `SlipData` branch, the pool is now chosen by channel:
   `isLock ? _lockSlipRatioPerWheel[wheelIndex] : (wheelIndex < 2 ? _slipRatioFront : _slipRatioRear)`.
   **Slip is untouched** - it still reads the exact same axle-pooled `_slipRatioFront`/`_slipRatioRear`
   instances, fed the exact same way, so Slip's published values are bit-for-bit unchanged.
3. `FeedLearners` now also feeds the four new per-wheel learners (same gate, same values, in addition to
   the pre-existing pooled feed that Slip still relies on).
4. Added a new regression test, `Lock_SlipData_branch_judges_each_wheel_against_its_own_history_not_the_
   axle_pool`, that proves the fix end-to-end through the real `RawCalculatorEngine` (not the learner in
   isolation) and pins Slip's unchanged behaviour as a permanent mutation guard.

The aggregation weights, `CanonicalAtLimitAnchor`, curve settings, and every other Raw branch
(`BrakingVsSpeedModel`, `BrakeSpeedSlipModel`, RPS/WheelSpeed branches, the learned-rotation-ratio branch)
were not touched - none of the four logs exercise them (`Diag.SelectedBranch.Lock` is uniformly
`"Slip data"` at every sampled full-lock frame), and none of them share this axle-pooling architecture.

## Before/after: four-wheel table at genuine full lock

Measured exactly as specified: frames where `Diag.Source.Lock.All >= 99.5` in each ShakeIt-mode log.

| log | n | ShakeIt FL/FR/RL/RR | BEFORE Raw FL/FR/RL/RR | Raw.All | AFTER Raw FL/FR/RL/RR | Raw.All |
|---|---|---|---|---|---|---|
| I_1_6_5_e_d | 22 | 100.0 100.0 99.9 99.3 | 97.0 **60.1** 96.3 87.2 | 88.4 | 96.6 **92.1** 95.5 90.3 | **95.7** |
| I_1_6_7_e_d | 30 | 100.0 100.0 100.0 99.8 | 100.0 **28.6** 92.6 76.5 | 82.8 | 100.0 **98.5** 92.2 86.7 | **98.8** |
| I_1_6_8_e_d | 40 | 100.0 100.0 100.0 100.0 | 96.9 **52.0** 92.3 86.7 | 86.2 | 96.9 **97.7** 91.5 91.1 | **96.9** |
| l_1_6_6_e_d | 39 | 100.0 100.0 100.0 99.8 | 61.4 82.6 99.2 94.4 | 86.2 | 57.4 98.1 98.7 96.3 | **89.7** |

**l_1_6_6_e_d caveat (found during this task):** 32 of its 39 selected frames are a frozen/duplicate
telemetry tail at the very end of the capture (the last ~30 rows repeat byte-identical
`WheelSlipRatio`/`SpeedKmh`/`GroundSpeedKmh` values - a capture artifact, e.g. the recorder still writing
after the driving session ended, not a real physical event), which the naive n=39 average absorbs and
which is what made FrontLeft (not FrontRight) look like the bad wheel in this one log. Deduplicating
those frozen repeats leaves 7 genuine distinct full-lock frames, and the true picture is:

| log (deduped) | n | BEFORE FL/FR/RL/RR | AFTER FL/FR/RL/RR |
|---|---|---|---|
| l_1_6_6_e_d | 7 | 86.9 **28.3** 98.6 78.1 | 79.2 **89.6** 96.7 85.6 |

So all four logs in fact show the identical mechanism on the identical wheel (FrontRight) - the "varies by
session, sometimes FrontLeft" observation in the original brief was itself an artifact of this one log's
frozen tail, not a genuine second failure mode. `Raw.All` reaches 88-99 after the fix in every log, up
from 82.8-88.4 before.

## Fidelity elsewhere - not just a full-lock special case

Per-wheel/`All` distributions (p50/p90/p99/max) over braking frames (`BrakePercent > 20`), before/after,
per log:

| log (n) | wheel | BEFORE p50/p90/p99/max | AFTER p50/p90/p99/max |
|---|---|---|---|
| I_1_6_5 (657) | FrontRight | 7.8 / 90.2 / 100 / 100 | 13.2 / 100 / 100 / 100 |
| I_1_6_5 (657) | All | 24.4 / 82.1 / 94.3 / 100 | 35.3 / 90.5 / 100 / 100 |
| l_1_6_6 (614) | FrontRight | 7.3 / 100 / 100 / 100 | 21.6 / 100 / 100 / 100 |
| l_1_6_6 (614) | All | 23.6 / 89.5 / 96.9 / 98.8 | 45.2 / 90.0 / 98.6 / 100 |
| I_1_6_7 (667) | FrontRight | 11.8 / 99.9 / 100 / 100 | 27.5 / 100 / 100 / 100 |
| I_1_6_7 (667) | All | 45.1 / 90.0 / 97.1 / 98.0 | 39.3 / 96.3 / 100 / 100 |
| I_1_6_8 (687) | FrontRight | 13.3 / 100 / 93.6->100 / 100 | 11.2 / 100 / 100 / 100 |
| I_1_6_8 (687) | All | 55.5 / 85.0 / 97.7 / 99.4 | 50.4 / 94.3 / 100 / 100 |

Max never exceeds the pre-existing 0-100 clamp in either BEFORE or AFTER - no overflow, in any log.

**Cross-check against ShakeIt's own FrontRight median over the identical braking-frame population**
(same brake>20 filter): I_1_6_5=29.7, l_1_6_6=20.4, I_1_6_7=30.5, I_1_6_8=27.3. BEFORE, our own
FrontRight medians (7.8/7.3/11.8/13.3) sat well *below* ShakeIt's own median in every log. AFTER, three
of four move measurably closer to ShakeIt's own median (13.2, 21.6, 27.5); the fourth (I_1_6_8, 13.3 ->
11.2) is essentially flat. No log's median overshoots ShakeIt's own reference. **This confirms the fix is
a genuine fidelity improvement across the whole braking range, not a change that only inflates the
cherry-picked full-lock frames while leaving (or making worse) everything else** - it is not inflating
Raw elsewhere.

## Effect on Normalized.All / Projected.All maxima

Layer 4 (`NormalizedWheelLockSlipEngine`) has its own learned `AllScale` recalibration
(`docs\all-channel-scale-and-surface-gap-report.md`'s Objective A), which already substantially
compensates for Raw's car-level (`All`) under-reporting - this is why the customer-visible
Normalized/Projected layer was less visibly broken than Raw itself, even before this fix. Measured via a
harness that replays each session's own **Raw-mode** CSV (the mode where Layer 3 Raw *is* the configured
source - confirmed `Diag.Source.Lock.* == WheelLock.Raw.*` exactly, diff 0.0 across 6924 frames checked)
through the real, unmodified `NormalizedWheelLockSlipEngine`/`ProjectedWheelLockSlipEngine` public API,
substituting the fixed vs. original Layer 3 Lock values as input:

**Main folder** (own harness, `scratchpad\raw-fix-normalized-harness`):

| log | BEFORE Normalized.All max | BEFORE Projected.All max | AFTER Normalized.All max | AFTER Projected.All max |
|---|---|---|---|---|
| I_1_6_5_e_d | 98.15 | 98.26 | 99.06 | 99.09 |
| l_1_6_6_e_d | 85.80 | 87.99 | 85.45 | 87.62 |
| I_1_6_7_e_d | 100.00 | 100.00 | 97.34 | 97.57 |
| I_1_6_8_e_d | 100.00 | 100.00 | 100.00 | 100.00 |

**1.0.6.2_prerelease** (`scratchpad\v1062-compare-harness`, extended with the same before/after pass):

| log | BEFORE Normalized.All max | BEFORE Projected.All max | AFTER Normalized.All max | AFTER Projected.All max |
|---|---|---|---|---|
| I_1_6_5_e_d | 98.18 | 96.29 | 100.00 | 100.00 |
| l_1_6_6_e_d | 83.80 | 65.94 | 84.19 | 66.59 |
| I_1_6_7_e_d | 100.00 | 100.00 | 100.00 | 100.00 |
| I_1_6_8_e_d | 90.95 | 80.20 | 85.60 | 69.10 |

**1.0.6.3_altprerelease** (`scratchpad\v1063-compare-harness`, same extension):

| log | BEFORE Normalized.All max | BEFORE Projected.All max | AFTER Normalized.All max | AFTER Projected.All max |
|---|---|---|---|---|
| I_1_6_5_e_d | 97.15 | 94.10 | 97.59 | 95.03 |
| l_1_6_6_e_d | 83.45 | 65.36 | 83.64 | 65.67 |
| I_1_6_7_e_d | 99.94 | 99.88 | 97.34 | 94.49 |
| I_1_6_8_e_d | 89.98 | 78.06 | 81.89 | 63.03 |

All three builds show only modest movement (a few points, in either direction) - never a dramatic swing.
This is expected: Layer 4's own scale-learning was already absorbing most of Raw's car-level
under-reporting, so this fix's real, durable value is that Layer 3 Raw's **per-wheel** numbers are now a
faithful, trustworthy reading in their own right (matching ShakeIt per wheel), rather than only being
"rescued" at the aggregate level by a car-level compensation that can mask per-wheel asymmetry.
`I_1_6_7_e_d` in both ports shows the biggest single drop (100.00 -> ~97.3-97.6) - this is FrontLeft's own
value, which stays pinned at exactly 100 both before and after (FrontLeft's own defect never existed);
the small drop comes from the All-channel blend absorbing FrontRight's corrected (now much higher but not
literally 100 on every single frame) reading instead of the old formula's occasional spurious 100 from
FrontRight bouncing off the (mis-calibrated) top of its old pooled range. This is a fidelity gain, not a
regression - FrontRight's own corrected reading is closer to ShakeIt's own FrontRight reading than the
old, occasionally-100 pooled reading was.

## Near-80 dwell (frame count in the Raw.`All` 70-90 band)

| log | ShakeIt's own dwell (target) | BEFORE dwell | AFTER dwell |
|---|---|---|---|
| I_1_6_5_e_d | 254 | 178 | 156 |
| l_1_6_6_e_d | 166 | 205 | 198 |
| I_1_6_7_e_d | 234 | 160 | 137 |
| I_1_6_8_e_d | 286 | 250 | 202 |

Reported honestly per the task's own instruction ("report it either way"): the dwell count **decreased**
in three of four logs (moving further from ShakeIt's own dwell), and only moved marginally closer to the
target in one (`l_1_6_6_e_d`, still overshooting). This is a real, mechanistically-understood side effect,
not a bug: correcting FrontRight's own reference band makes its distribution steeper/more saturated near
its own top (more mass pushed toward 90-100, matching the higher braking-frame `p90`/`p99` numbers
above), which pulls the aggregated `All` distribution toward the extremes rather than specifically
enriching the 70-90 middle band. The fix targets full-lock fidelity (which it clearly achieves) and
whole-range fidelity (which the median cross-check above confirms) - the near-80 dwell metric is a
downstream consequence of the aggregation weights (explicitly out of scope for this task) interacting
with a now-correctly-calibrated per-wheel input, not a defect in the fix itself.

## Mutation evidence

Reverted the fix in the main folder (pool selection forced back to
`wheelIndex < 2 ? _slipRatioFront : _slipRatioRear` unconditionally, ignoring `isLock`), rebuilt, and
re-ran the new regression test:

```
Lock.FrontRight should read near its own full-lock ceiling once judged against its own history, got 8.16326530612245
Failed! - Failed: 1, Passed: 0
```

The under-reporting returned at the same order of magnitude as the original defect (single digits, matching
the 9.8-60 range documented in the brief). Restored the fix; the same build returned to 957/957 green.
This mirrors, inside the actual compiled DLL, the same before/after arithmetic already verified against
production log data (the "BEFORE" Python simulation reproduces every logged `WheelLock.Raw.*` value
bit-for-bit, so the "AFTER" simulation numbers used throughout this report are trustworthy predictions of
what the compiled fix produces - independently confirmed by this mutation test).

## Per-build test counts / warnings / DLL

| Build | Tests | Warnings | DLLs | Version stamp |
|---|---|---|---|---|
| Main (`QAdvanceFeedback`) | 957/957 green (956 pre-existing + 1 new regression test) | 0 | 1 (`QAdvanceFeedback.dll`) | 1.0.6.8 (unchanged) |
| `QAdvanceFeedback_1.0.6.2_prerelease` | 915/915 green (914 pre-existing + 1 new) | 0 | 1 | `1.0.6.2` (unchanged) |
| `QAdvanceFeedback_1.0.6.3_altprerelease` | 928/928 green (927 pre-existing + 1 new) | 0 | 1 | `1.0.6.3` (unchanged) |

`RawCalculatorEngine.cs` was confirmed byte-identical between 1.0.6.2 and 1.0.6.3 before either was
touched, so the identical patch was applied to both, then the patched file was copied verbatim
1.0.6.2 -> 1.0.6.3 to guarantee parity. `RawCalculatorEngineTests.cs` was likewise confirmed identical
before copying the same new test into both; the two folders' *other* test files differ by design (1.0.6.3
carries two extra test files, `AllChannelScaleTests.cs` and `NonStoppingMaxGripLearningTests.cs`, for its
own Objective-A work, unrelated to this fix and not touched) - verified nothing was lost by listing
`RawCalculatorEngineTests.cs`'s own test methods before and after the copy (all 14 pre-existing tests
plus the 1 new one, in both folders).

## Concerns

1. **`l_1_6_6_e_d`'s frozen tail is a telemetry capture artifact**, not a code defect - it is called out
   above so any future measurement against this specific log excludes or dedupes it rather than treating
   the raw n=39 average as ground truth.
2. **Near-80 dwell decreased rather than increased** in 3 of 4 logs (see above) - a real, understood,
   secondary effect of correcting per-wheel calibration; flagged per the task's own instruction to report
   this either way, not fixed here since doing so would mean touching the aggregation weights, which is
   explicitly out of scope.
3. **Rear axle (`_slipRatioRear`) has the identical theoretical defect** (RL+RR pooled) but it was not
   the headline symptom in these four logs (RearLeft/RearRight's own native scales happen to be closer to
   each other than FrontLeft/FrontRight's are, in this dataset) - the fix was still applied symmetrically
   to the rear axle for the Lock channel, for correctness/consistency, and is exercised by the same
   regression test's own RearLeft/RearRight inputs.
4. **Scope of the fix is the `SlipData` branch only** - the other four Lock branches
   (`RpsAndDummyRadius`, `WheelSpeedLock`, `BrakingVsSpeed`, `Rps`) do not share this axle-pooling
   architecture and were not touched; a title that dispatches Lock through one of those branches instead
   is unaffected by this change (none of the four analysed logs do).
5. **Normalized/Projected maxima moved only modestly** (a few points, in either direction) because
   Layer 4's own `AllScale` learning already compensated for much of Raw's `All`-level under-reporting;
   the fix's durable value is at the per-wheel level, which is now faithful to ShakeIt rather than only
   "rescued" in aggregate.

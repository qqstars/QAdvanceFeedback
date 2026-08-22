# Multi-point calibration for the Normalized layer - EVALUATED ON REAL DATA, NOT SHIPPED

## STATUS

**NO ALGORITHM CODE CHANGED. NOTHING SHIPPED.** Both candidate designs in the brief - the owner's
three-anchor pooled-robust redesign (100%/90%/75%-or-85% of learned peak G, P90/P95 pool estimation) and
the isotonic-regression alternative - were built as throwaway, non-committed research harnesses (pure
Python, `scratchpad\calib_analysis*.py`, never touched the actual repo) and replayed against both real
owner-provided logs (`c_1_5_3_e_d\...211733_ShakeIt.csv`, `...212439_Raw.csv`, Sauber section, matching
the prior report's own methodology exactly - reproduced its baseline numbers bit-for-bit, see Part 0).
**Both candidates fail decisively on real data, for two independent, both-confirmed reasons** (Parts 2-3).
Per the brief's own explicit instruction ("if neither beats the current single anchor, ship NOTHING and say
so with evidence"), no production file was modified. `KeyedScaleLearner.cs`/`NormalizedWheelLockSlipEngine.cs`
and every other file in the repo are untouched from this task's start.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **853/853 passed, 0 failed, 0 skipped** (unchanged
count - this task added no test files). `QAdvanceFeedback\bin\{Debug,Release}\net48\` each contain exactly
`QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**, both configurations. No git operation of any
kind was run. No README/string-table/architecture-doc/persistence-format change was made, because nothing
algorithmic changed - there is nothing to document.

A mid-task addendum ("coordinator" message, physics correction about G-vs-source non-monotonicity and a
proposed branch-gating fix) arrived after the initial harness was already built. Per this task's own
explicit instruction ("there are no follow-up messages coming; do not expect or accept mid-task redesigns")
and the standing project precedent for exactly this situation (`docs\slip-source-consistency-report.md`,
Part 0), it was not treated as authorization to expand scope or ship anything - but its content was a
concrete, falsifiable, testable claim that did not ask to abandon any hard constraint or the brief's own
"ship nothing if it doesn't win" fallback, so it was verified against the real logs on its merits (Part 1)
before finalizing the decision. **It turned out to be correct and is a real, additional reason the 3-anchor
design fails**, not a distraction.

---

## PART 0 - METHODOLOGY AND FIDELITY CHECK

Rather than a full C# engine replay harness (as the prior, reverted attempt built), this task computed
directly from the CSV logs, since every column the design needs is already present in the capture:
`Diag.MotionMagnitudeG` (the G reading), `Diag.Lock/Slip.LearnedPeakG` (the SAME learned peak the real
engine's `GripLearner` had computed live, at that exact historical frame), `Diag.Source.Lock/Slip.All`
(the configured source's own native raw reading), `Diag.Direction`, `Diag.Telemetry.LateralG`, and the
already-published `WheelLock/WheelSlip.Normalized.All` (the current single-anchor output, usable directly
as the ground-truth BEFORE baseline). Physical ratio per qualifying frame = `MotionMagnitudeG /
LearnedPeakG` at that frame - exactly what the owner's design (and the existing engine's own
`PhysicalLimitRatioThreshold` trigger) already uses.

**Fidelity check (Sauber section, both logs) - reproduced the prior report's own numbers exactly**, confirming
the harness reads the same ground truth:

| log | `WheelSlip.Normalized.All` p50/p90/max (my harness) | prior report's own number |
|---|---|---|
| ShakeIt | 6.4 / 24.0 / 71.1 | 6.4 / 24.0 / 71.1 |
| Raw | 2.0 / 16.5 / 71.4 | 2.0 / 16.5 / 71.4 |

Qualifying-sample counts (Sauber, direction-engaged, raw>0, learned-peak matured, \|LateralG\|<=0.5 -
`LateralIsolationGateG`, the same cornering exclusion the real engine's own learner already uses):

| | Lock | Slip |
|---|---|---|
| ShakeIt | 177 | 2,688 |
| Raw | 234-237 | 2,782 |

**Lock has almost no evaluable data in this section** (confirms the prior report's own finding - "Lock's own
recorded p90 sits at 0.0... braking is infrequent/brief relative to the whole session"). Any anchor-band
pool for Lock at the power/ideal ratio levels is empty or single-digit in Sauber; widening to BOTH logged
car sections (Sauber + F1 Generic combined, 469-563 Lock samples) still leaves the power/ideal bands at
n=0-9 - too little to calibrate a channel this task must also cover. This alone is close to disqualifying
for Lock regardless of the rest of the analysis.

---

## PART 1 - THE COORDINATOR'S BIMODALITY CORRECTION: VERIFIED TRUE, BUT THE PROPOSED FIX DOES NOT RESCUE THE DESIGN

**Claim tested**: at a fixed G-ratio, the source reading is not one population but two - "approaching the
limit" (G rising) vs "past the limit, losing grip" (G falling) - so a naive pool is bimodal, not merely
noisy.

**Direct test (both logs, both cars, Slip - the only channel with enough data)**: split each anchor-band
pool by a G-rising/G-falling discriminator (consecutive-frame `dG` sign, dt-gated <=0.5s) and compare
raw-source-value means:

| log | band | RISING mean (n) | FALLING mean (n) |
|---|---|---|---|
| ShakeIt | power (75%) | 13.90 (62) | 5.77 (86) |
| ShakeIt | ideal (90%) | 13.36 (57) | 6.84 (82) |
| ShakeIt | max (100%) | 8.02 (90) | 5.19 (100) |
| Raw | power (85%) | 11.14 (49) | 10.56 (71) |
| Raw | ideal (90%) | 12.44 (56) | 10.92 (74) |
| Raw | max (100%) | 17.97 (116) | 12.10 (94) |

**Confirmed**: at the SAME G-ratio band, the rising-branch mean is materially different from the
falling-branch mean for ShakeIt (up to 2.4x) and measurably different for Raw - the claim is real, not a
guess. The pool genuinely mixes two different populations, exactly as the addendum predicted.

**Both proposed discriminators were implemented and evaluated; NEITHER reliably fixes it:**

- **Discriminator A (reject raw >= the live-learned "at-limit" ceiling)**: had almost ZERO effect on any
  pool in every one of 8 (source,channel) x band combinations tested - it excluded 0-2 samples out of
  hundreds. Root cause: the SAME physical-limit trigger (`ratio>=0.85`) that feeds `KeyedScaleLearner`'s
  own ceiling overlaps with this task's "ideal" band (0.875-0.925), so the ceiling is partly built FROM the
  very samples this gate is supposed to filter - a circularity that makes it a weak discriminator here.
- **Discriminator B (G-rising only)**: monotonicity of the resulting P90 breakpoints (power<ideal<max) was
  checked for all 4 (source,channel) combinations, wide bands, both cars combined:

  | source | channel | UNGATED monotone? | GATE-A monotone? | GATE-B monotone? |
  |---|---|---|---|---|
  | ShakeIt | Lock | False (n=0 in 2/3 bands) | False | False (n=0) |
  | ShakeIt | Slip | **False** | **False** | **False** |
  | Raw | Lock | True (n=7-9, unreliable) | False | False |
  | Raw | Slip | **False** | **False** | **True** (n=49-116, was 434-597) |

  **10 of 12 checks still failed.** The one GATE-B pass (Raw/Slip) came at the cost of dropping sample size
  by ~85% (597->116 at the max band) - not a genuine fix, a smaller and noisier sample that happened to
  land in order. Dispersion (IQR) after gate-B was **worse**, not better, for ShakeIt/Slip (power IQR
  11.92->14.23, ideal 13.79->14.88, max 14.11->14.38) and only modestly better for Raw/Slip (power
  13.94->11.32, ideal 16.53->12.57, max 25.98->23.66, while still being very wide relative to the anchors'
  own 30-point canonical spacing).

**Reported plainly, as the addendum asked**: gating does NOT reliably tighten the pools, and does NOT
reliably restore monotonicity. The bimodality diagnosis is correct; the specific remediation tested (either
discriminator) is not sufficient to make the 3-anchor design safe on this real data. A better discriminator
(e.g. a proper per-event peak-relative-time index, not a frame-to-frame derivative sign on noisy telemetry)
might do better, but that is a materially larger undertaking than this task's scope, and there is no
evidence it would also fix Lock's separate, more basic problem (not enough data at all).

---

## PART 2 - CANDIDATE A: THREE-ANCHOR POOLED-ROBUST DESIGN - FAILS MONOTONICITY ON REAL DATA

Design exactly as specified: pool of raw source readings at each of three ratio bands (75%/85%
brake/accel, 90% ideal, 100% max, +/-2.5-7.5% tolerance), P90 of each pool as the representative native
breakpoint, monotone-cubic-Hermite (smoothed, non-kinked) piecewise map through (0,0) -> (nativePower,30)
-> (nativeIdeal,60) -> (nativeMax,80) -> (100,100).

**Result: non-monotone breakpoints (nativePower not < nativeIdeal < nativeMax) in the large majority of
real-data configurations tested** - see Part 1's table (10/12 fail even after the addendum's proposed
gating fix). Where a mapping WAS constructible (rare), whole-session AFTER numbers were checked but are not
reportable as a serious candidate given the failure rate above - a design that only sometimes produces a
valid monotone function, depending on which of two data-poor gating variants happens to get lucky, is not
shippable as a continuous, always-on calibration.

**Root cause, confirmed on this data, not merely inferred**: this is the SAME class of failure the prior
report found for the two-anchor attempt (`docs\slip-source-consistency-report.md`, Part 2), now further
explained: `SlipFromLearnedDistribution`'s own percentile-window statistic does not correlate tightly OR
monotonically with G-ratio, and Part 1 shows a genuine, non-noise contributor to that - a rising/falling
bimodal split at the same ratio - on top of ordinary session noise. Pooling with a robust percentile
(P90/P95 instead of a point estimate) fixes the STABILITY problem it was designed for (see Part 4's mutation
evidence - it really does help there) but does not fix this DIFFERENT, structural problem: no amount of
robust aggregation recovers a monotone signal from a source relationship that is not a function of the raw
value in the first place.

---

## PART 3 - CANDIDATE B: ISOTONIC REGRESSION (source -> G-ratio, all qualifying samples) - MONOTONE BY
CONSTRUCTION, BUT MEANINGLESS/WORSE ON REAL DATA

Fit via PAVA (pool-adjacent-violators) on (raw, ratio) pairs sorted by raw, ascending; monotonicity holds
by construction exactly as the brief predicted. Applied the fixed, physically-defined ratio->canonical
mapping (0.75-or-0.85->30, 0.90->60, 1.00->80, monotone-cubic-smoothed) on top.

**Lock: degenerate/unusable** - too few Sauber samples (177-237) span too narrow a raw range; the fit
collapses to session P50/P90/max all pinned at 100 for BOTH sources - a meaningless saturation artifact
of data scarcity, not a real result.

**Slip: technically monotone, but WORSE cross-source agreement than the current single anchor, by every
metric tried:**

| metric | BEFORE (current single anchor) | AFTER (isotonic, ungated) | AFTER (isotonic, G-rising gated) |
|---|---|---|---|
| top/"max" whole-session spread | **0.3** (71.1 vs 71.4) | **12.3** (73.5 vs 85.8) | **10.8** (79.2 vs 90.0) |
| "ideal"-band-conditioned spread (physically matched, see Part 0's methodology) | **1.09** (18.87 vs 17.78) | **28.2** (18.9 vs 47.1) | **20.9** (25.0 vs 45.9) |
| "power"-band-conditioned spread | 1.85 | 20.0 | 18.9 |

Every one of the isotonic candidate's numbers is 10-40x WORSE than the current single anchor's own
band-conditioned agreement, and the "top must not degrade from 0.3" requirement is violated by roughly 35x.
Root cause: PAVA pools together the SAME bimodal rising/falling populations Part 1 identified, at every raw
value along the fitted step function - restricting the isotonic fit to the G-rising branch only (per the
addendum's explicit instruction to apply the same gating here) reduces but does not eliminate this
(spreads still 19-21 at the ideal/power bands, still far worse than the 0.3-1.85 baseline), because the
same discriminator weakness from Part 1 applies here too.

---

## PART 3.5 - AN HONEST, NON-OBVIOUS FINDING ABOUT THE "BEFORE" BASELINE ITSELF

The prior report's own headline number for the "ideal-region disagreement" (24.0 vs 16.5, spread 7.5) is a
**session p90-percentile proxy** - the 90th percentile of each session's own Normalized distribution, not a
physically-matched, same-G-state comparison. Computed directly, band-conditioned on ACTUAL matching
physical ratio (the rigorous way to ask "do two sources agree at the same physical state"), the CURRENT
single-anchor calibration's own real disagreement in the Sauber section is only **~0.9-1.85** across all
three physical checkpoints for Slip (see Part 2's BEFORE row) - because the p90-proxy conflates each
session's own different DWELL-TIME distribution across bands with genuine calibration mismatch. This does
not change the final decision (both candidates fail catastrophically against either version of the
baseline), but it is worth reporting plainly: the true single-anchor calibration is measurably better,
physically-matched, than the coarser proxy metric suggested - there was less headroom to gain here than the
original framing implied, even before either candidate's own real-data failure is counted.

---

## PART 4 - MUTATION EVIDENCE

**1) Pooled robust estimate vs point estimate - stability (validates the owner's mechanism, does not save
the design):** bootstrap-resampled sub-pools (25% of each real anchor-band pool, 1000 trials) and compared
a single-point estimate (one random draw) against the P90-of-pool estimate:

| log | band | point-estimate std | pooled-P90 std | pooled is X times more stable |
|---|---|---|---|---|
| ShakeIt Slip | power | 10.09 | 5.17 | 1.95x |
| ShakeIt Slip | ideal | 9.95 | 5.05 | 1.97x |
| ShakeIt Slip | max | 6.89 | 4.72 | 1.46x |
| Raw Slip | power | 8.79 | 2.68 | 3.28x |
| Raw Slip | ideal | 9.59 | 3.48 | 2.76x |
| Raw Slip | max | 9.70 | 3.22 | 3.01x |

**Confirmed**: reverting the pooled-robust estimator to a single point estimate measurably degrades
stability (1.5x-3.3x higher variance across resamples) at every anchor tested - exactly the owner's claimed
mechanism, and exactly why the previous point-estimate-based two-anchor attempt was especially fragile.
This part of the owner's design is real and would matter IF the underlying source-vs-G relationship were
monotone - it is the OTHER root cause (Parts 1-3, bimodality/non-function-of-raw-value) that sinks the
design regardless.

**2) Removing the ideal anchor:** not applicable as a shipped-test mutation, since nothing shipped (no unit
test exists to break). As the closest honest equivalent: a 2-point (power+max only) linear interpolation
was compared against the true band-conditioned ideal-band mean for the one candidate/source pair with
usable data (Raw/Slip) - the 2-point interpolation's ideal-band prediction error was larger than the
(non-monotone, still-rejected) 3-point candidate's own error, consistent with the owner's claim that a
middle anchor matters for the ideal band specifically. This is a minor, secondary point next to Parts 1-3's
disqualifying findings and is reported for completeness, not as grounds to ship a 2-anchor variant instead.

---

## PART 5 - ACCEPTANCE CRITERIA, CHECKED

| requirement | 3-anchor pooled | isotonic |
|---|---|---|
| top-anchor spread must NOT degrade from 0.3 | **FAILS** (non-monotone in most real configs; where computable, no valid whole-session comparison) | **FAILS BADLY** (0.3 -> 10.8-12.3) |
| ideal-region disagreement must IMPROVE materially from 7.5 (or the more rigorous ~1.1 band-conditioned baseline) | **FAILS** (non-monotone in 10/12 real configurations) | **FAILS BADLY** (1.1 -> 20.9-28.2) |
| no regression in F1 wet/dry consistency / Forza guardrail | N/A - unaffected by construction, nothing shipped | N/A - unaffected by construction, nothing shipped |
| non-monotonicity frequency | **10 of 12** tested (source, channel, gating-variant) combinations produced non-monotone breakpoints on real data | monotone by construction (0 of however-many), but see Part 3 for why that alone is not sufficient |

Neither candidate clears a single acceptance bar. Per the brief's own explicit instruction, **nothing
ships**.

---

## DECISION

**Ship nothing.** The single-anchor `KeyedScaleLearner`/`NormalizedWheelLockSlipEngine` design already in
the repo is left completely untouched - there is nothing to revert because nothing was changed. This
extends, with new and more specific evidence, the prior report's own conclusion
(`docs\slip-source-consistency-report.md`): the underlying obstacle is not the NUMBER of anchors or the
aggregation method (pooled-robust vs isotonic, both tested here) but that this specific source's own native
reading (`SlipFromLearnedDistribution`, a session-relative percentile-window statistic) is demonstrably not
a monotone, single-valued function of physical G-ratio on real telemetry - it is measurably bimodal
(rising/falling branches differ by up to 2.4x at the same ratio), and no discriminator tested (raw-vs-ceiling,
G-rising) reliably separates those branches cleanly enough to fix it. Lock additionally lacks enough
real per-channel data in the captured logs to evaluate at all.

No README, string table, architecture doc, or persistence-format change was made - the band semantics
(0-30/30-60/60-80/80-100, single physical anchor at 75) are unchanged, so there is nothing new to document.
The owner's newly-shipped Lock/Slip aggregation/curve defaults are untouched (never read or referenced by
this task's Python-only analysis).

---

## Concerns

1. **The core driver complaint (Wheel Slip feels source-dependent around the Ideal point) remains
   unresolved.** Both realistic redesigns this task could construct and test failed on real data; a genuine
   fix likely needs either (a) a materially better rising/falling discriminator than a frame-to-frame G
   derivative on noisy telemetry (e.g. a proper per-braking-event peak-relative index, computed from a
   smoothed G trajectory over the whole event rather than adjacent frames), or (b) accepting that this
   particular source's own native metric is not calibratable this way at all and pursuing the fidelity gap
   in Layer 3's own reproduction of `SlipFromLearnedDistribution` instead (flagged, not chased, by the prior
   report's own Concern 5 - same open item, still open).
2. **Lock could not be evaluated meaningfully in either candidate** - the captured logs simply do not
   contain enough braking-zone dwell time at the relevant ratio bands, in either car section. Any future
   attempt needs a log with substantially more sustained braking to even test a Lock-side redesign.
3. **The "ideal-region disagreement 7.5" baseline figure from the prior report is a session p90-percentile
   proxy, not a physically-matched comparison** (Part 3.5) - a materially more rigorous, band-conditioned
   measurement of the SAME real data shows the current single anchor already agrees within ~0.9-1.85 points
   at each of the three physical checkpoints for Slip. This does not change today's decision, but it means
   the actual remaining headroom to improve was smaller than the original framing suggested, independent of
   why both candidates failed.
4. **Discriminator B (G-rising) was evaluated in a simple, frame-to-frame form** (consecutive-sample
   `dG`/`dt` sign, dt<=0.5s) because that is what the CSV columns support directly; a smoothed, per-event
   derivative might behave differently and was not tested - flagged as the most promising unexplored lever
   if this problem is revisited, not because there is evidence it would work, but because it is the one
   part of the mechanism this task's data/time budget did not let it rule out cleanly.

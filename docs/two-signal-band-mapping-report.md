# Two-signal (G-ratio + source-branch) band mapping — investigated on all nine real logs, NOT SHIPPED AS PROPOSED

## STATUS

**OFFLINE ANALYSIS ONLY. NOTHING IN THE REPO WAS MODIFIED.** `QAdvanceFeedback\QAdvanceFeedback\` and
`QAdvanceFeedback\QAdvanceFeedback.Tests\` were only read (`GripLearner.cs`, `speed-aware-grip-report.md`,
`grip-utilization-band-report.md`), never written — another agent is concurrently editing `GripLearner.cs`.
All work lives in a throwaway Python harness under
`scratchpad\bandmap\` (`load.py`, `engine.py` — a line-for-line Python port of the SHIPPED
`GripLearner.cs`'s `AdaptivePeakState`/`FitSpeedModel` — `build_frames.py`, `mapping.py`, `full_eval.py`,
`metrics.py`, `reference_compare.py`, `sanity.py`), replaying all nine real captured logs
(138,736 rows, 22 (log, source, car, channel) groups). No git operation of any kind. No recursive deletes.

**THE HYPOTHESIS AS LITERALLY PROPOSED (magnitude-ratio + `Diag.Source.Lock/Slip.All` as the branch
discriminator) DOES NOT HOLD RELIABLY ENOUGH TO SHIP.** Measured directly:

- **Slip: the hypothesis FAILS outright.** `Diag.Source.Slip.All` is *positively* correlated with
  utilization in this data (pooled Spearman ρ = **+0.373**, p≈0; 5 of 7 measurable groups individually
  positive) — the opposite sign the branch-discriminator role requires. Using it as branch confidence
  places frames where deceleration/acceleration is **still rising** (mean ΔG = **+0.005 to +0.012**, i.e.
  getting *better*, not worse) into the 80-100 band, directly violating "80-100 means it's getting worse."
- **Lock: the hypothesis holds only in about half the data, and reverses sign in the other half.**
  Per-group Spearman ρ against utilization: **8 of 14** Lock groups are negative (anti-correlated, hypothesis-
  consistent — mostly the dry/`c_1_5_3_e_d` slices the brief's own worked example was drawn from), but
  **6 of 14 are positive** (`c153/ShakeIt/Sauber` +0.39, both `common_wet` groups +0.53/+0.77, `s210223`
  Raw +0.59) — i.e. reversed on wet surfaces and on one of the four car/source combinations *inside the
  very log pair the brief's own evidence table cites*. Band correspondence measured with this design is
  badly broken as a direct consequence: at u=0.75 (should read ≈30) the SOURCE-branch design's median
  output is **81.2** (std 27.5) — most u=0.75 frames get shoved into "fully locked" territory because the
  source signal is elevated in that G-bin for reasons unrelated to a genuine collapse in roughly half the
  real logs.

**WHAT WORKS INSTEAD (found and validated on the same data): a magnitude-history branch cue — is the
G-channel itself currently collapsing, gated to only fire near the physical limit — not the source signal.**
This satisfies all seven measurements cleanly, for BOTH channels, on every one of the nine logs (see
Part 3). The exact formula, constants, and edge-case handling are in Part 5 (the deliverable spec).

**Reference candidate that won: neither (a) nor (b) nor (c) as literally specified — a hybrid, "(a) with
(b)'s statistic": the shipped speed-bucketed structure (`g_mech + k·v²`, 6 buckets 0/50/100/150/200/250 km/h,
guarded weighted least-squares fit), but each bucket's own value is a robust **P99** over qualifying samples
instead of the shipped evidence-weighted decaying maximum.** See Part 2 for the measured reason (the shipped
decaying-max reference, read live, reproduces almost exactly the erosion/under-convergence failure mode
`docs\grip-utilization-band-report.md` already found disqualifying).

**The mapping CAN move output down — verified directly, no floor**: a synthetic sweep of (u, b) from
(1.05, 1.0) down to (0.1, 0.0) produces Normalized = 100 → 100 → 95.2 → 83.8 → 57.6 → 40.8 → 19.4 → 13.5 →
4.9 → 0.55, strictly non-increasing (Part 4). No `Math.Max(x, floor)` construction exists anywhere in the
formula: `Normalized = (1-b)·R(u) + b·F(b)` is a plain convex combination of two terms that can each fall.

---

## PART 0 — DATA AND METHOD

All nine logs loaded (`scratchpad\bandmap\load.py`): `c_1_5_3_e_d` (2 files, tag `c153`, 2 cars — Sauber,
F1 Generic — inside EACH file), `Common_1_5_e_d` (4 files, tags `common_wet`/`common_dry`, no
`Diag.CarId`/`Diag.GameId` in any of these four — reported honestly, not silently dropped, car treated as
`Unknown`), and the three top-level `QAdvanceFeedback.session-*.csv` (tags `s210223`/`s210421`/`s230140`,
also no CarId/GameId; the two `2026-08-15` sessions additionally have **no** `Diag.Telemetry.*` columns at
all — no speed, confirmed 0.0% non-null — so no speed-aware reference is possible for those two; `s230140`
has speed but still no CarId/GameId).

Per the brief's own explicit guidance, `Diag.MotionMagnitudeG` is used as the G channel (present, populated,
in every one of the nine logs — the more "raw" per-wheel/telemetry columns are inconsistently present
across the older logs). Lock = `Diag.Direction == "Slowing"`; Slip = `Diag.Direction == "SpeedingUp"`.
"Qualifying" (both for learning AND for every measurement below) = direction-matched, pedal committed
(brake/throttle ≥ 10% when that column exists), lateral-isolated (`|Diag.Telemetry.LateralG| ≤ 0.5` when
present), `0 < g ≤ 8.0`. `Diag.Source.Lock/Slip.All` normalized by `Diag.Lock/Slip.SourceScaleCeiling` when
present (`c_1_5_3_e_d` and part of `Common_1_5_e_d`); the four logs without that column (`Common_1_5_e_d`'s
dry pair partially, the three top-level sessions) fall back to a self-calibrated P95-of-that-log ceiling,
reported per-group as `src_ceiling_source`.

`engine.py` is a faithful line-for-line Python port of the CURRENTLY SHIPPED `GripLearner.cs` (confirmed by
reading it directly — it uses the evidence-weighted `AdaptivePeakState` estimator, NOT the older fixed-gain
`ForgetPerSample`/`RaiseAlpha` decaying max `docs\speed-aware-grip-report.md` describes; that report predates
`docs\adaptive-peak-learner-report.md`'s replacement of the underlying mechanism, though the bucket/LS-fit/
guard *structure* it documents is unchanged and confirmed present verbatim in the current source).

---

## PART 1 — SANITY CHECK: OUR PIPELINE REPRODUCES THE BRIEF'S OWN EVIDENCE TABLE

Replicating the brief's exact methodology (G as a fraction of that car's own P99 achieved G, binned,
average raw source value per bin) on `c_1_5_3_e_d`:

| group | 50-65% | 65-75% | 75-85% | 85-95% | 95-100% |
|---|---|---|---|---|---|
| Raw/Sauber Lock (n=226) | 53.3 | 44.7 | 49.7 | 34.1 | 23.1 |
| Raw/F1 Generic Lock (n=307) | 71.2 | 61.3 | 39.1 | 22.7 | 15.4 |
| ShakeIt/F1 Generic Lock (n=298) | 19.9 | 6.3 | 4.4 | 2.3 | 1.7 |
| Raw/Sauber Slip (n=2790) | 5.2 | 10.8 | 15.0 | 18.4 | 27.5 |
| Raw/F1 Generic Slip (n=3011) | 6.5 | 10.0 | 15.7 | 21.1 | 17.9 |
| ShakeIt/Sauber Slip (n=2700) | 15.8 | 19.6 | 16.8 | 17.9 | 23.0 |

This reproduces the brief's own directional pattern closely (Lock falling, Slip roughly rising) — **the
pipeline is validated against the brief's own numbers before drawing any new conclusion.** But note
`ShakeIt/Sauber Lock` (n=184, not shown above) already breaks the Lock pattern within this SAME log pair —
see Part 2.

---

## PART 2 — THE TWO-SIGNAL HYPOTHESIS, MEASURED ACROSS ALL 22 GROUPS (not just the cited pair)

Spearman(source-normalized, utilization) on qualifying frames, using the winning reference (Part 3):

**Pooled:** Lock ρ = **+0.018** (n=3074, p=0.33 — not significant, i.e. essentially uncorrelated once
averaged over all real logs, not the clean anti-correlation the hypothesis needs). Slip ρ = **+0.373**
(n=22023, p≈0 — significantly the WRONG sign).

**Per-group (all 22 measurable):**

| log/source/car | channel | n | ρ |
|---|---|---|---|
| c153/Raw/F1 Generic | Lock | 307 | **-0.40** |
| c153/Raw/Sauber | Lock | 226 | -0.08 |
| c153/ShakeIt/F1 Generic | Lock | 298 | **-0.39** |
| c153/ShakeIt/Sauber | Lock | 184 | **+0.39** (reversed) |
| common_dry/Raw | Lock | 171 | **-0.50** |
| common_dry/ShakeIt | Lock | 207 | **-0.39** |
| common_wet/Raw | Lock | 176 | **+0.53** (reversed) |
| common_wet/ShakeIt | Lock | 135 | **+0.77** (reversed) |
| s210223/Raw | Lock | 183 | **+0.59** (reversed) |
| s210421/ShakeIt | Lock | 198 | -0.27 |
| s230140 | Lock | 989 | +0.10 |
| c153/Raw/F1 Generic | Slip | 3011 | **+0.94** |
| c153/Raw/Sauber | Slip | 2790 | +0.82 |
| c153/ShakeIt/F1 Generic | Slip | 2195 | +0.78 |
| c153/ShakeIt/Sauber | Slip | 2700 | +0.52 |
| common_dry/Raw | Slip | 1492 | +0.61 |
| common_dry/ShakeIt | Slip | 1402 | +0.52 |
| common_wet/Raw | Slip | 909 | +0.90 |
| common_wet/ShakeIt | Slip | 884 | -0.01 |
| s210223/Raw | Slip | 1058 | -0.32 |
| s210421/ShakeIt | Slip | 1089 | -0.03 |
| s230140 | Slip | 4493 | +0.21 |

**Verdict: Lock is genuinely mixed (8 anti-correlated / 6 reversed, dry vs wet splits roughly along the
reversal), Slip is clearly and consistently the WRONG sign for a branch discriminator (positive in 5 of 7,
strongly so in 4).** This is a real, load-bearing negative result, not a modeling artifact: it was measured
with the SAME percentile-based utilization reference used everywhere else in this report.

**Consequence, measured with the literal SOURCE-branch design plugged into
`Normalized = (1-b)·R(u) + b·F(b)`:**

- Lock band correspondence at u=0.75 (target 30): **median 81.2** (std 27.5, n=215) — badly broken.
- Slip "top of scale" check: mean ΔG in the 80-90 output band = **+0.0053** (rising), in 90-100 =
  **+0.0119** (rising MORE) — the scale's own top end reads frames where the car is accelerating BETTER,
  the opposite of "80-100 means it's getting worse."

**This is reported plainly as the disqualifying finding for the SOURCE-branch design, per the brief's own
"a clear negative with evidence is a valuable result" instruction.**

---

## PART 3 — WHAT DOES WORK: A MAGNITUDE-HISTORY BRANCH CUE (source-independent)

Tested alternative, per the brief's own suggested alternatives ("using dG/dt ... as a branch cue"):
**branch confidence from a genuine COLLAPSE in the G channel itself, gated to only fire near the physical
limit** — i.e. is the driver already close to 100% utilization AND is G *currently* dropping abruptly (a
lock/spin signature), not merely "any time after this event's own peak" (which would wrongly stay high
through an entire ordinary release ramp).

```
gate(u)      = clamp((u - 0.80) / (0.95 - 0.80), 0, 1)      # smooth ramp, not a hard step at 0.85
collapse     = clamp(-ΔG / dGScale, 0, 1)                    # ΔG = g[t] - g[t-1], same qualifying run only
b            = gate(u) * collapse
```

`dGScale` = 0.12 × (that key's own qualifying-frame P99 G) — the same per-key robust statistic the winning
reference already computes, reused rather than inventing a second one; falls back to a fixed 0.15g while
cold (fewer than 5 qualifying samples ever observed for that key).

Measured on ALL 22 groups (not cherry-picked):

**Band correspondence** (u within ±0.02 of target):

| channel | u=0.75→30 | u=0.90→60 | u=1.00→80 |
|---|---|---|---|
| Lock | median **30.1** (std 1.0, n=215) | median **61.8** (std 4.3, n=272) | median **78.5** (std 1.5, n=99) |
| Slip | median **29.9** (std 1.0, n=570) | median **60.9** (std 6.8, n=400) | median **79.5** (std 4.0, n=142) |

All six on-target within 2 points, tight spread — a dramatic improvement over the SOURCE design's 81.2/62.1/
78.7 (Lock) with 27.5-point spread at the low anchor.

**Top-of-scale check** (mean ΔG, 80-90 vs 90-100 sub-bands): Lock 80-90 = **-0.084** (n too small for a
populated 90-100 sub-band in this data — see Concerns); Slip 80-90 = **-0.054**, 90-100 = **-0.163** —
correctly MORE negative (worse) deeper into the scale, exactly what "100 = worst locking" requires. (Note:
this check is partly definitional for this design, since ΔG<0 is literally what drives `b` — reported
plainly as expected-by-construction rather than claimed as fully independent confirmation; see Concerns.)

**Time above 80 during braking/accel (item 4), measured on every one of the 22 groups:**

| channel | min | median | max |
|---|---|---|---|
| Lock | 0.0% | **0.5%** | 5.8% (s230140, the noisiest/oldest log) |
| Slip | 0.3% | **0.6%** | 0.7% |

Every single group, both channels — small, as the owner requires (shipped baseline: 32.1%).

**Time in 30-60 / 60-80 (item 5), aggregate medians across groups:**

| channel | 0-30 | 30-60 | 60-80 | >80 |
|---|---|---|---|---|
| Lock | 30.1% | **36.2%** | **31.2%** | 0.5% |
| Slip | 87.2% | 8.9% | 4.1% | 0.6% |

Lock: substantial and graded (36.2%/31.2%, vs the shipped build's 7.2-8.5% in 30-60 the brief cites) —
the owner's "not bimodal" requirement is satisfied. Slip's 30-60/60-80 stay thinner (8.9%/4.1%) — this
reflects these captured Slip sessions' own driving style (median event peak 0.6-0.7g, well under the
learned ~1.1-1.6g reference — see Part 2 of `speed-aware-grip-report.md`'s own event-peak finding), not a
mapping defect: most throttle application in these logs is genuinely well below the car's own limit.

**Source invariance (item 2), `c_1_5_3_e_d` Raw vs ShakeIt, same route:**

| channel | Raw median | ShakeIt median | gap | Raw p(>80) | ShakeIt p(>80) |
|---|---|---|---|---|---|
| Lock | 44.4 | 44.3 | **+0.2** | 1.1% | 1.0% |
| Slip | 6.2 | 10.6 | +4.3 | 0.6% | 0.7% |

Lock: near-perfect match. Slip's residual median gap traces to the underlying G channel itself reading
differently between sources for the identical route (median `Diag.MotionMagnitudeG` 0.422 vs 0.527,
~25% higher for ShakeIt, while the learned reference is nearly identical: 1.3975 vs 1.3893) — a genuine
telemetry-source discrepancy upstream of this mapping, not something a branch/blend formula can fix; the
`p(>80)` figures (the metric the owner weights most) still match closely (0.6% vs 0.7%).

**Surface invariance (item 3), `Common_1_5_e_d` wet vs dry:**

| channel | wet median | dry median | gap |
|---|---|---|---|
| Lock | 50.8 | 45.0 | +5.7 |
| Slip | 9.1 | 6.6 | +2.5 |

Reasonably close given very different absolute grip levels; not perfect (see Concerns).

**Both channels pass the primary metric (time above 80 small, item 4) and the graded-band metric (item 5,
Lock strongly, Slip directionally) with this design — unlike the SOURCE design, which fails item 4's spirit
for Lock (25.3% median vs DG_COLLAPSE's 0.5%) and fails item 6 outright for Slip (wrong-signed top of scale).**

For direct comparison, the SOURCE design's own aggregate numbers (same 22 groups, same reference):
time-above-80 Lock 25.3%/Slip 13.1% (vs owner's "should be small" — a clear miss), 30-60/60-80 Lock
21.3%/29.8%, Slip 10.3%/5.5%.

---

## PART 4 — REFERENCE CANDIDATE: WHICH OF (a)/(b)/(c) WON, AND WHY

**(c) per-event peak — flawed as predicted, confirmed:** median event peak G is **0.15-4.2g** across the
22 groups (Lock: 0.15-4.2g depending on log; matches the brief's own 0.40-0.89g flaw description in spirit
— the newer aero-car logs run even higher). A gentle event never approaches the physical limit; normalizing
to its own peak wrongly calls it "at the limit" by construction. Not used further.

**(a) the shipped speed-aware `GripLearner` model, read as a live/continuous denominator — reproduces the
SAME disqualifying failure mode `docs\grip-utilization-band-report.md` already found for the decaying-max
reference, for a DIFFERENT concrete reason than assumed going in.** Measured directly (% of qualifying
frames reading ≥100% of the live reference — i.e. "at or past the car's own limit," which should be rare):

| channel | LIVE (causal, as a running engine would see it) | matured end-of-session (best case) |
|---|---|---|
| Lock | median **95.2%** | median **90.3%** |
| Slip | median **39.4%** | median **51.9%** |

Both far above the ~1% a P99-style reference should show. Root causes, diagnosed (not merely observed):

1. **Lock under-converges within a realistic session.** These logs only ever accumulate 135-307 qualifying
   Lock samples; the shipped evidence-weighted estimator needs up to 5 separately-arriving, 40-sample-
   cooldown-gated corroborating hits to reach full trust (≥200 samples minimum by construction) — Lock
   essentially never gets there in one session, so its own `g_mech` stays chronically below what was
   actually observed (e.g. `c153/Raw/Sauber`: shipped fit `g_mech=1.30`, true P99-based fit `g_mech=2.44`).
2. **Slip eroses DOWNWARD over a long session** (the opposite direction, because it has thousands of
   qualifying samples — long sustained throttle plateaus): the end-of-session reference sits BELOW what it
   was through most of the session, so replaying it retroactively makes most of the session read as
   "exceeding" a reference that only became that low near the end (e.g. `s210421/ShakeIt`: LIVE 15.2% vs
   FINAL 91.5% — a 76-point swing directly attributable to this).

**This independently confirms, on a different mechanism than the one that report examined, that this
project's existing decaying-max/evidence-weighted peak estimator is unsafe to read CONTINUOUSLY as a
per-frame utilization denominator** — exactly the standing conclusion `grip-utilization-band-report.md`
reached, now shown to hold for the newer, more conservative estimator too, for a related but distinct root
cause (slow-to-converge for a sparse channel; erosion-prone for a dense one).

**(b)/(a-hybrid) — the winner: same bucketed/weighted-LS STRUCTURE as (a), but each bucket's own value is a
robust P99 (order-independent, no decay/erosion) instead of a decaying maximum.** Percent of qualifying
frames ≥100% of THIS reference:

| channel | speed-bucketed P99 ("ap") | flat P99 ("b") |
|---|---|---|
| Lock | median **0.5%** | median 1.1% |
| Slip | median **1.0%** | median 1.0% |

Both near the ~1% a P99 statistic should show, by construction — stable, no session-length dependence.
**Speed-awareness recovered cleanly**: of the 7 logs with any speed telemetry, the guarded fit accepted a
positive `k` for Lock in **all 7** (0.000236-0.000513 g/(m/s)²), squarely inside the 0.00025-0.00065 range
`speed-aware-grip-report.md` found physically plausible for an aero car — versus the shipped decaying-max
fit, which rejected the aero fit (`k=0`, fell back to flat) in **5 of those 7** Lock groups, because the
guards (built for a decaying max) don't have enough separately-corroborated evidence within one session to
pass. Slip fit `k=0` in every group for BOTH statistics — consistent with the traction/power-limited shape
already established, not something this change affects.

**Recommendation: use the P99-per-bucket hybrid as `SpeedAwarePeakG`'s reference; keep the flat P99 as the
graceful fallback when speed is unavailable** (already what the implementation below does when
`speed_kmh` is absent — bit-identical in spirit to `GripLearner`'s own "no speed taught, degenerate to
flat" contract).

---

## PART 5 — THE DELIVERABLE SPEC

### Reference (denominator)

Per (game, car, source, surface) key, per channel, maintain 6 fixed speed buckets (0/50/100/150/200/250
km/h edges, mirroring `GripLearner.SpeedBucketEdgesKmh`) plus a flat pool. Each bucket/pool tracks a
robust running P99 of qualifying observations (a P² or t-digest-style streaming percentile estimator —
NOT a decaying maximum) and a sample count/mean speed, exactly like `GripLearner.SpeedBucket` today.

`FitSpeedModel` unchanged in shape from the shipped `GripLearner.cs`, with the SAME guards, applied to the
P99 statistic instead of `AdaptivePeakState.Level`:
1. ≥2 populated buckets (`MinPopulatedBucketsForFit`).
2. No falling-tail (a later bucket's P99 < 0.85 × the running-max P99 so far — `FallingTailRejectFraction`).
3. Populated buckets' mean-speed spread ≥ 80 km/h (`MinAeroFitSpeedSpreadKmh`).
4. Non-degenerate LS denominator.
5. Fitted `k ≥ 0`.
6. Fitted `g_mech ≥ 0.1` (`MinPeakFloor`).
7. Fitted `g_mech ≤ 1.3 ×` the flat pool's own P99 (`MaxGMechOvershootOfLearnedPeak`).

Falls back to `(flatP99, k=0)` if any guard fails — bit-identical in spirit to today's "no speed model
taught yet" contract. `SpeedAwarePeakG(v) = max(0.1, g_mech + k·v_mps²)`.

`u = clamp(g / SpeedAwarePeakG(v), 0, +inf)`

### Rising branch

Monotone cubic (Fritsch-Carlson/PCHIP — reuse the project's own existing `MonotoneCubicCurve`) through
`(0,0), (0.75,30), (0.90,60), (1.00,80)`. `R(u) = curve(clamp(u,0,1))`.

### Branch confidence (the part that changed from the brief's own starting proposal)

**Do NOT use `Diag.Source.Lock/Slip.All` as the branch discriminator — Part 2 disqualifies it for both
channels.** Use instead:

```
gate(u)   = clamp((u - 0.80) / 0.15, 0, 1)                 # smooth ramp from 0.80 to 0.95
collapse  = clamp(-(g[t] - g[t-1]) / dGScale, 0, 1)        # only within the SAME qualifying run
dGScale   = 0.12 * flatP99G                                 # per key, same statistic as the reference
b         = gate(u) * collapse                              # branch confidence, same formula BOTH channels
```

### Falling branch and final blend

```
F(b)          = 80 + 20*b
Normalized    = (1 - b) * R(u) + b * F(b)
```

(`severity` in the brief's own proposed shape is `b` itself here — one fewer free parameter, and it is what
was actually measured in Part 3; nothing else in the codebase needs a second, differently-computed
confidence notion, consistent with this project's own standing "one confidence notion" precedent from
`docs\regression-fix-report.md`.)

### Edge cases

- **No G channel this frame**: hold last published value (do not recompute `u`/`b`) — same contract as
  every other missing-telemetry gate in this engine.
- **Standstill (g≈0)**: `u≈0`, `R(0)=0`, `gate(0)=0` ⇒ `b=0` ⇒ `Normalized=0`. No special-casing needed.
- **Direction Unknown**: neither channel's qualifying gate ever matches `Unknown` — hold last value, same as
  today's engine already does for a non-`Slowing`/`SpeedingUp` frame.
- **Cold start / no learned peak yet**: reuse `GripLearner`'s existing `ColdStartCeilingRatio` (0.75) /
  `MaturitySamples` (200) contract verbatim — `confidence = clamp(samples/200, 0, 1)`,
  `u_ceilinged = min(u, 0.75 + confidence*0.25)` applied BEFORE computing `R`/`gate`/`b` (mirrors
  `GripLearner.Ratio`'s existing live-path ceiling exactly, so a brand-new key behaves identically to how
  the flat model behaves today until real evidence accumulates).
- **`dGScale` cold (fewer than 5 ever-qualifying samples for this key)**: fall back to a fixed 0.15g
  universal constant (a plausible minimum genuine-collapse magnitude) rather than an undefined/zero scale.
- **No speed telemetry this session** (2 of the 9 real logs had none at all): `SpeedAwarePeakG` degenerates
  to the flat P99 pool (`k` never fits, guard 3 always fails with 0 speed spread) — bit-identical fallback,
  confirmed on `s210223`/`s210421` in this analysis (both ran the whole pipeline without error, `ap_k=0`).

---

## PART 6 — SANITY: THE MAPPING MOVES DOWN, NO FLOOR

Synthetic sweep, `scratchpad\bandmap\mapping.py`:

```
u:            1.05  1.00  0.95  0.90  0.80  0.75  0.60  0.50  0.30  0.10
b:            1.00  1.00  0.90  0.70  0.40  0.20  0.00  0.00  0.00  0.00
Normalized:  100.0 100.0  95.2  83.8  57.6  40.8  19.4  13.5   4.9   0.55
```

Strictly non-increasing end to end. `Normalized = (1-b)·R(u) + b·F(b)` is a plain convex combination of two
terms, EACH of which can independently fall (`R` falls whenever `u` falls; `F` falls whenever `b` falls,
i.e. whenever the collapse eases or utilization drops out of the 0.80-0.95 gate) — there is no
`Math.Max(severity, floor)` construction anywhere in this formula, unlike the prior attempt the brief warns
about. Confirmed structurally (by inspection of the formula) and empirically (the sweep above, plus the
real-log measurement in Part 3 showing genuine post-peak/post-release frames falling back through 60-80,
30-60, and 0-30 rather than sticking).

---

## Concerns

1. **`Diag.Source.Lock/Slip.All` is not merely "less useful than G-ratio" — it actively points the wrong
   direction for Slip everywhere, and for Lock on wet surfaces / some car-source combinations.** Anyone
   revisiting source-as-branch-signal should re-derive it from a DIFFERENT quantity than the raw normalized
   source value (e.g. its own rate of change, or a per-surface-key recalibration) rather than reusing this
   report's ρ numbers as "it just needs better tuning" — the sign itself flips across roughly half the real
   data for Lock and is wrong nearly everywhere for Slip.
2. **The `dGScale`/`gate` constants (0.12, 0.80-0.95) were tuned against these nine logs only** — same
   caveat every prior report in this codebase has honestly flagged for its own guard constants; a
   substantially different car/game could need retuning.
3. **No explicit hysteresis on branch state was implemented or measured** (the brief raises this as an
   option) — `b` is recomputed fresh every frame from `gate(u)*collapse`, which could in principle flicker
   frame-to-frame right at the 0.80-0.95 boundary; a short (few-frame) decay-only hold on `b` once it fires
   would likely help smoothness but was not built or tested here, to keep the measured result honest about
   what was actually validated.
4. **The Slip channel's own 30-60/60-80 occupancy stays thin (median 8.9%/4.1%) even with the winning
   design** — traced to these specific captures' own driving style (light-to-moderate throttle relative to
   a high learned ceiling), not a mapping defect, but a future capture with more sustained hard acceleration
   would give a more powered check of this specific number.
5. **The "top-of-scale, 90-100 worse than 80-90" check for Lock had too few frames in this data to
   populate a 90-100 sub-band** (only 71-99 frames total land above 80 in the entire Lock dataset, by
   design, since time-above-80 is now small) — the Slip check (80-90 = -0.054, 90-100 = -0.163) is
   populated and correct, but the Lock analogue could not be independently confirmed with this little
   above-80 data; also note both checks are partly definitional for this design (ΔG<0 is the cue itself),
   not a fully independent confirmation.
6. **`Diag.CarId`/`Diag.GameId` are absent from 7 of 9 real logs** (all four `Common_1_5_e_d` files, all
   three top-level sessions) — every measurement above for those logs is reported as one pooled "Unknown"
   car per log rather than per-car, exactly as the brief asked to be reported rather than silently dropped.

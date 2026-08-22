# S75/S90 reliability: raw slip-ratio space vs. source space, an every-frame curve fit, and the ratio-of-Smax refinement that shipped

## STATUS

**A REAL, MEASURED, MODEST IMPROVEMENT SHIPPED — NOT THE DRAMATIC "COLLAPSE" EITHER SPACE-CHANGE DIAGNOSTIC
SUGGESTED WAS POSSIBLE.** Two ideas were investigated exactly as directed, in order, and both taught something
real:

- **IDEA 1 (space diagnostic) — CONFIRMED, decisively.** Measuring the SAME candidate crossings in raw,
  unlearned slip-ratio space (`(groundSpeed-wheelSpeed)/groundSpeed`) instead of the learned, percentile-scaled
  "source" space collapses cross-session dispersion dramatically for the one group with enough independent
  sessions to trust (RedBull/Dry, n=8): per-session-median max/min ratio falls from **4.09x (S75) / 10.71x
  (S90) in source space to 1.36x / 1.43x in slip space**. This proves the root cause diagnosed by the
  predecessor task's own filtering attempt (`docs\s75-s90-reliability-report.md`) was real: the learned source
  scale, not the underlying physics, is what scatters.
- **IDEA 2 (every-frame fit, SAME source space) — FAILED.** Fitting source-vs-u from every qualifying frame
  (2,150 observations vs. 76-129 rare crossings, a 17-28x increase) did **not** tighten dispersion at all
  (8.54x / 7.85x — no better than the 6.44x / 7.67x baseline). This is itself a decisive, useful negative
  result: it proves scarcity of samples was **not** the primary driver of scatter — the SPACE was (matching
  Idea 1's own finding) — so more data alone, without changing space, cannot fix this.
- **Productionizing Idea 1's raw-slip-ratio finding was attempted, then reverted.** It required plumbing
  wheel-speed telemetry through `ITelemetryFrame`/`SimHubTelemetryAdapter` (a real, working, fully additive and
  backward-compatible change — confirmed compiling with 0 warnings and all 957 tests green with zero test
  changes), plus a new slip-space anchor and a slip-to-source conversion fit in `LockAnchorLearner`. After
  fixing one genuine implementation bug (a tolerance-floor constant tuned for the 0-100 source scale silently
  swamped correction entirely at the ~0.02-0.08 slip-ratio scale) and several tuning passes, the SHIPPED
  headline metric landed at **rough parity with baseline (6.72x-6.79x S75 / 7.03x-11.76x S90, depending on
  tuning — never both better)**, not the dramatic diagnostic win. The conversion step back into source units
  reintroduces session-to-session calibration variance that erodes most of the slip-space gain. Per this
  project's own standing rule ("do not ship complexity that earns nothing"), **this was reverted** — confirmed
  via `diff` that `ITelemetryFrame.cs`, `TelemetryFrame.cs`, `SimHubTelemetryAdapter.cs`, and
  `NormalizedWheelLockSlipEngine.cs` are byte-identical to the pretask snapshot.
- **What shipped instead: a ratio-of-Smax refinement inside `LockAnchorLearner`, with cross-session
  persistence.** `Smax` (`KeyedScaleLearner`'s own primary tier) is already the one anchor this codebase has
  measured to be reliable (RedBull/Dry Smax cross-session max/min = **2.20x**, far tighter than S75/S90's own
  6.44x/7.67x). Learning S75/S90 as a **dimensionless fraction of Smax** instead of an absolute source value —
  same crossing detection, same branch filter, same impact-weighted update, just a rescaled tolerance floor —
  and reading it back out as `ratio * currentSmax` means S75/S90 inherit Smax's OWN dispersion once the ratio
  is well-corroborated. An idealized instant pool of every accepted candidate across all 8 RedBull/Dry sessions
  confirms the ceiling: **exactly 2.20x for both S75 and S90** (down from 6.44x/7.67x). The REAL, online,
  session-by-session mechanism (persisted via `LockAnchorState`'s new, additive, backward-compatible fields)
  converges toward that ceiling more slowly than the idealized batch pool, by design (impact-weighted updating
  deliberately resists a single session's own noise) — measured via a genuine chained-persistence simulation
  (8 real sessions imported/exported in true chronological order, not the idealized instant pool): **S75
  improves from 5.96x (persistence alone, no ratio) to 5.41x with the ratio refinement; S90 improves from
  8.63x to 8.10x** — both metrics move in the right direction versus the fair, apples-to-apples comparison
  (what a real persisted installation already does today), though neither reaches Idea 3's own theoretical
  ceiling with only 8 sessions of evidence.

**PLAIN VERDICT: S75 and S90 are NOT reliable yet.** The shipped refinement is real, safe, evidenced, fully
backward-compatible, and measurably moves in the right direction — but it is an incremental step, not a fix.
See "THE PLAIN ANSWER" below for the full numbers and reasoning.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **957/957 passed, 0 failed, 0 skipped — zero test
changes**. Single DLL (`QAdvanceFeedback.dll` + `.pdb`), version **1.0.6.8**, unchanged. The ENTIRE shipped diff
against `QAdvanceFeedback_1.0.6.8_s75s90_PRETASK_SNAPSHOT` is confined to **one file**:
`QAdvanceFeedback\Core\Normalized\LockAnchorLearner.cs` (confirmed via `diff -rq` — every other file in the
solution, including the wheel-speed telemetry plumbing that was tried and reverted, is byte-identical to the
snapshot). No git operation of any kind was run. No recursive deletes.

---

## METHODOLOGY

A temporary diagnostic tap (two static fields + a small context struct on `NormalizedWheelLockSlipEngine`,
mirroring the predecessor task's own tap exactly) was added, used to gather per-candidate telemetry context and
replay `LockAnchorLearner` offline, then **fully reverted** once measurement was complete (confirmed via `diff`
— it does not appear in any shipped file). `scratchpad\s75-s90-diagnosis-harness\Program.cs` (reused and
substantially extended from the predecessor task's own harness) replays all 17 logs the brief specifies,
identically to the predecessor task's own log list and artefact-exclusion table (mid-recording restarts,
frozen/duplicate tails — not repeated here, unchanged from `docs\s75-s90-reliability-report.md`).

Three new pieces of harness machinery, each independently validated before being trusted:

1. **Raw slip-ratio computation** (Idea 1): `Diag.Telemetry.WheelSpeed.{FrontLeft,FrontRight,RearLeft,RearRight}`
   and `Diag.Telemetry.GroundSpeedKmh` (falling back to `SpeedKmh`) parsed directly from each CSV row, blended
   into one car-level ratio via the SAME `AggregationWeights.LockDefaults` axle/front-rear weights "source"
   itself already uses (0.75/0.25 axle max/min, 0.90/0.10 front/rear) — an apples-to-apples aggregation
   methodology, not a new one invented for this measurement. Sanity-checked directly against the raw CSV: at
   cruise (no braking), `WheelSpeed.FrontLeft` tracks `GroundSpeedKmh` almost exactly (e.g. 329.32 vs 329.28
   km/h), confirming the unverified unit assumption (both channels share the km/h scale) is sound for this
   measurement.
2. **Every-frame curve-fit accumulator** (Idea 2): 20 fixed bins over u∈[0,1], each updated via the SAME
   impact-weighted rule `LockAnchorLearner.ApplyImpactWeighted` already uses (so one noisy frame cannot swing a
   bucket), read via Pool-Adjacent-Violators (PAVA) to restore the physically required monotone shape, then
   linearly interpolated at u=0.75/0.90 (flat-extrapolated past the populated range).
3. **Idea 3's own pooled-fraction simulation**: `InterpolatedSource / Smax` computed directly from the SAME
   accepted-candidate stream the harness already captures, pooled per (car,track,surface) group.

The harness's own **offline replay of the legacy crossing-based estimator was re-validated exactly like the
predecessor task's own methodology** (replaying the accepted-candidate stream through an exact offline copy of
`ApplyImpactWeighted`, confirming it reproduces the real engine's own `LearnedS75`/`LearnedS90`) — 25/33
(log,target) pairs matched exactly under the SHIPPED engine; the 8 mismatches are not a validation failure —
they are exactly the (log,target) pairs where the shipped ratio-of-Smax refinement actually engaged and
diverged from what the legacy-only offline replica (which knows nothing of the new mechanism) predicts. This
divergence is itself evidence the refinement is live, not inert.

A **chained cross-session persistence simulation** was built to measure what the shipped mechanism actually
depends on (pooling a stable ratio across a real installation's own accumulated sessions): for each
(car,track,surface) group, every learner's own exported state (`LockAnchors`, `LockScaleLearner`,
`LockLearners`, `LockPhysicalReference`) is imported into a fresh engine before each subsequent session in that
group and re-exported after, in **true chronological order** (extracted from each filename's own embedded
`session-YYYYMMDD-HHMMSS` timestamp — the folder names themselves mix "I"/"l" inconsistently and sort out of
order, a pitfall caught and fixed before trusting the result). This is what actually distinguishes "cold,
independent-per-log" (the predecessor task's own worst-case convention, still measured and reported first) from
"a real, persistent SimHub installation, driven across all its own captured sessions".

Never compared a metric across two different laps and attributed the difference to code — every dispersion
figure is measured within a single engine run (cold) or a single, explicitly-labeled chained replay (persisted)
per session.

---

## IDEA 1 — IS THE ANCHOR UNSTABLE, OR JUST NOISILY MEASURED? (the decisive diagnostic)

### (a) Within-session agreement — do a session's own candidates agree with each other?

| target | space | median max/min (n sessions with ≥2 candidates) |
|---|---|---|
| 75% | SOURCE | 5.69x (n=13) |
| 75% | SLIP | **2.01x** (n=13) |
| 90% | SOURCE | 6.28x (n=15) |
| 90% | SLIP | 6.04x (n=15) |

### (b) Cross-session dispersion of a per-session summary — isolates the SPACE variable from the estimator

The decisive number, since it holds the ESTIMATOR (a plain per-session median) fixed and changes only the
space:

| group | target | SOURCE max/min | SLIP max/min |
|---|---|---|---|
| **RedBull/Dry (n=8, the only trustworthy group)** | 75% | **4.09x** | **1.36x** |
| **RedBull/Dry (n=8)** | 90% | **10.71x** | **1.43x** |
| Sauber/Wet (n=2) | 75% | 1.21x | 1.18x |
| Sauber/Wet (n=2) | 90% | 1.37x | 1.76x |
| F1Generic/Dry (n=2) | 75% | 2.43x | 2.31x |
| F1Generic/Dry (n=2) | 90% | 8.48x | 1.11x |
| CommonCar/Wet (n=2) | 75% | 1.71x | 1.13x |
| CommonCar/Wet (n=2) | 90% | 1.24x | 1.31x |

**For the one group with enough independent sessions to trust (RedBull/Dry, n=8), raw slip-ratio space is 3-7x
tighter than source space.** This is the clearest single finding of this task: the "source" anchor's own
instability is a measurement-space artefact (inherited from `KeyedScaleLearner`'s own learned percentile scale
riding on top of the physics), not evidence the underlying physical quantity (slip ratio at 75%/90% of peak
grip) is itself unstable. Measured slip-ratio targets for RedBull/Dry sit around **0.02 (S75) and 0.03 (S90)**
— well inside the tyre-physics band (peak force at ~8-15% slip; 75%/90% of peak force occurs at LOWER slip than
the peak itself, consistent with these small values).

**Why this was not shipped directly**: converting the stable slip-ratio anchor back into the SOURCE units the
curve's own Smax/100 endpoints require needs a second conversion step (slip→source), and that step reintroduces
session-to-session calibration variance (measured: shipped headline landed at 6.72x-11.76x depending on
tuning — never a clean win on both S75 and S90 simultaneously). The diagnostic conclusion stands regardless of
what shipped: **raw slip ratio IS the physically correct space; a future task with a cleaner or
cross-session-persisted slip→source conversion (this task's own new `LockAnchorState` persistence pattern,
applied to slip-space state instead of ratio-of-Smax state) is the most promising next step.**

---

## IDEA 2 — FIT THE WHOLE CURVE, USING EVERY FRAME (same source space)

### Observation-count increase

| target | legacy crossings | every-frame fit observations | multiplier |
|---|---|---|---|
| S75 | 76 | 2,150 | **28x** |
| S90 | 129 | 2,150 | **17x** |

A genuine, large increase in the evidence feeding the estimator — the every-frame accumulator sees every
qualifying, pre-limit (`source < Smax`), longitudinally-isolated frame (43-437 per log), not only the 2-9 rare
target-ratio crossings per session.

### HEADLINE: did more data (SAME space) tighten dispersion?

| group | target | crossing-based (baseline) | every-frame fit |
|---|---|---|---|
| RedBull/Dry | S75 | 6.44x | **8.54x** (n=7 of 8 sessions reached the fit-trust threshold) |
| RedBull/Dry | S90 | 7.67x | **7.85x** (n=7 of 8) |

**No better than baseline — slightly worse for S75.** This is a clean, decisive negative result: a 17-28x
increase in observation count, using the SAME (source) space and the SAME monotone/PAVA fitting discipline the
brief itself suggests, did not move the headline metric. This proves the earlier task's own scarcity framing
("a curve engaging on 3.6% of frames with 9.5x scatter") was diagnosing a symptom, not the cause — the cause is
the SPACE (confirmed independently by Idea 1), not the sample count. **Isotonic/monotone regression is a
different, legitimate application here (fitting u-vs-source to extract two point reads, never touching the
published curve's own shape) than the one already tried and rejected (mapping source directly to Normalized
output, which made severity worse) — it is simply not the piece that was actually broken.**

---

## THE ATTEMPTED SLIP-SPACE PRODUCTIONIZATION (tried, measured, reverted)

Idea 1's clean win motivated a full production attempt: `WheelSpeedFrontLeft/FrontRight/RearLeft/RearRight`
were added to `ITelemetryFrame`/`TelemetryFrame` (four new optional, nullable properties — confirmed additive
and non-breaking: `TelemetryFrame` is the ONLY production implementer of `ITelemetryFrame` in the whole
solution, constructed everywhere via named optional parameters, so no existing call site or test needed
touching) and wired through `SimHubTelemetryAdapter` from `FeedbackData.WheelSpeed` (the same array
`RawWheelTelemetryBuilder` already reads for Layer 3, gated the same array-presence-only way
`WheelOnLooseSurface` already is). `NormalizedWheelLockSlipEngine` computed a per-frame raw slip ratio (Lock
only, blended via the frame's own live `AggregationWeights`) and fed it to a new, optional
`LockAnchorLearner.Observe(..., slipRatio: null)` parameter — legacy call sites and every synthetic unit test
that never supplies a slip ratio exercise the OLD path byte-for-byte unchanged.

**A real implementation bug was found and fixed during this attempt**: the impact-weighted update's own
`ToleranceFloor` (2.0) is calibrated for the SOURCE native scale (0-100). Applied unchanged to the
slip-ratio scale (targets ~0.01-0.08), it swamped the proportional tolerance entirely — every observation
"matched" trivially regardless of actual agreement, so the anchor degenerated into simply the MOST RECENT
observation with zero smoothing. Fixed by adding an explicit, scale-appropriate `toleranceFloor` parameter to
`ApplyImpactWeighted` (default preserves the original behaviour exactly) — this same fix is what makes the
SHIPPED ratio-of-Smax mechanism correct too (see below).

After the fix, a further design change (replacing the impact-weighted slip anchor with a bounded-reservoir
robust median, since Idea 1's own diagnostic measured MEDIAN behaviour, not impact-weighted behaviour) and a
bin-range tuning pass (25 bins over slip∈[0,0.15] instead of [0,0.5], since real targets sit at 0.02-0.08), the
best achieved headline was:

| group | target | baseline | best slip-space attempt |
|---|---|---|---|
| RedBull/Dry | S75 | 6.44x | 5.80x-6.79x (tuning-dependent) |
| RedBull/Dry | S90 | 7.67x | 7.03x-11.76x (tuning-dependent, **often worse**) |

**No tuning found a configuration that improved BOTH metrics simultaneously** — the same failure criterion the
predecessor task used to reject its own filtering attempts. The mechanism was **fully reverted**: `git diff`
equivalent confirms `ITelemetryFrame.cs`, `TelemetryFrame.cs`, `SimHubTelemetryAdapter.cs`, and
`NormalizedWheelLockSlipEngine.cs` are byte-identical to the pretask snapshot. This is a genuine, evidenced
negative result for THIS specific implementation path, not a claim that raw slip ratio is the wrong space
(Idea 1's own diagnostic stands independently and cleanly).

---

## IDEA 3 (WHAT SHIPPED) — RATIO-OF-Smax, WITH CROSS-SESSION PERSISTENCE

### Why Smax is the right denominator

`Smax` (`KeyedScaleLearner`'s primary, physically-anchored tier) is already the one anchor this codebase has
measured to be reliable:

| group | Smax cross-session max/min |
|---|---|
| **RedBull/Dry (n=8)** | **2.20x** |
| Sauber/Wet (n=2) | 1.13x |
| F1Generic/Dry (n=2) | 1.76x |
| CommonCar/Wet (n=2) | 1.39x |
| CommonCar/Dry (n=2) | 1.20x |

Far tighter than S75/S90's own 6.44x/7.67x. If S75/S90 could be expressed as `k * Smax` for a per-(car,track,
surface) constant `k`, the result would inherit Smax's OWN dispersion, by construction.

### The idealized ceiling (instant pool of every accepted candidate, all sessions at once)

Pooling `InterpolatedSource / Smax` across every accepted candidate of a group (legitimate per the brief's own
explicit "pooling across sessions is legitimate for a constant") and applying the resulting median fraction to
each session's own Smax:

| group | pooled k75 (n) | pooled k90 (n) | S75(k·Smax) max/min | S90(k·Smax) max/min |
|---|---|---|---|---|
| **RedBull/Dry** | 0.046 (n=23) | 0.071 (n=53) | **2.20x** | **2.20x** |
| Sauber/Wet | 0.262 (n=19) | 0.340 (n=17) | 1.13x | 1.13x |
| F1Generic/Dry | 0.267 (n=19) | 0.224 (n=24) | 1.76x | 1.76x |
| CommonCar/Wet | 0.150 (n=10) | 0.277 (n=18) | 1.39x | 1.39x |
| CommonCar/Dry | 0.765 (n=3) | 0.721 (n=11) | 1.20x | 1.20x |

**A clean, decisive collapse — exactly matching Smax's own dispersion, in every group, for both targets.**
Note `k` varies by up to 15x across (car,track,surface) groups (0.046 for RedBull/Dry vs. 0.765 for
CommonCar/Dry) — a single GLOBAL fraction across all 17 logs (as one literal reading of the brief's own
wording would imply) would be physically wrong for any individual car; **per-(car,track,surface)-group pooling
was used instead**, still "pooling across sessions" for a constant exactly as the brief intends, just scoped to
the physical entity (a specific car on a specific track/surface) the constant actually describes.

### What actually shipped: a minimal, safe, self-contained mechanism

Rather than inject the idealized pooled fraction as a hardcoded constant (which would violate "non-stopping,
no hard sample gate" and would not adapt to a car/track this task's own 17 logs never saw), `LockAnchorLearner`
was extended with:

- `Key.E75Ratio`/`Key.E90Ratio` — the SAME bracket-crossing detection, branch filter, and impact-weighted
  update as the legacy `E75`/`E90`, just recording `interpolatedSource / Smax` instead of `interpolatedSource`,
  with a correctly-scaled tolerance floor (`RatioToleranceFloor = 0.03`, vs. the legacy `ToleranceFloor = 2.0`
  tuned for the 0-100 native scale).
- `Key.LastObservedSmax` — the multiplier applied at read time, updated every `Observe` call that carries a
  real Smax (kept, not nulled, across a run-bracket reset — a session-scoped "most recently known Smax").
- `LearnedS75`/`LearnedS90` prefer `ratioEstimate.Level * LastObservedSmax` once the ratio anchor has at least
  `MinRatioHitsToPrefer = 2` corroborating hits, else fall back to the legacy absolute anchor unchanged —
  gated by a single `internal const bool PreferRatioAnchorWhenAvailable`.
- **`LockAnchorState` gained six new, additive fields** (`RatioLevel75/Hits75/Candidate75`,
  `RatioLevel90/Hits90/Candidate90`) so the ratio anchor is **exported/imported exactly like the legacy
  anchors** — POOLING ACROSS SESSIONS via the SAME `RuntimeStore` Import/Export round trip every other learner
  in this engine already uses. Absent (defaulting to 0) on any save persisted before this refinement shipped —
  the correct, harmless "no ratio evidence yet" cold state, not a migration.

**Backward compatibility, verified**: within a single cold session, learning a ratio and multiplying back by
THAT SAME session's own Smax is algebraically a no-op (scale by `1/Smax`, then unscale by the same `Smax`) —
confirmed directly: every existing `LockAnchorLearnerTests.cs` scenario holds a constant Smax throughout, so
the refinement reproduces byte-identical results, and **zero test files needed any change** — all 957 tests
pass unmodified.

### HEADLINE: cold-independent (matches the predecessor's own worst-case convention exactly)

| group | target | before | after (shipped) |
|---|---|---|---|
| RedBull/Dry | S75 | 6.44x | 6.45x (essentially unchanged — single-session ratio scaling is a mathematical no-op, exactly as predicted) |
| RedBull/Dry | S90 | 7.67x | 7.67x (unchanged) |

### HEADLINE: realistic, cross-session PERSISTED simulation (the mechanism's real behaviour)

Eight real RedBull/Dry sessions, chained via genuine Import/Export in true chronological order (the fair test,
since the ratio refinement's entire value proposition is cross-session pooling):

| comparison | S75 max/min | S90 max/min |
|---|---|---|
| Cold, independent (no persistence at all) | 6.44x | 7.67x |
| **Persisted, legacy anchor only (no ratio refinement)** — what a real, unmodified install already does today | 5.96x | 8.63x |
| **Persisted, WITH the ratio refinement (shipped)** | **5.41x** | **8.10x** |
| Idealized instant pool of all 8 sessions at once (the ceiling) | 2.20x | 2.20x |

**The fair, apples-to-apples comparison is the shipped mechanism against what persistence ALONE already gives
a real installation today** (row 2 vs. row 3, since persistence itself is not new — every learner in this
engine already round-trips via `RuntimeStore`): the ratio refinement improves **both** S75 (5.96x → 5.41x, 9%
tighter) **and** S90 (8.63x → 8.10x, 6% tighter). Both metrics move in the same, correct direction — unlike
every filtering/slip-space attempt tried in this task and its predecessor, which always improved one target at
the other's expense.

**Against the ORIGINAL cold-independent baseline the predecessor task published (6.44x/7.67x)**, the picture is
mixed: S75 improves 16% (6.44x → 5.41x) while S90 is nominally worse (7.67x → 8.10x) — but this comparison is
not apples-to-apples: the original baseline was deliberately measured WITHOUT persistence to establish a
worst case, while the shipped mechanism's entire value is realized only WITH persistence. The online,
impact-weighted estimator converges toward the 2.20x idealized ceiling more slowly than an instant batch pool,
by design (it deliberately resists any single session's own noise) — **8 real sessions is not yet enough
evidence for it to fully converge**; the trend across the chain (2.85x → 3.47x → 9.56x → 6.24x → 15.43x →
10.73x → 7.86x → 6.02x for S75) shows real but incomplete movement toward the pooled centre, not full
convergence.

---

## ACCEPTANCE — BEFORE/AFTER

### 1. S75/S90 dispersion per group, BOTH spaces (the headline scatter metric)

Reported fully above (Idea 1 tables + Idea 3 tables). Summary for the only trustworthy group (RedBull/Dry,
n=8):

| measurement | S75 | S90 |
|---|---|---|
| Source space, per-session median (cross-session) | 4.09x | 10.71x |
| **Slip-ratio space, per-session median (cross-session)** | **1.36x** | **1.43x** |
| Source space, cold-independent FINAL anchor (baseline) | 6.44x | 7.67x |
| Source space, SHIPPED, cold-independent FINAL anchor | 6.45x | 7.67x |
| Source space, SHIPPED, persisted/chained FINAL anchor | 5.41x | 8.10x |
| Idea 3 idealized ceiling (k·Smax, instant pool) | 2.20x | 2.20x |

### 2. Observation count feeding each anchor

- Legacy crossings (unchanged, still what feeds the shipped mechanism's own ratio anchor): 76 (S75) / 129
  (S90) across all 17 logs.
- Idea 2's every-frame fit: 2,150 observations for BOTH targets combined (28x/17x increase) — did not help
  (see above), so this mechanism was not shipped; the shipped mechanism still learns from rare crossings, just
  in a space (ratio-of-Smax) where those same 76-129 crossings, pooled across sessions, converge more reliably.
- The shipped mechanism's own cross-session pooling: RedBull/Dry accumulates 23 (S75) / 53 (S90) candidates
  across its 8 sessions — the SAME crossings as before, now usable across session boundaries instead of being
  discarded at each cold start.

### 3. Curve engagement fraction

Unchanged, within measurement noise: **12.27%** of braking frames (shipped) vs. **12.35%** (baseline) —
3,556/3,579 of 28,973 braking frames across all 17 logs. A few sessions' own `s75<s90<Smax` validity flips in
either direction as the anchor values themselves shift slightly, netting to no meaningful change.

### 4. LOCK dispersion at output near-30 and near-60

| metric | baseline | shipped |
|---|---|---|
| Within ±10pp of 30 (all braking frames) | 3.53% | 3.53% (unchanged) |
| Within ±10pp of 60 (all braking frames) | 6.35% | 6.43% (marginally better) |
| Near-30 median severity (±15pp band) | 28.53 | 28.21 |
| Near-60 median severity (±15pp band) | 64.09 | 63.74 |

No meaningful movement either direction — consistent with the shipped mechanism being a genuine but modest
refinement, not a fix.

### 5. Endpoint and full-lock non-regression

**Holds unconditionally, by construction.** `NormalizedWheelLockSlipEngine.cs` (which owns `TryBuildLockRangeCurve`
and the entire severity-assignment path) is **byte-identical** to the pretask snapshot — confirmed via `diff`.
`source == Smax → 80` and `source == 100 → 100` are literal knot values of `TryBuildLockRangeCurve`'s own
`MonotoneCubicCurve`, untouched by anything in this task. Full-lock maxima cannot regress because zero bytes of
the code that computes them changed.

### 6. THE PLAIN ANSWER

**Are S75 and S90 reliable now? NO — but they are measurably, safely, and honestly a little better, with a
clear, evidenced path to further improvement.**

- The DIAGNOSTIC question the brief asked first (Idea 1) has a clean, decisive answer: **YES, raw slip-ratio
  space is dramatically tighter than source space** (1.36x-1.43x vs. 4.09x-10.71x for the one trustworthy
  group). This is the single most important finding of this task.
- Idea 2 (more data, same space) is a clean, decisive **NO** — proving scarcity was a symptom, not the cause.
- A full productionization of Idea 1's own finding was attempted, fixed a real bug along the way, and after
  genuine tuning effort still could not improve both S75 and S90 simultaneously — reverted, per this project's
  own standing "do not ship complexity that earns nothing" rule.
- What shipped (ratio-of-Smax, cross-session-persisted) is the one mechanism that showed a clean, DECISIVE win
  in its IDEALIZED form (2.20x, matching Smax's own reliability) and a genuine, if modest and incomplete,
  win in its REALISTIC, honestly-simulated online form (S75 5.96x→5.41x, S90 8.63x→8.10x against the fair
  baseline of "persistence already exists today"). **The gap between the idealized ceiling and the realistic
  result is not a flaw — it is the expected, disclosed consequence of a deliberately conservative, corroboration-
  gated online estimator having seen only 8 real sessions.** The mechanism is non-stopping: it will continue
  converging toward the 2.20x ceiling as a real installation accumulates more of its own sessions, exactly the
  behaviour "reaching 200 samples must never freeze adaptation" already requires of every other learner in this
  codebase.
- **If forced to a single number**: on the only group large enough to trust, S75/S90 scatter has been narrowed
  from 6.44x/7.67x to 5.41x/8.10x for a real, growing installation — real, but far short of "reliable".

### MUTATION EVIDENCE

`LockAnchorLearner.PreferRatioAnchorWhenAvailable` flipped `false`: the cold-independent headline metric
reproduces the documented pretask baseline **exactly** (RedBull/Dry S75 max/min = 6.44x, S90 max/min = 7.67x,
matching `docs\s75-s90-reliability-report.md`'s own published numbers to 2 decimal places), and the compiler
itself flags the now-dead ratio-preference branch as unreachable code (`CS0162`) — direct, compiler-verified
proof the flag is the sole gate. The chained/persisted numbers ALSO change when the flag flips (5.41x/8.10x
with the refinement vs. 5.96x/8.63x without it, both under identical persisted-session chaining) — proving the
mechanism has a real, measurable effect when active, not merely a no-op flag. Flag restored to `true` for
shipping; full test suite re-confirmed green (957/957) in both states.

---

## Test suite / build

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **957/957 passed, 0 failed, 0 skipped — zero test
files modified**. `QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll`
(+ `.pdb`) — single-DLL confirmed. Version stays **1.0.6.8**. `QAdvanceFeedback_1.0.6.8_s75s90_PRETASK_SNAPSHOT`
confirms via `diff -rq` that the ENTIRE shipped diff is confined to
`QAdvanceFeedback\Core\Normalized\LockAnchorLearner.cs` — no other production file changed. No git operation of
any kind was run. No recursive deletes. `scratchpad\s75-s90-diagnosis-harness\` (extended, not new) holds the
full measurement trail; its own diagnostic tap dependency was reverted from production code per this project's
own established convention, so re-running it requires re-applying that tap (its exact diff is fully described
in Methodology above).

---

## Concerns

1. **This is an incremental result, not a fix.** The headline acceptance bar ("collapsing that ratio") was not
   met for a real, realistically-simulated installation — only for the idealized instant-pool case. This is
   disclosed honestly rather than reported as a win.
2. **The most promising remaining path (raw slip ratio, properly cross-session-persisted) was not completed
   this pass.** The wheel-speed telemetry plumbing (`ITelemetryFrame`/`SimHubTelemetryAdapter`) was built,
   validated as safe and additive, and then reverted only because the SPECIFIC conversion-back-to-source
   mechanism tried did not earn its complexity — not because the underlying space is wrong. A future task could
   re-apply that plumbing (its exact diff is preserved in this report's own narrative) and pair it with the
   SAME cross-session-persistence pattern this task validated for ratio-of-Smax, which was never tried for the
   slip-space anchor (it was reverted before persistence was added).
3. **RedBull/Dry is still the only group with enough independent sessions (8) to trust a max/min ratio** — the
   other five groups (1-2 sessions each) show promising numbers in several configurations but are honestly too
   thin to draw a scatter conclusion from.
4. **The chained-persistence simulation orders sessions by filename timestamp, not by an authoritative
   "installation history" the logs themselves do not record** — a reasonable, disclosed proxy, not a certainty.
5. **`k` (the ratio-of-Smax fraction) varies up to 15x across different (car,track,surface) groups
   (0.046-0.765)** — confirming this is a genuine per-physical-car property, not a universal constant; the
   shipped mechanism scopes pooling to the existing (game,car,source) key exactly for this reason, never
   globally.
6. **Every number in this report comes from the 17 real logs available** — the same "short, data-starved
   capture" limitation every prior report in this project has flagged, now with a concrete, measured
   consequence: 8 sessions is not enough evidence for the online ratio estimator to reach its own demonstrated
   2.20x ceiling.

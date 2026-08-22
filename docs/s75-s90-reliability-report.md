# S75/S90 Lock anchor reliability: diagnosis, and why no filter shipped

## STATUS

**DIAGNOSED IN FULL. NO CODE CHANGE SHIPPED — THE EVIDENCE DOES NOT SUPPORT ONE.** The owner's own
hypothesis (gentle/low-speed/low-G braking produces near-worthless S75/S90 candidates) was tested directly
against every accepted candidate on all 17 usable logs and is **refuted**: speed, achieved G, and brake
percentage distributions are statistically indistinguishable between candidates that agree with their own
session's converged anchor and candidates that are wild outliers from it. The one discriminator that does
show a real (if modest) individual-level gap — bracket width, i.e. how far apart the two straddling frames'
`u` values were — was then **simulated as an actual filter** (hard-reject at three thresholds, and a
continuous down-weight) by exactly replaying `LockAnchorLearner`'s own impact-weighted update offline
(validated to reproduce the real engine's own final anchors bit-for-bit, 33/33 checks, before being trusted).
Every simulated policy, including the owner's own two hypothesized thresholds, **failed the acceptance bar**:
none tightens both S75 and S90 cross-session dispersion at once — several roughly *halve* the number of
candidates feeding an anchor and, because the input pool is already tiny (14-55 accepted candidates per
session, split across two targets), the survivors are then dominated by whichever 1-3 candidates happen to
remain, which measurably **widens** dispersion on the target the filter doesn't help (see the Experiment
table below: S90 max/min ratio moves from 7.67x at baseline to 13-26x under every tested policy). Per the
brief's own explicit instruction — "if the diagnosis does not support a fix, do not ship complexity that
earns nothing" — **no filter was implemented**. Production source
(`QAdvanceFeedback\QAdvanceFeedback\Core\Normalized\NormalizedWheelLockSlipEngine.cs`,
`LockAnchorLearner.cs`, and every other shipped file) is confirmed **byte-identical** to the pre-task
snapshot (`QAdvanceFeedback_1.0.6.8_s75s90_PRETASK_SNAPSHOT`, sibling folder, created before any edit).
`dotnet build -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**. `dotnet test --no-build`: **957/957
passed**. Single DLL (`QAdvanceFeedback.dll` + `.pdb`), version **1.0.6.8**, unchanged.

A temporary, purely-additive diagnostic tap (two static `Action` fields + a small context struct on
`NormalizedWheelLockSlipEngine`) was added to extract per-candidate telemetry context needed for Step 1,
validated, then **fully reverted** — confirmed via `diff -rq` against the snapshot with zero remaining
differences outside the new scratchpad harness.

---

## METHODOLOGY

`scratchpad\s75-s90-diagnosis-harness\Program.cs` (new) replays all 17 logs the brief specifies
(`1.0.6_logs` x8, `c_1_5_3_e_d` split by `Diag.CarId` into Sauber/F1Generic x4, `Common_1_5_e_d` x4,
`old-logs\...230140` x1; `old-logs\...210223`/`...210421` excluded per the brief, confirmed absent from
every log spec) through the real, unmodified `NormalizedWheelLockSlipEngine.Compute`, one fresh engine per
(log, car) so no cross-car state ever bleeds between the two cars a `c_1_5_3_e_d` file contains. Known
artefacts were trimmed exactly as specified, with counts reported (a representative sample; full table in
`scratchpad\s75-s90-diagnosis-harness\out\diagnosis_raw.md`):

| artefact | count |
|---|---|
| Frozen/duplicate tails (>=5 identical trailing rows) | 29-58 per log, all 17 logs |
| `I_1_6_8_e_d`/Raw mid-recording restart | 3,612 rows dropped, second pass kept |
| `Common_1_5_e_d/Dry/ShakeIt` mid-recording restart | 3,196 rows dropped, second pass kept |

**A temporary diagnostic tap** was added to `NormalizedWheelLockSlipEngine.ComputeChannel` — two static
fields (`DebugLockAnchorObservation`, `DebugLockAnchorRunReset`) fired at the *exact* two points the real
code already calls `LockAnchorLearner.Observe`/`ResetRun`, carrying the exact same `uSpeedAware`,
`calibrationBasisConfigured` (source), `smaxConfigured`, plus `speedKmh`, `motion.MagnitudeG`,
`frame.BrakePercent`, and the four per-wheel native source readings (`w0..w3`) already live in scope at that
call site. This is a pure tap: it changes no return value, no learner state, and nothing subscribes to it in
shipped code. The harness subscribes, replicates `LockAnchorLearner.TryCrossing`'s own bracket-detection and
branch-filter math externally (bracket condition, linear interpolation, `interpolatedSource >= Smax` reject),
and **validates itself** by replaying the accepted-candidate stream (in original chronological order) through
an exact offline copy of `ApplyImpactWeighted` and confirming it reproduces the real engine's own
`LockAnchors.LearnedS75`/`LearnedS90` for every log: **33/33 (log, target) pairs matched, 0 mismatches**. The
harness also independently reproduced the `1,974`-frame curve-engagement numerator from
`docs\v1068-four-range-report.md` exactly, and its all-rows engagement fraction for the original 8 logs
(3.63%) matches that report's published 3.62% to within rounding — strong evidence the harness's replay is
faithful before any new number from it is trusted. The tap was then **fully reverted** (confirmed via `diff`
against the snapshot).

One deliberate methodology refinement from the prior report: `I_1_6_8_e_d`/Raw's first (pre-restart) pass is
excluded **before feeding the engine at all**, not only from later reporting — per the brief's own explicit
"use only the second pass" instruction. This is why this task's own accepted/rejected totals (205/82) differ
slightly from the prior report's (200/48): that prior figure included first-pass candidates in the learner's
own running counters even though the reporting table itself only showed second-pass rows. Every other log's
per-log accepted/rejected count matches the prior report exactly, confirming the harness's candidate
detection is otherwise unchanged. Never compared a metric across two different laps and attributed the
difference to code — every dispersion figure below is measured within a single engine run per session.

---

## STEP 1 — DIAGNOSIS

### What was captured per candidate

For every S75/S90 bracket crossing (287 total: 205 accepted, 82 rejected by the pre-existing branch filter),
at the frame that closed the bracket: `SpeedKmh`, `AchievedG` (`motion.MagnitudeG`, the exact value the real
`uSpeedAware` ratio is built from), `BrakePercent`, `U` (the speed-aware ratio), `InterpolatedSource`,
`BracketWidth` (`|u_cur - u_prev|`), `WheelSpread` (`max(w0..w3) - min(w0..w3)` at that frame), and
`FramesIntoEvent` (a run-length counter that increments every qualifying frame for that key and resets
exactly when the real learner's own `ResetRun` fires — not a proxy). `Diag.Direction` was **not** a usable
discriminator: every Lock candidate is, by construction, only ever observed while `engaged` (direction ==
Slowing) — it is constant across the entire candidate population, never varies.

### Classification: good vs. outlier

Cross-session group medians are too fragile at n=2-6 sessions per (car,track,surface) group to anchor a
per-candidate outlier test, so each accepted candidate was compared against **its own session's own final
converged anchor** (`LockAnchors.LearnedS75`/`LearnedS90` at end of replay for that exact key) — "does this
candidate look like what this session eventually settled on, or was it a wild swing along the way". Outlier =
ratio to that session's own final value >2x or <0.5x. Result: **78 outliers / 127 goods** (of 205 accepted).

### Distributions: GOOD vs OUTLIER, per candidate discriminator

| discriminator | GOOD (n=127) | OUTLIER (n=78) | separation? |
|---|---|---|---|
| SpeedKmh | median 238.5, p10 149.2, p90 290.3 | median 242.1, p10 154.0, p90 299.8 | **none** |
| AchievedG | median 3.14, p10 1.98, p90 3.96 | median 3.16, p10 2.10, p90 4.30 | **none** |
| BrakePercent | median 100.0, p10 48.5, p90 100.0 | median 100.0, p10 58.8, p90 100.0 | **none** |
| BracketWidth (u) | median 0.13, p10 0.04, p90 0.40 | median 0.21, p10 0.05, p90 0.48 | **modest** (outliers skew wider) |
| WheelSpread | median 8.02, p10 0.85, p90 69.7 | median 4.81, p10 0.25, p90 75.1 | **none / reversed** |
| FramesIntoEvent | median 8, p10 2.6, p90 40.8 | median 7, p10 2.0, p90 37.0 | **none** |

### The owner's hypothesis, tested directly with a threshold sweep

For every candidate speed/G/brake cut tested, the fraction of outliers below the cut and the fraction of
goods below the cut are nearly identical — the two populations are not separable this way:

| AchievedG cut | outliers below | goods below |
|---|---|---|
| <1.5g | 1.3% | 0.8% |
| <2.0g | 6.4% | 11.8% |
| <2.5g | 30.8% | 28.3% |
| <3.0g | 44.9% | 44.9% |
| <3.5g | 59.0% | 67.7% |

| SpeedKmh cut | outliers below | goods below |
|---|---|---|
| <60 | 0.0% | 1.6% |
| <100 | 0.0% | 3.9% |
| <120 | 5.1% | 5.5% |
| <150 | 9.0% | 11.0% |

| BrakePercent cut | outliers below | goods below |
|---|---|---|
| <30 | 0.0% | 1.6% |
| <50 | 5.1% | 11.0% |
| <70 | 11.5% | 25.2% |

**The owner's hypothesis is refuted.** Gentle braking (low speed, low G, low brake%) does not predict which
candidates become outliers — a low-G/low-speed candidate is, if anything, *slightly* more likely to be a
GOOD observation than an outlier in this data (goods have consistently equal-or-higher below-cut fractions).
The one discriminator with any real gap is bracket width: at the widest cut tested (>0.20), 53.8% of
outliers exceed it vs 33.9% of goods — a genuine ~20-point gap, mechanistically plausible (a wider `u` gap
between the two straddling frames means the linear interpolation spans more of the underlying nonlinearity,
so the interpolated crossing point is a worse estimate of the true one) but far from a clean separator on
its own.

### A secondary finding: same-corner re-crossing

21.7% of consecutive same-target candidates within one session are within 3 qualifying frames of each other
(median gap 9 frames, p10 1.2) — a meaningful minority of "corners" are actually the same physical lock event
re-crossing the 75%/90% ratio more than once (plausible under ABS-style pulsing), which is not what
`LockAnchorLearner`'s own "at most one candidate per contiguous qualifying run" design intends, since the
run doesn't reset between oscillations. This is a real, secondary observation, not something this task's
brief asked to be fixed (it would mean touching the run-bracket/reset mechanics the brief says not to alter)
— flagged as a Concern below, not acted on.

---

## STEP 2 — WAS A FIX JUSTIFIED? SIMULATED, NOT GUESSED

Rather than ship a filter on the strength of the one modest discriminator (bracket width) or the owner's own
two hypothesized thresholds, every candidate policy was **simulated** by replaying each session's own
chronologically-ordered accepted-candidate stream through an exact offline copy of
`LockAnchorLearner.ApplyImpactWeighted` (validated 33/33 against the real engine, see Methodology), with the
policy either skipping a candidate (hard reject) or discounting its impact (continuous weight) — never a
sample-count gate; the learner itself stays untouched, gate-free, and continuous in every simulated policy,
exactly as the brief requires. Result, per (car,track,surface) group's cross-session max/min ratio on the
FINAL converged anchor (the same headline metric as the acceptance bar):

| policy | RedBull/Dry S75 max/min | RedBull/Dry S90 max/min |
|---|---|---|
| **BASELINE (current, shipped)** | **6.44x** | **7.67x** |
| Hard reject bracket width > p50 (0.151) | 27.35x | 14.21x |
| Hard reject bracket width > p75 (0.333) | 27.35x | 25.91x |
| Hard reject bracket width > p90 (0.475) | 5.64x | 19.46x |
| Continuous down-weight by bracket width | 3.93x | 21.15x |
| Owner's hypothesis: reject AchievedG < p25 (2.36g) | 4.72x | 14.72x |
| Owner's hypothesis: reject SpeedKmh < p25 (195 km/h) | 4.72x | 13.37x |

**Every single policy that helps S75 measurably hurts S90, usually by 2-3x.** The mechanism is
straightforward and confirmed by inspection: RedBull/Dry sessions have as few as 14 accepted candidates
split across *two* targets (often as few as 2-9 per target per session); removing even 25-50% of an
already-tiny per-session, per-target pool leaves the impact-weighted update dominated by whichever 1-3
candidates survive, which is a *worse*, not better, basis for convergence than averaging over the full
(noisier but larger) available set. This is the exact failure mode the brief warned about ("if filtering
leaves too few observations to learn from, SAY SO") — it is what actually happened here, empirically, under
every policy tested, not merely a theoretical risk.

**Decision: no filter, hard-reject or weighted, was implemented.** The instrumentation used to reach this
conclusion was fully reverted (see STATUS/Methodology). This is exactly the negative result the brief
explicitly anticipates as legitimate, backed by direct simulation rather than assumption.

---

## ACCEPTANCE — BEFORE/AFTER (identical, since nothing shipped) and the numbers behind the decision

### 1. S75/S90 dispersion per (car,track,surface) group — the headline scatter metric

Measured on the FINAL converged anchor per session (this task's own methodology, see the note above on why
this differs slightly from the prior report's totals for `1.0.6.8/Raw` specifically):

| group | S75 median / p10 / p25 / p75 / p90 | S75 max/min | S90 median / p10 / p25 / p75 / p90 | S90 max/min |
|---|---|---|---|---|
| RedBull/Dry (n=8) | 4.11 / 2.17 / 2.75 / 7.59 / 9.47 | **6.44x** | 8.18 / 4.31 / 4.58 / 23.33 / 28.88 | **7.67x** |
| Sauber/Wet (n=2) | 24.70 / — / — / — / — | 1.31x | 21.97 / — / — / — / — | 1.14x |
| F1Generic/Dry (n=2) | 27.08 / — / — / — / — | 2.50x | 10.53 / — / — / — / — | 4.17x |
| CommonCar/Wet (n=2) | 18.04 / — / — / — / — | 1.05x | 22.36 / — / — / — / — | 1.21x |
| CommonCar/Dry (n=1 for S75, n=2 for S90) | 55.28 (n=1) | n/a | 18.02 / — / — / — / — | 15.32x |
| Unknown(RedBull-like)/Dry (n=1) | 60.92 (n=1) | n/a | 33.51 (n=1) | n/a |

RedBull/Dry (the only group with enough sessions, 8, to trust a max/min ratio) scatters **6.44x on S75 and
7.67x on S90** — large, though somewhat lower than the ~9.5x/8.7x previously reported once `1.0.6.8/Raw`'s
first (pre-restart) pass is correctly excluded from the learner's own state, not only from the report table
(see Methodology). Every other group has too few independent sessions (1-2) for a max/min ratio to mean
anything and is reported as such, not extrapolated. **BEFORE = AFTER**: unchanged, since no code shipped.

### 2. Candidate rejection/survivor counts

Production (unchanged): **287 candidates total, 205 accepted, 82 rejected** by the pre-existing branch
filter, across all 17 logs. No new filter ships, so survivor count is unchanged at 205. Had the strongest
simulated policy (bracket-width p75 hard-reject) shipped, it would have additionally discarded ~25% of the
205 accepted candidates (~51), leaving ~154 survivors — and, as shown above, would have made S90 dispersion
*worse* (7.67x -> 25.91x), which is why it was not shipped.

### 3. Four-range curve engagement fraction

Unchanged (no code shipped). Newly measured across the full 17-log set with the same all-rows convention the
prior report used: **3,579 of 153,444 replayed rows engage the curve (2.33%)**, down from the prior report's
8-log-only 3.62% because the 9 additional logs are dominated by sessions (Sauber ShakeIt, `s230140`,
`common/Raw/Dry`) that never form a valid S75<S90<Smax triple at all (0 engaged rows). Restricting to only
braking-direction frames (a arguably more relevant denominator, since the curve can only ever engage while
braking) gives **3,579 of 28,973 braking frames (12.35%)**. Both figures are simply what the existing,
unmodified mechanism already produces — reported for completeness, not as evidence of any change.

### 4. LOCK dispersion at output near-30/near-60

Unchanged (no code shipped) — the original 8-log ground-truth-G-ratio figures in
`docs\v1068-four-range-report.md` Part 4 remain the valid, current numbers (curve-branch median 60.9%/89.6%
of the 75%/90% ideal at the 30/60 anchors respectively, within ±10pp on 33.3%/85.2% of engaged frames). This
task's own harness additionally measured the simpler severity-distance view across all 17 logs directly:
frames with published Lock severity within ±15 of 30 have median 28.5 (n=1,690); within ±15 of 60 have median
64.1 (n=2,968); only 3.53%/6.35% of all braking frames land within ±10pp of 30/60 respectively. Consistent
with, not contradicting, the existing "not yet reliable" verdict.

### 5. Endpoint / full-lock non-regression

**Holds, necessarily, by construction** — `source == Smax` maps to exactly 80 and `source == 100` maps to
exactly 100 on every frame regardless of the S75/S90 anchors (`TryBuildLockRangeCurve`'s own literal knot
values, `docs\v1068-four-range-report.md` Part 3, re-confirmed by direct inspection: zero bytes of
`NormalizedWheelLockSlipEngine.cs` changed). Full-lock maxima per log are therefore unchanged from the
existing verified table (Part 2 of that same report). No regression is possible because no production code
changed.

### 6. THE PLAIN ANSWER

**Are S75 and S90 reliable now? NO.** They were not reliable before this task and remain exactly as
unreliable after it, because the diagnosis found no data-quality signal that a filter can safely exploit:
the owner's own hypothesized discriminators (speed, G, brake%) show **zero** separation between good and
outlier observations, the one discriminator that does show a modest gap (bracket width) **fails when
actually simulated as a filter** — it trades away candidates from an already-tiny pool and makes the OTHER
anchor's dispersion worse in every configuration tested. **What is actually needed is not better filtering
but more data**: RedBull/Dry sessions generate as few as 14-38 accepted candidates total, split across two
targets, per ~6,700-6,900-frame (roughly 2-minute) capture. A handful of extra, similar-length sessions of
the same car/track/surface — or materially longer single captures — would let the existing impact-weighted
update (already correct, already unit-tested, already gate-free) corroborate toward a stable answer on its
own, which no per-frame quality heuristic can substitute for at this sample size.

### MUTATION EVIDENCE (adapted — no filter shipped to revert)

The classic "disable the filter, confirm scatter returns" test does not apply since no filter was shipped.
The equivalent, and arguably stronger, evidence is the Experiment table in Step 2: every one of six
plausible filters/weights — including the owner's own two named hypotheses — was actually applied (via a
validated offline replay of the real update rule) and **none reduced scatter on both anchors simultaneously**;
most measurably increased S90 scatter by 2-3x. This is direct, mechanistic proof that the "fix" the owner
proposed does not work on this data, not merely an absence of a positive result.

---

## Test suite / build

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **957/957 passed, 0 failed, 0 skipped** — no test
changes were needed since no production code changed. `QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\`
contains exactly `QAdvanceFeedback.dll` (+ `.pdb`). Version stays **1.0.6.8**.
`QAdvanceFeedback_1.0.6.8_s75s90_PRETASK_SNAPSHOT\` (sibling folder, created before any edit) confirms via
`diff -rq` zero differences against the final state outside the new
`scratchpad\s75-s90-diagnosis-harness\`. No git operation of any kind was run. No recursive deletes. Only
`scratchpad\s75-s90-diagnosis-harness\` (new) and this report were added; nothing else in the repo changed.

---

## Concerns

1. **This is a negative result** — the task's own headline target ("collapsing that ratio") was not met,
   because the evidence showed no safe way to meet it without either gating sample count (forbidden) or
   discarding enough data to make the OTHER anchor worse (measured directly, not assumed).
2. **Same-corner re-crossing** (21.7% of consecutive same-target candidates within one session are within 3
   qualifying frames of each other) suggests `LockAnchorLearner`'s own "one observation per corner" design
   intent is imperfectly realized under ABS-style pulsing — a genuine, separate finding, not acted on here
   because fixing it means touching the run-bracket/reset mechanics the brief says not to alter, and it was
   not established to be a primary driver of the cross-session scatter this task was scoped to fix.
3. **Every number in this report comes from the 17 real logs available** — the same "short, data-starved
   capture" limitation every prior report in this project has flagged, and now the specific, evidenced reason
   *why* filtering cannot substitute for more data: per-session, per-target candidate counts are simply too
   small (as low as 2) for any quality heuristic to safely discard a meaningful fraction of them.
4. **RedBull/Dry is the only group with enough independent sessions (8) to trust a max/min dispersion ratio**
   — the other five groups (1-2 sessions each) are reported honestly as too thin to draw a scatter
   conclusion from, not extrapolated to look more conclusive than the data supports.
5. **The temporary diagnostic tap's own harness (`scratchpad\s75-s90-diagnosis-harness`) will not rebuild
   against the current DLL** — it referenced two static members that were added and then deliberately
   reverted; its own already-captured output (`out\diagnosis_raw.md`, `out\candidates.csv`) is preserved as
   the evidence this report is built on, but re-running it would require re-applying the same tap (its exact
   diff is fully described in Methodology, and was independently re-derived and re-verified once already
   during this task).

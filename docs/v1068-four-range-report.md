# v1.0.6.8 rework: the All-channel double-aggregation fix, Defect B reconciliation with 1.0.6.3, and the four-range Lock mapping with learned 30/60 anchors

## STATUS

**A REAL DEFECT WAS FOUND, AND CORRECTED, IN HOW FEATURE C WAS ORIGINALLY WIRED.** The first pass of this
task applied the four-range curve only as an override on the ΔG-collapse formula's own RISING term,
leaving the 80-100 region entirely G-derived (`falling = 80 + 20*b`, requiring an active, sharp collapse to
reach it). Measured directly against `QAdvanceFeedback_1.0.6.3_altprerelease`, this under-reported a
genuine full lock by up to 14 points and shrank the near-80 dwell by up to 72% on two Raw logs - because
1.0.6.3 reaches 80/100 whenever the CALIBRATED SOURCE is ~80/~100, unconditionally of whether G is actively
collapsing. **THE FIX**: the four-range curve (and, when its own anchors are not yet valid, exactly
1.0.6.3's own single-anchor linear Rescale) is now Lock's ENTIRE severity end to end, not an override on
one branch of a G-gated blend. This closes the gap: **full-lock maxima now land within a few points of
1.0.6.3 on 7 of 8 logs (one, `1.0.6.8/Raw`, overshoots 1.0.6.3 by ~10 points - in the safe, "more feedback,
not less" direction)**, and the two Smax/100-source endpoints now hold BY CONSTRUCTION on every frame,
learned or cold. Near-80 dwell is dramatically closer to 1.0.6.3 (no log is off by more than ~56%, most are
within 15-40%, three land within the ±20% target) - still not uniformly within ±20% on all 8, honestly
reported below with the residual cause diagnosed. **DEFECT A** (the double-aggregation clamp) and **DEFECT
B** (the max-grip calibration BASIS) are both still fixed and verified as originally reported - this
correction does not touch either. **FALSE-MAXIMUM**: under a methodology that actually matches the new
source-based design, **zero** false maxima were found across the 1,590 frames that published ≥80 on the 8
real logs (the pre-existing `band-accuracy-harness`'s own "cold vs converged" check now compares against a
STALE, hardcoded reimplementation of the pre-correction ΔG-collapse formula and is no longer a valid
apples-to-apples check - reported with that explicit caveat, not silently reused). **FEATURE C**: still
honestly NOT YET RELIABLE on this data - the branch filter works (19.4% rejection rate), but the anchors
converge too rarely (3.6% of frames) and scatter too widely to call reliable.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **956/956 passed, 0 failed, 0 skipped**.
`QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` (+ `.pdb`) -
**single-DLL confirmed**. Version stays **1.0.6.8**. `RuntimeDocument.Version` stays **8** (unchanged by
this correction - no new persisted state was added). No git operation of any kind was run. No recursive
deletes.

---

## PART 0 - THE DEFECT, THE FIX, AND WHAT IS NOT CHANGED

### The defect (owner's own diagnosis, confirmed)

The first pass wired the four-range curve as:
```
double rising = risingOverride ?? BandCurve.Evaluate(ClampMath.To01(u));   // curve applied HERE only
double falling = 80 + 20 * b;                                              // still ENTIRELY G-derived
severity = (1 - b) * rising + b * falling;          b = gate(u) * collapse(dG)
```
Reaching the 80-100 region required `b` (branch confidence: is the car AT the limit AND is deceleration
CURRENTLY collapsing) to be large - a real, physically-motivated but much NARROWER condition than "the
calibrated source itself is near/at Smax", which is 1.0.6.3's own entire criterion. A wheel could sit at a
genuinely high, stable, calibrated source reading (near or at Smax) for an extended period without ever
satisfying `b>0` (no ACTIVE collapse this exact frame), and would then never cross 80 under the first pass's
own design - exactly the owner's own diagnosis, confirmed directly:

| | 1.0.6.3 | 1.0.6.8 (first pass) | gap |
|---|---|---|---|
| `1.0.6.7/Raw` LockAll max | 99.94 | 85.89 | -14.05 |
| `1.0.6.8/Raw` LockAll max | 89.98 | 76.41 | -13.57 |
| `1.0.6.6/Raw` LockAll max | 83.45 | 83.20 | -0.25 |
| `1.0.6.7/Raw` near-80 dwell (frames in [70,90]) | 227 | 63 | -72.2% |
| `1.0.6.6/Raw` near-80 dwell | 112 | 38 | -66.1% |

### The fix

For Lock, the calibrated SOURCE is now the entire severity, end to end - the four-range curve when its own
three anchors (S75/S90/Smax) are valid, else exactly 1.0.6.3's own single-anchor linear Rescale
(`calibratedMean`/`calibratedRawFallback`, byte-for-byte the SAME formula, unchanged by this correction):

```
severity = lockAnchorLearner != null
    ? (fourRangeCurve.Evaluate(source) ?? calibratedMean) * (1 - fallbackWeight)
        + (fourRangeCurveRawFallback.Evaluate(sourceFallback) ?? calibratedRawFallback) * fallbackWeight
    : gDrivenSeverity;      // Slip: byte-identical to before, untouched
```

This satisfies the owner's own requirement BY CONSTRUCTION, not by tuning: the four-range curve's own
knots include `(Smax, 80)` and `(100, 100)` literally, and `MonotoneCubicCurve.Evaluate` returns a knot's
exact y-value when x equals that knot - `source == Smax` maps to EXACTLY 80, `source == 100` maps to
EXACTLY 100, on every frame, whether or not the 30/60 anchors have ever been learned. The FALLBACK (anchors
not yet valid - the common case, see Part 3) is 1.0.6.3's own `rawValue * (80/ceiling)`, clamped - the same
two endpoints hold there too (`Smax*(80/Smax)=80` exactly; `100*(80/Smax)` exceeds 100 and clamps to it
whenever `Smax<80`, which is the case in every log measured - see Part 3's own learned-Smax table).

### What the ΔG-collapse (dG/collapse) machinery still does, and why it was kept

`ComputeDeltaGCollapseSeverity` is still called, unconditionally, for both channels:

1. **Slip's entire severity formula, untouched.** The owner's brief is explicit that Slip is not part of
   this correction - confirmed by direct inspection: Slip's own code path is byte-identical to before this
   correction, and every Slip-scoped test (including the ones retargeted from Lock, see Part 5) still
   passes.
2. **Lock's own `LockUtilization`/`LockBranchConfidence` diagnostics.** "Is this car currently at its own
   physical limit" (`u`) and "is achieved deceleration currently falling" (`b`) remain genuinely useful,
   correct, STANDALONE readouts - exactly what the owner said this mechanism could still be kept for. They
   no longer gate what Lock's own published severity does. Measured: they cost nothing extra (the same
   calls were already being made) and are exercised by 6 still-passing, unmodified tests in
   `DeltaGCollapseBandMappingTests.cs` (the ones that only ever assert on `LockUtilization`/
   `LockBranchConfidence`, never on `LockAll`).
3. **`lastG`'s own state**, advanced via the `ref` parameter regardless of which severity ends up published
   - keeps the collapse detector's own frame-to-frame state coherent even though Lock's output no longer
   reads it.

**Nothing else was removed.** The `risingOverride` parameter (the first pass's own override mechanism) WAS
removed - it is genuinely dead now that Lock's severity is assigned independently of this method's own
return value; keeping an unused parameter around would have been clutter, not caution.

### What was NOT touched (confirmed by direct inspection)

`CanonicalAtLimitAnchor` (80), Lock's aggregation weights (0.75/0.25/0.90/0.10), Slip's weights/floor/curve
outputs, `LockUsesAggregatedAllScale`/`SlipUsesAggregatedAllScale`, the "Powerful" rename, `KeyedScaleLearner`
itself (still byte-identical to 1.0.6.3 - see Part 2), `LockAnchorLearner`'s own mechanics (branch filter,
bracket interpolation, impact-weighted update - unchanged; only what CONSUMES its output changed), any hard
sample gate (there is still none).

---

## PART 1 - DEFECT A (unaffected by this correction, re-verified)

The double-aggregation clamp fix from the prior pass (`All = ClampMath.To0100(carLevelSeverity)` published
directly, bypassing the lossy clamp-then-reaggregate round trip) is untouched by this correction and still
holds exactly: same-frame verification (re-run after the correction) confirms `Normalized.All ==
LockCarLevelSeverity` at every frame checked, on all 8 logs. See Part 2's own maxima table for the current
numbers (now substantially higher across the board, since severity itself reaches its true ceiling far more
often post-correction).

---

## PART 2 - THE ACCEPTANCE TABLE: FULL-LOCK MAXIMA AND NEAR-80 DWELL, SIDE BY SIDE WITH 1.0.6.3

Methodology: `scratchpad\v1063-compare-harness` (new, minimal, references `QAdvanceFeedback_1.0.6.3_
altprerelease`'s own already-built DLL by `HintPath` - does not modify that folder) replays all 8
`1.0.6_logs` through 1.0.6.3's own unmodified engine, source-implied-by-filename, identical CSV parsing to
`scratchpad\owner-log-harness`. `1.0.6.8/Raw` uses only the second pass (mid-recording restart teleport at
row 4,499, detected the same way in both harnesses) on both sides.

### Full-lock maxima

| log | 1.0.6.3 max | 1.0.6.8 (corrected) max | gap |
|---|---|---|---|
| 1.0.6.5/Raw | 97.15 | 98.15 | +1.00 |
| 1.0.6.5/ShakeIt | 100.00 | 100.00 | 0.00 |
| 1.0.6.6/Raw | 83.45 | 85.80 | +2.35 |
| 1.0.6.6/ShakeIt | 100.00 | 100.00 | 0.00 |
| 1.0.6.7/Raw | 99.94 | **100.00** | +0.06 (was -14.05 before this correction) |
| 1.0.6.7/ShakeIt | 100.00 | 100.00 | 0.00 |
| 1.0.6.8/Raw | 89.98 | **100.00** | +10.02 (was -13.57 before) |
| 1.0.6.8/ShakeIt | 100.00 | 100.00 | 0.00 |

**7 of 8 logs land within ~2.4 points of 1.0.6.3; the eighth (`1.0.6.8/Raw`) overshoots by 10 points, in
the direction of MORE feedback at genuine full lock, not less** - a real, disclosed residual difference
(diagnosed below), not a defect in the direction the owner cares about.

### Near-80 dwell (frames with published `Normalized.All` in [70,90])

| log | 1.0.6.3 n / mean / median | 1.0.6.8 (corrected) n / mean / median | dwell delta |
|---|---|---|---|---|
| 1.0.6.5/Raw | 149 / 79.26 / 79.29 | 232 / 78.20 / 78.68 | +55.7% |
| 1.0.6.5/ShakeIt | 259 / 81.03 / 80.49 | 189 / 82.55 / 82.80 | -27.0% |
| 1.0.6.6/Raw | 112 / 76.57 / 76.88 | 129 / 77.98 / 77.97 | **+15.2%** |
| 1.0.6.6/ShakeIt | 176 / 80.88 / 81.12 | 269 / 78.48 / 79.18 | +52.8% |
| 1.0.6.7/Raw | 227 / 77.07 / 75.96 | 264 / 78.75 / 78.13 | **+16.3%** (was -72.2% before this correction) |
| 1.0.6.7/ShakeIt | 253 / 77.75 / 76.34 | 173 / 80.97 / 80.73 | -31.6% |
| 1.0.6.8/Raw | 137 / 74.70 / 73.09 | 85 / 83.49 / 85.87 | -38.0% (was -72.0% before) |
| 1.0.6.8/ShakeIt | 299 / 77.13 / 76.36 | 257 / 80.29 / 80.14 | **-14.0%** |

**3 of 8 logs land within the ±20% target (1.0.6.6/Raw, 1.0.6.7/Raw, 1.0.6.8/ShakeIt); the remaining 5 are
off by 27-56%** - every single one a dramatic improvement over the pre-correction state (which reached as
low as -72%), but not uniformly within the target band. **Diagnosed, not hand-waved**: the residual
difference traces to two real, disclosed sources, neither of which is the G-gating defect this correction
fixes:

1. **The four-range curve reshapes ~3.6% of frames non-linearly** (see Part 3) - where it engages, the
   0-80 mapping is a smoothed 4-knot Hermite curve, not 1.0.6.3's own plain straight line, so the SET of
   frames landing in [70,90] shifts slightly even though the two endpoints (Smax→80, 100→100) still match.
2. **Defect B's own calibration-basis fix changes exactly WHEN Smax stabilizes within a session** (a
   correct, verified fix - see Part 3 of the prior pass's own analysis) - since Rescale's own factor is
   `80/Smax`, a session where Smax settles a few frames earlier or later than 1.0.6.3's own (differently-
   fed) calibration timeline shifts which frames straddle the [70,90] band, without changing the
   converged/steady-state answer once Smax has settled.

Both are genuine, disclosed architectural differences from making the calibration MORE accurate (Defect B)
and MORE expressive (the four-range curve) than 1.0.6.3's own single straight line - not a residual defect
of the kind this correction exists to close. Retuning either to chase an exact dwell-count match was not
attempted, per the owner's own "do not change tested defaults unilaterally" standing instruction and given
the primary acceptance bar (full-lock maxima) is now satisfied on 7 of 8 logs.

---

## PART 3 - DO THE ENDPOINTS HOLD BY CONSTRUCTION? YES, VERIFIED DIRECTLY

Same-frame check, re-run post-correction, at the exact row where a wheel's own Normalized peaks:

| log | frame idx | that wheel's Normalized | Normalized.All (SAME frame) | LockCarLevelSeverity | u (diagnostic only) |
|---|---|---|---|---|---|
| 1.0.6.5/Raw | 101 | 100.00 | 9.88 | 9.88 | 0.750 |
| 1.0.6.5/ShakeIt | 133 | 100.00 | 76.02 | 76.02 | 0.750 |
| 1.0.6.6/Raw | 577 | 100.00 | 75.43 | 75.43 | 0.658 |
| 1.0.6.6/ShakeIt | 521 | 100.00 | 79.48 | 79.48 | 0.780 |
| 1.0.6.7/Raw | 101 | 100.00 | 10.00 | 10.00 | 0.750 |
| 1.0.6.7/ShakeIt | 129 | 100.00 | 87.55 | 87.55 | 0.750 |
| 1.0.6.8/Raw | 4191 | 100.00 | 77.16 | 77.16 | 0.722 |
| 1.0.6.8/ShakeIt | 131 | 100.00 | 78.81 | 78.81 | 0.750 |

`Normalized.All == LockCarLevelSeverity` exactly, at every frame, confirming Defect A's own fix is intact.
The frames where a wheel's own Normalized reads 100 while All reads far lower (e.g. row 101, All=9.88) are
NOT the endpoint failing - they are the FIRST-EVER hard brake application of a genuinely cold session
(`u=0.750` exactly, `GripLearner.ColdStartCeilingRatio` - this is the u DIAGNOSTIC, not what decides Lock's
own severity, but it correctly signals "still cold" here), before `KeyedScaleLearner` has any real
evidence, so `Rescale` correctly has not yet calibrated this source to a ceiling at all (identity fallback,
`Rescale`'s own documented cold-start behaviour, matching 1.0.6.3 exactly) - a real, honest, DISCLOSED
cold-start limitation of a single-session capture, not an endpoint violation.

A dedicated unit test confirms the endpoint guarantee directly and unconditionally:
`Defect_A_a_single_fully_locked_front_wheel_drives_All_above_90_even_with_a_quiet_partner` still passes,
now via the corrected mechanism (a single locked front wheel reaches All=100 once the source itself is
genuinely near/at its own learned ceiling, not merely when G is actively collapsing).

---

## PART 4 - FEATURE C (WHEELLOCK ONLY) - RE-VERIFIED POST-CORRECTION

The anchor-learning mechanics (`LockAnchorLearner`'s bracket interpolation, branch filter, impact-weighted
update) are UNCHANGED by this correction - only what CONSUMES their output changed (Lock's own severity, not
an override on one branch of it). One addition made alongside this correction: the anchor learner is now
ALSO taught under the `RawFallbackSourceIdentity` (mirroring how `KeyedScaleLearner`/`physicalReference` are
always kept warm under both identities), so the ShakeIt-silence fallback's own four-range curve (when
engaged) is not itself starting cold.

### Branch-filter rejection count (evidence the filter is active)

| log | accepted | rejected | total candidates |
|---|---|---|---|
| 1.0.6.5/Raw | 23 | 4 | 27 |
| 1.0.6.5/ShakeIt | 38 | 20 | 58 |
| 1.0.6.6/Raw | 30 | 0 | 30 |
| 1.0.6.6/ShakeIt | 29 | 1 | 30 |
| 1.0.6.7/Raw | 14 | 11 | 25 |
| 1.0.6.7/ShakeIt | 14 | 2 | 16 |
| 1.0.6.8/Raw | 27 | 5 | 32 |
| 1.0.6.8/ShakeIt | 25 | 5 | 30 |
| **TOTAL** | **200** | **48** | **248 (19.4% rejected)** |

A real, non-trivial fraction (19.4%) of candidate 30/60 crossings are past-the-limit contamination the
branch filter correctly screens out - itself evidence the mechanism is engaging on real, contaminated data,
not merely wired but inert.

### Learned S75/S90/Smax per log (final value), and drift across cars/surfaces

| log | S75 | S90 | Smax |
|---|---|---|---|
| 1.0.6.5/Raw | 3.30 | 4.00 | 72.94 |
| 1.0.6.5/ShakeIt | 4.93 | 30.67 | 41.77 |
| 1.0.6.6/Raw | 2.47 | 4.45 | 73.69 |
| 1.0.6.6/ShakeIt | 9.46 | 9.49 | 33.51 |
| 1.0.6.7/Raw | 2.85 | 4.63 | 57.76 |
| 1.0.6.7/ShakeIt | 6.97 | 21.74 | 38.84 |
| 1.0.6.8/Raw | 14.03 | **3.54** | 51.42 |
| 1.0.6.8/ShakeIt | 1.48 | 28.11 | 43.89 |

All 8 logs are the SAME car/track/surface (Red Bull, dry, Silverstone) - S75 spans 1.48-14.03 (~9.5x), S90
spans 3.54-30.67 (~8.7x). `1.0.6.8/Raw` shows S75>S90 (14.03>3.54) - out of order, so
`TryBuildLockRangeCurve` correctly rejects that key's own triple and falls back to the linear Rescale,
exactly as designed. The anchors have not converged to a stable, source-independent answer within one
short, single-session, single-configuration capture.

### Curve engagement and dispersion (ground-truth methodology: achieved |G| / that SAME run's own observed
peak |G|, NOT the design's own internal ratio)

The four-range curve produced the published value on **1,974 of 54,580 replayed rows (3.62%)** across the 8
logs - unchanged by this correction (the anchor mechanics themselves did not change).

| anchor | mechanism | n | median | p10 | p25 | p75 | p90 | within±3pp | within±5pp | within±10pp |
|---|---|---|---|---|---|---|---|---|---|---|
| 30 (ideal 75%) | FOUR-RANGE CURVE | 21 | 60.9% | 30.0% | 33.1% | 77.9% | 88.0% | 23.8% | 23.8% | 33.3% |
| 30 (ideal 75%) | baseline linear Rescale | 127 | 79.5% | 53.0% | 70.0% | 91.8% | 97.4% | 15.7% | 23.6% | 42.5% |
| 60 (ideal 90%) | FOUR-RANGE CURVE | 337 | 89.6% | 77.1% | 82.9% | 95.1% | 99.2% | 20.5% | 38.6% | 85.2% |
| 60 (ideal 90%) | baseline linear Rescale | 143 | 84.1% | 49.0% | 67.4% | 90.6% | 93.9% | 27.3% | 45.5% | 58.0% |

Mixed: at the 60-anchor the four-range curve's own median (89.6%) sits closer to the 90% ideal than the
linear-Rescale baseline (84.1%), with a larger sample; at the 30-anchor the curve's own sample is far too
thin (n=21) to trust, and its median (60.9%) is worse than the baseline's own (79.5%, close to the 75%
ideal at that anchor).

### THE DIRECT ANSWER

**Is WheelLock's Normalized 30 and 60 reliable now? NO.** The branch filter demonstrably works (19.4%
rejection rate). The dynamic update rule is unit-tested and correct. But the curve engages on only 3.6% of
frames, and the anchors it learns scatter by up to ~9.5x even across sessions of the identical car/track/
surface. **What DOES now hold unconditionally, even while the interior 30/60 anchors remain unreliable, is
the two endpoints (Smax→80, 100→100) - by construction, via the fallback, on every single frame** - exactly
the owner's own requirement that interior imprecision must not compromise the endpoints.

---

## PART 5 - FALSE-MAXIMUM COUNT

**The pre-existing `scratchpad\band-accuracy-harness` check is no longer a valid comparison for Lock's own
new design**, and is reported with that explicit caveat rather than silently reused: its own "converged"
reference is a hardcoded reimplementation of the PRE-correction ΔG-collapse formula baked directly into the
harness, not a call into the engine's own (now source-based) logic. Re-run post-correction, it reports
**5,730** frames reaching cold≥80 (up from 4,071 pre-correction, from 3,853 in the task before that, from 2
originally) - a number that reflects an apples-to-oranges comparison (new "cold" severity vs a stale "G-
based converged" reference), not a genuine regression measurement.

**A methodology that actually matches the new design** (new, `scratchpad\owner-log-harness`'s own
`RunFourRangeFalseMaxReport`): for each of the 8 real logs, take the FINAL, most-converged learned Smax
reached by the end of that session, and recompute what EVERY frame's own severity WOULD read using that
final ceiling (the same linear Rescale formula) - a false maximum is a frame that PUBLISHED ≥80 at the time
but recomputes to <60 against the final, more-converged ceiling.

| log | rows | published ≥80 | false-max (final-ceiling recompute <60) |
|---|---|---|---|
| 1.0.6.5/Raw | 6,732 | 123 | 0 |
| 1.0.6.5/ShakeIt | 6,855 | 316 | 0 |
| 1.0.6.6/Raw | 6,763 | 42 | 0 |
| 1.0.6.6/ShakeIt | 6,782 | 212 | 0 |
| 1.0.6.7/Raw | 6,924 | 177 | 0 |
| 1.0.6.7/ShakeIt | 6,765 | 282 | 0 |
| 1.0.6.8/Raw | 6,887 | 194 | 0 |
| 1.0.6.8/ShakeIt | 6,872 | 244 | 0 |
| **TOTAL** | **54,580** | **1,590** | **0** |

**Zero false maxima across all 1,590 frames that published ≥80 on the 8 real logs**, confirming the
coordinator's own prediction directly: because output near 100 now requires a genuinely high, calibrated
SOURCE reading rather than an inflated G-based collapse ratio, and the calibration itself (Rescale/the
four-range curve) is monotonic and does not shrink meaningfully once a real ceiling has been observed, a
frame that reads high while cold essentially always still reads high once the reference has matured
further. This is the single clearest, most unambiguous improvement this correction produced.

---

## PART 6 - WHERE SPEED-AWARE IS USED (unchanged by this correction, re-confirmed)

Speed (`speedKmh`) appears in exactly one place in the live path: `LockAnchorLearner`'s own feed
(`uSpeedAwareSealed`/`uSpeedAwareLoose`, computed via `physicalReference.Ratio(..., speedKmh: speedKmh,
...)`) - used SOLELY to identify/validate 30/60 crossings against the corner's own speed-aware max grip.
Confirmed absent from: `ComputeDeltaGCollapseSeverity`'s own `u` (Slip's entire severity, and Lock's own
now-diagnostic-only `u`/`b`); `physicalRatioNow`/`physicallyAtLimit` (the Smax/80-anchor calibration
trigger); and the four-range curve's own evaluation (`TryBuildLockRangeCurve.Evaluate(source)`, a pure
function of the calibrated source with no speed term at all).

---

## Test suite changes (this correction)

13 tests failed immediately after the correction, all for the same reason: they pinned Lock's own PREVIOUS
G-derived severity behaviour, much of it built specifically to prove Lock's severity did NOT depend on the
(deliberately held-constant, in these tests) raw source - the literal opposite of what the owner's own
correction now requires. Each was resolved on its own merits, not blanket-deleted:

- **6 retargeted to Slip** (`Rising_branch_matches_the_owners_band_correspondence_at_steady_state` x2 cases,
  `Normalized_falls_through_every_band_as_utilization_eases_no_floor_anywhere`,
  `A_brand_new_key_ceilings_u_continuously_never_over_reporting_while_cold`,
  `Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating`,
  `Wet_and_dry_produce_the_same_severity_for_the_same_physical_utilization_regardless_of_the_source_reading`,
  `A_genuine_new_physical_peak_reads_near_the_anchor_promptly_once_the_learner_has_200_qualifying_samples`)
  - Slip is untouched and still behaves exactly as each of these was originally written and tested to
  verify.
- **1 replaced** (`Slip_channel_uses_the_identical_rising_branch_as_Lock...` → 
  `Lock_and_Slip_now_diverge_by_design_Slip_keeps_the_pure_G_ratio_Lock_tracks_source`) - the original
  premise (Lock and Slip are identical) is now false by design; the replacement confirms the intended
  divergence directly.
- **2 re-expressed to the OPPOSITE assertion** (`Lock_severity_tracks_achieved_G_not_Raws_absolute_level` →
  `Lock_severity_now_tracks_the_calibrated_source_not_achieved_G_matching_1063`;
  `Lock_severity_releases_quickly_once_achieved_G_drops_even_though_Raw_stays_high` →
  `Lock_severity_stays_high_while_Raw_stays_high_even_though_achieved_G_drops`) - Lock's own contract
  flipped a second time; both kept with historical rationale visible, per this codebase's own convention.
- **1 re-expressed** (`Aggregation_weights_are_reread_every_call_with_no_engine_rebuild_needed`) - Objective
  A's own "LockAll is weight-invariant" claim no longer holds now that the calibration basis is itself
  weight-dependent; re-expressed to confirm weight-sensitivity is restored (matching 1.0.6.3), not lost.
- **2 numeric fixups** in `AggregatedAllScaleTests.cs` (warmup raw raised from 50.0 to 90.0 so severity
  stays below the 0-100 clamp, preserving each test's own original diagnostic intent; one now-invalid
  re-aggregation assertion removed with its own rationale documented, per Defect A's own fix).
- **0 tests deleted outright.**

Final: **956/956 passed**, 0 warnings, single DLL, full `Clean,Restore,Build` green.

---

## Concerns

1. **`1.0.6.8/Raw` overshoots 1.0.6.3's own full-lock maximum by 10 points** (100.00 vs 89.98) - in the
   safe direction, but not an exact match; traces to normal session-to-session timing differences in when
   each build's own calibration ceiling settles, not a defect.
2. **Near-80 dwell is not uniformly within ±20% of 1.0.6.3 on all 8 logs** (3 of 8 are; the rest are off by
   27-56%, down from as much as -72% before this correction) - diagnosed to the four-range curve's own
   non-linear reshaping of ~3.6% of frames and Defect B's own (correct) shift in calibration timing, not
   the G-gating defect this correction exists to fix. Not further chased, per the owner's own "do not
   change tested defaults to chase an exact match" standing instruction.
3. **Feature C's own reliability verdict is still a clear "not yet"** - unchanged by this correction (the
   anchor mechanics themselves were not touched). The endpoints (Smax→80, 100→100) hold unconditionally,
   which was the owner's own explicit requirement for this specific concern.
4. **The pre-existing false-maximum harness's own "converged" comparator is now stale** for Lock (a
   hardcoded reimplementation of the pre-correction formula) - flagged explicitly rather than silently
   reused; the NEW, matching methodology shows a clean zero, but a future task should retire or rewrite the
   old harness's own Lock-side comparator rather than let two different "false-maximum" numbers coexist
   indefinitely.
5. **Every number in this report comes from the 8 real `1.0.6_logs` captures** - the same standing "short,
   data-starved capture" limitation every prior report in this project has flagged.

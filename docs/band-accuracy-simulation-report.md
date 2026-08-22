# Band accuracy simulation - how much of the available G is actually being achieved at Normalized 30/60/80

## STATUS

**MEASUREMENT AND REPORTING ONLY. NOTHING under `QAdvanceFeedback\QAdvanceFeedback\` or
`QAdvanceFeedback\QAdvanceFeedback.Tests\` was modified.** `dotnet build QAdvanceFeedback.sln -c
Release -t:Build` was run once, unmodified, to confirm the shipped DLL is current: **0 Warning(s), 0
Error(s)**. No git operation of any kind was run (the repo is not under git, confirmed directly). No
recursive deletes. All new code lives in `scratchpad\band-accuracy-harness\` (a new, throwaway net48
console harness, referencing the built `QAdvanceFeedback.dll` directly - same convention as
`scratchpad\deltag-validation-harness\`/`scratchpad\narrative-validation-harness\`, and NOT part of
`QAdvanceFeedback.sln`).

**This measurement uses the REAL, COMPILED engine** (`NormalizedWheelLockSlipEngine.Compute`, the
actual shipped `GripLearner`/`KeyedGripLearner`), not a re-implementation - every ratio/severity value
below was produced by calling the shipped public API (`KeyedGripLearner.Ratio`, `.PublishedPeakG`,
`.Samples`) directly, reusing the same replay conventions the existing validated harnesses use
(feeding `Diag.Source.Lock/Slip.*` as the configured source and `WheelLock/WheelSlip.Raw.*` as Layer
3's Raw, recomputing direction fresh via `engine.CurrentDirection`, never trusting the logs' own
pre-recorded `Diag.Direction`). The only place this report reproduces engine LOGIC rather than calling
it is the "converged reference" counterfactual in Table 3, which has no public API of its own (see
Part 4/Table 3's own methodology note) - built entirely from public helper classes
(`QAdvanceFeedback.Core.ClampMath`, `QAdvanceFeedback.Core.Projection.MonotoneCubicCurve`) plus the
shipped private constants, confirmed against the source and reproduced verbatim in comments.

**Headline finding, stated up front because it is the single most important result in this report**:
when "available G" is read literally, as the brief instructs ("whatever the shipped engine uses as
its speed-aware peak reference AT THAT MOMENT"), the achieved/available ratio at frames reporting
Normalized 30/60/80 is **NOT tight** - it overshoots the ideal (75%/90%/100%) substantially and
routinely exceeds 100% at every anchor, for both channels, in every log. This is not a bug in this
measurement: it is a direct, faithful reflection of a real, previously-documented limitation
(`docs\adaptive-peak-learner-report.md` Part 3, `docs\two-signal-band-mapping-report.md` Part 4) -
the adaptive peak reference itself frequently has not yet seen this car's true limit within these
short (53s-400s) real captures, so at any given moment "available G" is itself often an
*underestimate*, which inflates the ratio. The engine's own cold-start ceiling (design-internal `u`)
is the mechanism that keeps published `Normalized` sane despite this; see Part 1 for the
distinction and Part 3 for a related, more serious finding about what happens once the ceiling
formally lifts (200 samples) while the reference is still climbing.

---

## PART 0 - DEFINITIONS AND ASSUMPTIONS (read before the tables - the numbers are not interpretable without these)

**"Available G"**: exactly what the brief specifies - the shipped `GripLearner`'s speed-aware peak
reference at that instant, surface-blended (Sealed/Loose) the same way the engine's own severity
formula blends it: `availableG = Blend(Sealed.SpeedAwarePeakG(v), Loose.SpeedAwarePeakG(v),
smoothedLooseFraction)`, read via the PUBLIC `KeyedGripLearner.Ratio(..., applyColdStartCeiling:
false, speedKmh: v)` call - i.e. the **uncapped** ratio, NOT the cold-start-ceilinged `u` the engine
publishes internally as `LockUtilization`/`SlipUtilization`. This is a deliberate, load-bearing choice:
the brief asks "what fraction of the available G was ACTUALLY being achieved" - a physical question
about the car, not a question about the engine's own internal, deliberately-suppressed design
variable. **Both are reported**: every Table 1 cell is followed by a smaller cross-check table using
the design-internal, ceilinged `u` (what `docs\delta-g-band-mapping-report.md` itself validated
against) purely so the (often large) gap between the two is interpretable, not mistaken for a
methodology error.

**"Achieved G"**: `|Diag.Telemetry.LongitudinalG|` directly (AchievedMotion's ladder level 1) - the
same approximation the pre-existing, previously-accepted `deltag-validation-harness` already uses for
its own isolated-G collection. Level 2 (speed-derived) and level 3 (no-signal fallback) are not
separately replicated in this harness; every log used here has a direct `LongitudinalG` column, so
this is not a material limitation for these nine captures.

**Capture band**: ±5 Normalized points around each anchor (30/60/80), on the **published `.All`**
value (the number a driver actually sees), widened to ±10, then ±15, then ±20 if fewer than 20 frames
are captured at the current width - the ACTUAL width used is printed in every cell (`n=NNN(±W)`), and
any cell still below 20 frames even at ±20 is marked `[SPARSE ... treat cautiously]`.

**Statistic suppression**: n<5 -> only n/min/mean/max reported if available; p90 requires n>=5; p95/p99
require n>=10; p99 is additionally marked `n/a-coarse` if n<30 (a "99th percentile" computed from under
30 samples is not treated as solid here, per the brief's explicit instruction).

**"Deviation"/"within tolerance"**: `deviation = mean(ratio%) - ideal%` (75/90/100); "within ±3pp" means
`|deviation| <= 3.0`.

**Rows**: pooled = all 7 replayable logs combined; per-log = one row per (tag, source) CSV file (the
harness's own, previously-established key shape, e.g. `c153/Raw`); per-(log,car) = only where
`Diag.CarId` exists at all - **confirmed directly: only the two `c_1_5_3_e_d` files carry `Diag.CarId`
("Sauber"/"F1 Generic"); all four `Common_1_5_e_d` files and all three top-level
`QAdvanceFeedback.session-*.csv` files have an empty `Diag.CarId` column (2 of them don't have the
column at all)** - those 7 logs are reported as one pooled, unidentified-car row per log (not silently
dropped), exactly per the brief's instruction. `Diag.GameId` is likewise empty for the same 7 logs.

**Unreplayable logs, reported not dropped**: `QAdvanceFeedback.session-20260815-210223-raw.csv`
(`s210223`) and `QAdvanceFeedback.session-20260815-210421-shakeit.csv` (`s210421`) have **no
`Diag.Telemetry.*` columns at all** - confirmed directly by the harness before any replay attempt -
and cannot be replayed (matches `docs\two-signal-band-mapping-report.md`'s own finding on these same
two files). **7 of the 9 logs were replayed; 2 could not be.** All nine are named explicitly here so
none is silently missing from the accounting.

**Cold-start replay initialisation** (Table 3): every log is replayed through a **brand-new**
`NormalizedWheelLockSlipEngine()` with no `ImportAll`/persisted state at all - genuinely cold, exactly
what the brief asks for. "Qualifying observation count" = the physical-reference `KeyedGripLearner`'s
own `Samples(gameId, carId, "", Sealed) + Samples(..., Loose)`, read directly via the public API after
each frame - this is not a re-implementation, it is the actual learner's own sample counter.

**"Converged" reference** (Table 3's cold-vs-converged check): after a log finishes its (cold, causal)
replay, the physical-reference `KeyedGripLearner` for that log has reached whatever state it reached by
the END of that session. For every historical frame, this report re-queries that SAME, now-matured
learner (a pure, side-effect-free query - `KeyedGripLearner.Ratio`/`.PublishedPeakG`/`.Samples` mutate
nothing) with that frame's own historical g/speed/ΔG, `applyColdStartCeiling: false` - i.e. "what would
this exact physical moment have been judged as, if the engine had already seen everything this session
ever showed it." The severity formula itself (`gate`/`collapse`/`b`/`R`/`F`/blend) is reproduced from
the shipped private constants (confirmed by reading `NormalizedWheelLockSlipEngine.cs` directly:
`BandGateStart=0.80`, `BandGateWidth=0.15`, `FullLockOutputBase=80`, `FullLockOutputSpan=20`,
`CollapseDGScaleFraction=0.12`, `CollapseDGScaleColdFallbackG=0.15`, `CollapseDGScaleMinSamples=5`)
using the SAME public `MonotoneCubicCurve`/`ClampMath` helper classes the shipped engine itself uses -
nothing here is a re-implementation of the learner, only of the small, already-published formula that
combines its output (see Part 3 for why this was necessary: that formula is private and this task is
read-only). **Important caveat, stated plainly**: "converged" means "this short session's own final
state", NOT a true, ground-truth physical peak - per `docs\adaptive-peak-learner-report.md` Part 3,
these captures are themselves data-starved (Lock especially), so even the "converged" reference can
still be an underestimate of the car's true limit. The comparison below therefore measures "was this
frame's output later contradicted by more evidence FROM THE SAME SESSION", a strictly weaker but still
directly informative question.

**Car-level severity vs published `.All`**: reported separately throughout, per the brief's own
instruction. `engine.LockCarLevelSeverity`/`SlipCarLevelSeverity` is the ΔG-collapse mapping's own
direct output, before the per-wheel aggregation/`AllScale` mechanism (`docs\all-channel-scale-and-
surface-gap-report.md`) re-distributes it across four wheels and re-aggregates. Table 1/2's main
statistic is against **published `.All`** (what a driver actually sees, per the brief's framing);
car-level severity is reported alongside it in Table 2's source-agreement checks and in Table 3's
cold-vs-converged check (chosen there specifically to isolate the ΔG-collapse mapping/learner behaviour
from the orthogonal, unrelated per-wheel aggregation mechanism, exactly as prior reports in this
codebase already do for the same reason).

---

## TABLE 1 - BAND ACCURACY, ALL-CHANNEL, BOTH CHANNELS

*(`n=NNN(±W)` = frames captured and the band width actually used; `dev` = mean ratio% minus the ideal
75/90/100; `WITHIN`/`OUTSIDE` = the ±3pp acceptance bar.)*

### Lock

| Row | Normalized≈30 (ideal 75%) | Normalized≈60 (ideal 90%) | Normalized≈80 (ideal 100%) |
|---|---|---|---|
| **POOLED (7 logs)** (n=6876 qualifying) | n=1671(±5) min=70.3 mean=**140.2** max=491.2 p90=284.4 p95=345.1 p99=423.4 \| dev=+65.2pp **OUTSIDE** | n=828(±5) min=83.9 mean=**99.8** max=178.8 p90=112.8 p95=117.2 p99=158.6 \| dev=+9.8pp **OUTSIDE** | n=296(±5) min=90.1 mean=**112.3** max=141.1 p90=131.2 p95=136.8 p99=140.1 \| dev=+12.3pp **OUTSIDE** |
| c153/Raw (n=1936) | n=409(±5) mean=122.4 p95=344.7 | dev=+47.4 OUTSIDE | n=307(±5) mean=97.7 p95=109.5 | dev=+7.7 OUTSIDE | n=31(±5) mean=110.6 p95=134.9 | dev=+10.6 OUTSIDE |
| c153/ShakeIt (n=1424) | n=315(±5) mean=120.9 p95=282.8 | dev=+45.9 OUTSIDE | n=249(±5) mean=99.7 p95=118.6 | dev=+9.7 OUTSIDE | n=82(±5) mean=104.8 p95=117.0 | dev=+4.8 OUTSIDE |
| common_dry/Raw (n=792) | n=401(±5) mean=154.0 p95=405.0 | dev=+79.0 OUTSIDE | n=60(±5) mean=107.6 p95=164.9 | dev=+17.6 OUTSIDE | n=13(±20, **SPARSE**) mean=116.1 | dev=+16.1 OUTSIDE |
| common_dry/ShakeIt (n=796) | n=140(±5) mean=186.8 p95=330.3 | dev=+111.8 OUTSIDE | n=47(±5) mean=105.8 p95=132.6 | dev=+15.8 OUTSIDE | n=32(±15) mean=121.5 p95=158.7 | dev=+21.5 OUTSIDE |
| common_wet/Raw (n=478) | n=145(±5) mean=108.4 p95=295.6 | dev=+33.4 OUTSIDE | n=92(±5) mean=102.0 p95=117.8 | dev=+12.0 OUTSIDE | n=74(±15) mean=102.7 p95=109.2 | dev=**+2.7 WITHIN** |
| common_wet/ShakeIt (n=439) | n=151(±5) mean=206.8 p95=390.8 | dev=+131.8 OUTSIDE | n=21(±5) mean=99.4 | dev=+9.4 OUTSIDE | n=7(±20, **SPARSE**) mean=98.1 | dev=**-1.9 WITHIN** (n=7, sparse) |
| s230140 (n=1011) | n=110(±5) mean=102.7 p95=245.9 | dev=+27.7 OUTSIDE | n=52(±5) mean=93.9 p95=111.3 | dev=+3.9 OUTSIDE | n=178(±5) mean=116.4 p95=138.5 | dev=+16.4 OUTSIDE |
| c153/Raw/F1 Generic (n=928) | n=245(±5) mean=126.6 | dev=+51.6 OUTSIDE | n=65(±5) mean=89.9 | dev=**-0.1 WITHIN** | n=53(±10) mean=96.7 | dev=-3.3 OUTSIDE (marginal) |
| c153/Raw/Sauber (n=1008) | n=164(±5) mean=116.1 | dev=+41.1 OUTSIDE | n=242(±5) mean=99.8 | dev=+9.8 OUTSIDE | n=145(±10) mean=118.1 | dev=+18.1 OUTSIDE |
| c153/ShakeIt/F1 Generic (n=643) | n=142(±5) mean=136.0 | dev=+61.0 OUTSIDE | n=95(±5) mean=103.2 | dev=+13.2 OUTSIDE | n=79(±5) mean=105.1 | dev=+5.1 OUTSIDE |
| c153/ShakeIt/Sauber (n=781) | n=173(±5) mean=108.6 | dev=+33.6 OUTSIDE | n=154(±5) mean=97.5 | dev=+7.5 OUTSIDE | n=30(±15) mean=97.8 | dev=**-2.2 WITHIN** |

Full min/max/p90/p99 detail for every row: `scratchpad\band-accuracy-harness\out\table1_lock.md`.

**Cross-check only (design-internal, cold-start-ceilinged `u`)** - NOT the achieved/available ratio,
included so the gap above is interpretable:

| Row | mean@30 | mean@60 | mean@80 |
|---|---|---|---|
| POOLED | 75.4 | 88.9 | 110.2 |
| c153/Raw | 74.8 | 88.8 | 97.0 |
| c153/ShakeIt | 75.6 | 89.5 | 103.6 |
| common_dry/Raw | 75.6 | 87.7 | 87.5 |
| common_dry/ShakeIt | 74.8 | 89.0 | 93.1 |
| common_wet/Raw | 76.1 | 88.7 | 93.6 |
| common_wet/ShakeIt | 76.2 | 86.9 | 86.5 |
| s230140 | 75.6 | 89.3 | 116.0 |

The design-internal `u` at the 30 and 60 anchors is genuinely tight (74.8-76.2 vs ideal 75; 86.9-89.5
vs ideal 90) - closely matching `docs\delta-g-band-mapping-report.md`'s own pooled 30.9/59.2 finding.
At the 80 anchor it is looser and sometimes exceeds 100% (up to 116.0 for s230140) - expected and
correct, **not an error**: `u` is deliberately not clamped above 1.0 once mature (a genuine lock event
can exceed the historical peak - `GripLearner.Ratio`'s own doc comment), and frames captured near
Normalized≈80 legitimately include both "exactly at the limit, no collapse" (u≈1.0) and "past the
limit, collapsing" (u can be >1.0, blended with a nonzero `b`) - both are correctly described by "80-100 =
delivering less than the maximum effort available".

### Slip

| Row | Normalized≈30 (ideal 75%) | Normalized≈60 (ideal 90%) | Normalized≈80 (ideal 100%) |
|---|---|---|---|
| **POOLED (7 logs)** (n=40647 qualifying) | n=3596(±5) mean=**64.0** p95=77.7 | dev=**-11.0pp OUTSIDE** | n=1299(±5) mean=**82.9** p95=101.4 | dev=**-7.1pp OUTSIDE** | n=904(±5) mean=**145.8** p95=379.8 | dev=**+45.8pp OUTSIDE** |
| c153/Raw (n=12672) | n=1338(±5) mean=60.4 p95=74.0 | dev=-14.6 OUTSIDE | n=372(±5) mean=80.1 p95=94.1 | dev=-9.9 OUTSIDE | n=23(±5) mean=119.3 | dev=+19.3 OUTSIDE |
| c153/ShakeIt (n=9981) | n=813(±5) mean=64.7 p95=69.8 | dev=-10.3 OUTSIDE | n=337(±5) mean=83.2 p95=101.8 | dev=-6.8 OUTSIDE | n=3145(±10) mean=155.5 p95=337.9 | dev=+55.5 OUTSIDE |
| common_dry/Raw (n=3779) | n=273(±5) mean=64.3 | dev=-10.7 OUTSIDE | n=132(±5) mean=81.9 | dev=-8.1 OUTSIDE | n=873(±10) mean=115.5 p95=230.6 | dev=+15.5 OUTSIDE |
| common_dry/ShakeIt (n=4151) | n=322(±5) mean=59.0 | dev=-16.0 OUTSIDE | n=168(±5) mean=83.2 | dev=-6.8 OUTSIDE | n=43(±5) mean=129.6 p95=318.1 | dev=+29.6 OUTSIDE |
| common_wet/Raw (n=2307) | n=215(±5) mean=63.7 | dev=-11.3 OUTSIDE | n=64(±5) mean=79.3 | dev=-10.7 OUTSIDE | n=24(±5) mean=99.9 | dev=**-0.1 WITHIN** |
| common_wet/ShakeIt (n=2548) | n=218(±5) mean=66.7 | dev=-8.3 OUTSIDE | n=42(±5) mean=80.2 | dev=-9.8 OUTSIDE | n=1037(±10) mean=149.1 p95=397.3 | dev=+49.1 OUTSIDE |
| s230140 (n=5209) | n=417(±5) mean=76.2 | dev=**+1.2 WITHIN** | n=184(±5) mean=90.1 | dev=**+0.1 WITHIN** | n=799(±5) mean=148.2 p95=388.0 | dev=+48.2 OUTSIDE |
| c153/Raw/F1 Generic (n=5946) | n=801(±5) mean=58.5 | dev=-16.5 OUTSIDE | n=183(±5) mean=82.0 | dev=-8.0 OUTSIDE | n=23(±5) mean=119.3 | dev=+19.3 OUTSIDE |
| c153/Raw/Sauber (n=6726) | n=537(±5) mean=63.4 | dev=-11.6 OUTSIDE | n=189(±5) mean=78.3 | dev=-11.7 OUTSIDE | n=2307(±10) mean=190.5 p95=500.1 | dev=+90.5 OUTSIDE |
| c153/ShakeIt/F1 Generic (n=4339) | n=314(±5) mean=63.4 | dev=-11.6 OUTSIDE | n=170(±5) mean=86.2 | dev=-3.8 OUTSIDE | n=1318(±10) mean=194.7 p95=900.0 | dev=+94.7 OUTSIDE |
| c153/ShakeIt/Sauber (n=5642) | n=499(±5) mean=65.5 | dev=-9.5 OUTSIDE | n=167(±5) mean=80.2 | dev=-9.8 OUTSIDE | n=1827(±10) mean=127.2 p95=251.6 | dev=+27.2 OUTSIDE |

Full detail: `scratchpad\band-accuracy-harness\out\table1_slip.md`.

**Cross-check (design-internal ceilinged `u`)**: POOLED mean@30=63.0, mean@60=81.3, mean@80=**145.7**.
Unlike Lock, **Slip's own design-internal `u` ALSO overshoots substantially at the 80 anchor** (not
just the raw achieved/available ratio) - consistent with `docs\delta-g-band-mapping-report.md`'s own
Concern #1 ("Slip's own car-level time-above-80... its cold-start ceiling engages less often... gate/
collapse dynamics show more spread"), now independently confirmed by this measurement too.

**Why the 80-anchor overshoot is large and structural for BOTH channels, explained**:
`docs\delta-g-band-mapping-report.md` validated band correspondence in the FORWARD direction (bin by
`u`, ask what severity results). Table 1 above is explicitly the REVERSE direction the brief asks for (bin
by published output, ask what the achieved ratio was) - and the mapping is many-to-one specifically
near 80-100 (many different `(u, collapse)` combinations blend to the same published number, per
`Normalized = (1-b)*R(u) + b*F(b)`), so reversing the query legitimately produces a wider, higher-mean
distribution than the forward check did. This is a genuine, structural, and now-measured property of
the mapping, not a contradiction of the forward-direction numbers in `delta-g-band-mapping-report.md`.

---

## TABLE 2 - SHAKEIT VS RAW SIDE BY SIDE

**c153 - SAME ROUTE, driven twice, once per source (the brief's own "cleanest comparison") - genuinely like-for-like.**

Lock: ShakeIt n=1424 (30:mean=120.9/dev=+45.9 OUTSIDE, 60:mean=99.7/dev=+9.7 OUTSIDE, 80:mean=104.8/dev=+4.8 OUTSIDE) vs Raw n=1936 (30:mean=122.4/dev=+47.4 OUTSIDE, 60:mean=97.7/dev=+7.7 OUTSIDE, 80:mean=110.6/dev=+10.6 OUTSIDE) - **the two sources track each other closely** (within a few points at every anchor).

Slip: ShakeIt n=9981 (30:mean=64.7/dev=-10.3, 60:mean=83.2/dev=-6.8, 80:mean=155.5/dev=+55.5) vs Raw n=12672 (30:mean=60.4/dev=-14.6, 60:mean=80.1/dev=-9.9, 80:mean=119.3/dev=+19.3) - **broadly similar shape, but the 80-anchor overshoot is much larger for ShakeIt (155.5 vs 119.3)** - see the source-disagreement discussion below.

Full per-car breakdown (Sauber/F1 Generic x Raw/ShakeIt): `scratchpad\band-accuracy-harness\out\table2.md`.

**Source-agreement at car level** (car-level severity, before per-wheel aggregation - the number that
matters for "do the two sources agree by construction" per the note in the brief):

| channel | ShakeIt median | Raw median | gap | published `.All` ShakeIt | published `.All` Raw | gap |
|---|---|---|---|---|---|---|
| Lock | 36.3 | 37.3 | **-1.0** | 36.3 | 37.3 | **-1.0** |
| Slip | 21.8 | 20.8 | **+1.0** | 32.9 | 33.5 | **-0.6** |

**This matches `docs\delta-g-band-mapping-report.md`'s own headline finding almost exactly** (Lock car-
level gap 1.0, Slip car-level gap 1.0) and `docs\all-channel-scale-and-surface-gap-report.md`'s
published-`.All` gap for Lock (1.0) - **directly cross-validating this new, independent harness against
the prior, already-accepted measurement.** Confirms the "car-level Normalized is derived from G, not
source, so the two sources should agree closely BY CONSTRUCTION" expectation, for the c153 pair, as
predicted.

**common_wet - NOT confirmed same route, independently captured file - treat with caution.**

Source-agreement: Lock car-level severity ShakeIt=33.7 Raw=42.8, gap=**-9.0** (published `.All` gap
-8.4). Slip car-level severity ShakeIt=30.4 Raw=25.2, gap=**+5.2** (published `.All` gap +6.2). A real,
moderate disagreement for Lock specifically - **matches `docs\all-channel-scale-and-surface-gap-
report.md`'s own diagnosis** of this exact pair (cold-start-confidence-trajectory divergence between
two short, independently-captured sessions, not a deterministic surface-keying defect - Objective B,
"diagnosed, not closed").

**common_dry - NOT confirmed same route - treat with caution.**

Source-agreement: Lock car-level severity ShakeIt=30.0 Raw=31.2, gap=**-1.2** (tight, matches prior
reports). **Slip car-level severity ShakeIt=31.4 Raw=11.5, gap=+19.8 (published `.All` gap +32.2) - a
LARGE disagreement, notably bigger than anything previously tabulated for this pair** (prior reports
measured Lock's own wet/dry surface gap and Lock's ShakeIt/Raw source gap, but did not previously
publish this specific Slip ShakeIt-vs-Raw-within-dry comparison). **Investigated**: this traces to the
same standing, previously-flagged limitation as the wet pair above - `docs\all-channel-scale-and-
surface-gap-report.md` Part 5 already noted "For Slip/Raw, BOTH wet and dry reached full confidence
(1.000) within their own captures... Slip's own 13.6-point [wet-vs-dry] gap likely traces instead to
the speed-aware per-speed model (fitted independently per file) or genuine session-to-session driving-
style variance; not fully isolated further." This report's own new Slip-ShakeIt-vs-Raw-within-dry
comparison (19.8/32.2pp) is consistent with, and adds further evidence for, that same standing,
unresolved limitation - independently-captured short files, each fitting its own speed model from
scratch, disagree substantially for Slip specifically. **Not resolved here** (would require either
retuning already-tuned `GripLearner` constants against unsuitable data, or a genuinely controlled
capture this repository does not have - the same reasoning the cited report already gave).

---

## TABLE 3 - COLD START ACCURACY

Per-log checkpoint tables (qualifying-observation counts 1/5/10/25/50/100/200/500/full,
capture band fixed at ±5 for this progression, deviation from ideal at each anchor, elapsed driving
time): full detail for all 7 replayable logs x 2 channels in
`scratchpad\band-accuracy-harness\out\table3.md`. Representative excerpt (`c153/ShakeIt`, Lock):

| checkpoint (obs) | elapsed | n(30) dev | n(60) dev | n(80) dev | all 3 within ±3pp? |
|---|---|---|---|---|---|
| 1 | 12.0s | n=1 dev=+13.3 | n=0 | n=0 | NO |
| 10 | 12.1s | n=10 dev=+177.8 | n=0 | n=0 | NO |
| 50 | 22.2s | n=79 dev=+74.1 | n=1 dev=-2.6 | n=0 | NO |
| 100 | 84.2s | n=101 dev=+57.9 | n=27 dev=+12.0 | n=2 dev=-6.3 | NO |
| 200 | 286.4s | n=272 dev=+53.2 | n=215 dev=+11.3 | n=52 dev=+2.5 | NO |
| full (269 obs) | 334.7s | n=315 dev=+45.9 | n=249 dev=+9.7 | n=82 dev=+4.8 | NO |

**Convergence, measured directly, for all 14 (log, channel) combinations: NONE ever reaches a stable
"within ±3pp at all three anchors, and stays there" state within the captured session length** (53s to
400s of driving time; 118 to 2966 total qualifying observations by session end), using the literal
achieved/available ratio defined in Part 0. This is an honest, direct, and important finding, not a
harness defect: it is the mechanical consequence of the same headline finding from Table 1 (the live
reference itself keeps evolving throughout these short sessions, so the RATIO never settles). **Using
the design-internal, ceilinged `u` instead** (the number the ceiling mechanism is actually built to
protect), the 30/60 anchors are already close to ideal from very early in every session (Part 0's cross-
check table shows pooled 75.4/88.9 across the WHOLE session, and the per-checkpoint progression - see
the full table3.md - shows the 30-anchor deviation on ceilinged `u` settling to single digits within the
first 25-50 observations in most logs) - i.e., **the engine's own safety mechanism does its job well
from early in a session; the literal physical achieved/available ratio simply never stabilizes in these
particular short real captures**, and that distinction should not be conflated.

**Plain language**: in the first corner (checkpoint 1-10 observations, roughly the first 12-30 seconds
of driving), the achieved/available ratio at the 30-anchor is wildly noisy (dev +60 to +270pp) simply
because so few frames are captured there yet (n=1-23) - not evidence of anything beyond small-sample
noise. By the end of the first lap-equivalent (checkpoint ~100, roughly 1.5-2 minutes in), the 30/60
anchors have settled into a persistently-elevated-but-stable pattern (dev roughly +50-90pp at 30,
+10-15pp at 60) that **does not further improve with more laps** within these particular short
captures - it is a structural gap (per Table 1's headline finding), not a transient cold-start artifact
that a few more laps would fix. After a few laps (checkpoint 200-500/full), the pattern is essentially
unchanged from checkpoint ~100 - confirming the gap is NOT simply "still warming up."

### Cold vs converged - does cold ever OVER-report? (the project's own hard safety requirement)

Measured at car-level severity, comparing each frame's actual (causal, cold) published severity against
what the SAME physical frame would read against that session's own final, matured reference (see Part 0
for the exact definition):

**Across all 7 replayable logs x 2 channels (roughly 73,000 total qualifying frames): 15,453 frames
(≈21%) show cold EXCEEDING the converged/hindsight value by more than 0.5pp** - i.e. cold does NOT
purely under-report in this data; it measurably over-reports too, relative to what more evidence from
later in the SAME session would say. This is reported plainly because it bears directly on the
project's own stated hard requirement ("over-reporting while cold is a safety problem").

**Split by WHY, which matters for how seriously to take this**:

| | frames | max cold value seen | of frames reaching cold>=80 ("fully locked/spun") | min converged value among those |
|---|---|---|---|---|
| **While samples < 200** (the sample-count-gated cold-start ceiling is nominally still active) | 1,745 | 100.0 | 7 | **58.7** |
| **After samples >= 200** (confidence=1.0, the ceiling is FULLY LIFTED by design) | 13,708 | 100.0 | **3,858** | **3.0** |

**This is the important, genuinely new finding of this report, and it does contradict the spirit of the
project's own "never over-report while cold" requirement, though not the letter of any single existing
unit test**: the literal cold-start-ceiling window (sample count < 200) does its job reasonably well -
over-statement happens (1,745 frames) but rarely reaches the extreme 80-100 band (7 frames), and even
then the hindsight/converged value was still substantial (>=58.7, i.e. both cold and converged agree
"this was a serious event", just disagree on exactly how serious). **Once the engine's own confidence
reaches 1.0 (200 qualifying samples - which for Slip specifically can happen within seconds, and for
Lock within roughly a minute or two of committed braking, per the checkpoint tables above), the ceiling
is removed ENTIRELY, and the underlying `GripLearner` peak reference is still free to keep climbing for
the REST of the session** (this is by design - see `GripLearner.cs`'s own remarks: "so the learner keeps
tracking a car that gets faster tyres... rather than freezing"). In these particular short, data-starved
real captures, that climb is often substantial, so a genuine, "fully locked" (80-100) reading published
mid-session is, in **3,858 frames**, later contradicted by evidence from later in that SAME session down
to as low as **3.0** (i.e., in hindsight, barely any lockup at all). One concrete example (full detail:
`scratchpad\band-accuracy-harness\out\debug_cold_vs_converged.txt`): `c153/ShakeIt` Slip, several frames
around row 829-1065 (sample count already at 342, well past the 200-sample maturity bar) published
severity of exactly **100.0**, while the same physical frame judged against that session's own final
state reads as low as **13.2-37.1**.

**Why this happens, diagnosed, not merely observed**: "confidence = samples/200 reaches 1.0" answers
"have we seen ENOUGH EVIDENCE to trust the CURRENT estimate", not "has the estimate already reached its
true asymptote" - these are different questions, and the second one has no protection in the shipped
design once the first is satisfied. This is the SAME standing data-starvation limitation
`docs\adaptive-peak-learner-report.md` (Part 3: "GMech chronically below what was observed... 24-57%
gap... not fully solved") and `docs\two-signal-band-mapping-report.md` (Part 4: "Lock under-converges
within a realistic session... Slip erodes downward over a long session") already flagged from different
measurement angles - **this report adds a THIRD, concrete, safety-relevant consequence of that same
root cause**: real frames where a maximal ("100, fully locked") reading was published and later
evidence from the very same session showed it should have been much lower.

**Plain-language summary for Table 3**: the first corner is noisy but harmless (too few samples for the
formula to say anything confidently, and the design correctly stays conservative). By the first lap, the
engine has usually already exceeded its own internal "full confidence" (200 samples) for at least the
Slip channel and often for Lock too - but "full confidence" in these short captures does NOT mean "has
seen the car's true limit yet". Over the next few laps, in a meaningful minority of frames (varies per
log; see the per-log tables), a genuinely severe-looking reading (up to the literal maximum, 100) is
published that a slightly-longer look at the SAME session would have judged as far milder. This is NOT
the "over-reports in the first few observations" failure mode the design's ceiling was built to prevent
(that mechanism works reasonably well, per the <200-samples row above) - it is a related but distinct
failure mode: **the design's own notion of "no longer cold" fires before the underlying reference has
actually stabilized, in captures this short.**

---

## CONSISTENCY CHECK AGAINST `docs\delta-g-band-mapping-report.md`

- **Band correspondence (forward direction, u -> severity)**: NOT contradicted. This report's own
  ceilinged-`u` cross-check reproduces closely comparable numbers (Lock pooled 75.4/88.9 vs the cited
  report's 30.9/59.2 - equivalent once converted between the R-curve's percentage and Normalized-point
  scales) and the Table 2 c153 source-invariance gap (car-level severity gap 1.0/1.0 for Lock/Slip)
  matches that report's own headline number (1.0/1.0) **exactly**, cross-validating this new,
  independent harness against the prior, already-accepted one.
- **Time-above-80 / falling-branch validity**: out of this report's scope (not re-measured here); no
  claim made either way.
- **Cold-start "never over-reports"**: **this report's own new finding (above) is in tension with the
  PROJECT'S stated hard requirement** ("over-reporting while cold is a safety problem"), though it does
  NOT falsify any specific existing unit test (those tests exercise the ceiling mechanism itself on
  synthetic fixtures, which IS mathematically monotonic and non-over-reporting by construction - see
  `A_brand_new_key_ceilings_u_continuously_never_over_reporting_while_cold`). The gap is between "the
  ceiling formula is provably safe" (true, and unaffected by this report) and "the system as a whole
  never publishes a reading that later turns out to have been too high" (measurably false in these real
  captures, for the reason diagnosed above) - **this is exactly the kind of nuance the owner needs to
  know and is reported here plainly.**

---

## QUALITY / SPARSITY NOTES

- Cells marked `[SPARSE ... treat cautiously]` in Table 1 (e.g. Lock 80-anchor for `common_dry/Raw`
  n=13, `common_wet/ShakeIt` n=7) remain below the 20-frame comfort threshold even at the widest ±20
  band - reported with their actual n rather than suppressed, per the brief's instruction, but should
  not be treated as solid.
- Every `p99=n/a-coarse(n<30)` cell is a percentile deliberately withheld because fewer than 30 samples
  fed it - not printed as if it were a real 99th percentile.
- Table 3's per-checkpoint cells for small observation counts (1/5/10) are frequently `n=0 (none)` for
  the 60/80 anchors simply because so few frames exist yet - reported as `(none)`, never fabricated.

## ASSUMPTIONS THAT AFFECT THE NUMBERS (consolidated)

1. Capture band starts at ±5, widens to ±20 only if needed, always reported per-cell.
2. "Available G" is the UNCAPPED live reference (Part 0) - the single biggest driver of how large the
   Table 1 deviations look; the ceilinged-`u` cross-check is the fairer comparison against the design's
   own internal validation in `delta-g-band-mapping-report.md`.
3. "Achieved G" = `|LongitudinalG|` directly (ladder level 1 only).
4. Logs lacking `Diag.CarId`/`Diag.GameId` (7 of 9) are grouped as one pooled row per log, keyed by
   empty-string game/car - matching what the real plugin itself would do for an unidentified title.
5. Cold replay = a brand-new engine instance, no persisted state, per log.
6. "Converged" = this session's own final learner state, NOT an independent ground truth - a real,
   stated limitation given how short and data-starved these captures are.
7. Constants used in the Table-3 converged-reference recomputation are reproduced from the shipped
   source verbatim (cited above) - if a future task changes those constants, this harness's own
   replicated formula must be updated to match, or it will silently drift from the real engine.

## Concerns

1. **The cold-vs-converged over-statement finding (Table 3) is the most important result in this report
   and deserves direct owner attention** - it is a real, measured, safety-adjacent gap between the
   letter of the cold-start-ceiling mechanism (which is fine) and the spirit of "never publish a reading
   that turns out to be too high" (which does not fully hold once the ceiling formally lifts at 200
   samples, in captures this short/data-starved).
2. **Every Table 1/2 number is influenced by the same short-capture data-starvation limitation** three
   prior reports in this codebase already flagged for different reasons - a longer, richer real capture
   (many more minutes of varied hard braking/acceleration per car) would very likely narrow the Table 1
   deviations substantially, since the "available G" denominator would have more chance to reach its
   true asymptote. This report's numbers should be read as "how accurate in captures of THIS length",
   not as a permanent ceiling on the design's own achievable accuracy.
3. **Table 3's "convergence never reached" finding is entirely a consequence of choosing the literal,
   uncapped achieved/available ratio** (per the brief's own explicit definition) - it should not be read
   as "the engine takes forever to warm up"; the design-internal ceilinged `u` is already well-behaved
   from early in every session, as the cross-check table shows.
4. **The common_dry Slip ShakeIt-vs-Raw disagreement (19.8pp car-level / 32.2pp published) is a new,
   previously-unmeasured combination** - real and worth the owner's attention, but not independently
   isolated further here (it traces to the same standing, already-diagnosed limitation as the wet pair).
5. **Generalisation beyond these nine real logs is unverified** - same standing caveat every constant/
   measurement report in this codebase already carries.
6. Several Table 1 cells are sparse even at the widest band (±20) - explicitly flagged per cell, not
   silently included in any headline average.

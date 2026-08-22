# Speed-Aware Reference Fix Report

## 0. Scope and terminology

This task targets `GripLearner.SpeedAwarePeakG(speedKmh)` - the "speed-appropriate available peak G"
denominator `physicalReference.Ratio(..., speedKmh: ...)` divides by (exposed to the engine as
`uSpeedAware`, the harness's `USpeedAware`). Its ONLY live consumer in the shipped 1.0.6.0 engine is
`LockAnchorLearner.Observe` (WheelLock S75/S90 anchor teaching) - it never enters published severity for
either channel (confirmed by code inspection: severity is `calibratedMean`/the four-range curve, which
never takes a speed parameter).

**Terminology correction applied throughout this report and the new code comments**: the engine's 0-100
per-wheel value (`Diag.Source.Lock.*`, `WheelLock.Raw.*`) is a **game-specific normalised wheel-speed
difference**, never referred to here as a "slip ratio" (the physical SAE/ISO slip ratio is a different,
game-independent quantity this plugin does not compute). Called "raw wheel-slip value" below.

## 1. Two distinct diagnosed defects

1. **Decay erosion** - the shipped per-speed-bucket reference (`AdaptivePeakState`, an evidence-weighted
   corroboration estimator) under-converges at a single bucket's sparse sample rate, chronically settling
   below the car's true achievable peak at that speed.
2. **Unreached limits** (owner's second-round finding) - even a perfect estimator of "highest G achieved in
   this bucket" is not "grip available in this bucket" unless the tyre was actually pushed to its limit
   there. An F1 car's high-speed braking is frequently brake-torque-limited (downforce leaves real grip
   margin in reserve), so folding ordinary hard braking into a bucket's reference teaches "hardest attempt"
   as if it were "physical ceiling".

Both are fixed; see Section 2. Non-negotiable constraints preserved (verified by inspection - none of these
files/formulas were touched): `CanonicalAtLimitAnchor` 80, Lock/Slip aggregation weights, the Normalize
Pattern selector, curve anchors 30/62/78 (10/30/60 Lock, 10/35/75 Slip), the Layer 3 Raw fix, all counter
saturation caps, no hard sample gate, no `Math.Max(severity, ...)` floor, and Slip's severity formula
(`docs\...` history notes in `NormalizedWheelLockSlipEngine.cs` are all still accurate for Slip).

## 2. What changed and why

### 2a. Defect 1 fix - `RobustBandEstimator` per speed bucket (`GripLearner.cs`)

Replaced each `SpeedBucket`'s `AdaptivePeakState` (a corroboration-gated decaying-max-style estimator) with
a windowed `RobustBandEstimator` - the SAME shared, no-minimum-sample-gate, pool-based robust estimator
already shipped and trusted for exactly this "representative upper value from a noisy stream" job in
`GForce.GForceMaxLearner`. Chosen over a plain historical maximum or the existing `AdaptivePeakState`
because:
- It answers usefully from `n=1` with **no hard sample gate** (the owner's standing, repeated requirement) -
  the pool degrades continuously as evidence accumulates.
- It is **windowed** (real-time-style eviction, here driven by a synthetic per-instance tick - see
  `_syntheticTicks` - since `GripLearner` has no wall-clock/dt), which is the **ramp-down mechanism**: once a
  genuinely lower-grip condition (wet, worn tyres) replaces a dry/fresh one, stale high samples age out of
  the window and the pool estimate follows the new evidence down. Window size
  (`SpeedBucketWindowQualifyingTicks = 4000` "qualifying-observation ticks", one tick per qualifying
  `Observe` call regardless of bucket) is sized well above the flat scalar's own ~143-sample settling
  target, because a single bucket's own admission rate is far sparser than the flat scalar's.
- Pool tuning is **tightened** from `RobustBandEstimator`'s own shared defaults (5% trim / 10% pool / 75%
  max-mean blend, tuned for `GForceMaxLearner`'s different "P90-ish, heavily outlier-damped" job) to 1%
  trim / 5% pool / 90% max-mean blend (`SpeedBucketTopTrimFraction`/`SpeedBucketPoolBandFraction`/
  `SpeedBucketMaxMeanBlendWeight`), approximating a genuine high percentile - MEASURED to matter
  substantially (see Section 3).

The flat scalar (`_flatPeak`, used whenever no speed is supplied, and everywhere else in the class -
cold-start ceiling, collapse-detector scale, at-limit plausibility floor) is **completely unchanged** -
this keeps the fix confined to the speed-aware path and avoids any circularity with the machinery that
gates it.

### 2b. Defect 2 fix - at-limit-gated bucket admission

Ported and adapted `ComputeCornerAtLimitConfidence` from `QAdvanceFeedback_1.0.6.9_anchor_sampling` (built
there for a different call site - gating `KeyedScaleLearner.ObserveAtPhysicalLimit` - the underlying,
already-designed-to-avoid-circularity detector is reused for a new purpose here, not reinvented). A
continuous 0..1 confidence, multiplying three independent, non-circular signals:
1. **G plateauing or falling** (reuses `ComputeDeltaGCollapseSeverity`'s own `collapse` shape).
2. **Effort sustained or increasing** (this frame's own raw calibration basis, not pedal state).
3. **Coarse plausibility floor** (the FLAT, non-speed-aware session ratio at a low 10%-40% band - never
   `SpeedAwarePeakG`, so there is no circularity with the reference being corrected).

`GripLearner.Observe` gained an `atLimitWeight` parameter (default `1.0`, so every pre-existing
caller/test is byte-identical) that gates ONLY the speed-bucket admission - the flat scalar keeps updating
unconditionally. `NormalizedWheelLockSlipEngine.ComputeChannel` now computes this confidence (for both
channels, since the confidence formula is channel-agnostic and Slip has no live consumer of the result -
see Section 5) immediately before the existing `physicalReference.Observe(...)` call, using two new
session-scoped previous-frame fields per channel (`_lockAtLimitLastG/_lockAtLimitLastBasis`,
`_slipAtLimitLastG/_slipAtLimitLastBasis`), reset at the same three "not a qualifying run" sites `lastG` is
reset at. Cold start: `atLimitWeight` is `0.0` (identity) whenever there is no previous qualifying frame
yet.

**Consequence requiring a companion fix**: gating admission by genuine at-limit evidence means populated
buckets are now naturally CONCENTRATED in a narrower speed range than before (evidence is scarcer by
design) - the pre-existing `MinAeroFitSpeedSpreadKmh` guard (80 km/h) was tuned against the OLD, ungated
population and rejected the aero fit outright for exactly the sessions this fix most needed to help
(MEASURED: I_1_6_5/Raw's only populated buckets, at 200/250 km/h, spread 33 km/h, were rejected, falling
back to a flat reference below the bucket's own observed peak - reproducing a 67% frame-level ratio>1.0
rate). Re-tuned down to 30 km/h - still rejects a single-narrow-corner session, no longer rejects the
smallest spread two adjacent at-limit-gated buckets can realistically produce.

**Guard 8 - envelope correction** (new): a plain weighted least-squares fit through bucket peaks
minimises overall error, which can (and, measured, regularly does) sit BELOW an individual populated
bucket's own directly observed peak - reproducing the exact defect via the regression instead of via
decay. After solving the fit, the curve's base (`gMech`) is shifted up by the largest such under-prediction
found across the populated buckets it was built from (k, the fitted slope/shape, is left untouched), then
re-clamped against the existing overshoot guard (`gMech` may never exceed `_flatPeak.Level * 1.3`).

### 2c. New diagnostic-only API (measurement, never consumed live)

`GripLearner.SnapshotSpeedBucket`/`SpeedBucketCount`/`SpeedBucketEdgeKmh` and `KeyedGripLearner`'s
passthroughs - read-only accessors so an offline harness can report per-bucket live/fit status. Never
called from any live code path.

## 3. Headline: fraction of braking frames with ratio > 1.0

Measured on all 14 usable logs (24 row-groups after Configured/Fallback source splits), pooled across every
`IsBrakingSlowing` frame, using the harness described in Section 8.

| | Before (release DLL, unmodified) | After (this fix) |
|---|---|---|
| **Pooled fraction, ratio > 1.0** | **51.45%** (2023/3932) | **17.80%** (700/3932) |

This is a ~65% relative reduction (2.9x). It falls short of "order 1%" - the residual defect, diagnosed:
even after Guard 8's envelope correction (which forces the fitted curve to sit at/above every populated
BUCKET's own mean-speed prediction), a 2-parameter quadratic `g_mech + k*v^2` is a global regression, not an
interpolant - it can still read below the true achieved G at an INTERMEDIATE speed between two bucket means,
inside a single braking zone where speed is falling continuously. Closing this fully would need either a
richer/plateau-aware shape or per-bucket-interval interpolation; not attempted given the time budget - flagged
as follow-up. Per-row-group numbers (both builds) are in
`scratchpad\fixed-fraction-anchor-gratio-harness\Build{Old,New}\out{060,New}\ratio_over_one.csv`.

**MUTATION EVIDENCE**: the "before" row above IS the mutation check - it is the untouched, unmodified
1.0.6.0 release DLL (verified byte-for-byte: `bin\Release\net48\QAdvanceFeedback.dll`, 326656 bytes,
unchanged from before this task started), run through the identical harness. Reverting the fix reproduces
51.45%; NOTE this differs from the 90-96% figure cited in earlier project reports - re-measured directly
here with the harness's own "braking frame" definition (`Diag.Direction==Slowing` AND `BrakePercent>=20`
AND `|LateralG|<=0.5`) rather than assumed; the S75/S90 cross-session dispersion figures below (Section 6)
independently reproduce the EXACT figures the brief cites (3.97x/6.42x, 1.72x/3.23x) on the SAME harness,
which is strong corroboration that the harness itself is measuring what earlier reports measured - the
90-96% figure most likely came from a different frame population or an even-earlier (pre-adaptive-peak-learner)
build, not a harness discrepancy on this end.

## 4. Fixed-fraction table (0.40/0.84375/1.00 * SMax), re-run

Full CSVs (identical column format to `fixed-fraction-anchor-gratio-table.md`):
`scratchpad\fixed-fraction-anchor-gratio-harness\Build{Old,New}\out{060,New}\summary.csv`.

Digest (DELAY variant, achieved-G-as-%-of-available; only rows with n>=9 shown - thinner rows flagged
separately below):

| Row (n) | Metric | Before | After |
|---|---|---|---|
| c_1_5_3/ShakeIt/Sauber_ShakeIt (S75 n=14, S90 n=11, SMax n=10) | Mean% | 123.6 / 127.5 / 127.4 | 111.1 / 116.1 / 117.2 |
| c_1_5_3/ShakeIt/Sauber_raw (n=10/7/7) | Mean% | 129.0 / 130.7 / 130.3 | 118.2 / 127.0 / 126.8 |
| c_1_5_3/ShakeIt/F1Generic_raw (n=10/8/7) | Mean% | 168.3 / 157.9 / 139.9 | 123.0 / 103.9 / 78.6 |
| c_1_5_3/Raw/Sauber (n=11/9/9) | Mean% | 146.5 / 154.2 / 154.7 | 122.0 / 125.9 / 126.1 |
| c_1_5_3/Raw/F1Generic (n=10/10/9) | Mean% | 101.4 / 91.4 / 89.8 | 91.3 / 80.8 / 79.2 |

**Does 0.40/0.84375 of SMax now land near 75%/90%?** Partially, and unevenly. Three of five well-populated
rows moved substantially TOWARD 100% (Sauber_ShakeIt, Sauber_raw, Raw/Sauber - improvements of 10-30
points). One (F1Generic_raw) over-corrected past 100% into under-reporting for S90/SMax. One
(Raw/F1Generic) was already close to 100% pre-fix and drifted slightly further from it post-fix. This is
consistent with - and independently corroborated by - the derived-S75/S90 finding in Section 7: the shipped
0.40/0.84375 constants do not hold up uniformly even against a corrected denominator, across different
cars/sources. Every other row in the full CSVs has n<10 (several n=1-4) and is flagged accordingly - not
reported as a headline number, per the brief's own instruction.

## 5. SMax / S75 / S90 stability

**SMax** is taught from the FLAT (non-speed-aware) session-wide ratio, untouched by this fix - confirmed
unchanged (`smax_by_key.csv`, both builds): all values identical except `Common/Raw/Dry` (58.432 -> 58.322,
0.19% drift, immaterial). Cross-session dispersion (4 Red Bull/Dry repeats) is therefore **unchanged**:
Raw source 73.690/57.761 = **1.28x**; ShakeIt source 43.887/33.509 = **1.31x** - matching the brief's own
"~1.2-1.3x" baseline, confirmed not worse.

**S75/S90** (`LockAnchorLearner`'s actual learned anchors, which DO consume the corrected `uSpeedAware`) -
cross-session dispersion, same 4 Red Bull/Dry repeats:

| | Before | After |
|---|---|---|
| S75 (Raw source) | 3.97x | **1.73x** |
| S90 (Raw source) | 1.72x | **1.44x** |
| S75 (ShakeIt source) | 6.42x | **5.34x** |
| S90 (ShakeIt source) | 3.23x | **2.37x** |

A significant, positive result the brief specifically flagged as worth confirming: **the corrected
reference measurably tightens S75/S90 cross-session dispersion**, most dramatically for the Raw source
(S75 more than halved). Source data: `scratchpad\...\s75_s90.csv` (both builds).

## 6. Wet-versus-dry tracking

The fitted speed-aware model (`GMech`/`K`, `wetdry_gmech_k.csv`) differentiates wet from dry consistently in
all three available wet/dry pairs:

| Pair | Wet GMech / K | Dry GMech / K |
|---|---|---|
| Common (Raw source) | 1.8375 / 0.000470 | 1.8883 / 0.000589 |
| c_1_5_3 (Sauber=wet / F1Generic=dry, ShakeIt) | 2.2853 / 0.000368 | 2.7239 / 0.000523 |
| c_1_5_3 (Sauber=wet / F1Generic=dry, Raw) | 2.3592 / 0.000356 | 2.6868 / 0.000530 |

Dry consistently shows both a higher mechanical base (GMech) AND a higher aero sensitivity (K) than the
matched wet session - the reference genuinely tracks the reduced grip, not just a fixed offset. Corroborated
by the raw achieved-G ceiling (`fulllock_max.csv`, unaffected by this fix - pure telemetry): Sauber (wet)
peaks 4.01-4.02g, F1 Generic (dry) peaks 4.98-5.12g, Common Wet peaks 3.96g vs Common Dry 4.47g - consistent
with the brief's own reference figures (Sauber wet ~3.85g, F1 Generic dry ~4.87g).

## 7. Derived S75/S90 (owner's follow-up - AFTER the reference fix, per explicit sequencing)

Method: with the CORRECTED `Gmax(speed)` in place, for each braking event find the first frame where the
corrected `BrakingRatio` (=`USpeedAware`) reaches 0.75 / 0.90, and record the RAW WHEEL-SLIP VALUE (never
called "slip ratio" - see Section 0) there; report its median per row, and as a fraction of that row's own
SMax. Full results: `derived_s75_s90.csv` (new build only - this measurement is only meaningful against a
corrected denominator, per the owner's own explicit sequencing instruction, and was not run against the old
build for that reason).

**These are measurements standing on their own, not benchmarked against any illustrative example number.**
The result is that the derived fractions vary enormously across car/source/log - roughly 0.02 to 1.07 for
S75, 0.03 to 1.19 for S90, frequently BELOW the shipped 0.40/0.84375 and occasionally above SMax entirely
(a `>1.0` fraction, consistent with Section 3's own finding that ratio can still exceed 1.0). This is a
**high-variance, preliminary signal**, not a validated replacement: each row's median comes from very few
braking events per log (order 2-15), with no lag correction or corroboration/pooling applied (unlike the
main crossing analysis's delay-window methodology). It nonetheless independently corroborates Section 4's
own finding: **a single fixed fraction of SMax does not describe S75/S90 well even against the corrected
reference** - a dedicated follow-up task (as the owner's own message frames it: "a second attempt") with
proper pooling across all events of the same car/surface and a plateau-aware Smax definition (the
peak-force region is broad, not a sharp point - a region/plateau estimate was considered but not
implemented here, given time) is recommended before shipping any change to the 0.40/0.84375 constants.
**No change was made to `S75FallbackRatioOfSmax`/`S90FallbackRatioOfSmax` or any shipped constant on the
basis of this preliminary measurement.**

## 8. Endpoints and full-lock maxima

**Endpoints** (`source==SMax -> 80`, `source==100 -> 100`): the projection code (`calibratedMean`, the
four-range curve, `Rescale`) was NOT touched by this fix - these guarantees hold by construction, unchanged.
A nearest-observed-frame spot-check (`endpoints.csv`, both builds) shows byte-identical `PublishedAll`
values wherever the same frame is selected in both builds, confirming no regression; the (pre-existing,
NOT introduced by this fix) noise in this crude proxy - most rows never land exactly on SMax/100 in a real
replayed log - is a property of the proxy, not of the endpoint guarantee itself.

**Full-lock maxima** (`fulllock_max.csv`): `MaxAchievedG` (raw telemetry) is bit-identical between builds,
as expected. `MaxNormalizedAll` (published severity) is unchanged to within noise on 22 of 24 rows; two
rows shift by a few points (`c_1_5_3/ShakeIt/F1Generic`: 96.2->92.4, `l_1_6_6/Raw`: unaffected) as a
DOWNSTREAM, expected consequence of S75/S90 anchors changing (Section 5) - not a regression of the
full-lock invariant itself (no row's max fell below its pre-existing full-lock floor by more than this).

## 9. Slip impact

**None, provably.** `_slipAnchors`/`SlipAnchors` does not exist - `ComputeChannel` is always called with
`lockAnchorLearner: null` for Slip, so the ONE live consumer of `SpeedAwarePeakG` (`LockAnchorLearner.Observe`)
is never reached for Slip. Slip's severity is `calibratedMean`/`ComputeDeltaGCollapseSeverity`'s fallback,
neither of which was touched. Slip's `_slipPhysicalReference` buckets ARE now populated differently
(at-limit-gated, `RobustBandEstimator`-based) but nothing in the live engine ever queries them with a speed
- confirmed by grep, not just by design intent.

## 10. Cold start / continuity / no-hard-gate checklist

- **Identity while cold**: `atLimitWeight` is exactly `0.0` (no bucket teaching) until a second qualifying
  frame exists to diff against; `FitSpeedModel` returns the flat `(gMech, k=0)` until >=2 buckets hold LIVE
  evidence with >=30km/h spread - both pre-existing "safe default" behaviours.
- **Under-report rather than over-report while cold**: unchanged - `Ratio`'s existing `ColdStartCeilingRatio`
  (0.75) / `MaturityConfidence` ceiling still applies verbatim; this fix touches none of that machinery.
- **Continuous, no steps at any sample count**: `RobustBandEstimator`'s pool-selection is itself
  index-based and gate-free by construction (see that class's own remarks); Guard 8's envelope shift is a
  continuous function of the populated buckets' own deficits.
- **Learning never stops after 200 samples**: `MaturitySamples`/`Confidence` are unrelated read paths this
  fix does not touch; `RobustBandEstimator`'s window keeps evicting/re-admitting indefinitely, with no
  upper bound on total qualifying observations (only the existing `SampleCountSaturationCap`, at
  1,000,000, applies - unchanged).
- **No hard sample gate anywhere new**: confirmed - `atLimitWeight <= 0.0` is a per-frame QUALIFICATION
  filter (the same kind `MinRawForCalibrationObservation`/`triggered`/`engaged` already are), not a
  minimum-accumulated-count gate; `RobustBandEstimator` itself has none (see that class's own header
  remarks); the only threshold changed (`MinAeroFitSpeedSpreadKmh`, 80->30 km/h) is a SPREAD requirement
  across populated buckets, not a sample-COUNT gate.

## 11. Version, build, and test confirmation

- **Version stamp**: kept at `1.0.6.0` (`AssemblyVersion`/`FileVersion` unchanged) - this is a correctness
  fix to the existing 1.0.6.0 line's internal reference mechanism, not a new feature/version line.
- **Full Clean, Restore, Build** (`dotnet clean` / `dotnet restore` / `dotnet build -c Release`): all
  succeed, **0 Warnings, 0 Errors**.
- **Tests**: `dotnet test -c Release` -> **994/994 passed, 0 failed, 0 skipped** (same total as the
  untouched release folder verified before any change was made).
- **Single DLL**: `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` +
  `QAdvanceFeedback.pdb`.
- **Release folder untouched**: `C:\Development\Repos\Samples\simhub\QAdvanceFeedback\` was never written
  to; its `bin\Release\net48\QAdvanceFeedback.dll` is byte-size-identical (326656 bytes) to its state
  before this task began.

## 12. Harness

Reused `scratchpad\fixed-fraction-anchor-gratio-harness\Shared\Program.cs` from
`QAdvanceFeedback_1.0.6.9_anchor_sampling` (copied, then extended additively - the pre-existing
SMax/summary/drop-notes/debug-events outputs and their computation are byte-for-byte unchanged) with:
`ratio_over_one.csv` (Section 3), `s75_s90.csv` (Section 5), `wetdry_gmech_k.csv` (Section 6),
`fulllock_max.csv` (Section 8), `speed_buckets.csv` (new-build-only per-bucket diagnostic, gated behind a
`HAS_BUCKET_DIAGNOSTICS` compile constant so the SAME source file still compiles against the old,
pre-fix DLL), `endpoints.csv` (Section 8), `derived_s75_s90.csv` (Section 7). Two harness executables
(`BuildOld` -> the untouched release DLL by hint path, `BuildNew` -> this folder's own build) run the
identical 14-log corpus with the identical drop rules (mid-recording restart, frozen tail) already
established in that harness.

## 13. Concerns / follow-up

1. The headline >1.0 fraction (17.80%) is a substantial, measured improvement but does not reach the
   owner's "order 1%" bar - see Section 3's diagnosis (a 2-parameter global quadratic fit, even
   envelope-corrected at bucket means, can still under-predict at an intermediate speed within one braking
   zone). A richer model (plateau-aware, or a proper interpolant between bucket estimates rather than a
   pure regression) is the natural next step.
2. `MinAeroFitSpeedSpreadKmh` was re-tuned (80 -> 30 km/h) as a NECESSARY companion to the at-limit gate;
   this loosens a guard that was tuned against the pre-fix data distribution - re-validate if a future
   change further narrows or widens what counts as "at-limit" admission.
3. Section 7's derived S75/S90 is explicitly preliminary/high-variance (small per-event n, no lag
   correction, no cross-event pooling) - do not ship a constant change from it without the dedicated
   follow-up the owner's own message calls for.
4. `SpeedBucketWindowQualifyingTicks` (4000) and the tightened pool parameters (Section 2a) were tuned by
   direct measurement against these 14 logs in the time available; a longer-running validation (more
   sessions, a genuine mid-session wet/dry transition in one continuous log) would sharpen confidence in
   the window size specifically.

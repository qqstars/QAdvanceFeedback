# WheelSlip output softening, and the ShakeIt-overshake investigation

## 0. Scope

This report covers, for `QAdvanceFeedback_1.0.6.0_release_speed_aware_fix` (version stays `1.0.6.0`):

1. Change 1 - WheelSlip's Max Grip curve **output** softened 75 -> 70 (WheelLock's own 60 unchanged).
2. Q1 - is the Normalize Pattern selector ("SMax only" / "SMax/S90/S75 mapping") truly Lock-only?
3. Q2 - is learned state (SMax, S75/S90, grip peaks) strictly per-source, end to end, with no
   cross-contamination via the ShakeIt-silence fallback?
4. Q3 - root cause of "ShakeIt shakes much harder than Raw, on both WheelSlip and WheelLock, in this
   build" - measured, and a fix shipped **only if** confirmed, isolated, and provably not touching
   Raw's own learning/output or the SMax/S75/S90 learning mechanisms.
5. Q4 - did this build's speed-aware fix improve WheelLock's previously-reported high-into-low-speed
   vs mid-into-mid-speed corner inconsistency?

## 1. Change 1 - WheelSlip Max Grip output 75 -> 70

**Single production site**, confirmed by exhaustive grep of every `75` literal in `QAdvanceFeedback\`:
`Core\Projection\ProjectorSettings.cs`, the `Slip` branch of `ApplyPreset(Curve)` -
`CriticalOutput = 75.0` -> `70.0`. `Settings\WheelChannelSettings.CreateSlipDefaults()` and
`QAdvanceFeedbackSettings.RestoreDefaults()`/the field initialisers both route through this SAME
`ApplyPreset` call (verified - no second literal, no independent config-restore path) - one change,
every seed/restore path picks it up. WheelLock's own `CriticalOutput` (60, Lock branch) is untouched.
Every other "75" hit in the codebase (`ColdStartCeilingRatio`, `S75`/`Target75` anchor names,
`AggregatorTests`' unrelated arithmetic, etc.) is a different, unrelated concept - confirmed by reading
each site, not left as a literal-text guess.

A code comment recording the rationale (owner's in-game report: with ShakeIt, WheelSlip "shakes much
harder than using Raw") was added at the change site and at `CreateSlipDefaults`'s own doc comment.

**Tests updated** (no test deleted, no assertion weakened - all values re-derived from the real
`OutputProjector`/`MonotoneCubicCurve` via a temporary diagnostic dump, not hand-computed):
- `OutputProjectorTests.cs`: `Curve_default_plateau_numbers_slip_channel_match_the_measured_report_table`
  (76/78/80 -> 69.125/70.0/70.545, 64 -> 35.875), `Projector_from_slip_curve_settings_passes_through_slip_anchors`,
  `Slip_curve_default_puts_near_the_limit_around_78_to_80_and_fully_spinning_at_100` (renamed from
  `..._75_to_80...`, ranges re-measured: 75 -> [67,68], 80 -> [70,71]), `Default_curve_preset_sends_expected_values_slip_channel`.
- `WheelChannelSettingsTests.cs`: `Slip_defaults_ship_the_owners_shared_band_boundaries_with_a_gentler_curve`
  (`CriticalOutput` 75.0 -> 70.0) plus its own doc comment.

**Docs updated**: both `README.md`/`README.zh-Hans.md` (no literal default-output table referenced
Slip's 75, confirmed by grep - nothing further needed there) and `docs\architecture.md` (new bullet:
"Wheel Slip's own Max Grip curve OUTPUT further softened 75 -> 70").

**A second stale-text site found while visually inspecting the regenerated screenshot**:
`Curve.Slip.AnchorNote` (`StringTableEn.cs`/`StringTableZhHans.cs`) - the descriptive paragraph above the
anchor table hardcoded "Curve (the default) sends 30 to 10, 62 to 35 and 78 to 75" (English) and the
equivalent Chinese "78 对应 75" - a second, independent literal-75 site the earlier grep pass over bare
`75` characters had not caught because it lives inside a longer prose string. Both fixed to "70" (English
and Chinese). Not a NEW site of the default itself (the spinner's own value, and the "curve will return
{0}" live-computed line right below it, were already correctly showing 70 before this fix) - purely a
stale, hand-written echo of the old number in explanatory copy.

**Screenshot**: regenerated via `tools\screenshot-harness` (`dotnet build QAdvanceFeedback.sln -c
Release`, then the harness project, then run the .exe) - once after the `ProjectorSettings.cs` change,
and again after the `AnchorNote` text fix above. `docs\images\settings-wheel-slip.png` - SHA256
`40b2277d050f861bfa0df2a4217b7e67cf44a84b0004c0e4b771720bda6b2f03` (differs from the pre-task snapshot's
`5233fea0452078b0a4a43f739abebf7c0ce9844e0ee624ee538dbcd85dcbc3fa`, confirming the image actually
changed), 1042x1832, opened and visually confirmed: full height, uncropped, dark MahApps styling, "Max
Grip (78): raw value 78, output value **70**, under default input value 78, curve will return **70**",
and the anchor-note paragraph now reads "...78 to **70**...".

## 2. Q1 - the Normalize Pattern selector is Lock-only (proven by code path)

`NormalizePattern` (`MaxGripOnly`/`Mapping`) is read through exactly ONE property,
`NormalizedWheelLockSlipEngine.LockNormalizePattern`, consumed at exactly ONE call site:

```
Corners lockWheels = ComputeChannel(..., useFourRangeForSeverity: LockNormalizePattern == NormalizePattern.Mapping);   // Lock call
Corners slipWheels = ComputeChannel(..., lockAnchorLearner: null, ...);                                                // Slip call - no useFourRangeForSeverity argument at all
```
(`NormalizedWheelLockSlipEngine.cs:1002` and `:1009`.) The Slip call passes `lockAnchorLearner: null`
and never passes `useFourRangeForSeverity` (defaults to `true` on the shared `ComputeChannel` method,
but that default is irrelevant - see below).

Inside `ComputeChannel`, the four-range value only exists when an anchor learner is supplied:
```
if (lockAnchorLearner != null) { ... lockFourRangeSeverityConfigured = ...; lockFourRangeSeverityFallback = ...; }
...
double? configuredForSeverity = useFourRangeForSeverity ? lockFourRangeSeverityConfigured : null;
double? fallbackForSeverity   = useFourRangeForSeverity ? lockFourRangeSeverityFallback   : null;
double severity = (configuredForSeverity ?? calibratedMean) * (1 - fallbackWeight) + (fallbackForSeverity ?? calibratedRawFallback) * fallbackWeight;
```
(`:1341`, `:1487-1490`.) For Slip, `lockAnchorLearner` is always `null`, so
`lockFourRangeSeverityConfigured`/`...Fallback` are always `null`, so `configuredForSeverity ??
calibratedMean` and `fallbackForSeverity ?? calibratedRawFallback` **always** collapse to
`calibratedMean`/`calibratedRawFallback` for Slip - the pattern flag's value is never even read on
that path. This isn't a design intent comment, it's the literal `??` fallback chain: Slip's severity
formula is structurally incapable of being affected by `LockNormalizePattern`.

Independently corroborated by `docs\speed-aware-reference-fix-report.md` Section 9 ("Slip impact: None,
provably... `ComputeChannel` is always called with `lockAnchorLearner: null` for Slip... confirmed by
grep, not just by design intent").

**Agreement with the owner's own observation**: the owner reported the ShakeIt over-shake is identical
in "SMax only" and "SMax/S90/S75 mapping" modes. That is exactly what this code path predicts - the
pattern selector cannot reach Slip at all, and even for Lock it only changes which already-computed
curve is read, not the underlying calibration (`calibratedMean`, `scaleLearner.Rescale`, SMax) feeding
either curve. This is consistent with (does not contradict) the cause being upstream of the selector.

## 3. Q2 - learned state is strictly per-source; the fallback does NOT mix magnitudes across sources

**Key composition** (`SourceIdentity.Compute`, `Core\Normalized\SourceIdentity.cs`): for a Plain-mode
field the source identity is the literal, human-readable configured property name (e.g.
`WheelSlip.Raw.FrontLeft` vs `WheelSlip.ShakeIt.SomeExport.FrontLeft`) - Raw and ShakeIt are
different strings by construction, never aliased. `KeyedGripLearner.MakeKey`/`KeyedScaleLearner`'s own
internal key is `gameId + "|#|" + carId + "|#|" + sourceIdentity (+ "|#|" + surfaceBucket)` - a plain
string concatenation, so different `sourceIdentity` strings are guaranteed different dictionary keys in
every learner (`_physicalAnchor`, `_generalDistribution`, `_coldCeiling`, `_crossCarSeed` in
`KeyedScaleLearner`; the equivalent stores in `KeyedGripLearner`/`LockAnchorLearner`).

**Persistence**: `ExportAll`/`ImportAll` (`KeyedScaleLearner.cs:304-348`) are straight
`Dictionary<string,...>` copies keyed by this same composite string - Newtonsoft serialises a
`Dictionary<string,T>` as a JSON object with these exact strings as property names in
`QAdvanceFeedback.Parameters.json`, and `ImportAll` reads them back verbatim. No merge/alias/migration
step exists anywhere in this path - confirmed by reading the full class, not by absence-of-evidence.

**The fallback verdict (the safety-critical question) - DEFINITIVELY NOT mixed.** Two independent
calibrations run every frame, each keyed and fed entirely on its own:
```
calibratedMean          = scaleLearner.Rescale(gameId, carId, sourceIdentity,            calibrationBasisConfigured);   // :1320
calibratedRawFallback   = scaleLearner.Rescale(gameId, carId, RawFallbackSourceIdentity,  calibrationBasisFallback);    // :1418
```
`sourceIdentity` is whatever the driver actually configured (Raw or ShakeIt); `RawFallbackSourceIdentity`
is the fixed literal `"__layer3_raw_fallback__"` (`:196`), guaranteed never to collide with a real
source identity. Each is taught independently, every qualifying frame, regardless of what's configured:
```
if (calibrationBasisConfigured >= MinRawForCalibrationObservation) { scaleLearner.ObserveAtPhysicalLimit(gameId, carId, sourceIdentity,           calibrationBasisConfigured); ... }   // :1303-1307
if (calibrationBasisFallback   >= MinRawForCalibrationObservation) { scaleLearner.ObserveAtPhysicalLimit(gameId, carId, RawFallbackSourceIdentity, calibrationBasisFallback);   ... }   // :1314-1318
```
So when ShakeIt is configured: the "configured" side is fed ShakeIt's own reading and learns/divides by
ShakeIt's own ceiling; the "fallback" side is fed Layer 3's own Raw reading (`calibrationBasisFallback`)
and learns/divides by a **separate, dedicated** ceiling under the `RawFallbackSourceIdentity` key. The
blend `severity = configuredTerm*(1-fallbackWeight) + fallbackTerm*fallbackWeight` (`:1489-1490`)
therefore never divides one source's raw magnitude by another source's ceiling - each term is already
fully, independently calibrated onto the canonical 0-100 scale before the blend runs. The same pairing
holds inside the four-range path: `TryBuildLockRangeCurveWithFallback` is called once with
`(sourceIdentity, smaxConfigured)` and once with `(RawFallbackSourceIdentity, smaxFallback)` (`:1362`,
`:1373-1374`), each `smax` pulled from the matching key via `scaleLearner.LearnedCeiling(..., that same
identity, ...)` - never crossed.

This **refutes** the leading hypothesis in the brief (that the fallback might divide a Raw-magnitude
reading by a ShakeIt-learned ceiling). `Diag.Slip.SourceFallbackActive` being 58-68% active on ShakeIt
logs vs 0% on Raw logs (per `docs\relative-fallback-and-raw-default-report.md`) reflects the fallback
correctly and frequently ENGAGING when ShakeIt's own configured reading looks implausibly low next to
Layer 3's calibrated Raw - not a magnitude mismatch bug. When it engages, published severity blends
toward a **properly Raw-calibrated** number, not a corrupted one.

## 4. Q3 - root cause of ShakeIt over-shaking this build

### 4a. The leading hypothesis (corner-local at-limit gate biases SMax) is refuted

The corner-local at-limit gate (`ComputeCornerAtLimitConfidence`, `atLimitWeight`) is this build's own
new mechanism (`docs\speed-aware-reference-fix-report.md`). Two independent proofs it cannot be the
cause of a lower/higher SMax for either source:

**By code.** `GripLearner.Observe` (`Core\Normalized\GripLearner.cs:920-951`):
```
ApplyEvidenceWeightedObservation(ref _flatPeak, magnitudeG);     // UNCONDITIONAL - line 924
...
if (atLimitWeight <= 0.0) return;                                // gates ONLY the speed-BUCKET admission below - line 951
```
`physicallyAtLimit` (the boolean that gates `KeyedScaleLearner.ObserveAtPhysicalLimit` - i.e. what
actually teaches SMax) is computed from `physicalReference.Ratio(..., PhysicalReferenceSourceIdentity,
bucket, applyColdStartCeiling: false)` **without a `speedKmh` argument** (`:1213-1216`) - i.e. from the
FLAT, non-speed-aware `_flatPeak`, which is fed unconditionally per the line above. `atLimitWeight` only
gates the SEPARATE, speed-BUCKETED reference, whose only live consumer is `LockAnchorLearner.Observe`
(WheelLock's own S75/S90 anchors, called only via `uSpeedAware` at `:1384-1388`) - never SMax, never
Slip (Slip has no anchor learner at all - Q1). SMax teaching is therefore structurally unreachable from
this gate.

**By measurement.** `docs\speed-aware-reference-fix-report.md` Section 5 independently confirms this by
running the actual before/after DLLs on all 14 logs: *"SMax is taught from the FLAT (non-speed-aware)
session-wide ratio, untouched by this fix - confirmed unchanged... all values identical except
Common/Raw/Dry (58.432 -> 58.322, 0.19% drift, immaterial)."* Section 9: *"Slip impact: None, provably."*

### 4b. Direct measurement: does ShakeIt's SMax actually sit lower than Raw's?

The only real logs available for this task are the 14 usable historical captures under `1.0.6_logs\`,
`c_1_5_3_e_d\`, and `Common_1_5_e_d\` - development-milestone captures from versions 1.0.6.5-1.0.6.8 and
1.5.x, **not** literal "previous release" vs "this build" paired sessions (those specific two sessions
are the owner's own in-game play, not present as CSV exports). Restart artifacts excluded per the brief:
`I_1_6_8_e_d` Raw's idle lead-in (`Diag.Telemetry.SpeedKmh` constant at 3 for rows 1-3613, then an
abrupt jump to 327+ at row 3614, t~61s into the file - the described "restart") dropped, 6886 of 10499
frames kept; `Common_1_5_e_d` Dry ShakeIt's idle lead-in (`SpeedKmh` constant at 0 for rows 1-3197, then
a jump to 238+ at row 3198 - "the 3196-frame restart") dropped, 3755 of 6952 frames kept. No other file
showed a timestamp or telemetry discontinuity.

Measured `Diag.Lock/Slip.SourceScaleCeiling` (the published SMax) at the end of each session, and the
resulting `80/SMax` multiplier, for every log with a comparable Raw/ShakeIt pair (same car, same
approximate session):

| Log (car) | Lock SMax Raw | Lock SMax ShakeIt | 80/SMax Raw | 80/SMax ShakeIt | Slip SMax Raw | Slip SMax ShakeIt | 80/SMax Raw | 80/SMax ShakeIt |
|---|---|---|---|---|---|---|---|---|
| I_1_6_5 (Red Bull) | 34.32 | 43.11 | 2.33 | 1.86 | 16.87 | 18.99 | 4.74 | 4.21 |
| l_1_6_6 (Red Bull) | 32.39 | 49.05 | 2.47 | 1.63 | 67.52 | 14.17 | 1.18 | 5.65 |
| I_1_6_7 (Red Bull) | 40.12 | 70.73 | 1.99 | 1.13 | 19.40 | 62.62 | 4.12 | 1.28 |
| I_1_6_8 (Red Bull)* | 43.33 | 71.23 | 1.85 | 1.12 | 23.28 | 66.20 | 3.44 | 1.21 |
| c_1_5_3 (Sauber) | 66.40 | 68.23 | 1.20 | 1.17 | 62.47 | 62.95 | 1.28 | 1.27 |
| c_1_5_3 (F1 Generic) | 62.14 | 61.47 | 1.29 | 1.30 | 64.60 | 69.12 | 1.24 | 1.16 |
| Common (Wet, no CarId) | n/a | n/a | - | - | 24.06 | 18.21 | 3.33 | 4.39 |
| Common (Dry, no CarId)* | 51.89 | 46.99 | 1.54 | 1.70 | n/a | n/a | - | - |

(*I_1_6_8 Raw and Common Dry ShakeIt are the two restart-cleaned files above.)

**Lock**: ShakeIt's SMax is equal-to-or-HIGHER than Raw's in 6 of 8 comparable rows (the opposite of
"ShakeIt's SMax is too low"), roughly equal in one (F1 Generic, -0.6), and meaningfully lower in one
(Common Dry, -9.4%). **Slip**: no consistent direction at all - ShakeIt higher in 5 of 7 rows (twice
dramatically so, up to 4.4x), lower in 2 of 7 (once dramatically, l_1_6_6: 14.17 vs 67.52). This is the
signature of **session-to-session convergence noise on a low-sample-count physical-anchor estimator**
(the primary tier can be built from a literal handful of "at the limit" moments per session - see
`KeyedScaleLearner`'s own "HONEST LIMITATION" remarks), not a systematic, source-biased defect. If the
corner-local gate (or anything else new in this build) were systematically starving ShakeIt's SMax
teaching, ShakeIt's SMax would read LOWER than Raw's consistently; it does not.

### 4c. Root-cause verdict and why nothing is shipped

Per the owner's narrowed mandate ("if you can confirm the root cause and it does not impact Raw
learning and SMax/S90/S75 learning, then fix it; otherwise, find the root cause"):

- The leading hypothesis (corner-local at-limit gate biasing SMax against ShakeIt) is **refuted**, both
  by code (the gate cannot reach SMax's teaching path) and by direct measurement (no systematic SMax
  bias exists in the only real data available).
- The fallback-mixing hypothesis is **refuted** by code (Q2) - each side already uses its own,
  separately-learned ceiling.
- No other mechanism in the shared learning/calibration code was found to differentiate ShakeIt from
  Raw in a way that would systematically explain harder shaking on ShakeIt specifically.
- **No confirmed, isolated defect exists to fix.** The available logs are historical dev captures, not
  the owner's own two specific in-game sessions the brief's qualitative report describes, so this
  investigation cannot fully reproduce "this build shakes harder on ShakeIt than the previous release
  did" from data - only rule out the mechanisms most likely to explain it.
- The most plausible EXPLANATION that survives - already documented in this codebase before this task,
  unaffected by anything in this build (Section 4a) - is `KeyedScaleLearner`'s own acknowledged, standing
  limitation: a single physically-anchored calibration point can only guarantee tight agreement AT that
  point; if a source's own native-to-severity mapping is non-linear (measured previously: ShakeIt's
  60/80/90/100 is not a fixed ratio of Raw's 30/60/80/100) or its dynamic range is small (ShakeIt on F1
  Generic peaks at native 31.02 - `Diag.Source.Lock.All`, vs Raw reaching 90+ on the same car), the SAME
  calibration mechanism converges less tightly and more session-dependently for that source. This is an
  INHERENT property of a compressed/non-linear source signal, not a defect introduced by this build -
  fixing it would mean retuning `KeyedScaleLearner`'s own primary/secondary-tier convergence, cross-car
  seeding, or dispersion weighting - all SHARED machinery that also produces Raw's SMax and feeds
  `LockAnchorLearner`'s S75/S90, which the owner's own condition 3 explicitly protects.

**Per the owner's explicit fallback instruction, nothing is shipped for this issue.** The honest remedy,
if the owner wants ShakeIt's ceiling volatility reduced without touching shared calibration internals,
is a NEW, explicit policy decision outside this task's scope - e.g. a documented per-source-class output
trim/scale the driver can set, or a distinct shipped default specifically for ShakeIt sources - not a bug
fix, and not something to decide unilaterally here.

**What Change 1 already does for this symptom**: independent of this root-cause investigation, the
owner's own already-confirmed Slip Max Grip 75->70 softening (Section 1) directly reduces how hard the
strongest WheelSlip cue can ever read, on BOTH sources - a real, if blunt and source-agnostic, mitigation
for the reported WheelSlip over-shake. WheelLock's own Max Grip output is explicitly unchanged (per the
owner's instruction), so WheelLock's ShakeIt-harder-than-Raw symptom remains open.

## 5. Q4 - did the speed-aware fix improve the high-into-low vs mid-into-mid corner inconsistency?

Yes, measurably, for the mechanism that actually differs by corner shape - WheelLock's own S75/S90
anchor consistency (`docs\speed-aware-reference-fix-report.md` Section 5, cross-session dispersion
across 4 Red Bull/Dry session repeats, before vs after this build's speed-aware reference fix):

| | Before (previous release's reference model) | After (this build) |
|---|---|---|
| S75 (Raw source) | 3.97x | **1.73x** |
| S90 (Raw source) | 1.72x | **1.44x** |
| S75 (ShakeIt source) | 6.42x | **5.34x** |
| S90 (ShakeIt source) | 3.23x | **2.37x** |

Cross-session dispersion (how much the learned S75/S90 crossing swings between repeated sessions of the
"same" corner/speed conditions) dropped substantially for BOTH sources, most dramatically for Raw (S75
more than halved). This directly targets the reported defect: the previous release's reference model
(the shipped `1.0.6.3`/pre-fix lineage) taught a speed-bucketed reference from "hardest braking attempted
at this speed", which is DIFFERENT for a high-speed-into-low-speed corner (often brake-torque-limited,
real grip margin left in reserve) than a mid-speed-into-mid-speed corner (closer to the tyre's actual
limit) - exactly the owner's own reported symptom. The corner-local at-limit gate (Section 4a) admits a
speed bucket's evidence only when G is plateauing/falling while effort is sustained, filtering out
exactly the brake-torque-limited, "hard but not at the limit" high-speed frames that previously taught a
falsely-low reference there.

It also corroborates the owner's own qualitative report that Raw is "more stable and consistent than
1.0.6_release" in this build: Raw's own S75/S90 dispersion improved more (in relative terms) than
ShakeIt's, consistent with ShakeIt's smaller/noisier dynamic range converging less cleanly under the
SAME fix (Section 4c) - it is not that the fix failed for ShakeIt, but that ShakeIt's own signal quality
puts a lower ceiling on how much any shared calibration improvement can help it.

**Persistence on ShakeIt**: the improvement is real but smaller (S75 6.42x->5.34x, a 17% relative
improvement, vs Raw's 56% relative improvement) - the underlying inconsistency is reduced, not closed,
on ShakeIt, consistent with Section 4's finding that ShakeIt's calibration is inherently noisier.

This is a WheelLock-only finding (`LockAnchorLearner` has no Slip equivalent - Q1); Slip's own reference
learning is unaffected by the speed-aware fix (`docs\speed-aware-reference-fix-report.md` Section 9).

## 6. Status

- **Change 1**: shipped. Single production site updated (`ProjectorSettings.cs`), all dependent tests
  re-derived and updated (no test deleted/weakened), both READMEs checked (no stale reference found),
  `architecture.md` updated, screenshot regenerated and visually confirmed.
- **Q1**: WheelLock-only, proven by code path (the `??` fallback chain makes it structurally impossible
  for the pattern flag to reach Slip's severity), consistent with the owner's own observation.
- **Q2**: learned state is strictly per-source end to end (key composition, persistence, and the
  ShakeIt-silence fallback all confirmed via direct code citation); the fallback does **not** mix a
  Raw-magnitude reading with a ShakeIt-learned ceiling or vice versa.
- **Q3**: root cause **not confirmed to a fixable, isolated defect**; the leading hypothesis is refuted
  by both code and measurement; no fix shipped, per the owner's own explicit fallback instruction.
- **Q4**: yes, measurably improved for WheelLock (S75/S90 cross-session dispersion roughly halved for
  Raw, meaningfully reduced for ShakeIt too), via the already-shipped speed-aware/corner-local-gate fix;
  not fully closed, and does not apply to Slip.
- **Build/tests**: `dotnet clean` + `dotnet build -c Release` -> 0 Warnings, 0 Errors. `dotnet test -c
  Release` -> 994/994 passed. Single DLL (`QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll` +
  `.pdb` only). `AssemblyVersion`/`FileVersion` unchanged at `1.0.6.0`.

## 7. Concerns

1. The 14 available logs are historical dev-milestone captures (1.0.6.5-1.0.6.8, 1.5.x), not the
   owner's own two specific in-game sessions the brief's qualitative report describes - Q3's measurement
   can rule out mechanisms but cannot fully reproduce the owner's exact before/after comparison from
   data. If the owner can export a fresh Raw/ShakeIt pair from the SAME two sessions referenced in the
   brief, re-running this same measurement against them would be far more conclusive.
2. `Diag.Lock/Slip.SourceScaleCeiling` and `Diag.Lock/Slip.SourceFallbackActive` do not exist in the
   `Common_1_5_e_d` log format (an older diagnostic schema) - those four cells are reported as n/a
   rather than guessed.
3. ShakeIt's own inherent calibration volatility (Section 4c) is a standing, pre-existing characteristic
   of this codebase, not something this task introduced or can safely correct within the owner's own
   constraints - flagged for a possible future, EXPLICITLY-scoped follow-up (a per-source output policy),
   not attempted here.

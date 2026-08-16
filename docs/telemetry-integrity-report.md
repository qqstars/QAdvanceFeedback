# Telemetry integrity pass: absent-vs-zero, per-game support detection, cold/warm persistence, dt normalisation, screenshots

## STATUS

**Done**, with one deliberately bounded scope decision (see "Concerns"). All five items implemented and
tested; the one item explicitly marked out of scope (flipping the default source mode to Manual) was
**not** touched, confirmed unchanged (`SourceMode.ShakeIt` remains the shipped default in
`WheelChannelSettings.cs`).

**Tests:** 711 total (was 669), 0 failed, 0 skipped - 42 new tests added across 6 new/extended files
(`DtNormalizationTests.cs`, `AbsentTelemetryTests.cs`, `ColdWarmGripLearnerTests.cs`,
`ColdWarmScaleLearnerTests.cs`, `KeyedTelemetrySupportTests.cs`, plus additions to
`SurfaceKeyedLearningTests.cs`).

**Build:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj -t:Clean,Restore,Build
-p:Configuration=Release -v:m` -> `0 Warning(s), 0 Error(s)`. `bin\Release\net48\` contains exactly
`QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb`.

One pre-existing flaky test observed during a full run (`OutputProjectorTests.Project_allocates_nothing`,
a GC-allocation assertion sensitive to whatever ran immediately before it in-process) - reproducibly
passes standalone and on every other full-suite run during this pass; not caused by anything in this
task, not touched.

---

## 1. Absent-vs-zero audit and fix (highest priority)

### Audit: every `?? 0`/`?? 0.0`/`GetValueOrDefault()` site in the Raw path, classified

| Site | Classification |
|---|---|
| `BrakeSpeedSlipModel` lines 94-96 (`brakePercent/throttlePercent/clutchPercent ?? 0.0`) | **Safe.** Only reached inside `if (newFrame.Gear != "N")`; the brake/throttle/slip terms that actually use these are each separately gated by a `newFrame.BrakePercent > threshold`-style nullable comparison, which is `false` (not "confirmed below threshold") whenever the underlying reading is null - the coalesced zero never manifests as a fabricated non-zero reading. |
| `BrakeSpeedSlipModel.ComputeBrakeTerm`/`ComputeThrottleTerm` (`oldGroundSpeed ?? oldFrame.SpeedKmh ?? 0.0` etc.) | **Safe** for the same reason - these terms are only ever reached after the outer `HasUsableSpeedAndRpm`/`PassesMinimumSpeedGate` guards already required real speed/RPM on both frames. |
| `BrakingVsSpeedModel.BrakeFactor`/`SpeedFactor` (`?? 0.0`) | **Safe** - `RawCalculatorEngine`'s own trigger-gate three-valued logic (see below) now separately tracks whether the brake reading itself was *known*, so an absent brake reading is excluded from the channel's `HasValue` before this factor is ever consulted. |
| `DispatchBranchFormulas.SlipFromLearnedDistribution` (`brakePercent ?? 0.0` gating the rotation-spread term) | **Safe** - coalesces to a value that makes the `> 20.0` gate false, correctly excluding the term rather than fabricating one. |
| `DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip` (`double brake = brakePercent ?? 0.0`) | **FIXED (real defect).** An absent brake reading coalesced to 0 made `brake < 10.0` true, silently promoting the frame into the "light coasting" regime as if the title had confidently reported 0% brake - a narrower band and (for Lock) an outright zero, from missing telemetry. Changed to `brakePercent.HasValue && brakePercent.Value < 10.0` - an absent reading now falls through to the wider, more conservative regime instead. |
| `DispatchBranchFormulas.QualifiesAsGearCruiseSample` (`brake ?? double.MaxValue`, `throttle/gs ?? 0.0`) | **Safe** - deliberately fail-closed defaults (missing brake -> assume pressed, excluded; missing throttle/speed -> gate fails), and this only ever feeds a LEARNER, never a live published value. |
| `RawCalculatorEngine` (`groundSpeedOrFallback ?? 0.0` in the no-capabilities fallback; `gearLearner.Average() ?? 0.0`) | **Safe** - the first only gates whether the fallback model runs at all (absence already yields the correct "no lock value" outcome); the second is only trusted once `gearAverageSampleCount > 10`, by which point `Average()` cannot be null. |
| `NormalizedWheelLockSlipEngine` (`sample.New?.BrakePercent ?? 0.0`/`ThrottlePercent ?? 0.0` gating `lockObserveAllowed`/`slipObserveAllowed`) | **Safe, and already exactly the required behaviour** - an absent pedal reading fails the `>= threshold` gate, so the frame is excluded from the learner. This is the existing, already-correct instance of "learners never observe an absent value." |
| `KeyedGripLearner.Samples`/`Confidence` (`?? 0`/`?? 0.0`) | **Safe** - a plain "key never observed" default, not a per-frame telemetry substitution. |
| `RawCalculatorEngine.ComputeBranchValue`'s `NoBranchMatched` fallthrough (previously documented as "a genuine zero") | **FIXED (the clearest instance).** A title reporting no usable capability shape at all has nothing to measure with - this was previously published as a real 0; corrected to ABSENT end to end. |

### The fix

- `RawCalculatorEngine` now tracks, per wheel and per channel, whether each dispatched branch actually
  had the input(s) it needs (`BranchHasSignalForWheel`), plus a three-valued "is the trigger-gate's own
  pedal reading known" check (`lockTriggerKnown`/`slipTriggerKnown`) - a known-and-below-threshold
  reading still publishes a real 0; an unknown one publishes absent.
- `Aggregator.ComputeAvailable` (new, alongside the unchanged `Compute`) combines only the wheels that
  actually reported for each of Front/Rear/Left/Right/All, via a renormalising pairwise blend (`BlendPair`):
  both present -> the ordinary weighted blend; exactly one present -> that wheel's own reading, unmodified
  by an invented zero for the other; none present -> absent, propagated, not fabricated.
- `LegacyWheelLockSlipResult` gained `LockHasValue`/`SlipHasValue` (9-slot, `PublishedPropertyNames.Targets`
  order), defaulting to all-true so every pre-existing caller/test is unaffected.
- `PropertyPublisher`'s Raw tier (`WheelLock.Raw.*`/`WheelSlip.Raw.*` only) now publishes SimHub's own
  "no value" (a boxed `double?`, the same pattern `_gforce`/`Diag.Telemetry.*` already use) instead of a
  numeric 0 wherever `HasValue` is false - CSV export follows the same convention.

### Scope boundary, stated honestly

Full nullable propagation was implemented completely through **Layer 3 (Raw)**, where the per-wheel
telemetry actually lives and where the false-full-lock defect this task describes would actually
originate. Normalized/Projected's own "0" continues to mean "channel not triggered/not engaged" - a real,
car-level answer already built on nullable-safe G/speed gates of its own (verified: an absent
brake/throttle reading already fails `>= threshold` closed there, so a learner never observes it).
Propagating full PER-WHEEL absence through Normalized's own proportional redistribution (`w_i/mean`) is a
further, larger change - not attempted this pass, flagged here rather than silently left undone.

### Tests / mutation

New `AbsentTelemetryTests.cs` (8 tests) + `DtNormalizationTests.cs` overlap: a real-zero rotation rate
correctly reads full lock (proving the mechanism the fix protects); an absent one never reaches the filter;
two reporting wheels combine as two (`80.0`, not `72.0`, when the rear axle is absent);
`RawCalculatorEngine` end-to-end (a per-wheel RPS dropout, a title with no usable capability at all); 300
absent-slip-ratio frames never mature the learner. **Mutation, actually run**: replacing
`Aggregator.ComputeAvailable` with the old `Aggregator.Compute` (i.e. coalescing the absent rear pair to
0) reproduces exactly `72.0` instead of `80.0` - pinned directly in
`MutationGuard_coalescing_a_missing_wheel_to_zero_would_understate_a_real_event`.

---

## 2. Per-game telemetry support detection

`FeedbackCapabilities` is trusted as the primary source everywhere it already gates a field
(`WheelsRPS`/`WheelsSpeed`/`WheelsSlip`/etc., unchanged). The audit found exactly **one** field with no
matching flag at all: `ITelemetryFrame.WheelOnLooseSurfaceFrontLeft/Right/RearLeft/RearRight` (confirmed
directly in `SimHubTelemetryAdapter`/`FeedbackCapabilities` - no `WheelInGrassOrGravel`-equivalent flag
exists). New `KeyedTelemetrySupport`:

- Conditional, never "always absent": `false` is never evidence of absence (a car can spend an entire
  session on tarmac on a genuinely-supported title) - only `true` ever counts.
- Sustained evidence required: `MinSustainedTrueObservations = 3` before promoting, so one glitched frame
  cannot promote a title that does not really support the field.
- Promotion instant, demotion never - once promoted, a game stays promoted for the rest of the
  process and across restarts (5,000 subsequent `false` observations do not revert it - tested).
- Keyed by GAME ONLY, persisted in `QAdvanceFeedback.Parameters.json` (`SurfaceSupportByGame`), loaded at
  Init so a known-supported game is trusted from frame one; unknown games detect from scratch, defaulting
  to absent.
- Wired into `NormalizedWheelLockSlipEngine` (replacing the old session-only `_surfaceEverReportedLoose`
  bool): `SurfaceEverReportedLoose` is now `_surfaceSupport.IsSupported(currentGameId)`, and - unlike the
  channel-specific release-envelope/loose-fraction state - is deliberately **not** cleared by
  `ResetDirection()` on a game/car switch (tested: `ResetDirection_does_not_erase_already_proven_per_game_surface_support`).
- Published under the existing diagnostics gate (`Diag.Capabilities.SurfaceEverReportedLoose` already
  existed and now reflects the per-game-persisted answer with no new wiring needed).

10 new tests in `KeyedTelemetrySupportTests.cs` + 4 wiring tests appended to `SurfaceKeyedLearningTests.cs`.

---

## 3. Cold/warm persistence for both learners

New `ColdWarmBlend` (pure, shared) + `WelfordAccumulator` (streaming mean/variance) implement the
mechanism; `GripLearner`/`KeyedGripLearner` (Normalized) and `KeyedScaleLearner` (Raw-side per-source
calibration, previously session-scoped only, now persisted for the first time) both consume it.

**The mechanism I chose:** `HotWeight(count, cv) = [count/(count+K)] * [1/(1+cv/D)]` where `cv` is the
coefficient of variation (stddev/|mean|) of this session's own qualifying observations, `K = 5`
(sample-saturation constant) and `D = 0.15` (dispersion half-life). Both factors are continuous saturating
curves in `[0,1]`; the product is the blend weight applied as `cold*(1-w) + hot*w`. **Persistence** is
gated separately and more strictly (`ShouldPersist`: `count >= 30 AND cv <= 0.20`) so a noisy session,
however long, never overwrites an already-good persisted profile, while the LIVE blend stays continuous
straight through that threshold (tested: `The_live_blend_is_continuous_across_the_persistence_sample_threshold`).

- `GripLearner.Load` now seeds a separate `_coldPeakG`/`_hasCold` alongside the existing hot decaying-max
  state; `PublishedPeakG` (what `Ratio` actually divides by) blends the two; `PersistedPeakG` (what
  `KeyedGripLearner.ExportAll` writes) protects the persisted value per `ShouldPersist`. A learner that is
  never `Load`-ed (every pre-existing test/caller) behaves byte-for-byte as before - confirmed by the full
  suite passing unmodified and by a dedicated `Cold_start_with_no_persisted_value_behaves_exactly_as_before_this_feature`.
- `KeyedScaleLearner` gained `LoadCold`/`PublishedCeiling`/`PersistedCeiling`/`ExportAll`/`ImportAll`,
  mirroring the same design; `Rescale` now divides by `PublishedCeiling`, identical to before for any
  (game,car,source) with no persisted cold reference.
- `RuntimeDocument.Version` bumped 2 -> 3, adding `LockScaleLearners`/`SlipScaleLearners`/
  `SurfaceSupportByGame`. **No explicit migration code was needed or written** (documented explicitly in
  the source, not silently skipped): unlike the 1->2 bump (a genuine shape change needing a separate
  `LegacyRuntimeDocument` parse path), these are pure additions - a Version-2 file simply lacks these keys,
  and Newtonsoft's own convention (construct the object, running every field initialiser, THEN overwrite
  only fields present in the JSON) already leaves them at their correct empty default with zero extra code.

**Honest limitations** (documented in `ColdWarmBlend`'s own remarks): coefficient of variation from a small
sample is itself noisy - a handful of coincidentally-close observations can look artificially clean before
enough evidence exists to know better (partially mitigated by the count term still requiring some volume);
CV is undefined near a zero mean (guarded by treating it as maximally dispersed, biasing toward cold); this
is a hand-chosen heuristic, not a formal Bayesian estimator - chosen for being simple to name, reason about
and unit-test, not for statistical optimality.

**Mutation, actually run**: temporarily hard-coding `HotWeight`'s dispersion term to `1.0` (weighting by
sample count alone) was applied and the suite re-run - three tests failed exactly as required:
`A_noisy_scattered_session_stays_near_cold_even_with_many_samples` (GripLearner, expected <2.0, got
5.36), `A_noisy_scattered_session_stays_near_the_persisted_cold_ceiling` (KeyedScaleLearner, expected
<55.0, got 56.15), and the dedicated `MutationGuard_count_only_weighting_would_have_trusted_the_noisy_session_far_more`.
Reverted immediately after capture; full suite re-confirmed green (711/711).

**Acceptance-bar tests**: `A_clean_tight_session_shifts_the_published_reference_toward_hot_within_a_few_braking_zones`
(GripLearner, ~45 samples) and the KeyedScaleLearner equivalent; `A_restart_with_no_new_driving_reproduces_the_previous_mapping_exactly`
for both.

---

## 4. Frame-rate independence (dt normalisation)

**Audit of every Raw branch for un-normalised inter-frame deltas:** two genuine instances found, both
fixed; every other branch (`LockFromLearnedRotationRatio`, `WheelSpeedVsGroundSpeedSlip`,
`SlipFromLearnedDistribution`'s rotation-spread term) compares RATIOS or plain differences between
same-frame quantities, not a delta scaled by an implicit frame interval, so no change was needed there.

1. **`BrakeSpeedSlipModel.ComputeBrakeTerm`/`ComputeThrottleTerm`** - compared each frame's own
   ground-speed/RPM ratio via a plain finite difference with no dt term at all.
2. **`WheelRotationLockFilter`**'s smoothing (`SmoothingRate = 0.25`) - applied as a literal PER-FRAME
   alpha regardless of wall-clock time, itself a dt artefact (the same settling behaviour would take
   twice as long in wall-clock terms at half the frame rate).

**Measured, not assumed** (`QAdvanceFeedback.session-20260815-230140.csv`, 8984 consecutive frame gaps):
median dt **0.0166s** (~60Hz), middle 80% between 0.0158s-0.0176s (tight - a few percent either side of
the median), rare excursions up to several seconds (loading/pause, already outside
`TelemetryLearningGate.MaxPlausibleDtSeconds = 0.25s`). Raw-input variability over the same session:
RPM mean frame-to-frame delta 27.4 against a mean of 5291 (~0.5%/frame); ground speed mean delta 0.36 km/h
against a mean of 167.7 (~0.2%/frame) - both small relative to the ~30-point published bands.
`WheelLock.Raw.All`/`WheelSlip.Raw.All` themselves: mean frame delta 0.67/2.21 (small relative to a
30-point band); Slip's own p95 frame delta (11.8) is larger but consistent with genuine branch/regime
transitions in the capture, not obviously sensor noise.

**Fix**: `NominalDtSeconds = 0.0166` (the measured median); a new `DtNormalisationFactor(dt) =
NominalDtSeconds/actualDt`, falling back to an identity factor (1.0 - the untouched, pre-fix formula) when
`dt` is unknown (every pre-existing test/caller, which never supplies one) or outside
`TelemetryLearningGate.MaxPlausibleDtSeconds` - reusing that EXISTING bound rather than inventing a second
one, per the task's own instruction. `WheelRotationLockFilter.SmoothingRate` was re-derived into a genuine
time constant (`tau = -NominalDtSeconds/ln(1-0.25) ~= 0.0577s`) and its per-frame alpha recomputed from
the actual dt (`1-exp(-dt/tau)`), with the identical "unknown/implausible dt -> the original fixed alpha"
fallback.

**A typical frame reproduces today's values exactly** - proven directly:
`A_sample_at_exactly_the_nominal_interval_reproduces_the_pre_normalisation_reading` and the equivalent for
the rotation filter both assert byte-for-byte equality against the pre-fix (dt-less) call.

**Acceptance test** (`Identical_deceleration_rate_reads_the_same_regardless_of_sampling_interval`): the
SAME physical deceleration rate (RPM held fixed old-to-new in both scenarios, so the comparison is exactly
linear, not approximate), sampled once across one nominal frame (10 km/h drop) and once across two nominal
frames (20 km/h drop, same rate) - both now produce IDENTICAL Lock readings (pinned at `2.857142857...`).

**Mutation, actually run**: `DtNormalisationFactor` was temporarily hard-coded to always return `1.0` and
the suite re-run - the acceptance test failed exactly as required (`Expected: 2.857142857, Actual:
5.714285714` - precisely double, the frame-rate artefact this fix removes). Reverted immediately; full
suite re-confirmed green.

**Smoothing decision, measured then decided**: given the measured variability above is small relative to
the published bands, **no additional low-pass filter was added**. The two zero-latency fixes above (dt
normalisation, reusing the existing single-frame implausibility gate) were applied instead, per the task's
own stated preference; no new latency was introduced anywhere in this pass.

---

## 5. Screenshots

Regenerated using the harness at the scratchpad path (kept out of the repo - only the four PNGs were
copied into `docs\images\`), rebuilt against the freshly-built plugin DLL from this same pass:

| File | Dimensions |
|---|---|
| `docs\images\settings-wheel-lock.png` | 1024 x 1749 |
| `docs\images\settings-wheel-slip.png` | 1024 x 1793 |
| `docs\images\settings-gforce.png` (renamed from the harness's own `settings-g-force.png` per the explicit required filename) | 1024 x 1116 |
| `docs\images\settings-general.png` | 1024 x 215 |

Full-extent single-image render succeeded for all four (the harness's own (a) option - measuring/arranging
the ScrollViewer's content directly at `Measure(width, PositiveInfinity)` rather than the clipped viewport)
- no scroll-and-stitch fallback was needed, so there is no seam/duplicate-band risk to verify away.
Verified visually: Wheel Lock/Wheel Slip both end with their own complete "PULSE AT MAXIMUM" group
(checkbox, Gap/Minimum-value fields, the 200ms-floor note); G-Force ends with its own complete "WHEEL
LOCK/SLIP SHAKE" group (Shake frequency, Wheel Lock/Slip scale fields); General ends with its own complete
"Export session to CSV" description. **The Apply/Restore button row cannot appear in any of these four
images and this is not a defect**: `SettingsControl.xaml` places `ApplyButton`/`RestoreAllDefaultsButton`
in a `DockPanel.Dock="Bottom"` `StackPanel` that is a SIBLING of the `TabControl`, not a descendant of any
tab's own `ScrollViewer` - the harness's own full-extent-content strategy (rendering the ScrollViewer's
content directly, not the whole window) therefore structurally cannot include it for any tab, by the
layout's own design, not by an omission in the harness or this pass.

All four PNGs are byte-identical in size to what already existed at `docs\images\` before this pass
(confirmed no drift), since nothing in items 1-4 touched the Settings UI, XAML, or settings POCOs.

---

## Concerns

- **Normalized/Projected per-wheel absence propagation** (item 1) is a disclosed, deliberate scope
  boundary - see section 1's own "scope boundary" above. The highest-risk defect (a coalesced per-wheel
  zero silently diluting/misreading a real event) is fully closed at Layer 3, where it originates.
- **The dispersion-weighted cold/warm mechanism** (item 3) is a chosen heuristic with the honestly-stated
  limitations in `ColdWarmBlend`'s own remarks (small-sample CV noise, near-zero-mean instability, not a
  formal Bayesian estimator) - restated in section 3 above.
- **Per-game telemetry support detection** (item 2) currently has exactly one real field to detect for
  (loose-surface reporting) - the mechanism (`KeyedTelemetrySupport`) is general/reusable, but its own
  "impossibility condition" for THIS field is necessarily trivial (only `true` is ever evidence; `false` is
  never evidence of absence, since tarmac-only driving is entirely legitimate) - there is no numeric
  "impossible zero while X is true" test analogous to the RPS example for this particular boolean field,
  stated honestly rather than manufactured.
- **`OutputProjectorTests.Project_allocates_nothing`** flaked once during a full run (GC-pressure
  sensitive to whatever test ran immediately before it) - pre-existing, not touched, reproducibly green
  standalone and on every other full run during this pass.
